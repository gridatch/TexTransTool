#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using net.rs64.TexTransCore;
using net.rs64.TexTransCoreEngineForUnity;
using net.rs64.TexTransTool.Editor.OtherMenuItem;
using UnityEditor;
using UnityEngine;

namespace net.rs64.TexTransTool.TextureAtlas.Editor
{
    internal static class AtlasTextureAssetExporter
    {
        internal static void Export(AtlasTexture atlasTexture)
        {
            PreviewUtility.ExitPreviews();

            var domainRoot = DomainMarkerFinder.FindMarker(atlasTexture.gameObject);
            if (domainRoot == null)
            {
                Debug.LogError("TexTransTool: AtlasTexture domain root was not found.");
                return;
            }

            var selectedFolder = EditorUtility.OpenFolderPanel(
                "Export Atlas Assets",
                Application.dataPath,
                atlasTexture.gameObject.name + "_Atlas"
            );
            if (string.IsNullOrEmpty(selectedFolder)) { return; }

            var outputAssetPath = ToAssetPath(selectedFolder);
            if (outputAssetPath == null)
            {
                EditorUtility.DisplayDialog(
                    "TexTransTool",
                    "The export destination must be inside this project's Assets folder.",
                    "OK"
                );
                return;
            }

            var textureAssetPath = outputAssetPath + "/Textures";
            var materialAssetPath = outputAssetPath + "/Materials";
            var meshAssetPath = outputAssetPath + "/Meshes";

            Directory.CreateDirectory(AssetPathToFullPath(textureAssetPath));
            Directory.CreateDirectory(AssetPathToFullPath(materialAssetPath));
            Directory.CreateDirectory(AssetPathToFullPath(meshAssetPath));
            AssetDatabase.Refresh();

            using var diskUtil = new UnityDiskUtil(false);
            var engine = new TTCEUnityWithTTT4Unity(diskUtil);

            try
            {
                using var domain = new NotWorkDomain(
                    domainRoot.GetComponentsInChildren<Renderer>(true),
                    engine
                );

                var nowRenderers = AtlasTexture.GetAtlasAllowedRenderers(
                    domain,
                    domain.EnumerateRenderer(),
                    atlasTexture.AtlasSetting.IncludeDisabledRenderer
                );

                var targetMaterials = atlasTexture.GetTargetMaterials(domain, nowRenderers).ToHashSet();
                var targetRenderers = AtlasTexture.FilterTargetRenderers(domain, nowRenderers, targetMaterials);
                targetRenderers = AtlasTexture.FilterExistUVChannel(
                    domain,
                    targetRenderers,
                    atlasTexture.AtlasSetting.AtlasTargetUVChannel
                );

                if (targetMaterials.Count == 0 || targetRenderers.Length == 0)
                {
                    Debug.LogWarning("TexTransTool: No AtlasTexture export target was found.");
                    return;
                }

                var atlasResult = AtlasTexture.DoAtlasTexture(
                    domain,
                    engine,
                    targetMaterials,
                    targetRenderers,
                    atlasTexture.IslandSizePriorityTuner,
                    atlasTexture.AtlasSetting
                );

                if (atlasResult.IsSuccess is false) { return; }

                using var atlasContext = atlasResult.AtlasContext!;
                var atlasedMeshes = atlasResult.AtlasedMeshes!;
                var compiledAtlasTextures = atlasResult.CompiledAtlasTextures!;

                var experimentalOptions = atlasTexture.GetComponent<AtlasTextureExperimentalFeature>();
                if (experimentalOptions == null) { experimentalOptions = null; }

                var tunedAtlasTextures = AtlasTexture.DoTextureFinTuning(
                    engine,
                    atlasContext,
                    atlasTexture.AtlasSetting,
                    compiledAtlasTextures,
                    experimentalOptions
                );

                var allRenderTextures = compiledAtlasTextures.Values
                    .Concat(tunedAtlasTextures.RenderTextures.Values)
                    .Concat(tunedAtlasTextures.TextureDescriptors.Keys)
                    .Distinct()
                    .ToArray();

                var temporaryDownloadedTextures = new List<Texture2D>();
                var generatedMaterials = new List<Material>();

                try
                {
                    var persistentTextures = SaveTextures(
                        engine,
                        tunedAtlasTextures,
                        textureAssetPath,
                        temporaryDownloadedTextures
                    );

                    var (materialMap, _) = AtlasTexture.GenerateAtlasedMaterialMaps(
                        domain,
                        targetMaterials,
                        atlasTexture.AtlasSetting,
                        (
                            atlasTexture.MergeMaterialGroups,
                            atlasTexture.AllMaterialMergeReference,
                            experimentalOptions
                        ),
                        persistentTextures,
                        atlasResult.PreserveBump2ndMaterials,
                        atlasResult.PreservedOriginalUVChannel
                    );

                    generatedMaterials.AddRange(materialMap.Values.Distinct());
                    SaveMaterials(generatedMaterials, materialAssetPath);
                    SaveMeshes(atlasedMeshes, meshAssetPath);

                    AssetDatabase.SaveAssets();
                    AssetDatabase.Refresh();

                    var folderAsset = AssetDatabase.LoadAssetAtPath<DefaultAsset>(outputAssetPath);
                    if (folderAsset != null)
                    {
                        Selection.activeObject = folderAsset;
                        EditorGUIUtility.PingObject(folderAsset);
                    }

                    Debug.Log(
                        $"TexTransTool: Atlas assets exported to {outputAssetPath} " +
                        $"({persistentTextures.Values.Distinct().Count()} textures, " +
                        $"{generatedMaterials.Distinct().Count()} materials, " +
                        $"{atlasedMeshes.Length} meshes)."
                    );
                }
                finally
                {
                    foreach (var texture in temporaryDownloadedTextures)
                    {
                        if (texture != null) UnityEngine.Object.DestroyImmediate(texture);
                    }

                    foreach (var material in generatedMaterials)
                    {
                        if (material != null && AssetDatabase.Contains(material) is false)
                            UnityEngine.Object.DestroyImmediate(material);
                    }

                    foreach (var mesh in atlasedMeshes)
                    {
                        if (mesh != null && AssetDatabase.Contains(mesh) is false)
                            UnityEngine.Object.DestroyImmediate(mesh);
                    }

                    foreach (var rt in allRenderTextures)
                    {
                        rt.Dispose();
                    }
                }
            }
            finally
            {
                if (engine is IDisposable disposableEngine) disposableEngine.Dispose();
            }
        }

        private static Dictionary<string, Texture2D> SaveTextures(
            ITexTransToolForUnity engine,
            FineTuning.TexFineTuningResult tunedAtlasTextures,
            string textureAssetPath,
            List<Texture2D> temporaryDownloadedTextures)
        {
            var renderTextureToPersistentTexture = new Dictionary<ITTRenderTexture, Texture2D>();

            foreach (var renderTexture in tunedAtlasTextures.RenderTextures.Values.Distinct())
            {
                if (tunedAtlasTextures.TextureDescriptors.TryGetValue(renderTexture, out var descriptor) is false)
                    descriptor = new TexTransToolTextureDescriptor();

                var downloaded = engine.DownloadToTexture2D(
                    renderTexture,
                    descriptor.UseMipMap,
                    null,
                    descriptor.AsLinear
                );
                temporaryDownloadedTextures.Add(downloaded);

                descriptor.WriteFillWarp(downloaded);
                downloaded.Apply(descriptor.UseMipMap, false);

                var propertyName = tunedAtlasTextures.RenderTextures
                    .First(kv => ReferenceEquals(kv.Value, renderTexture))
                    .Key;
                var fileName = SanitizeFileName("AtlasTex" + propertyName) + ".png";
                var assetPath = AssetDatabase.GenerateUniqueAssetPath(textureAssetPath + "/" + fileName);

                var pngBytes = ImageConversion.EncodeToPNG(downloaded);
                File.WriteAllBytes(AssetPathToFullPath(assetPath), pngBytes);
                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);

                ConfigureTextureImporter(assetPath, downloaded, descriptor);

                var persistentTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
                if (persistentTexture == null)
                    throw new InvalidOperationException("Failed to import exported atlas texture: " + assetPath);

                renderTextureToPersistentTexture[renderTexture] = persistentTexture;
            }

            return tunedAtlasTextures.RenderTextures.ToDictionary(
                kv => kv.Key,
                kv => renderTextureToPersistentTexture[kv.Value]
            );
        }

        private static void ConfigureTextureImporter(
            string assetPath,
            Texture2D downloaded,
            TexTransToolTextureDescriptor descriptor)
        {
            if (AssetImporter.GetAtPath(assetPath) is not TextureImporter importer) { return; }

            importer.textureType = descriptor.IsNormalMap
                ? TextureImporterType.NormalMap
                : TextureImporterType.Default;
            importer.sRGBTexture = descriptor.AsLinear is false;
            importer.mipmapEnabled = descriptor.UseMipMap;
            importer.filterMode = descriptor.filterMode;
            importer.anisoLevel = descriptor.anisoLevel;
            importer.mipMapBias = descriptor.mipMapBias;
            importer.wrapModeU = descriptor.wrapModeU;
            importer.wrapModeV = descriptor.wrapModeV;
            importer.wrapModeW = descriptor.wrapModeW;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.maxTextureSize = Mathf.Clamp(
                Mathf.NextPowerOfTwo(Mathf.Max(downloaded.width, downloaded.height)),
                32,
                8192
            );

            var (textureFormat, compressionQuality) = descriptor.TextureFormat.Get(downloaded);
            importer.compressionQuality = compressionQuality;

            if (descriptor.TextureFormat is TextureCompressionData compressionData)
            {
                importer.textureCompression = compressionData.FormatQualityValue switch
                {
                    FormatQuality.None => TextureImporterCompression.Uncompressed,
                    FormatQuality.Low => TextureImporterCompression.CompressedLQ,
                    FormatQuality.Normal => TextureImporterCompression.Compressed,
                    FormatQuality.High => TextureImporterCompression.CompressedHQ,
                    _ => TextureImporterCompression.Compressed,
                };

                if (compressionData.UseOverride)
                    ApplyExplicitTextureFormat(importer, textureFormat, compressionQuality);
            }
            else
            {
                ApplyExplicitTextureFormat(importer, textureFormat, compressionQuality);
            }

            importer.SaveAndReimport();

            static void ApplyExplicitTextureFormat(
                TextureImporter importer,
                TextureFormat textureFormat,
                int compressionQuality)
            {
                if (Enum.TryParse(textureFormat.ToString(), out TextureImporterFormat importerFormat) is false)
                    return;

                var platformSettings = importer.GetDefaultPlatformTextureSettings();
                platformSettings.maxTextureSize = importer.maxTextureSize;
                platformSettings.compressionQuality = compressionQuality;
                platformSettings.format = importerFormat;
                importer.SetPlatformTextureSettings(platformSettings);
            }
        }

        private static void SaveMaterials(IEnumerable<Material> materials, string materialAssetPath)
        {
            foreach (var material in materials.Distinct())
            {
                var fileName = SanitizeFileName(material.name) + ".mat";
                var assetPath = AssetDatabase.GenerateUniqueAssetPath(materialAssetPath + "/" + fileName);
                AssetDatabase.CreateAsset(material, assetPath);
            }
        }

        private static void SaveMeshes(IEnumerable<Mesh> meshes, string meshAssetPath)
        {
            foreach (var mesh in meshes.Distinct())
            {
                var fileName = SanitizeFileName(mesh.name) + ".asset";
                var assetPath = AssetDatabase.GenerateUniqueAssetPath(meshAssetPath + "/" + fileName);
                AssetDatabase.CreateAsset(mesh, assetPath);
            }
        }

        private static string? ToAssetPath(string fullPath)
        {
            var normalized = Path.GetFullPath(fullPath).Replace('\\', '/').TrimEnd('/');
            var assets = Path.GetFullPath(Application.dataPath).Replace('\\', '/').TrimEnd('/');

            if (normalized.Equals(assets, StringComparison.OrdinalIgnoreCase))
                return "Assets";

            if (normalized.StartsWith(assets + "/", StringComparison.OrdinalIgnoreCase) is false)
                return null;

            return "Assets" + normalized.Substring(assets.Length);
        }

        private static string AssetPathToFullPath(string assetPath)
        {
            var projectRoot = Directory.GetParent(Application.dataPath)!.FullName;
            return Path.GetFullPath(Path.Combine(projectRoot, assetPath));
        }

        private static string SanitizeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var chars = name.Select(c => invalid.Contains(c) || c == '/' || c == '\\' ? '_' : c).ToArray();
            var sanitized = new string(chars).Trim();

            if (string.IsNullOrEmpty(sanitized)) sanitized = "AtlasAsset";
            if (sanitized.Length > 120) sanitized = sanitized.Substring(0, 120);

            return sanitized;
        }
    }
}
