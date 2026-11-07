#nullable enable
using System.Collections.Generic;
using UnityEngine;

namespace net.rs64.TexTransTool.TextureAtlas.Editor
{
    /// <summary>
    /// Component-independent AutoAtlas settings used by both NDMF builds and
    /// standalone editor workflows such as Part Prefab extraction.
    /// </summary>
    internal sealed class AutoAtlasTextureExecutionSettings
    {
        internal float IslandPadding = 0.01f;
        internal bool IncludeDisabledRenderer;
        internal bool EnableAdditionalAtlasMerging = true;
        internal bool ExcludeVisibilityAnimatedItemsFromTextureMerging;
        internal bool OptimizeConstantTextures = true;
        internal int ConstantTextureColorTolerance = 2;
        internal bool DisableVisuallyUnusedShaderFeatures = true;

        internal IReadOnlyList<Renderer> TextureMergingExcludedRenderers =
            new List<Renderer>();
        internal IReadOnlyList<Material> TextureMergingExcludedMaterials =
            new List<Material>();
        internal IReadOnlyList<Renderer> ExcludedRenderers =
            new List<Renderer>();
        internal IReadOnlyList<Material> ExcludedMaterials =
            new List<Material>();

        internal static AutoAtlasTextureExecutionSettings FromComponent(
            AutoAtlasTexture configuration)
        {
            return new AutoAtlasTextureExecutionSettings
            {
                IslandPadding = configuration.IslandPadding,
                IncludeDisabledRenderer = false,
                EnableAdditionalAtlasMerging =
                    configuration.EnableAdditionalAtlasMerging,
                ExcludeVisibilityAnimatedItemsFromTextureMerging =
                    configuration.ExcludeVisibilityAnimatedItemsFromTextureMerging,
                OptimizeConstantTextures =
                    configuration.OptimizeConstantTextures,
                ConstantTextureColorTolerance =
                    configuration.ConstantTextureColorTolerance,
                DisableVisuallyUnusedShaderFeatures =
                    configuration.DisableVisuallyUnusedShaderFeatures,
                TextureMergingExcludedRenderers =
                    new List<Renderer>(
                        configuration.TextureMergingExcludedRenderers),
                TextureMergingExcludedMaterials =
                    new List<Material>(
                        configuration.TextureMergingExcludedMaterials),
                ExcludedRenderers =
                    new List<Renderer>(configuration.ExcludedRenderers),
                ExcludedMaterials =
                    new List<Material>(configuration.ExcludedMaterials),
            };
        }
    }

    internal readonly struct AutoAtlasTextureExecutionResult
    {
        internal readonly bool HasChanges;
        internal readonly int AppliedGroupCount;
        internal readonly long ProcessingMilliseconds;

        internal AutoAtlasTextureExecutionResult(
            bool hasChanges,
            int appliedGroupCount,
            long processingMilliseconds)
        {
            HasChanges = hasChanges;
            AppliedGroupCount = appliedGroupCount;
            ProcessingMilliseconds = processingMilliseconds;
        }
    }
}
