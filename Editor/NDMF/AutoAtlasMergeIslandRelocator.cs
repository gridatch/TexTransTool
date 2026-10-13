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
    /// Relocator used only by additional AutoAtlas merging.
    ///
    /// Each atomic texture-connected group is packed independently with the same
    /// NFDH relocator used by ordinary AtlasTexture, then those already-valid
    /// horizontal bands are stacked vertically. This deliberately gives up
    /// theoretical area-optimal packing in exchange for a property the planner can
    /// reason about cheaply and exactly: only the used height of each band matters.
    /// </summary>
    internal sealed class AutoAtlasMergeIslandRelocator : IIslandRelocator
    {
        private const float Epsilon = 1e-5f;
        private readonly int[] _clusterBySourceIsland;

        internal AutoAtlasMergeIslandRelocator(
            AtlasContext atlasContext,
            IReadOnlyDictionary<Material, int> atomicGroupByMaterial)
        {
            var allAtomicIds = atomicGroupByMaterial.Values.Distinct().ToArray();
            var parent = allAtomicIds.ToDictionary(id => id, id => id);

            int Find(int id)
            {
                var root = id;
                while (parent[root] != root) root = parent[root];
                while (parent[id] != id)
                {
                    var next = parent[id];
                    parent[id] = root;
                    id = next;
                }
                return root;
            }

            void Union(int left, int right)
            {
                left = Find(left);
                right = Find(right);
                if (left == right) return;

                // Stable root for deterministic packing order.
                var root = Math.Min(left, right);
                var child = Math.Max(left, right);
                parent[child] = root;
            }

            // MaterialGroupingContext may combine compatible materials from
            // different atomic groups. They then share one atlas material group and
            // cannot be separated at island level, so treat them as one band cluster.
            for (var materialGroupId = 0;
                 materialGroupId < atlasContext.MaterialGroupingCtx.GroupMaterials.Length;
                 materialGroupId += 1)
            {
                var atomicIds = atlasContext.MaterialGroupingCtx
                    .GroupMaterials[materialGroupId]
                    .Select(material =>
                        atomicGroupByMaterial.TryGetValue(material, out var id)
                            ? (int?)id
                            : null)
                    .Where(id => id.HasValue)
                    .Select(id => id!.Value)
                    .Distinct()
                    .OrderBy(id => id)
                    .ToArray();

                for (var index = 1; index < atomicIds.Length; index += 1)
                    Union(atomicIds[0], atomicIds[index]);
            }

            _clusterBySourceIsland =
                new int[atlasContext.SourceVirtualIslands.Length];

            for (var index = 0;
                 index < atlasContext.SourceVirtualIslands.Length;
                 index += 1)
            {
                var sourceIsland = atlasContext.SourceVirtualIslands[index];
                var originIsland = atlasContext
                    .ReverseSourceVirtualIsland2OriginIslands[sourceIsland]
                    .First();
                var atlasSubMesh =
                    atlasContext.AtlasIslandCtx.ReverseOriginDict[originIsland];
                var materialGroup = atlasContext.MaterialGroupingCtx
                    .GroupMaterials[atlasSubMesh.MaterialGroupID];

                var atomicIds = materialGroup
                    .Select(material =>
                        atomicGroupByMaterial.TryGetValue(material, out var id)
                            ? (int?)id
                            : null)
                    .Where(id => id.HasValue)
                    .Select(id => Find(id!.Value))
                    .Distinct()
                    .ToArray();

                _clusterBySourceIsland[index] =
                    atomicIds.Length == 0 ? -1 : atomicIds.Min();
            }
        }

        public RelocationResult Relocation(
            RelocationContext relocationContext,
            IslandTransform[] islandTransforms)
        {
            if (islandTransforms.Length != _clusterBySourceIsland.Length
                || islandTransforms.Length == 0
                || _clusterBySourceIsland.Any(cluster => cluster < 0))
            {
                return new RelocationResult(false, true);
            }

            var groups = Enumerable.Range(0, islandTransforms.Length)
                .GroupBy(index => _clusterBySourceIsland[index])
                .OrderBy(group => group.Key)
                .ToArray();

            var offsetY = 0f;
            foreach (var group in groups)
            {
                var localIslands = group
                    .Select(index => islandTransforms[index])
                    .ToArray();

                RelocationResult localResult;
                try
                {
                    localResult = new NFDHPlasFC().Relocation(
                        new RelocationContext(
                            relocationContext.TargetHeight,
                            relocationContext.Padding),
                        localIslands);
                }
                catch (ArgumentException exception)
                    when (IsNfdhComparerFailure(exception))
                {
                    return new RelocationResult(false, true);
                }

                if (!localResult.IsSuccess)
                    return new RelocationResult(false, true);

                var maxY = 0f;
                foreach (var island in localIslands)
                {
                    var box = island.GetBox();
                    if (float.IsNaN(box.Y) || float.IsNaN(box.W)
                        || float.IsInfinity(box.Y) || float.IsInfinity(box.W))
                    {
                        return new RelocationResult(false, true);
                    }
                    maxY = Math.Max(maxY, box.W);
                }

                var bandHeight = maxY + relocationContext.Padding;
                if (offsetY + bandHeight
                    > relocationContext.TargetHeight + Epsilon)
                {
                    return new RelocationResult(false, true);
                }

                if (offsetY > 0f)
                {
                    foreach (var island in localIslands)
                        island.Position.Y += offsetY;
                }

                offsetY += bandHeight;
            }

            return new RelocationResult(true, true);
        }

        internal static bool IsNfdhComparerFailure(ArgumentException exception)
        {
            return exception.Message.IndexOf(
                       "IComparer.Compare() method returns inconsistent results",
                       StringComparison.Ordinal) >= 0
                && (exception.StackTrace?.IndexOf(
                        "NFDHPlasFC.NextFitDecreasingHeightPlusFloorCeiling",
                        StringComparison.Ordinal) ?? -1) >= 0;
        }
    }
}
