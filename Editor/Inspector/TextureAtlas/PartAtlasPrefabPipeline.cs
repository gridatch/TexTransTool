#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace net.rs64.TexTransTool.TextureAtlas.Editor
{
    internal sealed class PartAtlasPrefabRequest
    {
        internal GameObject SourcePrefabAsset = null!;
        internal string ExtractionRootPath = "";
        internal readonly HashSet<string> KeepRendererPaths = new(StringComparer.Ordinal);
        internal MatsukawaOptions ExtractionOptions = new();
        internal AtlasTexture AtlasSettings = null!;
        internal string OutputName = "";
        internal bool OverwriteExistingOutput;
    }

    internal sealed class PartAtlasPrefabResult
    {
        internal bool Success;
        internal string Error = "";
        internal string OutputFolder = "";
        internal string AtlasOutputFolder = "";
        internal string PrefabPath = "";
        internal string ReportText = "";
    }

    /// <summary>
    /// Backend pipeline for the Prefab workflow.
    /// Input is a Prefab asset; all destructive extraction work is performed in a temporary
    /// additive scene. Matsukawa owns dependency analysis/pruning, while TTT owns atlasing
    /// and persistent Atlas assets.
    /// </summary>
    internal static class PartAtlasPrefabPipeline
    {
        internal static PartAtlasPrefabResult Execute(PartAtlasPrefabRequest request)
        {
            var result = new PartAtlasPrefabResult();

            if (request == null)
                return Fail(result, "Part Atlas Prefab request is null.");

            if (!MatsukawaAdapter.TryCreate(out var matsukawa, out var adapterError) || matsukawa == null)
                return Fail(result, "松川ツール Ver1.1.0 の公開APIを利用できません。\n" + adapterError);

            if (request.SourcePrefabAsset == null
                || EditorUtility.IsPersistent(request.SourcePrefabAsset) is false
                || PrefabUtility.IsPartOfPrefabAsset(request.SourcePrefabAsset) is false)
            {
                return Fail(result, "入力にはProject上のPrefab Assetを指定してください。");
            }

            if (request.AtlasSettings == null)
                return Fail(result, "AtlasTexture設定が指定されていません。");

            if (request.KeepRendererPaths.Count == 0)
                return Fail(result, "抽出対象Rendererが選択されていません。");

            var outputName = matsukawa.SanitizeName(request.OutputName?.Trim() ?? "");
            if (string.IsNullOrWhiteSpace(outputName))
                return Fail(result, "出力名を入力してください。");

            var outputFolder = matsukawa.GetOutputFolder(outputName);
            result.OutputFolder = outputFolder;
            result.AtlasOutputFolder = outputFolder + "/Atlas";

            var outputExists = AssetDatabase.IsValidFolder(outputFolder);
            if (outputExists && request.OverwriteExistingOutput is false)
                return Fail(result, "同名の抽出結果が既に存在します: " + outputFolder);

            Scene temporaryScene = default;
            GameObject? instantiatedRoot = null;
            GameObject? extractionRoot = null;
            MatsukawaExecutionResult? extraction = null;
            var committed = false;

            try
            {
                if (outputExists && matsukawa.DeleteOutputFolder(outputName) is false)
                    return Fail(result, "既存の抽出結果を削除できませんでした: " + outputFolder);

                temporaryScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);

                instantiatedRoot = PrefabUtility.InstantiatePrefab(request.SourcePrefabAsset, temporaryScene) as GameObject;
                if (instantiatedRoot == null)
                    return Fail(result, "入力Prefabを一時Sceneへ展開できませんでした。");

                if (PrefabUtility.IsPartOfPrefabInstance(instantiatedRoot))
                {
                    PrefabUtility.UnpackPrefabInstance(
                        instantiatedRoot,
                        PrefabUnpackMode.Completely,
                        InteractionMode.AutomatedAction
                    );
                }

                extractionRoot = ResolveExtractionRoot(instantiatedRoot, request.ExtractionRootPath);
                if (extractionRoot == null)
                    return Fail(result, "指定された抽出ルートがPrefab内に見つかりません: " + request.ExtractionRootPath);

                if (extractionRoot != instantiatedRoot)
                {
                    var externalReferenceError = SanitizeAndValidateExternalReferences(
                        extractionRoot,
                        instantiatedRoot
                    );
                    if (!string.IsNullOrEmpty(externalReferenceError))
                        return Fail(result, externalReferenceError);

                    extractionRoot.transform.SetParent(null, true);
                    UnityEngine.Object.DestroyImmediate(instantiatedRoot);
                    instantiatedRoot = extractionRoot;
                }

                extractionRoot.name = outputName;

                var atlasSettingsError = ValidateAtlasSettingsReferences(request.AtlasSettings, extractionRoot);
                if (!string.IsNullOrEmpty(atlasSettingsError))
                    return Fail(result, atlasSettingsError);

                var entries = matsukawa.CollectRenderers(extractionRoot).ToList();
                foreach (var entry in entries)
                    entry.Keep = request.KeepRendererPaths.Contains(entry.Path);

                var selectedCount = entries.Count(entry => entry.Keep);
                if (selectedCount == 0)
                    return Fail(result, "抽出対象RendererがPrefab内で1件も一致しませんでした。");

                var executionOptions = CloneOptions(request.ExtractionOptions);
                // The temporary scene is already the disposable working copy. Asking HCE to make
                // another scene copy only adds hierarchy noise and complicates output naming.
                executionOptions.WorkOnCopy = false;

                var analysis = matsukawa.Analyze(extractionRoot, entries, executionOptions);
                if (analysis.WarningCount > 0)
                {
                    foreach (var warning in analysis.Warnings)
                        Debug.LogWarning("Part Atlas Prefab / Matsukawa: " + warning);
                }

                extraction = matsukawa.Execute(extractionRoot, entries, executionOptions);
                if (extraction.Succeeded is false || extraction.Result == null)
                    return Fail(result, "松川ツールの抽出処理が完了しませんでした。");

                extractionRoot = extraction.Result;

                var (targetMaterials, targetRenderers) = ResolveAtlasTargets(
                    request.AtlasSettings,
                    extractionRoot
                );

                if (targetMaterials.Count == 0 || targetRenderers.Length == 0)
                {
                    return Fail(
                        result,
                        "抽出結果の中に、現在のAtlasTexture設定と一致するアトラス化対象がありません。"
                    );
                }

                if (AtlasTextureBaker.BakeResolved(
                        request.AtlasSettings,
                        extractionRoot,
                        targetMaterials,
                        targetRenderers,
                        outputName,
                        result.AtlasOutputFolder,
                        promptOverwrite: false,
                        pingOutputFolder: false
                    ) is false)
                {
                    return Fail(result, "TTT Atlas処理に失敗したため、Prefab化を中断しました。");
                }

                RemoveSupersededTrimMeshes(matsukawa, extraction);

                matsukawa.AddReportNote(
                    extraction,
                    "TexTransToolでアトラス化し、永続Assetを " + result.AtlasOutputFolder + " に保存しました。"
                );
                var reportText = matsukawa.BuildReportText(extraction);
                matsukawa.SetReportText(extraction, reportText);
                RewriteReport(extraction.OutputFolder, reportText);

                var prefabPath = matsukawa.SavePrefab(extraction);
                if (string.IsNullOrEmpty(prefabPath))
                    return Fail(result, "抽出・アトラス化後のPrefabを保存できませんでした。");

                result.PrefabPath = prefabPath;
                result.ReportText = reportText;
                result.Success = true;
                committed = true;
                return result;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                return Fail(result, e.Message);
            }
            finally
            {
                try { matsukawa.ClearHierarchyHighlight(); } catch { }

                if (temporaryScene.IsValid())
                {
                    try
                    {
                        EditorSceneManager.CloseScene(temporaryScene, true);
                    }
                    catch (Exception closeError)
                    {
                        Debug.LogException(closeError);
                    }
                }
                else if (instantiatedRoot != null)
                {
                    UnityEngine.Object.DestroyImmediate(instantiatedRoot);
                }

                if (!committed && AssetDatabase.IsValidFolder(outputFolder))
                {
                    // A failed pipeline must not leave HCE trim meshes, reports, or a partial Atlas
                    // set behind. Existing output was only removed above when overwrite was explicit.
                    matsukawa.DeleteOutputFolder(outputName);
                    AssetDatabase.Refresh();
                }
            }
        }

        private static GameObject? ResolveExtractionRoot(GameObject prefabRoot, string path)
        {
            if (string.IsNullOrEmpty(path)) return prefabRoot;
            return prefabRoot.transform.Find(path)?.gameObject;
        }

        private static MatsukawaOptions CloneOptions(MatsukawaOptions source)
        {
            var copy = new MatsukawaOptions
            {
                WorkOnCopy = source.WorkOnCopy,
                BoneMode = source.BoneMode,
                WeightThreshold = source.WeightThreshold,
                KeepPhysBoneChains = source.KeepPhysBoneChains,
                KeepReferencedObjects = source.KeepReferencedObjects,
                KeepHumanoidBones = source.KeepHumanoidBones,
                ProtectOtherComponents = source.ProtectOtherComponents,
                StripAvatarComponents = source.StripAvatarComponents,
            };
            copy.ForceDeletePaths.AddRange(source.ForceDeletePaths);
            return copy;
        }

        private static (HashSet<Material> targetMaterials, Renderer[] targetRenderers) ResolveAtlasTargets(
            AtlasTexture atlasSettings,
            GameObject root)
        {
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            using var domain = new NotWorkDomain(renderers, null);

            var allowed = AtlasTexture.GetAtlasAllowedRenderers(
                domain,
                domain.EnumerateRenderer(),
                atlasSettings.AtlasSetting.IncludeDisabledRenderer
            );
            var targetMaterials = atlasSettings.GetTargetMaterials(domain, allowed).ToHashSet();
            var targetRenderers = AtlasTexture.FilterTargetRenderers(domain, allowed, targetMaterials);
            targetRenderers = AtlasTexture.FilterExistUVChannel(
                domain,
                targetRenderers,
                atlasSettings.AtlasSetting.AtlasTargetUVChannel
            );

            return (targetMaterials, targetRenderers);
        }

        private static void RemoveSupersededTrimMeshes(
            MatsukawaAdapter matsukawa,
            MatsukawaExecutionResult extraction)
        {
            if (extraction.Result == null || extraction.GeneratedMeshes.Count == 0) return;

            var stillReferenced = new HashSet<string>(StringComparer.Ordinal);
            foreach (var smr in extraction.Result.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var mesh = smr.sharedMesh;
                if (mesh == null) continue;
                var path = AssetDatabase.GetAssetPath(mesh);
                if (!string.IsNullOrEmpty(path)) stillReferenced.Add(path);
            }

            foreach (var filter in extraction.Result.GetComponentsInChildren<MeshFilter>(true))
            {
                var mesh = filter.sharedMesh;
                if (mesh == null) continue;
                var path = AssetDatabase.GetAssetPath(mesh);
                if (!string.IsNullOrEmpty(path)) stillReferenced.Add(path);
            }

            foreach (var generatedPath in extraction.GeneratedMeshes.ToArray())
            {
                if (stillReferenced.Contains(generatedPath)) continue;
                if (AssetDatabase.DeleteAsset(generatedPath))
                    matsukawa.RemoveGeneratedMesh(extraction, generatedPath);
            }

            AssetDatabase.SaveAssets();
        }

        private static void RewriteReport(string outputFolder, string reportText)
        {
            if (string.IsNullOrEmpty(outputFolder)) return;

            var assetPath = outputFolder.TrimEnd('/') + "/抽出レポート.txt";
            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            if (string.IsNullOrEmpty(projectRoot)) return;

            var fullPath = Path.GetFullPath(Path.Combine(projectRoot, assetPath));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, reportText, new UTF8Encoding(true));
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
        }

        /// <summary>
        /// When an installed outfit subtree is detached from an avatar Prefab, direct scene-object
        /// references outside that subtree cannot be stored in the resulting standalone Prefab.
        /// Modular Avatar's AvatarObjectReference deliberately stores both a path and a direct cache;
        /// the direct targetObject cache may be cleared when a non-empty referencePath exists.
        /// Other external references are rejected instead of being silently broken.
        /// </summary>
        private static string SanitizeAndValidateExternalReferences(
            GameObject extractionRoot,
            GameObject fullPrefabRoot)
        {
            var issues = new List<string>();

            foreach (var component in extractionRoot.GetComponentsInChildren<Component>(true))
            {
                if (component == null) continue;

                SerializedObject serialized;
                try { serialized = new SerializedObject(component); }
                catch { continue; }

                var safeToClear = new List<string>();
                var iterator = serialized.GetIterator();
                var enterChildren = true;
                var guard = 0;

                while (iterator.NextVisible(enterChildren) && guard++ < 10000)
                {
                    enterChildren = true;
                    if (iterator.propertyType != SerializedPropertyType.ObjectReference) continue;

                    var referenced = iterator.objectReferenceValue;
                    var referencedTransform = ToTransform(referenced);
                    if (referencedTransform == null) continue;
                    if (referencedTransform.IsChildOf(fullPrefabRoot.transform) is false) continue;
                    if (referencedTransform.IsChildOf(extractionRoot.transform)) continue;

                    if (CanClearAvatarObjectReferenceCache(serialized, iterator.propertyPath))
                    {
                        safeToClear.Add(iterator.propertyPath);
                        continue;
                    }

                    issues.Add(
                        RelativePath(extractionRoot.transform, component.transform)
                        + " :: "
                        + component.GetType().Name
                        + "."
                        + iterator.propertyPath
                        + " -> "
                        + RelativePath(fullPrefabRoot.transform, referencedTransform)
                    );
                }

                foreach (var propertyPath in safeToClear)
                {
                    var property = serialized.FindProperty(propertyPath);
                    if (property != null && property.propertyType == SerializedPropertyType.ObjectReference)
                        property.objectReferenceValue = null;
                }

                if (safeToClear.Count > 0)
                {
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                    EditorUtility.SetDirty(component);
                }
            }

            if (issues.Count == 0) return "";

            var detail = string.Join("\n", issues.Take(20));
            if (issues.Count > 20) detail += "\n... and " + (issues.Count - 20) + " more";

            return
                "抽出ルートの外側を直接参照しているComponentがあります。"
                + "\nこの参照を黙って切るとPrefabが壊れるため処理を中断しました。"
                + "\nデフォルト衣装などAvatar本体のBoneを直接使う構成では、Avatar Rootを抽出ルートにしてください。"
                + "\n\n"
                + detail;
        }

        private static bool CanClearAvatarObjectReferenceCache(
            SerializedObject serialized,
            string propertyPath)
        {
            const string targetObject = "targetObject";
            if (propertyPath == targetObject)
            {
                var referencePath = serialized.FindProperty("referencePath");
                return referencePath?.propertyType == SerializedPropertyType.String
                    && string.IsNullOrEmpty(referencePath.stringValue) is false;
            }

            var suffix = "." + targetObject;
            if (propertyPath.EndsWith(suffix, StringComparison.Ordinal) is false) return false;

            var prefix = propertyPath.Substring(0, propertyPath.Length - targetObject.Length);
            var referencePathProperty = serialized.FindProperty(prefix + "referencePath");
            return referencePathProperty?.propertyType == SerializedPropertyType.String
                && string.IsNullOrEmpty(referencePathProperty.stringValue) is false;
        }

        private static string ValidateAtlasSettingsReferences(
            AtlasTexture atlasSettings,
            GameObject extractionRoot)
        {
            var problems = new List<string>();
            ValidateComponent(atlasSettings, problems);

            var experimental = atlasSettings.GetComponent<AtlasTextureExperimentalFeature>();
            if (experimental != null) ValidateComponent(experimental, problems);

            if (problems.Count == 0) return "";

            var detail = string.Join("\n", problems.Take(20));
            if (problems.Count > 20) detail += "\n... and " + (problems.Count - 20) + " more";

            return
                "AtlasTexture設定が抽出結果の外側にあるScene Objectを参照しています。"
                + "\nPrefab化ではその参照を安全に移せないため処理を中断しました。"
                + "\n\n"
                + detail;

            void ValidateComponent(Component component, List<string> output)
            {
                var serialized = new SerializedObject(component);
                var iterator = serialized.GetIterator();
                var enterChildren = true;
                var guard = 0;

                while (iterator.NextVisible(enterChildren) && guard++ < 10000)
                {
                    enterChildren = true;
                    if (iterator.propertyType != SerializedPropertyType.ObjectReference) continue;
                    if (iterator.propertyPath == "m_Script") continue;
                    if (iterator.propertyPath.StartsWith("BakeExcludedRenderers", StringComparison.Ordinal)) continue;

                    var referenced = iterator.objectReferenceValue;
                    if (referenced == null) continue;
                    if (EditorUtility.IsPersistent(referenced)) continue;

                    var transform = ToTransform(referenced);
                    if (transform != null && transform.IsChildOf(extractionRoot.transform)) continue;

                    output.Add(component.GetType().Name + "." + iterator.propertyPath + " -> " + referenced.name);
                }
            }
        }

        private static Transform? ToTransform(UnityEngine.Object? value)
        {
            return value switch
            {
                Transform transform => transform,
                GameObject gameObject => gameObject.transform,
                Component component => component.transform,
                _ => null,
            };
        }

        private static string RelativePath(Transform root, Transform target)
        {
            if (target == root) return "";

            var segments = new List<string>();
            var current = target;
            while (current != null && current != root)
            {
                segments.Add(current.name);
                current = current.parent;
            }

            segments.Reverse();
            return string.Join("/", segments);
        }

        private static PartAtlasPrefabResult Fail(PartAtlasPrefabResult result, string message)
        {
            result.Success = false;
            result.Error = message;
            Debug.LogError("TexTransTool Part Atlas Prefab: " + message);
            return result;
        }
    }
}
