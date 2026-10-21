#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using net.rs64.TexTransCore;
using net.rs64.TexTransTool.TextureAtlas;
using net.rs64.TexTransTool.TextureAtlas.FineTuning;
using UnityEngine;

namespace net.rs64.TexTransTool.NDMF
{
    /// <summary>
    /// TEMPORARY diagnostic. Run the existing atlas + FineTuning pipeline on
    /// independent, read-only usage dictionaries. No domain replacements,
    /// generated assets registrations, or changes to original materials.
    /// This is an assumed texture unbinding, NOT permission to disable lilToon.
    /// </summary>
    internal static class AutoAtlasShadowReplan
    {
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
