#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using net.rs64.TexTransCore;
using net.rs64.TexTransCore.UVIsland;
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
        private const int MaxMergeGroupCount = 12;
        // Candidate exploration below never calls DoAtlasTexture. These limits
        // only bound cheap integer-DP state retention, not expensive atlas probes.
        private const int BandStatesPerHeight = 8;
        private const int BandCandidatesPerHost = 512;
        private const int MaxRuntimeUsageScenarios = 64;
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
            internal int BandWidthPixels;
            internal int BandUsedHeightPixels;
            internal readonly Dictionary<string, double> PropertyDemandLog2 =
                new(StringComparer.Ordinal);
            internal readonly Dictionary<string, int> PropertyUseCount =
                new(StringComparer.Ordinal);
            internal TextureCostPlan BaselineCost = TextureCostPlan.Empty;
            internal bool BaselineCostKnown;
        }

        internal sealed class MergeSelection
        {
            internal readonly int[] GroupIds;
            internal readonly long BaselineBytes;
            internal readonly Vector2Int PlannedLayoutSize;

            internal MergeSelection(
                IEnumerable<int> groupIds,
                TextureCostPlan baseline,
                Vector2Int plannedLayoutSize)
            {
                GroupIds = groupIds.OrderBy(id => id).ToArray();
                BaselineBytes = baseline.FullResidentBytes;
                PlannedLayoutSize = plannedLayoutSize;
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

        private sealed class BandState
        {
            internal readonly int UsedHeightPixels;
            internal readonly int[] GuestIds;
            internal readonly long GuestBaselineBytes;

            internal BandState(
                int usedHeightPixels,
                IEnumerable<int> guestIds,
                long guestBaselineBytes)
            {
                UsedHeightPixels = usedHeightPixels;
                GuestIds = guestIds.OrderBy(id => id).ToArray();
                GuestBaselineBytes = guestBaselineBytes;
            }

            internal string Key => string.Join(",", GuestIds);
        }

        private enum UnknownAlphaAssumption
        {
            AssumeAbsent,
            AssumePresent,
        }

        private sealed class Candidate
        {
            internal readonly int[] GroupIds;
            internal readonly TextureCostPlan Baseline;
            internal readonly Vector2Int LayoutSize;
            internal readonly long SavingBytes;
            internal readonly float SavingRate;
            internal readonly bool IsAdoptable;

            internal Candidate(
                int[] groupIds,
                TextureCostPlan baseline,
                TextureCostPlan merged,
                Vector2Int layoutSize,
                RuntimeUsageModel usage)
            {
                GroupIds = groupIds;
                Baseline = baseline;
                LayoutSize = layoutSize;
                SavingBytes = baseline.FullResidentBytes - merged.FullResidentBytes;
                SavingRate = baseline.FullResidentBytes <= 0
                    ? 0f
                    : (float)SavingBytes / baseline.FullResidentBytes;

                IsAdoptable =
                    merged.PixelCount < baseline.PixelCount
                    && merged.FullResidentBytes < baseline.FullResidentBytes
                    && merged.MaxTextureDimension
                        <= baseline.MaxTextureDimension;

                if (IsAdoptable
                    && HasSamplingRegression(baseline, merged, groupIds))
                {
                    IsAdoptable = false;
                }

                if (IsAdoptable
                    && HasStreamingRegression(baseline, merged, groupIds, usage))
                {
                    IsAdoptable = false;
                }
            }
        }

        internal sealed class TextureCostPlan
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

        internal sealed class TextureCostEntry
        {
            internal readonly int Width;
            internal readonly int Height;
            internal readonly uint BlockWidth;
            internal readonly uint BlockHeight;
            internal readonly uint BlockSize;
            internal readonly int MipCount;
            internal readonly bool Streaming;
            internal readonly HashSet<int> Contributors;
            internal readonly Dictionary<int, double> DemandLog2;
            internal readonly long FullResidentBytes;

            internal TextureCostEntry(
                int width,
                int height,
                TextureFormat format,
                int mipCount,
                bool streaming,
                IEnumerable<int> contributors,
                Dictionary<int, double> demandLog2)
                : this(
                    width,
                    height,
                    GetBlockLayout(format),
                    mipCount,
                    streaming,
                    contributors,
                    demandLog2)
            {
            }

            private TextureCostEntry(
                int width,
                int height,
                (uint Width, uint Height, uint Size) block,
                int mipCount,
                bool streaming,
                IEnumerable<int> contributors,
                Dictionary<int, double> demandLog2)
            {
                Width = width;
                Height = height;
                BlockWidth = block.Width;
                BlockHeight = block.Height;
                BlockSize = block.Size;
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
                    return Array.Empty<HashSet<int>>();

                // Maximal feasible active sets are maximal cliques in the
                // compatibility graph (an edge means "can overlap"). Enumerating
                // only maximal cliques avoids generating every one of the 2^N
                // subsets first. In the common case where nothing is proven
                // exclusive this returns exactly one scenario: all groups active.
                var results = new List<HashSet<int>>();
                var all = groupIds.OrderBy(id => id).ToHashSet();

                HashSet<int> CompatibleWith(int id)
                {
                    return all.Where(other =>
                        other != id && !AreExclusive(id, other)).ToHashSet();
                }

                void Search(
                    HashSet<int> current,
                    HashSet<int> possible,
                    HashSet<int> excluded)
                {
                    if (results.Count > MaxRuntimeUsageScenarios) return;

                    if (possible.Count == 0 && excluded.Count == 0)
                    {
                        results.Add(new HashSet<int>(current));
                        return;
                    }

                    var pivot = possible.Concat(excluded)
                        .OrderByDescending(id =>
                            possible.Count(candidate =>
                                candidate != id && !AreExclusive(id, candidate)))
                        .ThenBy(id => id)
                        .FirstOrDefault();

                    var pivotNeighbors = (possible.Count != 0 || excluded.Count != 0)
                        ? CompatibleWith(pivot)
                        : new HashSet<int>();

                    foreach (var vertex in possible
                                 .Where(id => !pivotNeighbors.Contains(id))
                                 .OrderBy(id => id)
                                 .ToArray())
                    {
                        var neighbors = CompatibleWith(vertex);
                        var nextCurrent = new HashSet<int>(current) { vertex };
                        var nextPossible = possible.Where(neighbors.Contains).ToHashSet();
                        var nextExcluded = excluded.Where(neighbors.Contains).ToHashSet();

                        Search(nextCurrent, nextPossible, nextExcluded);
                        possible.Remove(vertex);
                        excluded.Add(vertex);

                        if (results.Count > MaxRuntimeUsageScenarios) return;
                    }
                }

                Search(new HashSet<int>(), new HashSet<int>(all), new HashSet<int>());

                // Too many distinct runtime scenarios means the cost model is too
                // uncertain/expensive for automatic merging. Returning no scenario
                // makes the caller reject the merge conservatively.
                return results.Count > MaxRuntimeUsageScenarios
                    ? Array.Empty<HashSet<int>>()
                    : results;
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
            AnimatorServicesContext? animatorServices,
            AtlasSetting settings,
            IReadOnlyDictionary<Material, IReadOnlyDictionary<string, Texture>> propertyTextures,
            IReadOnlyList<PreparedGroup> groups)
        {
            if (!configuration.EnableAdditionalAtlasMerging
                || animatorServices == null
                || groups.Count < 2)
            {
                return new PlanResult(Array.Empty<MergeSelection>());
            }

            var engine = domain.GetTexTransCoreEngineForUnity();
            var alphaCache = new Dictionary<Texture, bool>();

            foreach (var group in groups)
            {
                group.StandaloneMetrics = AnalyzeAtlas(
                    domain, engine, settings, group.Materials, group.Renderers);
            }

            foreach (var group in groups)
            {
                if (group.StandaloneMetrics is not { } metrics)
                    continue;

                group.BandWidthPixels = Math.Max(1, metrics.LayoutSize.x);
                group.BandUsedHeightPixels = GetBandUsedHeightPixels(metrics);

                foreach (var property in group.Materials
                             .Where(propertyTextures.ContainsKey)
                             .SelectMany(material => propertyTextures[material].Keys)
                             .Distinct(StringComparer.Ordinal))
                {
                    var usageSources = group.Materials
                        .Where(propertyTextures.ContainsKey)
                        .SelectMany(material =>
                            propertyTextures[material].TryGetValue(
                                property, out var texture)
                                && texture != null
                                ? new[] { texture }
                                : Array.Empty<Texture>())
                        .ToHashSet();

                    if (usageSources.Count == 0) continue;

                    group.PropertyUseCount[property] = 1;
                    group.PropertyDemandLog2[property] =
                        FindPropertyDemandLog2(
                            domain,
                            group,
                            property,
                            usageSources,
                            propertyTextures);
                }

                if (metrics.OutputTexturePixelCount > 0
                    && metrics.OutputTexturePixelCount < group.OriginalPixels)
                {
                    group.BaselineCost = BuildGeneratedCost(
                        new[] { group },
                        propertyTextures,
                        metrics.PropertySizes,
                        alphaCache,
                        UnknownAlphaAssumption.AssumeAbsent);
                    group.BaselineCostKnown = true;
                }
                else
                {
                    var originalCost = TryBuildOriginalCost(
                        domain, group, propertyTextures, alphaCache);
                    if (originalCost != null)
                    {
                        group.BaselineCost = originalCost;
                        group.BaselineCostKnown = true;
                    }
                }
            }

            bool CanHostAnyGuest(PreparedGroup host)
            {
                if (host.StandaloneMetrics == null
                    || !host.BaselineCostKnown
                    || host.BandWidthPixels <= 0
                    || host.BandUsedHeightPixels <= 0)
                {
                    return false;
                }

                var capacity = host.BandWidthPixels - host.BandUsedHeightPixels;
                if (capacity <= 0) return false;

                return groups.Any(guest =>
                    guest.Id != host.Id
                    && guest.StandaloneMetrics != null
                    && guest.BaselineCostKnown
                    && guest.BandWidthPixels > 0
                    && guest.BandWidthPixels <= host.BandWidthPixels
                    && guest.BandUsedHeightPixels > 0
                    && guest.BandUsedHeightPixels <= capacity);
            }

            var usage = AnalyzeRuntimeUsage(animatorServices, groups);
            var byId = groups.ToDictionary(group => group.Id);
            var adoptable = new Dictionary<string, Candidate>();
            var evaluatedLayouts = new HashSet<string>(StringComparer.Ordinal);

            foreach (var host in groups.Where(CanHostAnyGuest))
            {
                var hostWidth = host.BandWidthPixels;
                var capacity = Math.Max(
                    0, hostWidth - host.BandUsedHeightPixels);
                if (capacity <= 0) continue;

                var guests = groups
                    .Where(group =>
                        group.Id != host.Id
                        && group.StandaloneMetrics != null
                        && group.BaselineCostKnown
                        && group.BandWidthPixels > 0
                        && group.BandWidthPixels <= hostWidth
                        && group.BandUsedHeightPixels > 0
                        && group.BandUsedHeightPixels <= capacity)
                    .OrderBy(group => group.Id)
                    .ToArray();
                if (guests.Length == 0) continue;

                var statesByHeight = new Dictionary<int, List<BandState>>
                {
                    [0] = new List<BandState>
                    {
                        new BandState(0, Array.Empty<int>(), 0),
                    },
                };

                foreach (var guest in guests)
                {
                    var snapshot = statesByHeight.Values
                        .SelectMany(states => states)
                        .Where(state =>
                            state.GuestIds.Length + 1 < MaxMergeGroupCount)
                        .ToArray();

                    foreach (var state in snapshot)
                    {
                        if (state.GuestIds.Contains(guest.Id)) continue;
                        var nextHeight =
                            state.UsedHeightPixels + guest.BandUsedHeightPixels;
                        if (nextHeight > capacity) continue;

                        var next = new BandState(
                            nextHeight,
                            state.GuestIds.Append(guest.Id),
                            state.GuestBaselineBytes
                                + guest.BaselineCost.FullResidentBytes);

                        if (!statesByHeight.TryGetValue(
                                nextHeight, out var bucket))
                        {
                            bucket = statesByHeight[nextHeight] =
                                new List<BandState>();
                        }

                        if (bucket.Any(existing =>
                                existing.Key == next.Key))
                            continue;

                        bucket.Add(next);
                        bucket.Sort((left, right) =>
                        {
                            var cmp = right.GuestBaselineBytes.CompareTo(
                                left.GuestBaselineBytes);
                            if (cmp != 0) return cmp;
                            cmp = right.GuestIds.Length.CompareTo(
                                left.GuestIds.Length);
                            if (cmp != 0) return cmp;
                            return string.CompareOrdinal(
                                left.Key, right.Key);
                        });

                        if (bucket.Count > BandStatesPerHeight)
                            bucket.RemoveRange(
                                BandStatesPerHeight,
                                bucket.Count - BandStatesPerHeight);
                    }
                }

                var hostStates = statesByHeight.Values
                    .SelectMany(states => states)
                    .Where(state => state.GuestIds.Length != 0)
                    .OrderByDescending(state => state.GuestBaselineBytes)
                    .ThenByDescending(state => state.UsedHeightPixels)
                    .ThenByDescending(state => state.GuestIds.Length)
                    .ThenBy(state => state.Key, StringComparer.Ordinal)
                    .Take(BandCandidatesPerHost)
                    .ToArray();

                foreach (var state in hostStates)
                {
                    var ids = new[] { host.Id }
                        .Concat(state.GuestIds)
                        .OrderBy(id => id)
                        .ToArray();
                    var key = string.Join(",", ids);
                    var usedHeight =
                        host.BandUsedHeightPixels + state.UsedHeightPixels;
                    var layoutSize = new Vector2Int(
                        hostWidth,
                        GetCroppedHeight(hostWidth, usedHeight));
                    var layoutKey =
                        key + "@" + layoutSize.x + "x" + layoutSize.y;
                    if (!evaluatedLayouts.Add(layoutKey))
                        continue;

                    var selectedGroups = ids
                        .Select(id => byId[id])
                        .ToArray();
                    var materials = selectedGroups
                        .SelectMany(group => group.Materials)
                        .ToHashSet();
                    var propertySizes = BuildPredictedPropertySizes(
                        materials, layoutSize, settings);

                    var baseline = TextureCostPlan.Combine(
                        selectedGroups.Select(group => group.BaselineCost));
                    var merged = BuildGeneratedCost(
                        selectedGroups,
                        propertyTextures,
                        propertySizes,
                        alphaCache,
                        UnknownAlphaAssumption.AssumePresent);

                    var candidate = new Candidate(
                        ids,
                        baseline,
                        merged,
                        layoutSize,
                        usage);

                    if (!candidate.IsAdoptable)
                        continue;

                    if (!adoptable.TryGetValue(key, out var prior)
                        || candidate.SavingBytes > prior.SavingBytes
                        || (candidate.SavingBytes == prior.SavingBytes
                            && candidate.LayoutSize.y < prior.LayoutSize.y))
                    {
                        adoptable[key] = candidate;
                    }
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
                    candidate.GroupIds,
                    candidate.Baseline,
                    candidate.LayoutSize));
                consumed.UnionWith(candidate.GroupIds);
            }

            return new PlanResult(selected);
        }

        private static int GetBandUsedHeightPixels(
            AtlasTexture.AtlasExecutionMetrics metrics)
        {
            var width = Math.Max(1, metrics.LayoutSize.x);
            var usedFraction = Math.Max(
                0d, Math.Min(1d, 1d - metrics.TopFreeFraction));
            return Math.Max(
                1,
                Math.Min(
                    width,
                    (int)Math.Ceiling(width * usedFraction - 1e-6d)));
        }

        private static int GetCroppedHeight(
            int atlasWidth,
            int usedHeightPixels)
        {
            var ratio = Math.Max(
                0d,
                Math.Min(1d, (double)usedHeightPixels / atlasWidth));

            var height = ratio switch
            {
                < (1d / 32d) => atlasWidth / 32,
                < (1d / 16d) => atlasWidth / 16,
                < (1d / 8d) => atlasWidth / 8,
                < (1d / 4d) => atlasWidth / 4,
                < (1d / 2d) => atlasWidth / 2,
                _ => atlasWidth,
            };
            return Math.Max(4, height);
        }

        private static IReadOnlyDictionary<string, Vector2Int>
            BuildPredictedPropertySizes(
                HashSet<Material> materials,
                Vector2Int layoutSize,
                AtlasSetting settings)
        {
            var grouping = new MaterialGroupingContext(
                materials, settings.AtlasTargetUVChannel, null);
            var properties = grouping.GroupMaterials
                .SelectMany(group => group.GroupedTexture.Keys)
                .Distinct()
                .OrderBy(property => property, StringComparer.Ordinal);

            var result = new Dictionary<string, Vector2Int>();
            foreach (var property in properties)
            {
                var primarySizes = new List<Vector2Int?>();
                var sourceSizes = new List<Vector2Int>();

                foreach (var group in grouping.GroupMaterials)
                {
                    if (!group.GroupedTexture.TryGetValue(
                            property, out var source)
                        || source == null)
                    {
                        continue;
                    }

                    var primary = group.GetPrimaryTexture(null);
                    primarySizes.Add(primary == null
                        ? null
                        : new Vector2Int(primary.width, primary.height));
                    sourceSizes.Add(
                        new Vector2Int(source.width, source.height));
                }

                result[property] =
                    AtlasTextureResolutionPlan.CalculatePropertySize(
                        layoutSize,
                        autoSize: true,
                        primarySizes,
                        sourceSizes);
            }

            return result;
        }

        private static AtlasTexture.AtlasExecutionMetrics? AnalyzeAtlas(
            IDomain domain,
            ITexTransToolForUnity engine,
            AtlasSetting settings,
            HashSet<Material> materials,
            Renderer[] renderers)
        {
            AtlasTexture.AtlasExecutionMetrics? captured = null;

            try
            {
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
            catch (ArgumentException exception)
                when (IsNfdhComparerFailure(exception))
            {
                // Additional merging probes material combinations which ordinary
                // AutoAtlasTexture never asks TexTransCore to relocate. NFDHPlasFC's
                // height comparer can become inconsistent for a sufficiently large
                // island-height range. Such a combination is simply not a viable
                // merge candidate; it must not abort the avatar build.
                return null;
            }
        }

        private static bool IsNfdhComparerFailure(ArgumentException exception)
        {
            return exception.Message.IndexOf(
                       "IComparer.Compare() method returns inconsistent results",
                       StringComparison.Ordinal) >= 0
                && (exception.StackTrace?.IndexOf(
                        "NFDHPlasFC.NextFitDecreasingHeightPlusFloorCeiling",
                        StringComparison.Ordinal) ?? -1) >= 0;
        }

        private static TextureCostPlan? TryBuildOriginalCost(
            IDomain domain,
            PreparedGroup group,
            IReadOnlyDictionary<Material, IReadOnlyDictionary<string, Texture>> propertyTextures,
            Dictionary<Texture, bool> alphaCache)
        {
            var entries = new List<TextureCostEntry>();
            var bytesPerTexel = new Dictionary<Texture, double>();

            foreach (var texture in group.SourceTextures)
            {
                if (!TryGetOriginalTextureCostInfo(
                        domain,
                        texture,
                        alphaCache,
                        out var format,
                        out var mipCount,
                        out var streaming))
                {
                    return null;
                }

                var demands = new Dictionary<int, double>
                {
                    [group.Id] = FindTextureDemandLog2(
                        group, texture, propertyTextures),
                };

                entries.Add(new TextureCostEntry(
                    texture.width,
                    texture.height,
                    format,
                    mipCount,
                    streaming,
                    new[] { group.Id },
                    demands));
                bytesPerTexel[texture] = GetBytesPerTexel(format);
            }

            var propertySamplingCosts = group.Materials
                .Where(propertyTextures.ContainsKey)
                .SelectMany(material => propertyTextures[material])
                .Where(pair => pair.Value != null)
                .GroupBy(pair => pair.Key)
                .Select(propertyGroup => propertyGroup
                    .Max(pair => bytesPerTexel[pair.Value]))
                .Sum();

            return new TextureCostPlan(
                entries,
                new Dictionary<int, double>
                {
                    [group.Id] = propertySamplingCosts,
                });
        }

        private static bool TryGetOriginalTextureCostInfo(
            IDomain domain,
            Texture texture,
            Dictionary<Texture, bool> alphaCache,
            out TextureFormat format,
            out int mipCount,
            out bool streaming)
        {
            format = default;
            mipCount = 1;
            streaming = false;

            TexTransToolTextureDescriptor descriptor;
            try
            {
                descriptor = domain.GetTextureDescriptor(texture);
            }
            catch
            {
                return false;
            }

            mipCount = descriptor.UseMipMap
                ? CalculateMipCount(texture.width, texture.height)
                : 1;

            switch (texture)
            {
                case Texture2D texture2D:
                    try
                    {
                        format = descriptor.TextureFormat.Get(texture2D).CompressFormat;
                    }
                    catch
                    {
                        return false;
                    }

                    streaming = texture2D.streamingMipmaps && mipCount > 1;
                    return true;

                case RenderTexture renderTexture:
                    if (descriptor.TextureFormat
                        is not TextureCompressionData compression)
                    {
                        return false;
                    }

                    var hasAlpha = HasAlpha(
                        renderTexture,
                        alphaCache,
                        UnknownAlphaAssumption.AssumeAbsent);
                    format = compression.UseOverride
                        ? compression.OverrideTextureFormat
                        : TextureCompressionData.GetQuality2TextureFormat(
                            compression.FormatQualityValue, hasAlpha);

                    // Registered TTT RenderTextures are downloaded to Texture2D
                    // and CompressDeferred enables Streaming Mipmaps on them.
                    streaming = mipCount > 1;
                    return true;

                default:
                    return false;
            }
        }

        private static TextureCostPlan BuildGeneratedCost(
            IReadOnlyList<PreparedGroup> groups,
            IReadOnlyDictionary<Material, IReadOnlyDictionary<string, Texture>> propertyTextures,
            IReadOnlyDictionary<string, Vector2Int> propertySizes,
            Dictionary<Texture, bool> alphaCache,
            UnknownAlphaAssumption unknownAlphaAssumption)
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
                        if (!group.PropertyUseCount.TryGetValue(
                                usageProperty, out var useCount)
                            || useCount <= 0)
                        {
                            continue;
                        }

                        groupUsedProperties += useCount;
                        var propertyDemand =
                            group.PropertyDemandLog2.GetValueOrDefault(
                                usageProperty, UnknownDemandLog2);
                        groupDemand = Math.Min(
                            groupDemand, propertyDemand);
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
                    || pixelSources.Any(texture =>
                        HasAlpha(
                            texture,
                            alphaCache,
                            unknownAlphaAssumption));
                var quality = property.Key is "_BumpMap" or "_Bump2ndMap"
                    ? FormatQuality.High
                    : FormatQuality.Normal;
                var format = TextureCompressionData.GetQuality2TextureFormat(
                    quality, hasAlpha);
                var bytesPerTexel = GetBytesPerTexel(format);

                foreach (var pair in usedPropertyCount)
                    sampling[pair.Key] += bytesPerTexel * pair.Value;

                entries.Add(new TextureCostEntry(
                    property.Value.x,
                    property.Value.y,
                    format,
                    CalculateMipCount(property.Value.x, property.Value.y),
                    // Generated atlas textures are downloaded through TTT's
                    // deferred compressor, which enables Streaming Mipmaps.
                    streaming: true,
                    contributors,
                    demand));
            }

            return new TextureCostPlan(entries, sampling);
        }

        private static bool HasAlpha(
            Texture texture,
            Dictionary<Texture, bool> cache,
            UnknownAlphaAssumption unknownAlphaAssumption)
        {
            if (texture is RenderTexture renderTexture)
            {
                if (!GraphicsFormatUtility.HasAlphaChannel(
                        renderTexture.graphicsFormat))
                {
                    return false;
                }

                // RenderTexture content would require a GPU readback to prove
                // whether alpha is actually used. For a baseline we assume the
                // cheaper no-alpha format; for a merge candidate we assume alpha.
                return unknownAlphaAssumption
                    == UnknownAlphaAssumption.AssumePresent;
            }

            if (texture is not Texture2D texture2D)
            {
                return unknownAlphaAssumption
                    == UnknownAlphaAssumption.AssumePresent;
            }

            if (cache.TryGetValue(texture, out var cached)) return cached;

            try
            {
                using var alpha =
                    TextureCompressionData.HasAlphaChannel(texture2D);
                return cache[texture] = alpha.GetResult();
            }
            catch
            {
                return unknownAlphaAssumption
                    == UnknownAlphaAssumption.AssumePresent;
            }
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
                    var blocksX =
                        ((long)width + entry.BlockWidth - 1) / entry.BlockWidth;
                    var blocksY =
                        ((long)height + entry.BlockHeight - 1) / entry.BlockHeight;
                    total += blocksX * blocksY * entry.BlockSize;
                }

                width = Math.Max(1, width >> 1);
                height = Math.Max(1, height >> 1);
            }

            return total;
        }

        private static (uint Width, uint Height, uint Size) GetBlockLayout(
            TextureFormat format)
        {
            try
            {
                return (
                    Math.Max(1u, GraphicsFormatUtility.GetBlockWidth(format)),
                    Math.Max(1u, GraphicsFormatUtility.GetBlockHeight(format)),
                    Math.Max(1u, GraphicsFormatUtility.GetBlockSize(format)));
            }
            catch
            {
                return (1u, 1u, 4u);
            }
        }

        private static double GetBytesPerTexel(TextureFormat format)
        {
            var block = GetBlockLayout(format);
            return (double)block.Size / (block.Width * block.Height);
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
            if (scenarios.Count == 0)
            {
                // The runtime-state space could not be bounded cheaply enough.
                // Reject automatic merging rather than underestimating mip cost.
                return true;
            }

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
                    values.Add(group.PropertyDemandLog2.GetValueOrDefault(
                        property.Key, UnknownDemandLog2));
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
            var uvUsageCache =
                new Dictionary<Material, IReadOnlyDictionary<string, TextureUVUsage>>();

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

                    if (!uvUsageCache.TryGetValue(material, out var uvUsages))
                    {
                        uvUsages =
                            TTShaderTextureUsageInformationUtil
                                .GetContainsUVTransformUsage(material);
                        uvUsageCache[material] = uvUsages;
                    }

                    if (!uvUsages.TryGetValue(property, out var uvUsage)
                        || uvUsage.UVMatrix is not { } uvMatrix)
                    {
                        // Unknown shader-side UV transforms can increase the
                        // required mip arbitrarily. Keep the conservative
                        // full-resolution demand instead of guessing from _ST.
                        best = Math.Min(best, UnknownDemandLog2);
                        continue;
                    }

                    var metric = CalculateUvDistributionMetric(
                        mesh, subMesh, renderer.transform.lossyScale,
                        uvMatrix);
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
            TextureAtlas.AAOCode.Matrix2x3 uvMatrix)
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

            var uvTransformArea = Math.Abs(
                (double)uvMatrix.M00 * uvMatrix.M11
                - (double)uvMatrix.M01 * uvMatrix.M10);
            if (uvTransformArea <= Epsilon)
                return 0d;

            return (worldArea / uvArea) / uvTransformArea;
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

            // If the renderer's material slots are animated, visibility alone
            // cannot prove which texture-connected group is active at runtime.
            // Treat the group as potentially simultaneous instead.
            if (HasAnimatedMaterialSlots(index, remapper, renderer))
                return null;

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

        private static bool HasAnimatedMaterialSlots(
            AnimationIndex index,
            ObjectPathRemapper remapper,
            Renderer renderer)
        {
            var rendererPath =
                remapper.GetVirtualPathForObject(renderer.gameObject);

            return index.ClipsWithObjectCurves.Any(clip =>
                clip.GetObjectCurveBindings().Any(binding =>
                    binding.path == rendererPath
                    && typeof(Renderer).IsAssignableFrom(binding.type)
                    && binding.propertyName.StartsWith(
                        "m_Materials.Array.data[",
                        StringComparison.Ordinal)));
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
