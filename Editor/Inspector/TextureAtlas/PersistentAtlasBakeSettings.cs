#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using net.rs64.TexTransTool.TextureAtlas.IslandSizePriorityTuner;
using UnityEngine;

namespace net.rs64.TexTransTool.TextureAtlas.Editor
{
    /// <summary>
    /// Component-independent settings consumed by the manual persistent Atlas
    /// bake core. AtlasTexture snapshots its component state into this DTO before
    /// invoking the shared baker.
    /// </summary>
    internal sealed class PersistentAtlasBakeSettings
    {
        internal AtlasSetting AtlasSetting = new AtlasSetting();
        internal List<IIslandSizePriorityTuner?> IslandSizePriorityTuner = new();
        internal List<AtlasTexture.MaterialMergeGroup> MergeMaterialGroups = new();
        internal Material? AllMaterialMergeReference;
        internal AtlasTextureExperimentalFeature? ExperimentalOptions;

        internal static PersistentAtlasBakeSettings FromComponent(AtlasTexture atlasTexture)
        {
            return new PersistentAtlasBakeSettings
            {
                AtlasSetting = atlasTexture.AtlasSetting,
                IslandSizePriorityTuner = atlasTexture.IslandSizePriorityTuner,
                MergeMaterialGroups = atlasTexture.MergeMaterialGroups,
                AllMaterialMergeReference = atlasTexture.AllMaterialMergeReference,
                ExperimentalOptions = atlasTexture.GetComponent<AtlasTextureExperimentalFeature>(),
            };
        }
    }

    /// <summary>
    /// Standalone AutoAtlas settings owned by PartAtlasPrefabWindow.
    /// These are intentionally component-independent and never participate in NDMF.
    /// </summary>
    internal sealed class PartAutoAtlasSettings : ScriptableObject
    {
        public bool Enabled = true;

        [Range(0f, 0.05f)]
        public float IslandPadding = 0.01f;

        public bool EnableAdditionalAtlasMerging = true;
        public bool OptimizeConstantTextures = true;

        [Range(0, 4)]
        public int ConstantTextureColorTolerance = 2;

        public bool DisableVisuallyUnusedShaderFeatures = true;

        internal AutoAtlasTextureExecutionSettings ToExecutionSettings()
        {
            return new AutoAtlasTextureExecutionSettings
            {
                IslandPadding = IslandPadding,
                IncludeDisabledRenderer = true,
                EnableAdditionalAtlasMerging = EnableAdditionalAtlasMerging,
                ExcludeVisibilityAnimatedItemsFromTextureMerging = false,
                OptimizeConstantTextures = OptimizeConstantTextures,
                ConstantTextureColorTolerance = ConstantTextureColorTolerance,
                DisableVisuallyUnusedShaderFeatures =
                    DisableVisuallyUnusedShaderFeatures,
            };
        }
    }
}
