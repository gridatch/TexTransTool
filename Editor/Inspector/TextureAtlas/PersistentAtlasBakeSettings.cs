#nullable enable
using System;
using System.Collections.Generic;
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

    [Serializable]
    internal sealed class PartAtlasMaterialPriority
    {
        public Material? Material;
        [Range(0f, 1f)] public float Priority = 1f;
    }

    /// <summary>
    /// Serialized backing model owned by PartAtlasPrefabWindow.
    /// It deliberately is not an AtlasTexture component and never participates in NDMF.
    /// </summary>
    internal sealed class PartAtlasPrefabSettings : ScriptableObject
    {
        public List<Material?> AtlasTargetMaterials = new();
        public List<PartAtlasMaterialPriority> MaterialSizePriorities = new();

        public List<AtlasTexture.MaterialMergeGroup> MergeMaterialGroups = new();
        public Material? AllMaterialMergeReference;
        public AtlasSetting AtlasSetting = new AtlasSetting();

        internal PersistentAtlasBakeSettings ToBakeSettings()
        {
            var priorityTuners = new List<IIslandSizePriorityTuner?>();

            foreach (var priority in MaterialSizePriorities)
            {
                if (priority == null
                    || priority.Material == null
                    || Mathf.Approximately(priority.Priority, 1f))
                {
                    continue;
                }

                priorityTuners.Add(new SetFromMaterial
                {
                    PriorityValue = Mathf.Clamp01(priority.Priority),
                    Materials = new List<Material> { priority.Material },
                });
            }

            if (priorityTuners.Count == 0)
                AtlasSetting.ForceSizePriority = false;

            return new PersistentAtlasBakeSettings
            {
                AtlasSetting = AtlasSetting,
                IslandSizePriorityTuner = priorityTuners,
                MergeMaterialGroups = MergeMaterialGroups,
                AllMaterialMergeReference = AllMaterialMergeReference,
                ExperimentalOptions = null,
            };
        }
    }
}
