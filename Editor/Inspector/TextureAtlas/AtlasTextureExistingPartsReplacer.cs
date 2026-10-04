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
    internal static class AtlasTextureExistingPartsReplacer
    {
        internal static void ReplaceExistingParts(AtlasTexture atlasTexture)
        {
            PreviewUtility.ExitPreviews();

            var domainRoot = DomainMarkerFinder.FindMarker(atlasTexture.gameObject);
            if (domainRoot == null)
            {
                Debug.LogError("TexTransTool: AtlasTexture domain root was not found.");
                return;
            }

            var outputAssetPath = GetExistingPartsOutputAssetPath(domainRoot, atlasTexture);

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

                var (targetMaterials, targetRenderers) = atlasTexture.ResolveAtlasTargets(
                    domain,
                    domain.EnumerateRenderer()
                );

                if (targetMaterials.Count == 0 || targetRenderers.Length == 0)
                {
                    Debug.LogWarning("TexTransTool: No AtlasTexture bake target was found.");
                    return;
                }

                var targetMeshes = targetRenderers
                    .Select(renderer => ((IRendererTargeting)domain).GetMesh(renderer))
                    .Where(mesh => mesh != null)
                    .Cast<Mesh>()
                    .ToHashSet();

                if (ValidateAnimationObjectReferences(domainRoot, targetMaterials, targetMeshes) is false)
                {
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

                var rendererMeshMap = BuildRendererMeshMap(domain, targetRenderers, atlasContext, atlasedMeshes);
                if (ValidateMeshCompatibility(domain, rendererMeshMap, targetRenderers.Length) is false)
                {
                    return;
                }

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

                    ApplyExistingPartsReplacement(
                        domainRoot,
                        targetRenderers,
                        rendererMeshMap,
                        materialMap
                    );

                    var folderAsset = AssetDatabase.LoadAssetAtPath<DefaultAsset>(outputAssetPath);
                    if (folderAsset != null)
                    {
                        Selection.activeObject = folderAsset;
                        EditorGUIUtility.PingObject(folderAsset);
                    }

                    Debug.Log(
                        $"TexTransTool: Existing parts replaced with persistent atlas assets at {outputAssetPath} " +
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

        private static Dictionary<Renderer, Mesh> BuildRendererMeshMap(
            IDomain domain,
            Renderer[] targetRenderers,
            AtlasContext atlasContext,
            Mesh[] atlasedMeshes)
        {
            var rendererMeshMap = new Dictionary<Renderer, Mesh>();

            foreach (var renderer in targetRenderers)
            {
                var mesh = domain.GetMesh(renderer);
                if (mesh == null) { continue; }
                if (atlasContext.NormalizedMeshCtx.Origin2NormalizedMesh.ContainsKey(mesh) is false) { continue; }

                var normalizedMesh = atlasContext.NormalizedMeshCtx.Origin2NormalizedMesh[mesh];
                var meshID = atlasContext.NormalizedMeshCtx.Normalized2MeshID[normalizedMesh];
                var matIDs = domain.GetMaterials(renderer)
                    .Select(m => m != null ? atlasContext.MaterialGroupingCtx.GetMaterialGroupID(m) : -1)
                    .ToArray();

                var subSet = new AtlasSubMeshIndexID?[matIDs.Length];
                for (var i = 0; i < subSet.Length; i += 1)
                {
                    var matID = matIDs[i];
                    if (matID is -1) { subSet[i] = null; }
                    else { subSet[i] = new AtlasSubMeshIndexID(meshID, i, matID); }
                }

                var identicalSubSetID = atlasContext.AtlasSubMeshIndexSetCtx.AtlasSubSets.FindIndex(candidate =>
                {
                    if (candidate.Length == subSet.Length && candidate.SequenceEqual(subSet)) { return true; }
                    if (AtlasSubMeshIndexIDSetContext.SubPartEqual(candidate, subSet) is false) { return false; }
                    return candidate.Length >= subSet.Length;
                });

                if (identicalSubSetID is -1) { continue; }

                rendererMeshMap[renderer] = atlasedMeshes[identicalSubSetID];
            }

            return rendererMeshMap;
        }

        private static bool ValidateMeshCompatibility(
            IDomain domain,
            IReadOnlyDictionary<Renderer, Mesh> rendererMeshMap,
            int expectedRendererCount)
        {
            if (rendererMeshMap.Count != expectedRendererCount)
            {
                Debug.LogError(
                    $"TexTransTool: Existing-parts replacement aborted because only {rendererMeshMap.Count} of {expectedRendererCount} target renderers received an atlas mesh."
                );
                return false;
            }

            foreach (var pair in rendererMeshMap)
            {
                var renderer = pair.Key;
                var atlasMesh = pair.Value;
                var sourceMesh = domain.GetMesh(renderer);
                if (sourceMesh == null) { continue; }

                if (sourceMesh.blendShapeCount != atlasMesh.blendShapeCount)
                {
                    Debug.LogError(
                        $"TexTransTool: Existing-parts replacement aborted because blend shape count changed on {renderer.name}."
                    );
                    return false;
                }

                for (var shapeIndex = 0; shapeIndex < sourceMesh.blendShapeCount; shapeIndex += 1)
                {
                    if (sourceMesh.GetBlendShapeName(shapeIndex) != atlasMesh.GetBlendShapeName(shapeIndex))
                    {
                        Debug.LogError(
                            $"TexTransTool: Existing-parts replacement aborted because blend shape order/name changed on {renderer.name}."
                        );
                        return false;
                    }

                    var sourceFrameCount = sourceMesh.GetBlendShapeFrameCount(shapeIndex);
                    var atlasFrameCount = atlasMesh.GetBlendShapeFrameCount(shapeIndex);
                    if (sourceFrameCount != atlasFrameCount)
                    {
                        Debug.LogError(
                            $"TexTransTool: Existing-parts replacement aborted because blend shape frame count changed on {renderer.name}."
                        );
                        return false;
                    }

                    for (var frameIndex = 0; frameIndex < sourceFrameCount; frameIndex += 1)
                    {
                        if (Mathf.Approximately(
                                sourceMesh.GetBlendShapeFrameWeight(shapeIndex, frameIndex),
                                atlasMesh.GetBlendShapeFrameWeight(shapeIndex, frameIndex)
                            ) is false)
                        {
                            Debug.LogError(
                                $"TexTransTool: Existing-parts replacement aborted because blend shape frame weights changed on {renderer.name}."
                            );
                            return false;
                        }
                    }
                }

                if (renderer is SkinnedMeshRenderer
                    && sourceMesh.bindposes.Length != atlasMesh.bindposes.Length)
                {
                    Debug.LogError(
                        $"TexTransTool: Existing-parts replacement aborted because bindpose count changed on {renderer.name}."
                    );
                    return false;
                }
            }

            return true;
        }

        private static bool ValidateAnimationObjectReferences(
            GameObject domainRoot,
            HashSet<Material> targetMaterials,
            HashSet<Mesh> targetMeshes)
        {
            var hits = new List<string>();
            var dependencies = EditorUtility.CollectDependencies(new UnityEngine.Object[] { domainRoot });

            foreach (var clip in dependencies.OfType<AnimationClip>().Distinct())
            {
                foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                {
                    var keyframes = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                    var referencedSourceAsset = keyframes
                        .Select(frame => frame.value)
                        .FirstOrDefault(value =>
                            value is Material material && targetMaterials.Contains(material)
                            || value is Mesh mesh && targetMeshes.Contains(mesh)
                        );

                    if (referencedSourceAsset != null)
                    {
                        hits.Add(
                            $"{clip.name}: {binding.path} / {binding.propertyName} -> " +
                            $"{referencedSourceAsset.GetType().Name} {referencedSourceAsset.name}"
                        );
                    }
                }
            }

            if (hits.Count == 0) { return true; }

            var detail = string.Join("\n", hits.Take(20));
            if (hits.Count > 20) detail += $"\n... and {hits.Count - 20} more";

            Debug.LogError(
                "TexTransTool: Existing-parts replacement was aborted because AnimationClip object-reference curves " +
                "directly restore one or more source Mesh/Material assets.\n" + detail
            );

            EditorUtility.DisplayDialog(
                "TexTransTool",
                "既存パーツ差し替えを中断しました。\n\n" +
                "差し替え元の Mesh / Material を直接参照する AnimationClip が見つかりました。\n" +
                "このまま差し替えると、アニメーション再生時に旧アセットへ戻る可能性があります。\n\n" +
                "Console に該当 Clip / binding を出力しています。",
                "OK"
            );

            return false;
        }

        private static void ApplyExistingPartsReplacement(
            GameObject domainRoot,
            Renderer[] targetRenderers,
            IReadOnlyDictionary<Renderer, Mesh> rendererMeshMap,
            IReadOnlyDictionary<Material, Material> materialMap)
        {
            var undoObjects = new HashSet<UnityEngine.Object>();

            foreach (var renderer in targetRenderers)
            {
                if (renderer == null) { continue; }

                undoObjects.Add(renderer);

                if (renderer is MeshRenderer)
                {
                    var meshFilter = renderer.GetComponent<MeshFilter>();
                    if (meshFilter != null) undoObjects.Add(meshFilter);
                }
            }

            if (undoObjects.Count != 0)
            {
                Undo.RecordObjects(
                    undoObjects.ToArray(),
                    "TexTransTool: 既存パーツ差し替え"
                );
            }

            foreach (var renderer in targetRenderers)
            {
                if (renderer == null) { continue; }

                if (rendererMeshMap.TryGetValue(renderer, out var atlasMesh))
                {
                    switch (renderer)
                    {
                        case SkinnedMeshRenderer skinnedMeshRenderer:
                            skinnedMeshRenderer.sharedMesh = atlasMesh;
                            EditorUtility.SetDirty(skinnedMeshRenderer);
                            RecordPrefabOverride(skinnedMeshRenderer);
                            break;

                        case MeshRenderer _:
                            var meshFilter = renderer.GetComponent<MeshFilter>();
                            if (meshFilter != null)
                            {
                                meshFilter.sharedMesh = atlasMesh;
                                EditorUtility.SetDirty(meshFilter);
                                RecordPrefabOverride(meshFilter);
                            }
                            break;
                    }
                }

                var currentMaterials = renderer.sharedMaterials;
                renderer.sharedMaterials = currentMaterials
                    .Select(material =>
                        material != null && materialMap.TryGetValue(material, out var replacement)
                            ? replacement
                            : material
                    )
                    .ToArray();

                EditorUtility.SetDirty(renderer);
                RecordPrefabOverride(renderer);
            }

            if (domainRoot.scene.IsValid())
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(domainRoot.scene);

            static void RecordPrefabOverride(UnityEngine.Object obj)
            {
                if (PrefabUtility.IsPartOfPrefabInstance(obj))
                    PrefabUtility.RecordPrefabInstancePropertyModifications(obj);
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

        private static string GetExistingPartsOutputAssetPath(
            GameObject domainRoot,
            AtlasTexture atlasTexture)
        {
            return "Assets/TexTransToolGenerated/AtlasTexture/"
                + SanitizeFileName(domainRoot.name)
                + "/"
                + SanitizeFileName(atlasTexture.gameObject.name);
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
