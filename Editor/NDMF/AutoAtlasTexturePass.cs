#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using nadena.dev.ndmf.localization;
using net.rs64.TexTransCore;
using net.rs64.TexTransCore.UVIsland;
using net.rs64.TexTransTool.TextureAtlas;
using net.rs64.TexTransTool.TextureAtlas.FineTuning;
using net.rs64.TexTransTool.Editor;
using net.rs64.TexTransTool.TextureAtlas.IslandSizePriorityTuner;
using net.rs64.TexTransTool.Utils;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
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

    internal sealed class AutoAtlasTextureTemporaryDiagnosticError : SimpleError
    {
        private readonly string _message;

        internal AutoAtlasTextureTemporaryDiagnosticError(string message)
        {
            _message = message;
        }

        public override Localizer Localizer => TTTLog.NDMFLocalizer;
        public override ErrorSeverity Severity => ErrorSeverity.Information;
        public override string TitleKey =>
            "AutoAtlasTexture:info:TemporaryDiagnosticMessage";
        public override string[] TitleSubst => new[] { _message };

        public override VisualElement CreateVisualElement(ErrorReport report)
        {
            var root = new VisualElement();
            root.style.flexGrow = 1;
            root.Add(base.CreateVisualElement(report));

            var copyButton = new Button(() =>
                EditorGUIUtility.systemCopyBuffer = _message)
            {
                text = "AutoAtlasTexture:button:CopyDiagnostics"
                    .GetLocalize(),
            };
            copyButton.style.alignSelf = Align.FlexEnd;
            root.Add(copyButton);

            return root;
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
                transform => new[]
                {
                    pathRemapper.GetVirtualPathForObject(transform),
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
            AnimationIndex? animationIndex = null,
            ObjectPathRemapper? objectPathRemapper = null,
            Transform? avatarRoot = null)
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
            var twoDimensionalExecutionSucceeded = 0;
            var twoDimensionalExecutionFallback = 0;
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

            // Capture the original renderer/material relationships before
            // standalone atlas execution mutates domain meshes and materials.
            var streamingVisibilityDiagnostic =
                mergePlan.ShadowStreamingExample is { } visibilityStreamingExample
                    ? DescribeStreamingVisibilityRelations(
                        preparedGroups,
                        visibilityStreamingExample,
                        domain.GetMaterials,
                        domain.GetMesh,
                        OriginalMaterialName,
                        configuration.transform.root)
                    : null;

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

                AutoAtlas2DMergeIslandRelocator? twoDimensionalRelocator = null;
                string? twoDimensionalSizeRejection = null;
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
                                            $"出力レイアウトが不適合: actual={metrics.LayoutSize.x}×{metrics.LayoutSize.y}, planned={mergeSelection.PlannedLayoutSize.x}×{mergeSelection.PlannedLayoutSize.y}, outputPixels={metrics.OutputTexturePixelCount}";
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
                                                $"プロパティ解像度が不適合: {property.Key} actual={property.Value.x}×{property.Value.y}, planned={plannedSize.x}×{plannedSize.y}";
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
                        if (failedMerge.PackedRegions != null)
                        {
                            twoDimensionalExecutionFallback += 1;
                            var reason = twoDimensionalRelocator?.FailureDetail;
                            if (string.IsNullOrEmpty(reason))
                                reason = twoDimensionalSizeRejection
                                    ?? "DoAtlasTextureが失敗（配置・サイズ判定の詳細未取得）";
                            twoDimensionalFallbackReasons.Add(
                                $"groups={string.Join(",", failedMerge.GroupIds.Select(id => "g" + id))}, layout={failedMerge.PlannedLayoutSize.x}×{failedMerge.PlannedLayoutSize.y}: {reason}");
                        }
                        // A merge plan is only an optimization. If its
                        // placement cannot be executed losslessly, retry the
                        // original atomic groups without merging them.
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

                    if (memoryCheckedMerge.PackedRegions != null)
                    {
                        twoDimensionalExecutionFallback += 1;
                        var reason = !hasExecutionTextureMemory
                            ? "最終テクスチャメモリを計算できない"
                            : $"最終テクスチャメモリが削減されない: actual={executionTextureMemoryBytes} bytes, baseline={memoryCheckedMerge.BaselineBytes} bytes";
                        twoDimensionalFallbackReasons.Add(
                            $"groups={string.Join(",", memoryCheckedMerge.GroupIds.Select(id => "g" + id))}, layout={memoryCheckedMerge.PlannedLayoutSize.x}×{memoryCheckedMerge.PlannedLayoutSize.y}: {reason}");
                    }
                    EnqueueStandaloneFallback(memoryCheckedMerge);
                    continue;
                }

                if (execution.MergeSelection?.PackedRegions != null)
                    twoDimensionalExecutionSucceeded += 1;

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
                mergePlan.StandaloneAnalysisCount,
                executionDoAtlasTimeMilliseconds,
                executionDoAtlasCount,
                mergePlan.PreparedGroupCount,
                mergePlan.Selections.Count,
                mergePlan.ShadowProfileCount,
                mergePlan.ShadowGroupsWithMultipleProfiles,
                mergePlan.ShadowHostCount,
                mergePlan.ShadowEvaluatedCandidateCount,
                mergePlan.ShadowEvaluatedUsingWiderProfileCount,
                mergePlan.ShadowAdoptableCandidateCount,
                mergePlan.ShadowAdoptableUsingWiderProfileCount,
                mergePlan.ShadowSelectedCount,
                mergePlan.ShadowRejectedPixelCount,
                mergePlan.ShadowRejectedMemoryCount,
                mergePlan.ShadowRejectedMaxDimensionCount,
                mergePlan.ShadowRejectedSamplingCount,
                mergePlan.Shadow2DPlanningMilliseconds,
                mergePlan.ShadowStateSelectionMilliseconds,
                mergePlan.ShadowPlacementAttemptCount,
                mergePlan.ShadowUniqueGeneratedStateCount,
                mergePlan.ShadowScoreStateCallCount,
                mergePlan.ShadowProjectionEvaluationCount,
                mergePlan.ShadowSafetyEvaluationCount,
                mergePlan.PropertyRequirementBuildCount,
                mergePlan.PropertyRequirementBuildMilliseconds,
                mergePlan.ShadowOptimisticExhaustive,
                mergePlan.ShadowOptimisticLowerBoundMilliseconds,
                mergePlan.ShadowOptimisticPixelViableCount,
                mergePlan.ShadowBestBaselinePixels,
                mergePlan.ShadowBestMergedPixels,
                mergePlan.ShadowBestRejectionReason);

            if (mergePlan.BudgetComparison is { } budgetComparison)
                temporaryTimingDiagnostic += "\n" + string.Format(
                    "AutoAtlasTexture:info:TemporarySearchBudgetComparison".GetLocalize(),
                    mergePlan.Shadow2DPlanningMilliseconds,
                    mergePlan.ShadowAdoptableCandidateCount,
                    mergePlan.Selections.Count,
                    mergePlan.Selections.Sum(item => item.PredictedSavingBytes),
                    mergePlan.ShadowPlacementAttemptCount,
                    mergePlan.ShadowUniqueGeneratedStateCount,
                    mergePlan.ShadowStateSelectionMilliseconds,
                    budgetComparison.SearchMilliseconds,
                    budgetComparison.AdoptableCandidateCount,
                    budgetComparison.SelectedCount,
                    budgetComparison.PredictedSavingBytes,
                    budgetComparison.PlacementAttempts,
                    budgetComparison.UniqueStates,
                    budgetComparison.StateSelectionMilliseconds,
                    budgetComparison.SameSelectedGroupLayouts,
                    budgetComparison.ProductionSummary,
                    budgetComparison.ComparisonSummary);

            temporaryTimingDiagnostic += "\n" + string.Format(
                "AutoAtlasTexture:info:Temporary2DProduction".GetLocalize(),
                mergePlan.Selections.Count(selection =>
                    selection.PackedRegions != null),
                twoDimensionalExecutionSucceeded,
                twoDimensionalExecutionFallback);
            if (twoDimensionalFallbackReasons.Count != 0)
                temporaryTimingDiagnostic += "\n" + string.Format(
                    "AutoAtlasTexture:info:Temporary2DFallbackReason".GetLocalize(),
                    string.Join("\n", twoDimensionalFallbackReasons));

            if (configuration.EnableAdditionalAtlasMerging
                && configuration.ExcludeVisibilityAnimatedItemsFromTextureMerging)
            {
                var affectedGroups = preparedGroups
                    .Where(group => group.Renderers.Any(
                        animatedVisibilityRenderers.Contains))
                    .ToArray();
                var affectedMaterials = affectedGroups
                    .SelectMany(group => group.Materials)
                    .Select(OriginalMaterialName)
                    .Distinct()
                    .OrderBy(name => name, StringComparer.Ordinal);
                temporaryTimingDiagnostic += "\n" + string.Format(
                    "AutoAtlasTexture:info:TemporaryVisibilityAnimationExclusion".GetLocalize(),
                    affectedGroups.Length,
                    string.Join(", ", affectedMaterials));
            }

            // The project directory name is useful when comparing logs
            // from several Unity projects. Never include its absolute path.
            var projectDirectory = System.IO.Path.GetDirectoryName(
                Application.dataPath) ?? Application.dataPath;
            var projectName = new System.IO.DirectoryInfo(projectDirectory).Name;
            temporaryTimingDiagnostic = string.Format(
                "AutoAtlasTexture:info:TemporaryProjectName".GetLocalize(),
                projectName)
                + "\n" + temporaryTimingDiagnostic;

            temporaryTimingDiagnostic += "\n" + string.Format(
                "AutoAtlasTexture:info:TemporaryStreamingImpactCount".GetLocalize(),
                mergePlan.ShadowStreamingRegressingCandidateCount);

            if (mergePlan.ShadowStreamingExample is { } streamingExample)
            {
                var candidate = streamingExample.Candidate;
                temporaryTimingDiagnostic += "\n" + string.Format(
                    "AutoAtlasTexture:info:TemporaryStreamingDiagnostic"
                        .GetLocalize(),
                    string.Join(", ", candidate.GroupIds.Select(id => $"g{id}")),
                    candidate.LayoutSize.x,
                    candidate.LayoutSize.y,
                    streamingExample.ActiveGroupId,
                    streamingExample.CameraBias,
                    streamingExample.BaselineBytes,
                    streamingExample.MergedBytes,
                    streamingExample.MergedBytes
                        - streamingExample.BaselineBytes,
                    streamingExample.BaselineEntries,
                    streamingExample.MergedEntries);

                if (streamingVisibilityDiagnostic != null)
                    temporaryTimingDiagnostic += "\n"
                        + streamingVisibilityDiagnostic;
            }

            if (completed.Count == 0)
            {
                var fullDiagnostic = string.Format(
                    "AutoAtlasTexture:info:TargetNotFoundWithTemporaryTiming"
                        .GetLocalize(),
                    processingTimer.ElapsedMilliseconds,
                    temporaryTimingDiagnostic);

                TTTLog.ReportingObject(configuration, () =>
                    ErrorReport.ReportError(
                        new AutoAtlasTextureTemporaryDiagnosticError(
                            fullDiagnostic)));
                return;
            }

            var completedBlocks = string.Join("\n\n", completed);
            var fullResultDiagnostic = string.Format(
                "AutoAtlasTexture:info:RelocateResultWithTemporaryTiming"
                    .GetLocalize(),
                completedBlocks,
                processingTimer.ElapsedMilliseconds,
                temporaryTimingDiagnostic);

            TTTLog.ReportingObject(configuration, () =>
                ErrorReport.ReportError(
                    new AutoAtlasTextureTemporaryDiagnosticError(
                        fullResultDiagnostic)));
        }

        /// <summary>
        /// Temporary diagnostic only. Snapshot which source renderers/material
        /// slots belong to the rejected atomic groups. Shared renderers have
        /// coupled Renderer/GameObject activation, but this cannot prove that
        /// distinct renderers will or will not be animated independently.
        /// </summary>
        internal static string DescribeStreamingVisibilityRelations(
            IReadOnlyList<AutoAtlasTextureMergePlanner.PreparedGroup> groups,
            AutoAtlasTextureMergePlanner.StreamingRegressionExample example,
            Func<Renderer, Material?[]> getMaterials,
            Func<Renderer, Mesh?> getMesh,
            Func<Material, string> originalMaterialName,
            Transform root)
        {
            string SafeName(string? name) =>
                (name ?? "(unnamed)").Replace("\r", " ").Replace("\n", " ");

            string RendererPath(Renderer renderer)
            {
                var parts = new Stack<string>();
                for (var t = renderer.transform; t != null; t = t.parent)
                {
                    parts.Push(SafeName(t.name) + "[" + t.GetSiblingIndex() + "]");
                    if (t == root) break;
                }

                return string.Join("/", parts);
            }

            var byId = groups.ToDictionary(group => group.Id);
            var selected = example.Candidate.GroupIds.Select(id => byId[id])
                .ToArray();

            var groupRendererSets = selected.Select(group =>
                group.Renderers.Where(renderer => renderer != null).ToHashSet())
                .ToArray();

            var commonRenderers = groupRendererSets.Length == 0
                ? new HashSet<Renderer>()
                : new HashSet<Renderer>(groupRendererSets[0]);
            foreach (var renderers in groupRendererSets.Skip(1))
                commonRenderers.IntersectWith(renderers);

            HashSet<Mesh> GroupMeshes(
                HashSet<Renderer> renderers)
            {
                return renderers.Select(getMesh)
                    .Where(mesh => mesh != null)
                    .Select(mesh => mesh!)
                    .ToHashSet();
            }

            var groupMeshes = groupRendererSets.Select(GroupMeshes).ToArray();
            var commonMeshes = groupMeshes.Length == 0
                ? new HashSet<Mesh>()
                : new HashSet<Mesh>(groupMeshes[0]);
            foreach (var meshes in groupMeshes.Skip(1))
                commonMeshes.IntersectWith(meshes);

            var groupDetails = new List<string>();
            foreach (var group in selected)
            {
                var materialNames = group.Materials
                    .Select(originalMaterialName)
                    .Select(SafeName)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToArray();

                var rendererDescriptions = group.Renderers
                    .Where(renderer => renderer != null)
                    .Distinct()
                    .OrderBy(RendererPath, StringComparer.Ordinal)
                    .ThenBy(renderer => renderer.GetType().Name,
                        StringComparer.Ordinal)
                    .Select(renderer =>
                    {
                        var slots = getMaterials(renderer);
                        var matchedSlots = new List<string>();
                        for (var index = 0; index < slots.Length; index += 1)
                        {
                            var material = slots[index];
                            if (material == null || !group.Materials.Contains(material))
                                continue;

                            matchedSlots.Add(index + ":"
                                + SafeName(originalMaterialName(material)));
                        }

                        var mesh = getMesh(renderer);
                        return string.Format(
                            "AutoAtlasTexture:info:TemporaryStreamingRenderer"
                                .GetLocalize(),
                            RendererPath(renderer),
                            renderer.GetType().Name,
                            matchedSlots.Count == 0
                                ? "(none)" : string.Join(", ", matchedSlots),
                            mesh == null ? "(none)" : SafeName(mesh.name),
                            renderer.enabled,
                            renderer.gameObject.activeSelf,
                            renderer.gameObject.activeInHierarchy);
                    })
                    .ToArray();

                groupDetails.Add(string.Format(
                    "AutoAtlasTexture:info:TemporaryStreamingGroup"
                        .GetLocalize(),
                    group.Id,
                    materialNames.Length == 0
                        ? "(none)" : string.Join(", ", materialNames),
                    rendererDescriptions.Length,
                    rendererDescriptions.Length == 0
                        ? "  (none)"
                        : string.Join("\n", rendererDescriptions)));
            }

            var commonRendererNames = commonRenderers
                .Select(renderer => RendererPath(renderer)
                    + " [" + renderer.GetType().Name + "]")
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            var commonMeshNames = commonMeshes
                .Select(mesh => SafeName(mesh.name))
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

            return string.Format(
                "AutoAtlasTexture:info:TemporaryStreamingVisibilityDiagnostic"
                    .GetLocalize(),
                string.Join(", ", selected.Select(group => "g" + group.Id)),
                commonRendererNames.Length,
                commonRendererNames.Length == 0
                    ? "(none)" : string.Join(", ", commonRendererNames),
                commonMeshNames.Length,
                commonMeshNames.Length == 0
                    ? "(none)" : string.Join(", ", commonMeshNames),
                string.Join("\n", groupDetails));
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
