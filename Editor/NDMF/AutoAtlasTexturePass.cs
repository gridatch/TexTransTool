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
    // The original renderer/material-slot names are captured before material
    // transformers run. ObjectRegistry is preferred; this snapshot handles
    // unregistered temporary material clones without parsing their names.
    internal sealed class AutoAtlasTextureOriginalNames
    {
        internal readonly Dictionary<ObjectReference, string[]> Slots = new();
    }

    internal sealed class CaptureAutoAtlasSourceMaterialsPass : Pass<CaptureAutoAtlasSourceMaterialsPass>
    {
        protected override void Execute(BuildContext context)
        {
            if (context.AvatarRootObject.GetComponentsInChildren<AutoAtlasTexture>(true).Length == 0)
                return;
            var names = context.GetState(_ => new AutoAtlasTextureOriginalNames());
            foreach (var renderer in context.AvatarRootObject.GetComponentsInChildren<Renderer>(true))
            {
                names.Slots[ObjectRegistry.GetReference(renderer)] =
                    renderer.sharedMaterials.Select(material => material != null ? material.name : "").ToArray();
            }
        }
    }

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
                TTTContext(context).Domain, context.AvatarRootObject,
                configurations[0], context.GetState(_ => new AutoAtlasTextureOriginalNames()));
        }
    }

    internal static class AutoAtlasTextureProcessor
    {
        internal static void Execute(IDomain domain, GameObject avatarRoot,
            AutoAtlasTexture configuration, AutoAtlasTextureOriginalNames initialNames)
        {
            var report = new AutoAtlasTextureBuildReport { AvatarName = avatarRoot.name };
            // The report is reset for every build, including builds without candidates.
            AutoAtlasTextureReportStore.Set(report);

            // Build a stable per-material lookup from initial renderer slot names.
            // A changed slot count is not mapped: silently guessing the slot would
            // be worse than keeping the build-stage name.
            var sourceNamesByMaterial = new Dictionary<Material, HashSet<string>>();
            foreach (var renderer in domain.EnumerateRenderer())
            {
                if (renderer == null ||
                    !initialNames.Slots.TryGetValue(ObjectRegistry.GetReference(renderer), out var originalSlots))
                    continue;
                var currentSlots = domain.GetMaterials(renderer);
                if (currentSlots.Length != originalSlots.Length) continue;

                for (var i = 0; i < currentSlots.Length; i++)
                {
                    var material = currentSlots[i];
                    if (material == null || string.IsNullOrEmpty(originalSlots[i])) continue;
                    if (!sourceNamesByMaterial.TryGetValue(material, out var names))
                        sourceNamesByMaterial[material] = names = new HashSet<string>();
                    names.Add(originalSlots[i]);
                }
            }

            string OriginalMaterialName(Material material)
            {
                // Use NDMF's original asset when the replacing tool registered it.
                var original = ObjectRegistry.GetReference(material)?.Object as Material;
                if (original != null && original != material) return original.name;

                // For unregistered clones, use the material-slot name saved
                // before the avatar build's Transforming phase.
                if (sourceNamesByMaterial.TryGetValue(material, out var names) && names.Count == 1)
                    return names.First();

                // Do not remove suffixes heuristically: that would invent a name.
                return original != null ? original.name : material.name;
            }

            void Skip(HashSet<Material> group, string reason)
            {
                report.Skipped.Add(new AutoAtlasTextureSkippedGroup
                {
                    MaterialNames = string.Join(", ", group.Select(OriginalMaterialName)
                        .Distinct().OrderBy(name => name, StringComparer.Ordinal)),
                    Reason = reason,
                });
            }

            string RendererPath(Renderer renderer)
            {
                var parts = new Stack<string>();
                var transform = renderer.transform;
                while (transform != null && transform != avatarRoot.transform)
                {
                    parts.Push(transform.name);
                    transform = transform.parent;
                }
                return string.Join("/", parts);
            }

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
            if (allMaterials.Count == 0)
            {
                TTTLog.ReportingObject(configuration, () =>
                    TTTLog.Info("AutoAtlasTexture:info:NoChanges"));
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
            var groups = MakeTextureConnectedGroups(allMaterials, propertyTextures);
            foreach (var group in groups)
            {
                if (group.Any(material => excludedMaterials.Any(excluded =>
                        domain.OriginEqual(excluded, material)) ||
                    manuallySelectedMaterials.Any(selected =>
                        domain.OriginEqual(selected, material))))
                {
                    Skip(group, "除外指定、または手動AtlasTextureの対象Materialとの重複");
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
                    Skip(group, "参照Rendererの一部が処理対象外（除外指定、EditorOnlyなど）");
                    continue;
                }

                var atlasRenderers = AtlasTexture.FilterExistUVChannel(
                    domain, referencingRenderers, UVChannel.UV0);
                if (atlasRenderers.Length != referencingRenderers.Length)
                {
                    Skip(group, "UV0を持たないRendererがある");
                    continue;
                }

                var texturesToReplace = group
                    .SelectMany(material => propertyTextures[material].Values)
                    .Where(texture => texture != null)
                    .ToHashSet();
                if (texturesToReplace.Count == 0)
                {
                    Skip(group, "置き換え可能なTextureがない");
                    continue;
                }

                // A source texture cannot be removed if it is also referenced by a
                // property outside this atlas operation. The complete source set
                // must be eligible; otherwise generating an additional atlas can
                // increase memory usage.
                if (!ReferencesAreExclusive(
                    allMaterials, group, propertyTextures, texturesToReplace))
                {
                    Skip(group, "対象Textureが処理対象外のMaterial/プロパティにも参照されている");
                    continue;
                }

                var originalPixels = texturesToReplace.Sum(texture =>
                    (long)texture.width * texture.height);

                var rejectedByPixelCount = false;
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
                        rejectedByPixelCount = outputPixels >= originalPixels;
                        return !rejectedByPixelCount;
                    },
                    reportProgressInfo: false);

                if (!atlasResult.IsSuccess)
                {
                    Skip(group,
                        rejectedByPixelCount
                            ? "生成後の総画素数が小さくならない"
                            : "指定された最大サイズ内に縮小なしで配置できない、または生成に失敗");
                    continue;
                }

                using var atlasContext = atlasResult.AtlasContext!;
                var compiledTextures = atlasResult.CompiledAtlasTextures!;
                var tuningResult = AtlasTexture.DoTextureFinTuning(
                    engine, atlasContext, settings, compiledTextures, null);
                var resultingTextures = tuningResult.RenderTextures.ToDictionary(
                    item => item.Key,
                    item => engine.GetReferenceRenderTexture(item.Value));
                var retainedRenderTextures = tuningResult.TextureDescriptors.Keys.ToHashSet();

                // Capture immutable names/dimensions while the source references still exist.
                var groupReport = new AutoAtlasTextureGroupReport
                {
                    MaterialNames = group.Select(OriginalMaterialName)
                        .Distinct().OrderBy(name => name, StringComparer.Ordinal).ToArray(),
                    TopFreeFraction = atlasResult.TopFreeFraction ?? 0f,
                    PropertyChanges = tuningResult.RenderTextures
                        .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                        .Select(entry => new AutoAtlasTexturePropertyReport
                        {
                            PropertyName = entry.Key,
                            SourceTextures = group
                                .Select(material => propertyTextures[material]
                                    .TryGetValue(entry.Key, out var texture) ? texture : null)
                                .Where(texture => texture != null)
                                .Cast<Texture>()
                                .Distinct()
                                .OrderBy(texture => texture.name, StringComparer.Ordinal)
                                .Select(texture => new AutoAtlasTextureImageReport
                                {
                                    Name = texture.name,
                                    Width = texture.width,
                                    Height = texture.height,
                                })
                                .ToArray(),
                            GeneratedTexture = new AutoAtlasTextureImageReport
                            {
                                Name = entry.Value.Name,
                                Width = entry.Value.Width,
                                Height = entry.Value.Hight,
                            },
                        }).ToArray(),
                    RendererNames = atlasRenderers.Select(RendererPath)
                        .OrderBy(name => name).ToArray(),
                    SourceTextures = texturesToReplace
                        .OrderBy(texture => texture.name)
                        .Select(texture => new AutoAtlasTextureImageReport
                        {
                            Name = texture.name,
                            Width = texture.width,
                            Height = texture.height,
                            Properties = string.Join(", ", group.SelectMany(material =>
                                propertyTextures[material]
                                    .Where(item => item.Value == texture)
                                    .Select(item => OriginalMaterialName(material) + "." + item.Key))
                                .Distinct().OrderBy(name => name)),
                        }).ToArray(),
                    GeneratedTextures = tuningResult.RenderTextures
                        .GroupBy(item => item.Value)
                        .Select(entries => new AutoAtlasTextureImageReport
                        {
                            Name = entries.Key.Name,
                            Width = entries.Key.Width,
                            Height = entries.Key.Hight,
                            Properties = string.Join(", ", entries.Select(entry => entry.Key)
                                .OrderBy(name => name)),
                        })
                        .OrderBy(image => image.Name).ToArray(),
                };

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

                report.Completed.Add(groupReport);
            }

            LogResults();

            void LogResults()
            {
                // A successful atlas operation is the unit of reporting.
                // No console spam for groups that did not need changing.
                if (report.Completed.Count == 0)
                {
                    TTTLog.ReportingObject(configuration, () =>
                        TTTLog.Info("AutoAtlasTexture:info:NoChanges"));
                    return;
                }

                foreach (var groupReport in report.Completed)
                {
                    var changes = string.Join("\n", groupReport.PropertyChanges.Select(change =>
                    {
                        var before = string.Join(", ", change.SourceTextures.Select(texture =>
                            $"{texture.Name} ({texture.Width}×{texture.Height})"));
                        var after = change.GeneratedTexture;
                        return $"  {change.PropertyName}: {before} → {after.Name} ({after.Width}×{after.Height})";
                    }));

                    var materialNames = string.Join("\n", groupReport.MaterialNames
                        .Select(name => "  " + name));
                    TTTLog.ReportingObject(configuration, () =>
                        TTTLog.Info("AutoAtlasTexture:info:AppliedGroup",
                            materialNames,
                            changes,
                            (groupReport.TopFreeFraction * 100f).ToString("F1")));
                }
            }
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
