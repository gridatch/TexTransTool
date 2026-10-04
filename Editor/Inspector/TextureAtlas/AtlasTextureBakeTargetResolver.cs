#nullable enable
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace net.rs64.TexTransTool.TextureAtlas.Editor
{
    internal static class AtlasTextureBakeTargetResolver
    {
        internal static Renderer[] GetCandidateRenderers(
            AtlasTexture atlasTexture,
            IDomain domain)
        {
            var allowedRenderers = AtlasTexture.GetAtlasAllowedRenderers(
                domain,
                domain.EnumerateRenderer(),
                atlasTexture.AtlasSetting.IncludeDisabledRenderer
            );

            var targetMaterials = atlasTexture.GetTargetMaterials(domain, allowedRenderers).ToHashSet();
            var candidateRenderers = AtlasTexture.FilterTargetRenderers(
                domain,
                allowedRenderers,
                targetMaterials
            );

            return AtlasTexture.FilterExistUVChannel(
                domain,
                candidateRenderers,
                atlasTexture.AtlasSetting.AtlasTargetUVChannel
            );
        }

        internal static (
            HashSet<Material> targetMaterials,
            Renderer[] targetRenderers
        ) ResolveBakeTargets(
            AtlasTexture atlasTexture,
            IDomain domain)
        {
            var candidateRenderers = GetCandidateRenderers(atlasTexture, domain);
            var excludedRenderers = atlasTexture.BakeExcludedRenderers?
                .Where(renderer => renderer != null)
                .Cast<Renderer>()
                .ToArray()
                ?? System.Array.Empty<Renderer>();

            var targetRenderers = candidateRenderers
                .Where(renderer =>
                    excludedRenderers.Any(excluded => domain.OriginEqual(renderer, excluded)) is false
                )
                .ToArray();

            var targetMaterials = atlasTexture
                .GetTargetMaterials(domain, targetRenderers.ToList())
                .ToHashSet();

            return (targetMaterials, targetRenderers);
        }
    }
}
