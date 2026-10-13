#nullable enable
using System;
using System.Collections.Generic;
using net.rs64.TexTransCore.UVIsland;
using UnityEngine;

namespace net.rs64.TexTransTool.TextureAtlas
{
    public sealed partial class AtlasTexture
    {
        internal sealed class AtlasExecutionOptions
        {
            internal bool RequireLossless;
            internal bool ReportProgressInfo = true;
            internal Func<AtlasContext, IIslandRelocator>? IslandRelocatorFactory;
            internal Func<AtlasContext, Vector2Int, AtlasExecutionMetrics, bool>? AcceptAtlasSize;
        }

        internal readonly struct AtlasRelocationMetrics
        {
            internal readonly float TopFreeFraction;
            internal readonly float PriorityDownScale;
            internal readonly float OverallDownScale;
            internal readonly int TotalRelocateCount;
            internal readonly long RelocationTimeMilliseconds;

            internal AtlasRelocationMetrics(
                float topFreeFraction,
                float priorityDownScale,
                float overallDownScale,
                int totalRelocateCount,
                long relocationTimeMilliseconds)
            {
                TopFreeFraction = topFreeFraction;
                PriorityDownScale = priorityDownScale;
                OverallDownScale = overallDownScale;
                TotalRelocateCount = totalRelocateCount;
                RelocationTimeMilliseconds = relocationTimeMilliseconds;
            }
        }

        internal readonly struct AtlasExecutionMetrics
        {
            internal readonly float TopFreeFraction;
            internal readonly int TotalRelocateCount;
            internal readonly long RelocationTimeMilliseconds;
            internal readonly long OutputTexturePixelCount;
            internal readonly Vector2Int LayoutSize;
            internal readonly IReadOnlyDictionary<string, Vector2Int> PropertySizes;

            internal AtlasExecutionMetrics(
                float topFreeFraction,
                int totalRelocateCount,
                long relocationTimeMilliseconds,
                long outputTexturePixelCount,
                Vector2Int layoutSize,
                IReadOnlyDictionary<string, Vector2Int> propertySizes)
            {
                TopFreeFraction = topFreeFraction;
                TotalRelocateCount = totalRelocateCount;
                RelocationTimeMilliseconds = relocationTimeMilliseconds;
                OutputTexturePixelCount = outputTexturePixelCount;
                LayoutSize = layoutSize;
                PropertySizes = propertySizes;
            }
        }
    }
}
