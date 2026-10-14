#nullable enable
using System;
using System.Collections.Generic;
using net.rs64.TexTransCore.UVIsland;
using UnityEngine;

namespace net.rs64.TexTransTool.TextureAtlas
{
    public sealed partial class AtlasTexture
    {
        internal sealed class PreparedAtlasContext : IDisposable
        {
            internal readonly AtlasContext AtlasContext;
            internal readonly HashSet<Material> PreserveBump2ndMaterials;
            internal readonly int? PreservedOriginalUVChannel;

            internal PreparedAtlasContext(
                AtlasContext atlasContext,
                HashSet<Material> preserveBump2ndMaterials,
                int? preservedOriginalUVChannel)
            {
                AtlasContext = atlasContext;
                PreserveBump2ndMaterials = preserveBump2ndMaterials;
                PreservedOriginalUVChannel = preservedOriginalUVChannel;
            }

            public void Dispose()
            {
                AtlasContext.Dispose();
            }
        }

        internal sealed class AtlasExecutionOptions
        {
            internal bool RequireLossless;
            internal bool ReportProgressInfo = true;
            internal Func<AtlasContext, IIslandRelocator>? IslandRelocatorFactory;
            internal Func<AtlasContext, Vector2Int, AtlasExecutionMetrics, bool>? AcceptAtlasSize;
            internal Action<AtlasExecutionTimingDiagnostic>? TimingDiagnostic;
        }

        internal readonly struct AtlasExecutionTimingDiagnostic
        {
            internal readonly long PreserveSetupMilliseconds;
            internal readonly long AtlasContextConstructionMilliseconds;
            internal readonly long IslandNormalizeMilliseconds;
            internal readonly long RelocationStageMilliseconds;
            internal readonly long ResolutionPlanMilliseconds;
            internal readonly long MeshGenerationMilliseconds;
            internal readonly long TextureGenerationMilliseconds;

            internal AtlasExecutionTimingDiagnostic(
                long preserveSetupMilliseconds,
                long atlasContextConstructionMilliseconds,
                long islandNormalizeMilliseconds,
                long relocationStageMilliseconds,
                long resolutionPlanMilliseconds,
                long meshGenerationMilliseconds,
                long textureGenerationMilliseconds)
            {
                PreserveSetupMilliseconds = preserveSetupMilliseconds;
                AtlasContextConstructionMilliseconds =
                    atlasContextConstructionMilliseconds;
                IslandNormalizeMilliseconds = islandNormalizeMilliseconds;
                RelocationStageMilliseconds = relocationStageMilliseconds;
                ResolutionPlanMilliseconds = resolutionPlanMilliseconds;
                MeshGenerationMilliseconds = meshGenerationMilliseconds;
                TextureGenerationMilliseconds = textureGenerationMilliseconds;
            }
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
