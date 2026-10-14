#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using net.rs64.TexTransCore;
using net.rs64.TexTransCore.UVIsland;
using net.rs64.TexTransTool.TextureAtlas;
using net.rs64.TexTransTool.TextureAtlas.FineTuning;
using net.rs64.TexTransTool.Editor;
using net.rs64.TexTransTool.TextureAtlas.IslandSizePriorityTuner;
using net.rs64.TexTransTool.Utils;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace net.rs64.TexTransTool.NDMF
{
    /// <summary>
    /// Runs after regular, explicitly configured AtlasTexture components.
    /// AutoAtlasTexture is deliberately not in the ordinary component phase list.
    /// </summary>
    internal sealed class AutoAtlasTexturePass : TTTPass<AutoAtlasTexturePass>
    {
        protected override void Execute(BuildContext context)
        {
            var configurations = AutoAtlasTextureProcessor.GetActiveConfigurations(
                context.AvatarRootObject);

            if (configurations.Length == 0) return;
            if (configurations.Length != 1)
            {
                TTTLog.Warning("AutoAtlasTexture:warn:MultipleConfigurations");
                return;
            }

            var session = TTTContext(context);
            var manualAtlases = session.PhaseAtList[TexTransPhase.Optimizing]
                .OfType<AtlasTexture>()
                .Where(atlas => TexTransBehaviorSearch.CheckIsActiveBehavior(
                    atlas, context.AvatarRootObject))
                .ToArray();
            AutoAtlasTextureProcessor.Execute(
                session.Domain,
                configurations[0],
                manualAtlases);
        }
    }

    internal static class AutoAtlasTextureProcessor
    {
        internal static AutoAtlasTexture[] GetActiveConfigurations(GameObject root)
        {
            return root.GetComponentsInChildren<AutoAtlasTexture>(true)
                .Where(component => component != null &&
                    TexTransBehaviorSearch.CheckIsActive(component.gameObject, root))
                .ToArray();
        }

        internal static Renderer[] GetAAONegotiationRenderers(
            IRendererTargeting domain, AutoAtlasTexture configuration)
        {
            // AAO negotiation runs before TTT material modification, so it must use
            // a conservative renderer set rather than the later material candidates.
            var excludedRenderers = domain
                .GetDomainsRenderers(configuration.ExcludedRenderers)
                .ToHashSet();
            return AtlasTexture.GetAtlasAllowedRenderers(
                    domain, domain.EnumerateRenderer(), includeDisabledRenderer: true)
                .Where(renderer => !excludedRenderers.Contains(renderer))
                .ToArray();
        }

        internal static void Execute(
            IDomain domain,
            AutoAtlasTexture configuration,
            IReadOnlyCollection<AtlasTexture> manualAtlases)
        {
            var processingTimer = System.Diagnostics.Stopwatch.StartNew();

            // Store only the NDMF console summary for this build.
            var completed = new List<string>();

            // Temporary diagnostics for validating the extra merge-planning cost.
            long executionDoAtlasTimeMilliseconds = 0;
            long executionRelocationTimeMilliseconds = 0;
            var executionDoAtlasCount = 0;
            long executionPreserveSetupMilliseconds = 0;
            long executionAtlasContextMilliseconds = 0;
            long executionIslandNormalizeMilliseconds = 0;
            long executionRelocationStageMilliseconds = 0;
            long executionResolutionPlanMilliseconds = 0;
            long executionMeshGenerationMilliseconds = 0;
            long executionTextureGenerationMilliseconds = 0;

            string OriginalMaterialName(Material material)
            {
                // Reuse NDMF's object identity; build-stage suffixes are not
                // stripped or guessed from the material name.
                var identity = ObjectRegistry.GetReference(material);
                if (identity?.Object is Material source && source != null) return source.name;
                // ObjectReference snapshots its display name at creation; this
                // remains available even if the original Unity object was destroyed.
                return identity?.ToString() ?? material.name;
            }

            var allRenderers = domain.EnumerateRenderer().Where(renderer => renderer != null).ToArray();

            // Selection is renderer-scoped. Domain.GetAllMaterials() is used
            // separately for safety checks because it also includes animation refs.
            var allMaterials = allRenderers.SelectMany(domain.GetMaterials)
                .UOfType<Material>().ToHashSet();
            // Includes animation material references in the NDMF domain.
            var allReferencedMaterials = domain.GetAllMaterials();
            if (allMaterials.Count == 0)
            {
                processingTimer.Stop();
                TTTLog.ReportingObject(configuration, () =>
                    TTTLog.Info(
                        "AutoAtlasTexture:info:TargetNotFound",
                        processingTimer.ElapsedMilliseconds));
                return;
            }

            // Material groups follow the textures actually transformed by TTT for UV0,
            // not merely common shaders or all texture references in the material.
            var textureUsages = new MaterialGroupingContext(
                allMaterials, UVChannel.UV0, primaryTexturePropertyOrMaximum: null);
            var propertyTextures = textureUsages.ContainsTextureDictionaries;

            // This is the final renderer eligibility check at Optimizing time,
            // after earlier TTT material modifications have already run.
            var excludedRenderers = domain
                .GetDomainsRenderers(configuration.ExcludedRenderers)
                .ToHashSet();
            var atlasAllowedRenderers = AtlasTexture.GetAtlasAllowedRenderers(
                    domain, allRenderers, includeDisabledRenderer: true)
                .Where(renderer => !excludedRenderers.Contains(renderer))
                .ToList();
            var candidateMaterials = propertyTextures
                .Where(pair => pair.Value.Count != 0)
                .Select(pair => pair.Key)
                .ToHashSet();
            var allowedRenderers = AtlasTexture.FilterTargetRenderers(
                    domain, atlasAllowedRenderers, candidateMaterials)
                .ToHashSet();

            var excludedMaterials = domain.GetDomainsMaterialsHashSet(
                configuration.ExcludedMaterials);

            // Resolve manual AtlasTexture targets through TTT's existing lookup
            // rather than rescanning hierarchy and comparing raw user selections.
            var manuallySelectedMaterials = manualAtlases
                .SelectMany(atlas => atlas.GetTargetMaterials(domain,
                    AtlasTexture.GetAtlasAllowedRenderers(domain,
                        domain.EnumerateRenderer(), atlas.AtlasSetting.IncludeDisabledRenderer)))
                .ToHashSet();

            var settings = new AtlasSetting
            {
                AutoAtlasTextureSize = true,
                UsePrimaryMaximumTexture = true,
                IncludeDisabledRenderer = true,
                AtlasTargetUVChannel = UVChannel.UV0,
                IslandPadding = Mathf.Clamp(configuration.IslandPadding, 0f, 0.05f),
            };
            var groups = MakeTextureConnectedGroups(allMaterials, propertyTextures);
            var preparedGroups = new List<AutoAtlasTextureMergePlanner.PreparedGroup>();
            foreach (var group in groups)
            {
                if (group.Any(material => excludedMaterials.Contains(material)
                    || manuallySelectedMaterials.Contains(material)))
                {
                    continue;
                }

                // Every renderer referencing a material must be transformed together:
                // replacing a shared material while leaving another mesh's UVs intact
                // would corrupt that mesh's appearance.
                var referencingRenderers = domain
                    .RendererFilterForMaterialFromDomains(group)
                    .ToArray();
                if (referencingRenderers.Length == 0
                    || referencingRenderers.Any(
                        renderer => !allowedRenderers.Contains(renderer)))
                {
                    continue;
                }

                var atlasRenderers = AtlasTexture.FilterExistUVChannel(
                    domain, referencingRenderers, UVChannel.UV0);
                if (atlasRenderers.Length != referencingRenderers.Length)
                {
                    continue;
                }

                var texturesToReplace = group
                    .SelectMany(material => propertyTextures[material].Values)
                    .Where(texture => texture != null)
                    .ToHashSet();
                if (texturesToReplace.Count == 0)
                {
                    continue;
                }

                // A source texture cannot be removed if it is also referenced by a
                // property outside this atlas operation. The complete source set
                // must be eligible; otherwise generating an additional atlas can
                // increase memory usage.
                if (!ReferencesAreExclusive(
                        allReferencedMaterials,
                        group,
                        propertyTextures,
                        texturesToReplace))
                {
                    continue;
                }

                preparedGroups.Add(new AutoAtlasTextureMergePlanner.PreparedGroup
                {
                    Id = preparedGroups.Count,
                    Materials = group,
                    Renderers = atlasRenderers,
                    SourceTextures = texturesToReplace,
                    OriginalPixels = texturesToReplace.Sum(texture =>
                        (long)texture.width * texture.height),
                });
            }

            var mergePlan = AutoAtlasTextureMergePlanner.Plan(
                domain,
                configuration,
                settings,
                propertyTextures,
                preparedGroups);

            var selectedIds = mergePlan.Selections
                .SelectMany(selection => selection.GroupIds)
                .ToHashSet();

            var executionGroups = new Queue<(
                HashSet<Material> Materials,
                Renderer[] Renderers,
                HashSet<Texture> SourceTextures,
                AutoAtlasTextureMergePlanner.MergeSelection? MergeSelection)>();

            foreach (var selection in mergePlan.Selections)
            {
                var selected = selection.GroupIds
                    .Select(id => preparedGroups[id])
                    .ToArray();
                executionGroups.Enqueue((
                    selected.SelectMany(group => group.Materials).ToHashSet(),
                    selected.SelectMany(group => group.Renderers).Distinct().ToArray(),
                    selected.SelectMany(group => group.SourceTextures).ToHashSet(),
                    selection));
            }

            foreach (var prepared in preparedGroups.Where(
                         group => !selectedIds.Contains(group.Id)))
            {
                executionGroups.Enqueue((
                    prepared.Materials,
                    prepared.Renderers,
                    prepared.SourceTextures,
                    null));
            }

            void EnqueueStandaloneFallback(
                AutoAtlasTextureMergePlanner.MergeSelection failedMerge)
            {
                foreach (var id in failedMerge.GroupIds)
                {
                    var prepared = preparedGroups[id];
                    executionGroups.Enqueue((
                        prepared.Materials,
                        prepared.Renderers,
                        prepared.SourceTextures,
                        null));
                }
            }

            var materialBlock = "AutoAtlasTexture:info:MaterialBlock".GetLocalize();
            while (executionGroups.Count != 0)
            {
                var execution = executionGroups.Dequeue();
                var group = execution.Materials;

                // Renderer/material/mesh state can change between planning and
                // execution (for example when earlier NDMF processing removes
                // EditorOnly objects or a prior AutoAtlas execution replaces
                // meshes/materials). Revalidate the cached renderer set against
                // the current domain before constructing AtlasContext.
                var atlasRenderers = execution.Renderers
                    .Where(renderer =>
                    {
                        if (renderer == null
                            || !AtlasTexture.IsAtlasAllowedRenderer(renderer))
                        {
                            return false;
                        }

                        var mesh = domain.GetMesh(renderer);
                        if (mesh == null
                            || mesh.subMeshCount <= 0
                            || !mesh.HasUV((int)UVChannel.UV0))
                        {
                            return false;
                        }

                        var materials = domain.GetMaterials(renderer);
                        for (var slot = 0; slot < materials.Length; slot += 1)
                        {
                            var material = materials[slot];
                            if (material == null || !group.Contains(material))
                                continue;

                            var subMeshIndex = Math.Clamp(
                                slot, 0, mesh.subMeshCount - 1);
                            if (mesh.GetSubMesh(subMeshIndex).indexCount != 0)
                                return true;
                        }

                        return false;
                    })
                    .ToArray();
                if (atlasRenderers.Length == 0)
                {
                    continue;
                }

                var originalPixels = execution.SourceTextures.Sum(texture =>
                    (long)texture.width * texture.height);

                var engine = domain.GetTexTransCoreEngineForUnity();

                IReadOnlyDictionary<Material, int>? mergeAtomicGroupByMaterial = null;
                if (execution.MergeSelection is { } selectedMerge)
                {
                    mergeAtomicGroupByMaterial = selectedMerge.GroupIds
                        .Select(id => preparedGroups[id])
                        .SelectMany(group => group.Materials.Select(
                            material => (material, group.Id)))
                        .ToDictionary(pair => pair.material, pair => pair.Id);
                }

                var executionMetrics = default(AtlasTexture.AtlasExecutionMetrics);
                var executionPhaseTiming =
                    default(AtlasTexture.AtlasExecutionTimingDiagnostic);
                var executionTimer = System.Diagnostics.Stopwatch.StartNew();
                var atlasResult = AtlasTexture.DoAtlasTexture(
                    domain,
                    engine,
                    group,
                    atlasRenderers,
                    new List<IIslandSizePriorityTuner?>(),
                    settings,
                    new AtlasTexture.AtlasExecutionOptions
                    {
                        RequireLossless = true,
                        ReportProgressInfo = false,
                        TimingDiagnostic = value => executionPhaseTiming = value,
                        IslandRelocatorFactory = mergeAtomicGroupByMaterial != null
                            ? atlasContext => new AutoAtlasMergeIslandRelocator(
                                atlasContext, mergeAtomicGroupByMaterial)
                            : null,
                        AcceptAtlasSize = (atlasContext, size, metrics) =>
                        {
                            executionMetrics = metrics;
                            if (execution.MergeSelection is { } mergeSelection)
                            {
                                // The planner models atomic groups as independently
                                // valid horizontal bands. Accept only if execution
                                // stays within both the predicted layout and every
                                // predicted property-resolution envelope.
                                if (metrics.OutputTexturePixelCount <= 0
                                    || metrics.LayoutSize.x
                                        > mergeSelection.PlannedLayoutSize.x
                                    || metrics.LayoutSize.y
                                        > mergeSelection.PlannedLayoutSize.y)
                                {
                                    return false;
                                }

                                foreach (var property in metrics.PropertySizes)
                                {
                                    if (!mergeSelection.PlannedPropertySizes
                                            .TryGetValue(
                                                property.Key,
                                                out var plannedSize)
                                        || property.Value.x > plannedSize.x
                                        || property.Value.y > plannedSize.y)
                                    {
                                        return false;
                                    }
                                }

                                return true;
                            }

                            // For standalone AutoAtlas, the pre-FineTuning
                            // property count is not authoritative. Default
                            // ReferenceCopy (and other FineTuning) can remove or
                            // merge outputs. Defer the benefit check until after
                            // DoTextureFinTuning(), before any mesh/material
                            // mutation.
                            return metrics.OutputTexturePixelCount > 0;
                        },
                    });
                executionTimer.Stop();
                executionDoAtlasCount += 1;
                executionDoAtlasTimeMilliseconds +=
                    executionTimer.ElapsedMilliseconds;
                executionRelocationTimeMilliseconds +=
                    executionMetrics.RelocationTimeMilliseconds;
                executionPreserveSetupMilliseconds +=
                    executionPhaseTiming.PreserveSetupMilliseconds;
                executionAtlasContextMilliseconds +=
                    executionPhaseTiming.AtlasContextConstructionMilliseconds;
                executionIslandNormalizeMilliseconds +=
                    executionPhaseTiming.IslandNormalizeMilliseconds;
                executionRelocationStageMilliseconds +=
                    executionPhaseTiming.RelocationStageMilliseconds;
                executionResolutionPlanMilliseconds +=
                    executionPhaseTiming.ResolutionPlanMilliseconds;
                executionMeshGenerationMilliseconds +=
                    executionPhaseTiming.MeshGenerationMilliseconds;
                executionTextureGenerationMilliseconds +=
                    executionPhaseTiming.TextureGenerationMilliseconds;

                if (!atlasResult.IsSuccess)
                {
                    if (execution.MergeSelection is { } failedMerge)
                    {
                        // A planned merge is only an optimization. If the exact
                        // build-time band layout does not satisfy the planner's
                        // envelope, preserve ordinary AutoAtlas behavior by
                        // retrying every atomic group independently.
                        EnqueueStandaloneFallback(failedMerge);
                    }
                    continue;
                }

                using var atlasContext = atlasResult.AtlasContext!;
                var compiledTextures = atlasResult.CompiledAtlasTextures!;
                var tuningResult = AtlasTexture.DoTextureFinTuning(
                    engine, atlasContext, settings, compiledTextures, null);

                var finalOutputPixels = tuningResult.TextureDescriptors.Keys
                    .Distinct()
                    .Sum(texture => (long)texture.Width * texture.Hight);

                if (execution.MergeSelection == null
                    && (finalOutputPixels <= 0
                        || finalOutputPixels >= originalPixels))
                {
                    foreach (var rt in compiledTextures.Values
                                 .Concat(tuningResult.TextureDescriptors.Keys)
                                 .Distinct())
                    {
                        rt.Dispose();
                    }
                    continue;
                }

                var hasExecutionTextureMemory =
                    AtlasTexture.TryCalculateFinalTextureMemory(
                        atlasContext,
                        tuningResult,
                        out var executionTextureMemoryBytes);

                if (execution.MergeSelection is { } memoryCheckedMerge
                    && (!hasExecutionTextureMemory
                        || executionTextureMemoryBytes
                            >= memoryCheckedMerge.BaselineBytes))
                {
                    // The planner only predicts texture cost. The final
                    // FineTuning descriptors are authoritative enough to reject a
                    // merge before it mutates meshes/materials. If the final cost
                    // cannot be determined, fall back conservatively as well.
                    foreach (var rt in compiledTextures.Values
                                 .Concat(tuningResult.TextureDescriptors.Keys)
                                 .Distinct())
                    {
                        rt.Dispose();
                    }

                    EnqueueStandaloneFallback(memoryCheckedMerge);
                    continue;
                }

                var resultingTextures = tuningResult.RenderTextures.ToDictionary(
                    item => item.Key,
                    item => engine.GetReferenceRenderTexture(item.Value));
                var retainedRenderTextures = tuningResult.TextureDescriptors.Keys.ToHashSet();

                var resolutionRoutes = AtlasResolutionRouteReport
                    .Collect(atlasContext, tuningResult);
                var block = string.Format(materialBlock,
                    string.Join(", ", group.Select(OriginalMaterialName)
                        .Distinct().OrderBy(name => name, StringComparer.Ordinal)),
                    executionMetrics.TopFreeFraction,
                    TTTLog.FormatLocalizedLines(
                        AtlasResolutionRouteReport.CreateLocalizedLogLines(
                            resolutionRoutes)));

                if (hasExecutionTextureMemory
                    && AtlasTexture.TryCalculateSourceTextureMemory(
                        domain,
                        execution.SourceTextures,
                        out var sourceTextureMemoryBytes))
                {
                    block += "\n" + string.Format(
                        "AtlasTexture:info:TargetTextureMemory".GetLocalize(),
                        AtlasTexture.FormatTextureMemory(sourceTextureMemoryBytes),
                        AtlasTexture.FormatTextureMemory(executionTextureMemoryBytes));
                }

                AtlasTexture.ReplaceMesh(
                    domain, atlasRenderers, atlasContext, atlasResult.AtlasedMeshes!);
                AtlasTexture.ReplaceAtlasedMaterials(
                    domain,
                    group,
                    settings,
                    (new List<AtlasTexture.MaterialMergeGroup>(), null, null),
                    resultingTextures,
                    atlasResult.PreserveBump2ndMaterials,
                    atlasResult.PreservedOriginalUVChannel);

                foreach (var rt in compiledTextures.Values)
                    if (!retainedRenderTextures.Contains(rt)) rt.Dispose();

                foreach (var descriptor in tuningResult.TextureDescriptors)
                    domain.RegisterPostProcessingAndLazyGPUReadBack(
                        descriptor.Key, descriptor.Value);

                completed.Add(block);
            }

            processingTimer.Stop();

            var temporaryTimingDiagnostic = string.Format(
                "AutoAtlasTexture:info:TemporaryTimingDiagnostic".GetLocalize(),
                mergePlan.PlanningTimeMilliseconds,
                mergePlan.StandaloneAnalysisTimeMilliseconds,
                mergePlan.StandaloneRelocationTimeMilliseconds,
                mergePlan.StandaloneAnalysisCount,
                executionDoAtlasTimeMilliseconds,
                executionRelocationTimeMilliseconds,
                executionDoAtlasCount,
                Math.Max(
                    0,
                    mergePlan.PlanningTimeMilliseconds
                        - mergePlan.StandaloneAnalysisTimeMilliseconds),
                mergePlan.StandalonePreserveSetupMilliseconds,
                mergePlan.StandaloneAtlasContextMilliseconds,
                mergePlan.StandaloneIslandNormalizeMilliseconds,
                mergePlan.StandaloneRelocationStageMilliseconds,
                mergePlan.StandaloneResolutionPlanMilliseconds,
                executionPreserveSetupMilliseconds,
                executionAtlasContextMilliseconds,
                executionIslandNormalizeMilliseconds,
                executionRelocationStageMilliseconds,
                executionResolutionPlanMilliseconds,
                executionMeshGenerationMilliseconds,
                executionTextureGenerationMilliseconds,
                mergePlan.PreparedGroupCount,
                mergePlan.SharedRendererCount,
                mergePlan.GroupsWithSharedRendererCount,
                mergePlan.GroupPairsSharingRendererCount,
                mergePlan.GroupPairsSharingSourceMeshCount,
                mergePlan.Selections.Count,
                mergePlan.CrossAtomicMaterialGroupCount,
                mergePlan.MergeHostCount,
                mergePlan.EvaluatedMergeCandidateCount,
                mergePlan.CandidateWithinHostCurrentHeightCount,
                mergePlan.AdoptableMergeCandidateCount,
                mergePlan.RejectedPixelCount,
                mergePlan.RejectedMemoryCount,
                mergePlan.RejectedMaxDimensionCount,
                mergePlan.RejectedSamplingCount,
                mergePlan.RejectedStreamingCount);

            if (completed.Count == 0)
            {
                TTTLog.ReportingObject(configuration, () =>
                    TTTLog.Info(
                        "AutoAtlasTexture:info:TargetNotFoundWithTemporaryTiming",
                        processingTimer.ElapsedMilliseconds,
                        temporaryTimingDiagnostic));
                return;
            }

            TTTLog.ReportingObject(configuration, () =>
                TTTLog.Info(
                    "AutoAtlasTexture:info:RelocateResultWithTemporaryTiming",
                    string.Join("\n\n", completed),
                    processingTimer.ElapsedMilliseconds,
                    temporaryTimingDiagnostic));
        }

        private static bool ReferencesAreExclusive(
            HashSet<Material> allReferencedMaterials,
            HashSet<Material> group,
            IReadOnlyDictionary<Material, IReadOnlyDictionary<string, Texture>> propertyTextures,
            HashSet<Texture> replacedTextures)
        {
            foreach (var material in allReferencedMaterials)
            {
                var isTarget = group.Contains(material);
                propertyTextures.TryGetValue(material, out var atlasProperties);
                // Reuse TTT's shader-aware property enumeration. The old
                // implementation only saw renderer materials and could overlook
                // materials referenced exclusively by animations.
                foreach (var property in material.GetTextureReferences())
                {
                    if (!replacedTextures.Contains(property.Value)) continue;
                    if (!isTarget || atlasProperties == null ||
                        !atlasProperties.TryGetValue(property.Key, out var atlasTexture) ||
                        atlasTexture != property.Value)
                        return false;
                }
            }
            return true;
        }

        private static List<HashSet<Material>> MakeTextureConnectedGroups(
            HashSet<Material> materials,
            IReadOnlyDictionary<Material, IReadOnlyDictionary<string, Texture>> properties)
        {
            var materialsByTexture = new Dictionary<Texture, HashSet<Material>>();
            foreach (var material in materials)
            {
                foreach (var texture in properties[material].Values)
                {
                    if (texture == null) continue;
                    if (!materialsByTexture.TryGetValue(texture, out var consumers))
                        materialsByTexture[texture] = consumers = new HashSet<Material>();
                    consumers.Add(material);
                }
            }

            var remaining = new HashSet<Material>(materials.Where(material =>
                properties[material].Count != 0));
            var results = new List<HashSet<Material>>();
            while (remaining.Count != 0)
            {
                var seed = remaining.First();
                remaining.Remove(seed);
                var group = new HashSet<Material> { seed };
                var pending = new Queue<Material>();
                pending.Enqueue(seed);

                while (pending.Count != 0)
                {
                    var current = pending.Dequeue();
                    foreach (var texture in properties[current].Values)
                    {
                        if (texture == null) continue;
                        foreach (var adjacent in materialsByTexture[texture])
                        {
                            if (!remaining.Remove(adjacent)) continue;
                            group.Add(adjacent);
                            pending.Enqueue(adjacent);
                        }
                    }
                }
                results.Add(group);
            }
            return results;
        }
    }
}
