#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using net.rs64.TexTransCore.UVIsland;
using net.rs64.TexTransTool.TextureAtlas;
using UnityEngine;

namespace net.rs64.TexTransTool.NDMF
{
    /// <summary>
    /// Executes the rectangle placements selected by the 2D texture
    /// merging planner. Every atomic group is packed independently into its
    /// reserved region; execution fails closed for incompatible groupings.
    /// </summary>
    internal sealed class AutoAtlas2DMergeIslandRelocator : IIslandRelocator
    {
        internal static bool IsNfdhComparerFailure(ArgumentException exception)
        {
            return exception.Message.IndexOf(
                       "IComparer.Compare() method returns inconsistent results",
                       StringComparison.Ordinal) >= 0
                && (exception.StackTrace?.IndexOf(
                       "NFDHPlasFC.NextFitDecreasingHeightPlusFloorCeiling",
                       StringComparison.Ordinal) ?? -1) >= 0;
        }

        private const float Epsilon = 1e-5f;
        private readonly int[] _groupByIsland;
        private readonly IReadOnlyDictionary<int, RectInt> _regions;
        private readonly int _atlasWidth;
        private readonly bool _valid;
        internal string FailureDetail { get; private set; } = "";

        private RelocationResult Fail(string reason)
        {
            FailureDetail = reason;
            return new RelocationResult(false, true);
        }

        internal AutoAtlas2DMergeIslandRelocator(
            AtlasContext context,
            IReadOnlyDictionary<Material, int> groupByMaterial,
            IReadOnlyDictionary<int, RectInt> regions,
            int atlasWidth)
        {
            _regions = regions;
            _atlasWidth = atlasWidth;
            _groupByIsland = new int[context.SourceVirtualIslands.Length];

            var valid = ValidatePlannedRegions(regions, atlasWidth)
                && groupByMaterial.Values.All(regions.ContainsKey);

            for (var index = 0; index < _groupByIsland.Length; index++)
            {
                // Multiple origin islands and material groups may map onto
                // one source virtual island. They must all belong to exactly
                // one atomic group, otherwise the planned split is impossible.
                var ids = context.ReverseSourceVirtualIsland2OriginIslands[
                        context.SourceVirtualIslands[index]]
                    .Select(origin => context.AtlasIslandCtx
                        .ReverseOriginDict[origin])
                    .Select(submesh => context.MaterialGroupingCtx
                        .GroupMaterials[submesh.MaterialGroupID])
                    .SelectMany(materials => materials)
                    .Select(material =>
                        groupByMaterial.TryGetValue(material, out var id)
                            ? (int?)id
                            : null)
                    .Distinct()
                    .ToArray();

                _groupByIsland[index] = ids.Length == 1 && ids[0].HasValue
                    ? ids[0].Value
                    : -1;
                if (_groupByIsland[index] < 0) valid = false;
            }

            _valid = valid;
            if (!valid)
                FailureDetail = !ValidatePlannedRegions(regions, atlasWidth)
                    ? "計画矩形の重複・範囲外・不足"
                    : "元UV IslandとAtomic Groupの対応が不整合";
        }

        internal static bool ValidatePlannedRegions(
            IReadOnlyDictionary<int, RectInt> regions, int atlasWidth)
        {
            if (regions.Count < 2 || atlasWidth <= 0) return false;

            var rectangles = regions.Values.ToArray();
            foreach (var rect in rectangles)
            {
                if (rect.width <= 0 || rect.height <= 0
                    || rect.xMin < 0 || rect.yMin < 0
                    || rect.xMax > atlasWidth || rect.yMax > atlasWidth)
                    return false;
            }

            for (var a = 0; a < rectangles.Length; a++)
                for (var b = a + 1; b < rectangles.Length; b++)
                    if (rectangles[a].Overlaps(rectangles[b]))
                        return false;
            return true;
        }

        public RelocationResult Relocation(
            RelocationContext context,
            IslandTransform[] islands)
        {
            if (!_valid) return Fail(FailureDetail);
            if (islands.Length == 0
                || islands.Length != _groupByIsland.Length
                || Math.Abs(context.TargetHeight - 1f) > Epsilon)
                return Fail($"Island数またはTargetHeightが不正: islands={islands.Length}, map={_groupByIsland.Length}, targetHeight={context.TargetHeight}");

            var groupedIndices = Enumerable.Range(0, islands.Length)
                .GroupBy(index => _groupByIsland[index]).ToArray();
            if (groupedIndices.Length != _regions.Count)
                return Fail($"実行時group数の相違: actual={groupedIndices.Length}, planned={_regions.Count}");

            foreach (var group in groupedIndices)
            {
                if (!_regions.TryGetValue(group.Key, out var region))
                    return Fail($"g{group.Key}の配置矩形なし");

                // The base island scale is relative to the full atlas width.
                // Convert to the group's profile width for local NFDH, then
                // translate the result back to global atlas UV coordinates.
                var localScale = (float)_atlasWidth / region.width;
                var localIslands = group.Select(index => islands[index]).ToArray();
                foreach (var island in localIslands)
                    island.Size *= localScale;

                // Diagnostic only; never used as the actual placement.
                var profilePaddingProbe = localIslands
                    .Select(island => island.Clone()).ToArray();

                RelocationResult localResult;
                try
                {
                    localResult = new NFDHPlasFC().Relocation(
                        new RelocationContext(
                            1f, context.Padding * localScale),
                        localIslands);
                }
                catch (ArgumentException exception)
                    when (AutoAtlas2DMergeIslandRelocator
                        .IsNfdhComparerFailure(exception))
                {
                    return Fail($"g{group.Key}のNFDH比較処理が失敗: {exception.Message}");
                }

                if (!localResult.IsSuccess)
                {
                    // Check if using the standalone-profile padding would fit.
                    // This is solely to diagnose any padding-model mismatch.
                    string probeResult;
                    try
                    {
                        var trial = new NFDHPlasFC().Relocation(
                            new RelocationContext(1f, context.Padding),
                            profilePaddingProbe);
                        probeResult = trial.IsSuccess.ToString();
                    }
                    catch (Exception)
                    {
                        probeResult = "error";
                    }
                    return Fail($"g{group.Key}の矩形内NFDH配置に失敗: region={region}, islands={localIslands.Length}, globalPadding={context.Padding}, localPadding={context.Padding * localScale}, standalonePaddingFit={probeResult}");
                }

                var minX = (float)region.xMin / _atlasWidth;
                var minY = (float)region.yMin / _atlasWidth;

                foreach (var island in localIslands)
                {
                    island.Size /= localScale;
                    island.Position /= localScale;
                    island.Position.X += minX;
                    island.Position.Y += minY;
                }

                // NFDH can succeed while leaving less than half-padding at
                // a region edge. Shift the entire group without changing
                // island size, rotation, or relative placement.
                var invalidIsland = localIslands.FirstOrDefault(island =>
                    !IsWithinPlannedRegion(
                        island, region, _atlasWidth, context.Padding));
                if (invalidIsland != null)
                {
                    if (!TryTranslateIslandsWithinPlannedRegion(
                            localIslands, region, _atlasWidth, context.Padding)
                        || localIslands.Any(island =>
                            !IsWithinPlannedRegion(
                                island, region, _atlasWidth, context.Padding)))
                    {
                        var unresolved = localIslands.FirstOrDefault(island =>
                            !IsWithinPlannedRegion(
                                island, region, _atlasWidth, context.Padding))
                            ?? invalidIsland;
                        var box = unresolved.GetBox();
                        return Fail($"g{group.Key}の実配置が矩形境界の必要余白を満たさない: region={region}, box=({box.X},{box.Y},{box.Z},{box.W}), globalPadding={context.Padding}, perSideMargin={context.Padding * 0.5f}");
                    }
                }
            }

            return new RelocationResult(true, true);
        }


        // NFDH's edge allowance can differ from the reserved region's
        // half-padding requirement. A translation preserves all inter-island
        // distances and rotations; never scale or relax the boundary check.
        internal static bool TryTranslateIslandsWithinPlannedRegion(
            IReadOnlyList<IslandTransform> islands, RectInt region,
            int atlasWidth, float padding)
        {
            if (islands.Count == 0 || atlasWidth <= 0
                || region.width <= 0 || region.height <= 0
                || region.xMin < 0 || region.yMin < 0
                || region.xMax > atlasWidth || region.yMax > atlasWidth
                || padding < 0f
                || float.IsNaN(padding) || float.IsInfinity(padding))
                return false;

            var minX = float.PositiveInfinity;
            var minY = float.PositiveInfinity;
            var maxX = float.NegativeInfinity;
            var maxY = float.NegativeInfinity;
            foreach (var island in islands)
            {
                var box = island.GetBox();
                if (float.IsNaN(box.X) || float.IsNaN(box.Y)
                    || float.IsNaN(box.Z) || float.IsNaN(box.W)
                    || float.IsInfinity(box.X) || float.IsInfinity(box.Y)
                    || float.IsInfinity(box.Z) || float.IsInfinity(box.W))
                    return false;

                minX = Math.Min(minX, box.X);
                minY = Math.Min(minY, box.Y);
                maxX = Math.Max(maxX, box.Z);
                maxY = Math.Max(maxY, box.W);
            }

            var halfPadding = padding * 0.5f;
            var shiftMinX = (float)region.xMin / atlasWidth + halfPadding - minX;
            var shiftMinY = (float)region.yMin / atlasWidth + halfPadding - minY;
            var shiftMaxX = (float)region.xMax / atlasWidth - halfPadding - maxX;
            var shiftMaxY = (float)region.yMax / atlasWidth - halfPadding - maxY;
            if (shiftMinX > shiftMaxX || shiftMinY > shiftMaxY)
                return false;

            // Keep an already-valid axis unchanged. Otherwise select the
            // smallest translation that fits within all four boundaries.
            var shiftX = Math.Max(shiftMinX, Math.Min(0f, shiftMaxX));
            var shiftY = Math.Max(shiftMinY, Math.Min(0f, shiftMaxY));
            foreach (var island in islands)
                island.Position += new System.Numerics.Vector2(shiftX, shiftY);

            return true;
        }

        internal static bool IsWithinPlannedRegion(
            IslandTransform island, RectInt region, int atlasWidth,
            float padding)
        {
            if (atlasWidth <= 0 || padding < 0f
                || float.IsNaN(padding) || float.IsInfinity(padding))
                return false;

            var box = island.GetBox();
            if (float.IsNaN(box.X) || float.IsNaN(box.Y)
                || float.IsNaN(box.Z) || float.IsNaN(box.W)
                || float.IsInfinity(box.X) || float.IsInfinity(box.Y)
                || float.IsInfinity(box.Z) || float.IsInfinity(box.W))
                return false;

            // NFDH has already placed and padded the islands. Requiring a
            // second full padding at each boundary rejects ordinary valid
            // NFDH results. Reserve at least half padding at each edge: two
            // adjacent, nonoverlapping regions then have a full padding gap.
            var halfPadding = padding * 0.5f;
            var minX = (float)region.xMin / atlasWidth;
            var minY = (float)region.yMin / atlasWidth;
            var maxX = (float)region.xMax / atlasWidth;
            var maxY = (float)region.yMax / atlasWidth;
            return box.X >= minX + halfPadding - Epsilon
                && box.Y >= minY + halfPadding - Epsilon
                && box.Z <= maxX - halfPadding + Epsilon
                && box.W <= maxY - halfPadding + Epsilon;
        }
    }
}
