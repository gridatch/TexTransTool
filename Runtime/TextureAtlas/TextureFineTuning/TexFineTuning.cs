#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using net.rs64.TexTransCore;
using net.rs64.TexTransTool.Utils;
using UnityEngine;

namespace net.rs64.TexTransTool.TextureAtlas.FineTuning
{
    /*
        競合しないタイプは 0、参照破壊や変更系が後の方に行く


        MipMapRemove 0
        ColorSpace 0
        Compress 0

        MergeTexture 16
        ReferenceCopy 32

        Remove 64

        DiscardAlphaChannel 128
        Resize 129

    */

    internal class TexFineTuningHolder
    {
        Dictionary<Type, ITuningData> _tuningDataDict;
        internal TexFineTuningHolder() { _tuningDataDict = new(); }
        internal IEnumerable<Type> TuningDataTypes => _tuningDataDict.Keys;

        internal TuningData? Find<TuningData>() where TuningData : class, ITuningData, new()
        {
            if (_tuningDataDict.TryGetValue(typeof(TuningData), out ITuningData t)) { return t as TuningData; }
            else { return null; }
        }
        internal TuningData Get<TuningData>() where TuningData : class, ITuningData, new()
        {
            if (_tuningDataDict.ContainsKey(typeof(TuningData))) { return (TuningData)_tuningDataDict[typeof(TuningData)]; }
            else
            {
                var d = _tuningDataDict[typeof(TuningData)] = new TuningData();
                return (TuningData)d;
            }
        }
        internal void Set<TuningData>(TuningData tuningData) where TuningData : class, ITuningData, new()
        {
            _tuningDataDict[typeof(TuningData)] = tuningData;
        }
    }
    internal class TexFineTuningProcessingContext
    {
        public ITexTransToolForUnity Engine;
        public Dictionary<string, TexFineTuningHolder> TuningHolder;
        public Dictionary<string, TextureProcessingHolder> ProcessingHolder;

        public Dictionary<string, ITTRenderTexture> RenderTextures;

        public HashSet<ITTRenderTexture> NewRenderTextures = new();

        public Dictionary<string, ITTRenderTexture> OriginRenderTextures;

        public TexFineTuningProcessingContext(ITexTransToolForUnity ttt4u, Dictionary<string, TexFineTuningHolder> tuningHolder, Dictionary<string, ITTRenderTexture> renderTextures, Dictionary<ITTRenderTexture, TexTransToolTextureDescriptor> textureDescriptors)
        {
            Engine = ttt4u;

            TuningHolder = tuningHolder;

            RenderTextures = new(renderTextures);
            ProcessingHolder = RenderTextures.ToDictionary(rt => rt.Key, rt => new TextureProcessingHolder(rt.Key, new TexTransToolTextureDescriptor()));
            foreach (var prop in TuningHolder.Keys)
            {
                if (ProcessingHolder.ContainsKey(prop)) { continue; }
                ProcessingHolder.Add(prop, new("", new()) { RTOwned = false, RenderTextureProperty = null });// ReferenceCopy で増えたやつ用に
            }

            OriginRenderTextures = renderTextures;
        }

        internal class TextureProcessingHolder
        {
            public bool RTOwned = true;
            public string? RenderTextureProperty;
            public TexTransToolTextureDescriptor TextureDescriptor;
            public HashSet<string> SourceProperties;

            public TextureProcessingHolder(string renderTextureProperty, TexTransToolTextureDescriptor textureDescriptor)
            {
                RenderTextureProperty = renderTextureProperty;
                TextureDescriptor = textureDescriptor;
                SourceProperties = string.IsNullOrEmpty(renderTextureProperty)
                    ? new HashSet<string>()
                    : new HashSet<string> { renderTextureProperty };
            }

        }
    }
    internal class TexFineTuningResult
    {
        public Dictionary<string, ITTRenderTexture> RenderTextures;
        public Dictionary<ITTRenderTexture, TexTransToolTextureDescriptor> TextureDescriptors;
        public Dictionary<string, IReadOnlyCollection<string>> SourceProperties;

        public TexFineTuningResult(
            Dictionary<string, ITTRenderTexture> renderTextures,
            Dictionary<ITTRenderTexture, TexTransToolTextureDescriptor> textureDescriptors,
            Dictionary<string, IReadOnlyCollection<string>> sourceProperties)
        {
            RenderTextures = renderTextures;
            TextureDescriptors = textureDescriptors;
            SourceProperties = sourceProperties;
        }
    }
    internal class TexFineTuningUtility
    {

        public static Dictionary<string, TexFineTuningHolder> InitTexFineTuningHolders(IEnumerable<string> properties)
        {
            var texFineTuningTargets = properties.ToDictionary(i => i, i => new TexFineTuningHolder());
            foreach (var texKv in texFineTuningTargets)
            {
                texKv.Value.Set(new MipMapData());
                texKv.Value.Set(new TextureCompressionTuningData());
            }
            return texFineTuningTargets;
        }

        internal static Dictionary<string, string>
            AddEquivalentOutputReferenceCopies(
                IReadOnlyCollection<Material> materials,
                IReadOnlyDictionary<
                    Material,
                    IReadOnlyDictionary<string, Texture>> propertyTextures,
                IReadOnlyDictionary<string, Vector2Int> propertySizes,
                Dictionary<string, TexFineTuningHolder> tuningHolders)
        {
            var uvUsages = materials.ToDictionary(
                material => material,
                TTShaderTextureUsageInformationUtil
                    .GetContainsUVTransformUsage);

            return AddEquivalentOutputReferenceCopies(
                materials,
                propertyTextures,
                uvUsages,
                propertySizes,
                tuningHolders);
        }

        internal static Dictionary<string, string>
            AddEquivalentOutputReferenceCopies(
                IReadOnlyCollection<Material> materials,
                IReadOnlyDictionary<
                    Material,
                    IReadOnlyDictionary<string, Texture>> propertyTextures,
                IReadOnlyDictionary<
                    Material,
                    IReadOnlyDictionary<string, TextureUVUsage>> uvUsages,
                IReadOnlyDictionary<string, Vector2Int> propertySizes,
                Dictionary<string, TexFineTuningHolder> tuningHolders)
        {
            var automaticCopies =
                new Dictionary<string, string>(StringComparer.Ordinal);

            var mergeParticipants =
                new HashSet<string>(StringComparer.Ordinal);
            foreach (var tuning in tuningHolders)
            {
                var merge = tuning.Value.Find<MergeTextureData>();
                if (merge == null
                    || string.IsNullOrWhiteSpace(merge.MargeParent))
                {
                    continue;
                }

                mergeParticipants.Add(tuning.Key);
                mergeParticipants.Add(merge.MargeParent);
            }

            var existingReferenceSources = tuningHolders
                .Select(pair => pair.Value.Find<ReferenceCopyData>()?.CopySource)
                .Where(source => !string.IsNullOrWhiteSpace(source))
                .Select(source => source!)
                .ToHashSet(StringComparer.Ordinal);

            var candidates = propertySizes.Keys
                .Where(tuningHolders.ContainsKey)
                .Where(property => IsEquivalentOutputCandidate(
                    property,
                    tuningHolders[property],
                    mergeParticipants))
                .OrderBy(property =>
                    existingReferenceSources.Contains(property) ? 0 : 1)
                .ThenBy(property =>
                    string.Equals(
                        property, "_MainTex",
                        StringComparison.Ordinal) ? 0 : 1)
                .ThenBy(property => property, StringComparer.Ordinal)
                .ToArray();

            var canonicalProperties = new List<string>();
            foreach (var property in candidates)
            {
                var source = canonicalProperties.FirstOrDefault(candidate =>
                    AreEquivalentAtlasOutputs(
                        candidate,
                        property,
                        materials,
                        propertyTextures,
                        uvUsages,
                        propertySizes,
                        tuningHolders));

                if (source == null)
                {
                    canonicalProperties.Add(property);
                    continue;
                }

                tuningHolders[property]
                    .Get<ReferenceCopyData>()
                    .CopySource = source;
                automaticCopies[property] = source;
            }

            return automaticCopies;
        }

        private static bool IsEquivalentOutputCandidate(
            string property,
            TexFineTuningHolder holder,
            HashSet<string> mergeParticipants)
        {
            if (mergeParticipants.Contains(property))
                return false;

            var referenceCopy = holder.Find<ReferenceCopyData>();
            if (referenceCopy != null
                && !string.IsNullOrWhiteSpace(referenceCopy.CopySource))
            {
                return false;
            }

            // Pixel-changing or output-removing tunings are deliberately
            // outside this first sharing optimization. Sharing is limited to
            // outputs whose pixels are produced by the same atlas rendering
            // path and whose descriptor-only tunings are equivalent.
            if (holder.Find<DiscardAlphaChannelData>() != null
                || holder.Find<SizeData>() != null
                || holder.Find<RemoveData>() != null)
            {
                return false;
            }

            foreach (var type in holder.TuningDataTypes)
            {
                if (type == typeof(MipMapData)
                    || type == typeof(TextureCompressionTuningData)
                    || type == typeof(ColorSpaceData)
                    || type == typeof(ReferenceCopyData)
                    || type == typeof(MergeTextureData))
                {
                    continue;
                }

                // A future/unknown tuning may change either pixels or the
                // resulting descriptor. Do not silently share across it.
                return false;
            }

            return true;
        }

        private static bool AreEquivalentAtlasOutputs(
            string leftProperty,
            string rightProperty,
            IReadOnlyCollection<Material> materials,
            IReadOnlyDictionary<
                Material,
                IReadOnlyDictionary<string, Texture>> propertyTextures,
            IReadOnlyDictionary<
                Material,
                IReadOnlyDictionary<string, TextureUVUsage>> uvUsages,
            IReadOnlyDictionary<string, Vector2Int> propertySizes,
            Dictionary<string, TexFineTuningHolder> tuningHolders)
        {
            if (!propertySizes.TryGetValue(leftProperty, out var leftSize)
                || !propertySizes.TryGetValue(rightProperty, out var rightSize)
                || leftSize != rightSize)
            {
                return false;
            }

            foreach (var material in materials)
            {
                Texture? leftTexture = null;
                Texture? rightTexture = null;
                if (propertyTextures.TryGetValue(
                        material, out var properties))
                {
                    properties.TryGetValue(
                        leftProperty, out leftTexture);
                    properties.TryGetValue(
                        rightProperty, out rightTexture);
                }

                if (!ReferenceEquals(leftTexture, rightTexture))
                    return false;

                if (leftTexture == null)
                    continue;

                if (!uvUsages.TryGetValue(material, out var usages)
                    || !usages.TryGetValue(
                        leftProperty, out var leftUsage)
                    || !usages.TryGetValue(
                        rightProperty, out var rightUsage)
                    || !AreEquivalentTextureUvUsages(
                        leftUsage, rightUsage))
                {
                    return false;
                }
            }

            return HaveEquivalentFineTuning(
                tuningHolders[leftProperty],
                tuningHolders[rightProperty],
                leftSize);
        }

        internal static bool AreEquivalentTextureUvUsages(
            TextureUVUsage left,
            TextureUVUsage right)
        {
            if (left.UVChannel != right.UVChannel)
                return false;

            if (left.UVMatrix is not { } leftMatrix
                || right.UVMatrix is not { } rightMatrix)
            {
                // A null matrix means the transform could not be described
                // statically. Equality is not proven even when both are null.
                return false;
            }

            return Mathf.Approximately(leftMatrix.M00, rightMatrix.M00)
                && Mathf.Approximately(leftMatrix.M01, rightMatrix.M01)
                && Mathf.Approximately(leftMatrix.M02, rightMatrix.M02)
                && Mathf.Approximately(leftMatrix.M10, rightMatrix.M10)
                && Mathf.Approximately(leftMatrix.M11, rightMatrix.M11)
                && Mathf.Approximately(leftMatrix.M12, rightMatrix.M12);
        }

        private static bool HaveEquivalentFineTuning(
            TexFineTuningHolder left,
            TexFineTuningHolder right,
            Vector2Int initialSize)
        {
            var leftColorSpace =
                left.Find<ColorSpaceData>()?.AsLinear ?? false;
            var rightColorSpace =
                right.Find<ColorSpaceData>()?.AsLinear ?? false;
            if (leftColorSpace != rightColorSpace)
                return false;

            var leftMipMap = left.Find<MipMapData>();
            var rightMipMap = right.Find<MipMapData>();
            var leftUseMipMap = leftMipMap?.UseMipMap ?? true;
            var rightUseMipMap = rightMipMap?.UseMipMap ?? true;
            if (leftUseMipMap != rightUseMipMap)
                return false;

            var leftMipAlgorithm =
                leftMipMap?.MipMapGenerateAlgorithm
                ?? ITexTransToolForUnity.DS_ALGORITHM_DEFAULT;
            var rightMipAlgorithm =
                rightMipMap?.MipMapGenerateAlgorithm
                ?? ITexTransToolForUnity.DS_ALGORITHM_DEFAULT;
            if (!string.Equals(
                    leftMipAlgorithm,
                    rightMipAlgorithm,
                    StringComparison.Ordinal))
            {
                return false;
            }

            var leftCompression =
                left.Find<TextureCompressionTuningData>();
            var rightCompression =
                right.Find<TextureCompressionTuningData>();

            if ((leftCompression?.FormatQualityValue
                    ?? FormatQuality.Normal)
                    != (rightCompression?.FormatQualityValue
                        ?? FormatQuality.Normal)
                || (leftCompression?.UseOverride ?? false)
                    != (rightCompression?.UseOverride ?? false)
                || (leftCompression?.OverrideTextureFormat
                    ?? TextureFormat.BC7)
                    != (rightCompression?.OverrideTextureFormat
                        ?? TextureFormat.BC7)
                || (leftCompression?.CompressionQuality ?? 50)
                    != (rightCompression?.CompressionQuality ?? 50))
            {
                return false;
            }

            return true;
        }

        public static TexFineTuningResult ProcessingTextureFineTuning(ITexTransToolForUnity engine, Dictionary<string, TexFineTuningHolder> tuningHolder, Dictionary<string, ITTRenderTexture> atlasedTextures)
        {
            var pressers = InterfaceUtility.CreateConcreteAssignableTypeInstances<ITuningProcessor>().ToArray();
            Array.Sort(pressers, (l, r) => l.Order - r.Order);

            var ctx = new TexFineTuningProcessingContext(
                engine
                , tuningHolder
                , atlasedTextures
                , new()
            );

            foreach (var p in pressers) { p.ProcessingTuning(ctx); }

            var resultTextures = new Dictionary<string, ITTRenderTexture>();
            var resultTextureDescriptor = new Dictionary<ITTRenderTexture, TexTransToolTextureDescriptor>();
            var resultSourceProperties = new Dictionary<string, IReadOnlyCollection<string>>();

            foreach (var pHolder in ctx.ProcessingHolder)
            {
                var rtProperty = pHolder.Value.RenderTextureProperty;
                if (rtProperty is null) { continue; }
                if (ctx.RenderTextures.TryGetValue(rtProperty, out var rt) is false) { continue; }
                resultTextures[pHolder.Key] = rt;
                resultSourceProperties[pHolder.Key] = pHolder.Value.SourceProperties.ToArray();

                if (pHolder.Value.RTOwned)
                {
                    resultTextureDescriptor[rt] = pHolder.Value.TextureDescriptor;
                }
            }

            // RTOwned ではないが、 merge Texture などの 子 となるやつが持ってしまっている場合に フォールバック的に適当な TexTransToolTextureDescriptor を割り当てる必要がある。
            foreach (var rt in resultTextures.Values)
            {
                if (resultTextureDescriptor.ContainsKey(rt)) continue;

                resultTextureDescriptor[rt] = ctx.ProcessingHolder.Values.FirstOrDefault(i =>
                {
                    if (i.RenderTextureProperty is null) { return false; }
                    if (ctx.RenderTextures.TryGetValue(i.RenderTextureProperty, out var contextRt))
                    {
                        return rt == contextRt;
                    }
                    return false;
                })?.TextureDescriptor ?? new();
            }

            foreach (var unusedRt in ctx.NewRenderTextures.Where(r => resultTextures.ContainsValue(r) is false))
                unusedRt.Dispose();

            return new(resultTextures, resultTextureDescriptor, resultSourceProperties);
        }
    }

}
