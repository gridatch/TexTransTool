#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using net.rs64.TexTransTool.TextureAtlas.IslandSizePriorityTuner;
using UnityEngine;

namespace net.rs64.TexTransTool.TextureAtlas.Editor
{
    /// <summary>
    /// Component-independent settings consumed by the persistent Atlas bake core.
    /// The normal AtlasTexture component and standalone Prefab workflow both snapshot
    /// their own state into this DTO before invoking the shared baker.
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
    /// Serialized backing model owned by PartAtlasPrefabWindow.
    /// It deliberately is not an AtlasTexture component and never participates in NDMF.
    /// </summary>
    internal sealed class PartAtlasPrefabSettings : ScriptableObject
    {
        public List<Material?> AtlasTargetMaterials = new();

        // The standalone Prefab workflow intentionally supports only the hierarchy-independent
        // SetFromMaterial variant. The list being empty has the same meaning as the regular
        // AtlasTexture component having no IslandSizePriorityTuner entries.
        public List<SetFromMaterial> MaterialSizePriorityTuners = new();

        public List<AtlasTexture.MaterialMergeGroup> MergeMaterialGroups = new();
        public Material? AllMaterialMergeReference;
        public AtlasSetting AtlasSetting = new AtlasSetting();

        internal PersistentAtlasBakeSettings ToBakeSettings()
        {
            return new PersistentAtlasBakeSettings
            {
                AtlasSetting = AtlasSetting,
                IslandSizePriorityTuner = MaterialSizePriorityTuners
                    .Where(tuner => tuner != null)
                    .Cast<IIslandSizePriorityTuner?>()
                    .ToList(),
                MergeMaterialGroups = MergeMaterialGroups,
                AllMaterialMergeReference = AllMaterialMergeReference,
                ExperimentalOptions = null,
            };
        }
    }
}
