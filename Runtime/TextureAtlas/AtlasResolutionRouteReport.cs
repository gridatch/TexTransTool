#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using net.rs64.TexTransCore;
using net.rs64.TexTransTool.TextureAtlas.FineTuning;
using UnityEngine;

namespace net.rs64.TexTransTool.TextureAtlas
{
    internal readonly struct AtlasTextureSizeCount
    {
        internal readonly int Width;
        internal readonly int Height;
        internal readonly int Count;

        internal AtlasTextureSizeCount(int width, int height, int count)
        {
            Width = width;
            Height = height;
            Count = count;
        }
    }

    internal readonly struct AtlasResolutionRouteInstance
    {
        internal readonly AtlasTextureSizeCount[] Inputs;
        internal readonly AtlasTextureSizeCount[] Outputs;

        internal AtlasResolutionRouteInstance(
            IEnumerable<AtlasTextureSizeCount> inputs,
            IEnumerable<AtlasTextureSizeCount> outputs)
        {
            Inputs = inputs.ToArray();
            Outputs = outputs.ToArray();
        }
    }

    internal sealed class AtlasResolutionRoute
    {
        internal AtlasTextureSizeCount[] Inputs { get; }
        internal AtlasTextureSizeCount[] Outputs { get; }
        internal int Occurrences { get; }

        internal AtlasResolutionRoute(
            AtlasTextureSizeCount[] inputs,
            AtlasTextureSizeCount[] outputs,
            int occurrences)
        {
            Inputs = inputs;
            Outputs = outputs;
            Occurrences = occurrences;
        }
    }

    internal static class AtlasResolutionRouteReport
    {
        private const string ResolutionRouteLocalizationKey =
            "AtlasTexture:info:ResolutionRoute";

        internal static AtlasResolutionRoute[] Collect(
            AtlasContext atlasContext,
            TexFineTuningResult fineTuningResult)
        {
            var sourcePropertiesByOutput =
                new Dictionary<ITTRenderTexture, HashSet<string>>(
                    ReferenceComparer<ITTRenderTexture>.Instance);

            foreach (var finalProperty in fineTuningResult.RenderTextures)
            {
                if (!fineTuningResult.SourceProperties.TryGetValue(
                        finalProperty.Key, out var sourceProperties))
                {
                    continue;
                }

                if (!sourcePropertiesByOutput.TryGetValue(
                        finalProperty.Value, out var outputSourceProperties))
                {
                    outputSourceProperties = new HashSet<string>();
                    sourcePropertiesByOutput.Add(
                        finalProperty.Value, outputSourceProperties);
                }

                outputSourceProperties.UnionWith(sourceProperties);
            }

            var activeGroupIds = atlasContext.AtlasSubMeshIndexSetCtx
                .AtlasSubMeshIndexIDHash
                .Select(subMesh => subMesh.MaterialGroupID)
                .Distinct()
                .ToArray();
            var materialGroups = atlasContext.MaterialGroupingCtx.GroupMaterials;

            var routeInstances = new List<AtlasResolutionRouteInstance>();
            foreach (var output in sourcePropertiesByOutput)
            {
                var sourceTextures = new HashSet<Texture>();
                var missingSourceSlots = 0;

                foreach (var sourceProperty in output.Value)
                {
                    foreach (var groupId in activeGroupIds)
                    {
                        var group = materialGroups[groupId];
                        if (group.GroupedTexture.TryGetValue(
                                sourceProperty, out var sourceTexture)
                            && sourceTexture != null)
                        {
                            sourceTextures.Add(sourceTexture);
                        }
                        else
                        {
                            // This material group still owns atlas UV space, but
                            // this property has no source texture for that space.
                            missingSourceSlots += 1;
                        }
                    }
                }

                if (sourceTextures.Count == 0 && missingSourceSlots == 0)
                    continue;

                var inputComposition = sourceTextures
                    .Select(texture => new AtlasTextureSizeCount(
                        texture.width, texture.height, 1))
                    .ToList();
                if (missingSourceSlots != 0)
                {
                    inputComposition.Add(new AtlasTextureSizeCount(
                        0, 0, missingSourceSlots));
                }

                routeInstances.Add(new AtlasResolutionRouteInstance(
                    inputComposition,
                    new[]
                    {
                        new AtlasTextureSizeCount(
                            output.Key.Width, output.Key.Hight, 1),
                    }
                ));
            }

            return AggregateRoutes(routeInstances);
        }

        internal static AtlasResolutionRoute[] AggregateRoutes(
            IEnumerable<AtlasResolutionRouteInstance> instances)
        {
            var groupedRoutes = instances
                .Select(instance => new
                {
                    Inputs = NormalizeComposition(instance.Inputs),
                    Outputs = NormalizeComposition(instance.Outputs),
                })
                .GroupBy(route =>
                    BuildRouteKey(route.Inputs, route.Outputs))
                .Select(group =>
                {
                    var first = group.First();
                    return new AtlasResolutionRoute(
                        first.Inputs,
                        first.Outputs,
                        group.Count()
                    );
                })
                .ToArray();

            Array.Sort(groupedRoutes, CompareRoutes);
            return groupedRoutes;
        }

        internal static FormattableString[] CreateLocalizedLogLines(
            IEnumerable<AtlasResolutionRoute> routes)
        {
            return routes.Select(route => FormattableStringFactory.Create(
                ResolutionRouteLocalizationKey,
                new object[]
                {
                    FormatComposition(route.Inputs),
                    FormatComposition(route.Outputs),
                    route.Occurrences,
                }
            )).ToArray();
        }

        internal static string FormatComposition(
            IReadOnlyList<AtlasTextureSizeCount> composition)
        {
            return string.Join(" + ", composition.Select(item =>
                $"{item.Width}×{item.Height} ×{item.Count}"));
        }

        private static AtlasTextureSizeCount[] NormalizeComposition(
            IEnumerable<AtlasTextureSizeCount> composition)
        {
            var normalized = composition
                .GroupBy(item => (item.Width, item.Height))
                .Select(group => new AtlasTextureSizeCount(
                    group.Key.Width,
                    group.Key.Height,
                    group.Sum(item => item.Count)
                ))
                .ToArray();

            Array.Sort(normalized, CompareSizeCount);
            return normalized;
        }

        private static int CompareRoutes(
            AtlasResolutionRoute left,
            AtlasResolutionRoute right)
        {
            var outputComparison =
                CompareComposition(left.Outputs, right.Outputs);
            if (outputComparison != 0) return outputComparison;

            return CompareComposition(left.Inputs, right.Inputs);
        }

        private static int CompareComposition(
            IReadOnlyList<AtlasTextureSizeCount> left,
            IReadOnlyList<AtlasTextureSizeCount> right)
        {
            var commonLength = Math.Min(left.Count, right.Count);
            for (var index = 0; index < commonLength; index += 1)
            {
                var itemComparison =
                    CompareSizeCount(left[index], right[index]);
                if (itemComparison != 0) return itemComparison;
            }

            // If one composition is an exact prefix of the other,
            // the composition with more entries is shown first.
            return right.Count.CompareTo(left.Count);
        }

        private static int CompareSizeCount(
            AtlasTextureSizeCount left,
            AtlasTextureSizeCount right)
        {
            var leftArea = (long)left.Width * left.Height;
            var rightArea = (long)right.Width * right.Height;

            var comparison = rightArea.CompareTo(leftArea);
            if (comparison != 0) return comparison;

            comparison = right.Width.CompareTo(left.Width);
            if (comparison != 0) return comparison;

            comparison = right.Height.CompareTo(left.Height);
            if (comparison != 0) return comparison;

            return right.Count.CompareTo(left.Count);
        }

        private static string BuildRouteKey(
            IReadOnlyList<AtlasTextureSizeCount> inputs,
            IReadOnlyList<AtlasTextureSizeCount> outputs)
        {
            return $"{BuildCompositionKey(inputs)}->{BuildCompositionKey(outputs)}";
        }

        private static string BuildCompositionKey(
            IReadOnlyList<AtlasTextureSizeCount> composition)
        {
            return string.Join(";", composition.Select(item =>
                $"{item.Width}x{item.Height}x{item.Count}"));
        }

        private sealed class ReferenceComparer<T> : IEqualityComparer<T>
            where T : class
        {
            internal static readonly ReferenceComparer<T> Instance = new();

            public bool Equals(T? left, T? right)
            {
                return ReferenceEquals(left, right);
            }

            public int GetHashCode(T obj)
            {
                return RuntimeHelpers.GetHashCode(obj);
            }
        }
    }
}
