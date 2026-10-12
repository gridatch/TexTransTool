#nullable enable
using System.Collections.Generic;
using UnityEngine;

namespace net.rs64.TexTransTool.TextureAtlas
{
    /// <summary>
    /// Automatically repacks only the UV islands still used by the avatar.
    /// Add using the same Hierarchy TexTransTool menu as TTT AtlasTexture.
    /// </summary>
    [AddComponentMenu(TexTransBehavior.TTTName + "/" + MenuPath)]
    public sealed class AutoAtlasTexture : TexTransMonoBaseGameObjectOwned, ITexTransToolStableComponent
    {
        internal const string ComponentName = "TTT AutoAtlasTexture";
        internal const string MenuPath = ComponentName;
        public int StabilizeSaveDataVersion => TTTDataVersion_0_10_X;

        [Range(0f, 0.05f)]
        public float IslandPadding = 0.01f;

        // Optional second-stage optimization. Existing AutoAtlasTexture behavior is
        // unchanged while this is false.
        public bool EnableAdditionalAtlasMerging = false;

        // Used only to prune hosts from the merge search. Low-free-space groups may
        // still be merged into another host.
        [Range(0f, 1f)]
        public float MinimumMergeHostFreeAreaRatio = 0.25f;

        // Additional savings required relative to the result of optimizing each
        // atomic texture-connected group independently.
        [Range(0f, 1f)]
        public float MinimumAdditionalMergeSavingsRate = 0.10f;

        public List<Renderer> ExcludedRenderers = new();
        public List<Material> ExcludedMaterials = new();
    }
}
