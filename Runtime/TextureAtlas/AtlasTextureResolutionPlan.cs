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

                    var primaryTexture =
                        atlasContext.MaterialGroupingCtx.GetPrimaryTexture(groupId);
                    primarySizes.Add(primaryTexture == null
                        ? null
                        : new Vector2Int(primaryTexture.width, primaryTexture.height));
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

            var candidate = layoutSize;
            if (HasSufficientTexelDensity(
                    candidate, layoutSize, primarySizes, sourceSizes))
            {
                while (candidate.x / 2 >= MinimumTextureDimension
                    && candidate.y / 2 >= MinimumTextureDimension)
                {
                    var smaller = new Vector2Int(
                        candidate.x / 2,
                        candidate.y / 2
                    );
                    if (!HasSufficientTexelDensity(
                            smaller, layoutSize, primarySizes, sourceSizes))
                    {
                        break;
                    }

                    candidate = smaller;
                }

                return candidate;
            }

            while (!HasSufficientTexelDensity(
                       candidate, layoutSize, primarySizes, sourceSizes))
            {
                if (candidate.x > int.MaxValue / 2
                    || candidate.y > int.MaxValue / 2)
                {
                    throw new InvalidOperationException(
                        "Required lossless atlas texture size is too large.");
                }

                candidate = new Vector2Int(
                    candidate.x * 2,
                    candidate.y * 2
                );
            }

            return candidate;
        }

        private static bool HasSufficientTexelDensity(
            Vector2Int candidateSize,
            Vector2Int layoutSize,
            IReadOnlyList<Vector2Int?> primarySizes,
            IReadOnlyList<Vector2Int> sourceSizes)
        {
            for (var index = 0; index < sourceSizes.Count; index += 1)
            {
                var primarySize = primarySizes[index];
                if (!primarySize.HasValue)
                {
                    if (candidateSize.x < layoutSize.x
                        || candidateSize.y < layoutSize.y)
                    {
                        return false;
                    }

                    continue;
                }

                var primary = primarySize.Value;
                var source = sourceSizes[index];
                if ((long)candidateSize.x * primary.x
                        < (long)layoutSize.x * source.x
                    || (long)candidateSize.y * primary.y
                        < (long)layoutSize.y * source.y)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
