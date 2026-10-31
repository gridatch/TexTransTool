#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using net.rs64.TexTransCore;
using net.rs64.TexTransCoreEngineForUnity;
using net.rs64.TexTransTool.TextureAtlas.AAOCode;
using net.rs64.TexTransTool.TextureAtlas.FineTuning;
using net.rs64.TexTransTool.Utils;
using UnityEngine;
using UnityEngine.Rendering;
using Color = UnityEngine.Color;

namespace net.rs64.TexTransTool.TextureAtlas
{
    internal enum AtlasSourceProofScope
    {
        UsedRegion,
        WholeTexture,
    }

    internal enum AtlasSourceConstantProofState
    {
        Unknown,
        Rejected,
        Accepted,
    }

    internal readonly struct AtlasConstantTextureDecision
    {
        internal readonly Material Material;
        internal readonly string Property;
        internal readonly Texture Source;
        internal readonly Color Color;
        internal readonly AtlasSourceProofScope ProofScope;
        internal readonly string[] BoundProperties;

        internal AtlasConstantTextureDecision(
            Material material, string property, Texture source, Color color)
            : this(
                material,
                property,
                source,
                color,
                AtlasSourceProofScope.UsedRegion)
        {
        }

        internal AtlasConstantTextureDecision(
            Material material,
            string property,
            Texture source,
            Color color,
            AtlasSourceProofScope proofScope)
            : this(
                material,
                property,
                source,
                color,
                proofScope,
                new[] { property })
        {
        }

        private AtlasConstantTextureDecision(
            Material material,
            string property,
            Texture source,
            Color color,
            AtlasSourceProofScope proofScope,
            IEnumerable<string> boundProperties)
        {
            Material = material;
            Property = property;
            Source = source;
            Color = color;
            ProofScope = proofScope;
            BoundProperties = boundProperties
                .Append(property)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(item => item, StringComparer.Ordinal)
                .ToArray();
        }

        internal AtlasConstantTextureDecision WithBoundProperties(
            IEnumerable<string> boundProperties)
        {
            return new AtlasConstantTextureDecision(
                Material,
                Property,
                Source,
                Color,
                ProofScope,
                boundProperties);
        }
    }

    internal sealed class AtlasFeatureDisableRule
    {
        internal readonly string SwitchProperty;
        internal readonly string[] TextureProperties;

        internal AtlasFeatureDisableRule(
            string switchProperty, params string[] textureProperties)
        {
            SwitchProperty = switchProperty;
            TextureProperties = textureProperties;
        }
    }

    internal readonly struct AtlasFeatureDisableDecision
    {
        internal readonly Material Material;
        internal readonly AtlasFeatureDisableRule Rule;
        internal readonly string[] SafetyProperties;

        internal AtlasFeatureDisableDecision(
            Material material,
            AtlasFeatureDisableRule rule,
            params string[] safetyProperties)
        {
            Material = material;
            Rule = rule;
            SafetyProperties = safetyProperties
                .Append(rule.SwitchProperty)
                .Where(property => !string.IsNullOrWhiteSpace(property))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }
    }

    internal interface IAtlasSourceOptimizationAnimationSafety
        : IDomainCustomContextData
    {
        bool HasUnsafeConstantTextureAnimation(
            IRendererTargeting targeting,
            Material material,
            string property,
            Texture source,
            AtlasSourceProofScope proofScope,
            IEnumerable<Renderer> renderers);

        bool HasUnsafeFeatureDisableAnimation(
            IRendererTargeting targeting,
            AtlasFeatureDisableDecision decision,
            IEnumerable<Renderer> renderers);
    }

    internal sealed class AtlasSourceOptimizationPlan
    {
        internal readonly IReadOnlyDictionary<
            Material, IReadOnlyDictionary<string, Texture>> OriginalTextureUses;
        internal readonly IReadOnlyDictionary<
            Material, IReadOnlyDictionary<string, Texture>> ProjectedTextureUses;
        internal readonly AtlasConstantTextureDecision[] ConstantTextures;
        internal readonly AtlasFeatureDisableDecision[] FeatureDisables;
        internal readonly IReadOnlyDictionary<Material, Vector2Int?>
            PrimaryDensitySizes;

        internal AtlasSourceOptimizationPlan(
            IReadOnlyDictionary<Material, IReadOnlyDictionary<string, Texture>>
                originalTextureUses,
            IReadOnlyDictionary<Material, IReadOnlyDictionary<string, Texture>>
                projectedTextureUses,
            IEnumerable<AtlasConstantTextureDecision> constantTextures,
            IEnumerable<AtlasFeatureDisableDecision> featureDisables,
            IReadOnlyDictionary<Material, Vector2Int?>? primaryDensitySizes =
                null)
        {
            OriginalTextureUses = originalTextureUses;
            ProjectedTextureUses = projectedTextureUses;
            ConstantTextures = constantTextures.ToArray();
            FeatureDisables = featureDisables.ToArray();
            PrimaryDensitySizes = primaryDensitySizes
                ?? new Dictionary<Material, Vector2Int?>();
        }

        internal bool HasChanges =>
            ConstantTextures.Length != 0 || FeatureDisables.Length != 0;

        internal HashSet<Material> GetAtlasMaterials(
            IEnumerable<Material> materials)
        {
            var selected = materials.ToHashSet();
            selected.ExceptWith(GetOptimizationOnlyMaterials(selected));
            return selected;
        }

        internal HashSet<Material> GetOptimizationOnlyMaterials(
            IEnumerable<Material> materials)
        {
            var changed = ConstantTextures.Select(item => item.Material)
                .Concat(FeatureDisables.Select(item => item.Material))
                .ToHashSet();
            return materials
                .Where(changed.Contains)
                .Where(material =>
                    !ProjectedTextureUses.TryGetValue(material, out var textures)
                    || textures.Count == 0)
                .ToHashSet();
        }

        internal HashSet<Texture> CollectOriginalTextures(
            IEnumerable<Material> materials)
        {
            var selected = materials.ToHashSet();
            var result = selected
                .Where(OriginalTextureUses.ContainsKey)
                .SelectMany(material => OriginalTextureUses[material].Values)
                .Where(texture => texture != null)
                .ToHashSet();

            // Feature removal can also release textures that were never atlas
            // inputs themselves (for example a non-mesh-UV MatCap texture
            // whose UV-space blend mask proves the whole feature inactive).
            foreach (var item in FeatureDisables)
            {
                if (!selected.Contains(item.Material)) continue;
                foreach (var property in item.Rule.TextureProperties)
                {
                    if (!item.Material.HasShaderTextureProperty(property))
                        continue;
                    var texture = item.Material.GetTexture(property);
                    if (texture is Texture2D || texture is RenderTexture)
                        result.Add(texture);
                }
            }

            return result;
        }

        internal HashSet<(Material material, string property)>
            RemovedBindings(IEnumerable<Material> materials)
        {
            var selected = materials.ToHashSet();
            var result = ConstantTextures
                .Where(item => selected.Contains(item.Material))
                .SelectMany(item => item.BoundProperties.Select(
                    property => (item.Material, property)))
                .ToHashSet();
            foreach (var item in FeatureDisables)
            {
                if (!selected.Contains(item.Material)) continue;
                foreach (var property in item.Rule.TextureProperties)
                    result.Add((item.Material, property));
            }
            return result;
        }

    }

    internal readonly struct AtlasConstantTextureOutput
    {
        internal readonly AtlasConstantTextureDecision Decision;
        internal readonly ITTRenderTexture RenderTexture;
        internal readonly TexTransToolTextureDescriptor Descriptor;
        internal readonly string[] BoundProperties;

        internal AtlasConstantTextureOutput(
            AtlasConstantTextureDecision decision,
            ITTRenderTexture renderTexture,
            TexTransToolTextureDescriptor descriptor,
            IEnumerable<string> boundProperties)
        {
            Decision = decision;
            RenderTexture = renderTexture;
            Descriptor = descriptor;
            BoundProperties = boundProperties
                .Distinct(StringComparer.Ordinal)
                .OrderBy(property => property, StringComparer.Ordinal)
                .ToArray();
        }
    }

    internal static class AtlasSourceOptimizer
    {
        private const long MaxSourcePixels = 4096L * 4096L;
        private const long MaxGpuBatchSourcePixels = 32L * 1024L * 1024L;

        private readonly struct SampleResult
        {
            internal readonly bool Known;
            internal readonly bool RgbZero;
            internal readonly bool AlphaZero;

            internal SampleResult(
                bool known,
                bool rgbZero,
                bool alphaZero)
            {
                Known = known;
                RgbZero = rgbZero;
                AlphaZero = alphaZero;
            }
        }

        private readonly struct Candidate
        {
            internal readonly Material Material;
            internal readonly string Property;
            internal readonly Texture Texture;
            internal readonly Matrix2x3? Matrix;
            internal readonly HashSet<(Mesh mesh, int subMesh)> Slots;

            internal Candidate(
                Material material, string property, Texture texture,
                Matrix2x3? matrix, HashSet<(Mesh mesh, int subMesh)> slots)
            {
                Material = material;
                Property = property;
                Texture = texture;
                Matrix = matrix;
                Slots = slots;
            }
        }

        private readonly struct ConstantProofResult
        {
            internal readonly AtlasSourceConstantProofState State;
            internal readonly AtlasSourceProofScope Scope;
            internal readonly Color Color;

            internal ConstantProofResult(
                AtlasSourceConstantProofState state,
                AtlasSourceProofScope scope,
                Color color)
            {
                State = state;
                Scope = scope;
                Color = color;
            }
        }

        internal static AtlasSourceOptimizationPlan Analyze(
            IDomain domain,
            Renderer[] targetRenderers,
            HashSet<Material> targetMaterials,
            UVChannel atlasTargetUvChannel,
            AtlasSetting atlasSetting,
            AtlasTextureExperimentalFeature? experimentalOptions = null,
            IReadOnlyCollection<Material>? excludedMaterials = null)
        {
            var original =
                MaterialGroupingContext.BuildContainsTextureDictionaries(
                    targetMaterials, atlasTargetUvChannel);

            if (!atlasSetting.OptimizeConstantTextures
                && !atlasSetting.DisableVisuallyUnusedShaderFeatures)
            {
                return new AtlasSourceOptimizationPlan(
                    original,
                    original,
                    Array.Empty<AtlasConstantTextureDecision>(),
                    Array.Empty<AtlasFeatureDisableDecision>());
            }

            var excluded = excludedMaterials?.ToHashSet()
                ?? new HashSet<Material>();
            var slots = BuildMaterialSlots(
                domain, targetRenderers, targetMaterials);
            var candidates = new List<Candidate>();

            foreach (var material in targetMaterials)
            {
                if (excluded.Contains(material)
                    || !original.TryGetValue(material, out var properties)
                    || !slots.TryGetValue(material, out var materialSlots)
                    || materialSlots.Count == 0)
                    continue;

                IReadOnlyDictionary<string, TextureUVUsage> usages;
                try
                {
                    usages = TTShaderTextureUsageInformationUtil
                        .GetContainsUVTransformUsage(material);
                }
                catch
                {
                    continue;
                }

                foreach (var pair in properties)
                {
                    if (pair.Value == null
                        || pair.Value.dimension != TextureDimension.Tex2D
                        || !usages.TryGetValue(pair.Key, out var usage)
                        || ((int)usage.UVChannel - 1)
                            != (int)atlasTargetUvChannel)
                        continue;

                    candidates.Add(new Candidate(
                        material,
                        pair.Key,
                        pair.Value,
                        usage.UVMatrix,
                        materialSlots));
                }
            }

            var samples =
                new Dictionary<(Material material, string property), SampleResult>();
            var constantProofs = candidates.ToDictionary(
                candidate =>
                    (candidate.Material, candidate.Property),
                _ => new ConstantProofResult(
                    AtlasSourceConstantProofState.Unknown,
                    AtlasSourceProofScope.UsedRegion,
                    default));
            var engine = domain.GetTexTransCoreEngineForUnity();
            var analyzer = CanUseUnityGpuSourceAnalyzer(engine)
                ? AtlasSourceGpuAnalyzer.TryCreate()
                : null;
            if (analyzer != null)
            {
                var geometryCache = new Dictionary<
                    (Material material,
                     int width,
                     int height,
                     float m00,
                     float m01,
                     float m02,
                     float m10,
                     float m11,
                     float m12),
                    AtlasSourceGpuGeometry>();
                var wholeGeometryCache =
                    new Dictionary<(int width, int height),
                        AtlasSourceGpuGeometry>();

                void SetConstantProof(
                    IEnumerable<Candidate> targetCandidates,
                    AtlasSourceConstantProofState state,
                    AtlasSourceProofScope scope,
                    Color color = default)
                {
                    foreach (var candidate in targetCandidates)
                    {
                        constantProofs[
                            (candidate.Material, candidate.Property)] =
                            new ConstantProofResult(state, scope, color);
                    }
                }

                void ProcessPendingBatch(
                    List<(Candidate[] candidates,
                        AtlasSourceGpuPending pending)> pending,
                    AtlasSourceProofScope scope,
                    bool recordZeroProof)
                {
                    var constantRequests =
                        new List<(Candidate[] candidates,
                            AtlasSourceGpuFlags flags,
                            AsyncGPUReadbackRequest request)>();
                    var tolerantRequests =
                        new List<(Candidate[] candidates,
                            AtlasSourceGpuFlags flags,
                            AtlasSourceGpuPending sourcePending,
                            AsyncGPUReadbackRequest referenceRequest,
                            AtlasSourceGpuProbePending probe)>();

                    foreach (var item in pending)
                    {
                        if (!item.pending.TryGetFlags(out var flags)
                            || !flags.Known)
                            continue;

                        if (recordZeroProof)
                        {
                            foreach (var candidate in item.candidates)
                            {
                                samples[(
                                    candidate.Material,
                                    candidate.Property)] =
                                    new SampleResult(
                                        true,
                                        flags.RgbZero,
                                        flags.AlphaZero);
                            }
                        }

                        if (!atlasSetting.OptimizeConstantTextures)
                            continue;

                        if (flags.IsConstant)
                        {
                            try
                            {
                                constantRequests.Add((
                                    item.candidates,
                                    flags,
                                    item.pending.RequestReferenceColor()));
                            }
                            catch
                            {
                                // Constant proof remains unknown and may enter
                                // whole-texture fallback when this is a
                                // used-region proof.
                            }
                            continue;
                        }

                        if (atlasSetting.ConstantTextureColorTolerance <= 0
                            || !flags.ApproximateConstantCandidate)
                        {
                            SetConstantProof(
                                item.candidates,
                                AtlasSourceConstantProofState.Rejected,
                                scope);
                            continue;
                        }

                        AtlasSourceGpuProbePending? probe = null;
                        try
                        {
                            if (!analyzer.TryQueueRepresentativeProbe(
                                    item.pending.Source,
                                    item.pending.Geometry,
                                    out probe)
                                || probe == null)
                                continue;

                            tolerantRequests.Add((
                                item.candidates,
                                flags,
                                item.pending,
                                item.pending.RequestReferenceColor(),
                                probe));
                            probe = null;
                        }
                        catch
                        {
                            // Constant proof remains unknown.
                        }
                        finally
                        {
                            probe?.Dispose();
                        }
                    }

                    foreach (var item in constantRequests)
                    {
                        try
                        {
                            var request = item.request;
                            request.WaitForCompletion();
                            if (request.hasError)
                                continue;

                            var data = request.GetData<Color>();
                            if (data.Length != 1)
                                continue;

                            SetConstantProof(
                                item.candidates,
                                AtlasSourceConstantProofState.Accepted,
                                scope,
                                data[0]);
                        }
                        catch
                        {
                            // Constant proof remains unknown.
                        }
                    }

                    var validationRequests =
                        new List<(Candidate[] candidates,
                            Color representative,
                            AtlasSourceGpuToleranceValidationPending pending)>();
                    foreach (var item in tolerantRequests)
                    {
                        try
                        {
                            var referenceRequest =
                                item.referenceRequest;
                            referenceRequest.WaitForCompletion();
                            if (referenceRequest.hasError
                                || !item.probe.TryGetRepresentative(
                                    out var representativeRgb))
                                continue;

                            var referenceData =
                                referenceRequest.GetData<Color>();
                            if (referenceData.Length != 1)
                                continue;

                            var reference = referenceData[0];
                            var representative = new Color(
                                representativeRgb.r,
                                representativeRgb.g,
                                representativeRgb.b,
                                reference.a);

                            if (!analyzer.TryQueueToleranceValidation(
                                    item.sourcePending.Source,
                                    item.sourcePending.Geometry,
                                    representative,
                                    atlasSetting
                                        .ConstantTextureColorTolerance,
                                    out var validation)
                                || validation == null)
                                continue;

                            validationRequests.Add((
                                item.candidates,
                                representative,
                                validation));
                        }
                        catch
                        {
                            // Constant proof remains unknown.
                        }
                        finally
                        {
                            item.probe.Dispose();
                        }
                    }

                    foreach (var item in validationRequests)
                    {
                        try
                        {
                            if (!item.pending.TryGetValidation(
                                    out var validation))
                                continue;

                            SetConstantProof(
                                item.candidates,
                                validation.Accepted
                                    ? AtlasSourceConstantProofState.Accepted
                                    : AtlasSourceConstantProofState.Rejected,
                                scope,
                                validation.Accepted
                                    ? item.representative
                                    : default);
                        }
                        finally
                        {
                            item.pending.Dispose();
                        }
                    }
                }

                try
                {
                    var textureGroups = candidates
                        .GroupBy(item => item.Texture)
                        .ToArray();
                    var groupIndex = 0;

                    while (groupIndex < textureGroups.Length)
                    {
                        var loadedSources =
                            new List<ITTRenderTexture>();
                        var loadedForWhole =
                            new List<(Texture source,
                                Candidate[] candidates,
                                RenderTexture sampledSource,
                                int width,
                                int height)>();
                        var pending =
                            new List<(Candidate[] candidates,
                                AtlasSourceGpuPending pending)>();
                        long batchPixels = 0;

                        try
                        {
                            while (groupIndex < textureGroups.Length)
                            {
                                var textureGroup =
                                    textureGroups[groupIndex];
                                var source = textureGroup.Key;
                                var importedWidth = source.width;
                                var importedHeight = source.height;
                                var importedPixels =
                                    (long)importedWidth * importedHeight;

                                if (importedWidth <= 0
                                    || importedHeight <= 0
                                    || importedPixels > MaxSourcePixels)
                                {
                                    groupIndex += 1;
                                    continue;
                                }

                                groupIndex += 1;
                                ITTRenderTexture? loadedSource = null;
                                try
                                {
                                    loadedSource =
                                        engine.WrappingOrUploadToLoadFullScale(
                                            source);
                                    var sampledSource =
                                        engine.GetReferenceRenderTexture(
                                            loadedSource);
                                    var width = sampledSource.width;
                                    var height = sampledSource.height;
                                    var sourcePixels =
                                        (long)width * height;
                                    if (width <= 0
                                        || height <= 0
                                        || sourcePixels > MaxSourcePixels)
                                        continue;

                                    if (loadedSources.Count != 0
                                        && batchPixels + sourcePixels
                                            > MaxGpuBatchSourcePixels)
                                    {
                                        groupIndex -= 1;
                                        break;
                                    }

                                    var loaded = loadedSource;
                                    loadedSources.Add(loaded);
                                    loadedSource = null;
                                    batchPixels += sourcePixels;

                                    var textureCandidates =
                                        textureGroup.ToArray();
                                    loadedForWhole.Add((
                                        source,
                                        textureCandidates,
                                        sampledSource,
                                        width,
                                        height));

                                    var analysisGroups = textureCandidates
                                        .Where(candidate =>
                                            candidate.Matrix.HasValue)
                                        .GroupBy(candidate =>
                                        {
                                            var matrix =
                                                candidate.Matrix!.Value;
                                            return (
                                                candidate.Material,
                                                width,
                                                height,
                                                matrix.M00,
                                                matrix.M01,
                                                matrix.M02,
                                                matrix.M10,
                                                matrix.M11,
                                                matrix.M12);
                                        });

                                    foreach (var analysisGroup in analysisGroups)
                                    {
                                        var groupedCandidates =
                                            analysisGroup.ToArray();
                                        var representative =
                                            groupedCandidates[0];
                                        var matrix =
                                            representative.Matrix!.Value;
                                        var geometryKey = (
                                            representative.Material,
                                            width,
                                            height,
                                            matrix.M00,
                                            matrix.M01,
                                            matrix.M02,
                                            matrix.M10,
                                            matrix.M11,
                                            matrix.M12);

                                        if (!geometryCache.TryGetValue(
                                                geometryKey,
                                                out var geometry))
                                        {
                                            if (!analyzer.TryBuildGeometry(
                                                    representative.Slots,
                                                    (int)atlasTargetUvChannel,
                                                    matrix,
                                                    width,
                                                    height,
                                                    out var builtGeometry)
                                                || builtGeometry == null)
                                                continue;

                                            geometry = builtGeometry;
                                            geometryCache.Add(
                                                geometryKey,
                                                geometry);
                                        }

                                        if (!analyzer.TryQueue(
                                                sampledSource,
                                                geometry,
                                                out var queued,
                                                atlasSetting
                                                    .ConstantTextureColorTolerance)
                                            || queued == null)
                                            continue;

                                        pending.Add((
                                            groupedCandidates,
                                            queued));
                                    }
                                }
                                catch
                                {
                                    // Source optimization is optional. A source
                                    // that cannot enter the canonical GPU path is
                                    // left to the existing Atlas implementation.
                                }
                                finally
                                {
                                    loadedSource?.Dispose();
                                }
                            }

                            ProcessPendingBatch(
                                pending,
                                AtlasSourceProofScope.UsedRegion,
                                recordZeroProof: true);

                            if (atlasSetting.OptimizeConstantTextures)
                            {
                                var wholePending =
                                    new List<(Candidate[] candidates,
                                        AtlasSourceGpuPending pending)>();
                                try
                                {
                                    foreach (var loaded in loadedForWhole)
                                    {
                                        // Whole-texture proof is a fallback
                                        // only for technically unknown
                                        // used-region proofs. A successfully
                                        // rejected used region must never be
                                        // overridden by broader statistics.
                                        var unknownCandidates = loaded.candidates
                                            .Where(candidate =>
                                                constantProofs[(
                                                        candidate.Material,
                                                        candidate.Property)]
                                                    .State
                                                == AtlasSourceConstantProofState
                                                    .Unknown)
                                            .ToArray();
                                        if (unknownCandidates.Length == 0)
                                            continue;

                                        var key = (
                                            loaded.width,
                                            loaded.height);
                                        if (!wholeGeometryCache.TryGetValue(
                                                key,
                                                out var wholeGeometry))
                                        {
                                            if (!AtlasSourceGpuAnalyzer
                                                    .TryBuildWholeTextureGeometry(
                                                        loaded.width,
                                                        loaded.height,
                                                        out var builtWhole)
                                                || builtWhole == null)
                                                continue;

                                            wholeGeometry = builtWhole;
                                            wholeGeometryCache.Add(
                                                key,
                                                wholeGeometry);
                                        }

                                        if (!analyzer.TryQueue(
                                                loaded.sampledSource,
                                                wholeGeometry,
                                                out var queued,
                                                atlasSetting
                                                    .ConstantTextureColorTolerance)
                                            || queued == null)
                                            continue;

                                        wholePending.Add((
                                            unknownCandidates,
                                            queued));
                                    }

                                    ProcessPendingBatch(
                                        wholePending,
                                        AtlasSourceProofScope.WholeTexture,
                                        recordZeroProof: false);
                                }
                                finally
                                {
                                    foreach (var item in wholePending)
                                        item.pending.Dispose();
                                }
                            }
                        }
                        finally
                        {
                            foreach (var item in pending)
                                item.pending.Dispose();
                            foreach (var loadedSource in loadedSources)
                                loadedSource.Dispose();
                        }
                    }
                }
                finally
                {
                    foreach (var geometry in geometryCache.Values)
                        geometry.Dispose();
                    foreach (var geometry in wholeGeometryCache.Values)
                        geometry.Dispose();
                    analyzer.Dispose();
                }
            }

            var animationSafety =
                domain.GetCustomContext<IAtlasSourceOptimizationAnimationSafety>();

            var constants = new List<AtlasConstantTextureDecision>();
            if (atlasSetting.OptimizeConstantTextures)
            {
                foreach (var candidate in candidates)
                {
                    var proof = constantProofs[
                        (candidate.Material, candidate.Property)];
                    if (proof.State
                        != AtlasSourceConstantProofState.Accepted)
                        continue;

                    if ((long)candidate.Texture.width
                            * candidate.Texture.height <= 16)
                        continue;

                    if (animationSafety?.HasUnsafeConstantTextureAnimation(
                            domain,
                            candidate.Material,
                            candidate.Property,
                            candidate.Texture,
                            proof.Scope,
                            targetRenderers) == true)
                        continue;

                    constants.Add(new AtlasConstantTextureDecision(
                        candidate.Material,
                        candidate.Property,
                        candidate.Texture,
                        proof.Color,
                        proof.Scope));
                }
            }

            var featureDisables =
                atlasSetting.DisableVisuallyUnusedShaderFeatures
                ? DetectFeatureDisables(
                        targetMaterials.Where(material =>
                            !excluded.Contains(material)),
                        samples)
                    .Where(decision =>
                        animationSafety?.HasUnsafeFeatureDisableAnimation(
                            domain,
                            decision,
                            targetRenderers) != true)
                    .ToList()
                : new List<AtlasFeatureDisableDecision>();

            FilterUnsetTextureConflicts(
                domain,
                constants,
                featureDisables,
                targetMaterials,
                experimentalOptions);

            FilterFineTuningConflicts(
                constants,
                featureDisables,
                original,
                atlasSetting,
                experimentalOptions);

            for (var i = 0; i < constants.Count; i += 1)
            {
                var decision = constants[i];
                constants[i] = decision.WithBoundProperties(
                    ResolveConstantBoundProperties(
                        decision.Property,
                        atlasSetting,
                        experimentalOptions));
            }

            var projected = BuildProjectedTextureUses(
                original,
                constants,
                featureDisables);

            var primaryDensitySizes = BuildPrimaryDensityOverrides(
                original,
                projected,
                targetMaterials,
                atlasSetting);

            // A disabled feature supersedes any constant replacement belonging
            // to that feature; no 4x4 texture needs to be generated for it.
            var disabledBindings = featureDisables
                .SelectMany(item => item.Rule.TextureProperties.Select(
                    property => (item.Material, property)))
                .ToHashSet();
            constants.RemoveAll(item =>
                disabledBindings.Contains((item.Material, item.Property)));

            return new AtlasSourceOptimizationPlan(
                original,
                projected,
                constants,
                featureDisables,
                primaryDensitySizes);
        }

        internal static string[] ResolveConstantBoundProperties(
            string property,
            AtlasSetting atlasSetting,
            AtlasTextureExperimentalFeature? experimentalOptions)
        {
            var holders = TexFineTuningUtility.InitTexFineTuningHolders(
                new[] { property });

            foreach (var fineTuning in atlasSetting.TextureFineTuning)
                fineTuning?.AddSetting(holders);
            if (experimentalOptions != null)
                AtlasTexture.IndividualTuningAddSettings(
                    holders,
                    experimentalOptions.TextureIndividualFineTuning);

            return TexFineTuningUtility.ResolveReferenceCopyBoundProperties(
                property,
                holders);
        }

        internal static IReadOnlyDictionary<
            Material, IReadOnlyDictionary<string, Texture>>
            BuildProjectedTextureUses(
                IReadOnlyDictionary<
                    Material, IReadOnlyDictionary<string, Texture>> original,
                IEnumerable<AtlasConstantTextureDecision> constants,
                IEnumerable<AtlasFeatureDisableDecision> featureDisables)
        {
            var projected = original.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.ToDictionary(
                    property => property.Key,
                    property => property.Value)
                    as IReadOnlyDictionary<string, Texture>);

            foreach (var item in constants)
            {
                if (!projected.TryGetValue(item.Material, out var readOnly))
                    continue;

                var properties = (Dictionary<string, Texture>)readOnly;
                foreach (var property in item.BoundProperties)
                    properties.Remove(property);
            }

            foreach (var item in featureDisables)
            {
                if (!projected.TryGetValue(item.Material, out var readOnly))
                    continue;

                var properties = (Dictionary<string, Texture>)readOnly;
                foreach (var property in item.Rule.TextureProperties)
                    properties.Remove(property);
            }

            return projected;
        }

        // Source optimization can remove the texture that previously
        // defined island density (notably an explicit _MainTex). Preserve only
        // materials whose projected primary basis actually changed; ordinary
        // groups keep the existing MaterialGroupingContext behavior.
        private static IReadOnlyDictionary<Material, Vector2Int?>
            BuildPrimaryDensityOverrides(
                IReadOnlyDictionary<
                    Material, IReadOnlyDictionary<string, Texture>> original,
                IReadOnlyDictionary<
                    Material, IReadOnlyDictionary<string, Texture>> projected,
                IEnumerable<Material> materials,
                AtlasSetting atlasSetting)
        {
            var result = new Dictionary<Material, Vector2Int?>();
            string? primaryProperty =
                atlasSetting.UsePrimaryMaximumTexture
                    ? null
                    : atlasSetting.PrimaryTextureProperty;

            foreach (var material in materials)
            {
                original.TryGetValue(material, out var originalTextures);
                projected.TryGetValue(material, out var projectedTextures);

                var originalSize = GetPrimarySize(
                    originalTextures,
                    primaryProperty);
                if (!originalSize.HasValue)
                    continue;

                var projectedSize = GetPrimarySize(
                    projectedTextures,
                    primaryProperty);
                if (projectedSize == originalSize)
                    continue;

                result[material] = originalSize;
            }

            return result;

            static Vector2Int? GetPrimarySize(
                IReadOnlyDictionary<string, Texture>? textures,
                string? primaryProperty)
            {
                if (textures == null || textures.Count == 0)
                    return null;

                if (primaryProperty != null)
                {
                    return textures.TryGetValue(
                               primaryProperty, out var primary)
                           && primary != null
                        ? new Vector2Int(primary.width, primary.height)
                        : null;
                }

                Texture? maximum = null;
                long maximumArea = -1;
                foreach (var texture in textures.Values)
                {
                    if (texture == null)
                        continue;
                    var area = (long)texture.width * texture.height;
                    if (maximum != null && area <= maximumArea)
                        continue;
                    maximum = texture;
                    maximumArea = area;
                }

                return maximum == null
                    ? null
                    : new Vector2Int(maximum.width, maximum.height);
            }
        }

        private static void FilterUnsetTextureConflicts(
            IDomain domain,
            List<AtlasConstantTextureDecision> constants,
            List<AtlasFeatureDisableDecision> featureDisables,
            IReadOnlyCollection<Material> materials,
            AtlasTextureExperimentalFeature? experimentalOptions)
        {
            if (experimentalOptions?.UnsetTextures.Any() != true)
                return;

            var referencedTextures = materials
                .SelectMany(material => material.EnumerateReferencedTextures())
                .Where(texture => texture != null)
                .Distinct()
                .ToArray();
            var unsetTextures = experimentalOptions.UnsetTextures
                .Select(selector => selector.SelectTexture)
                .Where(texture => texture != null)
                .SelectMany(selected => referencedTextures.Where(
                    texture => domain.OriginEqual(texture, selected)))
                .ToHashSet();
            if (unsetTextures.Count == 0)
                return;

            constants.RemoveAll(item =>
                unsetTextures.Contains(item.Source));
            featureDisables.RemoveAll(item =>
                item.Rule.TextureProperties.Any(property =>
                    item.Material.HasShaderTextureProperty(property)
                    && item.Material.GetTexture(property) is Texture texture
                    && unsetTextures.Contains(texture)));
        }

        private static void FilterFineTuningConflicts(
            List<AtlasConstantTextureDecision> constants,
            List<AtlasFeatureDisableDecision> featureDisables,
            IReadOnlyDictionary<
                Material, IReadOnlyDictionary<string, Texture>> original,
            AtlasSetting atlasSetting,
            AtlasTextureExperimentalFeature? experimentalOptions)
        {
            if (constants.Count == 0 && featureDisables.Count == 0)
                return;

            // The automatic experimental merge/reference-copy modes derive
            // relationships from the complete atlas group. Reproducing those
            // semantics per material would be a second atlas planner, so leave
            // source optimization disabled while either mode is active.
            if (experimentalOptions?.AutoMergeTextureSetting == true
                || experimentalOptions?.AutoReferenceCopySetting == true)
            {
                constants.Clear();
                featureDisables.Clear();
                return;
            }

            var properties = original.Values
                .SelectMany(textures => textures.Keys)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (properties.Length == 0)
                return;

            var holders =
                TexFineTuningUtility.InitTexFineTuningHolders(properties);
            foreach (var fineTuning in atlasSetting.TextureFineTuning)
                fineTuning?.AddSetting(holders);
            if (experimentalOptions != null)
                AtlasTexture.IndividualTuningAddSettings(
                    holders,
                    experimentalOptions.TextureIndividualFineTuning);

            var globalProperties =
                properties.ToHashSet(StringComparer.Ordinal);
            var blocked = new HashSet<string>(StringComparer.Ordinal);
            var referenceSources =
                new HashSet<string>(StringComparer.Ordinal);
            foreach (var pair in holders)
            {
                var remove = pair.Value.Find<RemoveData>();
                if (remove?.IsRemove == true)
                    blocked.Add(pair.Key);

                var reference = pair.Value.Find<ReferenceCopyData>();
                if (reference != null
                    && !string.IsNullOrWhiteSpace(reference.CopySource)
                    && globalProperties.Contains(reference.CopySource!))
                {
                    // The normal atlas intentionally replaces this property's
                    // pixels with another property. Its own constant source is
                    // therefore not the semantic output to optimize.
                    blocked.Add(pair.Key);
                    referenceSources.Add(reference.CopySource!);
                }

                var merge = pair.Value.Find<MergeTextureData>();
                if (merge == null
                    || string.IsNullOrWhiteSpace(merge.MargeParent)
                    || !globalProperties.Contains(merge.MargeParent!))
                    continue;

                blocked.Add(pair.Key);
                blocked.Add(merge.MargeParent!);
            }

            constants.RemoveAll(item => blocked.Contains(item.Property));
            featureDisables.RemoveAll(item =>
                item.Rule.TextureProperties.Any(property =>
                    blocked.Contains(property)
                    || referenceSources.Contains(property)));
        }

        internal static AtlasConstantTextureOutput[] CreateConstantOutputs(
            ITexTransToolForUnity engine,
            AtlasSourceOptimizationPlan plan,
            AtlasSetting atlasSetting,
            AtlasTextureExperimentalFeature? experimentalOptions,
            IReadOnlyCollection<Material> materials)
        {
            var selected = materials.ToHashSet();
            var outputs = new List<AtlasConstantTextureOutput>();

            foreach (var decision in plan.ConstantTextures
                         .Where(item => selected.Contains(item.Material)))
            {
                var created = engine.CreateRenderTexture(4, 4);
                created.Name = "AtlasConstant_" + decision.Property;
                engine.ColorFill(created, decision.Color.ToTTCore());

                var holders = TexFineTuningUtility.InitTexFineTuningHolders(
                    new[] { decision.Property });
                TexFineTuningUtility.ApplySourceMipMapDefaults(
                    new[] { decision.Material },
                    plan.OriginalTextureUses,
                    holders);

                foreach (var fineTuning in atlasSetting.TextureFineTuning)
                    fineTuning?.AddSetting(holders);
                if (experimentalOptions != null)
                    AtlasTexture.IndividualTuningAddSettings(
                        holders,
                        experimentalOptions.TextureIndividualFineTuning);

                var raw = new Dictionary<string, ITTRenderTexture>
                {
                    [decision.Property] = created,
                };
                var tuned = TexFineTuningUtility.ProcessingTextureFineTuning(
                    engine, holders, raw);

                if (!tuned.RenderTextures.TryGetValue(
                        decision.Property, out var final)
                    || !tuned.TextureDescriptors.TryGetValue(
                        final, out var descriptor))
                {
                    foreach (var texture in tuned.TextureDescriptors.Keys)
                        texture.Dispose();
                    if (!tuned.TextureDescriptors.ContainsKey(created))
                        created.Dispose();
                    continue;
                }

                foreach (var texture in tuned.TextureDescriptors.Keys
                             .Where(texture => !ReferenceEquals(texture, final))
                             .Distinct())
                    texture.Dispose();
                if (!ReferenceEquals(created, final)
                    && !tuned.TextureDescriptors.ContainsKey(created))
                    created.Dispose();

                outputs.Add(new AtlasConstantTextureOutput(
                    decision,
                    final,
                    descriptor,
                    decision.BoundProperties));
            }

            return outputs.ToArray();
        }

        internal static void DisposeConstantOutputs(
            IEnumerable<AtlasConstantTextureOutput> outputs)
        {
            foreach (var texture in outputs
                         .Select(output => output.RenderTexture)
                         .Distinct())
                texture.Dispose();
        }

        internal static void ApplyToGeneratedMaterials(
            IReadOnlyDictionary<Material, Material> materialMap,
            IEnumerable<AtlasConstantTextureOutput> constantOutputs,
            IEnumerable<AtlasFeatureDisableDecision> featureDisables,
            ITexTransToolForUnity engine,
            bool forceSetTexture = false)
        {
            var outputs = constantOutputs
                .OrderBy(output => output.Decision.Property,
                    StringComparer.Ordinal)
                .ToArray();

            // Apply direct decisions first. FineTuning aliases such as
            // ReferenceCopy are applied afterwards, matching normal atlas
            // semantics where the copy source supersedes the target output.
            foreach (var output in outputs)
            {
                SetTexture(
                    output.Decision.Material,
                    output.Decision.Property,
                    output);
            }

            foreach (var output in outputs)
            {
                foreach (var property in output.BoundProperties)
                {
                    if (string.Equals(
                            property,
                            output.Decision.Property,
                            StringComparison.Ordinal))
                        continue;
                    SetTexture(output.Decision.Material, property, output);
                }
            }

            foreach (var item in featureDisables)
            {
                if (!materialMap.TryGetValue(item.Material, out var generated))
                    continue;

                if (generated.HasInt(item.Rule.SwitchProperty))
                    generated.SetInt(item.Rule.SwitchProperty, 0);
                else if (generated.HasFloat(item.Rule.SwitchProperty))
                    generated.SetFloat(item.Rule.SwitchProperty, 0f);

                foreach (var property in item.Rule.TextureProperties)
                    if (generated.HasShaderTextureProperty(property))
                        generated.SetTexture(property, null);
            }

            void SetTexture(
                Material sourceMaterial,
                string property,
                AtlasConstantTextureOutput output)
            {
                if (!materialMap.TryGetValue(
                        sourceMaterial, out var generated)
                    || !generated.HasShaderTextureProperty(property))
                    return;

                var current = generated.GetTexture(property);
                if (!forceSetTexture && current == null)
                    return;
                if (current != null
                    && !(current is Texture2D)
                    && !(current is RenderTexture))
                    return;

                generated.SetTexture(
                    property,
                    engine.GetReferenceRenderTexture(output.RenderTexture));
            }
        }

        private static Dictionary<Material, HashSet<(Mesh mesh, int subMesh)>>
            BuildMaterialSlots(
                IRendererTargeting targeting,
                IEnumerable<Renderer> renderers,
                HashSet<Material> targetMaterials)
        {
            var result =
                new Dictionary<Material, HashSet<(Mesh mesh, int subMesh)>>();

            foreach (var renderer in renderers)
            {
                if (renderer == null) continue;
                var mesh = targeting.GetMesh(renderer);
                if (mesh == null || mesh.subMeshCount == 0) continue;
                var materials = targeting.GetMaterials(renderer);

                for (var slot = 0; slot < materials.Length; slot += 1)
                {
                    var material = materials[slot];
                    if (material == null
                        || !targetMaterials.Contains(material))
                        continue;

                    var subMesh = Mathf.Clamp(slot, 0, mesh.subMeshCount - 1);
                    if (mesh.GetSubMesh(subMesh).indexCount == 0) continue;
                    if (!result.TryGetValue(material, out var materialSlots))
                        result[material] = materialSlots =
                            new HashSet<(Mesh mesh, int subMesh)>();
                    materialSlots.Add((mesh, subMesh));
                }
            }

            return result;
        }

        private static List<AtlasFeatureDisableDecision>
            DetectFeatureDisables(
                IEnumerable<Material> materials,
                IReadOnlyDictionary<
                    (Material material, string property), SampleResult> samples)
        {
            var result = new List<AtlasFeatureDisableDecision>();

            foreach (var material in materials)
            {
                if (!IsSupportedLilToon(material)) continue;

                bool RgbZero(string property) =>
                    samples.TryGetValue((material, property), out var sample)
                    && sample.Known && sample.RgbZero;
                bool AlphaZero(string property) =>
                    samples.TryGetValue((material, property), out var sample)
                    && sample.Known && sample.AlphaZero;
                bool Enabled(string property) =>
                    material.HasProperty(property)
                    && Mathf.RoundToInt(material.GetFloat(property)) != 0;
                bool FloatZero(string property) =>
                    material.HasProperty(property)
                    && material.GetFloat(property) == 0f;
                bool ColorAlphaZero(string property) =>
                    material.HasProperty(property)
                    && material.GetColor(property).a == 0f;
                bool ColorRgbZero(string property)
                {
                    if (!material.HasProperty(property)) return false;
                    var color = material.GetColor(property);
                    return color.r == 0f && color.g == 0f && color.b == 0f;
                }
                int BlendMode(string property) =>
                    material.HasProperty(property)
                        ? Mathf.RoundToInt(material.GetFloat(property))
                        : -1;

                if (Enabled("_UseMatCap"))
                {
                    string? proof = null;
                    if (RgbZero("_MatCapBlendMask"))
                        proof = "_MatCapBlendMask";
                    else if (FloatZero("_MatCapBlend"))
                        proof = "_MatCapBlend";
                    else if (ColorAlphaZero("_MatCapColor"))
                        proof = "_MatCapColor.a";

                    if (proof != null)
                    {
                        result.Add(new AtlasFeatureDisableDecision(
                            material,
                            new AtlasFeatureDisableRule(
                                "_UseMatCap",
                                "_MatCapTex",
                                "_MatCapBlendMask",
                                "_MatCapBumpMap"),
                            proof));
                    }
                }

                if (Enabled("_UseMatCap2nd"))
                {
                    string? proof = null;
                    if (RgbZero("_MatCap2ndBlendMask"))
                        proof = "_MatCap2ndBlendMask";
                    else if (FloatZero("_MatCap2ndBlend"))
                        proof = "_MatCap2ndBlend";
                    else if (ColorAlphaZero("_MatCap2ndColor"))
                        proof = "_MatCap2ndColor.a";

                    if (proof != null)
                    {
                        result.Add(new AtlasFeatureDisableDecision(
                            material,
                            new AtlasFeatureDisableRule(
                                "_UseMatCap2nd",
                                "_MatCap2ndTex",
                                "_MatCap2ndBlendMask",
                                "_MatCap2ndBumpMap"),
                            proof));
                    }
                }

                if (Enabled("_UseMain2ndTex")
                    && material.HasProperty("_Main2ndTexAlphaMode")
                    && material.GetFloat("_Main2ndTexAlphaMode") == 0f
                    && material.HasProperty("_Main2ndDissolveParams")
                    && material.GetVector("_Main2ndDissolveParams").x == 0f)
                {
                    var safety = new List<string>
                    {
                        "_Main2ndTexAlphaMode",
                        "_Main2ndDissolveParams.x",
                    };
                    var main2ndBlendMode =
                        BlendMode("_Main2ndTexBlendMode");
                    var neutral = false;

                    if (RgbZero("_Main2ndBlendMask"))
                    {
                        safety.Add("_Main2ndBlendMask");
                        neutral = true;
                    }
                    else if (AlphaZero("_Main2ndTex"))
                    {
                        safety.Add("_Main2ndTex");
                        neutral = true;
                    }
                    else if (ColorAlphaZero("_Color2nd"))
                    {
                        safety.Add("_Color2nd.a");
                        neutral = true;
                    }
                    else if (FloatZero("_Main2ndEnableLighting"))
                    {
                        safety.Add("_Main2ndEnableLighting");
                        neutral = true;
                    }
                    else if (IsZeroRgbNeutralBlendMode(main2ndBlendMode)
                             && RgbZero("_Main2ndTex"))
                    {
                        safety.Add("_Main2ndTex");
                        safety.Add("_Main2ndTexBlendMode");
                        neutral = true;
                    }
                    else if (IsZeroRgbNeutralBlendMode(main2ndBlendMode)
                             && ColorRgbZero("_Color2nd"))
                    {
                        safety.AddRange(new[]
                        {
                            "_Color2nd.r",
                            "_Color2nd.g",
                            "_Color2nd.b",
                        });
                        safety.Add("_Main2ndTexBlendMode");
                        neutral = true;
                    }

                    if (neutral)
                    {
                        result.Add(new AtlasFeatureDisableDecision(
                            material,
                            new AtlasFeatureDisableRule(
                                "_UseMain2ndTex",
                                "_Main2ndTex",
                                "_Main2ndBlendMask",
                                "_Main2ndDissolveMask",
                                "_Main2ndDissolveNoiseMask"),
                            safety.ToArray()));
                    }
                }

                TryEmission(
                    "_UseEmission",
                    "_EmissionBlendMode",
                    "_EmissionBlend",
                    "_EmissionColor",
                    "_EmissionMap",
                    "_EmissionBlendMask",
                    "_EmissionGradTex");
                TryEmission(
                    "_UseEmission2nd",
                    "_Emission2ndBlendMode",
                    "_Emission2ndBlend",
                    "_Emission2ndColor",
                    "_Emission2ndMap",
                    "_Emission2ndBlendMask",
                    "_Emission2ndGradTex");

                void TryEmission(
                    string switchProperty,
                    string blendModeProperty,
                    string blendProperty,
                    string colorProperty,
                    string mapProperty,
                    string maskProperty,
                    string gradProperty)
                {
                    if (!Enabled(switchProperty)
                        || !material.HasProperty(blendModeProperty))
                        return;

                    var mode = BlendMode(blendModeProperty);
                    var safety = new List<string>();

                    if (FloatZero(blendProperty))
                    {
                        safety.Add(blendProperty);
                    }
                    else if (ColorAlphaZero(colorProperty))
                    {
                        safety.Add(colorProperty + ".a");
                    }
                    else if (AlphaZero(mapProperty))
                    {
                        safety.Add(mapProperty);
                    }
                    else if (AlphaZero(maskProperty))
                    {
                        safety.Add(maskProperty);
                    }
                    else if (IsZeroRgbNeutralBlendMode(mode)
                             && RgbZero(mapProperty))
                    {
                        safety.Add(mapProperty);
                        safety.Add(blendModeProperty);
                    }
                    else if (IsZeroRgbNeutralBlendMode(mode)
                             && RgbZero(maskProperty))
                    {
                        safety.Add(maskProperty);
                        safety.Add(blendModeProperty);
                    }
                    else if (IsZeroRgbNeutralBlendMode(mode)
                             && ColorRgbZero(colorProperty))
                    {
                        safety.AddRange(new[]
                        {
                            colorProperty + ".r",
                            colorProperty + ".g",
                            colorProperty + ".b",
                        });
                        safety.Add(blendModeProperty);
                    }
                    else
                    {
                        return;
                    }

                    result.Add(new AtlasFeatureDisableDecision(
                        material,
                        new AtlasFeatureDisableRule(
                            switchProperty,
                            mapProperty, maskProperty, gradProperty),
                        safety.ToArray()));
                }
            }

            return result;
        }

        private static bool CanUseUnityGpuSourceAnalyzer(
            ITexTransToolForUnity engine)
        {
#if CONTAINS_TTCE_WGPU
            // WGPU exposes a Unity RenderTexture placeholder so normal TTT
            // outputs can be replaced after deferred readback. Its pixels are
            // not synchronized with the TTWgpuRenderTexture at this point, so
            // source proof must not sample that placeholder.
            if (engine is global::net.rs64.TexTransTool.TTCEWgpuWithTTT4Unity)
                return false;
#endif
            return true;
        }

        internal static bool IsZeroRgbNeutralBlendMode(
            int blendMode)
        {
            // lilBlendColor: black is neutral only for Add and Screen.
            // Normal and Multiply still modify the destination when alpha > 0.
            return blendMode == 1 || blendMode == 2;
        }

        private static bool IsSupportedLilToon(Material material)
        {
            var shaderName = material.shader != null
                ? material.shader.name
                : string.Empty;

            return (shaderName == "lilToon"
                    || shaderName.StartsWith(
                        "Hidden/lilToon", StringComparison.Ordinal))
                   && !shaderName.Contains("Lite")
                   && !shaderName.Contains("Gem");
        }
    }
}
