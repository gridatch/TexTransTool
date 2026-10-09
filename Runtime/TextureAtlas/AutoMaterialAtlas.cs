#nullable enable
using UnityEngine;

namespace net.rs64.TexTransTool.TextureAtlas
{
    /// <summary>
    /// Opts an avatar into automatic material atlas optimization.
    /// The first implementation milestone only reports candidate groups;
    /// it does not change materials, meshes, or textures.
    /// </summary>
    [AddComponentMenu("TexTransTool/TTT Auto Material Atlas")]
    public sealed class AutoMaterialAtlas : TexTransMonoBaseGameObjectOwned
    {
    }
}
