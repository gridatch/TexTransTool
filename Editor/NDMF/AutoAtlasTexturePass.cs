#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using net.rs64.TexTransCore;
using net.rs64.TexTransCore.UVIsland;
using net.rs64.TexTransTool.TextureAtlas;
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
            var configurations = context.AvatarRootObject
                .GetComponentsInChildren<AutoAtlasTexture>(true)
                .Where(component => component != null &&
                    TexTransBehaviorSearch.CheckIsActive(component.gameObject, context.AvatarRootObject))
                .ToArray();

            if (configurations.Length == 0) return;
            if (configurations.Length != 1)
            {
                Debug.LogWarning("AutoAtlasTexture: 同一アバターに有効な設定が複数あります。二重処理を防ぐため実行しません。");
                return;
            }

            AutoAtlasTextureProcessor.Execute(
                TTTContext(context).Domain, context.AvatarRootObject, configurations[0]);
        }
    }

    internal static class AutoAtlasTextureProcessor
    {
        internal static void Execute(IDomain domain, GameObject avatarRoot, AutoAtlasTexture configuration)
        {
            var allRenderers = domain.EnumerateRenderer().Where(renderer => renderer != null).ToArray();
            var allowedRenderers = AtlasTexture.GetAtlasAllowedRenderers(
                domain, allRenderers, includeDisabledRenderer: true)
                .Where(renderer => !configuration.ExcludedRenderers.Any(excluded =>
                    excluded != null && domain.OriginEqual(excluded, renderer)))
                .ToHashSet();

            var allMaterials = allRenderers
                .SelectMany(renderer => domain.GetMaterials(renderer))
                .UOfType<Material>()
                .ToHashSet();
            if (allMaterials.Count == 0) return;

            // Material groups follow the textures actually transformed by TTT for UV0,
            // not merely common shaders or all texture references in the material.
            var textureUsages = new MaterialGroupingContext(
                allMaterials, UVChannel.UV0, primaryTexturePropertyOrMaximum: null);
            var propertyTextures = textureUsages.ContainsTextureDictionaries;
            var excludedMaterials = configuration.ExcludedMaterials
                .Where(material => material != null)
                .ToArray();

            var manuallySelectedMaterials = avatarRoot
                .GetComponentsInChildren<AtlasTexture>(true)
                .Where(atlas => atlas != null &&
                    TexTransBehaviorSearch.CheckIsActive(atlas.gameObject, avatarRoot))
                .SelectMany(atlas => atlas.AtlasTargetMaterials)
                .Where(material => material != null)
                .Cast<Material>()
                .ToArray();

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

            var maxSize = Mathf.Clamp(configuration.MaxAtlasSize, 256, 4096);
            var succeeded = 0;
            var groups = MakeTextureConnectedGroups(allMaterials, propertyTextures);
            foreach (var group in groups)
            {
                if (group.Any(material => excludedMaterials.Any(excluded =>
                        domain.OriginEqual(excluded, material)) ||
                    manuallySelectedMaterials.Any(selected =>
                        domain.OriginEqual(selected, material))))
                    continue;

                // Every renderer referencing a material must be transformed together:
                // replacing a shared material while leaving another mesh's UVs intact
                // would corrupt that mesh's appearance.
                var referencingRenderers = allRenderers.Where(renderer =>
                    domain.GetMaterials(renderer).UOfType<Material>().Any(group.Contains)).ToArray();
                if (referencingRenderers.Length == 0 ||
                    referencingRenderers.Any(renderer => !allowedRenderers.Contains(renderer)))
                    continue;

                var atlasRenderers = AtlasTexture.FilterExistUVChannel(
                    domain, referencingRenderers, UVChannel.UV0);
                if (atlasRenderers.Length != referencingRenderers.Length) continue;

                var texturesToReplace = group
                    .SelectMany(material => propertyTextures[material].Values)
                    .Where(texture => texture != null)
                    .ToHashSet();
                if (texturesToReplace.Count == 0) continue;

                // A source texture cannot be removed if it is also referenced by a
                // property outside this atlas operation. The complete source set
                // must be eligible; otherwise generating an additional atlas can
                // increase memory usage.
                if (!ReferencesAreExclusive(
                    allMaterials, group, propertyTextures, texturesToReplace))
                    continue;

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
                    maxAtlasSize: maxSize,
                    acceptAtlasSize: (atlasContext, size) =>
                    {
                        var texturePropertyCount =
                            atlasContext.MaterialGroupingCtx.GetContainsAllProperties().Count;
                        if (texturePropertyCount == 0) return false;
                        var outputPixels = (long)size.x * size.y * texturePropertyCount;
                        return outputPixels < originalPixels;
                    });

                if (!atlasResult.IsSuccess) continue;

                using var atlasContext = atlasResult.AtlasContext!;
                var compiledTextures = atlasResult.CompiledAtlasTextures!;
                var tuningResult = AtlasTexture.DoTextureFinTuning(
                    engine, atlasContext, settings, compiledTextures, null);
                var resultingTextures = tuningResult.RenderTextures.ToDictionary(
                    item => item.Key,
                    item => engine.GetReferenceRenderTexture(item.Value));
                var retainedRenderTextures = tuningResult.TextureDescriptors.Keys.ToHashSet();

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

                succeeded++;
            }

            Debug.Log($"AutoAtlasTexture: {succeeded} グループをアトラス化しました。");
        }

        private static bool ReferencesAreExclusive(
            HashSet<Material> allMaterials,
            HashSet<Material> group,
            IReadOnlyDictionary<Material, IReadOnlyDictionary<string, Texture>> propertyTextures,
            HashSet<Texture> replacedTextures)
        {
            foreach (var material in allMaterials)
            {
                var isTarget = group.Contains(material);
                var atlasProperties = propertyTextures[material];
                foreach (var property in material.GetTexturePropertyNames())
                {
                    var texture = material.GetTexture(property);
                    if (texture == null || !replacedTextures.Contains(texture)) continue;
                    if (!isTarget ||
                        !atlasProperties.TryGetValue(property, out var atlasTexture) ||
                        atlasTexture != texture)
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
