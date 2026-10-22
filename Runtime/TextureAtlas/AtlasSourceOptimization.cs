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
    internal readonly struct AtlasConstantTextureDecision
    {
        internal readonly Material Material;
        internal readonly string Property;
        internal readonly Texture Source;
        internal readonly Color Color;

        internal AtlasConstantTextureDecision(
            Material material, string property, Texture source, Color color)
        {
            Material = material;
            Property = property;
            Source = source;
            Color = color;
        }
    }

    internal sealed class AtlasFeatureDisableRule
    {
        internal readonly string Name;
        internal readonly string SwitchProperty;
        internal readonly string[] TextureProperties;

        internal AtlasFeatureDisableRule(
            string name, string switchProperty, params string[] textureProperties)
        {
            Name = name;
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

        internal AtlasSourceOptimizationPlan(
            IReadOnlyDictionary<Material, IReadOnlyDictionary<string, Texture>>
                originalTextureUses,
            IReadOnlyDictionary<Material, IReadOnlyDictionary<string, Texture>>
                projectedTextureUses,
            IEnumerable<AtlasConstantTextureDecision> constantTextures,
            IEnumerable<AtlasFeatureDisableDecision> featureDisables)
        {
            OriginalTextureUses = originalTextureUses;
            ProjectedTextureUses = projectedTextureUses;
            ConstantTextures = constantTextures.ToArray();
            FeatureDisables = featureDisables.ToArray();
        }

        internal bool HasChanges =>
            ConstantTextures.Length != 0 || FeatureDisables.Length != 0;

        internal HashSet<Material> GetAtlasMaterials(
            IEnumerable<Material> materials)
        {
            return materials
                .Where(material =>
                    ProjectedTextureUses.TryGetValue(material, out var textures)
                    && textures.Count != 0)
                .ToHashSet();
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
                .Select(item => (item.Material, item.Property))
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
            internal readonly bool IsConstant;
            internal readonly Color ConstantColor;

            internal SampleResult(
                bool known, bool rgbZero, bool alphaZero,
                bool isConstant, Color constantColor)
            {
                Known = known;
                RgbZero = rgbZero;
                AlphaZero = alphaZero;
                IsConstant = isConstant;
                ConstantColor = constantColor;
            }
        }

        private readonly struct Candidate
        {
            internal readonly Material Material;
            internal readonly string Property;
            internal readonly Texture Texture;
            internal readonly Matrix2x3 Matrix;
            internal readonly HashSet<(Mesh mesh, int subMesh)> Slots;

            internal Candidate(
                Material material, string property, Texture texture,
                Matrix2x3 matrix, HashSet<(Mesh mesh, int subMesh)> slots)
            {
                Material = material;
                Property = property;
                Texture = texture;
                Matrix = matrix;
                Slots = slots;
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
                    original, original,
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
                        || ((int)usage.UVChannel - 1) != (int)atlasTargetUvChannel
                        || usage.UVMatrix is not { } matrix)
                        continue;

                    candidates.Add(new Candidate(
                        material, pair.Key, pair.Value, matrix, materialSlots));
                }
            }

            var samples =
                new Dictionary<(Material material, string property), SampleResult>();
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
                                var width = source.width;
                                var height = source.height;
                                var sourcePixels =
                                    (long)width * height;

                                if (width <= 0
                                    || height <= 0
                                    || sourcePixels > MaxSourcePixels)
                                {
                                    groupIndex += 1;
                                    continue;
                                }

                                if (loadedSources.Count != 0
                                    && batchPixels + sourcePixels
                                        > MaxGpuBatchSourcePixels)
                                    break;

                                groupIndex += 1;
                                ITTRenderTexture? loadedSource = null;
                                try
                                {
                                    // Use the same canonical source loading path
                                    // as AtlasContext. The GPU proof only asks
                                    // equality/zero questions, while an accepted
                                    // constant color is read back separately as
                                    // RGBAFloat from this same render texture.
                                    loadedSource =
                                        engine.WrappingOrUploadToLoadFullScale(
                                            source);
                                    var sampledSource =
                                        engine.GetReferenceRenderTexture(
                                            loadedSource);
                                    if (sampledSource.width != width
                                        || sampledSource.height != height)
                                        continue;

                                    var loaded = loadedSource;
                                    if (loaded == null)
                                        continue;
                                    loadedSources.Add(loaded);
                                    loadedSource = null;
                                    batchPixels += sourcePixels;

                                    var analysisGroups = textureGroup
                                        .GroupBy(candidate =>
                                        {
                                            var matrix = candidate.Matrix;
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
                                        var matrix = representative.Matrix;
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
                                                out var queued)
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

                            var constantRequests =
                                new List<(Candidate[] candidates,
                                    AtlasSourceGpuFlags flags,
                                    AsyncGPUReadbackRequest request)>();

                            // Every result request in this batch has already
                            // been issued. Waiting here avoids a render->wait
                            // round-trip for every material/property candidate.
                            foreach (var item in pending)
                            {
                                if (!item.pending.TryGetFlags(out var flags)
                                    || !flags.Known)
                                    continue;

                                foreach (var candidate in item.candidates)
                                {
                                    // Zero proofs remain useful even if the
                                    // optional constant-color readback fails.
                                    samples[(
                                        candidate.Material,
                                        candidate.Property)] =
                                        new SampleResult(
                                            true,
                                            flags.RgbZero,
                                            flags.AlphaZero,
                                            false,
                                            default);
                                }

                                if (!flags.IsConstant)
                                    continue;

                                try
                                {
                                    constantRequests.Add((
                                        item.candidates,
                                        flags,
                                        item.pending.RequestReferenceColor()));
                                }
                                catch
                                {
                                    // Keep the zero proofs above; only 4x4
                                    // constant replacement is unavailable.
                                }
                            }

                            // Issue every 1x1 color request before waiting for
                            // any of them. This preserves the exact canonical
                            // RGBAFloat value used by the previous CPU path.
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

                                    foreach (var candidate in item.candidates)
                                    {
                                        samples[(
                                            candidate.Material,
                                            candidate.Property)] =
                                            new SampleResult(
                                                true,
                                                item.flags.RgbZero,
                                                item.flags.AlphaZero,
                                                true,
                                                data[0]);
                                    }
                                }
                                catch
                                {
                                    // Keep the zero proof recorded above.
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
                    if (!samples.TryGetValue(
                            (candidate.Material, candidate.Property),
                            out var sample)
                        || !sample.Known
                        || !sample.IsConstant)
                        continue;

                    if ((long)candidate.Texture.width
                            * candidate.Texture.height <= 16)
                        continue;

                    if (animationSafety?.HasUnsafeConstantTextureAnimation(
                            domain,
                            candidate.Material,
                            candidate.Property,
                            candidate.Texture,
                            targetRenderers) == true)
                        continue;

                    constants.Add(new AtlasConstantTextureDecision(
                        candidate.Material,
                        candidate.Property,
                        candidate.Texture,
                        sample.ConstantColor));
                }
            }

            var featureDisables =
                atlasSetting.DisableVisuallyUnusedShaderFeatures
                ? DetectFeatureDisables(targetMaterials, samples)
                    .Where(decision =>
                        animationSafety?.HasUnsafeFeatureDisableAnimation(
                            domain,
                            decision,
                            targetRenderers) != true)
                    .ToList()
                : new List<AtlasFeatureDisableDecision>();

            FilterFineTuningConflicts(
                constants,
                featureDisables,
                original,
                atlasSetting,
                experimentalOptions);

            var projected = original.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.ToDictionary(
                    property => property.Key,
                    property => property.Value)
                    as IReadOnlyDictionary<string, Texture>);

            foreach (var item in constants)
            {
                if (projected.TryGetValue(item.Material, out var readOnly))
                    ((Dictionary<string, Texture>)readOnly).Remove(item.Property);
            }

            foreach (var item in featureDisables)
            {
                if (!projected.TryGetValue(item.Material, out var readOnly))
                    continue;
                var properties = (Dictionary<string, Texture>)readOnly;
                foreach (var property in item.Rule.TextureProperties)
                    properties.Remove(property);
            }

            // A disabled feature supersedes any constant replacement belonging
            // to that feature; no 4x4 texture needs to be generated for it.
            var disabledBindings = featureDisables
                .SelectMany(item => item.Rule.TextureProperties.Select(
                    property => (item.Material, property)))
                .ToHashSet();
            constants.RemoveAll(item =>
                disabledBindings.Contains((item.Material, item.Property)));

            return new AtlasSourceOptimizationPlan(
                original, projected, constants, featureDisables);
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
                item.Rule.TextureProperties.Any(blocked.Contains));
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

                var boundProperties = tuned.RenderTextures
                    .Where(pair => ReferenceEquals(pair.Value, final))
                    .Select(pair => pair.Key)
                    .ToArray();
                outputs.Add(new AtlasConstantTextureOutput(
                    decision, final, descriptor, boundProperties));
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

        internal static bool TryGetCoverageBounds(
            bool[] coverage,
            int width,
            int height,
            out RectInt bounds)
        {
            var minX = width;
            var minY = height;
            var maxX = -1;
            var maxY = -1;

            for (var y = 0; y < height; y += 1)
            for (var x = 0; x < width; x += 1)
            {
                if (!coverage[y * width + x]) continue;
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }

            if (maxX < minX || maxY < minY)
            {
                bounds = default;
                return false;
            }

            bounds = new RectInt(
                minX,
                minY,
                maxX - minX + 1,
                maxY - minY + 1);
            return true;
        }

        internal static bool TryGetExactConstantForCoverage(
            Color[] pixels,
            bool[] coverage,
            out Color color)
        {
            var result = Scan(pixels, coverage);
            color = result.ConstantColor;
            return result.Known && result.IsConstant;
        }

        private static SampleResult Scan(Color[] pixels, bool[] coverage)
        {
            if (pixels.Length != coverage.Length)
                return default;

            var hasFirst = false;
            var first = default(Color);
            var constant = true;
            var rgbZero = true;
            var alphaZero = true;

            for (var index = 0; index < pixels.Length; index += 1)
            {
                if (!coverage[index]) continue;
                var pixel = pixels[index];
                if (!hasFirst)
                {
                    first = pixel;
                    hasFirst = true;
                }
                else if (constant
                         && (pixel.r != first.r
                             || pixel.g != first.g
                             || pixel.b != first.b
                             || pixel.a != first.a))
                {
                    constant = false;
                }

                if (pixel.r != 0f || pixel.g != 0f || pixel.b != 0f)
                    rgbZero = false;
                if (pixel.a != 0f)
                    alphaZero = false;
            }

            return new SampleResult(
                hasFirst,
                rgbZero,
                alphaZero,
                hasFirst && constant,
                first);
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
                        proof = "_MatCapColor";

                    if (proof != null)
                    {
                        result.Add(new AtlasFeatureDisableDecision(
                            material,
                            new AtlasFeatureDisableRule(
                                "MatCap1", "_UseMatCap",
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
                        proof = "_MatCap2ndColor";

                    if (proof != null)
                    {
                        result.Add(new AtlasFeatureDisableDecision(
                            material,
                            new AtlasFeatureDisableRule(
                                "MatCap2", "_UseMatCap2nd",
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
                        "_Main2ndDissolveParams",
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
                        safety.Add("_Color2nd");
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
                        safety.Add("_Color2nd");
                        safety.Add("_Main2ndTexBlendMode");
                        neutral = true;
                    }

                    if (neutral)
                    {
                        result.Add(new AtlasFeatureDisableDecision(
                            material,
                            new AtlasFeatureDisableRule(
                                "Main2nd", "_UseMain2ndTex",
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
                    "_EmissionGradTex",
                    "Emission1");
                TryEmission(
                    "_UseEmission2nd",
                    "_Emission2ndBlendMode",
                    "_Emission2ndBlend",
                    "_Emission2ndColor",
                    "_Emission2ndMap",
                    "_Emission2ndBlendMask",
                    "_Emission2ndGradTex",
                    "Emission2");

                void TryEmission(
                    string switchProperty,
                    string blendModeProperty,
                    string blendProperty,
                    string colorProperty,
                    string mapProperty,
                    string maskProperty,
                    string gradProperty,
                    string name)
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
                        safety.Add(colorProperty);
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
                        safety.Add(colorProperty);
                        safety.Add(blendModeProperty);
                    }
                    else
                    {
                        return;
                    }

                    result.Add(new AtlasFeatureDisableDecision(
                        material,
                        new AtlasFeatureDisableRule(
                            name, switchProperty,
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
