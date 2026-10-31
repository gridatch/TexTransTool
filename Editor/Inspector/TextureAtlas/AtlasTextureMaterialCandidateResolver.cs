#nullable enable
using System.Collections.Generic;
using System.Linq;
using net.rs64.TexTransTool.Utils;
using UnityEngine;

namespace net.rs64.TexTransTool.TextureAtlas.Editor
{
    internal static class AtlasTextureMaterialCandidateResolver
    {
        internal readonly struct CandidateSet
        {
            internal readonly HashSet<Material> ActiveMaterials;
            internal readonly HashSet<Material> InactiveMaterials;

            internal CandidateSet(
                HashSet<Material> activeMaterials,
                HashSet<Material> inactiveMaterials)
            {
                ActiveMaterials = activeMaterials;
                InactiveMaterials = inactiveMaterials;
            }
        }

        internal static CandidateSet Resolve(GameObject domainRoot)
        {
            var renderers = domainRoot
                .GetComponentsInChildren<Renderer>(true)
                .Where(AtlasTexture.IsAtlasAllowedRenderer)
                .ToArray();
            var domain = new NotWorkDomain(renderers, null);

            var activeRenderers = renderers
                .Where(renderer => AtlasTexture.CheckRendererActive(
                    domain,
                    renderer,
                    includeDisabledRenderer: false))
                .ToArray();
            var inactiveRenderers = renderers
                .Where(renderer => !AtlasTexture.CheckRendererActive(
                    domain,
                    renderer,
                    includeDisabledRenderer: false))
                .ToArray();

            var activeMaterials = RendererUtility
                .GetMaterials(activeRenderers)
                .Where(material => material != null)
                .ToHashSet();
            var inactiveMaterials = RendererUtility
                .GetMaterials(inactiveRenderers)
                .Where(material => material != null)
                .ToHashSet();

            inactiveMaterials.ExceptWith(activeMaterials);
            return new CandidateSet(activeMaterials, inactiveMaterials);
        }
    }
}
