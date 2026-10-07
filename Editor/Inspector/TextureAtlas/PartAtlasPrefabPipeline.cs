#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using net.rs64.TexTransCore;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace net.rs64.TexTransTool.TextureAtlas.Editor
{
    internal sealed class PartAtlasPrefabRequest
    {
        internal GameObject SourcePrefabAsset = null!;
        internal readonly List<string> ExtractionTargetPaths = new();
        internal readonly HashSet<string> KeepRendererKeys = new(StringComparer.Ordinal);
        internal MatsukawaOptions ExtractionOptions = new();
        internal PartAtlasPrefabSettings AtlasSettings = null!;
        internal string OutputName = "";
    }

    internal sealed class PartAtlasExtractionStaging : IDisposable
    {
        private sealed class TargetState
        {
            internal Transform Target = null!;
            internal string SourcePath = "";
            internal Transform? OriginalParent;
            internal int OriginalSiblingIndex;
            internal string StagedPath = "";
        }

        private readonly GameObject _root;
        private readonly List<TargetState> _states;
        private GameObject? _container;
        private bool _restoreOnDispose = true;

        private PartAtlasExtractionStaging(
            GameObject root,
            IReadOnlyList<Transform> targets)
        {
            _root = root;
            _states = targets
                .Select(target => new TargetState
                {
                    Target = target,
                    SourcePath = RelativePath(root.transform, target),
                    OriginalParent = target.parent,
                    OriginalSiblingIndex = target.GetSiblingIndex(),
                })
                .ToList();

            if (_states.Count == 1 && _states[0].Target == root.transform)
                return;

            var containerName = "__WDT_ExtractionTargets";
            var suffix = 1;
            while (root.transform.Find(containerName) != null)
                containerName = "__WDT_ExtractionTargets_" + suffix++;

            _container = new GameObject(containerName);
            _container.transform.SetParent(root.transform, false);

            for (var i = 0; i < _states.Count; i++)
            {
                var state = _states[i];
                var wrapper = new GameObject("__WDT_Target_" + i.ToString("D4"));
                wrapper.transform.SetParent(_container.transform, false);
                state.Target.SetParent(wrapper.transform, true);
                state.StagedPath = RelativePath(root.transform, state.Target);
            }
        }

        internal int ScaffoldCount => _container == null ? 0 : _states.Count + 1;

        internal IReadOnlyList<Transform> Targets =>
            _states
                .Where(state => state.Target != null)
                .Select(state => state.Target)
                .ToArray();

        internal static PartAtlasExtractionStaging Create(
            GameObject root,
            IEnumerable<Transform> targets)
        {
            if (root == null)
                throw new ArgumentNullException(nameof(root));

            var normalized = NormalizeTargets(root.transform, targets);
            if (normalized.Count == 0)
                throw new InvalidOperationException("抽出対象が指定されていません。");

            return new PartAtlasExtractionStaging(root, normalized);
        }

        internal string RemapSourcePath(string sourcePath)
        {
            if (_container == null || string.IsNullOrEmpty(sourcePath))
                return sourcePath;

            foreach (var state in _states)
            {
                if (string.Equals(sourcePath, state.SourcePath, StringComparison.Ordinal))
                    return state.StagedPath;

                var prefix = state.SourcePath + "/";
                if (sourcePath.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return state.StagedPath
                        + sourcePath.Substring(state.SourcePath.Length);
                }
            }

            return sourcePath;
        }

        internal void DisableAutoRestore()
        {
            _restoreOnDispose = false;
        }

        internal bool MoveTargetsToRoot(GameObject outputRoot, out string error)
        {
            error = "";

            foreach (var state in _states)
            {
                if (state.Target == null)
                {
                    error = "抽出処理後に対象GameObjectを確認できませんでした: " + state.SourcePath;
                    return false;
                }

                if (state.Target == outputRoot.transform)
                    continue;

                state.Target.SetParent(outputRoot.transform, true);
            }

            DestroyScaffolding();
            _restoreOnDispose = false;
            return true;
        }

        public void Dispose()
        {
            if (_restoreOnDispose)
                RestoreOriginalHierarchy();

            DestroyScaffolding();
        }

        private void RestoreOriginalHierarchy()
        {
            foreach (var state in _states)
            {
                if (state.Target == null)
                    continue;

                var parent = state.OriginalParent;
                if (parent == null && state.Target != _root.transform)
                    parent = _root.transform;

                if (state.Target != _root.transform)
                {
                    state.Target.SetParent(parent, true);
                    if (parent != null)
                    {
                        var maxIndex = Math.Max(0, parent.childCount - 1);
                        state.Target.SetSiblingIndex(
                            Math.Min(state.OriginalSiblingIndex, maxIndex)
                        );
                    }
                }
            }
        }

        private void DestroyScaffolding()
        {
            if (_container != null)
            {
                UnityEngine.Object.DestroyImmediate(_container);
                _container = null;
            }
        }

        private static List<Transform> NormalizeTargets(
            Transform root,
            IEnumerable<Transform> targets)
        {
            var candidates = targets
                .Where(target =>
                    target != null
                    && target != root
                    && target.IsChildOf(root))
                .Distinct()
                .OrderBy(target => DepthFrom(root, target))
                .ThenBy(target => RelativePath(root, target), StringComparer.Ordinal)
                .ToList();

            var result = new List<Transform>();
            foreach (var target in candidates)
            {
                if (result.Any(parent =>
                        target != parent
                        && target.IsChildOf(parent)))
                {
                    continue;
                }

                result.RemoveAll(child =>
                    child != target
                    && child.IsChildOf(target));
                result.Add(target);
            }

            return result;
        }

        private static int DepthFrom(Transform root, Transform target)
        {
            if (target == root) return 0;

            var depth = 0;
            var current = target;
            while (current != null && current != root)
            {
                depth++;
                current = current.parent;
            }

            return depth;
        }

        private static string RelativePath(Transform root, Transform target)
        {
            if (target == root) return "";

            var parts = new List<string>();
            var current = target;
            while (current != null && current != root)
            {
                parts.Add(current.name);
                current = current.parent;
            }

            parts.Reverse();
            return string.Join("/", parts);
        }
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
        internal static void UnpackPrefabInstancesForStaging(GameObject root)
        {
            if (root == null)
                throw new ArgumentNullException(nameof(root));

            if (PrefabUtility.IsPartOfPrefabInstance(root))
            {
                var outermostRoot = PrefabUtility.GetOutermostPrefabInstanceRoot(root);
                if (outermostRoot == root)
                {
                    PrefabUtility.UnpackPrefabInstance(
                        root,
                        PrefabUnpackMode.Completely,
                        InteractionMode.AutomatedAction
                    );
                    return;
                }
            }

            while (true)
            {
                GameObject? nestedInstanceRoot = null;

                foreach (var transform in root.GetComponentsInChildren<Transform>(true))
                {
                    if (transform == root.transform
                        || PrefabUtility.IsPartOfPrefabInstance(transform.gameObject) is false)
                    {
                        continue;
                    }

                    var candidate = PrefabUtility.GetOutermostPrefabInstanceRoot(
                        transform.gameObject
                    );
                    if (candidate == null
                        || candidate == root
                        || candidate.transform.IsChildOf(root.transform) is false)
                    {
                        continue;
                    }

                    nestedInstanceRoot = candidate;
                    break;
                }

                if (nestedInstanceRoot == null)
                    break;

                PrefabUtility.UnpackPrefabInstance(
                    nestedInstanceRoot,
                    PrefabUnpackMode.Completely,
                    InteractionMode.AutomatedAction
                );
            }
        }

        internal static PartAtlasPrefabResult Execute(PartAtlasPrefabRequest request)
        {
            var result = new PartAtlasPrefabResult();
            var previousSelection = Selection.activeObject;

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

            var atlasRequested = request.AtlasSettings.AtlasTargetMaterials
                .Any(material => material != null);

            var normalizedTargetPaths = NormalizeExtractionTargetPaths(
                request.ExtractionTargetPaths
            );
            if (normalizedTargetPaths.Count == 0)
                return Fail(result, "抽出対象GameObjectが選択されていません。");

            if (request.KeepRendererKeys.Count == 0)
                return Fail(result, "抽出対象Rendererが選択されていません。");

            var requestedOutputName = matsukawa.SanitizeName(request.OutputName?.Trim() ?? "");
            if (string.IsNullOrWhiteSpace(requestedOutputName))
                return Fail(result, "名前を入力してください。");

            if (TryValidateOutputName(
                    requestedOutputName,
                    out var outputNameError
                ) is false)
            {
                return Fail(result, outputNameError);
            }

            var generationTimestamp = DateTime.Now;
            var outputName = CreateUniqueGeneratedOutputName(
                matsukawa,
                requestedOutputName,
                generationTimestamp,
                out var generatedNameError
            );
            if (string.IsNullOrEmpty(outputName))
                return Fail(result, generatedNameError);

            var outputFolder = matsukawa.GetOutputFolder(outputName);
            result.OutputFolder = outputFolder;
            result.AtlasOutputFolder = outputFolder + "/Atlas";

            Scene temporaryScene = default;
            GameObject? instantiatedRoot = null;
            GameObject? extractionRoot = null;
            MatsukawaExecutionResult? extraction = null;
            var transientInputMeshes = new List<Mesh>();
            var targetStates = new List<ExtractionTargetState>();
            var sourceIsAvatar = false;
            var avatarArmaturePath = "";
            ModularAvatarAdapter? modularAvatarAdapter = null;
            var ownsOutputFolder = false;
            var committed = false;
            PartAtlasExtractionStaging? staging = null;

            try
            {
                temporaryScene = EditorSceneManager.NewScene(
                    NewSceneSetup.EmptyScene,
                    NewSceneMode.Additive
                );

                instantiatedRoot = PrefabUtility.InstantiatePrefab(
                    request.SourcePrefabAsset,
                    temporaryScene
                ) as GameObject;
                if (instantiatedRoot == null)
                    return Fail(result, "入力Prefabを一時Sceneへ展開できませんでした。");

                UnpackPrefabInstancesForStaging(instantiatedRoot);

                var extractionTargets = ResolveExtractionTargets(
                    instantiatedRoot,
                    normalizedTargetPaths,
                    out var targetResolveError
                );
                if (extractionTargets == null)
                    return Fail(result, targetResolveError);

                foreach (var target in extractionTargets)
                {
                    if (ContainsExtractableMesh(target.gameObject) is false)
                    {
                        return Fail(
                            result,
                            "抽出対象配下に抽出可能なMeshがありません: "
                            + RelativePath(instantiatedRoot.transform, target)
                        );
                    }
                }

                sourceIsAvatar = HasAvatarDescriptor(instantiatedRoot);

                targetStates = extractionTargets
                    .Select(target => new ExtractionTargetState
                    {
                        Target = target,
                        SourcePath = RelativePath(instantiatedRoot.transform, target),
                        Attachment = target == instantiatedRoot.transform || target.parent == null
                            ? null
                            : FindHumanoidParentAttachment(
                                instantiatedRoot,
                                target.parent
                            ),
                    })
                    .ToList();

                if (atlasRequested)
                {
                    var atlasSettingsError = ValidateAtlasSettingsReferences(
                        request.AtlasSettings,
                        instantiatedRoot
                    );
                    if (!string.IsNullOrEmpty(atlasSettingsError))
                        return Fail(result, atlasSettingsError);
                }

                var entries = matsukawa.CollectRenderers(instantiatedRoot).ToList();
                foreach (var entry in entries)
                {
                    entry.Keep = request.KeepRendererKeys.Contains(
                        GetRendererKey(instantiatedRoot, entry.Renderer)
                    );
                }

                var selectedEntries = entries
                    .Where(entry => entry.Keep)
                    .ToArray();
                if (selectedEntries.Length == 0)
                    return Fail(result, "抽出対象RendererがPrefab内で1件も一致しませんでした。");

                var uncoveredTarget = targetStates.FirstOrDefault(state =>
                    selectedEntries.Any(entry =>
                        entry.Renderer != null
                        && (entry.Renderer.transform == state.Target
                            || entry.Renderer.transform.IsChildOf(state.Target))) is false
                );
                if (uncoveredTarget != null)
                {
                    return Fail(
                        result,
                        "抽出対象「"
                        + (string.IsNullOrEmpty(uncoveredTarget.SourcePath)
                            ? "<Prefab Root>"
                            : uncoveredTarget.SourcePath)
                        + "」配下で、残すRendererが1件も選択されていません。"
                    );
                }

                // Atlas-specific preflight is required only when at least one Material was
                // explicitly selected for atlasing. Extraction itself remains independent.
                var retainedAtlasRenderers = Array.Empty<Renderer>();
                if (atlasRequested)
                {
                    // Atlas and animation validation must use the original source hierarchy before
                    // extraction targets are staged under the temporary WDT container.
                    var (preflightMaterials, preflightRenderers) = ResolveAtlasTargets(
                        request.AtlasSettings,
                        instantiatedRoot,
                        request.KeepRendererKeys
                    );

                    if (preflightMaterials.Count == 0 || preflightRenderers.Length == 0)
                    {
                        return Fail(
                            result,
                            "選択したMaterialを使用する抽出対象Rendererに、アトラス化可能なRendererがありません。"
                        );
                    }

                    retainedAtlasRenderers = preflightRenderers.ToArray();
                    var preflightMeshes = preflightRenderers
                        .Select(GetRendererMesh)
                        .Where(mesh => mesh != null)
                        .Cast<Mesh>()
                        .ToHashSet();

                    if (AtlasTextureBaker.ValidateAnimationObjectReferences(
                            instantiatedRoot,
                            preflightRenderers,
                            preflightMaterials,
                            preflightMeshes,
                            displayDialog: false
                        ) is false)
                    {
                        return Fail(
                            result,
                            "抽出前のMesh / Materialを直接参照するAnimationClipがあるため中断しました。"
                            + "該当Clip / bindingはConsoleに出力しています。"
                        );
                    }
                }

                // HCE derives the output folder and Prefab name from the working root name.
                instantiatedRoot.name = outputName;

                staging = PartAtlasExtractionStaging.Create(
                    instantiatedRoot,
                    extractionTargets
                );

                var executionOptions = CreateStagedOptions(
                    request.ExtractionOptions,
                    staging,
                    sourceIsAvatar
                );

                var analysis = matsukawa.Analyze(
                    instantiatedRoot,
                    entries,
                    executionOptions
                );
                if (analysis.WarningCount > 0)
                {
                    foreach (var warning in analysis.Warnings)
                        Debug.LogWarning("Part Atlas Prefab / Matsukawa: " + warning);
                }

                var referenceError = SanitizeAndValidateReferencesAgainstAnalysis(
                    instantiatedRoot,
                    analysis,
                    executionOptions.StripAvatarComponents
                );
                if (!string.IsNullOrEmpty(referenceError))
                    return Fail(result, referenceError);

                if (sourceIsAvatar)
                    avatarArmaturePath = FindAvatarArmaturePath(instantiatedRoot, analysis);

                var requiresModularAvatar =
                    targetStates.Any(state => state.Attachment != null)
                    || (sourceIsAvatar
                        && string.IsNullOrEmpty(avatarArmaturePath) is false);

                if (requiresModularAvatar
                    && ModularAvatarAdapter.TryCreate(
                        out modularAvatarAdapter,
                        out var maCapabilityError
                    ) is false)
                {
                    return Fail(
                        result,
                        "この抽出結果の装着情報を保持するにはModular Avatarの対応APIが必要です。"
                        + (string.IsNullOrEmpty(maCapabilityError)
                            ? ""
                            : "\n" + maCapabilityError)
                    );
                }

                var trimRenderers = analysis.TrimTargets
                    .Where(renderer => renderer != null)
                    .Distinct()
                    .ToArray();
                var trimSourceMeshes = trimRenderers
                    .Select(renderer => renderer.sharedMesh)
                    .Where(mesh => mesh != null)
                    .Cast<Mesh>()
                    .ToHashSet();

                if (trimRenderers.Length > 0
                    && AtlasTextureBaker.ValidateAnimationObjectReferences(
                        instantiatedRoot,
                        trimRenderers,
                        new HashSet<Material>(),
                        trimSourceMeshes,
                        displayDialog: false
                    ) is false)
                {
                    return Fail(
                        result,
                        "松川ツールのボーン配列切り詰め対象Meshを直接参照するAnimationClipがあるため中断しました。"
                        + "該当Clip / bindingはConsoleに出力しています。"
                    );
                }

                DisambiguateTrimMeshNames(
                    matsukawa,
                    analysis.TrimTargets,
                    transientInputMeshes
                );

                ownsOutputFolder = true;

                // From this point HCE may delete original parents, so the staging helper must not
                // attempt to restore the source hierarchy during disposal.
                staging.DisableAutoRestore();

                extraction = matsukawa.Execute(
                    instantiatedRoot,
                    entries,
                    executionOptions
                );
                if (extraction.Succeeded is false || extraction.Result == null)
                    return Fail(result, "松川ツールの抽出処理が完了しませんでした。");

                extractionRoot = extraction.Result;

                if (staging.MoveTargetsToRoot(extractionRoot, out var moveError) is false)
                    return Fail(result, moveError);

                var trimValidationError = ValidateTrimResults(
                    analysis,
                    extraction,
                    outputFolder
                );
                if (string.IsNullOrEmpty(trimValidationError) is false)
                    return Fail(result, trimValidationError);

                if (TransientMeshStillReferenced(extractionRoot, transientInputMeshes))
                {
                    return Fail(
                        result,
                        "Meshのボーン切り詰め結果を正常に確定できなかったため、処理を中断しました。"
                    );
                }

                matsukawa.SetWorkedOnCopy(extraction, true);
                matsukawa.SetSourceName(extraction, request.SourcePrefabAsset.name);

                matsukawa.AddReportNote(
                    extraction,
                    "入力Prefab内の抽出対象: "
                    + string.Join(
                        ", ",
                        targetStates.Select(state =>
                            string.IsNullOrEmpty(state.SourcePath)
                                ? "<Prefab Root>"
                                : state.SourcePath)
                    )
                );

                foreach (var targetState in targetStates)
                {
                    if (targetState.Target == null)
                    {
                        return Fail(
                            result,
                            "抽出処理後に対象GameObjectを確認できませんでした: "
                            + targetState.SourcePath
                        );
                    }

                    if (targetState.Attachment == null)
                        continue;

                    if (modularAvatarAdapter == null)
                        return Fail(result, "MA Bone Proxyを自動設定できませんでした。");

                    if (modularAvatarAdapter.ConfigureBoneProxy(
                            targetState.Target.gameObject,
                            targetState.Attachment.BoneReference,
                            targetState.Attachment.SubPath,
                            out var addedBoneProxy,
                            out var boneProxyError
                        ) is false)
                    {
                        return Fail(
                            result,
                            "元の親ボーンへのMA Bone Proxy設定に失敗しました。\n"
                            + boneProxyError
                        );
                    }

                    matsukawa.AddReportNote(
                        extraction,
                        (addedBoneProxy
                            ? "抽出対象「"
                              + targetState.SourcePath
                              + "」にMA Bone Proxyを設定しました（接続先: "
                              + targetState.Attachment.ParentPath
                              + "）。"
                            : "抽出対象「"
                              + targetState.SourcePath
                              + "」の既存MA Bone Proxy設定を保持しました。")
                    );
                }

                if (sourceIsAvatar)
                {
                    matsukawa.AddReportNote(
                        extraction,
                        "Avatar用Component（Animator / AvatarDescriptor / Pipeline系）を出力から除外しました。"
                    );

                    if (string.IsNullOrEmpty(avatarArmaturePath) is false)
                    {
                        if (modularAvatarAdapter == null)
                            return Fail(result, "MA Merge Armatureを自動設定できませんでした。");

                        if (modularAvatarAdapter.ConfigureArmature(
                                extractionRoot,
                                avatarArmaturePath,
                                out var configureError
                            ) is false)
                        {
                            return Fail(
                                result,
                                "抽出ArmatureへのMA Merge Armature設定に失敗しました。\n"
                                + configureError
                            );
                        }

                        matsukawa.AddReportNote(
                            extraction,
                            "Armature「"
                            + avatarArmaturePath
                            + "」にMA Merge Armature / MA Outfit Rootを設定しました。"
                        );
                    }
                }

                var finalReferenceError = ValidateNoExternalSceneReferences(
                    extractionRoot
                );
                if (!string.IsNullOrEmpty(finalReferenceError))
                    return Fail(result, finalReferenceError);

                if (atlasRequested)
                {
                    var (targetMaterials, targetRenderers) = ResolveRetainedAtlasTargets(
                        request.AtlasSettings,
                        extractionRoot,
                        retainedAtlasRenderers
                    );

                    if (targetMaterials.Count == 0 || targetRenderers.Length == 0)
                    {
                        return Fail(
                            result,
                            "抽出結果の中に、現在のAtlasTexture設定と一致するアトラス化対象がありません。"
                        );
                    }

                    if (AtlasTextureBaker.BakeResolved(
                            request.AtlasSettings.ToBakeSettings(),
                            extractionRoot,
                            targetMaterials,
                            targetRenderers,
                            requestedOutputName,
                            result.AtlasOutputFolder,
                            promptOverwrite: false,
                            pingOutputFolder: false,
                            recordUndo: false
                        ) is false)
                    {
                        return Fail(result, "TTT Atlas処理に失敗したため、Prefab化を中断しました。");
                    }

                    RemoveSupersededTrimMeshes(matsukawa, extraction);

                    matsukawa.AddReportNote(
                        extraction,
                        "Atlas Asset: " + result.AtlasOutputFolder
                    );
                }

                var prefabPath = matsukawa.SavePrefab(extraction);
                if (string.IsNullOrEmpty(prefabPath))
                    return Fail(result, "Prefabを保存できませんでした。");

                var reportText = matsukawa.BuildReportText(extraction);
                matsukawa.SetReportText(extraction, reportText);
                RewriteReport(extraction.OutputFolder, reportText);

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
                staging?.Dispose();

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

                foreach (var mesh in transientInputMeshes)
                {
                    if (mesh != null && AssetDatabase.Contains(mesh) is false)
                        UnityEngine.Object.DestroyImmediate(mesh);
                }

                if (!committed
                    && ownsOutputFolder
                    && string.IsNullOrEmpty(outputFolder) is false
                    && AssetDatabase.IsValidFolder(outputFolder))
                {
                    if (AssetDatabase.DeleteAsset(outputFolder) is false)
                    {
                        Debug.LogError(
                            "TexTransTool Part Atlas Prefab: 失敗途中の新規出力を削除できませんでした: "
                            + outputFolder
                        );
                    }

                    AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                }

                if (!committed && previousSelection != null)
                    Selection.activeObject = previousSelection;
            }
        }

        private static string CreateUniqueGeneratedOutputName(
            MatsukawaAdapter matsukawa,
            string logicalOutputName,
            DateTime generationTimestamp,
            out string error)
        {
            var stem =
                logicalOutputName
                + "_extracted_"
                + generationTimestamp.ToString(
                    "yyyyMMdd_HHmmss",
                    System.Globalization.CultureInfo.InvariantCulture
                );

            for (var index = 0; index < 10000; index++)
            {
                var candidate = index == 0
                    ? stem
                    : stem
                      + "_"
                      + index.ToString(
                          "D2",
                          System.Globalization.CultureInfo.InvariantCulture
                      );

                if (TryValidateOutputName(candidate, out error) is false)
                    return "";

                var folder = matsukawa.GetOutputFolder(candidate);
                if (AssetDatabase.IsValidFolder(folder) is false
                    && Directory.Exists(AssetPathToFullPath(folder)) is false)
                {
                    error = "";
                    return candidate;
                }
            }

            error = "同一生成日時の出力名を確定できませんでした。";
            return "";
        }

        internal static bool TryValidateOutputName(
            string outputName,
            out string error)
        {
            if (AtlasTextureBaker.TryValidateBakeName(outputName, out error))
                return true;

            error = error.Replace("ベイク名", "名前");
            return false;
        }

        private sealed class ExtractionTargetState
        {
            internal Transform Target = null!;
            internal string SourcePath = "";
            internal ParentAttachmentInfo? Attachment;
        }

        private sealed class ParentAttachmentInfo
        {
            internal HumanBodyBones BoneReference;
            internal string SubPath = "";
            internal string ParentPath = "";
        }

        private static ParentAttachmentInfo? FindHumanoidParentAttachment(
            GameObject avatarRoot,
            Transform originalParent)
        {
            if (originalParent == null || originalParent == avatarRoot.transform)
                return null;

            var animator = avatarRoot.GetComponent<Animator>();
            if (animator == null || animator.avatar == null || animator.isHuman is false)
                return null;

            var humanoidBones = new Dictionary<Transform, HumanBodyBones>();
            foreach (HumanBodyBones boneType in Enum.GetValues(typeof(HumanBodyBones)))
            {
                if (boneType == HumanBodyBones.LastBone) continue;
                var bone = animator.GetBoneTransform(boneType);
                if (bone != null && bone.IsChildOf(avatarRoot.transform))
                    humanoidBones[bone] = boneType;
            }

            var current = originalParent;
            while (current != null && current != avatarRoot.transform)
            {
                if (humanoidBones.TryGetValue(current, out var boneReference))
                {
                    return new ParentAttachmentInfo
                    {
                        BoneReference = boneReference,
                        SubPath = RelativePath(current, originalParent),
                        ParentPath = RelativePath(avatarRoot.transform, originalParent),
                    };
                }

                current = current.parent;
            }

            return null;
        }

        private static bool HasAvatarDescriptor(GameObject root)
        {
            return root.GetComponents<Component>()
                .Where(component => component != null)
                .Any(component => component.GetType().Name == "VRCAvatarDescriptor");
        }

        private static string FindAvatarArmaturePath(
            GameObject root,
            MatsukawaAnalysis analysis)
        {
            // HCE's keep-set is authoritative here: it already includes renderer ancestors,
            // weighted bones, probe anchors, PhysBone dependencies and followed references.
            // If the canonical Humanoid armature root survived that analysis, the extracted
            // part depends on avatar-armature content and needs a Merge Armature attachment.
            var animator = root.GetComponent<Animator>();
            if (animator == null || animator.avatar == null || animator.isHuman is false)
                return "";

            var hips = animator.GetBoneTransform(HumanBodyBones.Hips);
            if (hips == null || hips.IsChildOf(root.transform) is false || hips.parent == null)
                return "";

            var armatureRoot = hips.parent;
            return analysis.KeepTransforms.Contains(armatureRoot)
                ? RelativePath(root.transform, armatureRoot)
                : "";
        }

        private static bool ContainsExtractableMesh(GameObject root)
        {
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer is not SkinnedMeshRenderer && renderer is not MeshRenderer)
                    continue;
                if (GetRendererMesh(renderer) != null)
                    return true;
            }

            return false;
        }

        internal static IReadOnlyList<string> NormalizeExtractionTargetPaths(
            IEnumerable<string> paths)
        {
            var ordered = paths
                .Where(path => path != null)
                .Select(path => path.Trim('/'))
                .Where(path => string.IsNullOrEmpty(path) is false)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(path => path.Count(character => character == '/'))
                .ThenBy(path => path.Length)
                .ThenBy(path => path, StringComparer.Ordinal)
                .ToList();

            var result = new List<string>();
            foreach (var path in ordered)
            {
                if (result.Any(parent =>
                        path.StartsWith(parent + "/", StringComparison.Ordinal)))
                {
                    continue;
                }

                result.Add(path);
            }

            return result;
        }

        private static IReadOnlyList<Transform>? ResolveExtractionTargets(
            GameObject prefabRoot,
            IReadOnlyList<string> paths,
            out string error)
        {
            error = "";
            var targets = new List<Transform>();

            foreach (var path in paths)
            {
                if (string.IsNullOrEmpty(path))
                {
                    error = "Prefab Rootは抽出対象に指定できません。";
                    return null;
                }

                var target = prefabRoot.transform.Find(path);

                if (target == null)
                {
                    error = "指定された抽出対象がPrefab内に見つかりません: " + path;
                    return null;
                }

                targets.Add(target);
            }

            return targets;
        }

        internal static MatsukawaOptions CreateStagedOptions(
            MatsukawaOptions source,
            PartAtlasExtractionStaging staging,
            bool forceStripAvatarComponents)
        {
            var copy = CloneOptions(source);
            copy.WorkOnCopy = false;
            copy.StripAvatarComponents = forceStripAvatarComponents;

            if (copy.ForceDeletePaths.Count > 0)
            {
                var remapped = copy.ForceDeletePaths
                    .Select(staging.RemapSourcePath)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                copy.ForceDeletePaths.Clear();
                copy.ForceDeletePaths.AddRange(remapped);
            }

            return copy;
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

        internal static Renderer[] GetAtlasCandidateRenderers(
            PartAtlasPrefabSettings atlasSettings,
            GameObject root)
        {
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            using var domain = new NotWorkDomain(renderers, null);

            var allowed = domain
                .EnumerateRenderer()
                .Where(AtlasTexture.IsAtlasAllowedRenderer)
                .ToList();
            var targetMaterials = ResolveSelectedMaterials(
                domain,
                allowed,
                atlasSettings.AtlasTargetMaterials
            );
            var targetRenderers = AtlasTexture.FilterTargetRenderers(domain, allowed, targetMaterials);
            return AtlasTexture.FilterExistUVChannel(
                domain,
                targetRenderers,
                atlasSettings.AtlasSetting.AtlasTargetUVChannel
            );
        }

        private static HashSet<Material> ResolveSelectedMaterials(
            IDomain domain,
            IEnumerable<Renderer> renderers,
            IEnumerable<Material?> selectedMaterials)
        {
            var selected = selectedMaterials
                .Where(material => material != null)
                .Cast<Material>()
                .ToArray();

            if (selected.Length == 0) return new HashSet<Material>();

            var presentMaterials = renderers
                .SelectMany(renderer => domain.GetMaterials(renderer))
                .Where(material => material != null)
                .Cast<Material>()
                .ToHashSet();

            return presentMaterials
                .Where(material => selected.Any(selectedMaterial =>
                    domain.OriginEqual(selectedMaterial, material)
                ))
                .ToHashSet();
        }

        private static Mesh? GetRendererMesh(Renderer renderer)
        {
            return renderer switch
            {
                SkinnedMeshRenderer skinned => skinned.sharedMesh,
                MeshRenderer meshRenderer => meshRenderer.GetComponent<MeshFilter>()?.sharedMesh,
                _ => null,
            };
        }

        internal static string GetRendererPath(GameObject root, Renderer renderer)
        {
            return AnimationUtility.CalculateTransformPath(
                renderer.transform,
                root.transform
            );
        }

        internal static string GetRendererKey(GameObject root, Renderer renderer)
        {
            var path = GetRendererPath(root, renderer);
            var renderers = renderer.GetComponents<Renderer>();
            var componentIndex = Array.IndexOf(renderers, renderer);

            return path
                + "|"
                + renderer.GetType().FullName
                + "|"
                + componentIndex;
        }

        private static (HashSet<Material> targetMaterials, Renderer[] targetRenderers) ResolveAtlasTargets(
            PartAtlasPrefabSettings atlasSettings,
            GameObject root,
            IReadOnlyCollection<string> keepRendererKeys)
        {
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            using var domain = new NotWorkDomain(renderers, null);

            var targetRenderers = GetAtlasCandidateRenderers(atlasSettings, root)
                .Where(renderer =>
                    keepRendererKeys.Contains(GetRendererKey(root, renderer))
                )
                .ToArray();

            var targetMaterials = ResolveSelectedMaterials(
                domain,
                targetRenderers,
                atlasSettings.AtlasTargetMaterials
            );

            return (targetMaterials, targetRenderers);
        }

        private static string ValidateTrimResults(
            MatsukawaAnalysis analysis,
            MatsukawaExecutionResult extraction,
            string outputFolder)
        {
            if (analysis.ExpectedTrimBoneCounts.Count == 0) return "";
            if (extraction.Result == null)
                return "抽出結果を確認できませんでした。";

            var currentRenderers = extraction.Result
                .GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .ToHashSet();
            var generated = extraction.GeneratedMeshes.ToHashSet(StringComparer.Ordinal);
            var expectedMeshFolder = outputFolder.TrimEnd('/') + "/Meshes/";

            foreach (var pair in analysis.ExpectedTrimBoneCounts)
            {
                var renderer = pair.Key;
                var expectedBoneCount = pair.Value;

                if (renderer == null || currentRenderers.Contains(renderer) is false)
                {
                    return "ボーン切り詰め対象Rendererを抽出結果で確認できませんでした。";
                }

                if (expectedBoneCount <= 0)
                {
                    return
                        "松川ツールの解析で、残すボーン数が0本になるSkinnedMeshRendererが見つかりました: "
                        + renderer.name;
                }

                if (renderer.bones == null || renderer.bones.Length != expectedBoneCount)
                {
                    return
                        "松川ツールのボーン配列切り詰め結果が解析値と一致しません: "
                        + renderer.name
                        + " (expected "
                        + expectedBoneCount
                        + ", actual "
                        + (renderer.bones?.Length ?? 0)
                        + ")";
                }

                var mesh = renderer.sharedMesh;
                if (mesh == null)
                {
                    return "松川ツールの切り詰め後Meshがありません: " + renderer.name;
                }

                var meshPath = AssetDatabase.GetAssetPath(mesh);
                if (string.IsNullOrEmpty(meshPath)
                    || meshPath.StartsWith(expectedMeshFolder, StringComparison.Ordinal) is false
                    || generated.Contains(meshPath) is false)
                {
                    return
                        "松川ツールの切り詰め後Meshが永続Assetとして確認できません: "
                        + renderer.name;
                }
            }

            return "";
        }

        private static (HashSet<Material> targetMaterials, Renderer[] targetRenderers)
            ResolveRetainedAtlasTargets(
                PartAtlasPrefabSettings atlasSettings,
                GameObject root,
                IReadOnlyCollection<Renderer> retainedRenderers)
        {
            var currentRenderers = root.GetComponentsInChildren<Renderer>(true).ToHashSet();
            var missing = retainedRenderers
                .Where(renderer => renderer == null || currentRenderers.Contains(renderer) is false)
                .ToArray();

            if (missing.Length > 0)
            {
                throw new InvalidOperationException(
                    "抽出処理後にアトラス化対象Rendererを確認できませんでした。"
                    + "抽出対象とアトラス対象の組み合わせを確認してください。"
                );
            }

            var renderers = retainedRenderers.ToArray();
            using var domain = new NotWorkDomain(
                root.GetComponentsInChildren<Renderer>(true),
                null
            );

            var targetMaterials = ResolveSelectedMaterials(
                domain,
                renderers,
                atlasSettings.AtlasTargetMaterials
            );

            return (targetMaterials, renderers);
        }

        private static bool TransientMeshStillReferenced(
            GameObject root,
            IReadOnlyCollection<Mesh> transientMeshes)
        {
            if (transientMeshes.Count == 0) return false;
            var transient = transientMeshes.ToHashSet();

            foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (renderer.sharedMesh != null && transient.Contains(renderer.sharedMesh))
                    return true;
            }

            foreach (var filter in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (filter.sharedMesh != null && transient.Contains(filter.sharedMesh))
                    return true;
            }

            return false;
        }

        private static void DisambiguateTrimMeshNames(
            MatsukawaAdapter matsukawa,
            IReadOnlyList<SkinnedMeshRenderer> trimTargets,
            List<Mesh> transientMeshes)
        {
            var candidates = trimTargets
                .Where(renderer => renderer != null && renderer.sharedMesh != null)
                .ToArray();

            if (candidates.Length < 2) return;

            // HceMeshTrimmer uses Sanitize(mesh.name) as the .asset filename. Detect
            // collisions on that exact key rather than on the raw Unity object name.
            var usedFileKeys = candidates
                .Select(renderer => matsukawa.SanitizeName(renderer.sharedMesh.name))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var group in candidates
                         .GroupBy(
                             renderer => matsukawa.SanitizeName(renderer.sharedMesh.name),
                             StringComparer.OrdinalIgnoreCase
                         )
                         .Where(group => group.Count() > 1))
            {
                var index = 0;
                foreach (var renderer in group)
                {
                    // Keep the first renderer on the original source mesh. Only later colliding
                    // renderers need transient clones because HceMeshTrimmer saves by mesh name.
                    if (index++ == 0) continue;

                    var source = renderer.sharedMesh;
                    var suffix = index;
                    string uniqueName;
                    string fileKey;
                    do
                    {
                        uniqueName = source.name + "__TTT_" + suffix++;
                        fileKey = matsukawa.SanitizeName(uniqueName);
                    } while (usedFileKeys.Add(fileKey) is false);

                    var clone = UnityEngine.Object.Instantiate(source);
                    clone.name = uniqueName;
                    renderer.sharedMesh = clone;
                    transientMeshes.Add(clone);
                }
            }
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

        private static string AssetPathToFullPath(string assetPath)
        {
            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName
                ?? throw new InvalidOperationException("Unity project root was not found.");
            return Path.GetFullPath(Path.Combine(projectRoot, assetPath));
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

        private static string SanitizeAndValidateReferencesAgainstAnalysis(
            GameObject root,
            MatsukawaAnalysis analysis,
            bool stripAvatarComponents)
        {
            var issues = new List<string>();
            var deleted = analysis.DeleteTransforms;

            foreach (var component in root.GetComponentsInChildren<Component>(true))
            {
                if (component == null || component is Transform) continue;
                if (deleted.Contains(component.transform)) continue;

                // When HCE is configured to strip Avatar-root components, references owned by
                // those components are irrelevant to the extracted Prefab because the component
                // itself will not survive execution. Validating them here would reject normal
                // AvatarDescriptor references such as Body / Eye_L / Eye_R before HCE removes
                // the descriptor.
                if (stripAvatarComponents
                    && IsAvatarRootComponentRemovedByExtraction(component, root))
                {
                    continue;
                }

                // HCE owns SkinnedMeshRenderer bone dependency analysis and rewrites the
                // bones array when it trims unused bones. Treating those references as
                // generic unsafe references here would reject the exact case HCE is
                // designed to process.
                if (component is SkinnedMeshRenderer) continue;

                SerializedObject serialized;
                try { serialized = new SerializedObject(component); }
                catch { continue; }

                var safeToClear = new List<string>();
                var iterator = serialized.GetIterator();
                var enterChildren = true;
                var guard = 0;

                while (iterator.Next(enterChildren) && guard++ < 10000)
                {
                    enterChildren = true;
                    if (iterator.propertyType != SerializedPropertyType.ObjectReference) continue;

                    var referenced = iterator.objectReferenceValue;
                    var referencedTransform = ToTransform(referenced);
                    if (referencedTransform == null) continue;
                    if (referencedTransform.IsChildOf(root.transform) is false) continue;
                    if (deleted.Contains(referencedTransform) is false) continue;

                    if (CanClearAvatarObjectReferenceCache(
                            component,
                            serialized,
                            iterator.propertyPath
                        ))
                    {
                        safeToClear.Add(iterator.propertyPath);
                        continue;
                    }

                    issues.Add(
                        RelativePath(root.transform, component.transform)
                        + " :: "
                        + component.GetType().Name
                        + "."
                        + iterator.propertyPath
                        + " -> "
                        + RelativePath(root.transform, referencedTransform)
                    );
                }

                foreach (var propertyPath in safeToClear)
                {
                    var property = serialized.FindProperty(propertyPath);
                    if (property != null
                        && property.propertyType == SerializedPropertyType.ObjectReference)
                    {
                        property.objectReferenceValue = null;
                    }
                }

                if (safeToClear.Count > 0)
                {
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                    EditorUtility.SetDirty(component);
                }
            }

            if (issues.Count == 0) return "";

            var detail = string.Join("\n", issues.Take(20));
            if (issues.Count > 20)
                detail += "\n... and " + (issues.Count - 20) + " more";

            return
                "抽出後に解決できないObject参照が残るため、処理を中断しました。"
                + "\n\n"
                + detail;
        }

        private static bool IsAvatarRootComponentRemovedByExtraction(
            Component component,
            GameObject root)
        {
            if (component.gameObject != root)
                return false;

            if (component is Animator)
                return true;

            var typeName = component.GetType().Name;
            return string.Equals(
                       typeName,
                       "VRCAvatarDescriptor",
                       StringComparison.Ordinal
                   )
                   || string.Equals(
                       typeName,
                       "PipelineManager",
                       StringComparison.Ordinal
                   );
        }

        private static string ValidateNoExternalSceneReferences(GameObject root)
        {
            var issues = new List<string>();

            foreach (var component in root.GetComponentsInChildren<Component>(true))
            {
                if (component == null || component is Transform) continue;

                SerializedObject serialized;
                try { serialized = new SerializedObject(component); }
                catch { continue; }

                var iterator = serialized.GetIterator();
                var enterChildren = true;
                var guard = 0;

                while (iterator.Next(enterChildren) && guard++ < 10000)
                {
                    enterChildren = true;
                    if (iterator.propertyType != SerializedPropertyType.ObjectReference) continue;

                    var referenced = iterator.objectReferenceValue;
                    if (referenced == null || EditorUtility.IsPersistent(referenced)) continue;

                    var referencedTransform = ToTransform(referenced);
                    if (referencedTransform == null) continue;
                    if (referencedTransform == root.transform
                        || referencedTransform.IsChildOf(root.transform))
                    {
                        continue;
                    }

                    issues.Add(
                        RelativePath(root.transform, component.transform)
                        + " :: "
                        + component.GetType().Name
                        + "."
                        + iterator.propertyPath
                        + " -> "
                        + referencedTransform.name
                    );
                }
            }

            if (issues.Count == 0) return "";

            var detail = string.Join("\n", issues.Take(20));
            if (issues.Count > 20)
                detail += "\n... and " + (issues.Count - 20) + " more";

            return
                "抽出結果のPrefab外を直接参照しているComponentがあります。"
                + "\nStandalone Prefabとして保存できないため処理を中断しました。"
                + "\n\n"
                + detail;
        }

        private static bool CanClearAvatarObjectReferenceCache(
            Component component,
            SerializedObject serialized,
            string propertyPath)
        {
            var componentNamespace = component.GetType().Namespace ?? "";
            if (componentNamespace.StartsWith(
                    "nadena.dev.modular_avatar",
                    StringComparison.Ordinal
                ) is false)
            {
                return false;
            }

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
            PartAtlasPrefabSettings atlasSettings,
            GameObject extractionRoot)
        {
            var problems = new List<string>();
            var serialized = new SerializedObject(atlasSettings);
            var iterator = serialized.GetIterator();
            var enterChildren = true;
            var guard = 0;

            while (iterator.NextVisible(enterChildren) && guard++ < 10000)
            {
                enterChildren = true;
                if (iterator.propertyType != SerializedPropertyType.ObjectReference) continue;
                if (iterator.propertyPath == "m_Script") continue;

                var referenced = iterator.objectReferenceValue;
                if (referenced == null) continue;
                if (EditorUtility.IsPersistent(referenced)) continue;

                var transform = ToTransform(referenced);
                if (transform != null && transform.IsChildOf(extractionRoot.transform)) continue;

                problems.Add(
                    atlasSettings.GetType().Name
                    + "."
                    + iterator.propertyPath
                    + " -> "
                    + referenced.name
                );
            }

            if (problems.Count == 0) return "";

            var detail = string.Join("\n", problems.Take(20));
            if (problems.Count > 20) detail += "\n... and " + (problems.Count - 20) + " more";

            return
                "Atlas設定に、出力Prefab内で解決できないScene Object参照が含まれるため、処理を中断しました。"
                + "\n\n"
                + detail;
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
