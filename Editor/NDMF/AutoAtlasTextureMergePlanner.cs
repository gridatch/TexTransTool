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
        private const int BandStatesPerHeight = 8;
        private const int BandCandidatesPerHost = 512;
        private const int ShadowPackingStatesPerHost = 512;
        private const int PackingGridUnit = 256;
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
            internal readonly Dictionary<int, AtlasTexture.AtlasExecutionMetrics>
                LayoutProfiles = new();
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
            internal readonly IReadOnlyDictionary<string, Vector2Int>
                PlannedPropertySizes;

            internal MergeSelection(
                IEnumerable<int> groupIds,
                TextureCostPlan baseline,
                Vector2Int plannedLayoutSize,
                IReadOnlyDictionary<string, Vector2Int> plannedPropertySizes)
            {
                GroupIds = groupIds.OrderBy(id => id).ToArray();
                BaselineBytes = baseline.FullResidentBytes;
                PlannedLayoutSize = plannedLayoutSize;
                PlannedPropertySizes = plannedPropertySizes.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value,
                    StringComparer.Ordinal);
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
            internal readonly int MergeHostCount;
            internal readonly int EvaluatedMergeCandidateCount;
            internal readonly int CandidateWithinHostCurrentHeightCount;
            internal readonly int AdoptableMergeCandidateCount;
            internal readonly int RejectedPixelCount;
            internal readonly int RejectedMemoryCount;
            internal readonly int RejectedMaxDimensionCount;
            internal readonly int RejectedSamplingCount;
            internal readonly int RejectedStreamingCount;

            // Temporary shadow 2D-packing diagnostics. These do not affect the
            // currently selected band-based merge plan.
            internal readonly int ShadowProfileCount;
            internal readonly int ShadowGroupsWithMultipleProfiles;
            internal readonly int ShadowHostCount;
            internal readonly int ShadowEvaluatedCandidateCount;
            internal readonly int ShadowEvaluatedBeyondBandLayoutCount;
            internal readonly int ShadowEvaluatedUsingWiderProfileCount;
            internal readonly int ShadowAdoptableCandidateCount;
            internal readonly int ShadowAdoptableBeyondBandCount;
            internal readonly int ShadowAdoptableUsingWiderProfileCount;
            internal readonly int ShadowSelectedCount;
            internal readonly int ShadowSelectedBeyondBandCount;
            internal readonly int ShadowRejectedPixelCount;
            internal readonly int ShadowRejectedMemoryCount;
            internal readonly int ShadowRejectedMaxDimensionCount;
            internal readonly int ShadowRejectedSamplingCount;
            internal readonly int ShadowRejectedStreamingCount;
            internal readonly int ShadowPixelRejectedEqualCount;
            internal readonly int ShadowPixelRejectedGreaterCount;
            internal readonly int ShadowPixelGateOnlyCount;
            internal readonly long ShadowBestBaselinePixels;
            internal readonly long ShadowBestMergedPixels;
            internal readonly int ShadowBestGroupCount;
            internal readonly Vector2Int ShadowBestLayoutSize;

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
                int preparedGroupCount = 0,
                int sharedRendererCount = 0,
                int groupsWithSharedRendererCount = 0,
                int groupPairsSharingRendererCount = 0,
                int groupPairsSharingSourceMeshCount = 0,
                int crossAtomicMaterialGroupCount = 0,
                int mergeHostCount = 0,
                int evaluatedMergeCandidateCount = 0,
                int candidateWithinHostCurrentHeightCount = 0,
                int adoptableMergeCandidateCount = 0,
                int rejectedPixelCount = 0,
                int rejectedMemoryCount = 0,
                int rejectedMaxDimensionCount = 0,
                int rejectedSamplingCount = 0,
                int rejectedStreamingCount = 0,
                int shadowProfileCount = 0,
                int shadowGroupsWithMultipleProfiles = 0,
                int shadowHostCount = 0,
                int shadowEvaluatedCandidateCount = 0,
                int shadowEvaluatedBeyondBandLayoutCount = 0,
                int shadowEvaluatedUsingWiderProfileCount = 0,
                int shadowAdoptableCandidateCount = 0,
                int shadowAdoptableBeyondBandCount = 0,
                int shadowAdoptableUsingWiderProfileCount = 0,
                int shadowSelectedCount = 0,
                int shadowSelectedBeyondBandCount = 0,
                int shadowRejectedPixelCount = 0,
                int shadowRejectedMemoryCount = 0,
                int shadowRejectedMaxDimensionCount = 0,
                int shadowRejectedSamplingCount = 0,
                int shadowRejectedStreamingCount = 0,
                int shadowPixelRejectedEqualCount = 0,
                int shadowPixelRejectedGreaterCount = 0,
                int shadowPixelGateOnlyCount = 0,
                long shadowBestBaselinePixels = 0,
                long shadowBestMergedPixels = 0,
                int shadowBestGroupCount = 0,
                Vector2Int shadowBestLayoutSize = default)
            {
                Selections = selections;
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
                PreparedGroupCount = preparedGroupCount;
                SharedRendererCount = sharedRendererCount;
                GroupsWithSharedRendererCount = groupsWithSharedRendererCount;
                GroupPairsSharingRendererCount = groupPairsSharingRendererCount;
                GroupPairsSharingSourceMeshCount =
                    groupPairsSharingSourceMeshCount;
                CrossAtomicMaterialGroupCount = crossAtomicMaterialGroupCount;
                MergeHostCount = mergeHostCount;
                EvaluatedMergeCandidateCount = evaluatedMergeCandidateCount;
                CandidateWithinHostCurrentHeightCount =
                    candidateWithinHostCurrentHeightCount;
                AdoptableMergeCandidateCount = adoptableMergeCandidateCount;
                RejectedPixelCount = rejectedPixelCount;
                RejectedMemoryCount = rejectedMemoryCount;
                RejectedMaxDimensionCount = rejectedMaxDimensionCount;
                RejectedSamplingCount = rejectedSamplingCount;
                RejectedStreamingCount = rejectedStreamingCount;
                ShadowProfileCount = shadowProfileCount;
                ShadowGroupsWithMultipleProfiles =
                    shadowGroupsWithMultipleProfiles;
                ShadowHostCount = shadowHostCount;
                ShadowEvaluatedCandidateCount =
                    shadowEvaluatedCandidateCount;
                ShadowEvaluatedBeyondBandLayoutCount =
                    shadowEvaluatedBeyondBandLayoutCount;
                ShadowEvaluatedUsingWiderProfileCount =
                    shadowEvaluatedUsingWiderProfileCount;
                ShadowAdoptableCandidateCount =
                    shadowAdoptableCandidateCount;
                ShadowAdoptableBeyondBandCount =
                    shadowAdoptableBeyondBandCount;
                ShadowAdoptableUsingWiderProfileCount =
                    shadowAdoptableUsingWiderProfileCount;
                ShadowSelectedCount = shadowSelectedCount;
                ShadowSelectedBeyondBandCount =
                    shadowSelectedBeyondBandCount;
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
                ShadowBestBaselinePixels = shadowBestBaselinePixels;
                ShadowBestMergedPixels = shadowBestMergedPixels;
                ShadowBestGroupCount = shadowBestGroupCount;
                ShadowBestLayoutSize = shadowBestLayoutSize;
            }
        }

        private sealed class ShadowPackingState
        {
            internal readonly int[] Skyline;
            internal readonly int[] GuestIds;
            internal readonly long GuestBaselineBytes;
            internal readonly bool UsesWiderProfile;

            internal ShadowPackingState(
                int[] skyline,
                IEnumerable<int> guestIds,
                long guestBaselineBytes,
                bool usesWiderProfile)
            {
                Skyline = skyline;
                GuestIds = guestIds.OrderBy(id => id).ToArray();
                GuestBaselineBytes = guestBaselineBytes;
                UsesWiderProfile = usesWiderProfile;
            }

            internal string Key =>
                string.Join(",", GuestIds)
                + "|"
                + string.Join(",", Skyline);
        }

        private readonly struct ShadowPackingDiagnostics
        {
            internal readonly int ProfileCount;
            internal readonly int GroupsWithMultipleProfiles;
            internal readonly int HostCount;
            internal readonly int EvaluatedCandidateCount;
            internal readonly int EvaluatedBeyondBandLayoutCount;
            internal readonly int EvaluatedUsingWiderProfileCount;
            internal readonly int AdoptableCandidateCount;
            internal readonly int AdoptableBeyondBandCount;
            internal readonly int AdoptableUsingWiderProfileCount;
            internal readonly int SelectedCount;
            internal readonly int SelectedBeyondBandCount;
            internal readonly int RejectedPixelCount;
            internal readonly int RejectedMemoryCount;
            internal readonly int RejectedMaxDimensionCount;
            internal readonly int RejectedSamplingCount;
            internal readonly int RejectedStreamingCount;
            internal readonly int PixelRejectedEqualCount;
            internal readonly int PixelRejectedGreaterCount;
            internal readonly int PixelGateOnlyCount;
            internal readonly long BestBaselinePixels;
            internal readonly long BestMergedPixels;
            internal readonly int BestGroupCount;
            internal readonly Vector2Int BestLayoutSize;

            internal ShadowPackingDiagnostics(
                int profileCount,
                int groupsWithMultipleProfiles,
                int hostCount,
                int evaluatedCandidateCount,
                int evaluatedBeyondBandLayoutCount,
                int evaluatedUsingWiderProfileCount,
                int adoptableCandidateCount,
                int adoptableBeyondBandCount,
                int adoptableUsingWiderProfileCount,
                int selectedCount,
                int selectedBeyondBandCount,
                int rejectedPixelCount,
                int rejectedMemoryCount,
                int rejectedMaxDimensionCount,
                int rejectedSamplingCount,
                int rejectedStreamingCount,
                int pixelRejectedEqualCount,
                int pixelRejectedGreaterCount,
                int pixelGateOnlyCount,
                long bestBaselinePixels,
                long bestMergedPixels,
                int bestGroupCount,
                Vector2Int bestLayoutSize)
            {
                ProfileCount = profileCount;
                GroupsWithMultipleProfiles = groupsWithMultipleProfiles;
                HostCount = hostCount;
                EvaluatedCandidateCount = evaluatedCandidateCount;
                EvaluatedBeyondBandLayoutCount =
                    evaluatedBeyondBandLayoutCount;
                EvaluatedUsingWiderProfileCount =
                    evaluatedUsingWiderProfileCount;
                AdoptableCandidateCount = adoptableCandidateCount;
                AdoptableBeyondBandCount = adoptableBeyondBandCount;
                AdoptableUsingWiderProfileCount =
                    adoptableUsingWiderProfileCount;
                SelectedCount = selectedCount;
                SelectedBeyondBandCount = selectedBeyondBandCount;
                RejectedPixelCount = rejectedPixelCount;
                RejectedMemoryCount = rejectedMemoryCount;
                RejectedMaxDimensionCount = rejectedMaxDimensionCount;
                RejectedSamplingCount = rejectedSamplingCount;
                RejectedStreamingCount = rejectedStreamingCount;
                PixelRejectedEqualCount = pixelRejectedEqualCount;
                PixelRejectedGreaterCount = pixelRejectedGreaterCount;
                PixelGateOnlyCount = pixelGateOnlyCount;
                BestBaselinePixels = bestBaselinePixels;
                BestMergedPixels = bestMergedPixels;
                BestGroupCount = bestGroupCount;
                BestLayoutSize = bestLayoutSize;
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
                                    : HasStreamingRegression(
                                        baseline, merged, groupIds)
                                        ? CandidateRejectionReason.Streaming
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

            internal TextureCostEntry(
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
                || groups.Count < 2)
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

            var alphaCache = new Dictionary<Texture, bool>();

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

            var byId = groups.ToDictionary(group => group.Id);
            var adoptable = new Dictionary<string, Candidate>();
            var evaluatedLayouts = new HashSet<string>(StringComparer.Ordinal);
            var mergeHostCount = 0;
            var evaluatedMergeCandidateCount = 0;
            var candidateWithinHostCurrentHeightCount = 0;
            var adoptableMergeCandidateCount = 0;
            var rejectedPixelCount = 0;
            var rejectedMemoryCount = 0;
            var rejectedMaxDimensionCount = 0;
            var rejectedSamplingCount = 0;
            var rejectedStreamingCount = 0;

            foreach (var host in groups.Where(CanHostAnyGuest))
            {
                mergeHostCount += 1;
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
                        propertySizes);

                    evaluatedMergeCandidateCount += 1;
                    if (host.StandaloneMetrics is { } hostMetrics
                        && layoutSize.y <= hostMetrics.LayoutSize.y)
                    {
                        candidateWithinHostCurrentHeightCount += 1;
                    }

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

                    adoptableMergeCandidateCount += 1;

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
                    candidate.LayoutSize,
                    candidate.PropertySizes));
                consumed.UnionWith(candidate.GroupIds);
            }

            var crossAtomicMaterialGroupCount = 0;
            foreach (var selection in selected)
            {
                var selectedGroups = selection.GroupIds
                    .Select(id => groups[id])
                    .ToArray();
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
                        .Distinct()
                        .Skip(1)
                        .Any());
            }

            var shadow2D = EvaluateShadow2DPacking(
                groups,
                byId,
                propertyTextures,
                settings,
                alphaCache,
                adoptable,
                evaluatedLayouts);

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
                groups.Count,
                sharedRenderers.Count,
                groupsWithSharedRendererCount,
                groupPairsSharingRendererCount,
                groupPairsSharingSourceMeshCount,
                crossAtomicMaterialGroupCount,
                mergeHostCount,
                evaluatedMergeCandidateCount,
                candidateWithinHostCurrentHeightCount,
                adoptableMergeCandidateCount,
                rejectedPixelCount,
                rejectedMemoryCount,
                rejectedMaxDimensionCount,
                rejectedSamplingCount,
                rejectedStreamingCount,
                shadow2D.ProfileCount,
                shadow2D.GroupsWithMultipleProfiles,
                shadow2D.HostCount,
                shadow2D.EvaluatedCandidateCount,
                shadow2D.EvaluatedBeyondBandLayoutCount,
                shadow2D.EvaluatedUsingWiderProfileCount,
                shadow2D.AdoptableCandidateCount,
                shadow2D.AdoptableBeyondBandCount,
                shadow2D.AdoptableUsingWiderProfileCount,
                shadow2D.SelectedCount,
                shadow2D.SelectedBeyondBandCount,
                shadow2D.RejectedPixelCount,
                shadow2D.RejectedMemoryCount,
                shadow2D.RejectedMaxDimensionCount,
                shadow2D.RejectedSamplingCount,
                shadow2D.RejectedStreamingCount,
                shadow2D.PixelRejectedEqualCount,
                shadow2D.PixelRejectedGreaterCount,
                shadow2D.PixelGateOnlyCount,
                shadow2D.BestBaselinePixels,
                shadow2D.BestMergedPixels,
                shadow2D.BestGroupCount,
                shadow2D.BestLayoutSize);
        }

        private static ShadowPackingDiagnostics EvaluateShadow2DPacking(
            IReadOnlyList<PreparedGroup> groups,
            IReadOnlyDictionary<int, PreparedGroup> byId,
            IReadOnlyDictionary<Material, IReadOnlyDictionary<string, Texture>>
                propertyTextures,
            AtlasSetting settings,
            Dictionary<Texture, bool> alphaCache,
            IReadOnlyDictionary<string, Candidate> bandAdoptable,
            HashSet<string> bandEvaluatedLayouts)
        {
            var profileCount = groups.Sum(group => group.LayoutProfiles.Count);
            var groupsWithMultipleProfiles = groups.Count(
                group => group.LayoutProfiles.Count > 1);
            var hostCount = 0;
            var evaluatedCandidateCount = 0;
            var evaluatedBeyondBandLayoutCount = 0;
            var evaluatedUsingWiderProfileCount = 0;
            var rejectedPixelCount = 0;
            var rejectedMemoryCount = 0;
            var rejectedMaxDimensionCount = 0;
            var rejectedSamplingCount = 0;
            var rejectedStreamingCount = 0;
            var pixelRejectedEqualCount = 0;
            var pixelRejectedGreaterCount = 0;
            var pixelGateOnlyCount = 0;
            long bestBaselinePixels = 0;
            long bestMergedPixels = 0;
            var bestGroupCount = 0;
            var bestLayoutSize = default(Vector2Int);
            var bestPixelRatio = double.PositiveInfinity;

            var shadowAdoptable =
                new Dictionary<string, Candidate>(StringComparer.Ordinal);
            var shadowUsesWiderProfile =
                new Dictionary<string, bool>(StringComparer.Ordinal);
            var evaluatedLayouts =
                new HashSet<string>(StringComparer.Ordinal);

            foreach (var host in groups.OrderBy(group => group.Id))
            {
                if (host.StandaloneMetrics is null
                    || !host.BaselineCostKnown
                    || host.BandWidthPixels < PackingGridUnit
                    || host.BandUsedHeightPixels <= 0
                    || host.BandWidthPixels % PackingGridUnit != 0)
                {
                    continue;
                }

                var hostWidth = host.BandWidthPixels;
                var capacity = hostWidth - host.BandUsedHeightPixels;
                if (capacity <= 0)
                    continue;

                var columnCount = hostWidth / PackingGridUnit;
                var guests = groups
                    .Where(group =>
                        group.Id != host.Id
                        && group.StandaloneMetrics != null
                        && group.BaselineCostKnown
                        && group.BandWidthPixels > 0
                        && group.BandWidthPixels <= hostWidth
                        && group.LayoutProfiles.Any(profile =>
                            profile.Key <= hostWidth
                            && profile.Key % PackingGridUnit == 0
                            && GetBandUsedHeightPixels(profile.Value)
                                <= capacity))
                    .OrderBy(group => group.Id)
                    .ToArray();

                if (guests.Length == 0)
                    continue;

                hostCount += 1;
                var states = new List<ShadowPackingState>
                {
                    new(
                        new int[columnCount],
                        Array.Empty<int>(),
                        0,
                        false),
                };

                foreach (var guest in guests)
                {
                    var generated =
                        new Dictionary<string, ShadowPackingState>(
                            StringComparer.Ordinal);
                    var snapshot = states
                        .Where(state =>
                            state.GuestIds.Length + 1
                                < MaxMergeGroupCount)
                        .ToArray();

                    foreach (var state in snapshot)
                    {
                        foreach (var profile in guest.LayoutProfiles
                                     .Where(profile =>
                                         profile.Key <= hostWidth
                                         && profile.Key
                                             % PackingGridUnit == 0)
                                     .OrderBy(profile => profile.Key))
                        {
                            var profileWidth = profile.Key;
                            var profileHeight =
                                GetBandUsedHeightPixels(profile.Value);
                            var widthColumns =
                                profileWidth / PackingGridUnit;
                            if (widthColumns <= 0
                                || widthColumns > columnCount
                                || profileHeight > capacity)
                            {
                                continue;
                            }

                            for (var x = 0;
                                 x + widthColumns <= columnCount;
                                 x += 1)
                            {
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
                                if (top > capacity)
                                    continue;

                                var skyline =
                                    (int[])state.Skyline.Clone();
                                for (var column = x;
                                     column < x + widthColumns;
                                     column += 1)
                                {
                                    skyline[column] = top;
                                }

                                var next = new ShadowPackingState(
                                    skyline,
                                    state.GuestIds.Append(guest.Id),
                                    state.GuestBaselineBytes
                                        + guest.BaselineCost
                                            .FullResidentBytes,
                                    state.UsesWiderProfile
                                        || profileWidth
                                            > guest.BandWidthPixels);

                                if (!generated.TryGetValue(
                                        next.Key,
                                        out var prior)
                                    || (next.UsesWiderProfile
                                        && !prior.UsesWiderProfile))
                                {
                                    generated[next.Key] = next;
                                }
                            }
                        }
                    }

                    if (generated.Count == 0)
                        continue;

                    states.AddRange(generated.Values);
                    states = states
                        .GroupBy(state => state.Key, StringComparer.Ordinal)
                        .Select(group => group.First())
                        .OrderByDescending(
                            state => state.GuestBaselineBytes)
                        .ThenByDescending(
                            state => state.GuestIds.Length)
                        .ThenBy(state =>
                            state.Skyline.Length == 0
                                ? 0
                                : state.Skyline.Max())
                        .ThenBy(state => state.Skyline.Sum())
                        .ThenBy(state => state.Key, StringComparer.Ordinal)
                        .Take(ShadowPackingStatesPerHost)
                        .ToList();

                    if (!states.Any(state => state.GuestIds.Length == 0))
                    {
                        states.Add(
                            new ShadowPackingState(
                                new int[columnCount],
                                Array.Empty<int>(),
                                0,
                                false));
                    }
                }

                foreach (var state in states.Where(
                             state => state.GuestIds.Length != 0))
                {
                    var ids = new[] { host.Id }
                        .Concat(state.GuestIds)
                        .OrderBy(id => id)
                        .ToArray();
                    var key = string.Join(",", ids);
                    var guestHeight =
                        state.Skyline.Length == 0
                            ? 0
                            : state.Skyline.Max();
                    var usedHeight =
                        host.BandUsedHeightPixels + guestHeight;
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
                        materials,
                        layoutSize,
                        settings);
                    var baseline = TextureCostPlan.Combine(
                        selectedGroups.Select(
                            group => group.BaselineCost));
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
                        propertySizes);

                    if (baseline.PixelCount > 0)
                    {
                        var ratio =
                            (double)merged.PixelCount / baseline.PixelCount;
                        if (ratio < bestPixelRatio)
                        {
                            bestPixelRatio = ratio;
                            bestBaselinePixels = baseline.PixelCount;
                            bestMergedPixels = merged.PixelCount;
                            bestGroupCount = ids.Length;
                            bestLayoutSize = layoutSize;
                        }
                    }

                    if (merged.PixelCount >= baseline.PixelCount)
                    {
                        if (merged.PixelCount == baseline.PixelCount)
                            pixelRejectedEqualCount += 1;
                        else
                            pixelRejectedGreaterCount += 1;

                        if (merged.FullResidentBytes
                                < baseline.FullResidentBytes
                            && merged.MaxTextureDimension
                                <= baseline.MaxTextureDimension
                            && !HasSamplingRegression(
                                baseline, merged, ids)
                            && !HasStreamingRegression(
                                baseline, merged, ids))
                        {
                            pixelGateOnlyCount += 1;
                        }
                    }

                    evaluatedCandidateCount += 1;
                    if (!bandEvaluatedLayouts.Contains(layoutKey))
                        evaluatedBeyondBandLayoutCount += 1;
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

            bool ImprovesOnBand(string key, Candidate shadow)
            {
                if (!bandAdoptable.TryGetValue(key, out var band))
                    return true;
                return shadow.SavingBytes > band.SavingBytes
                    || (shadow.SavingBytes == band.SavingBytes
                        && shadow.LayoutSize.y < band.LayoutSize.y);
            }

            var adoptableBeyondBandCount = shadowAdoptable.Count(pair =>
                ImprovesOnBand(pair.Key, pair.Value));
            var adoptableUsingWiderProfileCount =
                shadowUsesWiderProfile.Count(pair =>
                    pair.Value && shadowAdoptable.ContainsKey(pair.Key));

            var selectedCount = 0;
            var selectedBeyondBandCount = 0;
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
                if (ImprovesOnBand(pair.Key, candidate))
                    selectedBeyondBandCount += 1;
                consumed.UnionWith(candidate.GroupIds);
            }

            return new ShadowPackingDiagnostics(
                profileCount,
                groupsWithMultipleProfiles,
                hostCount,
                evaluatedCandidateCount,
                evaluatedBeyondBandLayoutCount,
                evaluatedUsingWiderProfileCount,
                shadowAdoptable.Count,
                adoptableBeyondBandCount,
                adoptableUsingWiderProfileCount,
                selectedCount,
                selectedBeyondBandCount,
                rejectedPixelCount,
                rejectedMemoryCount,
                rejectedMaxDimensionCount,
                rejectedSamplingCount,
                rejectedStreamingCount,
                pixelRejectedEqualCount,
                pixelRejectedGreaterCount,
                pixelGateOnlyCount,
                bestBaselinePixels,
                bestMergedPixels,
                bestGroupCount,
                bestLayoutSize);
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
                        standalone ??= metrics;
                    }
                    catch (ArgumentException exception)
                        when (AutoAtlasMergeIslandRelocator.IsNfdhComparerFailure(
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
                    demands));
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
