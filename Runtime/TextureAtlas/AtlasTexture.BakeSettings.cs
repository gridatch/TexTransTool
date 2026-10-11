#nullable enable
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

namespace net.rs64.TexTransTool.TextureAtlas
{
    public sealed partial class AtlasTexture
    {
        // WDT persistent-bake settings. These do not affect normal NDMF processing.
        public string BakeName = "";

        [FormerlySerializedAs("AtlasExcludedRenderers")]
        public List<Renderer?> BakeExcludedRenderers = new List<Renderer?>();
    }
}
