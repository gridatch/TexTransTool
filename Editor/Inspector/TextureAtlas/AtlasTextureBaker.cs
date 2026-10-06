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
    internal static class AtlasTextureBaker
    {
        internal static bool Bake(AtlasTexture atlasTexture)
        {
            PreviewUtility.ExitPreviews();

            var bakeName = atlasTexture.BakeName ?? "";
            if (TryValidateBakeName(bakeName, out var bakeNameError) is false)
            {
                EditorUtility.DisplayDialog("TexTransTool", bakeNameError, "OK");
                return false;
            }

            var domainRoot = DomainMarkerFinder.FindMarker(atlasTexture.gameObject);
            if (domainRoot == null)
            {
                Debug.LogError("TexTransTool: AtlasTexture domain root was not found.");
                return false;
            }

            using var resolveDomain = new NotWorkDomain(
                domainRoot.GetComponentsInChildren<Renderer>(true),
                null
            );
            var (targetMaterials, targetRenderers) =
                AtlasTextureBakeTargetResolver.ResolveBakeTargets(atlasTexture, resolveDomain);

            return BakeResolved(
                PersistentAtlasBakeSettings.FromComponent(atlasTexture),
                domainRoot,
                targetMaterials,
                targetRenderers,
                bakeName,
                GetBakeOutputAssetPath(bakeName),
                promptOverwrite: true,
                pingOutputFolder: true
            );
        }

        /// <summary>
        /// Runs the persistent Atlas bake against an explicitly supplied domain and target set.
        /// This is the reusable boundary used by workflows that operate on temporary/extracted
        /// hierarchies rather than the AtlasTexture component's normal avatar domain.
        /// </summary>
        internal static bool BakeResolved(
            PersistentAtlasBakeSettings settings,
            GameObject domainRoot,
            HashSet<Material> targetMaterials,
            Renderer[] targetRenderers,
            string bakeName,
            string outputAssetPath,
            bool promptOverwrite,
            bool pingOutputFolder)
        {
            PreviewUtility.ExitPreviews();

            if (settings == null)
            {
                Debug.LogError("TexTransTool: persistent Atlas bake settings are null.");
                return false;
            }

            if (domainRoot == null)
            {
                Debug.LogError("TexTransTool: AtlasTexture bake domain root is null.");
                return false;
            }

            if (targetMaterials == null || targetRenderers == null)
            {
                Debug.LogError("TexTransTool: AtlasTexture bake target set is null.");
                return false;
            }

            if (TryValidateBakeName(bakeName, out var bakeNameError) is false)
            {
                EditorUtility.DisplayDialog("TexTransTool", bakeNameError, "OK");
                return false;
            }

            if (TryValidateOutputAssetPath(outputAssetPath, out var outputPathError) is false)
            {
                Debug.LogError("TexTransTool: " + outputPathError);
                EditorUtility.DisplayDialog("TexTransTool", outputPathError, "OK");
                return false;
            }

            var manifestAssetPath = outputAssetPath + "/BakeManifest.asset";
            var outputExists = Directory.Exists(AssetPathToFullPath(outputAssetPath));

            AtlasTextureBakeManifest? manifest = null;
            AtlasTextureBakeManifest.Entry[] previousEntries = Array.Empty<AtlasTextureBakeManifest.Entry>();

            if (outputExists)
            {
                if (promptOverwrite
                    && EditorUtility.DisplayDialog(
                        "TexTransTool",
                        $"ベイク名「{bakeName}」は既に存在します。上書きしますか？",
                        "上書き",
                        "キャンセル"
                    ) is false)
                {
                    return false;
                }

                manifest = AssetDatabase.LoadAssetAtPath<AtlasTextureBakeManifest>(manifestAssetPath);
                if (manifest == null)
                {
                    EditorUtility.DisplayDialog(
                        "TexTransTool",
                        $"出力先「{outputAssetPath}」の管理情報が見つからないため、安全に上書きできません。",
                        "OK"
                    );
                    return false;
                }

                previousEntries = manifest.Entries
                    .Where(entry =>
                        entry != null
                        && string.IsNullOrEmpty(entry.Role) is false
                        && string.IsNullOrEmpty(entry.AssetPath) is false
                    )
                    .Select(entry => new AtlasTextureBakeManifest.Entry
                    {
                        Role = entry.Role,
                        AssetPath = entry.AssetPath,
                    })
                    .ToArray();
            }

            var previousRoleAssets = previousEntries
                .GroupBy(entry => entry.Role)
                .ToDictionary(group => group.Key, group => group.Last().AssetPath);

            var textureAssetPath = outputAssetPath + "/Textures";
            var materialAssetPath = outputAssetPath + "/Materials";
            var meshAssetPath = outputAssetPath + "/Meshes";

            using var diskUtil = new UnityDiskUtil(false);
            var engine = new TTCEUnityWithTTT4Unity(diskUtil);

            try
            {
                using var domain = new NotWorkDomain(
                    domainRoot.GetComponentsInChildren<Renderer>(true),
                    engine
                );

                if (targetMaterials.Count == 0 || targetRenderers.Length == 0)
                {
                    Debug.LogWarning("TexTransTool: No AtlasTexture bake target was found.");
                    return false;
                }

                var domainRendererSet = domainRoot
                    .GetComponentsInChildren<Renderer>(true)
                    .ToHashSet();

                if (targetRenderers.Any(renderer => renderer == null || domainRendererSet.Contains(renderer) is false))
                {
                    Debug.LogError("TexTransTool: AtlasTexture bake targets include a renderer outside the supplied domain.");
                    return false;
                }

                var targetMeshes = targetRenderers
                    .Select(renderer => ((IRendererTargeting)domain).GetMesh(renderer))
                    .Where(mesh => mesh != null)
                    .Cast<Mesh>()
                    .ToHashSet();

                if (ValidateAnimationObjectReferences(domainRoot, targetRenderers, targetMaterials, targetMeshes) is false)
                {
                    return false;
                }

                var atlasResult = AtlasTexture.DoAtlasTexture(
                    domain,
                    engine,
                    targetMaterials,
                    targetRenderers,
                    settings.IslandSizePriorityTuner,
                    settings.AtlasSetting
                );

                if (atlasResult.IsSuccess is false) { return false; }

                using var atlasContext = atlasResult.AtlasContext!;
                var atlasedMeshes = atlasResult.AtlasedMeshes!;
                var compiledAtlasTextures = atlasResult.CompiledAtlasTextures!;

                var rendererMeshMap = BuildRendererMeshMap(domain, targetRenderers, atlasContext, atlasedMeshes);
                if (ValidateMeshCompatibility(domain, rendererMeshMap, targetRenderers.Length) is false)
                {
                    return false;
                }

                var experimentalOptions = settings.ExperimentalOptions;
                if (experimentalOptions == null) { experimentalOptions = null; }

                var tunedAtlasTextures = AtlasTexture.DoTextureFinTuning(
                    engine,
                    atlasContext,
                    settings.AtlasSetting,
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
                var currentEntries = new List<AtlasTextureBakeManifest.Entry>();

                Directory.CreateDirectory(AssetPathToFullPath(textureAssetPath));
                Directory.CreateDirectory(AssetPathToFullPath(materialAssetPath));
                Directory.CreateDirectory(AssetPathToFullPath(meshAssetPath));
                AssetDatabase.Refresh();

                try
                {
                    var persistentTextures = SaveTextures(
                        engine,
                        tunedAtlasTextures,
                        bakeName,
                        outputAssetPath,
                        textureAssetPath,
                        previousRoleAssets,
                        currentEntries,
                        temporaryDownloadedTextures
                    );

                    var (materialMap, _) = AtlasTexture.GenerateAtlasedMaterialMaps(
                        domain,
                        targetMaterials,
                        settings.AtlasSetting,
                        (
                            settings.MergeMaterialGroups,
                            settings.AllMaterialMergeReference,
                            experimentalOptions
                        ),
                        persistentTextures,
                        atlasResult.PreserveBump2ndMaterials,
                        atlasResult.PreservedOriginalUVChannel
                    );

                    generatedMaterials.AddRange(materialMap.Values.Distinct());

                    var persistentMaterialMap = SaveMaterials(
                        materialMap,
                        bakeName,
                        outputAssetPath,
                        materialAssetPath,
                        previousRoleAssets,
                        currentEntries
                    );

                    var persistentRendererMeshMap = SaveMeshes(
                        domainRoot,
                        rendererMeshMap,
                        bakeName,
                        outputAssetPath,
                        meshAssetPath,
                        previousRoleAssets,
                        currentEntries
                    );

                    DeleteStaleManagedAssets(previousEntries, currentEntries);
                    manifest = SaveManifest(
                        manifest,
                        manifestAssetPath,
                        bakeName,
                        currentEntries
                    );

                    AssetDatabase.SaveAssets();
                    AssetDatabase.Refresh();

                    ApplyBakedAtlas(
                        domainRoot,
                        targetRenderers,
                        persistentRendererMeshMap,
                        persistentMaterialMap
                    );

                    if (pingOutputFolder)
                    {
                        var folderAsset = AssetDatabase.LoadAssetAtPath<DefaultAsset>(outputAssetPath);
                        if (folderAsset != null)
                        {
                            Selection.activeObject = folderAsset;
                            EditorGUIUtility.PingObject(folderAsset);
                        }
                    }

                    Debug.Log(
                        $"TexTransTool: Atlas baked to persistent assets and applied at {outputAssetPath} " +
                        $"({persistentTextures.Values.Distinct().Count()} textures, " +
                        $"{persistentMaterialMap.Values.Distinct().Count()} materials, " +
                        $"{persistentRendererMeshMap.Values.Distinct().Count()} meshes)."
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

            return true;
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
                    $"TexTransTool: Atlas bake aborted because only {rendererMeshMap.Count} of {expectedRendererCount} target renderers received an atlas mesh."
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
                        $"TexTransTool: Atlas bake aborted because blend shape count changed on {renderer.name}."
                    );
                    return false;
                }

                for (var shapeIndex = 0; shapeIndex < sourceMesh.blendShapeCount; shapeIndex += 1)
                {
                    if (sourceMesh.GetBlendShapeName(shapeIndex) != atlasMesh.GetBlendShapeName(shapeIndex))
                    {
                        Debug.LogError(
                            $"TexTransTool: Atlas bake aborted because blend shape order/name changed on {renderer.name}."
                        );
                        return false;
                    }

                    var sourceFrameCount = sourceMesh.GetBlendShapeFrameCount(shapeIndex);
                    var atlasFrameCount = atlasMesh.GetBlendShapeFrameCount(shapeIndex);
                    if (sourceFrameCount != atlasFrameCount)
                    {
                        Debug.LogError(
                            $"TexTransTool: Atlas bake aborted because blend shape frame count changed on {renderer.name}."
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
                                $"TexTransTool: Atlas bake aborted because blend shape frame weights changed on {renderer.name}."
                            );
                            return false;
                        }
                    }
                }

                if (renderer is SkinnedMeshRenderer
                    && sourceMesh.bindposes.Length != atlasMesh.bindposes.Length)
                {
                    Debug.LogError(
                        $"TexTransTool: Atlas bake aborted because bindpose count changed on {renderer.name}."
                    );
                    return false;
                }
            }

            return true;
        }

        internal static bool ValidateAnimationObjectReferences(
            GameObject domainRoot,
            Renderer[] targetRenderers,
            HashSet<Material> targetMaterials,
            HashSet<Mesh> targetMeshes,
            bool displayDialog = true)
        {
            var hits = new List<string>();
            var dependencies = EditorUtility.CollectDependencies(new UnityEngine.Object[] { domainRoot });

            foreach (var clip in dependencies.OfType<AnimationClip>().Distinct())
            {
                foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                {
                    if (BindingTargetsRenderer(domainRoot, binding, targetRenderers) is false)
                    {
                        continue;
                    }

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
                "TexTransTool: Atlas bake was aborted because AnimationClip object-reference curves " +
                "directly restore one or more source Mesh/Material assets.\n" + detail
            );

            if (displayDialog)
            {
                EditorUtility.DisplayDialog(
                    "TexTransTool",
                    "アトラス化のベイクを中断しました。\n\n" +
                    "ベイク前の Mesh / Material を直接参照する AnimationClip が見つかりました。\n" +
                    "このままベイクすると、アニメーション再生時に旧アセットへ戻る可能性があります。\n\n" +
                    "Console に該当 Clip / binding を出力しています。",
                    "OK"
                );
            }

            return false;
        }

        private static bool BindingTargetsRenderer(
            GameObject domainRoot,
            EditorCurveBinding binding,
            Renderer[] targetRenderers)
        {
            var targetTransform = string.IsNullOrEmpty(binding.path)
                ? domainRoot.transform
                : domainRoot.transform.Find(binding.path);

            if (targetTransform == null) { return false; }

            if (binding.type == typeof(MeshFilter))
            {
                return targetRenderers
                    .OfType<MeshRenderer>()
                    .Any(renderer => renderer.transform == targetTransform);
            }

            if (typeof(Renderer).IsAssignableFrom(binding.type) is false)
            {
                return false;
            }

            return targetRenderers.Any(renderer =>
                renderer.transform == targetTransform
                && binding.type.IsAssignableFrom(renderer.GetType())
            );
        }

        private static void ApplyBakedAtlas(
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
                    "TexTransTool: アトラス化をベイク"
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
            string bakeName,
            string outputAssetPath,
            string textureAssetPath,
            IReadOnlyDictionary<string, string> previousRoleAssets,
            List<AtlasTextureBakeManifest.Entry> currentEntries,
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

                var propertyNames = tunedAtlasTextures.RenderTextures
                    .Where(kv => ReferenceEquals(kv.Value, renderTexture))
                    .Select(kv => kv.Key)
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToArray();

                var role = "Texture:" + string.Join("|", propertyNames);
                var displayProperty = string.Join(
                    "_",
                    propertyNames
                        .Select(NormalizeTexturePropertyName)
                        .Where(name => string.IsNullOrEmpty(name) is false)
                );
                if (string.IsNullOrEmpty(displayProperty)) displayProperty = "Texture";

                var desiredAssetPath =
                    textureAssetPath + "/" +
                    SanitizeFileName(bakeName + "_" + displayProperty) +
                    ".png";

                var assetPath = ResolveManagedAssetPath(
                    role,
                    desiredAssetPath,
                    ".png",
                    outputAssetPath,
                    previousRoleAssets
                );

                var pngBytes = ImageConversion.EncodeToPNG(downloaded);
                File.WriteAllBytes(AssetPathToFullPath(assetPath), pngBytes);
                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);

                ConfigureTextureImporter(assetPath, downloaded, descriptor);

                var persistentTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
                if (persistentTexture == null)
                    throw new InvalidOperationException("Failed to import exported atlas texture: " + assetPath);

                renderTextureToPersistentTexture[renderTexture] = persistentTexture;
                currentEntries.Add(new AtlasTextureBakeManifest.Entry
                {
                    Role = role,
                    AssetPath = assetPath,
                });
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

        private static Dictionary<Material, Material> SaveMaterials(
            IReadOnlyDictionary<Material, Material> materialMap,
            string bakeName,
            string outputAssetPath,
            string materialAssetPath,
            IReadOnlyDictionary<string, string> previousRoleAssets,
            List<AtlasTextureBakeManifest.Entry> currentEntries)
        {
            var generatedToPersistent = new Dictionary<Material, Material>();

            foreach (var group in materialMap.GroupBy(pair => pair.Value))
            {
                var generatedMaterial = group.Key;
                var sourceKeys = group
                    .Select(pair => GetStableMaterialKey(pair.Key))
                    .OrderBy(key => key, StringComparer.Ordinal)
                    .ToArray();

                var role = "Material:" + string.Join("|", sourceKeys);
                var desiredAssetPath =
                    materialAssetPath + "/" +
                    SanitizeFileName(bakeName + "_" + generatedMaterial.name) +
                    ".mat";

                var assetPath = ResolveManagedAssetPath(
                    role,
                    desiredAssetPath,
                    ".mat",
                    outputAssetPath,
                    previousRoleAssets
                );

                var assetObjectName = Path.GetFileNameWithoutExtension(assetPath);
                var existingMaterial = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
                Material persistentMaterial;

                if (existingMaterial != null)
                {
                    EditorUtility.CopySerialized(generatedMaterial, existingMaterial);
                    existingMaterial.name = assetObjectName;
                    EditorUtility.SetDirty(existingMaterial);
                    persistentMaterial = existingMaterial;
                }
                else
                {
                    var existingAsset = AssetDatabase.LoadMainAssetAtPath(assetPath);
                    if (existingAsset != null)
                    {
                        throw new InvalidOperationException(
                            $"Cannot update Atlas bake material because another asset exists at {assetPath}."
                        );
                    }

                    generatedMaterial.name = assetObjectName;
                    AssetDatabase.CreateAsset(generatedMaterial, assetPath);
                    persistentMaterial = generatedMaterial;
                }

                generatedToPersistent[generatedMaterial] = persistentMaterial;
                currentEntries.Add(new AtlasTextureBakeManifest.Entry
                {
                    Role = role,
                    AssetPath = assetPath,
                });
            }

            return materialMap.ToDictionary(
                pair => pair.Key,
                pair => generatedToPersistent[pair.Value]
            );
        }

        private static Dictionary<Renderer, Mesh> SaveMeshes(
            GameObject domainRoot,
            IReadOnlyDictionary<Renderer, Mesh> rendererMeshMap,
            string bakeName,
            string outputAssetPath,
            string meshAssetPath,
            IReadOnlyDictionary<string, string> previousRoleAssets,
            List<AtlasTextureBakeManifest.Entry> currentEntries)
        {
            var generatedToPersistent = new Dictionary<Mesh, Mesh>();

            foreach (var group in rendererMeshMap.GroupBy(pair => pair.Value))
            {
                var generatedMesh = group.Key;
                var renderers = group.Select(pair => pair.Key).ToArray();
                var rendererKeys = renderers
                    .Select(renderer => GetStableRendererKey(domainRoot, renderer))
                    .OrderBy(key => key, StringComparer.Ordinal)
                    .ToArray();

                var role = "Mesh:" + string.Join("|", rendererKeys);
                var displayName = renderers.Length == 1
                    ? renderers[0].gameObject.name
                    : generatedMesh.name;

                if (string.IsNullOrWhiteSpace(displayName))
                    displayName = "Mesh";

                var desiredAssetPath =
                    meshAssetPath + "/" +
                    SanitizeFileName(bakeName + "_" + displayName) +
                    ".asset";

                var assetPath = ResolveManagedAssetPath(
                    role,
                    desiredAssetPath,
                    ".asset",
                    outputAssetPath,
                    previousRoleAssets
                );

                var assetObjectName = Path.GetFileNameWithoutExtension(assetPath);
                var existingMesh = AssetDatabase.LoadAssetAtPath<Mesh>(assetPath);
                Mesh persistentMesh;

                if (existingMesh != null)
                {
                    EditorUtility.CopySerialized(generatedMesh, existingMesh);
                    existingMesh.name = assetObjectName;
                    EditorUtility.SetDirty(existingMesh);
                    persistentMesh = existingMesh;
                }
                else
                {
                    var existingAsset = AssetDatabase.LoadMainAssetAtPath(assetPath);
                    if (existingAsset != null)
                    {
                        throw new InvalidOperationException(
                            $"Cannot update Atlas bake mesh because another asset exists at {assetPath}."
                        );
                    }

                    generatedMesh.name = assetObjectName;
                    AssetDatabase.CreateAsset(generatedMesh, assetPath);
                    persistentMesh = generatedMesh;
                }

                generatedToPersistent[generatedMesh] = persistentMesh;
                currentEntries.Add(new AtlasTextureBakeManifest.Entry
                {
                    Role = role,
                    AssetPath = assetPath,
                });
            }

            return rendererMeshMap.ToDictionary(
                pair => pair.Key,
                pair => generatedToPersistent[pair.Value]
            );
        }

        private static AtlasTextureBakeManifest SaveManifest(
            AtlasTextureBakeManifest? manifest,
            string manifestAssetPath,
            string bakeName,
            IEnumerable<AtlasTextureBakeManifest.Entry> entries)
        {
            var isNew = manifest == null;
            manifest ??= ScriptableObject.CreateInstance<AtlasTextureBakeManifest>();

            manifest.name = Path.GetFileNameWithoutExtension(manifestAssetPath);
            manifest.Version = AtlasTextureBakeManifest.CurrentVersion;
            manifest.BakeName = bakeName;
            manifest.Entries = entries
                .OrderBy(entry => entry.Role, StringComparer.Ordinal)
                .Select(entry => new AtlasTextureBakeManifest.Entry
                {
                    Role = entry.Role,
                    AssetPath = entry.AssetPath,
                })
                .ToList();

            if (isNew)
                AssetDatabase.CreateAsset(manifest, manifestAssetPath);
            else
                EditorUtility.SetDirty(manifest);

            return manifest;
        }

        private static void DeleteStaleManagedAssets(
            IEnumerable<AtlasTextureBakeManifest.Entry> previousEntries,
            IEnumerable<AtlasTextureBakeManifest.Entry> currentEntries)
        {
            var current = currentEntries.ToArray();
            var currentPaths = new HashSet<string>(
                current.Select(entry => entry.AssetPath),
                StringComparer.Ordinal
            );

            foreach (var previous in previousEntries)
            {
                var matchingRole = current.FirstOrDefault(entry => entry.Role == previous.Role);
                if (matchingRole != null && matchingRole.AssetPath == previous.AssetPath)
                    continue;

                if (currentPaths.Contains(previous.AssetPath))
                    continue;

                if (string.IsNullOrEmpty(previous.AssetPath) is false)
                    AssetDatabase.DeleteAsset(previous.AssetPath);
            }
        }

        private static string ResolveManagedAssetPath(
            string role,
            string desiredAssetPath,
            string expectedExtension,
            string outputAssetPath,
            IReadOnlyDictionary<string, string> previousRoleAssets)
        {
            if (previousRoleAssets.TryGetValue(role, out var previousAssetPath)
                && IsPathInside(previousAssetPath, outputAssetPath)
                && string.Equals(
                    Path.GetExtension(previousAssetPath),
                    expectedExtension,
                    StringComparison.OrdinalIgnoreCase
                ))
            {
                return previousAssetPath;
            }

            return AssetDatabase.GenerateUniqueAssetPath(desiredAssetPath);
        }

        private static bool IsPathInside(string assetPath, string folderAssetPath)
        {
            var normalizedFolder = folderAssetPath.TrimEnd('/') + "/";
            return assetPath.StartsWith(normalizedFolder, StringComparison.Ordinal);
        }

        private static string GetStableMaterialKey(Material material)
        {
            if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
                    material,
                    out var guid,
                    out long localId
                )
                && string.IsNullOrEmpty(guid) is false)
            {
                return guid + ":" + localId;
            }

            return material.name + "|" + (material.shader != null ? material.shader.name : "");
        }

        private static string GetStableRendererKey(GameObject domainRoot, Renderer renderer)
        {
            var path = AnimationUtility.CalculateTransformPath(
                renderer.transform,
                domainRoot.transform
            );

            var renderers = renderer.GetComponents<Renderer>();
            var componentIndex = Array.IndexOf(renderers, renderer);

            return path
                + "|"
                + renderer.GetType().FullName
                + "|"
                + componentIndex;
        }

        private static string NormalizeTexturePropertyName(string propertyName)
        {
            var normalized = propertyName.TrimStart('_');
            return string.IsNullOrEmpty(normalized) ? propertyName : normalized;
        }

        internal static bool TryValidateBakeName(string bakeName, out string error)
        {
            if (string.IsNullOrWhiteSpace(bakeName))
            {
                error = "ベイク名を入力してください。";
                return false;
            }

            if (bakeName != bakeName.Trim())
            {
                error = "ベイク名の先頭または末尾に空白は使用できません。";
                return false;
            }

            if (bakeName is "." or "..")
            {
                error = "このベイク名は使用できません。";
                return false;
            }

            var invalidChars = new HashSet<char>(
                Path.GetInvalidFileNameChars()
                    .Concat(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' })
            );

            if (bakeName.Any(invalidChars.Contains) || bakeName.EndsWith(".", StringComparison.Ordinal))
            {
                error = "ベイク名にファイル名として使用できない文字が含まれています。";
                return false;
            }

            if (bakeName.Length > 120)
            {
                error = "ベイク名は120文字以内で入力してください。";
                return false;
            }

            error = "";
            return true;
        }

        private static string GetBakeOutputAssetPath(string bakeName)
        {
            return "Assets/TexTransToolGenerated/AtlasTexture/"
                + bakeName;
        }

        private static bool TryValidateOutputAssetPath(string outputAssetPath, out string error)
        {
            if (string.IsNullOrWhiteSpace(outputAssetPath)
                || outputAssetPath == "Assets"
                || outputAssetPath.StartsWith("Assets/", StringComparison.Ordinal) is false)
            {
                error = "Atlas bake output path must be a folder below Assets/.";
                return false;
            }

            if (outputAssetPath.IndexOfAny(new[] { '\\', ':', '*', '?', '"', '<', '>', '|' }) >= 0)
            {
                error = "Atlas bake output path contains an invalid character.";
                return false;
            }

            error = "";
            return true;
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
