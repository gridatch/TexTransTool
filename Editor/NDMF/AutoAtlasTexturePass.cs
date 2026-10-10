#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using net.rs64.TexTransCore;
using net.rs64.TexTransCore.UVIsland;
using net.rs64.TexTransTool.TextureAtlas;
using net.rs64.TexTransTool.Editor;
using net.rs64.TexTransTool.TextureAtlas.FineTuning;
using net.rs64.TexTransTool.TextureAtlas.IslandSizePriorityTuner;
using net.rs64.TexTransTool.Utils;
using UnityEngine;

namespace net.rs64.TexTransTool.NDMF
{
    /// <summary>
    /// Runs after regular, explicitly configured AtlasTexture components.
    /// AutoAtlasTexture is deliberately not in the ordinary component phase list.
    /// </summary>
    internal sealed class AutoAtlasTexturePass : TTTPass<AutoAtlasTexturePass>
    {
        protected override void Execute(BuildContext context)
        {
            var configurations = AutoAtlasTextureProcessor.GetActiveConfigurations(
                context.AvatarRootObject);

            if (configurations.Length == 0) return;
            if (configurations.Length != 1)
            {
                Debug.LogWarning("AutoAtlasTexture: 同一アバターに有効な設定が複数あります。二重処理を防ぐため実行しません。");
                return;
            }

            var session = TTTContext(context);
            var manualAtlases = session.PhaseAtList[TexTransPhase.Optimizing]
                .OfType<AtlasTexture>()
                .Where(atlas => TexTransBehaviorSearch.CheckIsActiveBehavior(
                    atlas, context.AvatarRootObject))
                .ToArray();
            AutoAtlasTextureProcessor.Execute(
                session.Domain, context.AvatarRootObject, configurations[0], manualAtlases);
        }
    }

    internal static class AutoAtlasTextureProcessor
    {
        internal static AutoAtlasTexture[] GetActiveConfigurations(GameObject root)
        {
            return root.GetComponentsInChildren<AutoAtlasTexture>(true)
                .Where(component => component != null &&
                    TexTransBehaviorSearch.CheckIsActive(component.gameObject, root))
                .ToArray();
        }

        // This is an upper bound computed before the AAO-specific UV negotiation,
        // not the final atlas selection, which can change during later NDMF phases.
        internal static Renderer[] GetPotentialRenderers(IRendererTargeting domain,
            AutoAtlasTexture configuration)
        {
            var allowed = AtlasTexture.GetAtlasAllowedRenderers(
                    domain, domain.EnumerateRenderer(), includeDisabledRenderer: true)
                .Where(renderer => !configuration.ExcludedRenderers.Any(excluded =>
                    excluded != null && domain.OriginEqual(excluded, renderer)))
                .ToArray();

            // UV0 is the only channel repacked by AutoAtlasTexture. Do not
            // evacuate UVs for renderers whose materials have no compatible
            // textured properties at the time of negotiation.
            var materials = allowed.SelectMany(domain.GetMaterials)
                .UOfType<Material>().ToHashSet();
            if (materials.Count == 0) return Array.Empty<Renderer>();
            var textureUsages = new MaterialGroupingContext(materials, UVChannel.UV0, null);
            var candidates = textureUsages.ContainsTextureDictionaries
                .Where(pair => pair.Value.Count != 0)
                .Select(pair => pair.Key)
                .ToHashSet();
            return allowed.Where(renderer =>
                domain.GetMaterials(renderer).UOfType<Material>().Any(candidates.Contains))
                .ToArray();
        }

        internal static void Execute(IDomain domain, GameObject avatarRoot,
            AutoAtlasTexture configuration, IReadOnlyCollection<AtlasTexture> manualAtlases)
        {
            // Store only the NDMF console summary for this build.
            var completed = new List<(string Block, int Relocations, long Milliseconds)>();

            string OriginalMaterialName(Material material)
            {
                // Reuse NDMF's object identity; build-stage suffixes are not
                // stripped or guessed from the material name.
                var identity = ObjectRegistry.GetReference(material);
                if (identity?.Object is Material source && source != null) return source.name;
                // ObjectReference snapshots its display name at creation; this
                // remains available even if the original Unity object was destroyed.
                return identity?.ToString() ?? material.name;
            }

            var allRenderers = domain.EnumerateRenderer().Where(renderer => renderer != null).ToArray();
            var allowedRenderers = GetPotentialRenderers(domain, configuration).ToHashSet();

            // Selection is renderer-scoped. Domain.GetAllMaterials() is used
            // separately for safety checks because it also includes animation refs.
            var allMaterials = allRenderers.SelectMany(domain.GetMaterials)
                .UOfType<Material>().ToHashSet();
            // Includes animation material references in the NDMF domain.
            var allReferencedMaterials = domain.GetAllMaterials();
            if (allMaterials.Count == 0)
            {
                TTTLog.ReportingObject(configuration, () =>
                    TTTLog.Info("AtlasTexture:info:TargetNotFound"));
                return;
            }

            // Material groups follow the textures actually transformed by TTT for UV0,
            // not merely common shaders or all texture references in the material.
            var textureUsages = new MaterialGroupingContext(
                allMaterials, UVChannel.UV0, primaryTexturePropertyOrMaximum: null);
            var propertyTextures = textureUsages.ContainsTextureDictionaries;
            var excludedMaterials = configuration.ExcludedMaterials
                .Where(material => material != null)
                .ToArray();

            // Resolve manual AtlasTexture targets through TTT's existing lookup
            // rather than rescanning hierarchy and comparing raw user selections.
            var manuallySelectedMaterials = manualAtlases
                .SelectMany(atlas => atlas.GetTargetMaterials(domain,
                    AtlasTexture.GetAtlasAllowedRenderers(domain,
                        domain.EnumerateRenderer(), atlas.AtlasSetting.IncludeDisabledRenderer)))
                .ToHashSet();

            var settings = new AtlasSetting
            {
                AutoAtlasTextureSize = true,
                UsePrimaryMaximumTexture = true,
                IncludeDisabledRenderer = true,
                AtlasTargetUVChannel = UVChannel.UV0,
                IslandPadding = Mathf.Clamp(configuration.IslandPadding, 0f, 0.05f),
            };
            // Lossless placement is separate from FineTuning. Do not apply the
            // default 512px resize to non-main textures for this component.
            settings.TextureFineTuning.RemoveAll(tuning => tuning is Resize);

            var groups = MakeTextureConnectedGroups(allMaterials, propertyTextures);
            var resolutionLine = "AutoAtlasTexture:info:ResolutionChange".GetLocalize();
            var materialBlock = "AutoAtlasTexture:info:MaterialBlock".GetLocalize();
            foreach (var group in groups)
            {
                if (group.Any(material => excludedMaterials.Any(excluded =>
                        domain.OriginEqual(excluded, material)) ||
                    manuallySelectedMaterials.Any(selected =>
                        domain.OriginEqual(selected, material))))
                {
                    continue;
                }

                // Every renderer referencing a material must be transformed together:
                // replacing a shared material while leaving another mesh's UVs intact
                // would corrupt that mesh's appearance.
                var referencingRenderers = allRenderers.Where(renderer =>
                    domain.GetMaterials(renderer).UOfType<Material>().Any(group.Contains)).ToArray();
                if (referencingRenderers.Length == 0 ||
                    referencingRenderers.Any(renderer => !allowedRenderers.Contains(renderer)))
                {
                    continue;
                }

                var atlasRenderers = AtlasTexture.FilterExistUVChannel(
                    domain, referencingRenderers, UVChannel.UV0);
                if (atlasRenderers.Length != referencingRenderers.Length)
                {
                    continue;
                }

                var texturesToReplace = group
                    .SelectMany(material => propertyTextures[material].Values)
                    .Where(texture => texture != null)
                    .ToHashSet();
                if (texturesToReplace.Count == 0)
                {
                    continue;
                }

                // A source texture cannot be removed if it is also referenced by a
                // property outside this atlas operation. The complete source set
                // must be eligible; otherwise generating an additional atlas can
                // increase memory usage.
                if (!ReferencesAreExclusive(
                    allReferencedMaterials, group, propertyTextures, texturesToReplace))
                {
                    continue;
                }

                var originalPixels = texturesToReplace.Sum(texture =>
                    (long)texture.width * texture.height);

                var engine = domain.GetTexTransCoreEngineForUnity();
                var atlasResult = AtlasTexture.DoAtlasTexture(
                    domain,
                    engine,
                    group,
                    atlasRenderers,
                    new List<IIslandSizePriorityTuner?>(),
                    settings,
                    requireLossless: true,
                    acceptAtlasSize: (atlasContext, size) =>
                    {
                        var texturePropertyCount =
                            atlasContext.MaterialGroupingCtx.GetContainsAllProperties().Count;
                        if (texturePropertyCount == 0) return false;
                        var outputPixels = (long)size.x * size.y * texturePropertyCount;
                        return outputPixels < originalPixels;
                    },
                    reportProgressInfo: false);

                if (!atlasResult.IsSuccess) continue;

                using var atlasContext = atlasResult.AtlasContext!;
                var compiledTextures = atlasResult.CompiledAtlasTextures!;
                var tuningResult = AtlasTexture.DoTextureFinTuning(
                    engine, atlasContext, settings, compiledTextures, null);
                var resultingTextures = tuningResult.RenderTextures.ToDictionary(
                    item => item.Key,
                    item => engine.GetReferenceRenderTexture(item.Value));
                var retainedRenderTextures = tuningResult.TextureDescriptors.Keys.ToHashSet();

                // A source texture may be used by multiple shader properties.
                // Count it only once for each resolution transition.
                var resolutionChanges = group
                    .SelectMany(material => propertyTextures[material]
                        .Where(property => tuningResult.RenderTextures.ContainsKey(property.Key))
                        .Select(property => new
                        {
                            Source = property.Value,
                            Output = tuningResult.RenderTextures[property.Key],
                        }))
                    .GroupBy(entry => (entry.Source, entry.Output.Width, entry.Output.Hight))
                    .Select(entries => entries.First())
                    .GroupBy(entry => (
                        BeforeWidth: entry.Source.width,
                        BeforeHeight: entry.Source.height,
                        AfterWidth: entry.Output.Width,
                        AfterHeight: entry.Output.Hight))
                    .OrderByDescending(entries => (long)entries.Key.BeforeWidth * entries.Key.BeforeHeight)
                    .ThenByDescending(entries => (long)entries.Key.AfterWidth * entries.Key.AfterHeight)
                    .Select(entries => string.Format(resolutionLine,
                        entries.Key.BeforeWidth, entries.Key.BeforeHeight,
                        entries.Key.AfterWidth, entries.Key.AfterHeight, entries.Count()));
                var block = string.Format(materialBlock,
                    string.Join(", ", group.Select(OriginalMaterialName)
                        .Distinct().OrderBy(name => name, StringComparer.Ordinal)),
                    atlasResult.TopFreeFraction ?? 0f,
                    string.Join("\n", resolutionChanges));

                AtlasTexture.ReplaceMesh(
                    domain, atlasRenderers, atlasContext, atlasResult.AtlasedMeshes!);
                AtlasTexture.ReplaceAtlasedMaterials(
                    domain,
                    group,
                    settings,
                    (new List<AtlasTexture.MaterialMergeGroup>(), null, null),
                    resultingTextures,
                    atlasResult.PreserveBump2ndMaterials,
                    atlasResult.PreservedOriginalUVChannel);

                foreach (var rt in compiledTextures.Values)
                    if (!retainedRenderTextures.Contains(rt)) rt.Dispose();

                foreach (var descriptor in tuningResult.TextureDescriptors)
                    domain.RegisterPostProcessingAndLazyGPUReadBack(
                        descriptor.Key, descriptor.Value);

                completed.Add((block, atlasResult.TotalRelocateCount,
                    atlasResult.RelocationTimeMilliseconds));
            }

            if (completed.Count == 0)
            {
                TTTLog.ReportingObject(configuration, () =>
                    TTTLog.Info("AtlasTexture:info:TargetNotFound"));
                return;
            }

            TTTLog.ReportingObject(configuration, () =>
                TTTLog.Info("AutoAtlasTexture:info:RelocateResult",
                    string.Join("\n\n", completed.Select(item => item.Block)),
                    completed.Sum(item => item.Relocations),
                    completed.Sum(item => item.Milliseconds)));
        }

        private static bool ReferencesAreExclusive(
            HashSet<Material> allReferencedMaterials,
            HashSet<Material> group,
            IReadOnlyDictionary<Material, IReadOnlyDictionary<string, Texture>> propertyTextures,
            HashSet<Texture> replacedTextures)
        {
            foreach (var material in allReferencedMaterials)
            {
                var isTarget = group.Contains(material);
                propertyTextures.TryGetValue(material, out var atlasProperties);
                // Reuse TTT's shader-aware property enumeration. The old
                // implementation only saw renderer materials and could overlook
                // materials referenced exclusively by animations.
                foreach (var property in material.GetTextureReferences())
                {
                    if (!replacedTextures.Contains(property.Value)) continue;
                    if (!isTarget || atlasProperties == null ||
                        !atlasProperties.TryGetValue(property.Key, out var atlasTexture) ||
                        atlasTexture != property.Value)
                        return false;
                }
            }
            return true;
        }

        private static List<HashSet<Material>> MakeTextureConnectedGroups(
            HashSet<Material> materials,
            IReadOnlyDictionary<Material, IReadOnlyDictionary<string, Texture>> properties)
        {
            var materialsByTexture = new Dictionary<Texture, HashSet<Material>>();
            foreach (var material in materials)
            {
                foreach (var texture in properties[material].Values)
                {
                    if (texture == null) continue;
                    if (!materialsByTexture.TryGetValue(texture, out var consumers))
                        materialsByTexture[texture] = consumers = new HashSet<Material>();
                    consumers.Add(material);
                }
            }

            var remaining = new HashSet<Material>(materials.Where(material =>
                properties[material].Count != 0));
            var results = new List<HashSet<Material>>();
            while (remaining.Count != 0)
            {
                var seed = remaining.First();
                remaining.Remove(seed);
                var group = new HashSet<Material> { seed };
                var pending = new Queue<Material>();
                pending.Enqueue(seed);

                while (pending.Count != 0)
                {
                    var current = pending.Dequeue();
                    foreach (var texture in properties[current].Values)
                    {
                        if (texture == null) continue;
                        foreach (var adjacent in materialsByTexture[texture])
                        {
                            if (!remaining.Remove(adjacent)) continue;
                            group.Add(adjacent);
                            pending.Enqueue(adjacent);
                        }
                    }
                }
                results.Add(group);
            }
            return results;
        }
    }
}
