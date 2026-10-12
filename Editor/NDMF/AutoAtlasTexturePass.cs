#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using net.rs64.TexTransCore;
using net.rs64.TexTransCore.UVIsland;
using net.rs64.TexTransTool.TextureAtlas;
using net.rs64.TexTransTool.Editor;
using net.rs64.TexTransTool.TextureAtlas.IslandSizePriorityTuner;
using net.rs64.TexTransTool.Utils;
using UnityEngine;

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
                manualAtlases,
                context.Extension<AnimatorServicesContext>());
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
            IReadOnlyCollection<AtlasTexture> manualAtlases,
            AnimatorServicesContext animatorServices)
        {
            // Store only the NDMF console summary for this build.
            var completed = new List<(string Block, int Relocations, long Milliseconds)>();

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
                TTTLog.ReportingObject(configuration, () =>
                    TTTLog.Info("AtlasTexture:info:TargetNotFound"));
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
                if (group.Any(material => excludedMaterials.Contains(material) ||
                    manuallySelectedMaterials.Contains(material)))
                {
                    continue;
                }

                // Every renderer referencing a material must be transformed together:
                // replacing a shared material while leaving another mesh's UVs intact
                // would corrupt that mesh's appearance.
                var referencingRenderers = domain
                    .RendererFilterForMaterialFromDomains(group)
                    .ToArray();
                if (referencingRenderers.Length == 0 ||
                    referencingRenderers.Any(renderer => !allowedRenderers.Contains(renderer)))
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
                    allReferencedMaterials, group, propertyTextures, texturesToReplace))
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
                animatorServices,
                settings,
                propertyTextures,
                preparedGroups);

            var selectedIds = mergePlan.Selections
                .SelectMany(selection => selection.GroupIds)
                .ToHashSet();

            var executionGroups = new List<(
                HashSet<Material> Materials,
                Renderer[] Renderers,
                HashSet<Texture> SourceTextures,
                AutoAtlasTextureMergePlanner.MergeSelection? MergeSelection)>();

            foreach (var selection in mergePlan.Selections)
            {
                var selected = selection.GroupIds
                    .Select(id => preparedGroups[id])
                    .ToArray();
                executionGroups.Add((
                    selected.SelectMany(group => group.Materials).ToHashSet(),
                    selected.SelectMany(group => group.Renderers).Distinct().ToArray(),
                    selected.SelectMany(group => group.SourceTextures).ToHashSet(),
                    selection));
            }

            foreach (var prepared in preparedGroups.Where(
                         group => !selectedIds.Contains(group.Id)))
            {
                executionGroups.Add((
                    prepared.Materials,
                    prepared.Renderers,
                    prepared.SourceTextures,
                    null));
            }

            var materialBlock = "AutoAtlasTexture:info:MaterialBlock".GetLocalize();
            var additionalMergeBlock =
                "AutoAtlasTexture:info:AdditionalMergeBlock".GetLocalize();

            foreach (var execution in executionGroups)
            {
                var group = execution.Materials;
                var atlasRenderers = execution.Renderers;
                var originalPixels = execution.SourceTextures.Sum(texture =>
                    (long)texture.width * texture.height);

                var engine = domain.GetTexTransCoreEngineForUnity();
                var executionMetrics = default(AtlasTexture.AtlasExecutionMetrics);
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
                        AcceptAtlasSize = (atlasContext, size, metrics) =>
                        {
                            executionMetrics = metrics;
                            return metrics.OutputTexturePixelCount > 0
                                && metrics.OutputTexturePixelCount < originalPixels;
                        },
                    });

                if (!atlasResult.IsSuccess) continue;

                using var atlasContext = atlasResult.AtlasContext!;
                var compiledTextures = atlasResult.CompiledAtlasTextures!;
                var tuningResult = AtlasTexture.DoTextureFinTuning(
                    engine, atlasContext, settings, compiledTextures, null);
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

                if (execution.MergeSelection is { } mergeSelection)
                {
                    block += "\n" + string.Format(
                        additionalMergeBlock,
                        FormatBytes(mergeSelection.BaselineBytes),
                        FormatBytes(mergeSelection.MergedBytes),
                        mergeSelection.SavingsRate * 100f,
                        mergeSelection.BaselinePixels,
                        mergeSelection.MergedPixels);
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

                completed.Add((block, executionMetrics.TotalRelocateCount,
                    executionMetrics.RelocationTimeMilliseconds));
            }

            if (completed.Count == 0)
            {
                TTTLog.ReportingObject(configuration, () =>
                    TTTLog.Info("AtlasTexture:info:TargetNotFound"));
                return;
            }

            TTTLog.ReportingObject(configuration, () =>
                TTTLog.Info("AutoAtlasTexture:info:RelocateResult",
                    string.Join("\n\n", completed.Select(item => item.Block)),
                    completed.Sum(item => item.Relocations),
                    completed.Sum(item => item.Milliseconds)));
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024L * 1024L)
                return (bytes / 1024d).ToString("0.0") + " KiB";
            return (bytes / (1024d * 1024d)).ToString("0.00") + " MiB";
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
