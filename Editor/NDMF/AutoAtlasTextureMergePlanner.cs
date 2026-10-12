#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using net.rs64.TexTransCore;
using net.rs64.TexTransTool.TextureAtlas;
using net.rs64.TexTransTool.TextureAtlas.IslandSizePriorityTuner;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace net.rs64.TexTransTool.NDMF
{
    /// <summary>
    /// Plans optional second-stage AutoAtlas merges without mutating the domain.
    /// The baseline is the result of optimizing each texture-connected atomic
    /// group independently; individual AutoAtlas savings are therefore never
    /// counted as additional merge savings.
    /// </summary>
    internal static class AutoAtlasTextureMergePlanner
    {
        private const int BeamWidth = 24;
        private const int MaxScenarioEnumerationGroups = 12;
        private const double UnknownDemandLog2 = -32d;
        private const double Epsilon = 1e-8;

        internal sealed class PreparedGroup
        {
            internal int Id;
            internal HashSet<Material> Materials = new();
            internal Renderer[] Renderers = Array.Empty<Renderer>();
            internal HashSet<Texture> SourceTextures = new();
            internal long OriginalPixels;

            internal AtlasTexture.AtlasExecutionMetrics? StandaloneMetrics;
            internal TextureCostPlan OriginalCost = TextureCostPlan.Empty;
            internal TextureCostPlan BaselineCost = TextureCostPlan.Empty;
        }

        internal sealed class MergeSelection
        {
            internal readonly int[] GroupIds;
            internal readonly long BaselineBytes;
            internal readonly long MergedBytes;
            internal readonly long BaselinePixels;
            internal readonly long MergedPixels;
            internal readonly float SavingsRate;

            internal MergeSelection(
                IEnumerable<int> groupIds,
                TextureCostPlan baseline,
                TextureCostPlan merged)
            {
                GroupIds = groupIds.OrderBy(id => id).ToArray();
                BaselineBytes = baseline.FullResidentBytes;
                MergedBytes = merged.FullResidentBytes;
                BaselinePixels = baseline.PixelCount;
                MergedPixels = merged.PixelCount;
                SavingsRate = BaselineBytes <= 0
                    ? 0f
                    : (float)(BaselineBytes - MergedBytes) / BaselineBytes;
            }
        }

        internal sealed class PlanResult
        {
            internal readonly IReadOnlyList<MergeSelection> Selections;

            internal PlanResult(IReadOnlyList<MergeSelection> selections)
            {
                Selections = selections;
            }
        }

        private sealed class Candidate
        {
            internal readonly int[] GroupIds;
            internal readonly TextureCostPlan Baseline;
            internal readonly TextureCostPlan Merged;
            internal readonly AtlasTexture.AtlasExecutionMetrics Metrics;
            internal readonly long SavingBytes;
            internal readonly float SavingRate;
            internal readonly bool IsAdoptable;

            internal Candidate(
                int[] groupIds,
                TextureCostPlan baseline,
                TextureCostPlan merged,
                AtlasTexture.AtlasExecutionMetrics metrics,
                float minimumSavingRate,
                RuntimeUsageModel usage)
            {
                GroupIds = groupIds;
                Baseline = baseline;
                Merged = merged;
                Metrics = metrics;
                SavingBytes = baseline.FullResidentBytes - merged.FullResidentBytes;
                SavingRate = baseline.FullResidentBytes <= 0
                    ? 0f
                    : (float)SavingBytes / baseline.FullResidentBytes;

                IsAdoptable =
                    SavingRate >= minimumSavingRate
                    && merged.PixelCount < baseline.PixelCount
                    && merged.FullResidentBytes < baseline.FullResidentBytes
                    && merged.MaxTextureDimension <= baseline.MaxTextureDimension
                    && !HasSamplingRegression(baseline, merged, groupIds)
                    && !HasStreamingRegression(baseline, merged, groupIds, usage);
            }
        }

        private sealed class TextureCostPlan
        {
            internal static readonly TextureCostPlan Empty = new(Array.Empty<TextureCostEntry>());

            internal readonly TextureCostEntry[] Entries;
            internal readonly long FullResidentBytes;
            internal readonly long PixelCount;
            internal readonly int MaxTextureDimension;
            internal readonly Dictionary<int, double> SamplingBytesPerTexel;

            internal TextureCostPlan(
                IEnumerable<TextureCostEntry> entries,
                IReadOnlyDictionary<int, double>? samplingBytesPerTexel = null)
            {
                Entries = entries.ToArray();
                FullResidentBytes = Entries.Sum(entry => entry.FullResidentBytes);
                PixelCount = Entries.Sum(entry => (long)entry.Width * entry.Height);
                MaxTextureDimension = Entries.Length == 0
                    ? 0
                    : Entries.Max(entry => Math.Max(entry.Width, entry.Height));
                SamplingBytesPerTexel = samplingBytesPerTexel == null
                    ? new Dictionary<int, double>()
                    : samplingBytesPerTexel.ToDictionary(pair => pair.Key, pair => pair.Value);
            }

            internal static TextureCostPlan Combine(IEnumerable<TextureCostPlan> plans)
            {
                var planArray = plans.ToArray();
                var sampling = new Dictionary<int, double>();
                foreach (var plan in planArray)
                {
                    foreach (var pair in plan.SamplingBytesPerTexel)
                    {
                        sampling[pair.Key] = sampling.GetValueOrDefault(pair.Key) + pair.Value;
                    }
                }
                return new TextureCostPlan(
                    planArray.SelectMany(plan => plan.Entries),
                    sampling);
            }
        }

        private sealed class TextureCostEntry
        {
            internal readonly string Key;
            internal readonly int Width;
            internal readonly int Height;
            internal readonly TextureFormat Format;
            internal readonly int MipCount;
            internal readonly bool Streaming;
            internal readonly HashSet<int> Contributors;
            internal readonly Dictionary<int, double> DemandLog2;
            internal readonly long FullResidentBytes;

            internal TextureCostEntry(
                string key,
                int width,
                int height,
                TextureFormat format,
                int mipCount,
                bool streaming,
                IEnumerable<int> contributors,
                Dictionary<int, double> demandLog2)
            {
                Key = key;
                Width = width;
                Height = height;
                Format = format;
                MipCount = Math.Max(1, mipCount);
                Streaming = streaming;
                Contributors = contributors.ToHashSet();
                DemandLog2 = demandLog2;
                FullResidentBytes = CalculateBytes(this, 0);
            }
        }

        private sealed class RuntimeUsageModel
        {
            private readonly HashSet<(int A, int B)> _exclusivePairs = new();

            internal void AddExclusive(int a, int b)
            {
                if (a == b) return;
                _exclusivePairs.Add(a < b ? (a, b) : (b, a));
            }

            internal bool AreExclusive(int a, int b)
            {
                if (a == b) return false;
                return _exclusivePairs.Contains(a < b ? (a, b) : (b, a));
            }

            internal IReadOnlyList<HashSet<int>> GetMaximalScenarios(int[] groupIds)
            {
                if (groupIds.Length == 0) return Array.Empty<HashSet<int>>();
                if (groupIds.Length > MaxScenarioEnumerationGroups)
                {
                    // Fallback is conservative: assume everything can overlap.
                    return new[] { groupIds.ToHashSet() };
                }

                var feasible = new List<HashSet<int>>();
                var count = 1 << groupIds.Length;
                for (var mask = 1; mask < count; mask += 1)
                {
                    var scenario = new HashSet<int>();
                    var valid = true;
                    for (var i = 0; i < groupIds.Length && valid; i += 1)
                    {
                        if ((mask & (1 << i)) == 0) continue;
                        var id = groupIds[i];
                        if (scenario.Any(existing => AreExclusive(existing, id)))
                        {
                            valid = false;
                            break;
                        }
                        scenario.Add(id);
                    }
                    if (valid) feasible.Add(scenario);
                }

                // Only maximal scenarios can produce a larger resident set.
                return feasible
                    .Where(candidate => !feasible.Any(other =>
                        other.Count > candidate.Count && candidate.IsSubsetOf(other)))
                    .ToArray();
            }
        }

        private readonly struct UsageGate : IEquatable<UsageGate>
        {
            internal readonly string Path;
            internal readonly Type Type;
            internal readonly string Property;

            internal UsageGate(string path, Type type, string property)
            {
                Path = path;
                Type = type;
                Property = property;
            }

            internal bool Matches(EditorCurveBinding binding)
            {
                return binding.path == Path
                    && binding.type == Type
                    && binding.propertyName == Property;
            }

            public bool Equals(UsageGate other)
            {
                return Path == other.Path && Type == other.Type && Property == other.Property;
            }

            public override bool Equals(object? obj)
            {
                return obj is UsageGate other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    var hash = 17;
                    hash = hash * 31 + Path.GetHashCode();
                    hash = hash * 31 + Type.GetHashCode();
                    hash = hash * 31 + Property.GetHashCode();
                    return hash;
                }
            }
        }

        internal static PlanResult Plan(
            IDomain domain,
            AutoAtlasTexture configuration,
            AnimatorServicesContext animatorServices,
            AtlasSetting settings,
            IReadOnlyDictionary<Material, IReadOnlyDictionary<string, Texture>> propertyTextures,
            IReadOnlyList<PreparedGroup> groups)
        {
            if (!configuration.EnableAdditionalAtlasMerging || groups.Count < 2)
                return new PlanResult(Array.Empty<MergeSelection>());

            var engine = domain.GetTexTransCoreEngineForUnity();
            var alphaCache = new Dictionary<Texture, bool>();

            foreach (var group in groups)
            {
                group.OriginalCost = BuildOriginalCost(
                    domain, group, propertyTextures);
                group.StandaloneMetrics = AnalyzeAtlas(
                    domain, engine, settings, group.Materials, group.Renderers);

                if (group.StandaloneMetrics is { } standalone
                    && standalone.OutputTexturePixelCount > 0
                    && standalone.OutputTexturePixelCount < group.OriginalPixels)
                {
                    group.BaselineCost = BuildGeneratedCost(
                        domain,
                        new[] { group },
                        propertyTextures,
                        standalone.PropertySizes,
                        alphaCache);
                }
                else
                {
                    group.BaselineCost = group.OriginalCost;
                }
            }

            var usage = AnalyzeRuntimeUsage(animatorServices, groups);
            var byId = groups.ToDictionary(group => group.Id);
            var adoptable = new Dictionary<string, Candidate>();
            var mergedPlanCache = new Dictionary<
                string,
                (AtlasTexture.AtlasExecutionMetrics Metrics, TextureCostPlan Cost)>();

            foreach (var host in groups.Where(group =>
                         group.StandaloneMetrics is { } metrics
                         && metrics.FreeAreaFraction >=
                         configuration.MinimumMergeHostFreeAreaRatio))
            {
                var frontier = new List<int[]> { new[] { host.Id } };
                var seen = new HashSet<string> { host.Id.ToString() };

                while (frontier.Count != 0)
                {
                    var expanded = new List<Candidate>();
                    foreach (var state in frontier)
                    {
                        foreach (var add in groups)
                        {
                            if (state.Contains(add.Id)) continue;

                            var ids = state.Append(add.Id).OrderBy(id => id).ToArray();
                            var key = string.Join(",", ids);
                            if (!seen.Add(key)) continue;

                            AtlasTexture.AtlasExecutionMetrics metrics;
                            TextureCostPlan merged;
                            if (mergedPlanCache.TryGetValue(key, out var cached))
                            {
                                metrics = cached.Metrics;
                                merged = cached.Cost;
                            }
                            else
                            {
                                var materials = ids.SelectMany(id => byId[id].Materials)
                                    .ToHashSet();
                                var renderers = ids.SelectMany(id => byId[id].Renderers)
                                    .Distinct().ToArray();
                                var analyzed = AnalyzeAtlas(
                                    domain, engine, settings, materials, renderers);
                                if (analyzed is null) continue;

                                metrics = analyzed.Value;
                                merged = BuildGeneratedCost(
                                    domain,
                                    ids.Select(id => byId[id]).ToArray(),
                                    propertyTextures,
                                    metrics.PropertySizes,
                                    alphaCache);
                                mergedPlanCache[key] = (metrics, merged);
                            }

                            var baseline = TextureCostPlan.Combine(
                                ids.Select(id => byId[id].BaselineCost));

                            var candidate = new Candidate(
                                ids,
                                baseline,
                                merged,
                                metrics,
                                configuration.MinimumAdditionalMergeSavingsRate,
                                usage);
                            expanded.Add(candidate);

                            if (candidate.IsAdoptable)
                            {
                                if (!adoptable.TryGetValue(key, out var prior)
                                    || candidate.SavingBytes > prior.SavingBytes)
                                {
                                    adoptable[key] = candidate;
                                }
                            }
                        }
                    }

                    // Do not prune using the final adoption threshold: a three-or-more
                    // group merge may be good even when every pair is below threshold.
                    frontier = expanded
                        .OrderBy(candidate => candidate.Merged.FullResidentBytes)
                        .ThenByDescending(candidate => candidate.Metrics.FreeAreaFraction)
                        .ThenByDescending(candidate => candidate.GroupIds.Length)
                        .Take(BeamWidth)
                        .Select(candidate => candidate.GroupIds)
                        .ToList();
                }
            }

            var selected = new List<MergeSelection>();
            var consumed = new HashSet<int>();
            foreach (var candidate in adoptable.Values
                         .OrderByDescending(candidate => candidate.SavingBytes)
                         .ThenByDescending(candidate => candidate.SavingRate)
                         .ThenByDescending(candidate => candidate.GroupIds.Length))
            {
                if (candidate.GroupIds.Any(consumed.Contains)) continue;
                selected.Add(new MergeSelection(
                    candidate.GroupIds, candidate.Baseline, candidate.Merged));
                consumed.UnionWith(candidate.GroupIds);
            }

            return new PlanResult(selected);
        }

        private static AtlasTexture.AtlasExecutionMetrics? AnalyzeAtlas(
            IDomain domain,
            ITexTransToolForUnity engine,
            AtlasSetting settings,
            HashSet<Material> materials,
            Renderer[] renderers)
        {
            AtlasTexture.AtlasExecutionMetrics? captured = null;
            var result = AtlasTexture.DoAtlasTexture(
                domain,
                engine,
                materials,
                renderers,
                new List<IIslandSizePriorityTuner?>(),
                settings,
                new AtlasTexture.AtlasExecutionOptions
                {
                    RequireLossless = true,
                    ReportProgressInfo = false,
                    AcceptAtlasSize = (_, _, metrics) =>
                    {
                        captured = metrics;
                        // Planning stops before mesh/texture generation.
                        return false;
                    },
                });

            if (result.AtlasContext != null)
                result.AtlasContext.Dispose();

            return captured;
        }

        private static TextureCostPlan BuildOriginalCost(
            IDomain domain,
            PreparedGroup group,
            IReadOnlyDictionary<Material, IReadOnlyDictionary<string, Texture>> propertyTextures)
        {
            var entries = new List<TextureCostEntry>();
            foreach (var texture in group.SourceTextures)
            {
                var format = GetSourceFormat(texture);
                var mipCount = texture is Texture2D mipTexture
                    ? Math.Max(1, mipTexture.mipmapCount)
                    : 1;
                var streaming = texture is Texture2D streamTexture
                    && streamTexture.streamingMipmaps
                    && mipCount > 1;

                var demands = new Dictionary<int, double>
                {
                    [group.Id] = FindTextureDemandLog2(
                        domain, group, texture, propertyTextures),
                };

                entries.Add(new TextureCostEntry(
                    "source:" + texture.GetInstanceID(),
                    texture.width,
                    texture.height,
                    format,
                    mipCount,
                    streaming,
                    new[] { group.Id },
                    demands));
            }

            var propertySamplingCosts = group.Materials
                .Where(propertyTextures.ContainsKey)
                .SelectMany(material => propertyTextures[material])
                .Where(pair => pair.Value != null)
                .GroupBy(pair => pair.Key)
                .Select(propertyGroup => propertyGroup
                    .Max(pair => GetBytesPerTexel(GetSourceFormat(pair.Value))))
                .Sum();

            return new TextureCostPlan(
                entries,
                new Dictionary<int, double>
                {
                    [group.Id] = propertySamplingCosts,
                });
        }

        private static TextureCostPlan BuildGeneratedCost(
            IDomain domain,
            IReadOnlyList<PreparedGroup> groups,
            IReadOnlyDictionary<Material, IReadOnlyDictionary<string, Texture>> propertyTextures,
            IReadOnlyDictionary<string, Vector2Int> propertySizes,
            Dictionary<Texture, bool> alphaCache)
        {
            var entries = new List<TextureCostEntry>();
            var sampling = groups.ToDictionary(group => group.Id, _ => 0d);
            var hasMain = propertySizes.ContainsKey("_MainTex");

            foreach (var property in propertySizes.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (hasMain && (property.Key == "_BaseMap" || property.Key == "_BaseColorMap"))
                {
                    // Default ReferenceCopy aliases these properties to _MainTex.
                    continue;
                }

                var usageProperties = hasMain && property.Key == "_MainTex"
                    ? new[] { "_MainTex", "_BaseMap", "_BaseColorMap" }
                    : new[] { property.Key };

                var contributors = new HashSet<int>();
                var pixelSources = new HashSet<Texture>();
                var demand = new Dictionary<int, double>();
                var usedPropertyCount = new Dictionary<int, int>();

                foreach (var group in groups)
                {
                    var rootSources = group.Materials
                        .Where(propertyTextures.ContainsKey)
                        .SelectMany(material =>
                            propertyTextures[material].TryGetValue(property.Key, out var texture)
                                && texture != null
                                ? new[] { texture }
                                : Array.Empty<Texture>())
                        .ToHashSet();
                    pixelSources.UnionWith(rootSources);

                    var groupDemand = double.PositiveInfinity;
                    var groupUsedProperties = 0;
                    foreach (var usageProperty in usageProperties)
                    {
                        var usageSources = group.Materials
                            .Where(propertyTextures.ContainsKey)
                            .SelectMany(material =>
                                propertyTextures[material].TryGetValue(
                                    usageProperty, out var texture)
                                    && texture != null
                                    ? new[] { texture }
                                    : Array.Empty<Texture>())
                            .ToHashSet();

                        if (usageSources.Count == 0) continue;
                        groupUsedProperties += 1;
                        groupDemand = Math.Min(
                            groupDemand,
                            FindPropertyDemandLog2(
                                domain,
                                group,
                                usageProperty,
                                usageSources,
                                propertyTextures));
                    }

                    if (groupUsedProperties == 0) continue;

                    contributors.Add(group.Id);
                    usedPropertyCount[group.Id] = groupUsedProperties;
                    demand[group.Id] = double.IsPositiveInfinity(groupDemand)
                        ? UnknownDemandLog2
                        : groupDemand;
                }

                if (contributors.Count == 0) continue;

                var hasAlpha = pixelSources.Count == 0
                    || pixelSources.Any(texture => HasAlpha(texture, alphaCache));
                var quality = property.Key is "_BumpMap" or "_Bump2ndMap"
                    ? FormatQuality.High
                    : FormatQuality.Normal;
                var format = TextureCompressionData.GetQuality2TextureFormat(
                    quality, hasAlpha);
                var bytesPerTexel = GetBytesPerTexel(format);

                foreach (var pair in usedPropertyCount)
                    sampling[pair.Key] += bytesPerTexel * pair.Value;

                entries.Add(new TextureCostEntry(
                    "generated:" + property.Key,
                    property.Value.x,
                    property.Value.y,
                    format,
                    CalculateMipCount(property.Value.x, property.Value.y),
                    streaming: true,
                    contributors,
                    demand));
            }

            return new TextureCostPlan(entries, sampling);
        }

        private static bool HasAlpha(Texture texture, Dictionary<Texture, bool> cache)
        {
            if (cache.TryGetValue(texture, out var cached)) return cached;

            if (texture is not Texture2D texture2D)
                return cache[texture] = true;

            using var alpha = TextureCompressionData.HasAlphaChannel(texture2D);
            return cache[texture] = alpha.GetResult();
        }

        private static int CalculateMipCount(int width, int height)
        {
            var count = 1;
            while (width > 1 || height > 1)
            {
                width = Math.Max(1, width >> 1);
                height = Math.Max(1, height >> 1);
                count += 1;
            }
            return count;
        }

        private static long CalculateBytes(TextureCostEntry entry, int firstMip)
        {
            firstMip = Math.Max(0, Math.Min(firstMip, entry.MipCount - 1));
            long total = 0;
            var width = entry.Width;
            var height = entry.Height;

            for (var mip = 0; mip < entry.MipCount; mip += 1)
            {
                if (mip >= firstMip)
                {
                    try
                    {
                        var blockWidth = Math.Max(1u,
                            GraphicsFormatUtility.GetBlockWidth(entry.Format));
                        var blockHeight = Math.Max(1u,
                            GraphicsFormatUtility.GetBlockHeight(entry.Format));
                        var blockSize = Math.Max(1u,
                            GraphicsFormatUtility.GetBlockSize(entry.Format));
                        var blocksX = ((long)width + blockWidth - 1) / blockWidth;
                        var blocksY = ((long)height + blockHeight - 1) / blockHeight;
                        total += blocksX * blocksY * blockSize;
                    }
                    catch
                    {
                        // Conservative fallback for unusual source formats.
                        total += (long)width * height * 4;
                    }
                }

                width = Math.Max(1, width >> 1);
                height = Math.Max(1, height >> 1);
            }

            return total;
        }

        private static TextureFormat GetSourceFormat(Texture texture)
        {
            return texture is Texture2D texture2D
                ? texture2D.format
                : TextureFormat.RGBA32;
        }

        private static double GetBytesPerTexel(TextureFormat format)
        {
            try
            {
                var blockWidth = Math.Max(1u,
                    GraphicsFormatUtility.GetBlockWidth(format));
                var blockHeight = Math.Max(1u,
                    GraphicsFormatUtility.GetBlockHeight(format));
                var blockSize = Math.Max(1u,
                    GraphicsFormatUtility.GetBlockSize(format));
                return (double)blockSize / (blockWidth * blockHeight);
            }
            catch
            {
                return 4d;
            }
        }

        private static bool HasSamplingRegression(
            TextureCostPlan baseline,
            TextureCostPlan merged,
            IEnumerable<int> groupIds)
        {
            foreach (var id in groupIds)
            {
                var before = baseline.SamplingBytesPerTexel.GetValueOrDefault(id);
                var after = merged.SamplingBytesPerTexel.GetValueOrDefault(id);
                if (after > before + 1e-9d)
                    return true;
            }
            return false;
        }

        private static bool HasStreamingRegression(
            TextureCostPlan baseline,
            TextureCostPlan merged,
            int[] groupIds,
            RuntimeUsageModel usage)
        {
            var scenarios = usage.GetMaximalScenarios(groupIds);
            if (scenarios.Count == 0) return false;

            // Camera distance/FOV are intentionally represented as a normalized
            // logarithmic bias. Sweeping the range catches relative mip-demand
            // regressions without assuming one particular viewing distance.
            for (var biasStep = -32; biasStep <= 40; biasStep += 1)
            {
                var bias = biasStep * 0.5d;
                foreach (var scenario in scenarios)
                {
                    var baselineBytes = CalculateScenarioBytes(
                        baseline, scenario, bias);
                    var mergedBytes = CalculateScenarioBytes(
                        merged, scenario, bias);
                    if (mergedBytes > baselineBytes)
                        return true;
                }
            }

            return false;
        }

        private static long CalculateScenarioBytes(
            TextureCostPlan plan,
            HashSet<int> activeGroups,
            double cameraBias)
        {
            long total = 0;
            foreach (var entry in plan.Entries)
            {
                var activeContributors = entry.Contributors
                    .Where(activeGroups.Contains)
                    .ToArray();
                if (activeContributors.Length == 0) continue;

                if (!entry.Streaming || entry.MipCount <= 1)
                {
                    total += entry.FullResidentBytes;
                    continue;
                }

                var requiredMip = entry.MipCount - 1;
                foreach (var contributor in activeContributors)
                {
                    var logC = entry.DemandLog2.TryGetValue(
                        contributor, out var value)
                        ? value
                        : UnknownDemandLog2;
                    var desiredMip = (int)Math.Floor(logC + cameraBias);
                    desiredMip = Math.Max(0,
                        Math.Min(entry.MipCount - 1, desiredMip));
                    requiredMip = Math.Min(requiredMip, desiredMip);
                }

                total += CalculateBytes(entry, requiredMip);
            }
            return total;
        }

        private static double FindTextureDemandLog2(
            IDomain domain,
            PreparedGroup group,
            Texture texture,
            IReadOnlyDictionary<Material, IReadOnlyDictionary<string, Texture>> propertyTextures)
        {
            var values = new List<double>();
            foreach (var material in group.Materials)
            {
                if (!propertyTextures.TryGetValue(material, out var properties))
                    continue;
                foreach (var property in properties)
                {
                    if (!ReferenceEquals(property.Value, texture)) continue;
                    values.Add(FindPropertyDemandLog2(
                        domain, group, property.Key,
                        new HashSet<Texture> { texture }, propertyTextures));
                }
            }
            return values.Count == 0 ? UnknownDemandLog2 : values.Min();
        }

        private static double FindPropertyDemandLog2(
            IDomain domain,
            PreparedGroup group,
            string property,
            HashSet<Texture> textures,
            IReadOnlyDictionary<Material, IReadOnlyDictionary<string, Texture>> propertyTextures)
        {
            var best = double.PositiveInfinity;

            foreach (var renderer in group.Renderers)
            {
                var mesh = domain.GetMesh(renderer);
                if (mesh == null || mesh.subMeshCount == 0) continue;
                var materials = domain.GetMaterials(renderer);

                for (var subMesh = 0;
                     subMesh < materials.Length && subMesh < mesh.subMeshCount;
                     subMesh += 1)
                {
                    if (materials[subMesh] is not Material material
                        || !group.Materials.Contains(material)
                        || !propertyTextures.TryGetValue(material, out var properties)
                        || !properties.TryGetValue(property, out var texture)
                        || texture == null
                        || !textures.Contains(texture))
                    {
                        continue;
                    }

                    var metric = CalculateUvDistributionMetric(
                        mesh, subMesh, renderer.transform.lossyScale,
                        material.GetTextureScale(property));
                    if (!(metric > Epsilon) || double.IsNaN(metric)
                        || double.IsInfinity(metric))
                    {
                        continue;
                    }

                    var texelCount = (double)texture.width * texture.height;
                    var logC = 0.5d * Math.Log(texelCount / metric, 2d);
                    best = Math.Min(best, logC);
                }
            }

            return double.IsPositiveInfinity(best) ? UnknownDemandLog2 : best;
        }

        private static double CalculateUvDistributionMetric(
            Mesh mesh,
            int subMesh,
            Vector3 lossyScale,
            Vector2 textureScale)
        {
            var vertices = mesh.vertices;
            var uv = mesh.uv;
            if (uv == null || uv.Length != vertices.Length) return 0d;

            var triangles = mesh.GetTriangles(subMesh);
            double worldArea = 0d;
            double uvArea = 0d;
            var scale = new Vector3(
                Math.Abs(lossyScale.x),
                Math.Abs(lossyScale.y),
                Math.Abs(lossyScale.z));

            for (var index = 0; index + 2 < triangles.Length; index += 3)
            {
                var i0 = triangles[index];
                var i1 = triangles[index + 1];
                var i2 = triangles[index + 2];

                var p0 = Vector3.Scale(vertices[i0], scale);
                var p1 = Vector3.Scale(vertices[i1], scale);
                var p2 = Vector3.Scale(vertices[i2], scale);
                var triangleWorldArea =
                    Vector3.Cross(p1 - p0, p2 - p0).magnitude * 0.5d;

                var uv0 = uv[i0];
                var uv1 = uv[i1];
                var uv2 = uv[i2];
                var a = uv1 - uv0;
                var b = uv2 - uv0;
                var triangleUvArea =
                    Math.Abs((double)a.x * b.y - (double)a.y * b.x) * 0.5d;

                if (triangleWorldArea <= Epsilon || triangleUvArea <= Epsilon)
                    continue;

                worldArea += triangleWorldArea;
                uvArea += triangleUvArea;
            }

            if (worldArea <= Epsilon || uvArea <= Epsilon) return 0d;

            var metric = worldArea / uvArea;
            var textureScaleArea = Math.Abs(
                (double)textureScale.x * textureScale.y);
            if (textureScaleArea > Epsilon)
                metric /= textureScaleArea;

            return metric;
        }

        private static RuntimeUsageModel AnalyzeRuntimeUsage(
            AnimatorServicesContext animatorServices,
            IReadOnlyList<PreparedGroup> groups)
        {
            var model = new RuntimeUsageModel();
            var gateByGroup = groups.ToDictionary(
                group => group.Id,
                group => FindCommonUsageGate(animatorServices, group));

            var layers = animatorServices.ControllerContext.Controllers.Values
                .Distinct()
                .SelectMany(controller => controller.Layers)
                .Where(layer => layer.StateMachine != null)
                .ToArray();

            for (var leftIndex = 0; leftIndex < groups.Count; leftIndex += 1)
            {
                var left = groups[leftIndex];
                if (gateByGroup[left.Id] is not UsageGate leftGate) continue;

                for (var rightIndex = leftIndex + 1;
                     rightIndex < groups.Count;
                     rightIndex += 1)
                {
                    var right = groups[rightIndex];
                    if (gateByGroup[right.Id] is not UsageGate rightGate
                        || leftGate.Equals(rightGate))
                    {
                        continue;
                    }

                    var leftLayers = layers
                        .Where(layer => LayerContainsBinding(layer, leftGate))
                        .ToArray();
                    var rightLayers = layers
                        .Where(layer => LayerContainsBinding(layer, rightGate))
                        .ToArray();

                    if (leftLayers.Length != 1
                        || rightLayers.Length != 1
                        || !ReferenceEquals(leftLayers[0], rightLayers[0]))
                    {
                        continue;
                    }

                    if (LayerProvesMutualExclusion(
                            leftLayers[0], leftGate, rightGate))
                    {
                        model.AddExclusive(left.Id, right.Id);
                    }
                }
            }

            return model;
        }

        private static UsageGate? FindCommonUsageGate(
            AnimatorServicesContext animatorServices,
            PreparedGroup group)
        {
            UsageGate? common = null;
            foreach (var renderer in group.Renderers)
            {
                var gate = FindNearestUsageGate(animatorServices, renderer);
                if (gate is null) return null;
                if (common is null) common = gate;
                else if (!common.Value.Equals(gate.Value)) return null;
            }
            return common;
        }

        private static UsageGate? FindNearestUsageGate(
            AnimatorServicesContext animatorServices,
            Renderer renderer)
        {
            var index = animatorServices.AnimationIndex;
            var remapper = animatorServices.ObjectPathRemapper;

            // Renderer.enabled is the most local gate.
            var rendererPath = remapper.GetVirtualPathForObject(renderer.gameObject);
            var rendererGate = new UsageGate(
                rendererPath, renderer.GetType(), "m_Enabled");
            if (BindingIsAnimated(index, rendererGate))
                return rendererGate;

            for (var transform = renderer.transform;
                 transform != null;
                 transform = transform.parent)
            {
                var path = remapper.GetVirtualPathForObject(transform.gameObject);
                var gate = new UsageGate(path, typeof(GameObject), "m_IsActive");
                if (BindingIsAnimated(index, gate))
                    return gate;
            }

            return null;
        }

        private static bool BindingIsAnimated(
            AnimationIndex index,
            UsageGate gate)
        {
            return index.GetClipsForObjectPath(gate.Path)
                .Any(clip => clip.GetFloatCurveBindings().Any(gate.Matches));
        }

        private static bool LayerContainsBinding(
            VirtualLayer layer,
            UsageGate gate)
        {
            return layer.StateMachine?.AllStates()
                .Any(state => MotionContainsBinding(state.Motion, gate))
                ?? false;
        }

        private static bool MotionContainsBinding(
            VirtualMotion? motion,
            UsageGate gate)
        {
            switch (motion)
            {
                case null:
                    return false;
                case VirtualClip clip:
                    return clip.GetFloatCurveBindings().Any(gate.Matches);
                case VirtualBlendTree tree:
                    return tree.Children.Any(child =>
                        MotionContainsBinding(child.Motion, gate));
                default:
                    return false;
            }
        }

        private static bool LayerProvesMutualExclusion(
            VirtualLayer layer,
            UsageGate left,
            UsageGate right)
        {
            if (layer.SyncedLayerIndex >= 0 || layer.StateMachine is null)
                return false;

            if (!AllTransitionsAreInstant(layer.StateMachine))
                return false;

            var states = layer.StateMachine.AllStates().ToArray();
            if (states.Length == 0) return false;

            var sawLeft = false;
            var sawRight = false;
            foreach (var state in states)
            {
                if (!TryGetConstantBoolean(state.Motion, left, out var leftValue)
                    || !TryGetConstantBoolean(state.Motion, right, out var rightValue))
                {
                    return false;
                }

                sawLeft |= leftValue;
                sawRight |= rightValue;
                if (leftValue && rightValue) return false;
            }

            return sawLeft && sawRight;
        }

        private static bool AllTransitionsAreInstant(VirtualStateMachine root)
        {
            foreach (var stateMachine in EnumerateStateMachines(root))
            {
                if (stateMachine.AnyStateTransitions.Any(
                        transition => transition.Duration > 1e-5f))
                {
                    return false;
                }

                foreach (var state in stateMachine.States)
                {
                    if (state.State != null
                        && state.State.Transitions.Any(
                            transition => transition.Duration > 1e-5f))
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        private static IEnumerable<VirtualStateMachine> EnumerateStateMachines(
            VirtualStateMachine root)
        {
            yield return root;
            foreach (var child in root.StateMachines)
            {
                foreach (var nested in EnumerateStateMachines(child.StateMachine))
                    yield return nested;
            }
        }

        private static bool TryGetConstantBoolean(
            VirtualMotion? motion,
            UsageGate gate,
            out bool value)
        {
            value = false;
            switch (motion)
            {
                case VirtualClip clip:
                {
                    var binding = clip.GetFloatCurveBindings()
                        .FirstOrDefault(gate.Matches);
                    if (string.IsNullOrEmpty(binding.propertyName))
                        return false;

                    var curve = clip.GetFloatCurve(binding);
                    if (curve == null || curve.length == 0) return false;

                    var first = curve.keys[0].value > 0.5f;
                    if (curve.keys.Any(key => (key.value > 0.5f) != first))
                        return false;

                    value = first;
                    return true;
                }
                case VirtualBlendTree tree:
                {
                    bool? common = null;
                    foreach (var child in tree.Children)
                    {
                        if (!TryGetConstantBoolean(
                                child.Motion, gate, out var childValue))
                            return false;

                        if (common.HasValue && common.Value != childValue)
                            return false;
                        common = childValue;
                    }

                    if (!common.HasValue) return false;
                    value = common.Value;
                    return true;
                }
                default:
                    return false;
            }
        }
    }
}
