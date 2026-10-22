#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using net.rs64.TexTransTool.TextureAtlas;
using UnityEngine;

namespace net.rs64.TexTransTool.NDMF
{
    /// <summary>
    /// Supplies NDMF animation knowledge to the shared Atlas source optimizer.
    /// Only bindings that can invalidate the exact proof used by an
    /// optimization are considered. Ordinary unrelated animation is ignored.
    /// </summary>
    internal sealed class AtlasSourceOptimizationAnimationSafety
        : IAtlasSourceOptimizationAnimationSafety
    {
        private static readonly HashSet<string> MainUvTextureProperties =
            new(StringComparer.Ordinal)
            {
                "_MainTex",
                "_Main2ndBlendMask",
                "_Main3rdBlendMask",
                "_MatCapBlendMask",
                "_MatCap2ndBlendMask",
                "_EmissionBlendMask",
                "_Emission2ndBlendMask",
            };

        private readonly AnimationIndex _animationIndex;
        private readonly ObjectPathRemapper _pathRemapper;

        internal AtlasSourceOptimizationAnimationSafety(BuildContext context)
        {
            var services = context.Extension<AnimatorServicesContext>();
            _animationIndex = services.AnimationIndex;
            _pathRemapper = services.ObjectPathRemapper;
        }

        public bool HasUnsafeConstantTextureAnimation(
            IRendererTargeting targeting,
            Material material,
            string property,
            Texture source,
            IEnumerable<Renderer> renderers)
        {
            return IsProofPropertyAnimated(
                targeting,
                material,
                property,
                source,
                renderers,
                includeSamplingControls: true);
        }

        public bool HasUnsafeFeatureDisableAnimation(
            IRendererTargeting targeting,
            AtlasFeatureDisableDecision decision,
            IEnumerable<Renderer> renderers)
        {
            if (decision.SafetyProperties.Length == 0)
                return true;

            foreach (var property in decision.SafetyProperties)
            {
                Texture? texture = null;
                var isTexture =
                    decision.Material.HasShaderTextureProperty(property);
                if (isTexture)
                    texture = decision.Material.GetTexture(property);

                if (IsProofPropertyAnimated(
                        targeting,
                        decision.Material,
                        property,
                        texture,
                        renderers,
                        includeSamplingControls: isTexture))
                    return true;
            }

            return false;
        }

        private bool IsProofPropertyAnimated(
            IRendererTargeting targeting,
            Material material,
            string property,
            Texture? currentTexture,
            IEnumerable<Renderer> renderers,
            bool includeSamplingControls)
        {
            foreach (var renderer in renderers)
            {
                if (renderer == null
                    || !targeting.GetMaterials(renderer).Contains(material))
                    continue;

                _pathRemapper.GetVirtualPathForObject(renderer.transform);
                foreach (var path in _pathRemapper
                             .GetAllPathsForObject(renderer.transform)
                             .Distinct(StringComparer.Ordinal))
                {
                    foreach (var clip in
                             _animationIndex.GetClipsForObjectPath(path))
                    {
                        foreach (var binding in clip.GetFloatCurveBindings())
                        {
                            if (!TargetsRenderer(binding, renderer, path))
                                continue;
                            if (!AffectsProofProperty(
                                    binding.propertyName,
                                    property,
                                    includeSamplingControls))
                                continue;
                            if (clip.GetFloatCurve(binding) != null)
                                return true;
                        }

                        foreach (var binding in clip.GetObjectCurveBindings())
                        {
                            if (!TargetsRenderer(binding, renderer, path)
                                || binding.propertyName
                                    != "material." + property)
                                continue;

                            var curve = clip.GetObjectCurve(binding);
                            if (curve == null) continue;

                            // A texture reference curve is harmless only when
                            // every key keeps the exact texture used by the
                            // proof. Other object-valued properties are treated
                            // as changing the proof conservatively.
                            if (currentTexture == null
                                || curve.Any(key =>
                                    key.value != currentTexture))
                                return true;
                        }
                    }
                }
            }

            return false;
        }

        private static bool TargetsRenderer(
            EditorCurveBinding binding,
            Renderer renderer,
            string path)
        {
            return binding.path == path
                && binding.type != null
                && typeof(Renderer).IsAssignableFrom(binding.type)
                && binding.type.IsAssignableFrom(renderer.GetType());
        }

        private static bool AffectsProofProperty(
            string bindingProperty,
            string property,
            bool includeSamplingControls)
        {
            const string prefix = "material.";
            if (!bindingProperty.StartsWith(
                    prefix, StringComparison.Ordinal))
                return false;

            var name = bindingProperty.Substring(prefix.Length);
            if (Affects(name, property))
                return true;

            if (!includeSamplingControls)
                return false;

            if (Affects(name, property + "_ST")
                || Affects(name, property + "_ScrollRotate")
                || Affects(name, property + "_UVMode")
                || name.StartsWith(
                    property + ".offset.", StringComparison.Ordinal)
                || name.StartsWith(
                    property + ".scale.", StringComparison.Ordinal))
                return true;

            if (!MainUvTextureProperties.Contains(property))
                return false;

            return Affects(name, "_MainTex_ST")
                || Affects(name, "_MainTex_ScrollRotate")
                || Affects(name, "_ShiftBackfaceUV");
        }

        private static bool Affects(string name, string property)
        {
            return name == property
                || name.StartsWith(
                    property + ".", StringComparison.Ordinal);
        }
    }
}
