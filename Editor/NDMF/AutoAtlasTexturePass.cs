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

    // Temporary diagnostic UI. This intentionally mirrors the previously
    // validated NDMF Console implementation: wrap SimpleError's normal UI and
    // append the copy button as a direct child instead of depending on NDMF's
    // internal button-container structure.
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

        internal static bool HasLiveTargetSubMesh(
            Mesh mesh,
            Material[] materials,
            HashSet<Material> targetMaterials)
        {
            if (mesh == null || mesh.subMeshCount <= 0)
                return false;

            for (var slot = 0; slot < materials.Length; slot += 1)
            {
                var material = materials[slot];
                if (material == null || !targetMaterials.Contains(material))
                    continue;

                var subMeshIndex = Math.Clamp(
                    slot, 0, mesh.subMeshCount - 1);
                if (mesh.GetSubMesh(subMeshIndex).indexCount != 0)
                    return true;
            }

            return false;
        }
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

            var diagnosticSections = new List<string>();
            var projectDirectory = System.IO.Path.GetDirectoryName(
                Application.dataPath) ?? Application.dataPath;
            var projectName =
                new System.IO.DirectoryInfo(projectDirectory).Name;
            diagnosticSections.Add(string.Format(
                "AutoAtlasTexture:info:TemporaryProjectName".GetLocalize(),
                projectName));

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
                var earlyResultText = string.Format(
                    "AutoAtlasTexture:info:TargetNotFound".GetLocalize(),
                    processingTimer.ElapsedMilliseconds);
                var earlyCompleteDiagnosticText = string.Join(
                    "\n\n",
                    diagnosticSections.Concat(new[] { earlyResultText }));

                TTTLog.ReportingObject(configuration, () =>
                    ErrorReport.ReportError(
                        new AutoAtlasTextureTemporaryDiagnosticError(
                            earlyCompleteDiagnosticText)));
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
            var preparationDiagnostics = new List<string>();

            void AppendDiagnostic(string key, params object[] args)
            {
                diagnosticSections.Add(
                    string.Format(key.GetLocalize(), args));
            }

            string DescribeGroupMaterials(HashSet<Material> group) =>
                string.Join(", ",
                    group.Select(OriginalMaterialName)
                        .OrderBy(name => name, StringComparer.Ordinal));

            foreach (var group in groups)
            {
                var diagnosticMaterials = DescribeGroupMaterials(group);
                if (group.Any(material => excludedMaterials.Contains(material)
                    || manuallySelectedMaterials.Contains(material)))
                {
                    preparationDiagnostics.Add(
                        $"SKIP exclusion/manual: {diagnosticMaterials}");
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
                    preparationDiagnostics.Add(
                        $"SKIP renderer eligibility: {diagnosticMaterials} "
                        + $"refs={referencingRenderers.Length}");
                    continue;
                }

                var atlasRenderers = AtlasTexture.FilterExistUVChannel(
                    domain, referencingRenderers, UVChannel.UV0);
                if (atlasRenderers.Length != referencingRenderers.Length)
                {
                    preparationDiagnostics.Add(
                        $"SKIP missing UV0: {diagnosticMaterials} "
                        + $"refs={referencingRenderers.Length} "
                        + $"uv0={atlasRenderers.Length}");
                    continue;
                }

                var texturesToReplace = group
                    .SelectMany(material => propertyTextures[material].Values)
                    .Where(texture => texture != null)
                    .ToHashSet();
                if (texturesToReplace.Count == 0)
                {
                    preparationDiagnostics.Add(
                        $"SKIP no source texture: {diagnosticMaterials}");
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
                    preparationDiagnostics.Add(
                        $"SKIP non-exclusive source texture: "
                        + diagnosticMaterials);
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

            foreach (var preparedGroup in preparedGroups)
            {
                var profiles = preparedGroup.TwoDimensionalProfileHeights
                    .OrderBy(profile => profile.Key.AtlasWidth)
                    .ThenBy(profile => profile.Key.RegionWidth)
                    .Select(profile =>
                        $"{profile.Key.AtlasWidth}/{profile.Key.RegionWidth}"
                        + $"={profile.Value}")
                    .ToArray();
                var standalone = preparedGroup.StandaloneMetrics is { } metrics
                    ? $"{metrics.LayoutSize.x}x{metrics.LayoutSize.y}"
                    : "(none)";
                preparationDiagnostics.Add(
                    $"g{preparedGroup.Id} PREPARED: "
                    + $"{DescribeGroupMaterials(preparedGroup.Materials)} "
                    + $"sources={preparedGroup.SourceTextures.Count} "
                    + $"renderers={preparedGroup.Renderers.Length} "
                    + $"mergeExcluded="
                    + preparedGroup.ExcludedFromTextureMerging
                    + $" standalone={standalone} "
                    + $"baselineKnown={preparedGroup.BaselineCostKnown} "
                    + $"profiles=[{string.Join(", ", profiles)}]");
            }

            string FormatMergeSelections(
                IEnumerable<AutoAtlasTextureMergePlanner.MergeSelection> selections)
            {
                return string.Join("\n", selections.Select(selection =>
                {
                    var ids = string.Join("+",
                        selection.GroupIds.Select(id => "g" + id));
                    var materials = string.Join(", ",
                        selection.GroupIds
                            .SelectMany(id => preparedGroups[id].Materials)
                            .Select(OriginalMaterialName)
                            .Distinct()
                            .OrderBy(name => name, StringComparer.Ordinal));
                    return $"{ids} [{selection.PlannedLayoutSize.x}×{selection.PlannedLayoutSize.y}] " +
                           $"{selection.PredictedSavingBytes} bytes: {materials}";
                }));
            }

            string FormatRejectedCandidates(
                IEnumerable<AutoAtlasTextureMergePlanner
                    .RejectedCandidateDiagnostic> candidates)
            {
                var lines = candidates.Select(candidate =>
                {
                    var ids = string.Join("+",
                        candidate.GroupIds.Select(id => "g" + id));
                    var materials = string.Join(", ",
                        candidate.GroupIds
                            .SelectMany(id => preparedGroups[id].Materials)
                            .Select(OriginalMaterialName)
                            .Distinct()
                            .OrderBy(name => name, StringComparer.Ordinal));
                    return $"{ids} "
                        + $"[{candidate.LayoutSize.x}x{candidate.LayoutSize.y}] "
                        + $"reason={candidate.RejectionReason} "
                        + $"pixels={candidate.BaselinePixels}"
                        + $"->{candidate.MergedPixels} "
                        + $"memory={candidate.BaselineBytes}"
                        + $"->{candidate.MergedBytes} "
                        + $"saving={candidate.SavingBytes} bytes: "
                        + materials;
                }).ToArray();
                return lines.Length == 0
                    ? "(none)"
                    : string.Join("\n", lines);
            }

            var executionGroups = CreateExecutionGroups(
                preparedGroups, mergePlan.Selections);

            var materialBlock = "AutoAtlasTexture:info:MaterialBlock".GetLocalize();
            while (executionGroups.Count != 0)
            {
                var execution = executionGroups.Dequeue();
                var group = execution.Materials;

                // Material replacement is domain-wide. Only renderers that
                // actually draw one of the target materials need atlas UVs;
                // references on empty submeshes are harmless. Fail closed when
                // any live consumer cannot be transformed, or when the live
                // consumer set has changed since planning.
                bool IsLiveTargetRenderer(Renderer renderer)
                {
                    var mesh = domain.GetMesh(renderer);
                    return mesh != null
                        && HasLiveTargetSubMesh(
                            mesh, domain.GetMaterials(renderer), group);
                }

                bool CanAtlasRenderer(Renderer renderer)
                {
                    if (!AtlasTexture.IsAtlasAllowedRenderer(renderer))
                        return false;

                    var mesh = domain.GetMesh(renderer);
                    return mesh != null
                        && mesh.subMeshCount > 0
                        && mesh.HasUV((int)UVChannel.UV0);
                }

                var atlasRenderers = execution.Renderers
                    .Where(renderer => renderer != null)
                    .Where(IsLiveTargetRenderer)
                    .Distinct()
                    .ToArray();
                var currentLiveRenderers = domain
                    .RendererFilterForMaterialFromDomains(group)
                    .Where(renderer => renderer != null)
                    .Where(IsLiveTargetRenderer)
                    .Distinct()
                    .ToHashSet();

                if (atlasRenderers.Length == 0
                    || atlasRenderers.Any(
                        renderer => !CanAtlasRenderer(renderer))
                    || !currentLiveRenderers.SetEquals(atlasRenderers))
                {
                    if (execution.MergeSelection is { } invalidMerge)
                    {
                        twoDimensionalFallbackReasons.Add(
                            $"groups={string.Join(",", invalidMerge.GroupIds.Select(id => "g" + id))}: 実行時の描画対象Renderer集合が計画時と一致しない");
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
                if (!atlasResult.IsSuccess)
                {
                    if (execution.MergeSelection is { } failedMerge)
                    {
                        if (failedMerge.PackedRegions != null)
                        {
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
                        var reason = !hasExecutionTextureMemory
                            ? "最終テクスチャメモリを計算できない"
                            : $"最終テクスチャメモリが削減されない: actual={executionTextureMemoryBytes} bytes, baseline={memoryCheckedMerge.BaselineBytes} bytes";
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


            if (mergePlan.Improvement is { } improvement)
            {
                var improvementText = string.Format(
                    "AutoAtlasTexture:info:SelectionImprovement".GetLocalize(),
                    improvement.CandidateCount,
                    improvement.GreedySavingBytes,
                    improvement.ExactSavingBytes,
                    improvement.ExactSavingBytes
                        - improvement.GreedySavingBytes,
                    FormatMergeSelections(improvement.GreedySelections),
                    FormatMergeSelections(mergePlan.Selections));
                diagnosticSections.Add(improvementText);

                TTTLog.ReportingObject(configuration, () =>
                    TTTLog.Info(
                        "AutoAtlasTexture:info:SelectionImprovement",
                        improvement.CandidateCount,
                        improvement.GreedySavingBytes,
                        improvement.ExactSavingBytes,
                        improvement.ExactSavingBytes
                            - improvement.GreedySavingBytes,
                        FormatMergeSelections(improvement.GreedySelections),
                        FormatMergeSelections(mergePlan.Selections)));
            }

            AppendDiagnostic(
                "AutoAtlasTexture:info:TemporaryPreparationDiagnostic",
                string.Join("\n", preparationDiagnostics));

            if (mergePlan.SearchBudgetComparison is { } searchComparison)
            {
                string FormatDiagnosticSelections(
                    IEnumerable<AutoAtlasTextureMergePlanner.MergeSelection>
                        selections)
                {
                    var formatted = FormatMergeSelections(selections);
                    return string.IsNullOrEmpty(formatted)
                        ? "(none)"
                        : formatted;
                }

                AppendDiagnostic(
                    "AutoAtlasTexture:info:TemporarySearchBudgetHeader",
                    searchComparison.ProductionSearchMilliseconds,
                    searchComparison.ProductionCandidateCount,
                    mergePlan.Selections.Count,
                    mergePlan.Selections.Sum(
                        selection => selection.PredictedSavingBytes),
                    searchComparison.ProductionMaxStatesBeforeRetention,
                    searchComparison.ProductionPrunedStateCount,
                    FormatDiagnosticSelections(mergePlan.Selections));

                foreach (var probe in searchComparison.Probes)
                {
                    if (probe.Aborted)
                    {
                        AppendDiagnostic(
                            "AutoAtlasTexture:info:TemporarySearchBudgetAborted",
                            probe.StateBudget,
                            probe.SearchMilliseconds,
                            probe.AbortReason);
                        continue;
                    }

                    AppendDiagnostic(
                        "AutoAtlasTexture:info:TemporarySearchBudgetProbe",
                        probe.StateBudget,
                        probe.SearchMilliseconds,
                        probe.CandidateCount,
                        probe.SelectedCount,
                        probe.PredictedSavingBytes,
                        probe.MaxStatesBeforeRetention,
                        probe.PrunedStateCount,
                        probe.SameSelectedPlan,
                        FormatDiagnosticSelections(probe.Selections));

                    AppendDiagnostic(
                        "AutoAtlasTexture:info:TemporarySearchBudgetDifferences",
                        probe.StateBudget,
                        probe.AdditionalOrChangedCandidateCount,
                        FormatDiagnosticSelections(
                            probe.AdditionalOrChangedCandidates),
                        probe.MissingOrChangedCandidateCount,
                        FormatDiagnosticSelections(
                            probe.MissingOrChangedCandidates));

                    if (probe.Rejected4096Candidates.Count != 0)
                    {
                        AppendDiagnostic(
                            "AutoAtlasTexture:info:TemporaryRejected4096Candidates",
                            probe.StateBudget,
                            FormatRejectedCandidates(
                                probe.Rejected4096Candidates));
                    }
                }
            }

            string? fallbackText = null;
            if (twoDimensionalFallbackReasons.Count != 0)
            {
                fallbackText = string.Format(
                    "AutoAtlasTexture:info:TwoDimensionalFallback".GetLocalize(),
                    string.Join("\n", twoDimensionalFallbackReasons));
                TTTLog.ReportingObject(configuration, () =>
                    TTTLog.Info(
                        "AutoAtlasTexture:info:TwoDimensionalFallback",
                        string.Join("\n", twoDimensionalFallbackReasons)));
            }

            var resultText = completed.Count == 0
                ? string.Format(
                    "AutoAtlasTexture:info:TargetNotFound".GetLocalize(),
                    processingTimer.ElapsedMilliseconds)
                : string.Format(
                    "AutoAtlasTexture:info:RelocateResult".GetLocalize(),
                    string.Join("\n\n", completed),
                    processingTimer.ElapsedMilliseconds);

            var copySections = new List<string>(diagnosticSections);
            if (!string.IsNullOrEmpty(fallbackText))
                copySections.Add(fallbackText);
            copySections.Add(resultText);
            var completeDiagnosticText =
                string.Join("\n\n", copySections);

            TTTLog.ReportingObject(configuration, () =>
                ErrorReport.ReportError(
                    new AutoAtlasTextureTemporaryDiagnosticError(
                        completeDiagnosticText)));

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
