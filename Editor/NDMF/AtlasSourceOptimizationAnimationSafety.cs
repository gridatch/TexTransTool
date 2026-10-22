#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using net.rs64.TexTransTool.TextureAtlas;
using net.rs64.TexTransTool.Utils;
using UnityEditor;
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
                "_BaseMap",
                "_BaseColorMap",
                "_MainColorAdjustMask",
                "_Main2ndBlendMask",
                "_Main3rdBlendMask",
                "_AlphaMask",
                "_BumpMap",
                "_Bump2ndScaleMask",
                "_AnisotropyTangentMap",
                "_AnisotropyScaleMask",
                "_AnisotropyShiftNoiseMask",
                "_BacklightColorTex",
                "_ShadowStrengthMask",
                "_ShadowBorderMask",
                "_ShadowBlurMask",
                "_ShadowColorTex",
                "_Shadow2ndColorTex",
                "_Shadow3rdColorTex",
                "_RimShadeMask",
                "_SmoothnessTex",
                "_MetallicGlossMap",
                "_ReflectionColorTex",
                "_MatCapBlendMask",
                "_MatCapBumpMap",
                "_MatCap2ndBlendMask",
                "_MatCap2ndBumpMap",
                "_RimColorTex",
                "_GlitterColorTex",
                "_AudioLinkMask",
                "_OutlineTex",
                "_OutlineWidthMask",
                "_OutlineVectorTex",
                "_FurMask",
                "_FurLengthMask",
                "_FurVectorTex",
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
                                    includeSamplingControls,
                                    material))
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

        internal static bool AffectsProofProperty(
            string bindingProperty,
            string property,
            bool includeSamplingControls)
        {
            return AffectsProofProperty(
                bindingProperty,
                property,
                includeSamplingControls,
                null);
        }

        private static bool AffectsProofProperty(
            string bindingProperty,
            string property,
            bool includeSamplingControls,
            Material? material)
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

            if (property == "_Main2ndTex"
                || property == "_Main3rdTex")
            {
                if (Affects(name, property + "Angle")
                    || Affects(name, property + "IsDecal")
                    || Affects(name, property + "IsLeftOnly")
                    || Affects(name, property + "IsRightOnly")
                    || Affects(name, property + "ShouldCopy")
                    || Affects(name, property + "ShouldFlipMirror")
                    || Affects(name, property + "ShouldFlipCopy")
                    || Affects(name, property + "DecalAnimation")
                    || Affects(name, property + "DecalSubParam"))
                    return true;
            }

            if (property == "_EmissionMap"
                && Affects(name, "_EmissionParallaxDepth"))
                return true;
            if (property == "_Emission2ndMap"
                && Affects(name, "_Emission2ndParallaxDepth"))
                return true;

            if (property == "_MainTex")
                return true;

            if (material != null)
            {
                var shaderName = material.shader != null
                    ? material.shader.name
                    : string.Empty;
                if (!shaderName.Contains(
                        "lilToon", StringComparison.Ordinal))
                    return false;

                if (property == "_GlitterColorTex")
                    return !material.HasProperty("_GlitterColorTex_UVMode")
                        || Mathf.RoundToInt(
                            material.GetFloat("_GlitterColorTex_UVMode")) == 0;
                if (property == "_OutlineVectorTex")
                    return !material.HasProperty("_OutlineVectorUVMode")
                        || Mathf.RoundToInt(
                            material.GetFloat("_OutlineVectorUVMode")) == 0;
                if (property == "_AudioLinkMask")
                    return !material.HasProperty("_AudioLinkMask_UVMode")
                        || Mathf.RoundToInt(
                            material.GetFloat("_AudioLinkMask_UVMode")) == 0;
            }

            if (property is "_EmissionBlendMask" or "_Emission2ndBlendMask")
            {
                if (Affects(name, "_EmissionBlendMask_ScrollRotate")
                    || Affects(name, "_Emission2ndBlendMask_ScrollRotate"))
                    return true;
            }

            if (property == "_OutlineVectorTex"
                && Affects(name, "_OutlineVectorUVMode"))
                return true;

            if (property is "_ShadowColorTex"
                or "_Shadow2ndColorTex"
                or "_Shadow3rdColorTex")
            {
                if (Affects(name, "_ShadowColorType"))
                    return true;
            }

            if (!UsesMainUvControls(material, property))
                return false;

            return Affects(name, "_MainTex_ST")
                || Affects(name, "_MainTex_ScrollRotate")
                || Affects(name, "_ShiftBackfaceUV");
        }

        private static bool UsesMainUvControls(
            Material? material,
            string property)
        {
            if (property is "_EmissionBlendMask" or "_Emission2ndBlendMask")
            {
                // lilToon switches both emission masks to independent UV0 when
                // either mask has UV animation configured.
                if (material == null)
                    return true;

                var first = material.HasProperty(
                        "_EmissionBlendMask_ScrollRotate")
                    ? material.GetVector(
                        "_EmissionBlendMask_ScrollRotate")
                    : Vector4.zero;
                var second = material.HasProperty(
                        "_Emission2ndBlendMask_ScrollRotate")
                    ? material.GetVector(
                        "_Emission2ndBlendMask_ScrollRotate")
                    : Vector4.zero;
                return first == Vector4.zero
                    && second == Vector4.zero;
            }

            return MainUvTextureProperties.Contains(property);
        }

        private static bool Affects(string name, string property)
        {
            return name == property
                || name.StartsWith(
                    property + ".", StringComparison.Ordinal);
        }
    }
}
