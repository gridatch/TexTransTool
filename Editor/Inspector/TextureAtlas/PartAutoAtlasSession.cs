#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using net.rs64.TexTransTool.Utils;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace net.rs64.TexTransTool.TextureAtlas.Editor
{
    internal sealed class PartAutoAtlasExecutionResult
    {
        internal bool HasChanges;
        internal int AppliedGroupCount;
        internal int TextureCount;
        internal int MaterialCount;
        internal int MeshCount;
    }

    internal static class PartAutoAtlasSession
    {
        internal static PartAutoAtlasExecutionResult Execute(
            GameObject root,
            PartAutoAtlasSettings settings,
            string outputAssetPath,
            IReadOnlyCollection<
                AtlasTextureBaker.AnimationObjectReferenceHit>
                sourceAnimationReferences)
        {
            var result = new PartAutoAtlasExecutionResult();
            if (!settings.Enabled)
                return result;

            var renderers = root.GetComponentsInChildren<Renderer>(true);
            var beforeStates =
                AtlasTextureBaker.CaptureRendererState(renderers);

            using var assetStore = new PartAutoAtlasAssetStore();
            var domain = new PartAutoAtlasDomain(root, assetStore);
            try
            {
                var manualAtlases = root
                    .GetComponentsInChildren<AtlasTexture>(true)
                    .Where(atlas =>
                        atlas != null
                        && TexTransBehaviorSearch.CheckIsActiveBehavior(
                            atlas, root))
                    .ToArray();

                var execution =
                    AutoAtlasTextureProcessor.Execute(
                        domain,
                        settings.ToExecutionSettings(),
                        manualAtlases,
                        root,
                        material => material.name,
                        (_, code, args) =>
                            Debug.Log(string.Format(
                                code.GetLocalize(),
                                args)));

                // AutoAtlas registers adopted GPU outputs for lazy readback.
                // Complete the domain before validation/persistence so the
                // final hierarchy references ordinary Texture2D objects.
                domain.Dispose();

                result.HasChanges = execution.HasChanges;
                result.AppliedGroupCount = execution.AppliedGroupCount;
                if (!execution.HasChanges)
                    return result;

                var sourceAssetsByRenderer =
                    BuildChangedSourceAssets(beforeStates);

                ValidateReplacementMeshes(beforeStates);

                var unsafeAnimationHits = sourceAnimationReferences
                    .Where(hit => hit.TargetRenderers.Any(renderer =>
                        renderer != null
                        && sourceAssetsByRenderer.TryGetValue(
                            renderer, out var changedSources)
                        && changedSources.Contains(
                            hit.ReferencedAsset)))
                    .ToArray();

                if (!AtlasTextureBaker.ValidateAnimationObjectReferenceHits(
                        unsafeAnimationHits,
                        displayDialog: false))
                {
                    throw new InvalidOperationException(
                        "AutoAtlasで置換されるMesh / Material / Textureを直接参照する"
                        + "AnimationClipがあるため、Prefab化を中断しました。"
                        + "該当Clip / bindingはConsoleに出力しています。");
                }

                var descriptors =
                    domain.DownloadedTextureDescriptors.ToDictionary(
                        pair => pair.Key,
                        pair => pair.Value);
                var writeResult = PartAutoAtlasAssetWriter.Write(
                    root,
                    assetStore,
                    descriptors,
                    outputAssetPath);

                result.TextureCount = writeResult.TextureCount;
                result.MaterialCount = writeResult.MaterialCount;
                result.MeshCount = writeResult.MeshCount;
                return result;
            }
            finally
            {
                domain.Dispose();
            }
        }

        private static Dictionary<Renderer, HashSet<Object>>
            BuildChangedSourceAssets(
                IEnumerable<AtlasTextureBaker.RendererBakeState> beforeStates)
        {
            var result =
                new Dictionary<Renderer, HashSet<Object>>();

            foreach (var before in beforeStates)
            {
                var renderer = before.Renderer;
                if (renderer == null)
                    continue;

                var changed = new HashSet<Object>();
                var currentMesh = renderer.GetMesh();
                if (before.Mesh != null && before.Mesh != currentMesh)
                    changed.Add(before.Mesh);

                var currentMaterials = renderer.sharedMaterials;
                var slotCount = Math.Max(
                    before.Materials.Length,
                    currentMaterials.Length);

                for (var slot = 0; slot < slotCount; slot++)
                {
                    var sourceMaterial =
                        slot < before.Materials.Length
                            ? before.Materials[slot]
                            : null;
                    var replacementMaterial =
                        slot < currentMaterials.Length
                            ? currentMaterials[slot]
                            : null;

                    if (sourceMaterial == null)
                        continue;

                    if (sourceMaterial != replacementMaterial)
                        changed.Add(sourceMaterial);

                    var replacementTextures =
                        replacementMaterial == null
                            ? new Dictionary<string, Texture>(
                                StringComparer.Ordinal)
                            : replacementMaterial
                                .GetTextureReferences()
                                .GroupBy(
                                    pair => pair.Key,
                                    StringComparer.Ordinal)
                                .ToDictionary(
                                    group => group.Key,
                                    group => group.Last().Value,
                                    StringComparer.Ordinal);

                    foreach (var sourceTexture in
                             sourceMaterial.GetTextureReferences())
                    {
                        if (sourceTexture.Value == null)
                            continue;

                        if (!replacementTextures.TryGetValue(
                                sourceTexture.Key,
                                out var replacementTexture)
                            || replacementTexture
                                != sourceTexture.Value)
                        {
                            changed.Add(sourceTexture.Value);
                        }
                    }
                }

                if (changed.Count != 0)
                    result[renderer] = changed;
            }

            return result;
        }

        private static void ValidateReplacementMeshes(
            IEnumerable<AtlasTextureBaker.RendererBakeState> beforeStates)
        {
            foreach (var before in beforeStates)
            {
                var renderer = before.Renderer;
                if (renderer == null || before.Mesh == null)
                    continue;

                var replacement = renderer.GetMesh();
                if (replacement == before.Mesh)
                    continue;

                if (replacement == null)
                {
                    throw new InvalidOperationException(
                        "AutoAtlas処理後にMeshが失われました: "
                        + renderer.name);
                }

                if (!AtlasTextureBaker.ValidateReplacementMeshCompatibility(
                        renderer,
                        before.Mesh,
                        replacement))
                {
                    throw new InvalidOperationException(
                        "AutoAtlas処理後のMesh互換性検証に失敗しました: "
                        + renderer.name);
                }
            }
        }
    }

    internal sealed class PartAutoAtlasAssetWriteResult
    {
        internal int TextureCount;
        internal int MaterialCount;
        internal int MeshCount;
    }

    internal static class PartAutoAtlasAssetWriter
    {
        internal static PartAutoAtlasAssetWriteResult Write(
            GameObject root,
            PartAutoAtlasAssetStore store,
            IReadOnlyDictionary<
                Texture2D, TexTransToolTextureDescriptor> textureDescriptors,
            string outputAssetPath)
        {
            var result = new PartAutoAtlasAssetWriteResult();
            var renderers = root.GetComponentsInChildren<Renderer>(true);

            var materials = renderers
                .SelectMany(renderer => renderer.sharedMaterials)
                .Where(material =>
                    material != null
                    && store.GeneratedMaterials.Contains(material))
                .Distinct()
                .ToArray();

            var meshes = renderers
                .Select(renderer => renderer.GetMesh())
                .Where(mesh =>
                    mesh != null
                    && store.GeneratedMeshes.Contains(mesh))
                .Cast<Mesh>()
                .Distinct()
                .ToArray();

            var textures = materials
                .SelectMany(material =>
                    material.GetTextureReferences()
                        .Select(pair => pair.Value))
                .OfType<Texture2D>()
                .Where(store.GeneratedTextures.Contains)
                .Distinct()
                .ToArray();

            if (materials.Length == 0
                && meshes.Length == 0
                && textures.Length == 0)
            {
                return result;
            }

            var textureFolder = outputAssetPath + "/Textures";
            var materialFolder = outputAssetPath + "/Materials";
            var meshFolder = outputAssetPath + "/Meshes";

            Directory.CreateDirectory(
                AtlasTextureBaker.AssetPathToFullPath(outputAssetPath));
            if (textures.Length != 0)
                Directory.CreateDirectory(
                    AtlasTextureBaker.AssetPathToFullPath(textureFolder));
            if (materials.Length != 0)
                Directory.CreateDirectory(
                    AtlasTextureBaker.AssetPathToFullPath(materialFolder));
            if (meshes.Length != 0)
                Directory.CreateDirectory(
                    AtlasTextureBaker.AssetPathToFullPath(meshFolder));
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            foreach (var texture in textures)
            {
                if (!textureDescriptors.TryGetValue(
                        texture, out var descriptor))
                {
                    throw new InvalidOperationException(
                        "AutoAtlas生成TextureのDescriptorを確認できません: "
                        + texture.name);
                }

                var usages = materials
                    .SelectMany(material =>
                        material.GetTextureReferences()
                            .Where(pair => pair.Value == texture)
                            .Select(pair => (
                                Material: material,
                                Property: pair.Key)))
                    .ToArray();
                var properties = usages
                    .Select(usage => usage.Property)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToArray();
                var usageMaterials = usages
                    .Select(usage => usage.Material)
                    .Distinct()
                    .ToArray();

                var materialPrefix =
                    BuildMaterialPrefix(usageMaterials);
                var role =
                    AtlasTextureBaker.BuildTextureDisplayRole(properties);
                var desiredPath =
                    textureFolder + "/"
                    + AtlasTextureBaker.SanitizeFileName(
                        materialPrefix + "_" + role)
                    + ".png";
                var assetPath =
                    AssetDatabase.GenerateUniqueAssetPath(desiredPath);

                var persistent = AtlasTextureBaker.SaveTextureAsset(
                    texture,
                    descriptor,
                    usageMaterials,
                    properties,
                    assetPath);

                foreach (var material in materials)
                {
                    if (material.ReferencesTexture(texture))
                        material.ReplaceTexture(texture, persistent);
                }

                result.TextureCount++;
            }

            foreach (var material in materials)
            {
                if (AssetDatabase.Contains(material))
                    continue;

                var desiredPath =
                    materialFolder + "/"
                    + AtlasTextureBaker.SanitizeFileName(material.name)
                    + ".mat";
                var assetPath =
                    AssetDatabase.GenerateUniqueAssetPath(desiredPath);
                material.name =
                    Path.GetFileNameWithoutExtension(assetPath);
                AssetDatabase.CreateAsset(material, assetPath);
                result.MaterialCount++;
            }

            foreach (var mesh in meshes)
            {
                if (AssetDatabase.Contains(mesh))
                    continue;

                var meshRenderers = renderers
                    .Where(renderer => renderer.GetMesh() == mesh)
                    .ToArray();
                var displayName = meshRenderers.Length == 1
                    ? meshRenderers[0].gameObject.name
                    : mesh.name;
                if (string.IsNullOrWhiteSpace(displayName))
                    displayName = "Mesh";

                var desiredPath =
                    meshFolder + "/"
                    + AtlasTextureBaker.SanitizeFileName(displayName)
                    + ".asset";
                var assetPath =
                    AssetDatabase.GenerateUniqueAssetPath(desiredPath);
                mesh.name =
                    Path.GetFileNameWithoutExtension(assetPath);
                AssetDatabase.CreateAsset(mesh, assetPath);
                result.MeshCount++;
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            return result;
        }

        private static string BuildMaterialPrefix(
            IReadOnlyCollection<Material> materials)
        {
            var names = materials
                .Where(material => material != null)
                .Select(material => material.name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

            if (names.Length == 0)
                return "Atlas";
            if (names.Length == 1)
                return names[0];

            return names[0] + "_Plus" + (names.Length - 1);
        }
    }
}
