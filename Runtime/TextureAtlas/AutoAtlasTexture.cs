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
        public bool ExcludeVisibilityAnimatedItemsFromTextureMerging = false;

        // Apply only to texture merging; standalone atlasing is unaffected.
        public List<Renderer> TextureMergingExcludedRenderers = new();
        public List<Material> TextureMergingExcludedMaterials = new();

        public List<Renderer> ExcludedRenderers = new();
        public List<Material> ExcludedMaterials = new();
    }
}
