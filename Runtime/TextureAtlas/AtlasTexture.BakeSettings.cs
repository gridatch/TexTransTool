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

        // Version 0 used only BakeExcludedRenderers and inherited the ordinary
        // Atlas renderer eligibility. Version 1 keeps that active-renderer
        // behavior while storing explicit opt-in for inactive bake targets.
        internal const int CurrentBakeRendererSelectionVersion = 1;

        [SerializeField, HideInInspector]
        internal List<Renderer?> BakeIncludedInactiveRenderers =
            new List<Renderer?>();

        [SerializeField, HideInInspector]
        internal int BakeRendererSelectionVersion;
    }
}
