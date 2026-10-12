#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        // Full Atlas relocation is expensive. Generate merge candidates using
        // standalone metrics first, then run the real relocation only for the
        // highest-value proposals.
        private const int MaxMergeGroupCount = 12;
        private const int MaxFullMergeAnalyses = 32;
        private const int PairAnalysisQuota = 20;
        private const int TripleAnalysisQuota = 8;
        private const int LargerAnalysisQuota = 4;
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
            internal readonly IReadOnlyList<string> DiagnosticLines;

            internal PlanResult(
                IReadOnlyList<MergeSelection> selections,
                IReadOnlyList<string>? diagnosticLines = null)
            {
                Selections = selections;
                DiagnosticLines = diagnosticLines ?? Array.Empty<string>();
            }
        }

        private sealed class CheapProposal
        {
            internal readonly int HostId;
            internal readonly int[] GroupIds;
            internal readonly string Key;
            internal readonly double Score;
            internal readonly long PotentialGuestSavingsBytes;
            internal readonly double CapacityUseRatio;

            internal CheapProposal(
                int hostId,
                IEnumerable<int> groupIds,
                double score,
                long potentialGuestSavingsBytes,
                double capacityUseRatio)
            {
                HostId = hostId;
                GroupIds = groupIds.OrderBy(id => id).ToArray();
                Key = string.Join(",", GroupIds);
                Score = score;
                PotentialGuestSavingsBytes = potentialGuestSavingsBytes;
                CapacityUseRatio = capacityUseRatio;
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
            internal readonly string DecisionReason;

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

                var failedGates = new List<string>();
                if (SavingRate < minimumSavingRate)
                    failedGates.Add("minimum-savings");
                if (merged.PixelCount >= baseline.PixelCount)
                    failedGates.Add("pixel-count");
                if (merged.FullResidentBytes >= baseline.FullResidentBytes)
                    failedGates.Add("resident-memory");
                if (merged.MaxTextureDimension > baseline.MaxTextureDimension)
                    failedGates.Add("max-dimension");
                if (HasSamplingRegression(baseline, merged, groupIds))
                    failedGates.Add("format-sampling");
                if (HasStreamingRegression(baseline, merged, groupIds, usage))
                    failedGates.Add("mip-streaming");

                IsAdoptable = failedGates.Count == 0;
                DecisionReason = IsAdoptable
                    ? "accepted"
                    : string.Join("+", failedGates);
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
            AnimatorServicesContext animatorServices,
            AtlasSetting settings,
            IReadOnlyDictionary<Material, IReadOnlyDictionary<string, Texture>> propertyTextures,
            IReadOnlyList<PreparedGroup> groups)
        {
            if (!configuration.EnableAdditionalAtlasMerging || groups.Count < 2)
                return new PlanResult(Array.Empty<MergeSelection>());

            var plannerWatch = Stopwatch.StartNew();
            var diagnosticLines = new List<string>();
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

            var hostCount = groups.Count(group =>
                group.StandaloneMetrics is { } metrics
                && metrics.FreeAreaFraction >= configuration.MinimumMergeHostFreeAreaRatio);
            diagnosticLines.Add(
                $"atomic groups={groups.Count}, merge hosts={hostCount}, host free-area threshold={configuration.MinimumMergeHostFreeAreaRatio:P0}");

            foreach (var group in groups.OrderBy(group => group.Id))
            {
                var materialNames = string.Join(", ",
                    group.Materials.Select(material => material.name)
                        .Distinct()
                        .OrderBy(name => name, StringComparer.Ordinal));
                var freeArea = group.StandaloneMetrics?.FreeAreaFraction;
                diagnosticLines.Add(
                    $"group {group.Id}: [{materialNames}], free-area={(freeArea.HasValue ? freeArea.Value.ToString("P1") : "n/a")}, baseline={group.BaselineCost.FullResidentBytes} B");
            }

            var usage = AnalyzeRuntimeUsage(animatorServices, groups);
            var byId = groups.ToDictionary(group => group.Id);
            var adoptable = new Dictionary<string, Candidate>();

            // Candidate generation is intentionally cheap: it uses only the
            // standalone layouts/costs already computed above. We then run the
            // real TexTransCore relocation for a small, deterministic number of
            // the most promising unique subsets.
            var proposals = BuildCheapMergeProposals(
                configuration, groups);
            var selectedProposals = SelectFullAnalysisProposals(proposals);

            diagnosticLines.Add(
                $"cheap proposals={proposals.Count}, full analyses={selectedProposals.Count}/{MaxFullMergeAnalyses}, " +
                $"pairs={selectedProposals.Count(proposal => proposal.GroupIds.Length == 2)}, " +
                $"triples={selectedProposals.Count(proposal => proposal.GroupIds.Length == 3)}, " +
                $"larger={selectedProposals.Count(proposal => proposal.GroupIds.Length >= 4)}");

            var relocationRejected = 0;
            foreach (var proposal in selectedProposals)
            {
                var ids = proposal.GroupIds;
                var materials = ids.SelectMany(id => byId[id].Materials)
                    .ToHashSet();
                var renderers = ids.SelectMany(id => byId[id].Renderers)
                    .Distinct().ToArray();

                var metrics = AnalyzeAtlas(
                    domain, engine, settings, materials, renderers);
                if (metrics is null)
                {
                    relocationRejected += 1;
                    diagnosticLines.Add(
                        $"candidate [{FormatGroupMaterials(ids, byId)}]: rejected=relocation, " +
                        $"host=[{FormatGroupMaterials(new[] { proposal.HostId }, byId)}], " +
                        $"potential-guest-saving={proposal.PotentialGuestSavingsBytes} B, " +
                        $"capacity-use={proposal.CapacityUseRatio:P1}, score={proposal.Score:0.##}");
                    continue;
                }

                var baseline = TextureCostPlan.Combine(
                    ids.Select(id => byId[id].BaselineCost));
                var merged = BuildGeneratedCost(
                    domain,
                    ids.Select(id => byId[id]).ToArray(),
                    propertyTextures,
                    metrics.Value.PropertySizes,
                    alphaCache);

                var candidate = new Candidate(
                    ids,
                    baseline,
                    merged,
                    metrics.Value,
                    configuration.MinimumAdditionalMergeSavingsRate,
                    usage);

                diagnosticLines.Add(
                    $"candidate [{FormatGroupMaterials(ids, byId)}]: " +
                    $"host=[{FormatGroupMaterials(new[] { proposal.HostId }, byId)}], " +
                    $"baseline={baseline.FullResidentBytes} B, merged={merged.FullResidentBytes} B, " +
                    $"saving={candidate.SavingRate:P1}, pixels={baseline.PixelCount}->{merged.PixelCount}, " +
                    $"max={baseline.MaxTextureDimension}->{merged.MaxTextureDimension}, decision={candidate.DecisionReason}");

                if (candidate.IsAdoptable)
                    adoptable[proposal.Key] = candidate;
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

            plannerWatch.Stop();
            diagnosticLines.Insert(0,
                $"planner time={plannerWatch.ElapsedMilliseconds} ms, relocation rejects={relocationRejected}, adopted merges={selected.Count}");
            return new PlanResult(selected, diagnosticLines);
        }

        private static string FormatGroupMaterials(
            IEnumerable<int> groupIds,
            IReadOnlyDictionary<int, PreparedGroup> byId)
        {
            return string.Join(", ",
                groupIds.SelectMany(id => byId[id].Materials)
                    .Select(material => material.name)
                    .Distinct()
                    .OrderBy(name => name, StringComparer.Ordinal));
        }

        private static IReadOnlyList<CheapProposal> SelectFullAnalysisProposals(
            IReadOnlyList<CheapProposal> proposals)
        {
            var selected = new List<CheapProposal>();
            var selectedKeys = new HashSet<string>();

            void AddBucket(IEnumerable<CheapProposal> bucket, int quota)
            {
                if (quota <= 0) return;

                var ordered = bucket
                    .OrderByDescending(proposal => proposal.Score)
                    .ThenBy(proposal => proposal.Key, StringComparer.Ordinal)
                    .ToArray();
                if (ordered.Length == 0) return;

                // First pass: one best proposal per host, so a single large host
                // cannot consume the whole expensive-analysis budget.
                foreach (var proposal in ordered
                             .GroupBy(proposal => proposal.HostId)
                             .Select(group => group.First())
                             .OrderByDescending(proposal => proposal.Score)
                             .ThenBy(proposal => proposal.HostId)
                             .ThenBy(proposal => proposal.Key, StringComparer.Ordinal))
                {
                    if (selected.Count(candidate =>
                            BucketClass(candidate.GroupIds.Length)
                                == BucketClass(proposal.GroupIds.Length))
                        >= quota)
                    {
                        break;
                    }

                    if (selectedKeys.Add(proposal.Key))
                        selected.Add(proposal);
                }

                // Second pass: fill unused quota by score.
                foreach (var proposal in ordered)
                {
                    if (selected.Count(candidate =>
                            BucketClass(candidate.GroupIds.Length)
                                == BucketClass(proposal.GroupIds.Length))
                        >= quota)
                    {
                        break;
                    }

                    if (selectedKeys.Add(proposal.Key))
                        selected.Add(proposal);
                }
            }

            static int BucketClass(int groupCount)
            {
                return groupCount switch
                {
                    2 => 2,
                    3 => 3,
                    _ => 4,
                };
            }

            AddBucket(
                proposals.Where(proposal => proposal.GroupIds.Length == 2),
                PairAnalysisQuota);
            AddBucket(
                proposals.Where(proposal => proposal.GroupIds.Length == 3),
                TripleAnalysisQuota);
            AddBucket(
                proposals.Where(proposal => proposal.GroupIds.Length >= 4),
                LargerAnalysisQuota);

            // If one bucket did not have enough proposals, use the remaining
            // global budget for the best not-yet-selected candidates.
            foreach (var proposal in proposals
                         .OrderBy(proposal => proposal.GroupIds.Length)
                         .ThenByDescending(proposal => proposal.Score)
                         .ThenBy(proposal => proposal.Key, StringComparer.Ordinal))
            {
                if (selected.Count >= MaxFullMergeAnalyses) break;
                if (selectedKeys.Add(proposal.Key))
                    selected.Add(proposal);
            }

            return selected.Take(MaxFullMergeAnalyses).ToArray();
        }

        private static IReadOnlyList<CheapProposal> BuildCheapMergeProposals(
            AutoAtlasTexture configuration,
            IReadOnlyList<PreparedGroup> groups)
        {
            var proposals = new Dictionary<string, CheapProposal>();

            static double LayoutPixels(PreparedGroup group)
            {
                if (group.StandaloneMetrics is not { } metrics) return 0d;
                return (double)metrics.LayoutSize.x * metrics.LayoutSize.y;
            }

            static double OccupiedPixels(PreparedGroup group)
            {
                if (group.StandaloneMetrics is not { } metrics) return 0d;
                return LayoutPixels(group) * Math.Max(
                    0d, 1d - metrics.FreeAreaFraction);
            }

            static double NoGrowthCapacityPixels(PreparedGroup group)
            {
                if (group.StandaloneMetrics is not { } metrics) return 0d;

                // The final hard gate forbids increasing the largest output
                // texture dimension. A layout may still grow from a cropped
                // rectangle toward a square without violating that gate. Use the
                // larger of the current layout dimension and baseline physical
                // texture dimension as a conservative cheap-search capacity.
                var allowedDimension = Math.Max(
                    Math.Max(metrics.LayoutSize.x, metrics.LayoutSize.y),
                    group.BaselineCost.MaxTextureDimension);
                return (double)allowedDimension * allowedDimension;
            }

            void AddGreedyPrefixes(
                PreparedGroup host,
                IEnumerable<PreparedGroup> orderedGuests)
            {
                var ids = new List<int> { host.Id };
                var hostOccupied = OccupiedPixels(host);
                var capacity = Math.Max(hostOccupied, NoGrowthCapacityPixels(host));
                var occupied = hostOccupied;
                long potentialGuestSavings = 0;

                foreach (var guest in orderedGuests)
                {
                    if (ids.Count >= MaxMergeGroupCount) break;

                    var guestOccupied = OccupiedPixels(guest);
                    if (guestOccupied <= 0d) continue;

                    // If even the sum of occupied pixel area cannot fit before
                    // increasing the host's allowed maximum dimension, this
                    // proposal can never pass the max-dimension hard gate.
                    if (occupied + guestOccupied > capacity * 1.0001d)
                        continue;

                    ids.Add(guest.Id);
                    occupied += guestOccupied;
                    potentialGuestSavings += guest.BaselineCost.FullResidentBytes;

                    var capacityUse = capacity <= 0d ? 1d : occupied / capacity;
                    var fitQuality = 1d - Math.Abs(0.85d - capacityUse);
                    fitQuality = Math.Max(0.1d, fitQuality);

                    // Only guest memory is potential *additional* savings.
                    // Including the host baseline made large hosts dominate the
                    // global ranking even when every guest caused atlas growth.
                    var score = potentialGuestSavings * fitQuality;

                    var proposal = new CheapProposal(
                        host.Id,
                        ids,
                        score,
                        potentialGuestSavings,
                        capacityUse);
                    if (!proposals.TryGetValue(proposal.Key, out var prior)
                        || proposal.Score > prior.Score)
                    {
                        proposals[proposal.Key] = proposal;
                    }
                }
            }

            foreach (var host in groups.Where(group =>
                         group.StandaloneMetrics is { } metrics
                         && metrics.FreeAreaFraction >=
                         configuration.MinimumMergeHostFreeAreaRatio))
            {
                var guests = groups
                    .Where(group =>
                        group.Id != host.Id
                        && group.StandaloneMetrics != null)
                    .ToArray();
                if (guests.Length == 0) continue;

                var remainingCapacity = Math.Max(
                    1d,
                    NoGrowthCapacityPixels(host) - OccupiedPixels(host));

                // 1. Best area fit first.
                AddGreedyPrefixes(
                    host,
                    guests
                        .OrderBy(group =>
                            Math.Abs(remainingCapacity - OccupiedPixels(group)))
                        .ThenByDescending(group =>
                            group.BaselineCost.FullResidentBytes)
                        .ThenBy(group => group.Id));

                // 2. Highest potential additional memory saving first.
                AddGreedyPrefixes(
                    host,
                    guests
                        .OrderByDescending(group =>
                            group.BaselineCost.FullResidentBytes)
                        .ThenBy(group => group.Id));

                // 3. Small groups first, preserving the 3+ material packing case.
                AddGreedyPrefixes(
                    host,
                    guests
                        .OrderBy(OccupiedPixels)
                        .ThenByDescending(group =>
                            group.BaselineCost.FullResidentBytes)
                        .ThenBy(group => group.Id));
            }

            return proposals.Values.ToArray();
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
