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
    public sealed class AutoAtlasTexture : TexTransMonoBaseGameObjectOwned
    {
        public const string ComponentName = "TTT AutoAtlasTexture";
        public const string MenuPath = ComponentName;

        [Range(0f, 0.05f)]
        public float IslandPadding = 0.01f;

        public List<Renderer> ExcludedRenderers = new();
        public List<Material> ExcludedMaterials = new();
    }
}
