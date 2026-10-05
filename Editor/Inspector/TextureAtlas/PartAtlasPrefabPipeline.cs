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
        internal readonly HashSet<string> KeepRendererKeys = new(StringComparer.Ordinal);
        internal MatsukawaOptions ExtractionOptions = new();
        internal AtlasTexture AtlasSettings = null!;
        internal string OutputName = "";
        // Prefab化専用のRenderer選択。通常のBakeExcludedRenderersは設定元Hierarchyを
        // 参照しているため、別Prefabへ暗黙に流用しない。
        internal readonly HashSet<string> AtlasRendererKeys = new(StringComparer.Ordinal);
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

            if (request.KeepRendererKeys.Count == 0)
                return Fail(result, "抽出対象Rendererが選択されていません。");

            if (request.AtlasRendererKeys.Count == 0)
                return Fail(result, "アトラス化対象Rendererが選択されていません。");

            var requestedOutputName = matsukawa.SanitizeName(request.OutputName?.Trim() ?? "");
            if (string.IsNullOrWhiteSpace(requestedOutputName))
                return Fail(result, "出力名を入力してください。");

            if (TryValidateOutputName(
                    requestedOutputName,
                    out var outputNameError
                ) is false)
            {
                return Fail(result, outputNameError);
            }

            string outputName = "";
            string outputFolder = "";

            Scene temporaryScene = default;
            GameObject? instantiatedRoot = null;
            GameObject? extractionRoot = null;
            MatsukawaExecutionResult? extraction = null;
            var transientInputMeshes = new List<Mesh>();
            var originalParentPath = "";
            ParentAttachmentInfo? parentAttachment = null;
            var isAvatarRootExtraction = false;
            var defaultAvatarArmaturePath = "";
            var ownsOutputFolder = false;
            string? backupDirectory = null;
            var committed = false;

            try
            {
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
                    if (extractionRoot.transform.parent != null)
                    {
                        originalParentPath = RelativePath(
                            instantiatedRoot.transform,
                            extractionRoot.transform.parent
                        );
                        parentAttachment = FindHumanoidParentAttachment(
                            instantiatedRoot,
                            extractionRoot.transform.parent
                        );
                    }

                    var externalReferenceError = SanitizeAndValidateExternalReferences(
                        extractionRoot,
                        instantiatedRoot
                    );
                    if (!string.IsNullOrEmpty(externalReferenceError))
                        return Fail(result, externalReferenceError);

                    // Normalize the detached part transform into the source avatar-root
                    // coordinate space rather than keeping scene-world coordinates. This keeps a
                    // BoneProxy/standalone part reusable even when the source avatar root itself
                    // was moved, rotated, or scaled in the scene used to author the Prefab.
                    extractionRoot.transform.SetParent(instantiatedRoot.transform, true);
                    extractionRoot.transform.SetParent(null, false);
                    UnityEngine.Object.DestroyImmediate(instantiatedRoot);
                    instantiatedRoot = extractionRoot;
                }

                // AnimationClip bindings and MA AvatarObjectReference paths are root-relative, so
                // changing only the temporary extraction root name does not alter child paths.
                // The source Prefab asset itself is never renamed.
                extractionRoot.name = requestedOutputName;
                outputName = extractionRoot.name;
                outputFolder = matsukawa.GetOutputFolder(outputName);
                result.OutputFolder = outputFolder;
                result.AtlasOutputFolder = outputFolder + "/Atlas";

                var outputExists = AssetDatabase.IsValidFolder(outputFolder);
                if (outputExists && request.OverwriteExistingOutput is false)
                    return Fail(result, "同名の抽出結果が既に存在します: " + outputFolder);

                var atlasSettingsError = ValidateAtlasSettingsReferences(request.AtlasSettings, extractionRoot);
                if (!string.IsNullOrEmpty(atlasSettingsError))
                    return Fail(result, atlasSettingsError);

                var entries = matsukawa.CollectRenderers(extractionRoot).ToList();
                foreach (var entry in entries)
                    entry.Keep = request.KeepRendererKeys.Contains(
                        GetRendererKey(extractionRoot, entry.Renderer)
                    );

                var selectedCount = entries.Count(entry => entry.Keep);
                if (selectedCount == 0)
                    return Fail(result, "抽出対象RendererがPrefab内で1件も一致しませんでした。");

                isAvatarRootExtraction =
                    string.IsNullOrEmpty(request.ExtractionRootPath)
                    && HasAvatarDescriptor(extractionRoot);

                if (isAvatarRootExtraction)
                {
                    defaultAvatarArmaturePath = FindAvatarArmaturePath(
                        extractionRoot,
                        entries.Where(entry => entry.Keep)
                    );
                }

                var executionOptions = CloneOptions(request.ExtractionOptions);
                // The temporary scene is already the disposable working copy. Asking HCE to make
                // another scene copy only adds hierarchy noise and complicates output naming.
                executionOptions.WorkOnCopy = false;

                // A default outfit extracted from an Avatar Prefab must become a part Prefab,
                // not another avatar. Always remove root Animator/Descriptor/Pipeline components.
                if (isAvatarRootExtraction)
                    executionOptions.StripAvatarComponents = true;

                var analysis = matsukawa.Analyze(extractionRoot, entries, executionOptions);
                if (analysis.WarningCount > 0)
                {
                    foreach (var warning in analysis.Warnings)
                        Debug.LogWarning("Part Atlas Prefab / Matsukawa: " + warning);
                }

                // Verify the Atlas renderer/material mapping before touching an existing output folder.
                // HCE only deletes objects and compacts meshes; it does not reparent kept renderers,
                // so these stable renderer keys must still resolve after extraction.
                var (preflightMaterials, preflightRenderers) = ResolveAtlasTargets(
                    request.AtlasSettings,
                    extractionRoot,
                    request.AtlasRendererKeys
                );

                // HCE runs in-place on this disposable hierarchy (workOnCopy=false), so kept
                // Renderer component instances remain the same objects through Execute. Retain
                // those references instead of re-identifying them after HCE has removed siblings.
                var retainedAtlasRenderers = preflightRenderers.ToArray();

                var preflightMeshes = preflightRenderers
                    .Select(GetRendererMesh)
                    .Where(mesh => mesh != null)
                    .Cast<Mesh>()
                    .ToHashSet();

                if (AtlasTextureBaker.ValidateAnimationObjectReferences(
                        extractionRoot,
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
                        extractionRoot,
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

                if (outputExists)
                {
                    try
                    {
                        // Keep the existing Assets path in place so HCE SavePrefab and the
                        // Atlas manifest can update existing assets without changing their GUIDs.
                        // Rollback bytes (including .meta files) live outside Assets.
                        backupDirectory = CreateOutputFolderBackup(outputFolder);
                    }
                    catch (Exception backupError)
                    {
                        return Fail(
                            result,
                            "既存の抽出結果をバックアップできないため、上書きを中断しました。\n"
                            + backupError.Message
                        );
                    }
                }

                // From this point on, outputFolder may be changed by HCE/TTT.
                ownsOutputFolder = true;

                extraction = matsukawa.Execute(extractionRoot, entries, executionOptions);
                if (extraction.Succeeded is false || extraction.Result == null)
                    return Fail(result, "松川ツールの抽出処理が完了しませんでした。");

                if (string.IsNullOrEmpty(backupDirectory) is false)
                {
                    PreserveExistingTrimMeshGuids(
                        extraction.Result,
                        extraction.GeneratedMeshes,
                        outputFolder,
                        backupDirectory
                    );
                }

                if (TransientMeshStillReferenced(extraction.Result, transientInputMeshes))
                {
                    return Fail(
                        result,
                        "同名Mesh衝突回避用の一時Meshが抽出結果に残りました。"
                        + "松川ツールのボーン切り詰めが完了していないため、安全のため中断します。"
                    );
                }

                // HCE itself was asked not to create a second copy, but the entire operation is
                // already running on a disposable Prefab instance. Normalize the report so it
                // correctly states that the source Prefab asset was left untouched.
                matsukawa.SetWorkedOnCopy(extraction, true);
                matsukawa.SetSourceName(extraction, request.SourcePrefabAsset.name);

                if (transientInputMeshes.Count > 0)
                {
                    matsukawa.AddReportNote(
                        extraction,
                        "同名Meshの切り詰めAsset衝突を避けるため、解析用コピー上で "
                        + transientInputMeshes.Count
                        + " 個のMesh名を一時的に分離しました。元Assetは変更していません。"
                    );
                }

                if (string.IsNullOrEmpty(request.ExtractionRootPath) is false)
                {
                    matsukawa.AddReportNote(
                        extraction,
                        "入力Prefab内の抽出ルート: " + request.ExtractionRootPath
                    );
                }

                if (string.IsNullOrEmpty(originalParentPath) is false)
                {
                    matsukawa.AddReportNote(
                        extraction,
                        "抽出元では親が「" + originalParentPath + "」でした。"
                    );
                }

                extractionRoot = extraction.Result;

                if (parentAttachment != null)
                {
                    if (ModularAvatarAdapter.TryCreate(out var modularAvatar, out var maError)
                        && modularAvatar != null)
                    {
                        if (modularAvatar.ConfigureBoneProxy(
                                extractionRoot,
                                parentAttachment.BoneReference,
                                parentAttachment.SubPath,
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
                            addedBoneProxy
                                ? "元の親ボーン「"
                                  + parentAttachment.ParentPath
                                  + "」へ接続するMA Bone Proxyを追加しました。"
                                : "既存のMA Bone Proxy設定を保持しました。抽出元の親ボーンは「"
                                  + parentAttachment.ParentPath
                                  + "」です。"
                        );
                    }
                    else
                    {
                        return Fail(
                            result,
                            "元の親ボーン接続をStandalone Prefabへ保持するにはModular Avatarが必要です。"
                            + (string.IsNullOrEmpty(maError) ? "" : "\n" + maError)
                        );
                    }
                }

                if (isAvatarRootExtraction)
                {
                    matsukawa.AddReportNote(
                        extraction,
                        "Avatar Rootからの抽出として、Animator / AvatarDescriptor / Pipeline系Componentを出力から外しました。"
                    );

                    if (string.IsNullOrEmpty(defaultAvatarArmaturePath) is false)
                    {
                        if (ModularAvatarAdapter.TryCreate(out var modularAvatar, out var maError)
                            && modularAvatar != null)
                        {
                            if (modularAvatar.ConfigureArmature(
                                    extractionRoot,
                                    defaultAvatarArmaturePath,
                                    out var configureError
                                ) is false)
                            {
                                return Fail(
                                    result,
                                    "デフォルト衣装用のMA Merge Armature設定に失敗しました。\n"
                                    + configureError
                                );
                            }

                            matsukawa.AddReportNote(
                                extraction,
                                "デフォルト衣装のArmature「"
                                + defaultAvatarArmaturePath
                                + "」にMA Merge Armature / MA Outfit Rootを設定しました。"
                            );
                        }
                        else
                        {
                            return Fail(
                                result,
                                "Avatar本体Armatureを使用するデフォルトパーツをStandalone Prefab化するにはModular Avatarが必要です。"
                                + (string.IsNullOrEmpty(maError) ? "" : "\n" + maError)
                            );
                        }
                    }
                }

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
                        request.AtlasSettings,
                        extractionRoot,
                        targetMaterials,
                        targetRenderers,
                        matsukawa.SanitizeName(outputName),
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

                if (string.IsNullOrEmpty(backupDirectory) is false)
                {
                    TryDeleteBackupDirectory(backupDirectory);
                    backupDirectory = null;
                }

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

                foreach (var mesh in transientInputMeshes)
                {
                    if (mesh != null && AssetDatabase.Contains(mesh) is false)
                        UnityEngine.Object.DestroyImmediate(mesh);
                }

                if (!committed)
                {
                    if (string.IsNullOrEmpty(backupDirectory) is false)
                    {
                        try
                        {
                            RestoreOutputFolderBackup(outputFolder, backupDirectory);
                            TryDeleteBackupDirectory(backupDirectory);
                            backupDirectory = null;
                        }
                        catch (Exception restoreError)
                        {
                            Debug.LogError(
                                "TexTransTool Part Atlas Prefab: 旧出力の復元に失敗しました。"
                                + "\n出力: " + outputFolder
                                + "\nバックアップ: " + backupDirectory
                                + "\n" + restoreError
                            );
                        }
                    }
                    else if (ownsOutputFolder
                             && string.IsNullOrEmpty(outputFolder) is false
                             && AssetDatabase.IsValidFolder(outputFolder))
                    {
                        // No previous output existed. Remove only the partial output created
                        // by this failed transaction.
                        if (AssetDatabase.DeleteAsset(outputFolder) is false)
                        {
                            Debug.LogError(
                                "TexTransTool Part Atlas Prefab: 失敗途中の新規出力を削除できませんでした: "
                                + outputFolder
                            );
                        }
                    }

                    AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                }

                if (!committed && previousSelection != null)
                    Selection.activeObject = previousSelection;
            }
        }

        internal static bool TryValidateOutputName(
            string outputName,
            out string error)
        {
            if (AtlasTextureBaker.TryValidateBakeName(outputName, out error))
                return true;

            error = error.Replace("ベイク名", "出力名");
            return false;
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
            IEnumerable<MatsukawaRendererEntry> keptEntries)
        {
            // Only auto-create MA Merge Armature when the selected skinned part actually
            // uses the avatar's canonical Humanoid armature. A MeshRenderer-only accessory,
            // or a separate top-level rig, must not accidentally receive an Armature merge.
            var animator = root.GetComponent<Animator>();
            if (animator == null || animator.avatar == null || animator.isHuman is false)
                return "";

            var hips = animator.GetBoneTransform(HumanBodyBones.Hips);
            if (hips == null || hips.IsChildOf(root.transform) is false || hips.parent == null)
                return "";

            var armatureRoot = hips.parent;
            var usesAvatarArmature = false;

            foreach (var entry in keptEntries)
            {
                if (entry.Renderer is not SkinnedMeshRenderer smr) continue;

                if (smr.rootBone != null
                    && (smr.rootBone == armatureRoot || smr.rootBone.IsChildOf(armatureRoot)))
                {
                    usesAvatarArmature = true;
                    break;
                }

                foreach (var bone in smr.bones ?? Array.Empty<Transform>())
                {
                    if (bone == null) continue;
                    if (bone == armatureRoot || bone.IsChildOf(armatureRoot))
                    {
                        usesAvatarArmature = true;
                        break;
                    }
                }

                if (usesAvatarArmature) break;
            }

            return usesAvatarArmature
                ? RelativePath(root.transform, armatureRoot)
                : "";
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

        internal static Renderer[] GetAtlasCandidateRenderers(
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
            return AtlasTexture.FilterExistUVChannel(
                domain,
                targetRenderers,
                atlasSettings.AtlasSetting.AtlasTargetUVChannel
            );
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
            AtlasTexture atlasSettings,
            GameObject root,
            IReadOnlyCollection<string> includedRendererKeys)
        {
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            using var domain = new NotWorkDomain(renderers, null);

            var candidates = GetAtlasCandidateRenderers(atlasSettings, root);
            var candidateKeys = candidates.ToDictionary(
                renderer => GetRendererKey(root, renderer),
                renderer => renderer,
                StringComparer.Ordinal
            );

            var missingKeys = includedRendererKeys
                .Where(key => candidateKeys.ContainsKey(key) is false)
                .OrderBy(key => key, StringComparer.Ordinal)
                .ToArray();

            if (missingKeys.Length > 0)
            {
                throw new InvalidOperationException(
                    "選択されていたアトラス化対象Rendererが抽出結果に存在しないか、"
                    + "現在のAtlasTexture設定では対象外になりました。\n"
                    + string.Join("\n", missingKeys.Take(20))
                );
            }

            var targetRenderers = includedRendererKeys
                .Select(key => candidateKeys[key])
                .ToArray();

            var targetMaterials = atlasSettings
                .GetTargetMaterials(domain, targetRenderers.ToList())
                .ToHashSet();

            return (targetMaterials, targetRenderers);
        }

        private static void PreserveExistingTrimMeshGuids(
            GameObject extractionRoot,
            IReadOnlyList<string> generatedMeshPaths,
            string outputFolder,
            string backupRoot)
        {
            if (generatedMeshPaths.Count == 0) return;

            var normalizedOutput = outputFolder.TrimEnd('/') + "/";
            var contentBackup = Path.Combine(backupRoot, "Content");

            foreach (var assetPath in generatedMeshPaths.Distinct(StringComparer.Ordinal))
            {
                if (string.IsNullOrEmpty(assetPath)
                    || assetPath.StartsWith(normalizedOutput, StringComparison.Ordinal) is false)
                {
                    continue;
                }

                var relativeAssetPath = assetPath.Substring(normalizedOutput.Length)
                    .Replace('/', Path.DirectorySeparatorChar);
                var backupMeta = Path.Combine(contentBackup, relativeAssetPath + ".meta");
                if (File.Exists(backupMeta) is false) continue;

                var affectedRenderers = extractionRoot
                    .GetComponentsInChildren<SkinnedMeshRenderer>(true)
                    .Where(renderer =>
                        renderer.sharedMesh != null
                        && string.Equals(
                            AssetDatabase.GetAssetPath(renderer.sharedMesh),
                            assetPath,
                            StringComparison.Ordinal
                        )
                    )
                    .ToArray();

                var currentMeta = AssetPathToFullPath(assetPath) + ".meta";
                if (File.Exists(currentMeta) is false)
                    throw new FileNotFoundException(
                        "生成されたTrim Meshのmetaが見つかりません。",
                        currentMeta
                    );

                // HCE recreates trim Mesh assets with DeleteAsset/CreateAsset. Restoring the
                // previous .meta GUID here keeps external references to that generated Mesh valid
                // across an overwrite while retaining the newly generated Mesh contents.
                File.Copy(backupMeta, currentMeta, true);
                AssetDatabase.ImportAsset(
                    assetPath,
                    ImportAssetOptions.ForceUpdate
                    | ImportAssetOptions.ForceSynchronousImport
                );

                var persistentMesh = AssetDatabase.LoadAssetAtPath<Mesh>(assetPath);
                if (persistentMesh == null)
                    throw new InvalidOperationException(
                        "GUID復元後のTrim Meshを読み込めません: " + assetPath
                    );

                foreach (var renderer in affectedRenderers)
                {
                    renderer.sharedMesh = persistentMesh;
                    EditorUtility.SetDirty(renderer);
                }
            }
        }

        private static (HashSet<Material> targetMaterials, Renderer[] targetRenderers)
            ResolveRetainedAtlasTargets(
                AtlasTexture atlasSettings,
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
                    "HCE抽出後に、選択済みのアトラス対象Rendererが失われました。"
                    + "抽出対象とアトラス対象の組み合わせを確認してください。"
                );
            }

            var renderers = retainedRenderers.ToArray();
            using var domain = new NotWorkDomain(
                root.GetComponentsInChildren<Renderer>(true),
                null
            );

            var targetMaterials = atlasSettings
                .GetTargetMaterials(domain, renderers.ToList())
                .ToHashSet();

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

        private static string CreateOutputFolderBackup(string outputAssetPath)
        {
            var sourceFullPath = AssetPathToFullPath(outputAssetPath);
            if (Directory.Exists(sourceFullPath) is false)
                throw new DirectoryNotFoundException(sourceFullPath);

            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName
                ?? throw new InvalidOperationException("Unity project root was not found.");

            var backupRoot = Path.Combine(
                projectRoot,
                "Library",
                "TexTransTool",
                "PartAtlasBackups",
                Guid.NewGuid().ToString("N")
            );
            var contentBackup = Path.Combine(backupRoot, "Content");
            Directory.CreateDirectory(contentBackup);
            CopyDirectory(sourceFullPath, contentBackup);

            var sourceMeta = sourceFullPath + ".meta";
            if (File.Exists(sourceMeta))
                File.Copy(sourceMeta, Path.Combine(backupRoot, "Folder.meta"), true);

            return backupRoot;
        }

        private static void RestoreOutputFolderBackup(
            string outputAssetPath,
            string backupRoot)
        {
            var outputFullPath = AssetPathToFullPath(outputAssetPath);
            var contentBackup = Path.Combine(backupRoot, "Content");
            if (Directory.Exists(contentBackup) is false)
                throw new DirectoryNotFoundException(contentBackup);

            if (AssetDatabase.IsValidFolder(outputAssetPath))
            {
                if (AssetDatabase.DeleteAsset(outputAssetPath) is false)
                    throw new IOException("現在の出力フォルダを削除できませんでした: " + outputAssetPath);
            }

            if (Directory.Exists(outputFullPath))
                Directory.Delete(outputFullPath, true);
            if (File.Exists(outputFullPath + ".meta"))
                File.Delete(outputFullPath + ".meta");

            Directory.CreateDirectory(outputFullPath);
            CopyDirectory(contentBackup, outputFullPath);

            var backupMeta = Path.Combine(backupRoot, "Folder.meta");
            if (File.Exists(backupMeta))
                File.Copy(backupMeta, outputFullPath + ".meta", true);

            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        }

        private static void TryDeleteBackupDirectory(string backupRoot)
        {
            try
            {
                if (Directory.Exists(backupRoot))
                    Directory.Delete(backupRoot, true);
            }
            catch (Exception e)
            {
                Debug.LogWarning(
                    "TexTransTool Part Atlas Prefab: 一時バックアップを削除できませんでした: "
                    + backupRoot
                    + "\n"
                    + e.Message
                );
            }
        }

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);

            foreach (var file in Directory.GetFiles(source))
            {
                File.Copy(
                    file,
                    Path.Combine(destination, Path.GetFileName(file)),
                    true
                );
            }

            foreach (var directory in Directory.GetDirectories(source))
            {
                CopyDirectory(
                    directory,
                    Path.Combine(destination, Path.GetFileName(directory))
                );
            }
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
                if (component == null || component is Transform) continue;

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
                    if (referencedTransform.IsChildOf(fullPrefabRoot.transform) is false) continue;
                    if (referencedTransform.IsChildOf(extractionRoot.transform)) continue;

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
