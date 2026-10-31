#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace net.rs64.TexTransTool.TextureAtlas
{
    internal sealed class AtlasTextureResolutionPlan
    {
        private const int MinimumTextureDimension = 4;

        internal Vector2Int LayoutSize { get; }
        internal IReadOnlyDictionary<string, Vector2Int> PropertySizes { get; }
        internal long TotalPixelCount { get; }

        private AtlasTextureResolutionPlan(
            Vector2Int layoutSize,
            IReadOnlyDictionary<string, Vector2Int> propertySizes)
        {
            LayoutSize = layoutSize;
            PropertySizes = propertySizes;
            TotalPixelCount = propertySizes.Values.Sum(size =>
                (long)size.x * size.y);
        }

        internal static AtlasTextureResolutionPlan Create(
            AtlasContext atlasContext,
            AtlasSetting atlasSetting,
            Vector2Int layoutSize)
        {
            var materialGroups = atlasContext.MaterialGroupingCtx.GroupMaterials;
            var activeGroupIds = atlasContext.AtlasSubMeshIndexSetCtx
                .AtlasSubMeshIndexIDHash
                .Select(subMesh => subMesh.MaterialGroupID)
                .Distinct()
                .ToArray();

            var propertyNames = activeGroupIds
                .SelectMany(groupId => materialGroups[groupId].GroupedTexture.Keys)
                .Distinct()
                .OrderBy(propertyName => propertyName, StringComparer.Ordinal);

            var propertySizes = new Dictionary<string, Vector2Int>();
            foreach (var propertyName in propertyNames)
            {
                var primarySizes = new List<Vector2Int?>();
                var sourceSizes = new List<Vector2Int>();

                foreach (var groupId in activeGroupIds)
                {
                    var group = materialGroups[groupId];
                    if (!group.GroupedTexture.TryGetValue(propertyName, out var sourceTexture)
                        || sourceTexture == null)
                    {
                        continue;
                    }

                    primarySizes.Add(
                        atlasContext.MaterialGroupingCtx
                            .GetPrimaryDensitySize(groupId));
                    sourceSizes.Add(new Vector2Int(
                        sourceTexture.width,
                        sourceTexture.height
                    ));
                }

                propertySizes[propertyName] = CalculatePropertySize(
                    layoutSize,
                    atlasSetting.AutoAtlasTextureSize,
                    primarySizes,
                    sourceSizes
                );
            }

            return new AtlasTextureResolutionPlan(layoutSize, propertySizes);
        }

        internal static Vector2Int CalculatePropertySize(
            Vector2Int layoutSize,
            bool autoSize,
            IReadOnlyList<Vector2Int?> primarySizes,
            IReadOnlyList<Vector2Int> sourceSizes)
        {
            if (primarySizes.Count != sourceSizes.Count)
                throw new ArgumentException(
                    "Primary/source texture resolution counts must match.");

            if (!autoSize || sourceSizes.Count == 0)
                return layoutSize;

            return new Vector2Int(
                CalculateAxisSize(
                    layoutSize.x,
                    isWidth: true,
                    layoutSize,
                    primarySizes,
                    sourceSizes),
                CalculateAxisSize(
                    layoutSize.y,
                    isWidth: false,
                    layoutSize,
                    primarySizes,
                    sourceSizes));
        }

        private static int CalculateAxisSize(
            int layoutDimension,
            bool isWidth,
            Vector2Int layoutSize,
            IReadOnlyList<Vector2Int?> primarySizes,
            IReadOnlyList<Vector2Int> sourceSizes)
        {
            var candidate = layoutDimension;
            if (HasSufficientTexelDensityForAxis(
                    candidate, isWidth, layoutSize, primarySizes, sourceSizes))
            {
                while (candidate / 2 >= MinimumTextureDimension)
                {
                    var smaller = candidate / 2;
                    if (!HasSufficientTexelDensityForAxis(
                            smaller, isWidth, layoutSize,
                            primarySizes, sourceSizes))
                    {
                        break;
                    }

                    candidate = smaller;
                }

                return candidate;
            }

            while (!HasSufficientTexelDensityForAxis(
                       candidate, isWidth, layoutSize,
                       primarySizes, sourceSizes))
            {
                if (candidate > int.MaxValue / 2)
                {
                    throw new InvalidOperationException(
                        "Required lossless atlas texture size is too large.");
                }

                candidate *= 2;
            }

            return candidate;
        }

        private static bool HasSufficientTexelDensityForAxis(
            int candidateDimension,
            bool isWidth,
            Vector2Int layoutSize,
            IReadOnlyList<Vector2Int?> primarySizes,
            IReadOnlyList<Vector2Int> sourceSizes)
        {
            for (var index = 0; index < sourceSizes.Count; index += 1)
            {
                var primarySize = primarySizes[index];
                if (!primarySize.HasValue)
                {
                    if (candidateDimension < (isWidth
                            ? layoutSize.x
                            : layoutSize.y))
                    {
                        return false;
                    }

                    continue;
                }

                var primaryDimension = isWidth
                    ? primarySize.Value.x
                    : primarySize.Value.y;
                var sourceDimension = isWidth
                    ? sourceSizes[index].x
                    : sourceSizes[index].y;
                var layoutDimension = isWidth
                    ? layoutSize.x
                    : layoutSize.y;
                if ((long)candidateDimension * primaryDimension
                    < (long)layoutDimension * sourceDimension)
                {
                    return false;
                }
            }

            return true;
        }

    }
}
