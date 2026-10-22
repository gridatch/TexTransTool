#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using net.rs64.TexTransCore;
using net.rs64.TexTransTool.TextureAtlas;
using net.rs64.TexTransTool.TextureAtlas.FineTuning;
using net.rs64.TexTransTool.Utils;
using UnityEngine;
using Color = UnityEngine.Color;

namespace net.rs64.TexTransTool.NDMF
{
    /// <summary>
    /// TEMPORARY diagnostic. Run the existing atlas + FineTuning pipeline on
    /// independent, read-only usage dictionaries. No domain replacements,
    /// generated assets registrations, or changes to original materials.
    /// This is an assumed texture unbinding, NOT permission to disable lilToon.
    /// </summary>
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
                    if (!material.HasProperty("_Main2ndTexAlphaMode")
                        || material.GetFloat("_Main2ndTexAlphaMode") != 0f)
                        return false;
                    candidate = new Rule("Main2nd", "_UseMain2ndTex",
                        "zero-R-alpha-and-alpha-mode-0",
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

        internal static Material CreateDisabledClone(Material original, Rule rule)
        {
            var clone = UnityEngine.Object.Instantiate(original);
            clone.name = original.name + "(shader-disable-diagnostic)";
            clone.SetFloat(rule.SwitchProperty, 0f);
            foreach (var property in rule.TextureProperties)
                if (clone.HasShaderTextureProperty(property))
                    clone.SetTexture(property, null);
            return clone;
        }
    }

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

        // Compare a SET of independent zero-mask hypotheses, since removing
        // just one edge can miss improvements enabled by multiple removals.
        // The domain stays unchanged, and all shaders are shadow-cloned only.
        internal static string CompareMergePlans(
            IDomain domain,
            AutoAtlasTexture configuration,
            AtlasSetting settings,
            IReadOnlyList<AutoAtlasTextureMergePlanner.PreparedGroup> originalGroups,
            IReadOnlyDictionary<Material, IReadOnlyDictionary<string, Texture>> originalUses,
            AutoAtlasTextureMergePlanner.PlanResult originalPlan,
            IReadOnlyList<(Material material, AutoAtlasFeatureDisableRules.Rule rule)> candidates)
        {
            if (candidates.Count == 0)
                return "status=not-run:no-feature-candidates";
            var unique = candidates.Distinct().ToArray();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var clones = new List<Material>();
            try
            {
                foreach (var item in unique)
                {
                    if (!originalUses.ContainsKey(item.material))
                        return "status=unknown:missing-material:"
                            + item.material.name;
                    var clone = AutoAtlasFeatureDisableRules.CreateDisabledClone(
                        item.material, item.rule);
                    clones.Add(clone);
                    if (clone.GetFloat(item.rule.SwitchProperty) != 0f
                        || item.rule.TextureProperties.Any(name =>
                            clone.HasShaderTextureProperty(name)
                            && clone.GetTexture(name) != null))
                        return "status=unknown:clone-toggle-failed:"
                            + item.material.name;
                }

                var uses = ProjectFeatureSetUses(originalUses, unique);
                var materials = originalGroups
                    .SelectMany(group => group.Materials).ToHashSet();
                var connected = AutoAtlasTextureProcessor.MakeTextureConnectedGroups(
                    materials, uses);
                var beforeConnected =
                    AutoAtlasTextureProcessor.MakeTextureConnectedGroups(
                        materials, originalUses);
                if (beforeConnected.Count != originalGroups.Count
                    || beforeConnected.Any(component =>
                        !originalGroups.Any(baseline =>
                            baseline.Materials.SetEquals(component))))
                {
                    return "feature=" + rule.Name
                        + ", status=unknown:baseline-connectivity-mismatch"
                        + ", preparedGroups=" + originalGroups.Count
                        + ", originalConnected=" + beforeConnected.Count;
                }

                // Removing texture-use edges cannot merge originally
                // disconnected components. Only the candidate material may
                // disappear entirely when no properties remain.
                if (connected.Count < beforeConnected.Count - 1)
                {
                    return "feature=" + rule.Name
                        + ", status=unknown:projected-connectivity-invariant"
                        + ", originalConnected=" + beforeConnected.Count
                        + ", projectedConnected=" + connected.Count;
                }
                var allReferenced = domain.GetAllMaterials();
                var alternatives = new List<AutoAtlasTextureMergePlanner.PreparedGroup>();
                foreach (var groupMaterials in connected)
                {
                    var renderers = AutoAtlasTextureProcessor.ResolveAutoAtlasTargetRenderers(
                        domain, configuration, groupMaterials);
                    if (renderers.Length == 0)
                        return "feature=" + rule.Name
                            + ", status=unknown:renderer-disappeared-in-projection"
                            + ", materialCount=" + groupMaterials.Count;
                    var sources = groupMaterials.SelectMany(mat => uses[mat].Values)
                        .Where(texture => texture != null).ToHashSet();
                    if (sources.Count == 0) continue;

                    var retained = new HashSet<Texture>();
                    foreach (var mat in allReferenced)
                    {
                        var belongs = groupMaterials.Contains(mat);
                        uses.TryGetValue(mat, out var usesForMaterial);
                        foreach (var pair in mat.GetTextureReferences())
                        {
                            if (!sources.Contains(pair.Value)) continue;
                            if (unique.Any(item =>
                                item.material == mat
                                && item.rule.TextureProperties.Contains(
                                    pair.Key, StringComparer.Ordinal)))
                                continue;
                            if (!belongs || usesForMaterial == null
                                || !usesForMaterial.TryGetValue(pair.Key, out var mapped)
                                || mapped != pair.Value)
                                retained.Add(pair.Value);
                        }
                    }
                    alternatives.Add(new AutoAtlasTextureMergePlanner.PreparedGroup
                    {
                        Id = alternatives.Count,
                        Materials = groupMaterials,
                        Renderers = renderers,
                        SourceTextures = sources,
                        RetainedSourceTextures = retained,
                        OriginalPixels = sources.Sum(texture =>
                            (long)texture.width * texture.height),
                        ExcludedFromTextureMerging = originalGroups
                            .Where(original => original.Materials.Overlaps(groupMaterials))
                            .Any(original => original.ExcludedFromTextureMerging),
                    });
                }
                var missing = connected.Count - alternatives.Count;
                if (missing != 0)
                    return "feature=" + rule.Name
                        + ", status=unknown:projected-groups-missing"
                        + ", planned=" + alternatives.Count
                        + ", connected=" + connected.Count;

                var projectedPlan = AutoAtlasTextureMergePlanner.Plan(
                    domain, configuration, settings, uses, alternatives,
                    useProjectedSources: true);
                long before = originalPlan.Selections.Sum(x => x.PredictedSavingBytes);
                long after = projectedPlan.Selections.Sum(x => x.PredictedSavingBytes);
                var originalEligible = originalGroups
                    .Where(g => !g.ExcludedFromTextureMerging).ToArray();
                var projectedEligible = alternatives
                    .Where(g => !g.ExcludedFromTextureMerging).ToArray();
                var originalScope = originalEligible
                    .SelectMany(g => g.Materials).ToHashSet();
                var projectedScope = projectedEligible
                    .SelectMany(g => g.Materials).ToHashSet();
                bool sameEligibleScope = originalScope.SetEquals(projectedScope);
                int beforeKnown = originalEligible.Count(g => g.BaselineCostKnown);
                int afterKnown = projectedEligible.Count(g => g.BaselineCostKnown);
                bool costKnown = sameEligibleScope
                    && beforeKnown == originalEligible.Length
                    && afterKnown == projectedEligible.Length;
                var originalEstimate = costKnown
                    ? originalEligible.Sum(g => g.BaselineCost.FullResidentBytes)
                        - before
                    : 0L;
                var projectedEstimate = costKnown
                    ? projectedEligible.Sum(g => g.BaselineCost.FullResidentBytes)
                        - after
                    : 0L;
                var comparableCost = costKnown
                    ? originalEstimate + "->" + projectedEstimate
                        + ", conditionalPlanSavingBytes="
                        + (originalEstimate - projectedEstimate)
                    : "unknown:eligibility-changed-or-cost-model-incomplete";
                return "featureSet=[" + string.Join(";",
                        unique.Select(item =>
                            item.material.name + ":" + item.rule.Name))
                    + "], removedUses=[" + string.Join(";",
                        unique.Select(item => item.material.name + ":"
                            + string.Join(",", item.rule.TextureProperties
                                .Where(name => originalUses[item.material]
                                    .ContainsKey(name))))) + "]"
                    + ", atomicGroups=" + originalGroups.Count
                    + "->" + alternatives.Count
                    + ", selectedMerges=" + originalPlan.Selections.Count
                    + "->" + projectedPlan.Selections.Count
                    + ", selectedMergeSavingBytes=" + before + "->" + after
                    + ", eligibleMaterialCount=" + originalScope.Count
                    + "->" + projectedScope.Count
                    + ", sameEligibleScope=" + sameEligibleScope
                    + ", knownBaselineGroups=" + beforeKnown
                    + "/" + originalEligible.Length + "->" + afterKnown
                    + "/" + projectedEligible.Length
                    + ", estimatedEligibleFinalBytes=" + comparableCost
                    + ", layoutsBefore=[" + string.Join(";",
                        originalPlan.Selections.Select(x =>
                            x.PlannedLayoutSize.x + "x" + x.PlannedLayoutSize.y))
                    + "], layoutsAfter=[" + string.Join(";",
                        projectedPlan.Selections.Select(x =>
                            x.PlannedLayoutSize.x + "x" + x.PlannedLayoutSize.y))
                    + "], elapsedMs=" + watch.ElapsedMilliseconds
                    + ", status=conditional-shadow-only"
                    + ", limitations=mip0;animations;shader-variants;"
                    + "render-equivalence-unverified;selected-merge-cost-not-total";
            }
            finally
            {
                foreach (var clone in clones)
                    if (clone != null)
                        UnityEngine.Object.DestroyImmediate(clone);
            }
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

        internal static string Compare(
            IDomain domain,
            AutoAtlasTexture configuration,
            AtlasSetting settings,
            HashSet<Material> executionMaterials,
            IReadOnlyDictionary<Material, IReadOnlyDictionary<string, Texture>> observedUses,
            HashSet<Texture> originalSources,
            long baselinePixels,
            long baselineMemory,
            AtlasTexture.AtlasExecutionMetrics baselineLayout,
            Material excludedMaterial,
            string excludedProperty)
        {
            if (!executionMaterials.Contains(excludedMaterial)
                || !observedUses.TryGetValue(excludedMaterial, out var inputs)
                || !inputs.ContainsKey(excludedProperty))
                return "shadow-replan=unknown:source-use-not-in-active-group";

            var actualUses = executionMaterials.ToDictionary(
                material => material,
                material => observedUses[material]);
            var shadowUses = WithoutUse(actualUses, excludedMaterial, excludedProperty);
            var shadowGroups = AutoAtlasTextureProcessor.MakeTextureConnectedGroups(
                executionMaterials, shadowUses);
            var engine = domain.GetTexTransCoreEngineForUnity();
            var originalMaterialConsumers = domain.GetAllMaterials();

            // Only source textures which remain referenced after the hypothetical
            // output material rewrite are charged. Outside consumers are left
            // untouched; in the affected material the one proven-mip0 mask is
            // assumed unbound for this projection alone.
            var retained = originalMaterialConsumers
                .Where(material => !executionMaterials.Contains(material))
                .SelectMany(material => material.EnumerateReferencedTextures())
                .Where(originalSources.Contains)
                .ToHashSet();

            long shadowOutputBytes = 0;
            long shadowOutputPixels = 0;
            var layoutReports = new List<string>();
            foreach (var shadowGroup in shadowGroups)
            {
                var renderers = AutoAtlasTextureProcessor.ResolveAutoAtlasTargetRenderers(
                    domain, configuration, shadowGroup);
                if (renderers.Length == 0)
                    return "shadow-replan=unknown:no-target-renderers";

                AtlasTexture.AtlasResult? atlas = null;
                TexFineTuningResult? tuning = null;
                Material[] generatedMaterials = Array.Empty<Material>();
                AtlasTexture.AtlasExecutionMetrics shadowLayout = default;
                try
                {
                    // DoAtlasTexture allocates isolated meshes/RTs but never
                    // applies them. The injected dictionary changes only this
                    // invocation's MaterialGroupingContext.
                    atlas = AtlasTexture.DoAtlasTexture(
                        domain, engine, shadowGroup, renderers,
                        new List<net.rs64.TexTransTool.TextureAtlas.IslandSizePriorityTuner.IIslandSizePriorityTuner?>(),
                        settings,
                        new AtlasTexture.AtlasExecutionOptions
                        {
                            RequireLossless = true,
                            ReportProgressInfo = false,
                            AcceptAtlasSize = (_, _, metrics) =>
                            {
                                shadowLayout = metrics;
                                return true;
                            }
                        },
                        shadowUses);
                    if (!atlas.IsSuccess || atlas.AtlasContext == null
                        || atlas.CompiledAtlasTextures == null)
                        return "shadow-replan=unknown:lossless-atlas-failed";

                    tuning = AtlasTexture.DoTextureFinTuning(
                        engine, atlas.AtlasContext, settings,
                        atlas.CompiledAtlasTextures, null);
                    if (!AtlasTexture.TryCalculateFinalTextureMemory(
                            atlas.AtlasContext, tuning, out var memory))
                        return "shadow-replan=unknown:output-memory-model";

                    var resultingTextures = tuning.RenderTextures.ToDictionary(
                        pair => pair.Key,
                        pair => engine.GetReferenceRenderTexture(pair.Value));
                    var materialMaps = AtlasTexture.GenerateAtlasedMaterialMaps(
                        domain, shadowGroup, settings,
                        (new List<AtlasTexture.MaterialMergeGroup>(), null, null),
                        resultingTextures,
                        atlas.PreserveBump2ndMaterials,
                        atlas.PreservedOriginalUVChannel);
                    generatedMaterials = materialMaps.materialMap.Values
                        .Concat(materialMaps.domainsMaterial2ReplaceMaterial.Values)
                        .Distinct()
                        .ToArray();

                    if (materialMaps.materialMap.TryGetValue(
                            excludedMaterial, out var excludedClone)
                        && excludedClone.HasProperty(excludedProperty))
                    {
                        // Clone only. The real material keeps its original binding.
                        excludedClone.SetTexture(excludedProperty, null);
                    }
                    retained.UnionWith(AutoAtlasTextureProcessor
                        .CollectRetainedSourceTextures(
                            generatedMaterials, originalSources));

                    shadowOutputBytes += memory;
                    shadowOutputPixels += tuning.TextureDescriptors.Keys
                        .Distinct()
                        .Sum(texture => (long)texture.Width * texture.Hight);
                    layoutReports.Add(shadowLayout.LayoutSize.x + "x"
                        + shadowLayout.LayoutSize.y + "@topFree="
                        + shadowLayout.TopFreeFraction.ToString(
                            "F6", System.Globalization.CultureInfo.InvariantCulture));
                }
                finally
                {
                    AutoAtlasTextureProcessor.DestroyUnappliedAtlasedMaterials(
                        generatedMaterials);
                    if (atlas?.CompiledAtlasTextures != null)
                    {
                        foreach (var rt in atlas.CompiledAtlasTextures.Values
                                     .Concat(tuning?.TextureDescriptors.Keys
                                         ?? Enumerable.Empty<ITTRenderTexture>())
                                     .Distinct())
                            rt.Dispose();
                    }
                    if (atlas?.AtlasedMeshes != null)
                        AutoAtlasTextureProcessor.DestroyUnappliedAtlasedMeshes(
                            atlas.AtlasedMeshes);
                    atlas?.AtlasContext?.Dispose();
                }
            }

            if (!AtlasTexture.TryCalculateSourceTextureMemory(
                    domain, retained, out var shadowRetainedBytes))
                return "shadow-replan=unknown:retained-source-memory-model";

            var shadowPixels = shadowOutputPixels + retained.Sum(texture =>
                (long)texture.width * texture.height);
            var shadowMemory = shadowOutputBytes + shadowRetainedBytes;
            return "material=" + excludedMaterial.name
                + ", excludedUse=" + excludedProperty
                + ", status=shadow-standalone-replan-not-applied"
                + ", groups=" + shadowGroups.Count
                + ", baselineLayout=" + baselineLayout.LayoutSize.x + "x"
                + baselineLayout.LayoutSize.y
                + ", baselineTopFree=" + baselineLayout.TopFreeFraction.ToString(
                    "F6", System.Globalization.CultureInfo.InvariantCulture)
                + ", shadowLayouts=[" + string.Join(";", layoutReports) + "]"
                + ", totalPixels=" + baselinePixels + "->" + shadowPixels
                + ", totalMemoryBytes=" + baselineMemory + "->" + shadowMemory
                + ", signedMemorySavingBytes=" + (baselineMemory - shadowMemory)
                + ", outputMemoryBytes=" + shadowOutputBytes
                + ", retainedSourceMemoryBytes=" + shadowRetainedBytes
                + ", constraints=assumed-mask-unbound;mip0-only-proof;"
                + "standalone-no-merge-reselection;no-animation-or-shader-proof";
        }
    }
}
