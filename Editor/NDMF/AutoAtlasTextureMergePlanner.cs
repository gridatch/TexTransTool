#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using net.rs64.TexTransCore;
using net.rs64.TexTransCore.UVIsland;
using net.rs64.TexTransTool.TextureAtlas;
using net.rs64.TexTransTool.TextureAtlas.IslandSizePriorityTuner;
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
        private const int ShadowPackingStatesPerHost = 1024;
        private static readonly int[] DiagnosticStateBudgets =
            { 2048, 4096 };
        // Prevent a diagnostic expansion from destabilizing the Unity process.
        // This limit applies to one generated-state dictionary, before any
        // property-aware retention. Production search is not subject to it.
        private const int DiagnosticGeneratedStateLimit = 200_000;
        private const int PackingGridUnit = 256;
        private const double Epsilon = 1e-8;

        internal sealed class PreparedGroup
        {
            internal int Id;
            internal bool ExcludedFromTextureMerging;
            internal HashSet<Material> Materials = new();
            internal Renderer[] Renderers = Array.Empty<Renderer>();
            internal HashSet<Texture> SourceTextures = new();
            internal long OriginalPixels;

            internal AtlasTexture.AtlasExecutionMetrics? StandaloneMetrics;
            // Heights include the final atlas's required padding, expressed
            // in pixels at the atomic group's own profile width.
            internal readonly Dictionary<(int AtlasWidth, int RegionWidth), int>
                TwoDimensionalProfileHeights = new();
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
            internal readonly IReadOnlyDictionary<string, Vector2Int>
                PlannedPropertySizes;
            internal readonly IReadOnlyDictionary<int, RectInt>? PackedRegions;
            internal readonly long PredictedSavingBytes;
            internal readonly float PredictedSavingRate;

            internal MergeSelection(
                Candidate candidate,
                IReadOnlyDictionary<int, RectInt>? packedRegions = null)
            {
                GroupIds = candidate.GroupIds.OrderBy(id => id).ToArray();
                BaselineBytes = candidate.Baseline.FullResidentBytes;
                PlannedLayoutSize = candidate.LayoutSize;
                PlannedPropertySizes = candidate.PropertySizes.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value,
                    StringComparer.Ordinal);
                PackedRegions = packedRegions;
                PredictedSavingBytes = candidate.SavingBytes;
                PredictedSavingRate = candidate.SavingRate;
            }
        }

        internal sealed class SelectionImprovement
        {
            internal readonly int CandidateCount;
            internal readonly IReadOnlyList<MergeSelection> GreedySelections;
            internal readonly long GreedySavingBytes;
            internal readonly long ExactSavingBytes;

            internal SelectionImprovement(
                int candidateCount,
                IReadOnlyList<MergeSelection> greedySelections,
                long greedySavingBytes,
                long exactSavingBytes)
            {
                CandidateCount = candidateCount;
                GreedySelections = greedySelections;
                GreedySavingBytes = greedySavingBytes;
                ExactSavingBytes = exactSavingBytes;
            }
        }

        internal sealed class RejectedCandidateDiagnostic
        {
            internal readonly int[] GroupIds;
            internal readonly Vector2Int LayoutSize;
            internal readonly CandidateRejectionReason RejectionReason;
            internal readonly long BaselinePixels;
            internal readonly long MergedPixels;
            internal readonly long BaselineBytes;
            internal readonly long MergedBytes;
            internal readonly long SavingBytes;

            internal RejectedCandidateDiagnostic(Candidate candidate)
            {
                GroupIds = candidate.GroupIds.OrderBy(id => id).ToArray();
                LayoutSize = candidate.LayoutSize;
                RejectionReason = candidate.RejectionReason;
                BaselinePixels = candidate.Baseline.PixelCount;
                MergedPixels = candidate.Merged.PixelCount;
                BaselineBytes = candidate.Baseline.FullResidentBytes;
                MergedBytes = candidate.Merged.FullResidentBytes;
                SavingBytes = candidate.SavingBytes;
            }
        }

        internal sealed class SearchBudgetProbe
        {
            internal readonly int StateBudget;
            internal readonly long SearchMilliseconds;
            internal readonly bool Aborted;
            internal readonly string AbortReason;
            internal readonly int CandidateCount;
            internal readonly int SelectedCount;
            internal readonly long PredictedSavingBytes;
            internal readonly int MaxStatesBeforeRetention;
            internal readonly long PrunedStateCount;
            internal readonly bool SameSelectedPlan;
            internal readonly IReadOnlyList<MergeSelection> Selections;
            internal readonly int AdditionalOrChangedCandidateCount;
            internal readonly IReadOnlyList<MergeSelection>
                AdditionalOrChangedCandidates;
            internal readonly int MissingOrChangedCandidateCount;
            internal readonly IReadOnlyList<MergeSelection>
                MissingOrChangedCandidates;
            internal readonly IReadOnlyList<RejectedCandidateDiagnostic>
                Rejected4096Candidates;

            internal SearchBudgetProbe(
                int stateBudget,
                long searchMilliseconds,
                bool aborted,
                string abortReason,
                int candidateCount = 0,
                IReadOnlyList<MergeSelection>? selections = null,
                int maxStatesBeforeRetention = 0,
                long prunedStateCount = 0,
                bool sameSelectedPlan = false,
                IReadOnlyList<MergeSelection>?
                    additionalOrChangedCandidates = null,
                IReadOnlyList<MergeSelection>?
                    missingOrChangedCandidates = null,
                IReadOnlyList<RejectedCandidateDiagnostic>?
                    rejected4096Candidates = null)
            {
                StateBudget = stateBudget;
                SearchMilliseconds = searchMilliseconds;
                Aborted = aborted;
                AbortReason = abortReason;
                CandidateCount = candidateCount;
                Selections = selections ?? Array.Empty<MergeSelection>();
                SelectedCount = Selections.Count;
                PredictedSavingBytes = Selections.Sum(
                    selection => selection.PredictedSavingBytes);
                MaxStatesBeforeRetention = maxStatesBeforeRetention;
                PrunedStateCount = prunedStateCount;
                SameSelectedPlan = sameSelectedPlan;
                AdditionalOrChangedCandidates =
                    (additionalOrChangedCandidates
                        ?? Array.Empty<MergeSelection>())
                    .Take(20).ToArray();
                AdditionalOrChangedCandidateCount =
                    additionalOrChangedCandidates?.Count ?? 0;
                MissingOrChangedCandidates =
                    (missingOrChangedCandidates
                        ?? Array.Empty<MergeSelection>())
                    .Take(20).ToArray();
                MissingOrChangedCandidateCount =
                    missingOrChangedCandidates?.Count ?? 0;
                Rejected4096Candidates =
                    rejected4096Candidates
                    ?? Array.Empty<RejectedCandidateDiagnostic>();
            }
        }

        internal sealed class SearchBudgetComparison
        {
            internal readonly int ProductionStateBudget;
            internal readonly long ProductionSearchMilliseconds;
            internal readonly int ProductionCandidateCount;
            internal readonly int ProductionMaxStatesBeforeRetention;
            internal readonly long ProductionPrunedStateCount;
            internal readonly IReadOnlyList<SearchBudgetProbe> Probes;

            internal SearchBudgetComparison(
                int productionStateBudget,
                long productionSearchMilliseconds,
                int productionCandidateCount,
                int productionMaxStatesBeforeRetention,
                long productionPrunedStateCount,
                IReadOnlyList<SearchBudgetProbe> probes)
            {
                ProductionStateBudget = productionStateBudget;
                ProductionSearchMilliseconds = productionSearchMilliseconds;
                ProductionCandidateCount = productionCandidateCount;
                ProductionMaxStatesBeforeRetention =
                    productionMaxStatesBeforeRetention;
                ProductionPrunedStateCount = productionPrunedStateCount;
                Probes = probes;
            }
        }

        internal sealed class PlanResult
        {
            internal readonly IReadOnlyList<MergeSelection> Selections;
            internal readonly SelectionImprovement? Improvement;
            internal readonly SearchBudgetComparison? SearchBudgetComparison;

            internal PlanResult(
                IReadOnlyList<MergeSelection> selections,
                SelectionImprovement? improvement = null,
                SearchBudgetComparison? searchBudgetComparison = null)
            {
                Selections = selections;
                Improvement = improvement;
                SearchBudgetComparison = searchBudgetComparison;
            }
        }

        private sealed class PropertyResolutionRequirement
        {
            internal readonly Vector2Int?[] PrimarySizes;
            internal readonly Vector2Int[] SourceSizes;

            internal PropertyResolutionRequirement(
                IEnumerable<Vector2Int?> primarySizes,
                IEnumerable<Vector2Int> sourceSizes)
            {
                PrimarySizes = primarySizes.ToArray();
                SourceSizes = sourceSizes.ToArray();
            }
        }

        private sealed class PropertyResolutionRequirements
        {
            internal readonly IReadOnlyDictionary<
                string, PropertyResolutionRequirement> ByProperty;

            internal PropertyResolutionRequirements(
                IReadOnlyDictionary<string, PropertyResolutionRequirement>
                    byProperty)
            {
                ByProperty = byProperty;
            }
        }

        // Keys borrow already-sorted group ID arrays. Callers must not mutate
        // those arrays after inserting a key in a cache.
        internal readonly struct GroupLayoutKey
            : IEquatable<GroupLayoutKey>
        {
            private readonly int[] _groupIds;
            private readonly int _width;
            private readonly int _height;
            private readonly int _hashCode;

            internal GroupLayoutKey(
                int[] groupIds,
                Vector2Int layoutSize)
            {
                _groupIds = groupIds;
                _width = layoutSize.x;
                _height = layoutSize.y;
                unchecked
                {
                    var hash = 17;
                    for (var index = 0; index < groupIds.Length; index += 1)
                        hash = hash * 31 + groupIds[index];
                    hash = hash * 31 + _width;
                    hash = hash * 31 + _height;
                    _hashCode = hash;
                }
            }

            public bool Equals(GroupLayoutKey other)
            {
                if (_hashCode != other._hashCode
                    || _width != other._width
                    || _height != other._height
                    || _groupIds.Length != other._groupIds.Length)
                    return false;
                for (var index = 0; index < _groupIds.Length; index += 1)
                    if (_groupIds[index] != other._groupIds[index]) return false;
                return true;
            }

            public override bool Equals(object? obj) =>
                obj is GroupLayoutKey other && Equals(other);

            public override int GetHashCode() => _hashCode;
        }
        private sealed class ShadowCostProjection
        {
            internal readonly TextureCostPlan Baseline;
            internal readonly TextureCostPlan Merged;
            internal readonly IReadOnlyDictionary<string, Vector2Int>
                PropertySizes;

            internal ShadowCostProjection(
                TextureCostPlan baseline,
                TextureCostPlan merged,
                IReadOnlyDictionary<string, Vector2Int> propertySizes)
            {
                Baseline = baseline;
                Merged = merged;
                PropertySizes = propertySizes;
            }
        }

        private readonly struct ShadowPackingStateKey
            : IEquatable<ShadowPackingStateKey>
        {
            private readonly int[] _groupIds;
            private readonly int[] _skyline;
            private readonly int _hashCode;

            internal ShadowPackingStateKey(
                int[] groupIds,
                int[] skyline)
            {
                _groupIds = groupIds;
                _skyline = skyline;

                unchecked
                {
                    var hash = 17;
                    for (var index = 0;
                         index < groupIds.Length;
                         index += 1)
                    {
                        hash = hash * 31 + groupIds[index];
                    }

                    hash = hash * 31 + 397;

                    for (var index = 0;
                         index < skyline.Length;
                         index += 1)
                    {
                        hash = hash * 31 + skyline[index];
                    }

                    _hashCode = hash;
                }
            }

            public bool Equals(ShadowPackingStateKey other)
            {
                if (_hashCode != other._hashCode
                    || _groupIds.Length != other._groupIds.Length
                    || _skyline.Length != other._skyline.Length)
                {
                    return false;
                }

                for (var index = 0;
                     index < _groupIds.Length;
                     index += 1)
                {
                    if (_groupIds[index] != other._groupIds[index])
                        return false;
                }

                for (var index = 0;
                     index < _skyline.Length;
                     index += 1)
                {
                    if (_skyline[index] != other._skyline[index])
                        return false;
                }

                return true;
            }

            public override bool Equals(object? obj) =>
                obj is ShadowPackingStateKey other
                && Equals(other);

            public override int GetHashCode() => _hashCode;
        }

        private sealed class ShadowPackingStateStableComparer
            : IComparer<ShadowPackingState>
        {
            internal static readonly ShadowPackingStateStableComparer
                Instance = new();

            public int Compare(
                ShadowPackingState? left,
                ShadowPackingState? right)
            {
                if (ReferenceEquals(left, right)) return 0;
                if (left is null) return -1;
                if (right is null) return 1;

                var commonGroupCount = Math.Min(
                    left.GroupIds.Length,
                    right.GroupIds.Length);
                for (var index = 0;
                     index < commonGroupCount;
                     index += 1)
                {
                    var cmp = left.GroupIds[index]
                        .CompareTo(right.GroupIds[index]);
                    if (cmp != 0) return cmp;
                }

                var groupCountCmp = left.GroupIds.Length
                    .CompareTo(right.GroupIds.Length);
                if (groupCountCmp != 0) return groupCountCmp;

                var commonSkylineCount = Math.Min(
                    left.Skyline.Length,
                    right.Skyline.Length);
                for (var index = 0;
                     index < commonSkylineCount;
                     index += 1)
                {
                    var cmp = left.Skyline[index]
                        .CompareTo(right.Skyline[index]);
                    if (cmp != 0) return cmp;
                }

                return left.Skyline.Length
                    .CompareTo(right.Skyline.Length);
            }
        }

        private sealed class ShadowPackingPlacement
        {
            internal readonly int GroupId;
            internal readonly RectInt Region;
            internal readonly ShadowPackingPlacement? Previous;

            internal ShadowPackingPlacement(
                int groupId, RectInt region, ShadowPackingPlacement? previous)
            {
                GroupId = groupId;
                Region = region;
                Previous = previous;
            }
        }

        private sealed class ShadowPackingState
        {
            internal readonly int[] Skyline;
            internal readonly int[] GroupIds;
            internal readonly long BaselineBytes;
            internal readonly int MaxHeight;
            internal readonly int SkylineArea;
            internal readonly ShadowPackingStateKey StateKey;
            internal readonly ShadowPackingPlacement? PlacementTail;
            internal long ProjectedPixelDelta;
            internal long ProjectedMemoryDelta;

            internal ShadowPackingState(
                int[] skyline,
                int[] groupIds,
                long baselineBytes,
                ShadowPackingPlacement? placementTail = null)
            {
                PlacementTail = placementTail;
                Skyline = skyline;
                GroupIds = groupIds;
                BaselineBytes = baselineBytes;

                var maxHeight = 0;
                var skylineArea = 0;
                for (var index = 0;
                     index < skyline.Length;
                     index += 1)
                {
                    maxHeight = Math.Max(
                        maxHeight,
                        skyline[index]);
                    skylineArea += skyline[index];
                }

                MaxHeight = maxHeight;
                SkylineArea = skylineArea;
                StateKey =
                    new ShadowPackingStateKey(
                        groupIds,
                        skyline);
            }
        }

        /// <summary>
        /// For identical already-placed group sets, a skyline no higher in
        /// any column leaves at least as much room for every future placement.
        /// A different group set must never be pruned using geometry alone:
        /// property-aware merge savings are not monotonic across sets.
        /// </summary>
        internal static bool SkylineDominates(
            IReadOnlyList<int> left, IReadOnlyList<int> right)
        {
            if (left.Count != right.Count) return false;
            for (var column = 0; column < left.Count; column += 1)
                if (left[column] > right[column]) return false;
            return true;
        }

        private static List<ShadowPackingState> RemoveDominatedSkylineStates(
            IEnumerable<ShadowPackingState> input)
        {
            var byGroupSet = new Dictionary<
                string, List<ShadowPackingState>>(StringComparer.Ordinal);

            foreach (var state in input)
            {
                var key = string.Join(",", state.GroupIds);
                if (!byGroupSet.TryGetValue(key, out var candidates))
                    byGroupSet[key] = candidates =
                        new List<ShadowPackingState>();
                candidates.Add(state);
            }

            var survivors = new List<ShadowPackingState>();
            foreach (var candidates in byGroupSet.Values)
            {
                // Exhaustive skyline dominance compares every pair, creating
                // quadratic cost (measured at 7.9 seconds for 131k generated
                // states in Seiran_SailorCupid). Instead, find two favorable
                // witnesses in linear time for each identical group set.
                // Only discard states PROVEN dominated by a witness; missing
                // some dominance opportunities is safe.
                var minimumArea = candidates[0];
                var minimumHeight = candidates[0];
                foreach (var candidate in candidates)
                {
                    if (candidate.SkylineArea < minimumArea.SkylineArea
                        || (candidate.SkylineArea
                                == minimumArea.SkylineArea
                            && candidate.MaxHeight
                                < minimumArea.MaxHeight))
                        minimumArea = candidate;
                    if (candidate.MaxHeight < minimumHeight.MaxHeight
                        || (candidate.MaxHeight
                                == minimumHeight.MaxHeight
                            && candidate.SkylineArea
                                < minimumHeight.SkylineArea))
                        minimumHeight = candidate;
                }

                foreach (var candidate in candidates)
                {
                    if (!ReferenceEquals(candidate, minimumArea)
                        && minimumArea.MaxHeight <= candidate.MaxHeight
                        && SkylineDominates(
                            minimumArea.Skyline, candidate.Skyline))
                        continue;

                    if (!ReferenceEquals(candidate, minimumHeight)
                        && minimumHeight.MaxHeight <= candidate.MaxHeight
                        && SkylineDominates(
                            minimumHeight.Skyline, candidate.Skyline))
                        continue;

                    survivors.Add(candidate);
                }
            }

            return survivors;
        }

        private readonly struct ShadowPackingDiagnostics
        {
            internal readonly IReadOnlyList<MergeSelection> ProductionCandidates;
            internal readonly int MaxStatesBeforeRetention;
            internal readonly long PrunedStateCount;
            internal readonly IReadOnlyList<RejectedCandidateDiagnostic>
                Rejected4096Candidates;

            internal ShadowPackingDiagnostics(
                IReadOnlyList<MergeSelection> productionCandidates,
                int maxStatesBeforeRetention,
                long prunedStateCount,
                IReadOnlyList<RejectedCandidateDiagnostic>
                    rejected4096Candidates)
            {
                ProductionCandidates = productionCandidates;
                MaxStatesBeforeRetention = maxStatesBeforeRetention;
                PrunedStateCount = prunedStateCount;
                Rejected4096Candidates = rejected4096Candidates;
            }
        }

        private sealed class DiagnosticSearchStateLimitExceededException
            : Exception
        {
            internal DiagnosticSearchStateLimitExceededException(
                int stateBudget,
                int targetWidth,
                int groupId,
                int generatedStateLimit)
                : base(
                    $"budget={stateBudget}, width={targetWidth}, "
                    + $"expanding=g{groupId}, generated-state limit="
                    + generatedStateLimit)
            {
            }
        }

        private enum UnknownAlphaAssumption
        {
            AssumeAbsent,
            AssumePresent,
        }

        internal enum CandidateRejectionReason
        {
            None,
            PixelCount,
            FullResidentMemory,
            Sampling,
        }

        internal sealed class Candidate
        {
            internal readonly int[] GroupIds;
            internal readonly TextureCostPlan Baseline;
            internal readonly TextureCostPlan Merged;
            internal readonly Vector2Int LayoutSize;
            internal readonly IReadOnlyDictionary<string, Vector2Int>
                PropertySizes;
            internal readonly long SavingBytes;
            internal readonly float SavingRate;
            internal readonly bool IsAdoptable;
            internal readonly CandidateRejectionReason RejectionReason;

            internal Candidate(
                int[] groupIds,
                TextureCostPlan baseline,
                TextureCostPlan merged,
                Vector2Int layoutSize,
                IReadOnlyDictionary<string, Vector2Int> propertySizes)
            {
                GroupIds = groupIds;
                Baseline = baseline;
                Merged = merged;
                LayoutSize = layoutSize;
                PropertySizes = propertySizes;
                SavingBytes = baseline.FullResidentBytes - merged.FullResidentBytes;
                SavingRate = baseline.FullResidentBytes <= 0
                    ? 0f
                    : (float)SavingBytes / baseline.FullResidentBytes;

                RejectionReason =
                    merged.PixelCount >= baseline.PixelCount
                        ? CandidateRejectionReason.PixelCount
                        : merged.FullResidentBytes >= baseline.FullResidentBytes
                            ? CandidateRejectionReason.FullResidentMemory
                            : HasSamplingRegression(
                                baseline, merged, groupIds)
                                ? CandidateRejectionReason.Sampling
                                : CandidateRejectionReason.None;

                IsAdoptable =
                    RejectionReason == CandidateRejectionReason.None;
            }
        }

        internal sealed class TextureCostPlan
        {
            internal static readonly TextureCostPlan Empty = new(Array.Empty<TextureCostEntry>());

            internal readonly TextureCostEntry[] Entries;
            internal readonly long FullResidentBytes;
            internal readonly long PixelCount;
            internal readonly Dictionary<int, double> SamplingBytesPerTexel;

            internal TextureCostPlan(
                IEnumerable<TextureCostEntry> entries,
                IReadOnlyDictionary<int, double>? samplingBytesPerTexel = null)
            {
                Entries = entries.ToArray();
                FullResidentBytes = Entries.Sum(entry => entry.FullResidentBytes);
                PixelCount = Entries.Sum(entry => (long)entry.Width * entry.Height);
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
            internal readonly long FullResidentBytes;

            internal TextureCostEntry(
                int width,
                int height,
                TextureFormat format,
                int mipCount)
                : this(width, height, GetBlockLayout(format), mipCount)
            {
            }

            internal TextureCostEntry(
                int width,
                int height,
                (uint Width, uint Height, uint Size) block,
                int mipCount)
            {
                Width = width;
                Height = height;
                BlockWidth = block.Width;
                BlockHeight = block.Height;
                BlockSize = block.Size;
                MipCount = Math.Max(1, mipCount);
                FullResidentBytes = CalculateBytes(this);
            }
        }

        internal static PlanResult Plan(
            IDomain domain,
            AutoAtlasTexture configuration,
            AtlasSetting settings,
            IReadOnlyDictionary<Material, IReadOnlyDictionary<string, Texture>> propertyTextures,
            IReadOnlyList<PreparedGroup> groups)
        {
            if (!configuration.EnableAdditionalAtlasMerging
                || groups.Count(group => !group.ExcludedFromTextureMerging) < 2)
            {
                return new PlanResult(Array.Empty<MergeSelection>());
            }

            var mergeGroups = groups
                .Where(group => !group.ExcludedFromTextureMerging)
                .ToArray();

            var alphaCache = new Dictionary<Texture, bool>();
            var propertyRequirementCache =
                new Dictionary<string, PropertyResolutionRequirements>(
                    StringComparer.Ordinal);
            IReadOnlyDictionary<string, Vector2Int>
                GetPredictedPropertySizes(
                    int[] ids,
                    Vector2Int layoutSize)
            {
                var orderedIds = ids.OrderBy(id => id).ToArray();
                var key = string.Join(",", orderedIds);
                if (!propertyRequirementCache.TryGetValue(
                        key,
                        out var requirements))
                {
                    var materials = orderedIds
                        .Select(id => groups[id])
                        .SelectMany(group => group.Materials)
                        .ToHashSet();
                    requirements = BuildPredictedPropertyRequirements(
                        materials,
                        propertyTextures);
                    propertyRequirementCache[key] = requirements;
                }

                return BuildPredictedPropertySizes(
                    requirements,
                    layoutSize);
            }

            // Excluded atomic groups always run independently. Their
            // standalone DoAtlasTexture execution does not use any planner
            // profiles, so avoid preparing and probing them in advance.
            foreach (var group in mergeGroups)
            {
                group.StandaloneMetrics = AnalyzeAtlas(
                    domain, settings, group);
            }

            foreach (var group in mergeGroups)
            {
                if (group.StandaloneMetrics is not { } metrics)
                    continue;

                foreach (var property in group.Materials
                             .Where(propertyTextures.ContainsKey)
                             .SelectMany(material => propertyTextures[material].Keys)
                             .Distinct(StringComparer.Ordinal))
                {
                    group.PropertyUseCount[property] = 1;
                }

                var standaloneGeneratedCost = BuildGeneratedCost(
                    new[] { group },
                    propertyTextures,
                    metrics.PropertySizes,
                    alphaCache,
                    UnknownAlphaAssumption.AssumeAbsent);

                if (standaloneGeneratedCost.PixelCount > 0
                    && standaloneGeneratedCost.PixelCount < group.OriginalPixels)
                {
                    group.BaselineCost = standaloneGeneratedCost;
                    group.BaselineCostKnown = true;
                }
                else
                {
                    var originalCost = TryBuildOriginalCost(
                        domain, group, propertyTextures);
                    if (originalCost != null)
                    {
                        group.BaselineCost = originalCost;
                        group.BaselineCostKnown = true;
                    }
                }
            }

            var byId = groups.ToDictionary(group => group.Id);

            var productionTimer =
                System.Diagnostics.Stopwatch.StartNew();
            var shadow2D = EvaluateShadow2DPacking(
                mergeGroups,
                byId,
                propertyTextures,
                settings,
                alphaCache,
                GetPredictedPropertySizes,
                ShadowPackingStatesPerHost);
            productionTimer.Stop();

            var selected = SelectTwoDimensionalMerges(
                shadow2D.ProductionCandidates, out var improvement);
            var productionPlanSignature =
                SelectionPlanSignature(selected);
            var productionByGroupSet = shadow2D.ProductionCandidates
                .ToDictionary(
                    SelectionGroupSetKey,
                    selection => SelectionSignature(selection),
                    StringComparer.Ordinal);

            // Diagnostic-only widening. A true unbounded run can grow
            // combinatorially before skyline dominance is reached, so compare
            // progressively larger retention budgets instead. Abort a probe
            // before its per-step generated-state dictionary can exhaust Unity.
            var probes = new List<SearchBudgetProbe>();
            foreach (var diagnosticBudget in DiagnosticStateBudgets)
            {
                var diagnosticTimer =
                    System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    var diagnostic = EvaluateShadow2DPacking(
                        mergeGroups,
                        byId,
                        propertyTextures,
                        settings,
                        alphaCache,
                        GetPredictedPropertySizes,
                        diagnosticBudget,
                        DiagnosticGeneratedStateLimit);
                    diagnosticTimer.Stop();

                    var diagnosticSelected =
                        SelectTwoDimensionalMerges(
                            diagnostic.ProductionCandidates);
                    var diagnosticByGroupSet =
                        diagnostic.ProductionCandidates.ToDictionary(
                            SelectionGroupSetKey,
                            selection => SelectionSignature(selection),
                            StringComparer.Ordinal);

                    var additionalOrChanged =
                        diagnostic.ProductionCandidates
                            .Where(selection =>
                                !productionByGroupSet.TryGetValue(
                                    SelectionGroupSetKey(selection),
                                    out var productionSignature)
                                || !string.Equals(
                                    productionSignature,
                                    SelectionSignature(selection),
                                    StringComparison.Ordinal))
                            .OrderByDescending(selection =>
                                selection.PredictedSavingBytes)
                            .ThenBy(
                                SelectionGroupSetKey,
                                StringComparer.Ordinal)
                            .ToArray();
                    var missingOrChanged =
                        shadow2D.ProductionCandidates
                            .Where(selection =>
                                !diagnosticByGroupSet.TryGetValue(
                                    SelectionGroupSetKey(selection),
                                    out var diagnosticSignature)
                                || !string.Equals(
                                    diagnosticSignature,
                                    SelectionSignature(selection),
                                    StringComparison.Ordinal))
                            .OrderByDescending(selection =>
                                selection.PredictedSavingBytes)
                            .ThenBy(
                                SelectionGroupSetKey,
                                StringComparer.Ordinal)
                            .ToArray();

                    probes.Add(new SearchBudgetProbe(
                        diagnosticBudget,
                        diagnosticTimer.ElapsedMilliseconds,
                        false,
                        "",
                        diagnostic.ProductionCandidates.Count,
                        diagnosticSelected,
                        diagnostic.MaxStatesBeforeRetention,
                        diagnostic.PrunedStateCount,
                        productionPlanSignature
                            == SelectionPlanSignature(
                                diagnosticSelected),
                        additionalOrChanged,
                        missingOrChanged,
                        diagnostic.Rejected4096Candidates));
                }
                catch (DiagnosticSearchStateLimitExceededException exception)
                {
                    diagnosticTimer.Stop();
                    probes.Add(new SearchBudgetProbe(
                        diagnosticBudget,
                        diagnosticTimer.ElapsedMilliseconds,
                        true,
                        exception.Message));
                    // Do not attempt a still wider probe after the raw-state
                    // safety ceiling is reached; the diagnostic must never
                    // destabilize the normal production search.
                    break;
                }
            }

            var comparison = new SearchBudgetComparison(
                ShadowPackingStatesPerHost,
                productionTimer.ElapsedMilliseconds,
                shadow2D.ProductionCandidates.Count,
                shadow2D.MaxStatesBeforeRetention,
                shadow2D.PrunedStateCount,
                probes);

            return new PlanResult(selected, improvement, comparison);
        }

        private static string SelectionGroupSetKey(
            MergeSelection selection) =>
            string.Join(",", selection.GroupIds.OrderBy(id => id));

        private static string SelectionSignature(
            MergeSelection selection) =>
            SelectionGroupSetKey(selection)
            + "@" + selection.PlannedLayoutSize.x
            + "x" + selection.PlannedLayoutSize.y
            + ":" + selection.PredictedSavingBytes;

        private static string SelectionPlanSignature(
            IEnumerable<MergeSelection> selections) =>
            string.Join(";",
                selections
                    .Select(SelectionSignature)
                    .OrderBy(value => value, StringComparer.Ordinal));

        internal static List<MergeSelection> SelectTwoDimensionalMerges(
            IEnumerable<MergeSelection> candidates)
        {
            return SelectTwoDimensionalMerges(candidates, out _);
        }

        internal static List<MergeSelection> SelectTwoDimensionalMerges(
            IEnumerable<MergeSelection> candidates,
            out SelectionImprovement? improvement)
        {
            improvement = null;
            var ranked = candidates
                .Where(candidate => candidate.PackedRegions != null)
                .OrderByDescending(candidate => candidate.PredictedSavingBytes)
                .ThenByDescending(candidate => candidate.PredictedSavingRate)
                .ThenByDescending(candidate => candidate.GroupIds.Length)
                .ToArray();

            // Preserve the established Greedy result when the candidate set is
            // large. The exact solver below examines at most 2^16 subsets.
            var greedy = new List<MergeSelection>();
            var consumed = new HashSet<int>();
            foreach (var candidate in ranked)
            {
                if (candidate.GroupIds.Any(consumed.Contains)) continue;
                greedy.Add(candidate);
                consumed.UnionWith(candidate.GroupIds);
            }

            const int exactCandidateLimit = 16;
            if (ranked.Length <= 1 || ranked.Length > exactCandidateLimit)
                return greedy;

            // Set packing: two independent merges may save more memory than a
            // higher-ranked merge overlapping both. The candidate safety gates
            // and packed regions are unchanged; only disjoint plans are combined.
            var memberSets = ranked
                .Select(candidate => candidate.GroupIds.ToHashSet())
                .ToArray();
            var conflictMasks = new int[ranked.Length];
            for (var i = 0; i < ranked.Length; i++)
            {
                var mask = 1 << i;
                for (var j = 0; j < ranked.Length; j++)
                {
                    if (i != j
                        && ranked[j].GroupIds.Any(memberSets[i].Contains))
                        mask |= 1 << j;
                }
                conflictMasks[i] = mask;
            }

            var memo = new long[1 << ranked.Length];
            for (var mask = 1; mask < memo.Length; mask++)
                memo[mask] = -1;

            long BestSaving(int available)
            {
                if (available == 0) return 0;
                if (memo[available] >= 0) return memo[available];

                var first = 0;
                while ((available & (1 << first)) == 0) first++;
                var withoutFirst = available & ~(1 << first);
                var skipped = BestSaving(withoutFirst);
                var taken = ranked[first].PredictedSavingBytes
                    + BestSaving(available & ~conflictMasks[first]);
                return memo[available] = Math.Max(skipped, taken);
            }

            var remaining = memo.Length - 1;
            var optimum = BestSaving(remaining);
            // On equal predicted savings, retain the previously validated
            // Greedy selection and its original execution order.
            var greedySaving = greedy.Sum(
                candidate => candidate.PredictedSavingBytes);
            if (optimum <= greedySaving)
                return greedy;

            var selected = new List<MergeSelection>();
            while (remaining != 0)
            {
                var first = 0;
                while ((remaining & (1 << first)) == 0) first++;
                var withoutFirst = remaining & ~(1 << first);
                var taken = ranked[first].PredictedSavingBytes
                    + BestSaving(remaining & ~conflictMasks[first]);
                if (taken >= BestSaving(withoutFirst))
                {
                    selected.Add(ranked[first]);
                    remaining &= ~conflictMasks[first];
                }
                else
                {
                    remaining = withoutFirst;
                }
            }

            improvement = new SelectionImprovement(
                ranked.Length, greedy, greedySaving, optimum);
            return selected;
        }

        private static ShadowPackingDiagnostics EvaluateShadow2DPacking(
            IReadOnlyList<PreparedGroup> groups,
            IReadOnlyDictionary<int, PreparedGroup> byId,
            IReadOnlyDictionary<Material, IReadOnlyDictionary<string, Texture>>
                propertyTextures,
            AtlasSetting settings,
            Dictionary<Texture, bool> alphaCache,
            Func<int[], Vector2Int,
                IReadOnlyDictionary<string, Vector2Int>>
                propertySizeProvider,
            int stateBudget,
            int? generatedStateLimit = null)
        {
            if (stateBudget < 8 || stateBudget % 8 != 0)
                throw new ArgumentOutOfRangeException(nameof(stateBudget));
            if (generatedStateLimit is <= 0)
                throw new ArgumentOutOfRangeException(
                    nameof(generatedStateLimit));

            var maxStatesBeforeRetention = 0;
            long prunedStateCount = 0;
            var shadowAdoptable =
                new Dictionary<string, Candidate>(StringComparer.Ordinal);
            var shadowPackedRegions =
                new Dictionary<string, IReadOnlyDictionary<int, RectInt>>(
                    StringComparer.Ordinal);
            var evaluatedLayouts =
                new HashSet<string>(StringComparer.Ordinal);
            var projectionCache =
                new Dictionary<GroupLayoutKey, ShadowCostProjection>();
            var candidateCache =
                new Dictionary<GroupLayoutKey, Candidate>();
            var rejected4096Candidates =
                new List<RejectedCandidateDiagnostic>();

            void RecordRejected4096(Candidate candidate)
            {
                if (candidate.LayoutSize.x != 4096)
                    return;

                rejected4096Candidates.Add(
                    new RejectedCandidateDiagnostic(candidate));
                if (rejected4096Candidates.Count <= 40)
                    return;

                rejected4096Candidates.Sort((left, right) =>
                {
                    var saving = right.SavingBytes.CompareTo(left.SavingBytes);
                    if (saving != 0) return saving;
                    var groupCount =
                        right.GroupIds.Length.CompareTo(left.GroupIds.Length);
                    if (groupCount != 0) return groupCount;
                    return string.CompareOrdinal(
                        string.Join(",", left.GroupIds),
                        string.Join(",", right.GroupIds));
                });
                rejected4096Candidates.RemoveRange(
                    40, rejected4096Candidates.Count - 40);
            }

            ShadowCostProjection GetProjection(
                int[] ids,
                Vector2Int layoutSize)
            {
                // Shadow states append IDs in ascending order; lower-bound
                // and final-candidate callers also supply sorted, immutable IDs.
                // Avoid re-sorting and allocating strings on every cache hit.
                var layoutKey = new GroupLayoutKey(ids, layoutSize);
                if (projectionCache.TryGetValue(layoutKey, out var cached))
                    return cached;

                var selectedGroups = ids
                    .Select(id => byId[id])
                    .ToArray();
                var propertySizes = propertySizeProvider(
                    ids,
                    layoutSize);
                var baseline = TextureCostPlan.Combine(
                    selectedGroups.Select(
                        group => group.BaselineCost));
                var merged = BuildGeneratedCost(
                    selectedGroups,
                    propertyTextures,
                    propertySizes,
                    alphaCache,
                    UnknownAlphaAssumption.AssumePresent);
                var projection = new ShadowCostProjection(
                    baseline,
                    merged,
                    propertySizes);
                projectionCache[layoutKey] = projection;
                return projection;
            }

            Candidate GetCandidate(int[] ids, Vector2Int layoutSize)
            {
                // Shadow states append IDs in ascending order; lower-bound
                // and final-candidate callers also supply sorted, immutable IDs.
                // Avoid re-sorting and allocating strings on every cache hit.
                var layoutKey = new GroupLayoutKey(ids, layoutSize);
                if (candidateCache.TryGetValue(layoutKey, out var cached))
                    return cached;

                var projection = GetProjection(ids, layoutSize);
                var candidate = new Candidate(
                    ids,
                    projection.Baseline,
                    projection.Merged,
                    layoutSize,
                    projection.PropertySizes);
                candidateCache[layoutKey] = candidate;
                return candidate;
            }

            void ScoreState(
                ShadowPackingState state,
                int targetWidth)
            {
                if (state.GroupIds.Length == 0)
                {
                    state.ProjectedPixelDelta = 0;
                    state.ProjectedMemoryDelta = 0;
                    return;
                }

                var layoutSize = new Vector2Int(
                    targetWidth,
                    GetCroppedHeight(
                        targetWidth,
                        Math.Max(1, state.MaxHeight)));
                var projection = GetProjection(
                    state.GroupIds,
                    layoutSize);
                state.ProjectedPixelDelta =
                    projection.Merged.PixelCount
                    - projection.Baseline.PixelCount;
                state.ProjectedMemoryDelta =
                    projection.Merged.FullResidentBytes
                    - projection.Baseline.FullResidentBytes;
            }

            List<ShadowPackingState> SelectPropertyAwareStates(
                IEnumerable<ShadowPackingState> source)
            {
                // Existing states cannot contain the group currently being
                // expanded, while generated states always do. generated itself
                // is deduplicated by StateKey, so the union is already unique.
                var unique = source.ToArray();
                maxStatesBeforeRetention = Math.Max(
                    maxStatesBeforeRetention, unique.Length);

                if (unique.Length <= stateBudget)
                {
                    return unique
                        .OrderBy(
                            state =>
                                state.GroupIds.Length == 0 ? 0 : 1)
                        .ThenBy(
                            state => state.ProjectedPixelDelta)
                        .ThenBy(
                            state => state.ProjectedMemoryDelta)
                        .ThenBy(state => state.MaxHeight)
                        .ThenBy(state => state.SkylineArea)
                        .ThenBy(
                            state => state,
                            ShadowPackingStateStableComparer.Instance)
                        .ToList();
                }

                var selected =
                    new HashSet<ShadowPackingState>();
                var empty = unique.FirstOrDefault(
                    state => state.GroupIds.Length == 0);
                if (empty != null)
                    selected.Add(empty);

                void Retain(
                    IEnumerable<ShadowPackingState> ordered,
                    int count)
                {
                    var added = 0;
                    foreach (var state in ordered)
                    {
                        if (selected.Count
                                >= stateBudget
                            || added >= count)
                        {
                            break;
                        }

                        if (!selected.Add(state))
                            continue;

                        added += 1;
                    }
                }

                // Current property cost is useful, but it is not monotonic:
                // a currently-unprofitable pair can become profitable when a
                // third group shares an already-paid property texture. Retain
                // several independent fronts instead of pruning only by the
                // current merge result.
                Retain(
                    unique
                        .OrderBy(
                            state => state.ProjectedPixelDelta)
                        .ThenBy(
                            state => state.ProjectedMemoryDelta)
                        .ThenBy(state => state.MaxHeight)
                        .ThenBy(
                            state => state,
                            ShadowPackingStateStableComparer.Instance),
                    stateBudget / 2);
                Retain(
                    unique
                        .OrderBy(
                            state => state.ProjectedMemoryDelta)
                        .ThenBy(
                            state => state.ProjectedPixelDelta)
                        .ThenBy(state => state.MaxHeight)
                        .ThenBy(
                            state => state,
                            ShadowPackingStateStableComparer.Instance),
                    stateBudget / 4);
                Retain(
                    unique
                        .OrderBy(state => state.MaxHeight)
                        .ThenBy(state => state.SkylineArea)
                        .ThenByDescending(
                            state => state.GroupIds.Length)
                        .ThenBy(
                            state => state,
                            ShadowPackingStateStableComparer.Instance),
                    stateBudget / 8);
                Retain(
                    unique
                        .OrderByDescending(
                            state => state.BaselineBytes)
                        .ThenByDescending(
                            state => state.GroupIds.Length)
                        .ThenBy(state => state.MaxHeight)
                        .ThenBy(
                            state => state,
                            ShadowPackingStateStableComparer.Instance),
                    stateBudget / 8);

                if (selected.Count < stateBudget)
                {
                    Retain(
                        unique
                            .OrderBy(
                                state =>
                                    state.ProjectedPixelDelta)
                            .ThenBy(
                                state =>
                                    state.ProjectedMemoryDelta)
                            .ThenBy(state => state.MaxHeight)
                            .ThenBy(state => state.SkylineArea)
                            .ThenBy(
                                state => state,
                                ShadowPackingStateStableComparer.Instance),
                        stateBudget);
                }

                prunedStateCount += unique.Length - selected.Count;

                return selected
                    .OrderBy(
                        state =>
                            state.GroupIds.Length == 0 ? 0 : 1)
                    .ThenBy(
                        state => state.ProjectedPixelDelta)
                    .ThenBy(
                        state => state.ProjectedMemoryDelta)
                    .ThenBy(state => state.MaxHeight)
                    .ThenBy(state => state.SkylineArea)
                    .ThenBy(
                        state => state,
                        ShadowPackingStateStableComparer.Instance)
                    .ToList();
            }

            foreach (var targetWidth in
                     AtlasTexture.AutoAtlasTextureSizeCandidates)
            {
                if (targetWidth < PackingGridUnit
                    || targetWidth % PackingGridUnit != 0)
                {
                    continue;
                }

                var eligibleGroups = groups
                    .Where(group =>
                        group.StandaloneMetrics != null
                        && group.BaselineCostKnown
                        && group.TwoDimensionalProfileHeights.Any(profile =>
                            profile.Key.AtlasWidth == targetWidth
                            && profile.Key.RegionWidth % PackingGridUnit == 0
                            && profile.Value <= targetWidth))
                    .OrderBy(group => group.Id)
                    .ToArray();

                if (eligibleGroups.Length < 2)
                    continue;

                var columnCount = targetWidth / PackingGridUnit;
                var states = new List<ShadowPackingState>
                {
                    new(
                        new int[columnCount],
                        Array.Empty<int>(),
                        0),
                };

                foreach (var group in eligibleGroups)
                {
                    var generated =
                        new Dictionary<
                            ShadowPackingStateKey,
                            ShadowPackingState>();
                    var snapshot = states
                        .Where(state =>
                            state.GroupIds.Length + 1
                                <= MaxMergeGroupCount)
                        .ToArray();

                    foreach (var state in snapshot)
                    {
                        foreach (var profile in group.TwoDimensionalProfileHeights
                                     .Where(profile =>
                                         profile.Key.AtlasWidth == targetWidth
                                         && profile.Key.RegionWidth
                                             % PackingGridUnit == 0)
                                     .OrderBy(profile =>
                                         profile.Key.RegionWidth))
                        {
                            var profileWidth = profile.Key.RegionWidth;
                            var profileHeight = profile.Value;
                            var widthColumns =
                                profileWidth / PackingGridUnit;
                            if (widthColumns <= 0
                                || widthColumns > columnCount
                                || profileHeight > targetWidth)
                            {
                                continue;
                            }

                            for (var x = 0;
                                 x + widthColumns <= columnCount;
                                 x += 1)
                            {
                                if (generatedStateLimit is { } limit
                                    && generated.Count >= limit)
                                {
                                    throw new
                                        DiagnosticSearchStateLimitExceededException(
                                            stateBudget,
                                            targetWidth,
                                            group.Id,
                                            limit);
                                }

                                var y = 0;
                                for (var column = x;
                                     column < x + widthColumns;
                                     column += 1)
                                {
                                    y = Math.Max(
                                        y,
                                        state.Skyline[column]);
                                }

                                var top = y + profileHeight;
                                if (top > targetWidth)
                                    continue;

                                var skyline =
                                    (int[])state.Skyline.Clone();
                                for (var column = x;
                                     column < x + widthColumns;
                                     column += 1)
                                {
                                    skyline[column] = top;
                                }

                                var nextGroupIds =
                                    new int[state.GroupIds.Length + 1];
                                Array.Copy(
                                    state.GroupIds,
                                    nextGroupIds,
                                    state.GroupIds.Length);
                                nextGroupIds[nextGroupIds.Length - 1] =
                                    group.Id;

                                var next = new ShadowPackingState(
                                    skyline,
                                    nextGroupIds,
                                    state.BaselineBytes
                                        + group.BaselineCost
                                            .FullResidentBytes,
                                    new ShadowPackingPlacement(
                                        group.Id,
                                        new RectInt(
                                            x * PackingGridUnit,
                                            y,
                                            profileWidth,
                                            profileHeight),
                                        state.PlacementTail));

                                // Same key means the same group set and exact
                                // skyline, therefore the same projected layout
                                // and property cost. Deduplicate geometry before
                                // running the property-cost projection.
                                if (!generated.ContainsKey(next.StateKey))
                                {
                                    generated[next.StateKey] = next;
                                }
                            }
                        }
                    }

                    if (generated.Count == 0)
                        continue;

                    var undominated = RemoveDominatedSkylineStates(
                        generated.Values);
                    foreach (var generatedState in undominated)
                        ScoreState(generatedState, targetWidth);

                    states.AddRange(undominated);
                    states = SelectPropertyAwareStates(states);
                }

                foreach (var state in states.Where(
                             state => state.GroupIds.Length >= 2))
                {
                    var ids = state.GroupIds
                        .OrderBy(id => id)
                        .ToArray();
                    var key = string.Join(",", ids);
                    var layoutSize = new Vector2Int(
                        targetWidth,
                        GetCroppedHeight(
                            targetWidth,
                            Math.Max(1, state.MaxHeight)));
                    var layoutKey =
                        key + "@" + layoutSize.x + "x" + layoutSize.y;
                    if (!evaluatedLayouts.Add(layoutKey))
                        continue;

                    var candidate = GetCandidate(ids, layoutSize);
                    if (!candidate.IsAdoptable)
                    {
                        RecordRejected4096(candidate);
                        continue;
                    }

                    if (!shadowAdoptable.TryGetValue(
                            key,
                            out var priorCandidate)
                        || candidate.SavingBytes
                            > priorCandidate.SavingBytes
                        || (candidate.SavingBytes
                                == priorCandidate.SavingBytes
                            && candidate.LayoutSize.y
                                < priorCandidate.LayoutSize.y))
                    {
                        shadowAdoptable[key] = candidate;
                        var regions = new Dictionary<int, RectInt>();
                        for (var placement = state.PlacementTail;
                             placement != null;
                             placement = placement.Previous)
                            regions.Add(placement.GroupId, placement.Region);
                        shadowPackedRegions[key] = regions;
                    }
                }
            }

            var rejectedDiagnostics = rejected4096Candidates
                .OrderByDescending(candidate => candidate.SavingBytes)
                .ThenByDescending(candidate => candidate.GroupIds.Length)
                .ThenBy(candidate =>
                    string.Join(",", candidate.GroupIds),
                    StringComparer.Ordinal)
                .Take(20)
                .ToArray();

            return new ShadowPackingDiagnostics(
                shadowAdoptable.Select(pair => new MergeSelection(
                    pair.Value, shadowPackedRegions[pair.Key])).ToArray(),
                maxStatesBeforeRetention,
                prunedStateCount,
                rejectedDiagnostics);
        }

        private static int GetUsedHeightPixels(
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

        private static PropertyResolutionRequirements
            BuildPredictedPropertyRequirements(
                HashSet<Material> materials,
                IReadOnlyDictionary<
                    Material,
                    IReadOnlyDictionary<string, Texture>>
                    propertyTextures)
        {
            var grouping = new MaterialGroupingContext(
                materials,
                propertyTextures,
                null);
            var properties = grouping.GroupMaterials
                .SelectMany(group => group.GroupedTexture.Keys)
                .Distinct()
                .OrderBy(property => property, StringComparer.Ordinal);

            var result = new Dictionary<
                string, PropertyResolutionRequirement>(
                StringComparer.Ordinal);
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
                        : new Vector2Int(
                            primary.width,
                            primary.height));
                    sourceSizes.Add(
                        new Vector2Int(
                            source.width,
                            source.height));
                }

                result[property] =
                    new PropertyResolutionRequirement(
                        primarySizes,
                        sourceSizes);
            }

            return new PropertyResolutionRequirements(result);
        }

        private static IReadOnlyDictionary<string, Vector2Int>
            BuildPredictedPropertySizes(
                PropertyResolutionRequirements requirements,
                Vector2Int layoutSize)
        {
            var result = new Dictionary<string, Vector2Int>(
                StringComparer.Ordinal);
            foreach (var pair in requirements.ByProperty)
            {
                result[pair.Key] =
                    AtlasTextureResolutionPlan.CalculatePropertySize(
                        layoutSize,
                        autoSize: true,
                        pair.Value.PrimarySizes,
                        pair.Value.SourceSizes);
            }

            return result;
        }

        internal static float GetTwoDimensionalLocalPadding(
            float atlasPadding, int atlasWidth, int regionWidth)
        {
            if (atlasWidth <= 0 || regionWidth <= 0)
                throw new ArgumentOutOfRangeException(
                    nameof(regionWidth), "Atlas and region widths must be positive.");
            return atlasPadding * ((float)atlasWidth / regionWidth);
        }

        private static AtlasSetting CreateTwoDimensionalProfileSettings(
            AtlasSetting settings, int atlasWidth, int regionWidth)
        {
            return new AtlasSetting
            {
                AtlasTextureSize = settings.AtlasTextureSize,
                CustomAspect = settings.CustomAspect,
                AtlasTextureHeightSize = settings.AtlasTextureHeightSize,
                AtlasTargetUVChannel = settings.AtlasTargetUVChannel,
                UsePrimaryMaximumTexture = settings.UsePrimaryMaximumTexture,
                PrimaryTextureProperty = settings.PrimaryTextureProperty,
                ForceSizePriority = settings.ForceSizePriority,
                IslandPadding = GetTwoDimensionalLocalPadding(
                    settings.IslandPadding, atlasWidth, regionWidth),
                IncludeDisabledRenderer = settings.IncludeDisabledRenderer,
                BackGroundColor = settings.BackGroundColor,
                PixelNormalize = settings.PixelNormalize,
                DownScaleAlgorithm = settings.DownScaleAlgorithm,
                ForceSetTexture = settings.ForceSetTexture,
                TextureFineTuning = settings.TextureFineTuning,
            };
        }

        private static AtlasTexture.AtlasExecutionMetrics? AnalyzeAtlas(
            IDomain domain,
            AtlasSetting settings,
            PreparedGroup group)
        {
            group.TwoDimensionalProfileHeights.Clear();

            var prepared = AtlasTexture.PrepareAtlasContext(
                domain,
                group.Materials,
                group.Renderers,
                settings);
            if (prepared is null)
                return null;

            using (prepared)
            {
                AtlasTexture.AtlasExecutionMetrics? standalone = null;

                foreach (var size in AtlasTexture.AutoAtlasTextureSizeCandidates)
                {
                    try
                    {
                        if (!AtlasTexture.TryAnalyzePreparedAtlasAtSize(
                                domain,
                                prepared,
                                new List<IIslandSizePriorityTuner?>(),
                                settings,
                                size,
                                out var metrics))
                        {
                            continue;
                        }

                        group.TwoDimensionalProfileHeights[(size, size)] =
                            GetUsedHeightPixels(metrics);
                        standalone ??= metrics;

                        // NFDH's padding is expressed in local UV units.
                        // A subregion of width 'size' inside a wider final
                        // atlas needs the same padding in final-atlas pixels:
                        // localPadding = globalPadding * finalWidth / size.
                        foreach (var atlasWidth in
                                 AtlasTexture.AutoAtlasTextureSizeCandidates)
                        {
                            if (atlasWidth <= size) continue;

                            if (settings.IslandPadding == 0f)
                            {
                                group.TwoDimensionalProfileHeights[
                                    (atlasWidth, size)] =
                                    GetUsedHeightPixels(metrics);
                                continue;
                            }

                            var profileSettings =
                                CreateTwoDimensionalProfileSettings(
                                    settings, atlasWidth, size);
                            try
                            {
                                if (AtlasTexture.TryAnalyzePreparedAtlasAtSize(
                                        domain,
                                        prepared,
                                        new List<IIslandSizePriorityTuner?>(),
                                        profileSettings,
                                        size,
                                        out var paddedMetrics))
                                {
                                    group.TwoDimensionalProfileHeights[
                                        (atlasWidth, size)] =
                                        GetUsedHeightPixels(paddedMetrics);
                                }
                            }
                            catch (ArgumentException exception)
                                when (AutoAtlas2DMergeIslandRelocator
                                    .IsNfdhComparerFailure(exception))
                            {
                                // This padded profile cannot be relocated
                                // reliably at the selected width.
                            }
                        }
                    }
                    catch (ArgumentException exception)
                        when (AutoAtlas2DMergeIslandRelocator.IsNfdhComparerFailure(
                            exception))
                    {
                        // A profile that cannot be relocated reliably at one
                        // candidate width is unavailable. Wider widths can still
                        // produce a valid lossless profile.
                    }
                }

                return standalone;
            }
        }

        private static TextureCostPlan? TryBuildOriginalCost(
            IDomain domain,
            PreparedGroup group,
            IReadOnlyDictionary<Material, IReadOnlyDictionary<string, Texture>> propertyTextures)
        {
            var entries = new List<TextureCostEntry>();
            var bytesPerTexel = new Dictionary<Texture, double>();

            foreach (var texture in group.SourceTextures)
            {
                if (!AtlasTexture.TryGetOriginalTextureCostInfo(
                        domain,
                        texture,
                        out var block,
                        out var mipCount))
                {
                    return null;
                }

                entries.Add(new TextureCostEntry(
                    texture.width,
                    texture.height,
                    block,
                    mipCount));
                bytesPerTexel[texture] =
                    (double)block.Size / (block.Width * block.Height);
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

                var pixelSources = new HashSet<Texture>();
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
                    }

                    if (groupUsedProperties == 0) continue;

                    usedPropertyCount[group.Id] = groupUsedProperties;
                }

                if (usedPropertyCount.Count == 0) continue;

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
                    CalculateMipCount(property.Value.x, property.Value.y)));
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

        private static long CalculateBytes(TextureCostEntry entry)
        {
            long total = 0;
            var width = entry.Width;
            var height = entry.Height;

            for (var mip = 0; mip < entry.MipCount; mip += 1)
            {
                var blocksX =
                    ((long)width + entry.BlockWidth - 1) / entry.BlockWidth;
                var blocksY =
                    ((long)height + entry.BlockHeight - 1) / entry.BlockHeight;
                total += blocksX * blocksY * entry.BlockSize;

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


    }
}
