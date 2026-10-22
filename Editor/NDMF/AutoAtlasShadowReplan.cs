#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using net.rs64.TexTransTool.TextureAtlas;
using UnityEngine;
using Color = UnityEngine.Color;

namespace net.rs64.TexTransTool.NDMF
{
    // This models the fragment equations in lilToon. A static mip0 result is
    // NOT proof of animation-, mip-, shader-variant-, or renderer-equivalence.
    internal static class AutoAtlasFeatureDisableRules
    {
        internal sealed class Rule
        {
            internal readonly string Name;
            internal readonly string SwitchProperty;
            internal readonly string Condition;
            internal readonly string[] TextureProperties;

            internal Rule(string name, string switchProperty,
                string condition, params string[] textures)
            {
                Name = name;
                SwitchProperty = switchProperty;
                Condition = condition;
                TextureProperties = textures;
            }
        }

        internal static bool TryClassify(Material material, string property,
            Color coveredColor, out Rule? rule)
        {
            rule = null;
            if (material == null || material.shader == null
                || material.shader.name.IndexOf("lilToon",
                    StringComparison.OrdinalIgnoreCase) < 0
                || material.shader.name.IndexOf("Lite",
                    StringComparison.OrdinalIgnoreCase) >= 0
                || coveredColor.r != 0f || coveredColor.g != 0f
                || coveredColor.b != 0f)
                return false;

            Rule candidate;
            switch (property)
            {
                case "_MatCapBlendMask":
                    candidate = new Rule("MatCap1", "_UseMatCap",
                        "zero-RGB-multiplies-final-blend-factor",
                        "_MatCapTex", "_MatCapBlendMask", "_MatCapBumpMap");
                    break;
                case "_MatCap2ndBlendMask":
                    candidate = new Rule("MatCap2", "_UseMatCap2nd",
                        "zero-RGB-multiplies-final-blend-factor",
                        "_MatCap2ndTex", "_MatCap2ndBlendMask",
                        "_MatCap2ndBumpMap");
                    break;
                case "_Main2ndBlendMask":
                    // lilToon can overwrite opacity when AlphaMode != 0.
                    // Additionally, lil_pass_forward_normal.hlsl adds
                    // _Main2ndDissolveColor * main2ndDissolveAlpha even if
                    // the layer's color alpha becomes zero; disabling the
                    // feature skips that emission. Reject active/unknown
                    // dissolve modes until that path is proven neutral.
                    if (!material.HasProperty("_Main2ndTexAlphaMode")
                        || material.GetFloat("_Main2ndTexAlphaMode") != 0f
                        || !material.HasProperty("_Main2ndDissolveParams")
                        || material.GetVector("_Main2ndDissolveParams").x != 0f)
                        return false;
                    candidate = new Rule("Main2nd", "_UseMain2ndTex",
                        "zero-R-alpha-and-alpha-mode-0-dissolve-off",
                        "_Main2ndTex", "_Main2ndBlendMask",
                        "_Main2ndDissolveMask", "_Main2ndDissolveNoiseMask");
                    break;
                case "_EmissionBlendMask":
                    if (!ZeroEmissionBlend(material, "_EmissionBlendMode"))
                        return false;
                    candidate = new Rule("Emission1", "_UseEmission",
                        "zero-RGB-add-or-screen-blend",
                        "_EmissionMap", "_EmissionBlendMask", "_EmissionGradTex");
                    break;
                case "_Emission2ndBlendMask":
                    if (!ZeroEmissionBlend(material, "_Emission2ndBlendMode"))
                        return false;
                    candidate = new Rule("Emission2", "_UseEmission2nd",
                        "zero-RGB-add-or-screen-blend",
                        "_Emission2ndMap", "_Emission2ndBlendMask",
                        "_Emission2ndGradTex");
                    break;
                default:
                    // Notably _OutlineWidthMask has side effects in vertex
                    // paths and _Bump2ndMap neutral sampling is unproven.
                    return false;
            }
            if (!material.HasProperty(candidate.SwitchProperty)
                || material.GetFloat(candidate.SwitchProperty) == 0f)
                return false;
            rule = candidate;
            return true;
        }

        internal static string? ExplainRejectedZeroMask(
            Material material, string property, Color color)
        {
            if (color.r != 0f || color.g != 0f || color.b != 0f)
                return null;
            if (property == "_OutlineWidthMask")
                return "outline-width-mask-affects-vertex-and-delete-mesh-path";
            if (property == "_Main2ndBlendMask")
                return !material.HasProperty("_Main2ndTexAlphaMode")
                    ? "main2nd-alpha-mode-unavailable"
                    : material.GetFloat("_Main2ndTexAlphaMode") != 0f
                        ? "main2nd-alpha-mode-not-zero"
                        : !material.HasProperty("_Main2ndDissolveParams")
                            ? "main2nd-dissolve-params-unavailable"
                            : material.GetVector("_Main2ndDissolveParams").x != 0f
                                ? "main2nd-dissolve-emission-may-remain"
                                : "main2nd-disabled-or-unsupported-shader";
            if (property == "_MatCapBlendMask"
                || property == "_MatCap2ndBlendMask"
                || property == "_EmissionBlendMask"
                || property == "_Emission2ndBlendMask")
                return "feature-switch-or-blend-mode-not-proven";
            return null;
        }

        private static bool ZeroEmissionBlend(Material material, string name)
        {
            if (!material.HasProperty(name)) return false;
            var value = Mathf.RoundToInt(material.GetFloat(name));
            return value == 1 || value == 2;
        }

        // Only call on AtlasTexture-generated clones, never on source
        // materials in the NDMF domain. The experimental branch automatically
        // applies classified removals; classification is still mip0-only
        // and does not certify animation or shader visual equivalence.
        internal static void DisableOnGeneratedMaterial(Material generated, Rule rule)
        {
            if (!generated.HasProperty(rule.SwitchProperty))
                throw new InvalidOperationException(
                    "Missing shader switch: " + rule.SwitchProperty);

            generated.SetFloat(rule.SwitchProperty, 0f);
            foreach (var property in rule.TextureProperties)
                if (generated.HasShaderTextureProperty(property))
                    generated.SetTexture(property, null);
        }

        internal static Material CreateDisabledClone(Material original, Rule rule)
        {
            var clone = UnityEngine.Object.Instantiate(original);
            clone.name = original.name + "(shader-disable-diagnostic)";
            DisableOnGeneratedMaterial(clone, rule);
            return clone;
        }
    }

    /// <summary>
    /// Build the immutable effective texture-use projection consumed by the
    /// one production atlas/merge plan. Source materials remain unchanged.
    /// </summary>
    internal static class AutoAtlasShadowReplan
    {

        internal static IReadOnlyDictionary<Material, IReadOnlyDictionary<string, Texture>>
            ProjectFeatureUses(
                IReadOnlyDictionary<Material, IReadOnlyDictionary<string, Texture>> source,
                Material material, AutoAtlasFeatureDisableRules.Rule rule)
        {
            var result = source.ToDictionary(item => item.Key, item => item.Value);
            if (!result.TryGetValue(material, out var original))
                return result;
            var copy = original.ToDictionary(item => item.Key, item => item.Value);
            foreach (var property in rule.TextureProperties)
                copy.Remove(property);
            result[material] = copy;
            return result;
        }

        internal static IReadOnlyDictionary<Material, IReadOnlyDictionary<string, Texture>>
            ProjectFeatureSetUses(
                IReadOnlyDictionary<Material, IReadOnlyDictionary<string, Texture>> source,
                IReadOnlyList<(Material material, AutoAtlasFeatureDisableRules.Rule rule)> disables)
        {
            IReadOnlyDictionary<Material, IReadOnlyDictionary<string, Texture>> current = source;
            foreach (var item in disables)
                current = ProjectFeatureUses(current, item.material, item.rule);
            return current;
        }

        internal static IReadOnlyDictionary<Material, IReadOnlyDictionary<string, Texture>>
            WithoutUse(
                IReadOnlyDictionary<Material, IReadOnlyDictionary<string, Texture>> source,
                Material excludedMaterial, string excludedProperty)
        {
            var projected = new Dictionary<Material, IReadOnlyDictionary<string, Texture>>();
            foreach (var entry in source)
            {
                if (entry.Key != excludedMaterial)
                {
                    projected.Add(entry.Key, entry.Value);
                    continue;
                }
                var copy = entry.Value.ToDictionary(pair => pair.Key, pair => pair.Value);
                copy.Remove(excludedProperty);
                projected.Add(entry.Key, copy);
            }
            return projected;
        }

    }
}
