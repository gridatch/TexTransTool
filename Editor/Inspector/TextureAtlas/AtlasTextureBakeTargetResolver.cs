#nullable enable
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace net.rs64.TexTransTool.TextureAtlas.Editor
{
    internal static class AtlasTextureBakeTargetResolver
    {
        internal static Renderer[] GetCandidateRenderers(
            AtlasTexture atlasTexture,
            IDomain domain)
        {
            var allowedRenderers = domain
                .EnumerateRenderer()
                .Where(AtlasTexture.IsAtlasAllowedRenderer)
                .ToList();
            var targetMaterials = atlasTexture
                .GetTargetMaterials(domain, allowedRenderers)
                .ToHashSet();
            var targetRenderers = AtlasTexture.FilterTargetRenderers(
                domain, allowedRenderers, targetMaterials);
            return AtlasTexture.FilterExistUVChannel(
                domain,
                targetRenderers,
                atlasTexture.AtlasSetting.AtlasTargetUVChannel);
        }

        internal static bool IsCurrentlyActive(
            IRendererTargeting domain,
            Renderer renderer)
        {
            return AtlasTexture.CheckRendererActive(
                domain,
                renderer,
                includeDisabledRenderer: false);
        }

        internal static bool IsRendererIncluded(
            AtlasTexture atlasTexture,
            IRendererTargeting domain,
            Renderer renderer)
        {
            if (ContainsRenderer(
                    domain,
                    atlasTexture.BakeExcludedRenderers,
                    renderer))
                return false;

            if (IsCurrentlyActive(domain, renderer))
                return true;

            return ContainsRenderer(
                domain,
                atlasTexture.BakeIncludedInactiveRenderers,
                renderer);
        }

        internal static bool EnsureSelectionMigrated(
            AtlasTexture atlasTexture,
            IDomain domain)
        {
            if (atlasTexture.BakeRendererSelectionVersion
                >= AtlasTexture.CurrentBakeRendererSelectionVersion)
                return false;

            // Version 0 derived bake candidates from ResolveAtlasTargets.
            // Re-run that exact legacy rule in the editor domain before
            // switching to activity-independent bake candidates, then preserve
            // only the inactive renderers that were actually included there.
            var legacyCandidates =
                atlasTexture.ResolveAtlasTargets(domain).targetRenderers;

            foreach (var renderer in legacyCandidates)
            {
                if (renderer == null
                    || IsCurrentlyActive(domain, renderer)
                    || ContainsRenderer(
                        domain,
                        atlasTexture.BakeExcludedRenderers,
                        renderer)
                    || ContainsRenderer(
                        domain,
                        atlasTexture.BakeIncludedInactiveRenderers,
                        renderer))
                {
                    continue;
                }

                atlasTexture.BakeIncludedInactiveRenderers.Add(renderer);
            }

            atlasTexture.BakeRendererSelectionVersion =
                AtlasTexture.CurrentBakeRendererSelectionVersion;
            EditorUtility.SetDirty(atlasTexture);
            return true;
        }

        internal static (
            HashSet<Material> targetMaterials,
            Renderer[] targetRenderers
        ) ResolveBakeTargets(
            AtlasTexture atlasTexture,
            IDomain domain)
        {
            EnsureSelectionMigrated(atlasTexture, domain);

            var candidateRenderers = GetCandidateRenderers(
                atlasTexture, domain);
            var targetRenderers = candidateRenderers
                .Where(renderer =>
                    IsRendererIncluded(atlasTexture, domain, renderer))
                .ToArray();

            var targetMaterials = atlasTexture
                .GetTargetMaterials(domain, targetRenderers.ToList())
                .ToHashSet();

            return (targetMaterials, targetRenderers);
        }

        private static bool ContainsRenderer(
            IRendererTargeting domain,
            IEnumerable<Renderer?> renderers,
            Renderer renderer)
        {
            return renderers.Any(candidate =>
                candidate != null
                && domain.OriginEqual(candidate, renderer));
        }
    }
}
