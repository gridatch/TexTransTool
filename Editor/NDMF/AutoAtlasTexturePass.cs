#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
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
            var configuration = configurations[0];
            var animationServices =
                configuration.EnableAdditionalAtlasMerging
                && configuration.ExcludeVisibilityAnimatedItemsFromTextureMerging
                    ? context.Extension<AnimatorServicesContext>()
                    : null;
            AutoAtlasTextureProcessor.Execute(
                session.Domain,
                configuration,
                manualAtlases,
                animationServices?.AnimationIndex,
                animationServices?.ObjectPathRemapper,
                context.AvatarRootTransform);
        }
    }

    /// <summary>
    /// Detects visibility animation on renderers and their parent GameObjects.
    /// This only determines whether a property can take a value different from
    /// its current one; it does not infer runtime usage frequency or menu reachability.
    /// </summary>
    internal static class AutoAtlasTextureVisibilityAnimationDetector
    {
        internal static HashSet<Renderer> Detect(
            AnimationIndex animationIndex,
            ObjectPathRemapper pathRemapper,
            Transform avatarRoot,
            IEnumerable<Renderer> renderers)
        {
            return Detect(
                animationIndex,
                avatarRoot,
                renderers,
                transform =>
                {
                    // An NDMF ReplaceObject can map multiple historical animation
                    // paths to the same current object. Inspect every alias.
                    // Register new objects first, just as the original lookup did.
                    pathRemapper.GetVirtualPathForObject(transform);
                    return pathRemapper.GetAllPathsForObject(transform);
                });
        }

        // Allows tests to supply deterministic object paths without requiring
        // a live NDMF BuildContext.
        internal static HashSet<Renderer> Detect(
            AnimationIndex animationIndex,
            Transform avatarRoot,
            IEnumerable<Renderer> renderers,
            Func<Transform, IEnumerable<string>> pathsForObject)
        {
            var animatedRenderers = new HashSet<Renderer>();
            var gameObjectVisibility = new Dictionary<Transform, bool>();

            foreach (var renderer in renderers)
            {
                if (renderer == null) continue;

                var transform = renderer.transform;
                var cursor = transform;
                var insideAvatar = false;
                while (cursor != null)
                {
                    if (cursor == avatarRoot)
                    {
                        insideAvatar = true;
                        break;
                    }
                    cursor = cursor.parent;
                }
                if (!insideAvatar) continue;

                if (HasAnimatedVisibility(
                        animationIndex, pathsForObject(transform),
                        renderer.GetType(), "m_Enabled", renderer.enabled))
                {
                    animatedRenderers.Add(renderer);
                    continue;
                }

                for (cursor = transform; cursor != null; cursor = cursor.parent)
                {
                    if (!gameObjectVisibility.TryGetValue(cursor, out var animated))
                    {
                        animated = HasAnimatedVisibility(
                            animationIndex, pathsForObject(cursor),
                            typeof(GameObject), "m_IsActive",
                            cursor.gameObject.activeSelf);
                        gameObjectVisibility.Add(cursor, animated);
                    }

                    if (animated)
                    {
                        animatedRenderers.Add(renderer);
                        break;
                    }

                    if (cursor == avatarRoot) break;
                }
            }

            return animatedRenderers;
        }

        private static bool HasAnimatedVisibility(
            AnimationIndex index, IEnumerable<string> paths,
            Type componentType, string propertyName, bool currentValue)
        {
            foreach (var path in paths.Distinct(StringComparer.Ordinal))
            {
                foreach (var clip in index.GetClipsForObjectPath(path))
                {
                    foreach (var binding in clip.GetFloatCurveBindings())
                    {
                        if (binding.path != path
                            || binding.propertyName != propertyName
                            || binding.type == null
                            || !binding.type.IsAssignableFrom(componentType)
                            || (propertyName == "m_IsActive"
                                ? binding.type != typeof(GameObject)
                                : !typeof(Renderer).IsAssignableFrom(binding.type)))
                            continue;

                        if (CurveChangesVisibility(
                                clip.GetFloatCurve(binding), currentValue))
                            return true;
                    }
                }
            }

            return false;
        }

        internal static bool CurveChangesVisibility(
            AnimationCurve? curve, bool currentValue)
        {
            if (curve == null) return false;
            // Boolean animation properties are encoded as float 0 or 1.
            // Curves that keep the current state must not exclude an item.
            return curve.keys.Any(key =>
                !float.IsNaN(key.value)
                && !float.IsInfinity(key.value)
                && (key.value >= 0.5f) != currentValue);
        }
    }

    internal static class AutoAtlasTextureProcessor
    {
        internal sealed class ExecutionGroup
        {
            internal readonly HashSet<Material> Materials;
            internal readonly Renderer[] Renderers;
            internal readonly HashSet<Texture> SourceTextures;
            internal readonly AutoAtlasTextureMergePlanner.MergeSelection? MergeSelection;

            internal ExecutionGroup(
                HashSet<Material> materials,
                Renderer[] renderers,
                HashSet<Texture> sourceTextures,
                AutoAtlasTextureMergePlanner.MergeSelection? mergeSelection)
            {
                Materials = materials;
                Renderers = renderers;
                SourceTextures = sourceTextures;
                MergeSelection = mergeSelection;
            }
        }

        internal static Queue<ExecutionGroup> CreateExecutionGroups(
            IReadOnlyList<AutoAtlasTextureMergePlanner.PreparedGroup> preparedGroups,
            IReadOnlyList<AutoAtlasTextureMergePlanner.MergeSelection> selections)
        {
            var selectedIds = selections.SelectMany(selection => selection.GroupIds)
                .ToHashSet();
            var queue = new Queue<ExecutionGroup>();

            foreach (var selection in selections)
            {
                var selected = selection.GroupIds
                    .Select(id => preparedGroups[id])
                    .ToArray();
                queue.Enqueue(new ExecutionGroup(
                    selected.SelectMany(group => group.Materials).ToHashSet(),
                    selected.SelectMany(group => group.Renderers).Distinct().ToArray(),
                    selected.SelectMany(group => group.SourceTextures).ToHashSet(),
                    selection));
            }

            foreach (var prepared in preparedGroups.Where(
                         group => !selectedIds.Contains(group.Id)))
            {
                queue.Enqueue(new ExecutionGroup(
                    prepared.Materials,
                    prepared.Renderers,
                    prepared.SourceTextures,
                    null));
            }

            return queue;
        }

        internal static void EnqueueStandaloneFallback(
            Queue<ExecutionGroup> queue,
            AutoAtlasTextureMergePlanner.MergeSelection failedMerge,
            IReadOnlyList<AutoAtlasTextureMergePlanner.PreparedGroup> preparedGroups)
        {
            foreach (var id in failedMerge.GroupIds)
            {
                var prepared = preparedGroups[id];
                queue.Enqueue(new ExecutionGroup(
                    prepared.Materials,
                    prepared.Renderers,
                    prepared.SourceTextures,
                    null));
            }
        }

        internal static bool HasStrictPixelReduction(
            long finalPixels,
            long baselinePixels)
        {
            return finalPixels > 0 && finalPixels < baselinePixels;
        }

        internal static bool RetainsAnySourceTexture(
            IEnumerable<Material> materials,
            HashSet<Texture> sourceTextures)
        {
            return materials
                .Where(material => material != null)
                .SelectMany(material => material.EnumerateReferencedTextures())
                .Any(sourceTextures.Contains);
        }

        internal static void DestroyUnappliedAtlasedMaterials(
            IEnumerable<Material>? materials)
        {
            if (materials == null) return;

            foreach (var material in materials
                         .Where(material => material != null)
                         .Distinct())
            {
                UnityEngine.Object.DestroyImmediate(material);
            }
        }

        internal static void DestroyUnappliedAtlasedMeshes(
            IEnumerable<Mesh>? meshes)
        {
            if (meshes == null) return;

            foreach (var mesh in meshes
                         .Where(mesh => mesh != null)
                         .Distinct())
            {
                UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        private static string FormatLocalized(
            string key,
            params object[] args)
        {
            return string.Format(key.GetLocalize(), args);
        }

        internal static AutoAtlasTexture[] GetActiveConfigurations(GameObject root)
        {
            return root.GetComponentsInChildren<AutoAtlasTexture>(true)
                .Where(component => component != null &&
                    TexTransBehaviorSearch.CheckIsActive(component.gameObject, root))
                .ToArray();
        }

        internal static List<Renderer> GetAutoAtlasAllowedRenderers(
            IRendererTargeting domain, AutoAtlasTexture configuration)
        {
            // AutoAtlasTexture adds automatic material selection and lossless
            // sizing on top of AtlasTexture. Renderer eligibility must therefore
            // follow AtlasTexture's normal default target resolution instead of
            // inventing a broader "include disabled" rule.
            var excludedRenderers = domain
                .GetDomainsRenderers(configuration.ExcludedRenderers)
                .ToHashSet();
            return AtlasTexture.GetAtlasAllowedRenderers(
                    domain,
                    domain.EnumerateRenderer(),
                    includeDisabledRenderer: false)
                .Where(renderer => !excludedRenderers.Contains(renderer))
                .ToList();
        }

        internal static Renderer[] ResolveAutoAtlasTargetRenderers(
            IRendererTargeting domain,
            AutoAtlasTexture configuration,
            HashSet<Material> targetMaterials)
        {
            var allowedRenderers =
                GetAutoAtlasAllowedRenderers(domain, configuration);
            var targetRenderers = AtlasTexture.FilterTargetRenderers(
                domain, allowedRenderers, targetMaterials);
            return AtlasTexture.FilterExistUVChannel(
                domain, targetRenderers, UVChannel.UV0);
        }

        internal static Renderer[] GetAAONegotiationRenderers(
            IRendererTargeting domain, AutoAtlasTexture configuration)
        {
            return GetAutoAtlasAllowedRenderers(domain, configuration)
                .ToArray();
        }

        internal static void Execute(
            IDomain domain,
            AutoAtlasTexture configuration,
            IReadOnlyCollection<AtlasTexture> manualAtlases,
            AnimationIndex? animationIndex = null,
            ObjectPathRemapper? objectPathRemapper = null,
            Transform? avatarRoot = null)
        {
            var processingTimer = System.Diagnostics.Stopwatch.StartNew();

            // Store only the NDMF console summary for this build.
            var completed = new List<string>();

            var twoDimensionalFallbackReasons = new List<string>();

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

            var allRenderers = domain.EnumerateRenderer()
                .Where(renderer => renderer != null)
                .ToArray();

            // Auto-select materials from the same renderer population that a
            // normal AtlasTexture with its default IncludeDisabledRenderer=false
            // would use. Additional referenced materials are still inspected
            // separately by the source-texture exclusivity guard.
            var excludedRenderers = domain
                .GetDomainsRenderers(configuration.ExcludedRenderers)
                .ToHashSet();
            var atlasAllowedRenderers =
                GetAutoAtlasAllowedRenderers(domain, configuration);
            var candidateMaterials = atlasAllowedRenderers
                .SelectMany(domain.GetMaterials)
                .UOfType<Material>()
                .ToHashSet();
            var allReferencedMaterials = domain.GetAllMaterials();
            if (candidateMaterials.Count == 0)
            {
                processingTimer.Stop();
                TTTLog.ReportingObject(configuration, () =>
                    TTTLog.Info(
                        "AutoAtlasTexture:info:TargetNotFound",
                        processingTimer.ElapsedMilliseconds));
                return;
            }

            // Material grouping remains shader/property-aware, but it is now
            // scoped to the same renderer-selected materials as AtlasTexture.
            var textureUsages = new MaterialGroupingContext(
                candidateMaterials,
                UVChannel.UV0,
                primaryTexturePropertyOrMaximum: null);
            var propertyTextures = textureUsages.ContainsTextureDictionaries;

            var excludedMaterials = domain.GetDomainsMaterialsHashSet(
                configuration.ExcludedMaterials);
            var mergingExcludedRenderers = domain
                .GetDomainsRenderers(configuration.TextureMergingExcludedRenderers)
                .ToHashSet();
            var mergingExcludedMaterials = domain.GetDomainsMaterialsHashSet(
                configuration.TextureMergingExcludedMaterials);
            var animatedVisibilityRenderers =
                configuration.EnableAdditionalAtlasMerging
                && configuration.ExcludeVisibilityAnimatedItemsFromTextureMerging
                && animationIndex != null
                && objectPathRemapper != null
                && avatarRoot != null
                    ? AutoAtlasTextureVisibilityAnimationDetector.Detect(
                        animationIndex, objectPathRemapper, avatarRoot,
                        allRenderers)
                    : new HashSet<Renderer>();

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
                IncludeDisabledRenderer = false,
                AtlasTargetUVChannel = UVChannel.UV0,
                IslandPadding = Mathf.Clamp(configuration.IslandPadding, 0f, 0.05f),
            };
            var groups = MakeTextureConnectedGroups(
                candidateMaterials, propertyTextures);
            var preparedGroups =
                new List<AutoAtlasTextureMergePlanner.PreparedGroup>();

            foreach (var group in groups)
            {
                if (group.Any(material => excludedMaterials.Contains(material)
                    || manuallySelectedMaterials.Contains(material)))
                {
                    continue;
                }

                // Preserve the explicit AutoAtlas renderer exclusion contract.
                // A shared material would otherwise be replaced globally even
                // though the user explicitly excluded one of its consumers.
                var explicitlyExcludedConsumers = excludedRenderers
                    .Where(renderer => domain.GetMaterials(renderer)
                        .UOfType<Material>()
                        .Any(group.Contains))
                    .ToArray();
                if (explicitlyExcludedConsumers.Length != 0)
                {
                    continue;
                }

                // Resolve renderers through the same AtlasTexture-equivalent
                // path that execution revalidation uses.
                var atlasRenderers = ResolveAutoAtlasTargetRenderers(
                    domain, configuration, group);
                if (atlasRenderers.Length == 0)
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
                    ExcludedFromTextureMerging = IsExcludedFromTextureMerging(
                        group, atlasRenderers, mergingExcludedMaterials,
                        mergingExcludedRenderers)
                        || atlasRenderers.Any(animatedVisibilityRenderers.Contains),
                });
            }

            var mergePlan = AutoAtlasTextureMergePlanner.Plan(
                domain,
                configuration,
                settings,
                propertyTextures,
                preparedGroups);

            var executionGroups = CreateExecutionGroups(
                preparedGroups, mergePlan.Selections);

            var materialBlock = "AutoAtlasTexture:info:MaterialBlock".GetLocalize();
            while (executionGroups.Count != 0)
            {
                var execution = executionGroups.Dequeue();
                var group = execution.Materials;

                // Revalidate against the exact same renderer-targeting rule
                // used during planning. Do not broaden execution to every
                // domain-wide material consumer; AutoAtlas is required to
                // preserve AtlasTexture's normal target semantics.
                var atlasRenderers = ResolveAutoAtlasTargetRenderers(
                    domain, configuration, group);
                var plannedRenderers = execution.Renderers
                    .Where(renderer => renderer != null)
                    .Distinct()
                    .ToHashSet();

                if (atlasRenderers.Length == 0
                    || !plannedRenderers.SetEquals(atlasRenderers))
                {
                    if (execution.MergeSelection is { } invalidMerge)
                    {
                        twoDimensionalFallbackReasons.Add(
                            $"groups={string.Join(",", invalidMerge.GroupIds.Select(id => "g" + id))}: "
                            + FormatLocalized(
                                "AutoAtlasTexture:fallback:RendererSetChanged"));
                        EnqueueStandaloneFallback(
                            executionGroups, invalidMerge, preparedGroups);
                    }
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

                AutoAtlas2DMergeIslandRelocator? twoDimensionalRelocator = null;
                string? twoDimensionalSizeRejection = null;
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
                        IslandRelocatorFactory = mergeAtomicGroupByMaterial != null
                            ? atlasContext =>
                            {
                                if (execution.MergeSelection?.PackedRegions
                                    is { } packedRegions)
                                {
                                    twoDimensionalRelocator =
                                        new AutoAtlas2DMergeIslandRelocator(
                                            atlasContext,
                                            mergeAtomicGroupByMaterial,
                                            packedRegions,
                                            execution.MergeSelection.PlannedLayoutSize.x);
                                    return twoDimensionalRelocator;
                                }

                                throw new InvalidOperationException(
                                    "Production merge candidates must have 2D packed regions.");
                            }
                            : null,
                        ForcedAutoAtlasWidth =
                            execution.MergeSelection?.PackedRegions != null
                                ? execution.MergeSelection.PlannedLayoutSize.x
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
                                    || (mergeSelection.PackedRegions != null
                                        && metrics.LayoutSize.x
                                            != mergeSelection.PlannedLayoutSize.x)
                                    || metrics.LayoutSize.x
                                        > mergeSelection.PlannedLayoutSize.x
                                    || metrics.LayoutSize.y
                                        > mergeSelection.PlannedLayoutSize.y)
                                {
                                    if (mergeSelection.PackedRegions != null)
                                        twoDimensionalSizeRejection =
                                            FormatLocalized(
                                                "AutoAtlasTexture:fallback:LayoutMismatch",
                                                metrics.LayoutSize.x,
                                                metrics.LayoutSize.y,
                                                mergeSelection.PlannedLayoutSize.x,
                                                mergeSelection.PlannedLayoutSize.y,
                                                metrics.OutputTexturePixelCount);
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
                                        if (mergeSelection.PackedRegions != null)
                                            twoDimensionalSizeRejection =
                                                FormatLocalized(
                                                    "AutoAtlasTexture:fallback:PropertyResolutionMismatch",
                                                    property.Key,
                                                    property.Value.x,
                                                    property.Value.y,
                                                    plannedSize.x,
                                                    plannedSize.y);
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
                if (!atlasResult.IsSuccess)
                {
                    if (execution.MergeSelection is { } failedMerge)
                    {
                        if (failedMerge.PackedRegions != null)
                        {
                            var reason = twoDimensionalRelocator?.FailureDetail;
                            if (string.IsNullOrEmpty(reason))
                                reason = twoDimensionalSizeRejection
                                    ?? FormatLocalized(
                                        "AutoAtlasTexture:fallback:AtlasExecutionFailed");
                            twoDimensionalFallbackReasons.Add(
                                $"groups={string.Join(",", failedMerge.GroupIds.Select(id => "g" + id))}, layout={failedMerge.PlannedLayoutSize.x}×{failedMerge.PlannedLayoutSize.y}: {reason}");
                        }
                        // A merge plan is only an optimization. If its
                        // placement cannot be executed losslessly, retry the
                        // original atomic groups without merging them.
                        EnqueueStandaloneFallback(
                            executionGroups, failedMerge, preparedGroups);
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
                    && !HasStrictPixelReduction(
                        finalOutputPixels,
                        originalPixels))
                {
                    foreach (var rt in compiledTextures.Values
                                 .Concat(tuningResult.TextureDescriptors.Keys)
                                 .Distinct())
                    {
                        rt.Dispose();
                    }
                    DestroyUnappliedAtlasedMeshes(
                        atlasResult.AtlasedMeshes);
                    continue;
                }

                if (execution.MergeSelection is { } pixelCheckedMerge
                    && !HasStrictPixelReduction(
                        finalOutputPixels,
                        pixelCheckedMerge.BaselinePixels))
                {
                    foreach (var rt in compiledTextures.Values
                                 .Concat(tuningResult.TextureDescriptors.Keys)
                                 .Distinct())
                    {
                        rt.Dispose();
                    }
                    DestroyUnappliedAtlasedMeshes(
                        atlasResult.AtlasedMeshes);

                    if (pixelCheckedMerge.PackedRegions != null)
                    {
                        twoDimensionalFallbackReasons.Add(
                            $"groups={string.Join(",", pixelCheckedMerge.GroupIds.Select(id => "g" + id))}, "
                            + $"layout={pixelCheckedMerge.PlannedLayoutSize.x}×{pixelCheckedMerge.PlannedLayoutSize.y}: "
                            + FormatLocalized(
                                "AutoAtlasTexture:fallback:FinalPixelsNotReduced",
                                finalOutputPixels,
                                pixelCheckedMerge.BaselinePixels));
                    }

                    EnqueueStandaloneFallback(
                        executionGroups,
                        pixelCheckedMerge,
                        preparedGroups);
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
                    DestroyUnappliedAtlasedMeshes(
                        atlasResult.AtlasedMeshes);

                    if (memoryCheckedMerge.PackedRegions != null)
                    {
                        var reason = !hasExecutionTextureMemory
                            ? FormatLocalized(
                                "AutoAtlasTexture:fallback:FinalMemoryUnknown")
                            : FormatLocalized(
                                "AutoAtlasTexture:fallback:FinalMemoryNotReduced",
                                executionTextureMemoryBytes,
                                memoryCheckedMerge.BaselineBytes);
                        twoDimensionalFallbackReasons.Add(
                            $"groups={string.Join(",", memoryCheckedMerge.GroupIds.Select(id => "g" + id))}, layout={memoryCheckedMerge.PlannedLayoutSize.x}×{memoryCheckedMerge.PlannedLayoutSize.y}: {reason}");
                    }
                    EnqueueStandaloneFallback(
                        executionGroups, memoryCheckedMerge, preparedGroups);
                    continue;
                }

                var resultingTextures = tuningResult.RenderTextures.ToDictionary(
                    item => item.Key,
                    item => engine.GetReferenceRenderTexture(item.Value));
                var retainedRenderTextures = tuningResult.TextureDescriptors.Keys.ToHashSet();

                var atlasMaterialMaps =
                    AtlasTexture.GenerateAtlasedMaterialMaps(
                        domain,
                        group,
                        settings,
                        (new List<AtlasTexture.MaterialMergeGroup>(), null, null),
                        resultingTextures,
                        atlasResult.PreserveBump2ndMaterials,
                        atlasResult.PreservedOriginalUVChannel);

                if (RetainsAnySourceTexture(
                        atlasMaterialMaps.materialMap.Values,
                        execution.SourceTextures))
                {
                    DestroyUnappliedAtlasedMaterials(
                        atlasMaterialMaps.materialMap.Values
                            .Concat(
                                atlasMaterialMaps
                                    .domainsMaterial2ReplaceMaterial.Values));
                    foreach (var rt in compiledTextures.Values
                                 .Concat(tuningResult.TextureDescriptors.Keys)
                                 .Distinct())
                    {
                        rt.Dispose();
                    }
                    DestroyUnappliedAtlasedMeshes(
                        atlasResult.AtlasedMeshes);

                    if (execution.MergeSelection is { } retainedSourceMerge)
                    {
                        if (retainedSourceMerge.PackedRegions != null)
                        {
                            twoDimensionalFallbackReasons.Add(
                                $"groups={string.Join(",", retainedSourceMerge.GroupIds.Select(id => "g" + id))}, "
                                + $"layout={retainedSourceMerge.PlannedLayoutSize.x}×{retainedSourceMerge.PlannedLayoutSize.y}: "
                                + FormatLocalized(
                                    "AutoAtlasTexture:fallback:SourceTextureRetained"));
                        }

                        EnqueueStandaloneFallback(
                            executionGroups,
                            retainedSourceMerge,
                            preparedGroups);
                    }

                    continue;
                }

                var resolutionRoutes = AtlasResolutionRouteReport
                    .Collect(atlasContext, tuningResult);
                var block = string.Format(materialBlock,
                    string.Join(", ", group.Select(OriginalMaterialName)
                        .Distinct().OrderBy(name => name, StringComparer.Ordinal)),
                    executionMetrics.TopFreeFraction,
                    TTTLog.FormatLocalizedLines(
                        AtlasResolutionRouteReport.CreateLocalizedLogLines(
                            resolutionRoutes)));

                var hasSourceTextureMemory =
                    AtlasTexture.TryCalculateSourceTextureMemory(
                        domain,
                        execution.SourceTextures,
                        out var sourceTextureMemoryBytes);
                if (hasExecutionTextureMemory && hasSourceTextureMemory)
                {
                    block += "\n" + string.Format(
                        "AtlasTexture:info:TargetTextureMemory".GetLocalize(),
                        AtlasTexture.FormatTextureMemory(sourceTextureMemoryBytes),
                        AtlasTexture.FormatTextureMemory(executionTextureMemoryBytes));
                }

                AtlasTexture.ReplaceMesh(
                    domain, atlasRenderers, atlasContext, atlasResult.AtlasedMeshes!);
                AtlasTexture.ApplyAtlasedMaterialMaps(
                    domain,
                    atlasMaterialMaps.materialMap,
                    atlasMaterialMaps.domainsMaterial2ReplaceMaterial);

                foreach (var rt in compiledTextures.Values)
                    if (!retainedRenderTextures.Contains(rt)) rt.Dispose();

                foreach (var descriptor in tuningResult.TextureDescriptors)
                    domain.RegisterPostProcessingAndLazyGPUReadBack(
                        descriptor.Key, descriptor.Value);

                completed.Add(block);
            }

            processingTimer.Stop();

            if (twoDimensionalFallbackReasons.Count != 0)
            {
                TTTLog.ReportingObject(configuration, () =>
                    TTTLog.Info(
                        "AutoAtlasTexture:info:TwoDimensionalFallback",
                        string.Join("\n", twoDimensionalFallbackReasons)));
            }

            if (completed.Count == 0)
            {
                TTTLog.ReportingObject(configuration, () =>
                    TTTLog.Info("AutoAtlasTexture:info:TargetNotFound",
                        processingTimer.ElapsedMilliseconds));
                return;
            }

            TTTLog.ReportingObject(configuration, () =>
                TTTLog.Info("AutoAtlasTexture:info:RelocateResult",
                    string.Join("\n\n", completed),
                    processingTimer.ElapsedMilliseconds));
        }

        internal static bool IsExcludedFromTextureMerging(
            IReadOnlyCollection<Material> materials,
            IEnumerable<Renderer> renderers,
            HashSet<Material> excludedMaterials,
            HashSet<Renderer> excludedRenderers)
        {
            return materials.Any(excludedMaterials.Contains)
                || renderers.Any(excludedRenderers.Contains);
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
