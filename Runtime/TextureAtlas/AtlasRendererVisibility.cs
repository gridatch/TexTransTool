#nullable enable
using System.Collections.Generic;
using UnityEngine;

namespace net.rs64.TexTransTool.TextureAtlas
{
    internal readonly struct AtlasRendererVisibilityState
    {
        internal readonly bool CanBecomeVisible;
        internal readonly bool CanBecomeHidden;

        internal bool CanToggleVisibility =>
            CanBecomeVisible && CanBecomeHidden;

        internal AtlasRendererVisibilityState(
            bool canBecomeVisible,
            bool canBecomeHidden)
        {
            CanBecomeVisible = canBecomeVisible;
            CanBecomeHidden = canBecomeHidden;
        }
    }

    internal interface IAtlasRendererVisibilityAnalyzer
        : IDomainCustomContextData
    {
        IReadOnlyDictionary<Renderer, AtlasRendererVisibilityState>
            Analyze(IEnumerable<Renderer> renderers);
    }
}
