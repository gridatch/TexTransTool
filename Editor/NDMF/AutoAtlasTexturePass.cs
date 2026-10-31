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
            var configuration = configurations[0];
            AutoAtlasTextureProcessor.Execute(
                session.Domain,
                configuration,
                manualAtlases);
        }
    }

    internal static class AutoAtlasTextureProcessor
    {
        internal enum ExecutionMode
        {
            Atlas,
            SourceOnly,
        }

        internal sealed class ExecutionGroup
        {
            internal readonly HashSet<Material> Materials;
            internal readonly Renderer[] Renderers;
            internal readonly HashSet<Texture> SourceTextures;
            internal readonly HashSet<Texture> OriginalSourceTextures;
            // Original sources that remain physically needed by runtime-relevant drawable
            // consumers after this execution. This is a cost concept only.
            internal readonly HashSet<Texture> RetainedSourceTextures;
            // Original sources that target materials intentionally keep on
            // bindings outside the atlas/source-optimization rewrite.
            internal readonly HashSet<Texture> ExpectedGeneratedSourceTextures;
            internal readonly AutoAtlasTextureMergePlanner.MergeSelection? MergeSelection;
            internal readonly AutoAtlasTextureMergePlanner.PreparedGroup? AtomicGroup;
            internal readonly ExecutionMode Mode;

            internal ExecutionGroup(
                HashSet<Material> materials,
                Renderer[] renderers,
                HashSet<Texture> sourceTextures,
                HashSet<Texture> originalSourceTextures,
                HashSet<Texture> retainedSourceTextures,
                HashSet<Texture> expectedGeneratedSourceTextures,
                AutoAtlasTextureMergePlanner.MergeSelection? mergeSelection,
                AutoAtlasTextureMergePlanner.PreparedGroup? atomicGroup,
                ExecutionMode mode)
            {
                Materials = materials;
                Renderers = renderers;
                SourceTextures = sourceTextures;
                OriginalSourceTextures = originalSourceTextures;
                RetainedSourceTextures = retainedSourceTextures;
                ExpectedGeneratedSourceTextures =
                    expectedGeneratedSourceTextures;
                MergeSelection = mergeSelection;
                AtomicGroup = atomicGroup;
                Mode = mode;
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
                    selected.SelectMany(group => group.OriginalSourceTextures).ToHashSet(),
                    selected.SelectMany(group => group.RetainedSourceTextures).ToHashSet(),
                    selected.SelectMany(group => group.ExpectedGeneratedSourceTextures).ToHashSet(),
                    selection,
                    null,
                    ExecutionMode.Atlas));
            }

            foreach (var prepared in preparedGroups.Where(
                         group => !selectedIds.Contains(group.Id)))
            {
                EnqueuePreparedAtlas(queue, prepared);
            }

            return queue;
        }

        private static void EnqueuePreparedAtlas(
            Queue<ExecutionGroup> queue,
            AutoAtlasTextureMergePlanner.PreparedGroup prepared)
        {
            queue.Enqueue(new ExecutionGroup(
                prepared.Materials,
                prepared.Renderers,
                prepared.SourceTextures,
                prepared.OriginalSourceTextures,
                prepared.RetainedSourceTextures,
                prepared.ExpectedGeneratedSourceTextures,
                null,
                prepared,
                ExecutionMode.Atlas));
        }

        internal static void EnqueueNonMergedFallback(
            Queue<ExecutionGroup> queue,
            AutoAtlasTextureMergePlanner.MergeSelection failedMerge,
            IReadOnlyList<AutoAtlasTextureMergePlanner.PreparedGroup> preparedGroups)
        {
            foreach (var id in failedMerge.GroupIds)
                EnqueuePreparedAtlas(queue, preparedGroups[id]);
        }

        private static void EnqueueNonAtlasFallback(
            Queue<ExecutionGroup> queue,
            ExecutionGroup execution)
        {
            var prepared = execution.AtomicGroup;
            if (prepared == null
                || prepared.NonAtlasMode
                    != AutoAtlasTextureMergePlanner.NonAtlasMode.SourceOnly)
            {
                return;
            }

            queue.Enqueue(new ExecutionGroup(
                prepared.Materials,
                prepared.Renderers,
                prepared.SourceTextures,
                prepared.OriginalSourceTextures,
                prepared.RetainedSourceTextures,
                prepared.ExpectedGeneratedSourceTextures,
                null,
                prepared,
                ExecutionMode.SourceOnly));
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
            IRendererTargeting domain,
            AutoAtlasTexture configuration,
            IReadOnlyDictionary<
                Renderer, AtlasRendererVisibilityState>? visibilityStates = null)
        {
            // AutoAtlasTexture follows AtlasTexture's normal
            // IncludeDisabledRenderer=false semantics. In NDMF builds this
            // includes renderers that are initially hidden but can become
            // visible at runtime.
            var excludedRenderers = domain
                .GetDomainsRenderers(configuration.ExcludedRenderers)
                .ToHashSet();
            return AtlasTexture.GetAtlasAllowedRenderers(
                    domain,
                    domain.EnumerateRenderer(),
                    includeDisabledRenderer: false,
                    visibilityStates)
                .Where(renderer => !excludedRenderers.Contains(renderer))
                .ToList();
        }

        internal static Renderer[] ResolveAutoAtlasTargetRenderers(
            IRendererTargeting domain,
            AutoAtlasTexture configuration,
            HashSet<Material> targetMaterials,
            IReadOnlyDictionary<
                Renderer, AtlasRendererVisibilityState>? visibilityStates = null)
        {
            var allowedRenderers =
                GetAutoAtlasAllowedRenderers(
                    domain, configuration, visibilityStates);
            var targetRenderers = AtlasTexture.FilterTargetRenderers(
                domain, allowedRenderers, targetMaterials);
            return AtlasTexture.FilterExistUVChannel(
                domain, targetRenderers, UVChannel.UV0);
        }

        internal static Renderer[] GetAAONegotiationRenderers(
            IRendererTargeting domain,
            AutoAtlasTexture configuration,
            IReadOnlyDictionary<
                Renderer, AtlasRendererVisibilityState>? visibilityStates = null)
        {
            var renderers = domain.EnumerateRenderer().ToArray();
            visibilityStates ??=
                AtlasTexture.AnalyzeRendererVisibility(domain, renderers);
            return GetAutoAtlasAllowedRenderers(
                    domain, configuration, visibilityStates)
                .ToArray();
        }

        private static string OriginalMaterialName(Material material)
        {
            // Reuse NDMF's object identity; build-stage suffixes are not
            // stripped or guessed from the material name.
            var identity = ObjectRegistry.GetReference(material);
            if (identity?.Object is Material source && source != null)
                return source.name;
            // ObjectReference snapshots its display name at creation; this
            // remains available even if the original Unity object was destroyed.
            return identity?.ToString() ?? material.name;
        }

        internal static void Execute(
            IDomain domain,
            AutoAtlasTexture configuration,
            IReadOnlyCollection<AtlasTexture> manualAtlases)
        {
            var processingTimer = System.Diagnostics.Stopwatch.StartNew();

            // Store only the NDMF console summary for this build.
            var completed = new List<string>();

            var twoDimensionalFallbackReasons = new List<string>();

            var allRenderers = domain.EnumerateRenderer()
                .Where(renderer => renderer != null)
                .ToArray();
            var visibilityStates =
                AtlasTexture.AnalyzeRendererVisibility(
                    domain, allRenderers);

            // Auto-select materials from the same runtime-relevant renderer
            // population as normal AtlasTexture with
            // IncludeDisabledRenderer=false. Arbitrary material, Animator, and
            // MA references must not become texture-memory cost.
            var excludedRenderers = domain
                .GetDomainsRenderers(configuration.ExcludedRenderers)
                .ToHashSet();
            var atlasAllowedRenderers =
                GetAutoAtlasAllowedRenderers(
                    domain, configuration, visibilityStates);
            var candidateMaterials = atlasAllowedRenderers
                .SelectMany(domain.GetMaterials)
                .UOfType<Material>()
                .ToHashSet();
            var runtimeRelevantTextureConsumers =
                CollectRuntimeRelevantTextureConsumers(
                    domain, visibilityStates);
            if (candidateMaterials.Count == 0)
            {
                processingTimer.Stop();
                TTTLog.ReportingObject(configuration, () =>
                    TTTLog.Info(
                        "AutoAtlasTexture:info:TargetNotFound",
                        processingTimer.ElapsedMilliseconds));
                return;
            }

            var excludedMaterials = domain.GetDomainsMaterialsHashSet(
                configuration.ExcludedMaterials);
            var mergingExcludedRenderers = domain
                .GetDomainsRenderers(configuration.TextureMergingExcludedRenderers)
                .ToHashSet();
            var mergingExcludedMaterials = domain.GetDomainsMaterialsHashSet(
                configuration.TextureMergingExcludedMaterials);
            // Resolve manual AtlasTexture targets through TTT's existing lookup
            // rather than rescanning hierarchy and comparing raw user selections.
            var manuallySelectedMaterials = manualAtlases
                .SelectMany(atlas => atlas.GetTargetMaterials(domain,
                    AtlasTexture.GetAtlasAllowedRenderers(
                        domain,
                        allRenderers,
                        atlas.AtlasSetting.IncludeDisabledRenderer,
                        visibilityStates)))
                .ToHashSet();

            var settings = new AtlasSetting
            {
                AutoAtlasTextureSize = true,
                UsePrimaryMaximumTexture = true,
                IncludeDisabledRenderer = false,
                AtlasTargetUVChannel = UVChannel.UV0,
                IslandPadding = Mathf.Clamp(configuration.IslandPadding, 0f, 0.05f),
                OptimizeConstantTextures =
                    configuration.OptimizeConstantTextures,
                ConstantTextureColorTolerance =
                    configuration.ConstantTextureColorTolerance,
                DisableVisuallyUnusedShaderFeatures =
                    configuration.DisableVisuallyUnusedShaderFeatures,
            };

            // Source optimization is evaluated against the same renderer
            // population that AutoAtlas may rewrite. Materials with any runtime-relevant
            // consumer outside that population are left untouched so the
            // domain-wide material replacement cannot affect an excluded mesh.
            var optimizationExcludedMaterials =
                new HashSet<Material>(excludedMaterials);
            optimizationExcludedMaterials.UnionWith(
                manuallySelectedMaterials);
            foreach (var material in candidateMaterials)
            {
                if (optimizationExcludedMaterials.Contains(material))
                    continue;
                var one = new HashSet<Material> { material };
                var materialRenderers = ResolveAutoAtlasTargetRenderers(
                    domain, configuration, one, visibilityStates);
                if (materialRenderers.Length == 0
                    || HasRuntimeRelevantUntransformedMaterialConsumer(
                        domain, one, materialRenderers, visibilityStates))
                {
                    optimizationExcludedMaterials.Add(material);
                }
            }

            var engine = domain.GetTexTransCoreEngineForUnity();
            var sourceOptimization = AtlasSourceOptimizer.Analyze(
                domain,
                atlasAllowedRenderers.ToArray(),
                candidateMaterials,
                UVChannel.UV0,
                settings,
                null,
                optimizationExcludedMaterials);
            var propertyTextures =
                sourceOptimization.ProjectedTextureUses;
            var optimizationOnlyMaterials =
                sourceOptimization.GetOptimizationOnlyMaterials(
                    candidateMaterials)
                    .Where(material =>
                        !optimizationExcludedMaterials.Contains(material))
                    .ToHashSet();

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
                    domain, configuration, group, visibilityStates);
                if (atlasRenderers.Length == 0)
                {
                    continue;
                }

                // Material replacement is domain-wide. A runtime-relevant
                // consumer that cannot receive the corresponding UV
                // rewrite would otherwise get an atlased material on its
                // original mesh. Permanently hidden consumers remain outside this check; initially
                // hidden renderers that can become visible are included.
                if (HasRuntimeRelevantUntransformedMaterialConsumer(
                        domain,
                        group,
                        atlasRenderers,
                        visibilityStates))
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

                var originalSourceTextures =
                    sourceOptimization.CollectOriginalTextures(group);
                var removedBindings =
                    sourceOptimization.RemovedBindings(group);
                var expectedGeneratedSourceTextures =
                    FindTargetRetainedSourceTextures(
                        group,
                        propertyTextures,
                        originalSourceTextures,
                        removedBindings);
                var retainedSourceTextures =
                    expectedGeneratedSourceTextures
                        .Union(FindExternallyRetainedSourceTextures(
                            runtimeRelevantTextureConsumers,
                            group,
                            originalSourceTextures))
                        .ToHashSet();

                preparedGroups.Add(new AutoAtlasTextureMergePlanner.PreparedGroup
                {
                    Id = preparedGroups.Count,
                    Materials = group,
                    Renderers = atlasRenderers,
                    SourceTextures = texturesToReplace,
                    OriginalSourceTextures = originalSourceTextures,
                    RetainedSourceTextures = retainedSourceTextures,
                    ExpectedGeneratedSourceTextures =
                        expectedGeneratedSourceTextures,
                    OriginalPixels = texturesToReplace.Sum(texture =>
                        (long)texture.width * texture.height),
                    ExcludedFromTextureMerging = IsExcludedFromTextureMerging(
                        group, atlasRenderers, mergingExcludedMaterials,
                        mergingExcludedRenderers)
                        || (configuration.ExcludeVisibilityAnimatedItemsFromTextureMerging
                            && atlasRenderers.Any(renderer =>
                                visibilityStates != null
                                && visibilityStates.TryGetValue(
                                    renderer, out var state)
                                && state.CanToggleVisibility)),
                });
            }

            var mergePlan = AutoAtlasTextureMergePlanner.Plan(
                domain,
                configuration,
                settings,
                sourceOptimization.OriginalTextureUses,
                propertyTextures,
                sourceOptimization.PrimaryDensitySizes,
                preparedGroups);

            var executionGroups = CreateExecutionGroups(
                preparedGroups, mergePlan.Selections);

            foreach (var group in MakeTextureConnectedGroups(
                         optimizationOnlyMaterials,
                         material => sourceOptimization
                             .CollectOriginalTextures(new[] { material })))
            {
                var originalSources =
                    sourceOptimization.CollectOriginalTextures(group);
                var removedBindings =
                    sourceOptimization.RemovedBindings(group);
                var expectedGeneratedSources =
                    FindTargetRetainedSourceTextures(
                        group,
                        propertyTextures,
                        originalSources,
                        removedBindings);
                var retainedSources = expectedGeneratedSources
                    .Union(FindExternallyRetainedSourceTextures(
                        runtimeRelevantTextureConsumers,
                        group,
                        originalSources))
                    .ToHashSet();

                executionGroups.Enqueue(new ExecutionGroup(
                    group,
                    ResolveAutoAtlasTargetRenderers(
                        domain, configuration, group, visibilityStates),
                    new HashSet<Texture>(),
                    originalSources,
                    retainedSources,
                    expectedGeneratedSources,
                    null,
                    null,
                    ExecutionMode.SourceOnly));
            }

            var materialBlock = "AutoAtlasTexture:info:MaterialBlock".GetLocalize();
            var sourceOptimizationBlock =
                "AutoAtlasTexture:info:SourceOptimizationMaterialBlock"
                    .GetLocalize();
            while (executionGroups.Count != 0)
            {
                var execution = executionGroups.Dequeue();
                var group = execution.Materials;

                if (execution.MergeSelection == null
                    && execution.Mode == ExecutionMode.SourceOnly)
                {
                    var sourceOnlyBlock = TryApplySourceOptimizationOnly(
                        domain,
                        engine,
                        configuration,
                        settings,
                        sourceOptimization,
                        execution,
                        sourceOptimizationBlock,
                        visibilityStates);
                    if (sourceOnlyBlock != null)
                        completed.Add(sourceOnlyBlock);
                    continue;
                }

                // Revalidate against the exact same renderer-targeting rule
                // used during planning. Do not broaden execution to every
                // domain-wide material consumer; AutoAtlas is required to
                // preserve AtlasTexture's normal target semantics.
                var atlasRenderers = ResolveAutoAtlasTargetRenderers(
                    domain, configuration, group, visibilityStates);
                var plannedRenderers = execution.Renderers
                    .Where(renderer => renderer != null)
                    .Distinct()
                    .ToHashSet();

                var hasRuntimeRelevantUntransformedConsumer =
                    HasRuntimeRelevantUntransformedMaterialConsumer(
                        domain,
                        group,
                        atlasRenderers,
                        visibilityStates);
                if (atlasRenderers.Length == 0
                    || !plannedRenderers.SetEquals(atlasRenderers)
                    || hasRuntimeRelevantUntransformedConsumer)
                {
                    if (execution.MergeSelection is { } invalidMerge)
                    {
                        var reason = hasRuntimeRelevantUntransformedConsumer
                            ? FormatLocalized(
                                "AutoAtlasTexture:fallback:RuntimeRelevantUntransformedConsumer")
                            : FormatLocalized(
                                "AutoAtlasTexture:fallback:RendererSetChanged");
                        twoDimensionalFallbackReasons.Add(
                            $"groups={string.Join(",", invalidMerge.GroupIds.Select(id => "g" + id))}: "
                            + reason);
                        EnqueueNonMergedFallback(
                            executionGroups, invalidMerge, preparedGroups);
                    }
                    else
                    {
                        EnqueueNonAtlasFallback(
                            executionGroups, execution);
                    }
                    continue;
                }

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
                        PrecomputedTextureUses = propertyTextures,
                        PrecomputedPrimaryDensitySizes =
                            sourceOptimization.PrimaryDensitySizes,
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
                            foreach (var propertySize in metrics.PropertySizes)
                            {
                                if (AtlasTexture
                                    .IsWithinAutoAtlasTextureMaximumDimension(
                                        propertySize.Value))
                                {
                                    continue;
                                }

                                if (execution.MergeSelection?.PackedRegions
                                    != null)
                                {
                                    twoDimensionalSizeRejection =
                                        FormatLocalized(
                                            "AutoAtlasTexture:fallback:MaxTextureDimensionExceeded",
                                            propertySize.Key,
                                            propertySize.Value.x,
                                            propertySize.Value.y,
                                            AtlasTexture
                                                .AutoAtlasTextureMaximumDimension);
                                }
                                return false;
                            }

                            if (execution.MergeSelection is { } mergeSelection)
                            {
                                // The planner reserves an independently valid
                                // 2D rectangle for each atomic group. Accept only
                                // if execution stays within both the predicted
                                // layout and every predicted property-resolution
                                // envelope.
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
                        EnqueueNonMergedFallback(
                            executionGroups, failedMerge, preparedGroups);
                    }
                    else
                    {
                        EnqueueNonAtlasFallback(
                            executionGroups, execution);
                    }
                    continue;
                }

                using var atlasContext = atlasResult.AtlasContext!;
                var compiledTextures = atlasResult.CompiledAtlasTextures!;

                // ReplaceMesh silently skips a renderer when no corresponding
                // generated atlas mesh can be resolved. That is only harmless
                // when the target material has no drawable geometry on that
                // renderer. Otherwise the later domain-wide material replacement
                // would pair an atlased material with unchanged UVs.
                var unresolvedRequiredRenderer =
                    atlasRenderers.FirstOrDefault(renderer =>
                        RendererRequiresAtlasMesh(
                            domain,
                            renderer,
                            group)
                        && AtlasTexture.ResolveAtlasedMeshForRenderer(
                            domain,
                            renderer,
                            atlasContext,
                            atlasResult.AtlasedMeshes!) is null);
                if (unresolvedRequiredRenderer != null)
                {
                    foreach (var rt in compiledTextures.Values.Distinct())
                        rt.Dispose();
                    DestroyUnappliedAtlasedMeshes(
                        atlasResult.AtlasedMeshes!);

                    if (execution.MergeSelection is { } unresolvedMerge)
                    {
                        if (unresolvedMerge.PackedRegions != null)
                        {
                            twoDimensionalFallbackReasons.Add(
                                $"groups={string.Join(",", unresolvedMerge.GroupIds.Select(id => "g" + id))}, "
                                + $"layout={unresolvedMerge.PlannedLayoutSize.x}×{unresolvedMerge.PlannedLayoutSize.y}: "
                                + FormatLocalized(
                                    "AutoAtlasTexture:fallback:RequiredMeshResolutionFailed",
                                    unresolvedRequiredRenderer.name));
                        }

                        EnqueueNonMergedFallback(
                            executionGroups,
                            unresolvedMerge,
                            preparedGroups);
                    }
                    else
                    {
                        EnqueueNonAtlasFallback(
                            executionGroups, execution);
                    }

                    continue;
                }

                var tuningResult = AtlasTexture.DoTextureFinTuning(
                    engine, atlasContext, settings, compiledTextures, null);
                var constantOutputs =
                    AtlasSourceOptimizer.CreateConstantOutputs(
                        engine,
                        sourceOptimization,
                        settings,
                        null,
                        group);

                var atlasOutputPixels = tuningResult.TextureDescriptors.Keys
                    .Distinct()
                    .Sum(texture => (long)texture.Width * texture.Hight);
                var retainedSourcePixels = execution.RetainedSourceTextures
                    .Sum(texture => (long)texture.width * texture.height);
                var nonAtlasBaselineTextures =
                    execution.AtomicGroup?.NonAtlasMode
                        == AutoAtlasTextureMergePlanner.NonAtlasMode.SourceOnly
                        ? execution.SourceTextures
                            .Union(execution.RetainedSourceTextures)
                            .ToHashSet()
                        : execution.OriginalSourceTextures;
                var nonAtlasBaselinePixels = nonAtlasBaselineTextures
                    .Sum(texture => (long)texture.width * texture.height);

                // Constant 4x4 outputs are deliberately excluded from adoption
                // decisions. They are tiny, common to source-optimized atlas
                // candidates, and are still included in the final memory log.
                var finalComparisonPixels =
                    atlasOutputPixels + retainedSourcePixels;
                var pixelBaseline = execution.MergeSelection?.BaselinePixels
                    ?? nonAtlasBaselinePixels;

                if (atlasOutputPixels <= 0
                    || finalComparisonPixels >= pixelBaseline)
                {
                    foreach (var rt in compiledTextures.Values
                                 .Concat(tuningResult.TextureDescriptors.Keys)
                                 .Distinct())
                    {
                        rt.Dispose();
                    }
                    AtlasSourceOptimizer.DisposeConstantOutputs(
                        constantOutputs);
                    DestroyUnappliedAtlasedMeshes(
                        atlasResult.AtlasedMeshes!);

                    if (execution.MergeSelection is { } pixelCheckedMerge)
                    {
                        if (pixelCheckedMerge.PackedRegions != null)
                        {
                            twoDimensionalFallbackReasons.Add(
                                $"groups={string.Join(",", pixelCheckedMerge.GroupIds.Select(id => "g" + id))}, "
                                + $"layout={pixelCheckedMerge.PlannedLayoutSize.x}×{pixelCheckedMerge.PlannedLayoutSize.y}: "
                                + FormatLocalized(
                                    "AutoAtlasTexture:fallback:FinalPixelsNotReduced",
                                    finalComparisonPixels,
                                    pixelCheckedMerge.BaselinePixels));
                        }

                        EnqueueNonMergedFallback(
                            executionGroups,
                            pixelCheckedMerge,
                            preparedGroups);
                    }
                    else
                    {
                        EnqueueNonAtlasFallback(
                            executionGroups, execution);
                    }

                    continue;
                }

                var hasExecutionTextureMemory =
                    AtlasTexture.TryCalculateFinalTextureMemory(
                        atlasContext,
                        tuningResult,
                        out var executionTextureMemoryBytes);
                var hasConstantTextureMemory =
                    AtlasTexture.TryCalculateConstantTextureMemory(
                        constantOutputs,
                        out var constantTextureMemoryBytes);
                var hasRetainedSourceMemory =
                    AtlasTexture.TryCalculateSourceTextureMemory(
                        domain,
                        execution.RetainedSourceTextures,
                        out var retainedSourceMemoryBytes);
                var hasSourceTextureMemory =
                    AtlasTexture.TryCalculateSourceTextureMemory(
                        domain,
                        execution.OriginalSourceTextures,
                        out var sourceTextureMemoryBytes);
                long nonAtlasBaselineTextureMemoryBytes = 0;
                var hasNonAtlasBaselineMemory =
                    execution.MergeSelection != null
                    || AtlasTexture.TryCalculateSourceTextureMemory(
                        domain,
                        nonAtlasBaselineTextures,
                        out nonAtlasBaselineTextureMemoryBytes);

                var finalComparisonTextureMemoryBytes =
                    executionTextureMemoryBytes
                    + retainedSourceMemoryBytes;
                var memoryBaseline =
                    execution.MergeSelection?.BaselineBytes
                    ?? nonAtlasBaselineTextureMemoryBytes;
                var finalTotalTextureMemoryBytes =
                    executionTextureMemoryBytes
                    + constantTextureMemoryBytes
                    + retainedSourceMemoryBytes;

                if (!hasExecutionTextureMemory
                    || !hasRetainedSourceMemory
                    || !hasNonAtlasBaselineMemory
                    || finalComparisonTextureMemoryBytes >= memoryBaseline)
                {
                    foreach (var rt in compiledTextures.Values
                                 .Concat(tuningResult.TextureDescriptors.Keys)
                                 .Distinct())
                    {
                        rt.Dispose();
                    }
                    AtlasSourceOptimizer.DisposeConstantOutputs(
                        constantOutputs);
                    DestroyUnappliedAtlasedMeshes(
                        atlasResult.AtlasedMeshes!);

                    if (execution.MergeSelection is { } memoryCheckedMerge)
                    {
                        if (memoryCheckedMerge.PackedRegions != null)
                        {
                            var reason =
                                !hasExecutionTextureMemory
                                || !hasRetainedSourceMemory
                                || !hasNonAtlasBaselineMemory
                                    ? FormatLocalized(
                                        "AutoAtlasTexture:fallback:FinalMemoryUnknown")
                                    : FormatLocalized(
                                        "AutoAtlasTexture:fallback:FinalMemoryNotReduced",
                                        finalComparisonTextureMemoryBytes,
                                        memoryCheckedMerge.BaselineBytes);
                            twoDimensionalFallbackReasons.Add(
                                $"groups={string.Join(",", memoryCheckedMerge.GroupIds.Select(id => "g" + id))}, "
                                + $"layout={memoryCheckedMerge.PlannedLayoutSize.x}×{memoryCheckedMerge.PlannedLayoutSize.y}: {reason}");
                        }

                        EnqueueNonMergedFallback(
                            executionGroups,
                            memoryCheckedMerge,
                            preparedGroups);
                    }
                    else
                    {
                        EnqueueNonAtlasFallback(
                            executionGroups, execution);
                    }

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
                AtlasSourceOptimizer.ApplyToGeneratedMaterials(
                    atlasMaterialMaps.materialMap,
                    constantOutputs,
                    sourceOptimization.FeatureDisables,
                    engine,
                    settings.ForceSetTexture);

                var generatedAtlasMaterials =
                    atlasMaterialMaps.materialMap.Values
                        .Concat(
                            atlasMaterialMaps
                                .domainsMaterial2ReplaceMaterial.Values)
                        .Distinct()
                        .ToArray();

                // External runtime-relevant consumers affect cost only. They must not
                // authorize a generated target material to retain an original
                // source. Only target-local bindings intentionally outside the
                // rewrite are valid generated-material source references.
                if (HasUnexpectedGeneratedSourceReference(
                        generatedAtlasMaterials,
                        execution.OriginalSourceTextures,
                        execution.ExpectedGeneratedSourceTextures))
                {
                    DestroyUnappliedAtlasedMaterials(
                        generatedAtlasMaterials);
                    foreach (var rt in compiledTextures.Values
                                 .Concat(tuningResult.TextureDescriptors.Keys)
                                 .Distinct())
                    {
                        rt.Dispose();
                    }
                    AtlasSourceOptimizer.DisposeConstantOutputs(
                        constantOutputs);
                    DestroyUnappliedAtlasedMeshes(
                        atlasResult.AtlasedMeshes!);

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

                        EnqueueNonMergedFallback(
                            executionGroups,
                            retainedSourceMerge,
                            preparedGroups);
                    }
                    else
                    {
                        EnqueueNonAtlasFallback(
                            executionGroups, execution);
                    }

                    continue;
                }

                var resolutionRoutes = AtlasResolutionRouteReport
                    .CollectWithOptimizations(
                        atlasContext,
                        tuningResult,
                        sourceOptimization,
                        constantOutputs,
                        group);
                var block = string.Format(materialBlock,
                    string.Join(", ", group.Select(OriginalMaterialName)
                        .Distinct().OrderBy(name => name, StringComparer.Ordinal)),
                    executionMetrics.TopFreeFraction,
                    TTTLog.FormatLocalizedLines(
                        AtlasResolutionRouteReport.CreateLocalizedLogLines(
                            resolutionRoutes)));

                if (hasSourceTextureMemory
                    && hasConstantTextureMemory
                    && hasExecutionTextureMemory
                    && hasRetainedSourceMemory)
                {
                    block += "\n" + string.Format(
                        "AtlasTexture:info:TargetTextureMemory".GetLocalize(),
                        AtlasTexture.FormatTextureMemory(sourceTextureMemoryBytes),
                        AtlasTexture.FormatTextureMemory(
                            finalTotalTextureMemoryBytes));
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
                foreach (var output in constantOutputs)
                    domain.RegisterPostProcessingAndLazyGPUReadBack(
                        output.RenderTexture,
                        output.Descriptor);

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

        internal static bool RendererRequiresAtlasMesh(
            IRendererTargeting targeting,
            Renderer renderer,
            HashSet<Material> targetMaterials)
        {
            var mesh = targeting.GetMesh(renderer);
            if (mesh == null || mesh.subMeshCount == 0)
                return false;

            var materials = targeting.GetMaterials(renderer);
            for (var slot = 0; slot < materials.Length; slot += 1)
            {
                var material = materials[slot];
                if (material == null
                    || !targetMaterials.Contains(material))
                {
                    continue;
                }

                var effectiveSubMesh = Math.Clamp(
                    slot, 0, mesh.subMeshCount - 1);
                if (mesh.GetSubMesh(effectiveSubMesh).indexCount != 0)
                    return true;
            }

            return false;
        }

        internal static HashSet<Texture>
            CollectGeneratedMaterialSourceReferences(
                IEnumerable<Material> materials,
                HashSet<Texture> sourceTextures)
        {
            return materials
                .Where(material => material != null)
                .SelectMany(material =>
                    material.EnumerateReferencedTextures())
                .Where(sourceTextures.Contains)
                .ToHashSet();
        }

        internal static bool RetainsAnySourceTexture(
            IEnumerable<Material> materials,
            HashSet<Texture> sourceTextures)
        {
            return CollectGeneratedMaterialSourceReferences(
                materials, sourceTextures).Count != 0;
        }

        internal static bool HasUnexpectedGeneratedSourceReference(
            IEnumerable<Material> generatedMaterials,
            HashSet<Texture> originalSources,
            HashSet<Texture> expectedSources)
        {
            return CollectGeneratedMaterialSourceReferences(
                    generatedMaterials,
                    originalSources)
                .Any(texture => !expectedSources.Contains(texture));
        }

        internal static void DestroyUnappliedAtlasedMaterials(
            IEnumerable<Material> materials)
        {
            foreach (var material in materials
                         .Where(material => material != null)
                         .Distinct())
            {
                UnityEngine.Object.DestroyImmediate(material);
            }
        }

        internal static bool HasRuntimeRelevantUntransformedMaterialConsumer(
            IRendererTargeting domain,
            IReadOnlyCollection<Material> materials,
            IReadOnlyCollection<Renderer> transformedRenderers,
            IReadOnlyDictionary<
                Renderer, AtlasRendererVisibilityState>? visibilityStates = null)
        {
            var transformed = transformedRenderers.ToHashSet();

            foreach (var renderer in domain.EnumerateRenderer())
            {
                if (renderer == null || renderer.tag == "EditorOnly")
                    continue;

                if (!IsRuntimeRelevant(
                        domain, renderer, visibilityStates))
                {
                    continue;
                }

                if (!domain.GetMaterials(renderer)
                        .UOfType<Material>()
                        .Any(materials.Contains))
                {
                    continue;
                }

                if (!transformed.Contains(renderer))
                    return true;
            }

            return false;
        }

        private static bool IsRuntimeRelevant(
            IRendererTargeting domain,
            Renderer renderer,
            IReadOnlyDictionary<
                Renderer, AtlasRendererVisibilityState>? visibilityStates)
        {
            if (domain.IsEnable(renderer)
                && domain.IsActive(renderer.gameObject))
                return true;

            return visibilityStates != null
                && visibilityStates.TryGetValue(renderer, out var state)
                && state.CanBecomeVisible;
        }

        internal static bool HasStrictMergePixelBenefit(
            long finalOutputPixels,
            long standaloneBaselinePixels)
        {
            return finalOutputPixels > 0
                && finalOutputPixels < standaloneBaselinePixels;
        }

        internal static void DestroyUnappliedAtlasedMeshes(
            IEnumerable<Mesh> meshes)
        {
            foreach (var mesh in meshes
                         .Where(mesh => mesh != null)
                         .Distinct())
            {
                UnityEngine.Object.DestroyImmediate(mesh);
            }
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

        internal sealed class RuntimeRelevantTextureConsumerSet
        {
            internal readonly HashSet<Material> Materials;

            internal RuntimeRelevantTextureConsumerSet(HashSet<Material> materials)
            {
                Materials = materials;
            }
        }

        internal static RuntimeRelevantTextureConsumerSet CollectRuntimeRelevantTextureConsumers(
            IRendererTargeting domain,
            IReadOnlyDictionary<
                Renderer, AtlasRendererVisibilityState>? visibilityStates = null)
        {
            var materials = new HashSet<Material>();

            foreach (var renderer in domain.EnumerateRenderer())
            {
                if (renderer == null
                    || renderer.tag == "EditorOnly"
                    || !IsRuntimeRelevant(
                        domain, renderer, visibilityStates))
                {
                    continue;
                }

                var rendererMaterials = domain.GetMaterials(renderer);
                var mesh = domain.GetMesh(renderer);
                if (mesh == null)
                {
                    // Particle/line and other non-mesh renderers can still be
                    // real runtime-relevant texture consumers even though AutoAtlas
                    // itself cannot rewrite their UVs.
                    materials.UnionWith(
                        rendererMaterials.UOfType<Material>());
                    continue;
                }
                if (mesh.subMeshCount == 0)
                    continue;

                for (var slot = 0;
                     slot < rendererMaterials.Length;
                     slot += 1)
                {
                    var material = rendererMaterials[slot];
                    if (material == null)
                        continue;

                    var subMesh = Math.Clamp(
                        slot, 0, mesh.subMeshCount - 1);
                    if (mesh.GetSubMesh(subMesh).indexCount == 0)
                        continue;

                    materials.Add(material);
                }
            }

            return new RuntimeRelevantTextureConsumerSet(materials);
        }

        internal static HashSet<Texture> FindTargetRetainedSourceTextures(
            HashSet<Material> group,
            IReadOnlyDictionary<
                Material,
                IReadOnlyDictionary<string, Texture>> propertyTextures,
            HashSet<Texture> replacedTextures,
            HashSet<(Material material, string property)>?
                removedBindings = null)
        {
            var retained = new HashSet<Texture>();
            foreach (var material in group)
            {
                propertyTextures.TryGetValue(
                    material, out var atlasProperties);

                foreach (var property in material.GetTextureReferences())
                {
                    if (!replacedTextures.Contains(property.Value))
                        continue;
                    if (removedBindings?.Contains(
                            (material, property.Key)) == true)
                        continue;

                    if (atlasProperties == null
                        || !atlasProperties.TryGetValue(
                            property.Key, out var atlasTexture)
                        || atlasTexture != property.Value)
                    {
                        retained.Add(property.Value);
                    }
                }
            }

            return retained;
        }

        internal static HashSet<Texture> FindExternallyRetainedSourceTextures(
            RuntimeRelevantTextureConsumerSet runtimeRelevantConsumers,
            HashSet<Material> group,
            HashSet<Texture> replacedTextures)
        {
            var retained = new HashSet<Texture>();
            foreach (var material in runtimeRelevantConsumers.Materials)
            {
                if (group.Contains(material))
                    continue;

                foreach (var property in material.GetTextureReferences())
                {
                    if (replacedTextures.Contains(property.Value))
                        retained.Add(property.Value);
                }
            }

            return retained;
        }

        private static string? TryApplySourceOptimizationOnly(
            IDomain domain,
            ITexTransToolForUnity engine,
            AutoAtlasTexture configuration,
            AtlasSetting settings,
            AtlasSourceOptimizationPlan sourceOptimization,
            ExecutionGroup execution,
            string materialBlock,
            IReadOnlyDictionary<
                Renderer, AtlasRendererVisibilityState>? visibilityStates)
        {
            var changedMaterials = sourceOptimization.ConstantTextures
                .Select(item => item.Material)
                .Concat(sourceOptimization.FeatureDisables.Select(
                    item => item.Material))
                .ToHashSet();
            var targets = execution.Materials
                .Where(changedMaterials.Contains)
                .ToHashSet();
            if (targets.Count == 0)
                return null;

            var targetRenderers = ResolveAutoAtlasTargetRenderers(
                domain, configuration, targets, visibilityStates);
            if (targetRenderers.Length == 0
                || HasRuntimeRelevantUntransformedMaterialConsumer(
                    domain, targets, targetRenderers, visibilityStates))
            {
                return null;
            }

            var originalSources = execution.OriginalSourceTextures;
            var remainingSources = execution.SourceTextures
                .Union(execution.RetainedSourceTextures)
                .ToHashSet();
            if (originalSources.Count == 0)
                return null;

            var originalPixels = originalSources.Sum(texture =>
                (long)texture.width * texture.height);
            var remainingPixels = remainingSources.Sum(texture =>
                (long)texture.width * texture.height);
            var hasSourceMemory =
                AtlasTexture.TryCalculateSourceTextureMemory(
                    domain,
                    originalSources,
                    out var sourceBytes);
            var hasRemainingMemory =
                AtlasTexture.TryCalculateSourceTextureMemory(
                    domain,
                    remainingSources,
                    out var remainingBytes);

            // Constant 4x4 outputs are intentionally ignored here. Source
            // optimization is adopted only when the source set itself strictly
            // reduces both pixel count and resident memory.
            if (originalPixels <= 0
                || remainingPixels >= originalPixels
                || !hasSourceMemory
                || !hasRemainingMemory
                || remainingBytes >= sourceBytes)
            {
                return null;
            }

            var constantOutputs =
                AtlasSourceOptimizer.CreateConstantOutputs(
                    engine,
                    sourceOptimization,
                    settings,
                    null,
                    targets);
            var optimizedMaps =
                AtlasTexture.GenerateAtlasedMaterialMaps(
                    domain,
                    targets,
                    settings,
                    (new List<AtlasTexture.MaterialMergeGroup>(),
                        null,
                        null),
                    new Dictionary<string, RenderTexture>(),
                    new HashSet<Material>(),
                    null);
            AtlasSourceOptimizer.ApplyToGeneratedMaterials(
                optimizedMaps.materialMap,
                constantOutputs,
                sourceOptimization.FeatureDisables,
                engine,
                settings.ForceSetTexture);

            var generatedMaterials =
                optimizedMaps.materialMap.Values
                    .Concat(
                        optimizedMaps
                            .domainsMaterial2ReplaceMaterial.Values)
                    .Distinct()
                    .ToArray();
            var expectedGeneratedSources =
                execution.SourceTextures
                    .Union(execution.ExpectedGeneratedSourceTextures)
                    .ToHashSet();
            if (HasUnexpectedGeneratedSourceReference(
                    generatedMaterials,
                    originalSources,
                    expectedGeneratedSources))
            {
                DestroyUnappliedAtlasedMaterials(generatedMaterials);
                AtlasSourceOptimizer.DisposeConstantOutputs(
                    constantOutputs);
                return null;
            }

            var routes =
                AtlasResolutionRouteReport.CollectSourceOptimizationOnly(
                    sourceOptimization,
                    constantOutputs,
                    targets);
            var block = string.Format(
                materialBlock,
                string.Join(
                    ", ",
                    targets.Select(OriginalMaterialName)
                        .Distinct()
                        .OrderBy(name => name, StringComparer.Ordinal)),
                TTTLog.FormatLocalizedLines(
                    AtlasResolutionRouteReport
                        .CreateLocalizedLogLines(routes)));

            if (AtlasTexture.TryCalculateConstantTextureMemory(
                    constantOutputs,
                    out var constantBytes))
            {
                block += "\n" + string.Format(
                    "AtlasTexture:info:TargetTextureMemory".GetLocalize(),
                    AtlasTexture.FormatTextureMemory(sourceBytes),
                    AtlasTexture.FormatTextureMemory(
                        remainingBytes + constantBytes));
            }

            AtlasTexture.ApplyAtlasedMaterialMaps(
                domain,
                optimizedMaps.materialMap,
                optimizedMaps.domainsMaterial2ReplaceMaterial);

            foreach (var output in constantOutputs)
                domain.RegisterPostProcessingAndLazyGPUReadBack(
                    output.RenderTexture,
                    output.Descriptor);

            return block;
        }

        private static List<HashSet<Material>> MakeTextureConnectedGroups(
            HashSet<Material> materials,
            IReadOnlyDictionary<Material, IReadOnlyDictionary<string, Texture>>
                properties)
        {
            return MakeTextureConnectedGroups(
                materials,
                material => properties.TryGetValue(material, out var textures)
                    ? textures.Values.Where(texture => texture != null)
                    : Enumerable.Empty<Texture>());
        }

        private static List<HashSet<Material>> MakeTextureConnectedGroups(
            HashSet<Material> materials,
            Func<Material, IEnumerable<Texture>> textureSelector)
        {
            var texturesByMaterial = materials.ToDictionary(
                material => material,
                material => textureSelector(material)
                    .Where(texture => texture != null)
                    .Distinct()
                    .ToArray());
            var materialsByTexture = new Dictionary<Texture, HashSet<Material>>();

            foreach (var pair in texturesByMaterial)
            {
                foreach (var texture in pair.Value)
                {
                    if (!materialsByTexture.TryGetValue(
                            texture, out var consumers))
                    {
                        materialsByTexture[texture] =
                            consumers = new HashSet<Material>();
                    }
                    consumers.Add(pair.Key);
                }
            }

            var remaining = new HashSet<Material>(
                materials.Where(material =>
                    texturesByMaterial[material].Length != 0));
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
                    foreach (var texture in texturesByMaterial[current])
                    {
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
