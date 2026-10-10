#nullable enable
using UnityEngine;

namespace net.rs64.TexTransTool.TextureAtlas
{
    /// <summary>
    /// Opts an avatar into automatic material atlas optimization.
    /// The first implementation milestone only reports candidate groups;
    /// it does not change materials, meshes, or textures.
    /// </summary>
    [AddComponentMenu("TexTransTool/" + MenuPath)]
    public sealed class AutoMaterialAtlas : TexTransMonoBaseGameObjectOwned
    {
        internal const string MenuPath = "TTT Auto Material Atlas";
    }
}
