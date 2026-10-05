#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace net.rs64.TexTransTool.TextureAtlas.Editor
{
    internal sealed class PartAtlasPrefabWindow : EditorWindow
    {
        private AtlasTexture? _atlasSettings;
        private MatsukawaAdapter? _matsukawa;
        private string _adapterError = "";

        private GameObject? _sourcePrefab;
        private GameObject? _loadedPrefabRoot;
        private GameObject? _extractionRoot;
        private string[] _rootPaths = Array.Empty<string>();
        private string[] _rootLabels = Array.Empty<string>();
        private int _rootIndex;

        private List<MatsukawaRendererEntry> _entries = new();
        private MatsukawaOptions _options = new();
        private MatsukawaAnalysis? _analysis;

        private string _filter = "";
        private Vector2 _mainScroll;
        private Vector2 _rendererScroll;
        private Vector2 _hierarchyScroll;
        private bool _showOptions = true;
        private bool _showHierarchy = true;
        private bool _showProtected = true;

        internal static void Open(AtlasTexture atlasSettings)
        {
            var window = GetWindow<PartAtlasPrefabWindow>();
            window.titleContent = new GUIContent("TTT Prefab化");
            window.minSize = new Vector2(520f, 620f);
            window._atlasSettings = atlasSettings;
            window.InitializeAdapter();
            window.Show();
        }

        private void OnEnable()
        {
            InitializeAdapter();
        }

        private void OnDisable()
        {
            UnloadSourcePrefab();
        }

        private void InitializeAdapter()
        {
            if (MatsukawaAdapter.TryCreate(out var adapter, out var error))
            {
                _matsukawa = adapter;
                _adapterError = "";
            }
            else
            {
                _matsukawa = null;
                _adapterError = error;
            }
        }

        private void OnGUI()
        {
            if (_atlasSettings == null)
            {
                EditorGUILayout.HelpBox(
                    "AtlasTexture設定元が失われました。AtlasTexture InspectorからPrefab化を開き直してください。",
                    MessageType.Error
                );
                return;
            }

            if (_matsukawa == null)
            {
                EditorGUILayout.HelpBox(
                    "松川怒りの髪衣装抽出ツール Ver1.1.0 が必要です。\n" + _adapterError,
                    MessageType.Error
                );
                if (GUILayout.Button("再検出")) InitializeAdapter();
                return;
            }

            using var scroll = new EditorGUILayout.ScrollViewScope(_mainScroll);
            _mainScroll = scroll.scrollPosition;

            DrawHeader();
            DrawSource();
            DrawRendererSelection();
            DrawOptions();
            DrawAnalysis();
            DrawExecute();
        }

        private void DrawHeader()
        {
            EditorGUILayout.LabelField("Prefab化", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Prefab Assetを入力として松川ツールの依存解析・抽出を行い、その抽出結果へ現在のAtlasTexture設定を適用してStandalone Prefabを生成します。元Prefab Assetは変更しません。",
                MessageType.Info
            );

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.ObjectField("Atlas設定", _atlasSettings, typeof(AtlasTexture), true);
            }

            EditorGUILayout.Space(4f);
        }

        private void DrawSource()
        {
            EditorGUILayout.LabelField("入力", EditorStyles.boldLabel);

            var next = EditorGUILayout.ObjectField(
                new GUIContent("Prefab Asset"),
                _sourcePrefab,
                typeof(GameObject),
                false
            ) as GameObject;

            if (next != _sourcePrefab)
            {
                SetSourcePrefab(next);
            }

            if (_loadedPrefabRoot == null)
            {
                EditorGUILayout.HelpBox("Project上のPrefab Assetを指定してください。", MessageType.None);
                return;
            }

            EditorGUI.BeginChangeCheck();
            var nextRoot = EditorGUILayout.Popup(
                new GUIContent(
                    "抽出ルート",
                    "元衣装Prefabは通常Root、着用済み衣装はAvatar内の衣装Root、デフォルト衣装はAvatar Rootを選択します。"
                ),
                _rootIndex,
                _rootLabels
            );
            if (EditorGUI.EndChangeCheck())
            {
                _rootIndex = Mathf.Clamp(nextRoot, 0, Math.Max(0, _rootPaths.Length - 1));
                RefreshExtractionRoot();
            }

            if (_extractionRoot != null)
            {
                var outputFolder = _matsukawa!.GetOutputFolder(_extractionRoot.name);
                EditorGUILayout.LabelField("出力", outputFolder, EditorStyles.wordWrappedMiniLabel);
            }

            EditorGUILayout.Space(4f);
        }

        private void DrawRendererSelection()
        {
            if (_extractionRoot == null) return;

            var keptCount = _entries.Count(entry => entry.Keep);
            EditorGUILayout.LabelField(
                $"抽出するRenderer（{keptCount}/{_entries.Count}）",
                EditorStyles.boldLabel
            );

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("髪＋衣装", EditorStyles.miniButton))
                    SelectBy(entry => entry.Category is "Hair" or "Costume");
                if (GUILayout.Button("髪だけ", EditorStyles.miniButton))
                    SelectBy(entry => entry.Category == "Hair");
                if (GUILayout.Button("衣装だけ", EditorStyles.miniButton))
                    SelectBy(entry => entry.Category == "Costume");
                if (GUILayout.Button("素体以外", EditorStyles.miniButton))
                    SelectBy(entry => entry.Category != "Body");
                if (GUILayout.Button("全解除", EditorStyles.miniButton))
                    SelectBy(_ => false);
            }

            _filter = EditorGUILayout.TextField(_filter, EditorStyles.toolbarSearchField);

            using var view = new EditorGUILayout.ScrollViewScope(
                _rendererScroll,
                GUILayout.MinHeight(140f),
                GUILayout.MaxHeight(260f)
            );
            _rendererScroll = view.scrollPosition;

            foreach (var entry in VisibleEntries())
            {
                using var row = new EditorGUILayout.HorizontalScope();
                var nextKeep = EditorGUILayout.Toggle(entry.Keep, GUILayout.Width(18f));
                if (nextKeep != entry.Keep)
                {
                    entry.Keep = nextKeep;
                    _analysis = null;
                }

                var category = string.IsNullOrEmpty(entry.Category) ? "Unknown" : entry.Category;
                var stats = entry.IsSkinned
                    ? $"  [{category}]  v:{entry.VertexCount:N0} / bones:{entry.BoneCount}"
                    : $"  [{category}]  v:{entry.VertexCount:N0}";
                var label = entry.Path + stats;
                if (GUILayout.Button(new GUIContent(label, label), EditorStyles.label))
                {
                    Selection.activeObject = entry.Renderer.gameObject;
                    EditorGUIUtility.PingObject(entry.Renderer.gameObject);
                }
            }

            EditorGUILayout.Space(4f);
        }

        private IEnumerable<MatsukawaRendererEntry> VisibleEntries()
        {
            if (string.IsNullOrWhiteSpace(_filter)) return _entries;
            var filter = _filter.Trim();
            return _entries.Where(entry =>
                entry.Path.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0
                || entry.Renderer.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0
            );
        }

        private void SelectBy(Func<MatsukawaRendererEntry, bool> predicate)
        {
            foreach (var entry in _entries) entry.Keep = predicate(entry);
            _analysis = null;
        }

        private void DrawOptions()
        {
            if (_extractionRoot == null) return;

            _showOptions = EditorGUILayout.Foldout(_showOptions, "抽出設定", true);
            if (!_showOptions) return;

            EditorGUI.BeginChangeCheck();

            _options.BoneMode = (MatsukawaBoneMode)EditorGUILayout.Popup(
                "残すボーン",
                (int)_options.BoneMode,
                new[]
                {
                    "ウェイトのあるボーンだけ残す（推奨）",
                    "メッシュが参照しているボーンは全部残す",
                }
            );

            if (_options.BoneMode == MatsukawaBoneMode.WeightedOnly)
            {
                _options.WeightThreshold = EditorGUILayout.Slider(
                    new GUIContent("ウェイトのしきい値", "これ以下のウェイトしか持たないボーンは使っていないと見なします。"),
                    _options.WeightThreshold,
                    0f,
                    0.01f
                );
            }

            _options.KeepPhysBoneChains = EditorGUILayout.Toggle(
                "PhysBoneのチェーンとコライダーを残す",
                _options.KeepPhysBoneChains
            );
            _options.KeepReferencedObjects = EditorGUILayout.Toggle(
                "参照されているオブジェクトを残す",
                _options.KeepReferencedObjects
            );
            _options.ProtectOtherComponents = EditorGUILayout.Toggle(
                "他のコンポーネント付きは削除しない",
                _options.ProtectOtherComponents
            );
            _options.KeepHumanoidBones = EditorGUILayout.Toggle(
                "Humanoidボーンは常に残す",
                _options.KeepHumanoidBones
            );
            _options.StripAvatarComponents = EditorGUILayout.Toggle(
                "アバター用コンポーネントを外す",
                _options.StripAvatarComponents
            );

            if (EditorGUI.EndChangeCheck()) _analysis = null;
            EditorGUILayout.Space(4f);
        }

        private void DrawAnalysis()
        {
            if (_extractionRoot == null) return;

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("解析（プレビュー）", GUILayout.Height(26f)))
                    Analyze();

                using (new EditorGUI.DisabledScope(_analysis == null))
                {
                    if (GUILayout.Button("解析をクリア", GUILayout.Height(26f), GUILayout.Width(100f)))
                        _analysis = null;
                }
            }

            if (_analysis == null) return;

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                var after = _analysis.TotalTransforms - _analysis.DeleteTransformCount;
                EditorGUILayout.LabelField("解析結果", EditorStyles.boldLabel);
                EditorGUILayout.LabelField(
                    $"オブジェクト {_analysis.TotalTransforms} → {after} / 削除Renderer {_analysis.DeleteRendererCount} / Trim {_analysis.TrimTargetCount}",
                    EditorStyles.wordWrappedLabel
                );

                foreach (var warning in _analysis.Warnings)
                    EditorGUILayout.HelpBox(warning, MessageType.Warning);
            }

            DrawProtectedObjects();
            DrawHierarchyPreview();
        }

        private void Analyze()
        {
            if (_matsukawa == null || _extractionRoot == null) return;
            _analysis = _matsukawa.Analyze(_extractionRoot, _entries, _options);
            Repaint();
        }

        private void DrawProtectedObjects()
        {
            if (_analysis == null || _extractionRoot == null) return;

            _showProtected = EditorGUILayout.Foldout(
                _showProtected,
                $"保護して残すオブジェクト（{_analysis.ProtectedObjects.Count}）",
                true
            );
            if (!_showProtected) return;

            foreach (var gameObject in _analysis.ProtectedObjects
                         .Where(gameObject => gameObject != null)
                         .OrderBy(gameObject => RelativePath(_extractionRoot.transform, gameObject.transform)))
            {
                var path = RelativePath(_extractionRoot.transform, gameObject.transform);
                var currentlyForced = _options.ForceDeletePaths.Contains(path);

                using (new EditorGUILayout.HorizontalScope())
                {
                    var keep = EditorGUILayout.Toggle(!currentlyForced, GUILayout.Width(18f));
                    if (keep == currentlyForced)
                    {
                        if (keep) _options.ForceDeletePaths.Remove(path);
                        else if (!_options.ForceDeletePaths.Contains(path)) _options.ForceDeletePaths.Add(path);
                        Analyze();
                        GUIUtility.ExitGUI();
                    }

                    var components = string.Join(
                        ", ",
                        gameObject.GetComponents<Component>()
                            .Where(component => component != null && component is not Transform)
                            .Select(component => component.GetType().Name)
                    );
                    EditorGUILayout.LabelField(
                        new GUIContent(path + "  " + components, path + "\n" + components),
                        EditorStyles.miniLabel
                    );
                }
            }
        }

        private void DrawHierarchyPreview()
        {
            if (_analysis == null || _extractionRoot == null) return;

            _showHierarchy = EditorGUILayout.Foldout(
                _showHierarchy,
                "構造プレビュー（緑=残る / 赤=消える / 黄=保護）",
                true
            );
            if (!_showHierarchy) return;

            using var view = new EditorGUILayout.ScrollViewScope(
                _hierarchyScroll,
                GUILayout.MinHeight(180f),
                GUILayout.MaxHeight(320f)
            );
            _hierarchyScroll = view.scrollPosition;

            DrawTransformRow(_extractionRoot.transform, 0);
        }

        private void DrawTransformRow(Transform transform, int depth)
        {
            if (_analysis == null) return;

            var rowRect = GUILayoutUtility.GetRect(
                GUIContent.none,
                EditorStyles.label,
                GUILayout.Height(EditorGUIUtility.singleLineHeight)
            );

            Color? background = null;
            if (_analysis.DeleteTransforms.Contains(transform))
                background = new Color(0.90f, 0.25f, 0.25f, 0.22f);
            else if (_analysis.ProtectedObjects.Contains(transform.gameObject))
                background = new Color(0.95f, 0.75f, 0.15f, 0.25f);
            else if (_analysis.KeepTransforms.Contains(transform))
                background = new Color(0.25f, 0.75f, 0.40f, 0.22f);

            if (background.HasValue)
                EditorGUI.DrawRect(rowRect, background.Value);

            var labelRect = rowRect;
            labelRect.xMin += 4f + depth * 14f;
            EditorGUI.LabelField(labelRect, transform.name);

            for (var i = 0; i < transform.childCount; i++)
                DrawTransformRow(transform.GetChild(i), depth + 1);
        }

        private void DrawExecute()
        {
            if (_analysis == null || _sourcePrefab == null || _extractionRoot == null) return;

            EditorGUILayout.Space(8f);
            using (new EditorGUI.DisabledScope(_entries.All(entry => entry.Keep is false)))
            {
                if (GUILayout.Button("抽出 → Atlas → Prefab保存", GUILayout.Height(32f)))
                    Execute();
            }
        }

        private void Execute()
        {
            if (_matsukawa == null
                || _atlasSettings == null
                || _sourcePrefab == null
                || _extractionRoot == null)
                return;

            var outputFolder = _matsukawa.GetOutputFolder(_extractionRoot.name);
            var overwrite = false;
            if (AssetDatabase.IsValidFolder(outputFolder))
            {
                overwrite = EditorUtility.DisplayDialog(
                    "TTT Prefab化",
                    outputFolder + " は既に存在します。\n既存の抽出結果を削除して作り直しますか？",
                    "上書き",
                    "キャンセル"
                );
                if (!overwrite) return;
            }

            var request = new PartAtlasPrefabRequest
            {
                SourcePrefabAsset = _sourcePrefab,
                ExtractionRootPath = _rootPaths.Length > _rootIndex ? _rootPaths[_rootIndex] : "",
                ExtractionOptions = _options,
                AtlasSettings = _atlasSettings,
                OverwriteExistingOutput = overwrite,
            };

            foreach (var entry in _entries.Where(entry => entry.Keep))
                request.KeepRendererPaths.Add(entry.Path);

            var pipelineResult = PartAtlasPrefabPipeline.Execute(request);
            if (!pipelineResult.Success)
            {
                EditorUtility.DisplayDialog(
                    "TTT Prefab化",
                    "Prefab化に失敗しました。\n\n" + pipelineResult.Error,
                    "OK"
                );
                return;
            }

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(pipelineResult.PrefabPath);
            if (prefab != null)
            {
                Selection.activeObject = prefab;
                EditorGUIUtility.PingObject(prefab);
            }

            EditorUtility.DisplayDialog(
                "TTT Prefab化",
                "Prefab化が完了しました。\n\n" + pipelineResult.PrefabPath,
                "OK"
            );
        }

        private void SetSourcePrefab(GameObject? source)
        {
            UnloadSourcePrefab();
            _sourcePrefab = source;

            if (source == null) return;
            if (EditorUtility.IsPersistent(source) is false || PrefabUtility.IsPartOfPrefabAsset(source) is false)
            {
                _sourcePrefab = null;
                return;
            }

            var assetPath = AssetDatabase.GetAssetPath(source);
            if (string.IsNullOrEmpty(assetPath))
            {
                _sourcePrefab = null;
                return;
            }

            _loadedPrefabRoot = PrefabUtility.LoadPrefabContents(assetPath);
            if (_loadedPrefabRoot == null)
            {
                _sourcePrefab = null;
                return;
            }

            BuildRootPaths();
            _rootIndex = 0;
            RefreshExtractionRoot();
        }

        private void BuildRootPaths()
        {
            if (_loadedPrefabRoot == null)
            {
                _rootPaths = Array.Empty<string>();
                _rootLabels = Array.Empty<string>();
                return;
            }

            var paths = new List<string> { "" };
            foreach (var transform in _loadedPrefabRoot.GetComponentsInChildren<Transform>(true))
            {
                if (transform == _loadedPrefabRoot.transform) continue;
                paths.Add(RelativePath(_loadedPrefabRoot.transform, transform));
            }

            _rootPaths = paths.ToArray();
            _rootLabels = paths
                .Select(path => string.IsNullOrEmpty(path) ? "<Prefab Root>" : path)
                .ToArray();
        }

        private void RefreshExtractionRoot()
        {
            _analysis = null;
            _entries.Clear();

            if (_loadedPrefabRoot == null || _matsukawa == null) return;

            var path = _rootPaths.Length > _rootIndex ? _rootPaths[_rootIndex] : "";
            _extractionRoot = string.IsNullOrEmpty(path)
                ? _loadedPrefabRoot
                : _loadedPrefabRoot.transform.Find(path)?.gameObject;

            if (_extractionRoot == null) return;
            _entries = _matsukawa.CollectRenderers(_extractionRoot).ToList();
        }

        private void UnloadSourcePrefab()
        {
            _analysis = null;
            _entries.Clear();
            _extractionRoot = null;
            _rootPaths = Array.Empty<string>();
            _rootLabels = Array.Empty<string>();

            if (_loadedPrefabRoot != null)
            {
                try { PrefabUtility.UnloadPrefabContents(_loadedPrefabRoot); }
                catch (Exception e) { Debug.LogException(e); }
                _loadedPrefabRoot = null;
            }
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
}
