#nullable enable
using System;
using UnityEngine;

namespace net.rs64.TexTransTool.TextureAtlas
{
    public sealed partial class AtlasTexture
    {
        internal sealed class AtlasExecutionOptions
        {
            internal bool RequireLossless;
            internal bool ReportProgressInfo = true;
            internal Func<AtlasContext, Vector2Int, AtlasExecutionMetrics, bool>? AcceptAtlasSize;
        }

        internal readonly struct AtlasExecutionMetrics
        {
            internal readonly float TopFreeFraction;
            internal readonly int TotalRelocateCount;
            internal readonly long RelocationTimeMilliseconds;

            internal AtlasExecutionMetrics(
                float topFreeFraction,
                int totalRelocateCount,
                long relocationTimeMilliseconds)
            {
                TopFreeFraction = topFreeFraction;
                TotalRelocateCount = totalRelocateCount;
                RelocationTimeMilliseconds = relocationTimeMilliseconds;
            }
        }
    }
}
