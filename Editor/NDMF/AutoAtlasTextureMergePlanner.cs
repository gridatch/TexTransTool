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
        // Evaluation build: compare 1024 retained skyline states against
        // the validated 512-state debug.608 baseline. Keep all other pruning
        // and packing rules unchanged for a controlled A/B comparison.
        private const int ShadowPackingStatesPerHost = 1024;
        private const int PackingGridUnit = 256;
        private const double UnknownDemandLog2 = -32d;
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
            internal readonly Dictionary<int, AtlasTexture.AtlasExecutionMetrics>
                LayoutProfiles = new();
            // Heights include the final atlas's required padding, expressed
            // in pixels at the atomic group's own profile width.
            internal readonly Dictionary<(int AtlasWidth, int RegionWidth), int>
                TwoDimensionalProfileHeights = new();
            internal int StandaloneWidthPixels;
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

        internal sealed class PlanResult
        {
            internal readonly IReadOnlyList<MergeSelection> Selections;

            // Temporary diagnostics for validating AutoAtlas processing cost.
            internal readonly long PlanningTimeMilliseconds;
            internal readonly long StandaloneAnalysisTimeMilliseconds;
            internal readonly long StandaloneRelocationTimeMilliseconds;
            internal readonly int StandaloneAnalysisCount;
            internal readonly long StandalonePreserveSetupMilliseconds;
            internal readonly long StandaloneAtlasContextMilliseconds;
            internal readonly long StandaloneIslandNormalizeMilliseconds;
            internal readonly long StandaloneRelocationStageMilliseconds;
            internal readonly long StandaloneAllProfileRelocatorMilliseconds;
            internal readonly long StandaloneCloneAndScaleMilliseconds;
            internal readonly long StandaloneReferenceIslandMilliseconds;
            internal readonly long StandaloneIslandDescriptionMilliseconds;
            internal readonly long StandaloneSizePriorityMilliseconds;
            internal readonly long StandalonePostProcessMilliseconds;
            internal readonly long StandaloneResolutionPlanMilliseconds;

            internal readonly long Shadow2DPlanningMilliseconds;
            internal readonly int PropertyRequirementBuildCount;
            internal readonly long PropertyRequirementBuildMilliseconds;

            // Temporary diagnostics for evaluating whether the standalone
            // preprocessing state can be safely reused later in the pass.
            internal readonly int PreparedGroupCount;
            internal readonly int SharedRendererCount;
            internal readonly int GroupsWithSharedRendererCount;
            internal readonly int GroupPairsSharingRendererCount;
            internal readonly int GroupPairsSharingSourceMeshCount;
            internal readonly int CrossAtomicMaterialGroupCount;

            // Temporary diagnostics for determining why practical merge
            // opportunities are or are not selected.


            // Temporary shadow 2D-packing diagnostics. These do not affect the
            // currently selected band-based merge plan.
            internal readonly int ShadowProfileCount;
            internal readonly int ShadowGroupsWithMultipleProfiles;
            internal readonly int ShadowHostCount;
            internal readonly int ShadowEvaluatedCandidateCount;

            internal readonly int ShadowEvaluatedUsingWiderProfileCount;
            internal readonly int ShadowAdoptableCandidateCount;

            internal readonly int ShadowAdoptableUsingWiderProfileCount;
            internal readonly int ShadowSelectedCount;

            internal readonly int ShadowRejectedPixelCount;
            internal readonly int ShadowRejectedMemoryCount;
            internal readonly int ShadowRejectedMaxDimensionCount;
            internal readonly int ShadowRejectedSamplingCount;
            internal readonly int ShadowRejectedStreamingCount;
            internal readonly int ShadowPixelRejectedEqualCount;
            internal readonly int ShadowPixelRejectedGreaterCount;
            internal readonly int ShadowPixelGateOnlyCount;
            internal readonly int ShadowPixelRejectedAlsoMemoryCount;
            internal readonly int ShadowPixelRejectedAlsoMaxDimensionCount;
            internal readonly int ShadowPixelRejectedAlsoSamplingCount;
            internal readonly int ShadowPixelRejectedAlsoStreamingCount;
            internal readonly long ShadowBestBaselinePixels;
            internal readonly long ShadowBestBaselineBytes;
            internal readonly long ShadowBestMergedBytes;
            internal readonly long ShadowBestMergedPixels;
            internal readonly int ShadowBestGroupCount;
            internal readonly Vector2Int ShadowBestLayoutSize;
            internal readonly string ShadowBestBaselineSizeSummary;
            internal readonly string ShadowBestPropertySizeSummary;
            internal readonly string ShadowBestRejectionReason;
            internal readonly int ShadowOptimisticSourceGroupCount;
            internal readonly bool ShadowOptimisticExhaustive;
            internal readonly long ShadowOptimisticLowerBoundMilliseconds;
            internal readonly int ShadowOptimisticEvaluatedCount;
            internal readonly int ShadowOptimisticPixelViableCount;
            internal readonly long ShadowOptimisticBestBaselinePixels;
            internal readonly long ShadowOptimisticBestMergedPixels;
            internal readonly int ShadowOptimisticBestGroupCount;
            internal readonly Vector2Int ShadowOptimisticBestLayoutSize;
            internal readonly int ShadowProjectionEvaluationCount;
            internal readonly int ShadowSafetyEvaluationCount;
            internal readonly long ShadowProjectionEvaluationMilliseconds;
            internal readonly long ShadowStateSelectionMilliseconds;
            internal readonly long ShadowPlacementAttemptCount;
            internal readonly long ShadowUniqueGeneratedStateCount;
            internal readonly long ShadowScoreStateCallCount;
            internal readonly string ShadowBestSamplingSummary;
            internal readonly StreamingRegressionExample? ShadowStreamingExample;
            internal readonly int ShadowStreamingRegressingCandidateCount;

            internal PlanResult(
                IReadOnlyList<MergeSelection> selections,
                long planningTimeMilliseconds = 0,
                long standaloneAnalysisTimeMilliseconds = 0,
                long standaloneRelocationTimeMilliseconds = 0,
                int standaloneAnalysisCount = 0,
                long standalonePreserveSetupMilliseconds = 0,
                long standaloneAtlasContextMilliseconds = 0,
                long standaloneIslandNormalizeMilliseconds = 0,
                long standaloneRelocationStageMilliseconds = 0,
                long standaloneAllProfileRelocatorMilliseconds = 0,
                long standaloneCloneAndScaleMilliseconds = 0,
                long standaloneReferenceIslandMilliseconds = 0,
                long standaloneIslandDescriptionMilliseconds = 0,
                long standaloneSizePriorityMilliseconds = 0,
                long standalonePostProcessMilliseconds = 0,
                long standaloneResolutionPlanMilliseconds = 0,

                long shadow2DPlanningMilliseconds = 0,
                int propertyRequirementBuildCount = 0,
                long propertyRequirementBuildMilliseconds = 0,
                int preparedGroupCount = 0,
                int sharedRendererCount = 0,
                int groupsWithSharedRendererCount = 0,
                int groupPairsSharingRendererCount = 0,
                int groupPairsSharingSourceMeshCount = 0,
                int crossAtomicMaterialGroupCount = 0,

                int shadowProfileCount = 0,
                int shadowGroupsWithMultipleProfiles = 0,
                int shadowHostCount = 0,
                int shadowEvaluatedCandidateCount = 0,

                int shadowEvaluatedUsingWiderProfileCount = 0,
                int shadowAdoptableCandidateCount = 0,

                int shadowAdoptableUsingWiderProfileCount = 0,
                int shadowSelectedCount = 0,

                int shadowRejectedPixelCount = 0,
                int shadowRejectedMemoryCount = 0,
                int shadowRejectedMaxDimensionCount = 0,
                int shadowRejectedSamplingCount = 0,
                int shadowRejectedStreamingCount = 0,
                int shadowPixelRejectedEqualCount = 0,
                int shadowPixelRejectedGreaterCount = 0,
                int shadowPixelGateOnlyCount = 0,
                int shadowPixelRejectedAlsoMemoryCount = 0,
                int shadowPixelRejectedAlsoMaxDimensionCount = 0,
                int shadowPixelRejectedAlsoSamplingCount = 0,
                int shadowPixelRejectedAlsoStreamingCount = 0,
                long shadowBestBaselinePixels = 0,
                long shadowBestMergedPixels = 0,
                long shadowBestBaselineBytes = 0,
                long shadowBestMergedBytes = 0,
                int shadowBestGroupCount = 0,
                Vector2Int shadowBestLayoutSize = default,
                string shadowBestBaselineSizeSummary = "",
                string shadowBestPropertySizeSummary = "",
                string shadowBestRejectionReason = "",
                int shadowOptimisticSourceGroupCount = 0,
                bool shadowOptimisticExhaustive = false,
                long shadowOptimisticLowerBoundMilliseconds = 0,
                int shadowOptimisticEvaluatedCount = 0,
                int shadowOptimisticPixelViableCount = 0,
                long shadowOptimisticBestBaselinePixels = 0,
                long shadowOptimisticBestMergedPixels = 0,
                int shadowOptimisticBestGroupCount = 0,
                Vector2Int shadowOptimisticBestLayoutSize = default,
                int shadowProjectionEvaluationCount = 0,
                int shadowSafetyEvaluationCount = 0,
                long shadowProjectionEvaluationMilliseconds = 0,
                long shadowStateSelectionMilliseconds = 0,
                long shadowPlacementAttemptCount = 0,
                long shadowUniqueGeneratedStateCount = 0,
                long shadowScoreStateCallCount = 0,
                string shadowBestSamplingSummary = "",
                StreamingRegressionExample? shadowStreamingExample = null,
                int shadowStreamingRegressingCandidateCount = 0)
            {
                Selections = selections;
                ShadowStreamingExample = shadowStreamingExample;
                ShadowStreamingRegressingCandidateCount =
                    shadowStreamingRegressingCandidateCount;
                PlanningTimeMilliseconds = planningTimeMilliseconds;
                StandaloneAnalysisTimeMilliseconds =
                    standaloneAnalysisTimeMilliseconds;
                StandaloneRelocationTimeMilliseconds =
                    standaloneRelocationTimeMilliseconds;
                StandaloneAnalysisCount = standaloneAnalysisCount;
                StandalonePreserveSetupMilliseconds =
                    standalonePreserveSetupMilliseconds;
                StandaloneAtlasContextMilliseconds =
                    standaloneAtlasContextMilliseconds;
                StandaloneIslandNormalizeMilliseconds =
                    standaloneIslandNormalizeMilliseconds;
                StandaloneRelocationStageMilliseconds =
                    standaloneRelocationStageMilliseconds;
                StandaloneAllProfileRelocatorMilliseconds =
                    standaloneAllProfileRelocatorMilliseconds;
                StandaloneCloneAndScaleMilliseconds =
                    standaloneCloneAndScaleMilliseconds;
                StandaloneReferenceIslandMilliseconds =
                    standaloneReferenceIslandMilliseconds;
                StandaloneIslandDescriptionMilliseconds =
                    standaloneIslandDescriptionMilliseconds;
                StandaloneSizePriorityMilliseconds =
                    standaloneSizePriorityMilliseconds;
                StandalonePostProcessMilliseconds =
                    standalonePostProcessMilliseconds;
                StandaloneResolutionPlanMilliseconds =
                    standaloneResolutionPlanMilliseconds;

                Shadow2DPlanningMilliseconds =
                    shadow2DPlanningMilliseconds;
                PropertyRequirementBuildCount =
                    propertyRequirementBuildCount;
                PropertyRequirementBuildMilliseconds =
                    propertyRequirementBuildMilliseconds;
                PreparedGroupCount = preparedGroupCount;
                SharedRendererCount = sharedRendererCount;
                GroupsWithSharedRendererCount = groupsWithSharedRendererCount;
                GroupPairsSharingRendererCount = groupPairsSharingRendererCount;
                GroupPairsSharingSourceMeshCount =
                    groupPairsSharingSourceMeshCount;
                CrossAtomicMaterialGroupCount = crossAtomicMaterialGroupCount;

                ShadowProfileCount = shadowProfileCount;
                ShadowGroupsWithMultipleProfiles =
                    shadowGroupsWithMultipleProfiles;
                ShadowHostCount = shadowHostCount;
                ShadowEvaluatedCandidateCount =
                    shadowEvaluatedCandidateCount;

                ShadowEvaluatedUsingWiderProfileCount =
                    shadowEvaluatedUsingWiderProfileCount;
                ShadowAdoptableCandidateCount =
                    shadowAdoptableCandidateCount;

                ShadowAdoptableUsingWiderProfileCount =
                    shadowAdoptableUsingWiderProfileCount;
                ShadowSelectedCount = shadowSelectedCount;

                ShadowRejectedPixelCount = shadowRejectedPixelCount;
                ShadowRejectedMemoryCount = shadowRejectedMemoryCount;
                ShadowRejectedMaxDimensionCount =
                    shadowRejectedMaxDimensionCount;
                ShadowRejectedSamplingCount = shadowRejectedSamplingCount;
                ShadowRejectedStreamingCount = shadowRejectedStreamingCount;
                ShadowPixelRejectedEqualCount = shadowPixelRejectedEqualCount;
                ShadowPixelRejectedGreaterCount =
                    shadowPixelRejectedGreaterCount;
                ShadowPixelGateOnlyCount = shadowPixelGateOnlyCount;
                ShadowPixelRejectedAlsoMemoryCount =
                    shadowPixelRejectedAlsoMemoryCount;
                ShadowPixelRejectedAlsoMaxDimensionCount =
                    shadowPixelRejectedAlsoMaxDimensionCount;
                ShadowPixelRejectedAlsoSamplingCount =
                    shadowPixelRejectedAlsoSamplingCount;
                ShadowPixelRejectedAlsoStreamingCount =
                    shadowPixelRejectedAlsoStreamingCount;
                ShadowBestBaselinePixels = shadowBestBaselinePixels;
                ShadowBestMergedPixels = shadowBestMergedPixels;
                ShadowBestBaselineBytes = shadowBestBaselineBytes;
                ShadowBestMergedBytes = shadowBestMergedBytes;
                ShadowBestGroupCount = shadowBestGroupCount;
                ShadowBestLayoutSize = shadowBestLayoutSize;
                ShadowBestBaselineSizeSummary =
                    shadowBestBaselineSizeSummary;
                ShadowBestPropertySizeSummary =
                    shadowBestPropertySizeSummary;
                ShadowBestRejectionReason = shadowBestRejectionReason;
                ShadowOptimisticSourceGroupCount =
                    shadowOptimisticSourceGroupCount;
                ShadowOptimisticExhaustive = shadowOptimisticExhaustive;
                ShadowOptimisticLowerBoundMilliseconds =
                    shadowOptimisticLowerBoundMilliseconds;
                ShadowOptimisticEvaluatedCount =
                    shadowOptimisticEvaluatedCount;
                ShadowOptimisticPixelViableCount =
                    shadowOptimisticPixelViableCount;
                ShadowOptimisticBestBaselinePixels =
                    shadowOptimisticBestBaselinePixels;
                ShadowOptimisticBestMergedPixels =
                    shadowOptimisticBestMergedPixels;
                ShadowOptimisticBestGroupCount =
                    shadowOptimisticBestGroupCount;
                ShadowOptimisticBestLayoutSize =
                    shadowOptimisticBestLayoutSize;
                ShadowProjectionEvaluationCount =
                    shadowProjectionEvaluationCount;
                ShadowSafetyEvaluationCount =
                    shadowSafetyEvaluationCount;
                ShadowProjectionEvaluationMilliseconds =
                    shadowProjectionEvaluationMilliseconds;
                ShadowStateSelectionMilliseconds =
                    shadowStateSelectionMilliseconds;
                ShadowPlacementAttemptCount =
                    shadowPlacementAttemptCount;
                ShadowUniqueGeneratedStateCount =
                    shadowUniqueGeneratedStateCount;
                ShadowScoreStateCallCount =
                    shadowScoreStateCallCount;
                ShadowBestSamplingSummary = shadowBestSamplingSummary;
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
            internal readonly bool UsesWiderProfile;
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
                bool usesWiderProfile,
                ShadowPackingPlacement? placementTail = null)
            {
                PlacementTail = placementTail;
                Skyline = skyline;
                GroupIds = groupIds;
                BaselineBytes = baselineBytes;
                UsesWiderProfile = usesWiderProfile;

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
            internal readonly int ProfileCount;
            internal readonly int GroupsWithMultipleProfiles;
            internal readonly int HostCount;
            internal readonly int EvaluatedCandidateCount;

            internal readonly int EvaluatedUsingWiderProfileCount;
            internal readonly int AdoptableCandidateCount;

            internal readonly int AdoptableUsingWiderProfileCount;
            internal readonly int SelectedCount;

            internal readonly int RejectedPixelCount;
            internal readonly int RejectedMemoryCount;
            internal readonly int RejectedMaxDimensionCount;
            internal readonly int RejectedSamplingCount;
            internal readonly int RejectedStreamingCount;
            internal readonly int PixelRejectedEqualCount;
            internal readonly int PixelRejectedGreaterCount;
            internal readonly int PixelGateOnlyCount;
            internal readonly int PixelRejectedAlsoMemoryCount;
            internal readonly int PixelRejectedAlsoMaxDimensionCount;
            internal readonly int PixelRejectedAlsoSamplingCount;
            internal readonly int PixelRejectedAlsoStreamingCount;
            internal readonly long BestBaselinePixels;
            internal readonly long BestBaselineBytes;
            internal readonly long BestMergedBytes;
            internal readonly long BestMergedPixels;
            internal readonly int BestGroupCount;
            internal readonly Vector2Int BestLayoutSize;
            internal readonly string BestBaselineSizeSummary;
            internal readonly string BestPropertySizeSummary;
            internal readonly string BestRejectionReason;
            internal readonly int OptimisticSourceGroupCount;
            internal readonly bool OptimisticExhaustive;
            internal readonly long OptimisticLowerBoundMilliseconds;
            internal readonly int OptimisticEvaluatedCount;
            internal readonly int OptimisticPixelViableCount;
            internal readonly long OptimisticBestBaselinePixels;
            internal readonly long OptimisticBestMergedPixels;
            internal readonly int OptimisticBestGroupCount;
            internal readonly Vector2Int OptimisticBestLayoutSize;
            internal readonly int ProjectionEvaluationCount;
            internal readonly int SafetyEvaluationCount;
            internal readonly long ProjectionEvaluationMilliseconds;
            internal readonly long StateSelectionMilliseconds;
            internal readonly long PlacementAttemptCount;
            internal readonly long UniqueGeneratedStateCount;
            internal readonly long ScoreStateCallCount;
            internal readonly string BestSamplingSummary;
            internal readonly StreamingRegressionExample? StreamingExample;
            internal readonly int StreamingRegressingCandidateCount;
            internal readonly IReadOnlyList<MergeSelection> ProductionCandidates;

            internal ShadowPackingDiagnostics(
                int profileCount,
                int groupsWithMultipleProfiles,
                int hostCount,
                int evaluatedCandidateCount,

                int evaluatedUsingWiderProfileCount,
                int adoptableCandidateCount,

                int adoptableUsingWiderProfileCount,
                int selectedCount,

                int rejectedPixelCount,
                int rejectedMemoryCount,
                int rejectedMaxDimensionCount,
                int rejectedSamplingCount,
                int rejectedStreamingCount,
                int pixelRejectedEqualCount,
                int pixelRejectedGreaterCount,
                int pixelGateOnlyCount,
                int pixelRejectedAlsoMemoryCount,
                int pixelRejectedAlsoMaxDimensionCount,
                int pixelRejectedAlsoSamplingCount,
                int pixelRejectedAlsoStreamingCount,
                long bestBaselinePixels,
                long bestMergedPixels,
                long bestBaselineBytes,
                long bestMergedBytes,
                int bestGroupCount,
                Vector2Int bestLayoutSize,
                string bestBaselineSizeSummary,
                string bestPropertySizeSummary,
                string bestRejectionReason,
                int optimisticSourceGroupCount,
                bool optimisticExhaustive,
                long optimisticLowerBoundMilliseconds,
                int optimisticEvaluatedCount,
                int optimisticPixelViableCount,
                long optimisticBestBaselinePixels,
                long optimisticBestMergedPixels,
                int optimisticBestGroupCount,
                Vector2Int optimisticBestLayoutSize,
                int projectionEvaluationCount,
                int safetyEvaluationCount,
                long projectionEvaluationMilliseconds,
                long stateSelectionMilliseconds,
                long placementAttemptCount,
                long uniqueGeneratedStateCount,
                long scoreStateCallCount,
                string bestSamplingSummary,
                StreamingRegressionExample? streamingExample,
                int streamingRegressingCandidateCount,
                IReadOnlyList<MergeSelection> productionCandidates)
            {
                ProductionCandidates = productionCandidates;
                ProfileCount = profileCount;
                GroupsWithMultipleProfiles = groupsWithMultipleProfiles;
                HostCount = hostCount;
                EvaluatedCandidateCount = evaluatedCandidateCount;

                EvaluatedUsingWiderProfileCount =
                    evaluatedUsingWiderProfileCount;
                AdoptableCandidateCount = adoptableCandidateCount;

                AdoptableUsingWiderProfileCount =
                    adoptableUsingWiderProfileCount;
                SelectedCount = selectedCount;

                RejectedPixelCount = rejectedPixelCount;
                RejectedMemoryCount = rejectedMemoryCount;
                RejectedMaxDimensionCount = rejectedMaxDimensionCount;
                RejectedSamplingCount = rejectedSamplingCount;
                RejectedStreamingCount = rejectedStreamingCount;
                PixelRejectedEqualCount = pixelRejectedEqualCount;
                PixelRejectedGreaterCount = pixelRejectedGreaterCount;
                PixelGateOnlyCount = pixelGateOnlyCount;
                PixelRejectedAlsoMemoryCount =
                    pixelRejectedAlsoMemoryCount;
                PixelRejectedAlsoMaxDimensionCount =
                    pixelRejectedAlsoMaxDimensionCount;
                PixelRejectedAlsoSamplingCount =
                    pixelRejectedAlsoSamplingCount;
                PixelRejectedAlsoStreamingCount =
                    pixelRejectedAlsoStreamingCount;
                BestBaselinePixels = bestBaselinePixels;
                BestMergedPixels = bestMergedPixels;
                BestBaselineBytes = bestBaselineBytes;
                BestMergedBytes = bestMergedBytes;
                BestGroupCount = bestGroupCount;
                BestLayoutSize = bestLayoutSize;
                BestBaselineSizeSummary = bestBaselineSizeSummary;
                BestPropertySizeSummary = bestPropertySizeSummary;
                BestRejectionReason = bestRejectionReason;
                OptimisticSourceGroupCount = optimisticSourceGroupCount;
                OptimisticExhaustive = optimisticExhaustive;
                OptimisticLowerBoundMilliseconds =
                    optimisticLowerBoundMilliseconds;
                OptimisticEvaluatedCount = optimisticEvaluatedCount;
                OptimisticPixelViableCount = optimisticPixelViableCount;
                OptimisticBestBaselinePixels = optimisticBestBaselinePixels;
                OptimisticBestMergedPixels = optimisticBestMergedPixels;
                OptimisticBestGroupCount = optimisticBestGroupCount;
                OptimisticBestLayoutSize = optimisticBestLayoutSize;
                ProjectionEvaluationCount = projectionEvaluationCount;
                SafetyEvaluationCount = safetyEvaluationCount;
                ProjectionEvaluationMilliseconds =
                    projectionEvaluationMilliseconds;
                StateSelectionMilliseconds = stateSelectionMilliseconds;
                PlacementAttemptCount = placementAttemptCount;
                UniqueGeneratedStateCount = uniqueGeneratedStateCount;
                ScoreStateCallCount = scoreStateCallCount;
                BestSamplingSummary = bestSamplingSummary;
                StreamingExample = streamingExample;
                StreamingRegressingCandidateCount =
                    streamingRegressingCandidateCount;
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
            MaxTextureDimension,
            Sampling,
            Streaming,
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
            internal readonly bool StreamingWouldRegress;
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
                            : merged.MaxTextureDimension
                                > baseline.MaxTextureDimension
                                ? CandidateRejectionReason.MaxTextureDimension
                                : HasSamplingRegression(
                                    baseline, merged, groupIds)
                                    ? CandidateRejectionReason.Sampling
                                    : CandidateRejectionReason.None;

                IsAdoptable =
                    RejectionReason == CandidateRejectionReason.None;
                // A potential singleton-view memory increase is a diagnostic,
                // not a condition for rejecting texture merging.
                StreamingWouldRegress = IsAdoptable
                    && HasStreamingRegression(baseline, merged, groupIds);
            }
        }

        // Only constructed for one viable candidate with a potential
        // single-group Streaming memory increase. Not used for selection.
        internal sealed class StreamingRegressionExample
        {
            internal readonly Candidate Candidate;
            internal readonly int ActiveGroupId;
            internal readonly double CameraBias;
            internal readonly long BaselineBytes;
            internal readonly long MergedBytes;
            internal readonly string BaselineEntries;
            internal readonly string MergedEntries;

            internal StreamingRegressionExample(
                Candidate candidate,
                int activeGroupId,
                double cameraBias,
                long baselineBytes,
                long mergedBytes,
                string baselineEntries,
                string mergedEntries)
            {
                Candidate = candidate;
                ActiveGroupId = activeGroupId;
                CameraBias = cameraBias;
                BaselineBytes = baselineBytes;
                MergedBytes = mergedBytes;
                BaselineEntries = baselineEntries;
                MergedEntries = mergedEntries;
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
            internal readonly string DiagnosticName;

            internal TextureCostEntry(
                int width,
                int height,
                TextureFormat format,
                int mipCount,
                bool streaming,
                IEnumerable<int> contributors,
                Dictionary<int, double> demandLog2,
                string diagnosticName = "")
                : this(
                    width,
                    height,
                    GetBlockLayout(format),
                    mipCount,
                    streaming,
                    contributors,
                    demandLog2,
                    diagnosticName)
            {
            }

            internal TextureCostEntry(
                int width,
                int height,
                (uint Width, uint Height, uint Size) block,
                int mipCount,
                bool streaming,
                IEnumerable<int> contributors,
                Dictionary<int, double> demandLog2,
                string diagnosticName = "")
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
                DiagnosticName = diagnosticName;
                FullResidentBytes = CalculateBytes(this, 0);
            }
        }

        internal static PlanResult Plan(
            IDomain domain,
            AutoAtlasTexture configuration,
            AtlasSetting settings,
            IReadOnlyDictionary<Material, IReadOnlyDictionary<string, Texture>> propertyTextures,
            IReadOnlyList<PreparedGroup> groups)
        {
            var planningTimer = System.Diagnostics.Stopwatch.StartNew();
            long standaloneAnalysisTimeMilliseconds = 0;
            long standaloneRelocationTimeMilliseconds = 0;
            var standaloneAnalysisCount = 0;
            long standalonePreserveSetupMilliseconds = 0;
            long standaloneAtlasContextMilliseconds = 0;
            long standaloneIslandNormalizeMilliseconds = 0;
            long standaloneRelocationStageMilliseconds = 0;
            long standaloneAllProfileRelocatorMilliseconds = 0;
            long standaloneCloneAndScaleMilliseconds = 0;
            long standaloneReferenceIslandMilliseconds = 0;
            long standaloneIslandDescriptionMilliseconds = 0;
            long standaloneSizePriorityMilliseconds = 0;
            long standalonePostProcessMilliseconds = 0;
            long standaloneResolutionPlanMilliseconds = 0;

            var sharedRenderers = groups
                .SelectMany(group => group.Renderers)
                .Where(renderer => renderer != null)
                .GroupBy(renderer => renderer)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToHashSet();
            var groupsWithSharedRendererCount = groups.Count(group =>
                group.Renderers.Any(sharedRenderers.Contains));

            var groupPairsSharingRendererCount = 0;
            var groupPairsSharingSourceMeshCount = 0;
            for (var left = 0; left < groups.Count; left += 1)
            {
                var leftRenderers = groups[left].Renderers.ToHashSet();
                var leftMeshes = groups[left].Renderers
                    .Select(domain.GetMesh)
                    .Where(mesh => mesh != null)
                    .ToHashSet();

                for (var right = left + 1; right < groups.Count; right += 1)
                {
                    if (groups[right].Renderers.Any(leftRenderers.Contains))
                        groupPairsSharingRendererCount += 1;

                    if (groups[right].Renderers
                        .Select(domain.GetMesh)
                        .Any(mesh => mesh != null && leftMeshes.Contains(mesh)))
                    {
                        groupPairsSharingSourceMeshCount += 1;
                    }
                }
            }

            if (!configuration.EnableAdditionalAtlasMerging
                || groups.Count(group => !group.ExcludedFromTextureMerging) < 2)
            {
                planningTimer.Stop();
                return new PlanResult(
                    Array.Empty<MergeSelection>(),
                    planningTimer.ElapsedMilliseconds,
                    preparedGroupCount: groups.Count,
                    sharedRendererCount: sharedRenderers.Count,
                    groupsWithSharedRendererCount: groupsWithSharedRendererCount,
                    groupPairsSharingRendererCount:
                        groupPairsSharingRendererCount,
                    groupPairsSharingSourceMeshCount:
                        groupPairsSharingSourceMeshCount);
            }

            var mergeGroups = groups
                .Where(group => !group.ExcludedFromTextureMerging)
                .ToArray();

            var alphaCache = new Dictionary<Texture, bool>();
            var propertyRequirementCache =
                new Dictionary<string, PropertyResolutionRequirements>(
                    StringComparer.Ordinal);
            var propertyRequirementBuildCount = 0;
            long propertyRequirementBuildMilliseconds = 0;

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
                    var requirementTimer =
                        System.Diagnostics.Stopwatch.StartNew();
                    requirements = BuildPredictedPropertyRequirements(
                        materials,
                        propertyTextures);
                    requirementTimer.Stop();
                    propertyRequirementBuildMilliseconds +=
                        requirementTimer.ElapsedMilliseconds;
                    propertyRequirementCache[key] = requirements;
                    propertyRequirementBuildCount += 1;
                }

                return BuildPredictedPropertySizes(
                    requirements,
                    layoutSize);
            }

            foreach (var group in groups)
            {
                var analysisTimer = System.Diagnostics.Stopwatch.StartNew();
                group.StandaloneMetrics = AnalyzeAtlas(
                    domain,
                    settings,
                    group,
                    out var phaseTiming,
                    out var allProfileRelocatorMilliseconds,
                    out var islandProcessingTiming);
                analysisTimer.Stop();

                standaloneAnalysisCount += 1;
                standalonePreserveSetupMilliseconds +=
                    phaseTiming.PreserveSetupMilliseconds;
                standaloneAtlasContextMilliseconds +=
                    phaseTiming.AtlasContextConstructionMilliseconds;
                standaloneIslandNormalizeMilliseconds +=
                    phaseTiming.IslandNormalizeMilliseconds;
                standaloneRelocationStageMilliseconds +=
                    phaseTiming.RelocationStageMilliseconds;
                standaloneAllProfileRelocatorMilliseconds +=
                    allProfileRelocatorMilliseconds;
                standaloneCloneAndScaleMilliseconds +=
                    islandProcessingTiming.CloneAndScaleMilliseconds;
                standaloneReferenceIslandMilliseconds +=
                    islandProcessingTiming.ReferenceIslandMilliseconds;
                standaloneIslandDescriptionMilliseconds +=
                    islandProcessingTiming.IslandDescriptionMilliseconds;
                standaloneSizePriorityMilliseconds +=
                    islandProcessingTiming.SizePriorityMilliseconds;
                standalonePostProcessMilliseconds +=
                    islandProcessingTiming.PostProcessMilliseconds;
                standaloneResolutionPlanMilliseconds +=
                    phaseTiming.ResolutionPlanMilliseconds;
                standaloneAnalysisTimeMilliseconds +=
                    analysisTimer.ElapsedMilliseconds;
                if (group.StandaloneMetrics is { } standaloneMetrics)
                {
                    standaloneRelocationTimeMilliseconds +=
                        standaloneMetrics.RelocationTimeMilliseconds;
                }
            }

            foreach (var group in groups)
            {
                if (group.StandaloneMetrics is not { } metrics)
                    continue;

                group.StandaloneWidthPixels = Math.Max(1, metrics.LayoutSize.x);

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
                        domain, group, propertyTextures, alphaCache);
                    if (originalCost != null)
                    {
                        group.BaselineCost = originalCost;
                        group.BaselineCostKnown = true;
                    }
                }
            }

            // The legacy band search is no longer needed for production.
            // Compatibility diagnostic values are temporary and will be
            // removed together with the obsolete console format.
            var byId = groups.ToDictionary(group => group.Id);

            var shadowPlanningTimer =
                System.Diagnostics.Stopwatch.StartNew();
            var shadow2D = EvaluateShadow2DPacking(
                mergeGroups,
                byId,
                propertyTextures,
                settings,
                alphaCache,
                GetPredictedPropertySizes);
            shadowPlanningTimer.Stop();
            var shadow2DPlanningMilliseconds =
                shadowPlanningTimer.ElapsedMilliseconds;

            // Production uses only two-dimensional packing candidates.
            var selected = SelectTwoDimensionalMerges(
                shadow2D.ProductionCandidates);

            var crossAtomicMaterialGroupCount = 0;
            foreach (var selection in selected)
            {
                var selectedGroups = selection.GroupIds
                    .Select(id => byId[id]).ToArray();
                var atomicGroupByMaterial = selectedGroups
                    .SelectMany(group => group.Materials.Select(
                        material => (material, group.Id)))
                    .ToDictionary(pair => pair.material, pair => pair.Id);
                var grouping = new MaterialGroupingContext(
                    selectedGroups.SelectMany(group => group.Materials).ToHashSet(),
                    settings.AtlasTargetUVChannel,
                    null);
                crossAtomicMaterialGroupCount += grouping.GroupMaterials.Count(
                    materialGroup => materialGroup
                        .Select(material => atomicGroupByMaterial[material])
                        .Distinct().Skip(1).Any());
            }

            planningTimer.Stop();
            return new PlanResult(
                selected,
                planningTimer.ElapsedMilliseconds,
                standaloneAnalysisTimeMilliseconds,
                standaloneRelocationTimeMilliseconds,
                standaloneAnalysisCount,
                standalonePreserveSetupMilliseconds,
                standaloneAtlasContextMilliseconds,
                standaloneIslandNormalizeMilliseconds,
                standaloneRelocationStageMilliseconds,
                standaloneAllProfileRelocatorMilliseconds,
                standaloneCloneAndScaleMilliseconds,
                standaloneReferenceIslandMilliseconds,
                standaloneIslandDescriptionMilliseconds,
                standaloneSizePriorityMilliseconds,
                standalonePostProcessMilliseconds,
                standaloneResolutionPlanMilliseconds,
                shadow2DPlanningMilliseconds,
                propertyRequirementBuildCount,
                propertyRequirementBuildMilliseconds,
                groups.Count,
                sharedRenderers.Count,
                groupsWithSharedRendererCount,
                groupPairsSharingRendererCount,
                groupPairsSharingSourceMeshCount,
                crossAtomicMaterialGroupCount,
                shadow2D.ProfileCount,
                shadow2D.GroupsWithMultipleProfiles,
                shadow2D.HostCount,
                shadow2D.EvaluatedCandidateCount,
                shadow2D.EvaluatedUsingWiderProfileCount,
                shadow2D.AdoptableCandidateCount,
                shadow2D.AdoptableUsingWiderProfileCount,
                shadow2D.SelectedCount,
                shadow2D.RejectedPixelCount,
                shadow2D.RejectedMemoryCount,
                shadow2D.RejectedMaxDimensionCount,
                shadow2D.RejectedSamplingCount,
                shadow2D.RejectedStreamingCount,
                shadow2D.PixelRejectedEqualCount,
                shadow2D.PixelRejectedGreaterCount,
                shadow2D.PixelGateOnlyCount,
                shadow2D.PixelRejectedAlsoMemoryCount,
                shadow2D.PixelRejectedAlsoMaxDimensionCount,
                shadow2D.PixelRejectedAlsoSamplingCount,
                shadow2D.PixelRejectedAlsoStreamingCount,
                shadow2D.BestBaselinePixels,
                shadow2D.BestMergedPixels,
                shadow2D.BestBaselineBytes,
                shadow2D.BestMergedBytes,
                shadow2D.BestGroupCount,
                shadow2D.BestLayoutSize,
                shadow2D.BestBaselineSizeSummary,
                shadow2D.BestPropertySizeSummary,
                shadow2D.BestRejectionReason,
                shadow2D.OptimisticSourceGroupCount,
                shadow2D.OptimisticExhaustive,
                shadow2D.OptimisticLowerBoundMilliseconds,
                shadow2D.OptimisticEvaluatedCount,
                shadow2D.OptimisticPixelViableCount,
                shadow2D.OptimisticBestBaselinePixels,
                shadow2D.OptimisticBestMergedPixels,
                shadow2D.OptimisticBestGroupCount,
                shadow2D.OptimisticBestLayoutSize,
                shadow2D.ProjectionEvaluationCount,
                shadow2D.SafetyEvaluationCount,
                shadow2D.ProjectionEvaluationMilliseconds,
                shadow2D.StateSelectionMilliseconds,
                shadow2D.PlacementAttemptCount,
                shadow2D.UniqueGeneratedStateCount,
                shadow2D.ScoreStateCallCount,
                shadow2D.BestSamplingSummary,
                shadow2D.StreamingExample,
                shadow2D.StreamingRegressingCandidateCount);
        }

        internal static List<MergeSelection> SelectTwoDimensionalMerges(
            IEnumerable<MergeSelection> candidates)
        {
            var selected = new List<MergeSelection>();
            var consumed = new HashSet<int>();
            foreach (var candidate in candidates
                         .Where(candidate => candidate.PackedRegions != null)
                         .OrderByDescending(candidate => candidate.PredictedSavingBytes)
                         .ThenByDescending(candidate => candidate.PredictedSavingRate)
                         .ThenByDescending(candidate => candidate.GroupIds.Length))
            {
                if (candidate.GroupIds.Any(consumed.Contains)) continue;
                selected.Add(candidate);
                consumed.UnionWith(candidate.GroupIds);
            }

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
                propertySizeProvider)
        {
            var profileCount = groups.Sum(
                group => group.TwoDimensionalProfileHeights.Count);
            var groupsWithMultipleProfiles = groups.Count(
                group => group.TwoDimensionalProfileHeights.Count > 1);
            var targetWidthCount = 0;
            var evaluatedCandidateCount = 0;
            var evaluatedUsingWiderProfileCount = 0;
            var rejectedPixelCount = 0;
            var rejectedMemoryCount = 0;
            var rejectedMaxDimensionCount = 0;
            var rejectedSamplingCount = 0;
            var rejectedStreamingCount = 0;
            var pixelRejectedEqualCount = 0;
            var pixelRejectedGreaterCount = 0;
            var pixelGateOnlyCount = 0;
            var pixelRejectedAlsoMemoryCount = 0;
            var pixelRejectedAlsoMaxDimensionCount = 0;
            var pixelRejectedAlsoSamplingCount = 0;
            var pixelRejectedAlsoStreamingCount = 0;
            long bestBaselinePixels = 0;
            long bestMergedPixels = 0;
            long bestBaselineBytes = 0;
            long bestMergedBytes = 0;
            var bestGroupCount = 0;
            var bestLayoutSize = default(Vector2Int);
            var bestBaselineSizeSummary = "";
            var bestPropertySizeSummary = "";
            var bestRejectionReason = "";
            var bestPixelRatio = double.PositiveInfinity;
            var optimisticSourceGroupCount = 0;
            var optimisticExhaustive = false;
            long optimisticLowerBoundMilliseconds = 0;
            var optimisticEvaluatedCount = 0;
            var optimisticPixelViableCount = 0;
            long optimisticBestBaselinePixels = 0;
            long optimisticBestMergedPixels = 0;
            var optimisticBestGroupCount = 0;
            var optimisticBestLayoutSize = default(Vector2Int);
            var optimisticBestPixelRatio = double.PositiveInfinity;
            var projectionEvaluationCount = 0;
            var safetyEvaluationCount = 0;
            long projectionEvaluationMilliseconds = 0;
            long stateSelectionMilliseconds = 0;
            long placementAttemptCount = 0;
            long uniqueGeneratedStateCount = 0;
            long scoreStateCallCount = 0;
            var bestSamplingSummary = "";
            Candidate? bestStreamingCandidate = null;
            var bestStreamingRatio = double.PositiveInfinity;
            var streamingRegressingCandidateCount = 0;

            var shadowAdoptable =
                new Dictionary<string, Candidate>(StringComparer.Ordinal);
            var shadowUsesWiderProfile =
                new Dictionary<string, bool>(StringComparer.Ordinal);
            var shadowPackedRegions =
                new Dictionary<string, IReadOnlyDictionary<int, RectInt>>(
                    StringComparer.Ordinal);
            var evaluatedLayouts =
                new HashSet<string>(StringComparer.Ordinal);
            var projectionCache =
                new Dictionary<GroupLayoutKey, ShadowCostProjection>();
            var candidateCache =
                new Dictionary<GroupLayoutKey, Candidate>();

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

                var projectionTimer =
                    System.Diagnostics.Stopwatch.StartNew();
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
                projectionTimer.Stop();

                projectionEvaluationCount += 1;
                projectionEvaluationMilliseconds +=
                    projectionTimer.ElapsedMilliseconds;
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
                safetyEvaluationCount += 1;
                var candidate = new Candidate(
                    ids,
                    projection.Baseline,
                    projection.Merged,
                    layoutSize,
                    projection.PropertySizes);
                candidateCache[layoutKey] = candidate;
                return candidate;
            }

            // Exact subset enumeration is bounded to the same maximum group count
            // already supported by merge execution. Geometry is deliberately
            // optimistic here: for each group, independently use its smallest
            // available profile area/height and derive only an area lower bound.
            // If even this lower bound cannot reduce pixel count, no real 2D
            // placement of that subset can satisfy the strict pixel gate.
            var lowerBoundGroups = groups
                .Where(group =>
                    group.StandaloneMetrics != null
                    && group.BaselineCostKnown
                    && group.LayoutProfiles.Count != 0)
                .OrderBy(group => group.Id)
                .ToArray();

            optimisticSourceGroupCount = lowerBoundGroups.Length;
            optimisticExhaustive =
                lowerBoundGroups.Length <= MaxMergeGroupCount;
            var optimisticTimer = System.Diagnostics.Stopwatch.StartNew();

            if (optimisticExhaustive)
            {
                var seenLowerBoundLayouts =
                    new HashSet<string>(StringComparer.Ordinal);
                var subsetCount = 1 << lowerBoundGroups.Length;
                for (var mask = 1; mask < subsetCount; mask += 1)
                {
                    var selectedGroups = new List<PreparedGroup>();
                    for (var bit = 0; bit < lowerBoundGroups.Length; bit += 1)
                    {
                        if ((mask & (1 << bit)) != 0)
                            selectedGroups.Add(lowerBoundGroups[bit]);
                    }

                    if (selectedGroups.Count < 2
                        || selectedGroups.Count > MaxMergeGroupCount)
                    {
                        continue;
                    }

                    var ids = selectedGroups
                        .Select(group => group.Id)
                        .OrderBy(id => id)
                        .ToArray();

                    foreach (var targetWidth in
                             AtlasTexture.AutoAtlasTextureSizeCandidates)
                    {
                        long minimumArea = 0;
                        var minimumHeight = 0;
                        var allGroupsFitWidth = true;

                        foreach (var group in selectedGroups)
                        {
                            var profiles = group.TwoDimensionalProfileHeights
                                .Where(profile =>
                                    profile.Key.AtlasWidth == targetWidth
                                    && profile.Key.RegionWidth % PackingGridUnit == 0)
                                .Select(profile => (
                                    Width: profile.Key.RegionWidth,
                                    Height: profile.Value))
                                .Where(profile =>
                                    profile.Height <= targetWidth)
                                .ToArray();

                            if (profiles.Length == 0)
                            {
                                allGroupsFitWidth = false;
                                break;
                            }

                            minimumArea += profiles.Min(profile =>
                                (long)profile.Width * profile.Height);
                            minimumHeight = Math.Max(
                                minimumHeight,
                                profiles.Min(profile => profile.Height));
                        }

                        if (!allGroupsFitWidth)
                            continue;

                        var areaHeight = (int)Math.Min(
                            int.MaxValue,
                            (minimumArea + targetWidth - 1)
                                / targetWidth);
                        var lowerBoundUsedHeight = Math.Max(
                            minimumHeight,
                            areaHeight);
                        if (lowerBoundUsedHeight <= 0
                            || lowerBoundUsedHeight > targetWidth)
                        {
                            continue;
                        }

                        var layoutSize = new Vector2Int(
                            targetWidth,
                            GetCroppedHeight(
                                targetWidth,
                                lowerBoundUsedHeight));
                        var lowerBoundKey =
                            string.Join(",", ids)
                            + "@"
                            + layoutSize.x
                            + "x"
                            + layoutSize.y;
                        if (!seenLowerBoundLayouts.Add(lowerBoundKey))
                            continue;

                        var projection = GetProjection(ids, layoutSize);
                        optimisticEvaluatedCount += 1;
                        if (projection.Merged.PixelCount
                            < projection.Baseline.PixelCount)
                        {
                            optimisticPixelViableCount += 1;
                        }

                        if (projection.Baseline.PixelCount > 0)
                        {
                            var ratio =
                                (double)projection.Merged.PixelCount
                                / projection.Baseline.PixelCount;
                            if (ratio < optimisticBestPixelRatio)
                            {
                                optimisticBestPixelRatio = ratio;
                                optimisticBestBaselinePixels =
                                    projection.Baseline.PixelCount;
                                optimisticBestMergedPixels =
                                    projection.Merged.PixelCount;
                                optimisticBestGroupCount = ids.Length;
                                optimisticBestLayoutSize = layoutSize;
                            }
                        }
                    }
                }
            }

            optimisticTimer.Stop();
            optimisticLowerBoundMilliseconds =
                optimisticTimer.ElapsedMilliseconds;

            void ScoreState(
                ShadowPackingState state,
                int targetWidth)
            {
                scoreStateCallCount += 1;
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

                if (unique.Length <= ShadowPackingStatesPerHost)
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
                                >= ShadowPackingStatesPerHost
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
                    ShadowPackingStatesPerHost / 2);
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
                    ShadowPackingStatesPerHost / 4);
                Retain(
                    unique
                        .OrderBy(state => state.MaxHeight)
                        .ThenBy(state => state.SkylineArea)
                        .ThenByDescending(
                            state => state.GroupIds.Length)
                        .ThenBy(
                            state => state,
                            ShadowPackingStateStableComparer.Instance),
                    ShadowPackingStatesPerHost / 8);
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
                    ShadowPackingStatesPerHost / 8);

                if (selected.Count < ShadowPackingStatesPerHost)
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
                        ShadowPackingStatesPerHost);
                }

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

                targetWidthCount += 1;
                var columnCount = targetWidth / PackingGridUnit;
                var states = new List<ShadowPackingState>
                {
                    new(
                        new int[columnCount],
                        Array.Empty<int>(),
                        0,
                        false),
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
                                placementAttemptCount += 1;
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
                                    state.UsesWiderProfile
                                        || profileWidth
                                            > group.StandaloneWidthPixels,
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
                                if (!generated.TryGetValue(
                                        next.StateKey,
                                        out var prior)
                                    || (next.UsesWiderProfile
                                        && !prior.UsesWiderProfile))
                                {
                                    generated[next.StateKey] = next;
                                }
                            }
                        }
                    }

                    if (generated.Count == 0)
                        continue;

                    uniqueGeneratedStateCount += generated.Count;
                    var selectionTimer =
                        System.Diagnostics.Stopwatch.StartNew();
                    var undominated = RemoveDominatedSkylineStates(
                        generated.Values);
                    selectionTimer.Stop();
                    stateSelectionMilliseconds +=
                        selectionTimer.ElapsedMilliseconds;
                    foreach (var generatedState in undominated)
                        ScoreState(generatedState, targetWidth);

                    states.AddRange(undominated);
                    selectionTimer.Restart();
                    states = SelectPropertyAwareStates(states);
                    selectionTimer.Stop();
                    stateSelectionMilliseconds +=
                        selectionTimer.ElapsedMilliseconds;
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
                    var baseline = candidate.Baseline;
                    var merged = candidate.Merged;
                    var propertySizes = candidate.PropertySizes;

                    if (baseline.PixelCount > 0)
                    {
                        var ratio =
                            (double)merged.PixelCount / baseline.PixelCount;
                        if (ratio < bestPixelRatio)
                        {
                            bestPixelRatio = ratio;
                            bestBaselinePixels = baseline.PixelCount;
                            bestMergedPixels = merged.PixelCount;
                            bestBaselineBytes = baseline.FullResidentBytes;
                            bestMergedBytes = merged.FullResidentBytes;
                            bestGroupCount = ids.Length;
                            bestLayoutSize = layoutSize;
                            bestBaselineSizeSummary = string.Join(
                                ", ",
                                baseline.Entries
                                    .GroupBy(entry =>
                                        (entry.Width, entry.Height))
                                    .OrderByDescending(group =>
                                        (long)group.Key.Width
                                            * group.Key.Height)
                                    .ThenByDescending(group =>
                                        group.Count())
                                    .Select(group =>
                                        $"{group.Key.Width}×{group.Key.Height}×{group.Count()}"));
                            bestPropertySizeSummary = string.Join(
                                ", ",
                                propertySizes
                                    .OrderBy(pair => pair.Key,
                                        StringComparer.Ordinal)
                                    .Select(pair =>
                                        $"{pair.Key}={pair.Value.x}×{pair.Value.y}"));
                            bestSamplingSummary = string.Join(
                                ", ",
                                ids.Select(id =>
                                {
                                    var before = baseline
                                        .SamplingBytesPerTexel
                                        .GetValueOrDefault(id);
                                    var after = merged
                                        .SamplingBytesPerTexel
                                        .GetValueOrDefault(id);
                                    return $"g{id}:{before:0.###}→{after:0.###}";
                                }));
                            bestRejectionReason =
                                candidate.RejectionReason switch
                                {
                                    CandidateRejectionReason.None => "none",
                                    CandidateRejectionReason.PixelCount => "pixels",
                                    CandidateRejectionReason.FullResidentMemory => "memory",
                                    CandidateRejectionReason.MaxTextureDimension => "max dimension",
                                    CandidateRejectionReason.Sampling => "sampling",
                                    CandidateRejectionReason.Streaming => "streaming",
                                    _ => "unknown",
                                };
                        }
                    }

                    if (merged.PixelCount >= baseline.PixelCount)
                    {
                        if (merged.PixelCount == baseline.PixelCount)
                            pixelRejectedEqualCount += 1;
                        else
                            pixelRejectedGreaterCount += 1;

                        var alsoMemory =
                            merged.FullResidentBytes
                                >= baseline.FullResidentBytes;
                        var alsoMaxDimension =
                            merged.MaxTextureDimension
                                > baseline.MaxTextureDimension;
                        var alsoSampling =
                            HasSamplingRegression(
                                baseline, merged, ids);
                        var alsoStreaming =
                            HasStreamingRegression(
                                baseline, merged, ids);

                        if (alsoMemory)
                            pixelRejectedAlsoMemoryCount += 1;
                        if (alsoMaxDimension)
                            pixelRejectedAlsoMaxDimensionCount += 1;
                        if (alsoSampling)
                            pixelRejectedAlsoSamplingCount += 1;
                        if (alsoStreaming)
                            pixelRejectedAlsoStreamingCount += 1;

                        if (!alsoMemory
                            && !alsoMaxDimension
                            && !alsoSampling
                            && !alsoStreaming)
                        {
                            pixelGateOnlyCount += 1;
                        }
                    }

                    evaluatedCandidateCount += 1;
                    if (state.UsesWiderProfile)
                        evaluatedUsingWiderProfileCount += 1;

                    if (!candidate.IsAdoptable)
                    {
                        switch (candidate.RejectionReason)
                        {
                            case CandidateRejectionReason.PixelCount:
                                rejectedPixelCount += 1;
                                break;
                            case CandidateRejectionReason.FullResidentMemory:
                                rejectedMemoryCount += 1;
                                break;
                            case CandidateRejectionReason.MaxTextureDimension:
                                rejectedMaxDimensionCount += 1;
                                break;
                            case CandidateRejectionReason.Sampling:
                                rejectedSamplingCount += 1;
                                break;
                            case CandidateRejectionReason.Streaming:
                                rejectedStreamingCount += 1;
                                break;
                        }

                        continue;
                    }

                    // Keep Streaming implications separate from the adoption
                    // gates. Analyze only viable candidates, retaining the
                    // best pixel-saving example for the temporary console log.
                    if (candidate.StreamingWouldRegress)
                    {
                        streamingRegressingCandidateCount += 1;
                        var ratio = (double)merged.PixelCount
                            / baseline.PixelCount;
                        if (ratio < bestStreamingRatio
                            || (ratio == bestStreamingRatio
                                && (bestStreamingCandidate == null
                                    || candidate.SavingBytes
                                        > bestStreamingCandidate.SavingBytes)))
                        {
                            bestStreamingRatio = ratio;
                            bestStreamingCandidate = candidate;
                        }
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
                        shadowUsesWiderProfile[key] =
                            state.UsesWiderProfile;
                    }
                    else if (candidate.SavingBytes
                                == priorCandidate.SavingBytes
                             && candidate.LayoutSize.y
                                == priorCandidate.LayoutSize.y
                             && state.UsesWiderProfile)
                    {
                        shadowUsesWiderProfile[key] = true;
                    }
                }
            }

            var adoptableUsingWiderProfileCount =
                shadowUsesWiderProfile.Count(pair =>
                    pair.Value && shadowAdoptable.ContainsKey(pair.Key));

            var selectedCount = 0;
            var consumed = new HashSet<int>();
            foreach (var pair in shadowAdoptable
                         .OrderByDescending(
                             pair => pair.Value.SavingBytes)
                         .ThenByDescending(
                             pair => pair.Value.SavingRate)
                         .ThenByDescending(
                             pair => pair.Value.GroupIds.Length))
            {
                var candidate = pair.Value;
                if (candidate.GroupIds.Any(consumed.Contains))
                    continue;

                selectedCount += 1;
                consumed.UnionWith(candidate.GroupIds);
            }

            // Diagnostics are calculated once, after the bounded search.
            var streamingExample = bestStreamingCandidate == null
                ? null
                : DiagnoseStreamingRegression(bestStreamingCandidate);

            return new ShadowPackingDiagnostics(
                profileCount,
                groupsWithMultipleProfiles,
                targetWidthCount,
                evaluatedCandidateCount,
                evaluatedUsingWiderProfileCount,
                shadowAdoptable.Count,
                adoptableUsingWiderProfileCount,
                selectedCount,
                rejectedPixelCount,
                rejectedMemoryCount,
                rejectedMaxDimensionCount,
                rejectedSamplingCount,
                rejectedStreamingCount,
                pixelRejectedEqualCount,
                pixelRejectedGreaterCount,
                pixelGateOnlyCount,
                pixelRejectedAlsoMemoryCount,
                pixelRejectedAlsoMaxDimensionCount,
                pixelRejectedAlsoSamplingCount,
                pixelRejectedAlsoStreamingCount,
                bestBaselinePixels,
                bestMergedPixels,
                bestBaselineBytes,
                bestMergedBytes,
                bestGroupCount,
                bestLayoutSize,
                bestBaselineSizeSummary,
                bestPropertySizeSummary,
                bestRejectionReason,
                optimisticSourceGroupCount,
                optimisticExhaustive,
                optimisticLowerBoundMilliseconds,
                optimisticEvaluatedCount,
                optimisticPixelViableCount,
                optimisticBestBaselinePixels,
                optimisticBestMergedPixels,
                optimisticBestGroupCount,
                optimisticBestLayoutSize,
                projectionEvaluationCount,
                safetyEvaluationCount,
                projectionEvaluationMilliseconds,
                stateSelectionMilliseconds,
                placementAttemptCount,
                uniqueGeneratedStateCount,
                scoreStateCallCount,
                bestSamplingSummary,
                streamingExample,
                streamingRegressingCandidateCount,
                shadowAdoptable.Select(pair => new MergeSelection(
                    pair.Value, shadowPackedRegions[pair.Key])).ToArray());
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
            PreparedGroup group,
            out AtlasTexture.AtlasExecutionTimingDiagnostic timing,
            out long allProfileRelocatorMilliseconds,
            out AtlasTexture.AtlasIslandProcessingTimingDiagnostic
                islandProcessingTiming)
        {
            timing = default;
            allProfileRelocatorMilliseconds = 0;
            islandProcessingTiming = default;
            group.LayoutProfiles.Clear();
            group.TwoDimensionalProfileHeights.Clear();

            var prepared = AtlasTexture.PrepareAtlasContext(
                domain,
                group.Materials,
                group.Renderers,
                settings,
                out var preserveSetupMilliseconds,
                out var atlasContextConstructionMilliseconds,
                out var islandNormalizeMilliseconds);
            if (prepared is null)
                return null;

            using (prepared)
            {
                long relocationStageMilliseconds = 0;
                long resolutionPlanMilliseconds = 0;
                long cloneAndScaleMilliseconds = 0;
                long referenceIslandMilliseconds = 0;
                long islandDescriptionMilliseconds = 0;
                long sizePriorityMilliseconds = 0;
                long postProcessMilliseconds = 0;
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
                                out var metrics,
                                out var relocationStage,
                                out var relocatorMilliseconds,
                                out var profileTiming,
                                out var resolutionPlan))
                        {
                            relocationStageMilliseconds += relocationStage;
                            allProfileRelocatorMilliseconds +=
                                relocatorMilliseconds;
                            cloneAndScaleMilliseconds +=
                                profileTiming.CloneAndScaleMilliseconds;
                            referenceIslandMilliseconds +=
                                profileTiming.ReferenceIslandMilliseconds;
                            islandDescriptionMilliseconds +=
                                profileTiming.IslandDescriptionMilliseconds;
                            sizePriorityMilliseconds +=
                                profileTiming.SizePriorityMilliseconds;
                            postProcessMilliseconds +=
                                profileTiming.PostProcessMilliseconds;
                            continue;
                        }

                        relocationStageMilliseconds += relocationStage;
                        allProfileRelocatorMilliseconds +=
                            relocatorMilliseconds;
                        cloneAndScaleMilliseconds +=
                            profileTiming.CloneAndScaleMilliseconds;
                        referenceIslandMilliseconds +=
                            profileTiming.ReferenceIslandMilliseconds;
                        islandDescriptionMilliseconds +=
                            profileTiming.IslandDescriptionMilliseconds;
                        sizePriorityMilliseconds +=
                            profileTiming.SizePriorityMilliseconds;
                        postProcessMilliseconds +=
                            profileTiming.PostProcessMilliseconds;
                        resolutionPlanMilliseconds += resolutionPlan;
                        group.LayoutProfiles[size] = metrics;
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
                                        out var paddedMetrics,
                                        out _,
                                        out _,
                                        out _,
                                        out _))
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

                timing = new AtlasTexture.AtlasExecutionTimingDiagnostic(
                    preserveSetupMilliseconds,
                    atlasContextConstructionMilliseconds,
                    islandNormalizeMilliseconds,
                    relocationStageMilliseconds,
                    resolutionPlanMilliseconds,
                    0,
                    0);
                islandProcessingTiming =
                    new AtlasTexture.AtlasIslandProcessingTimingDiagnostic(
                        cloneAndScaleMilliseconds,
                        referenceIslandMilliseconds,
                        islandDescriptionMilliseconds,
                        sizePriorityMilliseconds,
                        allProfileRelocatorMilliseconds,
                        postProcessMilliseconds);
                return standalone;
            }
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
                if (!AtlasTexture.TryGetOriginalTextureCostInfo(
                        domain,
                        texture,
                        alphaCache,
                        out var block,
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
                    block,
                    mipCount,
                    streaming,
                    new[] { group.Id },
                    demands,
                    diagnosticName: texture.name));
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
                    demand,
                    diagnosticName: property.Key));
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

        private static (uint Width, uint Height, uint Size) GetBlockLayout(
            GraphicsFormat format)
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

        internal static StreamingRegressionExample? DiagnoseStreamingRegression(
            Candidate candidate)
        {
            long worstExcess = 0;
            var worstGroup = -1;
            var worstBiasStep = 0;
            long worstBaselineBytes = 0;
            long worstMergedBytes = 0;

            foreach (var groupId in candidate.GroupIds)
            {
                var active = new HashSet<int> { groupId };
                for (var biasStep = -32; biasStep <= 40; biasStep += 1)
                {
                    var bias = biasStep * 0.5d;
                    var baselineBytes = CalculateScenarioBytes(
                        candidate.Baseline, active, bias);
                    var mergedBytes = CalculateScenarioBytes(
                        candidate.Merged, active, bias);
                    var excess = mergedBytes - baselineBytes;
                    if (excess <= worstExcess) continue;

                    worstExcess = excess;
                    worstGroup = groupId;
                    worstBiasStep = biasStep;
                    worstBaselineBytes = baselineBytes;
                    worstMergedBytes = mergedBytes;
                }
            }

            if (worstGroup < 0) return null;

            var worstBias = worstBiasStep * 0.5d;
            return new StreamingRegressionExample(
                candidate,
                worstGroup,
                worstBias,
                worstBaselineBytes,
                worstMergedBytes,
                DescribeScenarioEntries(candidate.Baseline, worstGroup, worstBias),
                DescribeScenarioEntries(candidate.Merged, worstGroup, worstBias));
        }

        private static string DescribeScenarioEntries(
            TextureCostPlan plan, int groupId, double cameraBias)
        {
            var lines = new List<string>();
            for (var index = 0; index < plan.Entries.Length; index += 1)
            {
                var entry = plan.Entries[index];
                if (!entry.Contributors.Contains(groupId)) continue;

                var demand = entry.DemandLog2.TryGetValue(
                    groupId, out var logC) ? logC : UnknownDemandLog2;
                var mip = 0;
                if (entry.Streaming && entry.MipCount > 1)
                {
                    var desiredMip = (int)Math.Floor(demand + cameraBias);
                    mip = Math.Max(0,
                        Math.Min(entry.MipCount - 1, desiredMip));
                }

                var name = string.IsNullOrEmpty(entry.DiagnosticName)
                    ? "(unidentified)"
                    : entry.DiagnosticName.Replace("\r", " ").Replace("\n", " ");
                var detail = demand == UnknownDemandLog2
                    ? "unknown"
                    : demand.ToString("0.###",
                        System.Globalization.CultureInfo.InvariantCulture);
                lines.Add(
                    $"  [{index}] {name}: {entry.Width}×{entry.Height}, "
                    + $"mip={mip}/{entry.MipCount - 1}, "
                    + $"bytes={CalculateBytes(entry, mip)}, "
                    + $"streaming(model)={entry.Streaming}, "
                    + $"block={entry.BlockWidth}×{entry.BlockHeight}/{entry.BlockSize}B, "
                    + $"demandLog2={detail}");
            }

            return lines.Count == 0 ? "  (none)" : string.Join("\n", lines);
        }

        private static bool HasStreamingRegression(
            TextureCostPlan baseline,
            TextureCostPlan merged,
            int[] groupIds)
        {
            // Cross-group atlasing can make a texture shared by groups that are
            // not visible at the same time. Checking only the all-active case can
            // hide that regression because the standalone baseline then includes
            // every group's independent texture cost.
            //
            // Check every atomic group in isolation instead. For a shared atlas
            // entry, any multi-group scenario needs the most detailed mip required
            // by one of its active contributors, while the standalone baseline is
            // the sum of those groups' independent costs. Therefore, if the merge
            // does not regress any singleton scenario, adding more active groups
            // cannot create a worse aggregate mip-memory case.
            foreach (var groupId in groupIds)
            {
                var activeGroups = new HashSet<int> { groupId };

                // Camera distance/FOV are represented as a normalized logarithmic
                // bias. Sweep the range instead of assuming one viewing distance.
                for (var biasStep = -32; biasStep <= 40; biasStep += 1)
                {
                    var bias = biasStep * 0.5d;
                    var baselineBytes = CalculateScenarioBytes(
                        baseline, activeGroups, bias);
                    var mergedBytes = CalculateScenarioBytes(
                        merged, activeGroups, bias);
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


    }
}
