#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using net.rs64.TexTransTool.TextureAtlas.IslandSizePriorityTuner;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace net.rs64.TexTransTool.TextureAtlas.Editor
{
    internal sealed class PartAtlasPrefabWindow : EditorWindow
    {
        private PartAtlasPrefabSettings? _atlasSettings;
        private SerializedObject? _atlasSettingsObject;
        private MatsukawaAdapter? _matsukawa;
        private string _adapterError = "";

        private GameObject? _sourcePrefab;
        private GameObject? _loadedPrefabRoot;
        private GameObject? _extractionRoot;
        private string? _extractionRootPath;
        private readonly HashSet<string> _validRootPaths = new(StringComparer.Ordinal);

        private List<MatsukawaRendererEntry> _entries = new();
        private readonly List<Material> _materialCandidates = new();
        private readonly List<List<Material>> _materialGroups = new();
        private MatsukawaOptions _options = new();
        private MatsukawaAnalysis? _analysis;

        private TreeViewState? _hierarchyTreeState;
        private PartAtlasPrefabHierarchyView? _hierarchyView;

        private string _filter = "";
        private string _outputName = "";
        private Vector2 _mainScroll;
        private Vector2 _rendererScroll;
        private bool _showOptions = true;
        private bool _showProtected = true;

        private float _leftPaneWidth = 320f;
        private float _rightPaneContentWidth = RightPaneMinWidth;
        private GameObject? _rendererLinkHighlightObject;
        private double _rendererLinkHighlightUntil;

        private const float SplitterWidth = 5f;
        private const float LeftPaneMinWidth = 240f;
        private const float RightPaneMinWidth = 560f;

        [MenuItem("Tools/TexTransTool/WDT/Prefab抽出・アトラス化...")]
        private static void OpenFromMenu()
        {
            Open();
        }

        internal static void Open()
        {
            var window = GetWindow<PartAtlasPrefabWindow>();
            window.titleContent = new GUIContent("TTT Prefab抽出");
            window.minSize = new Vector2(900f, 680f);
            window.EnsureAtlasSettings();
            window.InitializeAdapter();
            window.Show();
        }

        private void OnEnable()
        {
            EnsureAtlasSettings();
            EnsureHierarchyView();
            InitializeAdapter();

            if (_loadedPrefabRoot != null)
                _hierarchyView?.SetRoot(_loadedPrefabRoot);
        }

        private void OnDisable()
        {
            UnloadSourcePrefab();
            DestroyAtlasSettings();
        }

        private void EnsureAtlasSettings()
        {
            if (_atlasSettings != null && _atlasSettingsObject != null)
            {
                return;
            }

            DestroyAtlasSettings();

            _atlasSettings = ScriptableObject.CreateInstance<PartAtlasPrefabSettings>();
            // HideAndDontSave contains HideFlags.NotEditable, which makes SerializedProperty
            // controls read-only. DontSave keeps this transient without disabling editing.
            _atlasSettings.hideFlags = HideFlags.DontSave;
            _atlasSettingsObject = new SerializedObject(_atlasSettings);
        }

        private void DestroyAtlasSettings()
        {
            _atlasSettingsObject = null;

            if (_atlasSettings != null)
            {
                DestroyImmediate(_atlasSettings);
                _atlasSettings = null;
            }
        }

        private void EnsureHierarchyView()
        {
            if (_hierarchyTreeState == null)
                _hierarchyTreeState = new TreeViewState();

            if (_hierarchyView != null)
                return;

            _hierarchyView = new PartAtlasPrefabHierarchyView(_hierarchyTreeState);
            _hierarchyView.SelectionChangedTransform += OnHierarchySelectionChanged;
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
            EnsureAtlasSettings();
            EnsureHierarchyView();

            _leftPaneWidth = Mathf.Clamp(
                _leftPaneWidth,
                LeftPaneMinWidth,
                Mathf.Max(LeftPaneMinWidth, position.width - RightPaneMinWidth - SplitterWidth)
            );

            var leftRect = new Rect(0f, 0f, _leftPaneWidth, position.height);
            var splitterRect = new Rect(_leftPaneWidth, 0f, SplitterWidth, position.height);
            var rightRect = new Rect(
                splitterRect.xMax,
                0f,
                Mathf.Max(0f, position.width - splitterRect.xMax),
                position.height
            );

            DrawHierarchyPane(leftRect);
            HandleSplitter(splitterRect);
            DrawRightPane(rightRect);

            if ((_hierarchyView?.HasActiveFlash ?? false)
                || EditorApplication.timeSinceStartup < _rendererLinkHighlightUntil)
            {
                Repaint();
            }
        }

        private void DrawHierarchyPane(Rect rect)
        {
            if (_hierarchyView == null) return;
            _hierarchyView.Draw(rect);
        }

        private void DrawRightPane(Rect rect)
        {
            GUILayout.BeginArea(rect);
            try
            {
                _rightPaneContentWidth = Mathf.Max(
                    1f,
                    rect.width
                    - GUI.skin.verticalScrollbar.fixedWidth
                    - 8f
                );

                using var scroll = new EditorGUILayout.ScrollViewScope(_mainScroll);
                _mainScroll = scroll.scrollPosition;

                DrawRightPaneContent();
            }
            finally
            {
                GUILayout.EndArea();
            }
        }

        private void DrawRightPaneContent()
        {
            if (_atlasSettings == null || _atlasSettingsObject == null)
            {
                EditorGUILayout.HelpBox(
                    "アトラス設定用の一時設定オブジェクトを初期化できませんでした。",
                    MessageType.Error
                );
                return;
            }

            DrawHeader();

            if (_matsukawa == null)
            {
                EditorGUILayout.HelpBox(
                    "松川怒りの髪衣装抽出ツール Ver1.1.0 が必要です。\n" + _adapterError,
                    MessageType.Error
                );
                if (GUILayout.Button("再検出")) InitializeAdapter();
                return;
            }

            DrawSource();
            if (_extractionRoot == null) return;

            DrawRendererSelection();
            DrawAtlasMaterialSelection();
            DrawAtlasSettings();
            DrawOptions();
            DrawAnalysis();
            DrawExecute();
        }

        private void HandleSplitter(Rect splitterRect)
        {
            EditorGUIUtility.AddCursorRect(splitterRect, MouseCursor.ResizeHorizontal);

            if (Event.current.type == EventType.Repaint)
            {
                EditorGUI.DrawRect(
                    splitterRect,
                    EditorGUIUtility.isProSkin
                        ? new Color(0.16f, 0.16f, 0.16f, 1f)
                        : new Color(0.72f, 0.72f, 0.72f, 1f)
                );
            }

            var controlId = GUIUtility.GetControlID(FocusType.Passive, splitterRect);
            var eventType = Event.current.GetTypeForControl(controlId);

            if (eventType == EventType.MouseDown
                && Event.current.button == 0
                && splitterRect.Contains(Event.current.mousePosition))
            {
                GUIUtility.hotControl = controlId;
                Event.current.Use();
            }
            else if (eventType == EventType.MouseDrag && GUIUtility.hotControl == controlId)
            {
                _leftPaneWidth = Mathf.Clamp(
                    Event.current.mousePosition.x,
                    LeftPaneMinWidth,
                    position.width - RightPaneMinWidth - SplitterWidth
                );
                Repaint();
                Event.current.Use();
            }
            else if (eventType == EventType.MouseUp && GUIUtility.hotControl == controlId)
            {
                GUIUtility.hotControl = 0;
                Event.current.Use();
            }
        }
        private void DrawHeader()
        {
            EditorGUILayout.LabelField("Prefab抽出・アトラス化", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Prefab Assetから必要なパーツを抽出し、このウィンドウ内で選択したMaterialとAtlas設定を使ってStandalone Prefabを生成します。元Prefab Assetは変更しません。",
                MessageType.Info
            );

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
                GUIUtility.ExitGUI();
            }

            if (_loadedPrefabRoot == null)
            {
                EditorGUILayout.HelpBox(
                    "Project上のPrefab Assetを指定してください。",
                    MessageType.None
                );
                return;
            }

            if (_validRootPaths.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "このPrefab内に抽出可能なMeshが見つかりません。",
                    MessageType.Warning
                );
                return;
            }

            var selectedTransform = _hierarchyView?.SelectedTransform;
            var selectedIsValidRoot = IsValidExtractionRoot(selectedTransform);

            var extractionRootDisplay = _extractionRootPath == null
                ? "[未設定]"
                : string.IsNullOrEmpty(_extractionRootPath)
                    ? "<Prefab Root>"
                    : _extractionRootPath;

            EditorGUILayout.LabelField(
                new GUIContent(
                    "抽出ルート",
                    "現在設定されている抽出ルートです。左のPrefab HierarchyでGameObjectを選択し、次の行のボタンで明示的に変更します。"
                ),
                new GUIContent(extractionRootDisplay)
            );

            var selectedDisplay = selectedTransform == null
                ? "[未選択]"
                : selectedTransform == _loadedPrefabRoot!.transform
                    ? "<Prefab Root>"
                    : RelativePath(_loadedPrefabRoot.transform, selectedTransform);

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(
                    new GUIContent(
                        "選択中",
                        "左のPrefab Hierarchyで現在選択しているGameObjectです。"
                    ),
                    new GUIContent(selectedDisplay)
                );

                using (new EditorGUI.DisabledScope(selectedIsValidRoot is false))
                {
                    if (GUILayout.Button(
                            "このGameObjectを抽出ルートに設定する",
                            GUILayout.Width(230f)))
                    {
                        SetExtractionRoot(selectedTransform!);
                        GUIUtility.ExitGUI();
                    }
                }
            }

            if (selectedTransform != null && selectedIsValidRoot is false)
            {
                EditorGUILayout.LabelField(
                    "選択中のGameObject配下には抽出可能なMeshがありません。",
                    EditorStyles.wordWrappedMiniLabel
                );
            }

            if (_extractionRoot == null)
            {
                EditorGUILayout.HelpBox(
                    "左のPrefab Hierarchyから抽出ルートを明示的に設定してください。",
                    MessageType.Info
                );
                EditorGUILayout.Space(4f);
                return;
            }

            _outputName = EditorGUILayout.TextField(
                new GUIContent(
                    "出力名",
                    "生成するStandalone PrefabのRoot名と、松川ツールの抽出フォルダ名に使用します。"
                ),
                _outputName
            );

            var sanitized = _matsukawa!.SanitizeName(_outputName.Trim());
            var outputFolder = string.IsNullOrWhiteSpace(sanitized)
                ? "-"
                : _matsukawa.GetOutputFolder(sanitized);
            EditorGUILayout.LabelField("出力", outputFolder, EditorStyles.wordWrappedMiniLabel);

            if (string.IsNullOrWhiteSpace(sanitized) is false
                && PartAtlasPrefabPipeline.TryValidateOutputName(
                    sanitized,
                    out var outputNameError
                ) is false)
            {
                EditorGUILayout.HelpBox(outputNameError, MessageType.Warning);
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

            var visible = VisibleEntries().ToList();
            using var view = new EditorGUILayout.ScrollViewScope(
                _rendererScroll,
                GUILayout.MinHeight(140f),
                GUILayout.MaxHeight(260f)
            );
            _rendererScroll = view.scrollPosition;

            using (new EditorGUILayout.VerticalScope(GUI.skin.box))
            {
                if (_entries.Count == 0)
                    EditorGUILayout.LabelField("メッシュが見つかりません。", EditorStyles.miniLabel);
                else if (visible.Count == 0)
                    EditorGUILayout.LabelField("絞り込みに一致するものがありません。", EditorStyles.miniLabel);

                foreach (var entry in visible)
                    DrawRendererRow(entry);
            }

            var unknownUnchecked = _entries.Count(entry =>
                entry.Keep is false
                && entry.Category == "Unknown"
            );
            if (unknownUnchecked > 0)
            {
                EditorGUILayout.HelpBox(
                    "用途が「不明」でチェックの入っていないRendererが "
                    + unknownUnchecked
                    + " 件あります。用途推定は名前からの推定なので、必要なものが混じっていないか確認してください。",
                    MessageType.Warning
                );
            }

            if (_options.BoneMode == MatsukawaBoneMode.WeightedOnly)
            {
                var unreadableEntries = _entries
                    .Where(entry =>
                        entry.Keep
                        && entry.IsSkinned
                        && RendererMesh(entry.Renderer) != null
                        && entry.MeshReadable is false
                    )
                    .ToArray();

                if (unreadableEntries.Length > 0)
                {
                    EditorGUILayout.HelpBox(
                        "残すSkinnedMeshRendererのうち "
                        + unreadableEntries.Length
                        + " 件でMeshのRead/Writeが無効です。松川ツールはそのRendererについてBone Weightを読めないため、ボーン配列の全ボーンを残します。",
                        MessageType.Warning
                    );

                    if (GUILayout.Button("これらのMeshのRead/Writeを有効にして再インポート"))
                    {
                        var sourcePath = _sourcePrefab != null
                            ? AssetDatabase.GetAssetPath(_sourcePrefab)
                            : "";
                        var meshes = unreadableEntries
                            .Select(entry => (entry.Renderer as SkinnedMeshRenderer)?.sharedMesh)
                            .Where(mesh => mesh != null)
                            .Cast<Mesh>()
                            .Distinct()
                            .ToArray();

                        var changed = _matsukawa!.EnableReadWrite(meshes);
                        EditorUtility.DisplayDialog(
                            "TTT Prefab化",
                            changed + " 個のModel ImporterでRead/Writeを有効にしました。",
                            "OK"
                        );

                        if (string.IsNullOrEmpty(sourcePath) is false)
                        {
                            SetSourcePrefab(
                                AssetDatabase.LoadAssetAtPath<GameObject>(sourcePath)
                            );
                        }

                        GUIUtility.ExitGUI();
                    }
                }
            }

            EditorGUILayout.Space(4f);
        }

        private void DrawRendererRow(MatsukawaRendererEntry entry)
        {
            var rect = GUILayoutUtility.GetRect(0f, 18f, GUILayout.ExpandWidth(true));
            var linkedHighlight =
                _rendererLinkHighlightObject == entry.Renderer.gameObject
                && EditorApplication.timeSinceStartup < _rendererLinkHighlightUntil;

            if (Event.current.type == EventType.Repaint)
            {
                if (linkedHighlight)
                    EditorGUI.DrawRect(rect, new Color(0.30f, 0.60f, 1.00f, 0.24f));
                else if (entry.Keep)
                    EditorGUI.DrawRect(rect, new Color(0.25f, 0.75f, 0.40f, 0.12f));
            }

            var x = rect.x + 2f;
            var toggleRect = new Rect(x, rect.y, 18f, rect.height);
            EditorGUI.BeginChangeCheck();
            entry.Keep = EditorGUI.Toggle(toggleRect, entry.Keep);
            if (EditorGUI.EndChangeCheck())
            {
                RefreshMaterialCandidates(preserveSelection: true);
                InvalidateAnalysis();
            }
            x += 20f;

            var categoryRect = new Rect(x, rect.y, 42f, rect.height);
            var previousColor = GUI.color;
            GUI.color = CategoryColor(entry.Category);
            GUI.Label(categoryRect, CategoryLabel(entry.Category), EditorStyles.miniLabel);
            GUI.color = previousColor;
            x += 44f;

            var right = rect.xMax;
            var available = right - x;
            var showBones = available > 190f;
            var showVertices = available > 280f;

            var boneRect = new Rect(right - 58f, rect.y, 56f, rect.height);
            var vertexRect = new Rect(right - 126f, rect.y, 66f, rect.height);
            var nameRight = right;
            if (showBones) nameRight = boneRect.x - 4f;
            if (showVertices) nameRight = vertexRect.x - 4f;
            var nameRect = new Rect(x, rect.y, Mathf.Max(20f, nameRight - x), rect.height);

            var displayName = string.IsNullOrEmpty(entry.Path)
                ? entry.Renderer.gameObject.name
                : entry.Path;
            var meshMissing = RendererMesh(entry.Renderer) == null;
            var label = displayName;
            var tooltip = displayName;

            if (meshMissing)
            {
                label += "  （メッシュ無し）";
                tooltip += "\nメッシュが割り当てられていません";
            }
            else if (entry.MeshReadable is false)
            {
                label += "  ⚠R/W無効";
                tooltip += "\nRead/Write が無効です";
            }

            tooltip += $"\n{entry.VertexCount:N0} 頂点 / ボーン {entry.BoneCount}";
            GUI.Label(nameRect, new GUIContent(label, tooltip), EditorStyles.label);

            if (showVertices)
                GUI.Label(
                    vertexRect,
                    meshMissing ? "-" : $"{entry.VertexCount:N0} 頂点",
                    EditorStyles.miniLabel
                );

            if (showBones)
                GUI.Label(
                    boneRect,
                    entry.IsSkinned ? $"骨 {entry.BoneCount}" : "静的",
                    EditorStyles.miniLabel
                );

            if (Event.current.type == EventType.MouseDown
                && rect.Contains(Event.current.mousePosition)
                && toggleRect.Contains(Event.current.mousePosition) is false)
            {
                _hierarchyView?.SelectAndReveal(entry.Renderer.transform, flash: true);
                Event.current.Use();
            }
        }

        private static string CategoryLabel(string category)
        {
            return category switch
            {
                "Hair" => "髪",
                "Costume" => "衣装",
                "Accessory" => "装飾",
                "Body" => "素体",
                _ => "不明",
            };
        }

        private static Color CategoryColor(string category)
        {
            return category switch
            {
                "Hair" => new Color(0.55f, 0.80f, 1.00f),
                "Costume" => new Color(0.60f, 1.00f, 0.65f),
                "Accessory" => new Color(1.00f, 0.85f, 0.50f),
                "Body" => new Color(1.00f, 0.60f, 0.60f),
                _ => Color.white,
            };
        }

        private void DrawAtlasMaterialSelection()
        {
            if (_extractionRoot == null
                || _atlasSettings == null
                || _atlasSettingsObject == null)
            {
                return;
            }

            _atlasSettingsObject.Update();
            var targetMaterials = _atlasSettingsObject.FindProperty(
                nameof(PartAtlasPrefabSettings.AtlasTargetMaterials)
            );

            EditorGUILayout.LabelField(
                $"アトラス化するMaterial（{_atlasSettings.AtlasTargetMaterials.Count(material => material != null)}/{_materialCandidates.Count}）",
                EditorStyles.boldLabel
            );

            using (new EditorGUILayout.HorizontalScope())
            {
                var buttonLayout = new[]
                {
                    GUILayout.MaxWidth(64f + 18f),
                    GUILayout.MinWidth(18f),
                    GUILayout.Height(18f),
                };

                using (new EditorGUI.DisabledScope(_materialCandidates.Count == 0))
                {
                    if (GUILayout.Button(
                            "AtlasTexture:button:SelectAll".GlcV(),
                            buttonLayout))
                    {
                        _atlasSettingsObject.ApplyModifiedProperties();
                        SetSelectedMaterials(_materialCandidates);
                        _atlasSettingsObject.Update();
                    }

                    if (GUILayout.Button(
                            "AtlasTexture:button:Invert".GlcV(),
                            buttonLayout))
                    {
                        _atlasSettingsObject.ApplyModifiedProperties();
                        var selected = _atlasSettings.AtlasTargetMaterials
                            .Where(material => material != null)
                            .Cast<Material>()
                            .ToHashSet();

                        SetSelectedMaterials(
                            _materialCandidates.Where(material =>
                                selected.Contains(material) is false)
                        );
                        _atlasSettingsObject.Update();
                    }
                }

                if (GUILayout.Button(
                        "AtlasTexture:button:RefreshMaterials".GetLocalize()))
                {
                    _atlasSettingsObject.ApplyModifiedProperties();
                    RefreshMaterialCandidates(preserveSelection: true);
                    _atlasSettingsObject.Update();
                }
            }

            if (_materialGroups.Count > 0)
            {
                AtlasTextureEditor.MaterialSelectEditor(
                    targetMaterials,
                    _materialGroups,
                    _rightPaneContentWidth
                );
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "現在の抽出対象RendererにはMaterialがありません。",
                    MessageType.Info
                );
            }

            if (_atlasSettingsObject.ApplyModifiedProperties())
            {
                SanitizeIslandSizePriorityMaterials();
                EditorUtility.SetDirty(_atlasSettings);
                _atlasSettingsObject.UpdateIfRequiredOrScript();
            }

            var automaticTargets = GetAutomaticAtlasRenderers();
            if (_atlasSettings.AtlasTargetMaterials.Any(material => material != null)
                && automaticTargets.Length > 0)
            {
                EditorGUILayout.LabelField(
                    $"選択Materialを使用する抽出Renderer {automaticTargets.Length} 件を自動的にアトラス化します。",
                    EditorStyles.wordWrappedMiniLabel
                );
            }

            EditorGUILayout.Space(4f);
        }

        private void DrawAtlasSettings()
        {
            if (_atlasSettings == null || _atlasSettingsObject == null) return;

            var previousLabelWidth = BeginWideLabels();
            try
            {
                _atlasSettingsObject.Update();

                var atlasSetting = _atlasSettingsObject.FindProperty(
                    nameof(PartAtlasPrefabSettings.AtlasSetting)
                );
                var islandSizePriority = _atlasSettingsObject.FindProperty(
                    nameof(PartAtlasPrefabSettings.MaterialSizePriorityTuners)
                );
                var mergeMaterialGroups = _atlasSettingsObject.FindProperty(
                    nameof(PartAtlasPrefabSettings.MergeMaterialGroups)
                );
                var allMaterialMergeReference = _atlasSettingsObject.FindProperty(
                    nameof(PartAtlasPrefabSettings.AllMaterialMergeReference)
                );

                var autoSize = atlasSetting.FindPropertyRelative("AutoAtlasTextureSize");
                var textureSize = atlasSetting.FindPropertyRelative("AtlasTextureSize");
                var customAspect = atlasSetting.FindPropertyRelative("CustomAspect");
                var heightSize = atlasSetting.FindPropertyRelative("AtlasTextureHeightSize");
                var uvChannel = atlasSetting.FindPropertyRelative("AtlasTargetUVChannel");
                var usePrimaryMaximum = atlasSetting.FindPropertyRelative("UsePrimaryMaximumTexture");
                var primaryTextureProperty = atlasSetting.FindPropertyRelative("PrimaryTextureProperty");
                var padding = atlasSetting.FindPropertyRelative("IslandPadding");
                var forceSizePriority = atlasSetting.FindPropertyRelative("ForceSizePriority");
                var forceSetTexture = atlasSetting.FindPropertyRelative("ForceSetTexture");
                var backgroundColor = atlasSetting.FindPropertyRelative("BackGroundColor");
                var pixelNormalize = atlasSetting.FindPropertyRelative("PixelNormalize");
                var textureFineTuning = atlasSetting.FindPropertyRelative("TextureFineTuning");

                EditorGUILayout.LabelField(
                    "AtlasTexture:label:IslandSizePriority".Glc(),
                    EditorStyles.boldLabel
                );
                using (new EditorGUI.IndentLevelScope(1))
                {
                    islandSizePriority.isExpanded = EditorGUILayout.Foldout(
                        islandSizePriority.isExpanded,
                        "AtlasTexture:prop:IslandSizePriorityTuner".Glc(),
                        true
                    );

                    if (islandSizePriority.isExpanded)
                    {
                        DrawMaterialIslandSizePriorityTuners(islandSizePriority);
                    }
                }

                EditorGUILayout.LabelField(
                    "AtlasTexture:label:MaterialSettings".Glc(),
                    EditorStyles.boldLabel
                );
                using (new EditorGUI.IndentLevelScope(1))
                {
                    EditorGUILayout.PropertyField(
                        mergeMaterialGroups,
                        "AtlasTexture:prop:MergeMaterialGroups".GlcV()
                    );
                    EditorGUILayout.PropertyField(
                        allMaterialMergeReference,
                        "AtlasTexture:prop:AllMaterialMergeReference".GlcV()
                    );
                }

                EditorGUILayout.LabelField(
                    "AtlasTexture:label:AtlasSettings".Glc(),
                    EditorStyles.boldLabel
                );
                using (new EditorGUI.IndentLevelScope(1))
                {
                    EditorGUILayout.PropertyField(
                        autoSize,
                        "AtlasTexture:prop:AutoAtlasTextureSize".GlcV()
                    );

                    using (new EditorGUI.DisabledScope(autoSize.boolValue))
                    {
                        EditorGUILayout.PropertyField(
                            textureSize,
                            "AtlasTexture:prop:AtlasTextureSize".GlcV()
                        );
                        if (customAspect.boolValue)
                        {
                            EditorGUILayout.PropertyField(
                                heightSize,
                                "AtlasTexture:prop:AtlasTextureHeightSize".GlcV()
                            );
                        }
                        EditorGUILayout.PropertyField(
                            customAspect,
                            "AtlasTexture:prop:CustomAspect".GlcV()
                        );
                    }

                    var uvBefore = uvChannel.enumValueIndex;
                    EditorGUILayout.PropertyField(
                        uvChannel,
                        "AtlasTexture:prop:AtlasTargetUVChannel".GlcV()
                    );

                    EditorGUILayout.PropertyField(
                        usePrimaryMaximum,
                        "AtlasTexture:prop:UsePrimaryMaximumTexture".GlcV()
                    );
                    if (usePrimaryMaximum.boolValue is false)
                    {
                        EditorGUILayout.PropertyField(
                            primaryTextureProperty,
                            "AtlasTexture:prop:PrimaryTextureProperty".GlcV()
                        );
                    }

                    EditorGUILayout.PropertyField(
                        padding,
                        "AtlasTexture:prop:Padding".GlcV()
                    );

                    EditorGUILayout.PropertyField(
                        forceSizePriority,
                        "AtlasTexture:prop:ForceSizePriority".GlcV()
                    );
                    EditorGUILayout.PropertyField(
                        forceSetTexture,
                        "AtlasTexture:prop:ForceSetTexture".GlcV()
                    );
                    EditorGUILayout.PropertyField(
                        backgroundColor,
                        "AtlasTexture:prop:BackGroundColor".GlcV()
                    );
                    EditorGUILayout.PropertyField(
                        pixelNormalize,
                        "AtlasTexture:prop:PixelNormalize".GlcV()
                    );
                    EditorGUILayout.PropertyField(
                        textureFineTuning,
                        "AtlasTexture:prop:TextureFineTuning".GlcV()
                    );

                    if (_atlasSettingsObject.ApplyModifiedProperties())
                    {
                        if (uvBefore != (int)_atlasSettings.AtlasSetting.AtlasTargetUVChannel)
                            RefreshMaterialGroups();

                        SanitizeIslandSizePriorityMaterials();
                        EditorUtility.SetDirty(_atlasSettings);
                        _atlasSettingsObject.UpdateIfRequiredOrScript();
                    }
                }
            }
            finally
            {
                EditorGUIUtility.labelWidth = previousLabelWidth;
            }

            EditorGUILayout.Space(4f);
        }

        private void DrawMaterialIslandSizePriorityTuners(SerializedProperty tuners)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("+ SetFromMaterial", EditorStyles.miniButton))
                {
                    var newIndex = tuners.arraySize;
                    tuners.arraySize += 1;

                    var newElement = tuners.GetArrayElementAtIndex(newIndex);
                    newElement.managedReferenceValue = new SetFromMaterial
                    {
                        PriorityValue = 1f,
                        Materials = new List<Material>(),
                    };
                }

                using (new EditorGUI.DisabledScope(tuners.arraySize == 0))
                {
                    if (GUILayout.Button("-", EditorStyles.miniButton, GUILayout.Width(28f)))
                    {
                        tuners.DeleteArrayElementAtIndex(tuners.arraySize - 1);
                    }
                }
            }

            var targetMaterials = _atlasSettings!.AtlasTargetMaterials
                .Where(material => material != null)
                .Cast<Material>()
                .ToArray();

            for (var i = 0; i < tuners.arraySize; i++)
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    EditorGUILayout.LabelField(
                        $"SetFromMaterial {i + 1}",
                        EditorStyles.miniBoldLabel
                    );
                    var selectorWidth = Mathf.Max(
                        1f,
                        _rightPaneContentWidth
                        - EditorStyles.helpBox.padding.horizontal
                        - EditorStyles.helpBox.margin.horizontal
                        - 24f
                    );

                    SetFromMaterialDrawer.DrawNow(
                        tuners.GetArrayElementAtIndex(i),
                        targetMaterials,
                        selectorWidth
                    );
                }
            }
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

            RefreshMaterialCandidates(preserveSelection: true);
            InvalidateAnalysis();
        }

        private void DrawOptions()
        {
            if (_extractionRoot == null) return;

            _showOptions = EditorGUILayout.Foldout(_showOptions, "抽出設定", true);
            if (!_showOptions) return;

            var previousLabelWidth = BeginWideLabels();
            try
            {
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
                        new GUIContent(
                            "ウェイトのしきい値",
                            "これ以下のウェイトしか持たないボーンは使っていないと見なします。"
                        ),
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

                var avatarRootExtraction = IsAvatarRootExtraction();
                using (new EditorGUI.DisabledScope(avatarRootExtraction))
                {
                    var stripValue = avatarRootExtraction
                        ? true
                        : _options.StripAvatarComponents;
                    var nextStrip = EditorGUILayout.Toggle(
                        "アバター用コンポーネントを外す",
                        stripValue
                    );
                    if (avatarRootExtraction is false)
                        _options.StripAvatarComponents = nextStrip;
                }

                if (avatarRootExtraction)
                {
                    EditorGUILayout.HelpBox(
                        "Avatar Rootを抽出ルートにしているため、出力をAvatarではなくPart Prefabにする目的でAnimator / AvatarDescriptor / Pipeline系Componentは自動的に外します。利用可能な場合は抽出ArmatureへMA Merge Armature / MA Outfit Rootも自動設定します。",
                        MessageType.Info
                    );
                }

                if (EditorGUI.EndChangeCheck())
                    InvalidateAnalysis();
            }
            finally
            {
                EditorGUIUtility.labelWidth = previousLabelWidth;
            }

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
                        InvalidateAnalysis();
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
        }

        private void Analyze()
        {
            if (_matsukawa == null || _extractionRoot == null) return;

            _analysis = _matsukawa.Analyze(_extractionRoot, _entries, _options);
            _hierarchyView?.SetAnalysis(_analysis);
            Repaint();
        }

        private void InvalidateAnalysis()
        {
            _analysis = null;
            _hierarchyView?.SetAnalysis(null);
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
                        InvalidateAnalysis();
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

        private void DrawExecute()
        {
            if (_analysis == null || _sourcePrefab == null || _extractionRoot == null) return;

            EditorGUILayout.Space(8f);
            var automaticTargets = GetAutomaticAtlasRenderers();

            using (new EditorGUI.DisabledScope(
                       _entries.All(entry => entry.Keep is false)
                       || automaticTargets.Length == 0))
            {
                if (GUILayout.Button("抽出 → Atlas → Prefab保存", GUILayout.Height(32f)))
                    Execute();
            }

            if (_atlasSettings == null
                || _atlasSettings.AtlasTargetMaterials.Any(material => material != null) is false)
            {
                EditorGUILayout.HelpBox(
                    "アトラス化するMaterialを1件以上選択してください。",
                    MessageType.Warning
                );
            }
            else if (automaticTargets.Length == 0)
            {
                EditorGUILayout.HelpBox(
                    "選択したMaterialを使用する抽出対象Rendererに、アトラス化可能なRendererがありません。",
                    MessageType.Warning
                );
            }
        }

        private void Execute()
        {
            if (_matsukawa == null
                || _atlasSettings == null
                || _sourcePrefab == null
                || _extractionRoot == null)
                return;

            var sanitizedOutputName = _matsukawa.SanitizeName(_outputName.Trim());
            if (string.IsNullOrWhiteSpace(sanitizedOutputName))
            {
                EditorUtility.DisplayDialog("TTT Prefab化", "出力名を入力してください。", "OK");
                return;
            }

            var outputFolder = _matsukawa.GetOutputFolder(sanitizedOutputName);
            var overwrite = AssetDatabase.IsValidFolder(outputFolder);

            var unknownUnchecked = _entries.Count(entry =>
                entry.Keep is false
                && entry.Category == "Unknown"
            );
            var atlasCount = GetAutomaticAtlasRenderers().Length;
            var deleteCount = _analysis?.DeleteTransformCount ?? 0;

            var confirm =
                "Prefab Asset「"
                + _sourcePrefab.name
                + "」の一時コピーから "
                + deleteCount
                + " 個のオブジェクトを削除し、"
                + atlasCount
                + " 件のRendererをアトラス化してPrefab保存します。"
                + "\n\n出力: "
                + outputFolder;

            if (unknownUnchecked > 0)
            {
                confirm +=
                    "\n\n⚠ 用途が「不明」でチェックの入っていないRendererが "
                    + unknownUnchecked
                    + " 件あります。残すべきものが混じっていないか確認してください。";
            }

            if (overwrite)
            {
                confirm +=
                    "\n\n既存の抽出結果があります。既存Assetを可能な限り同じGUIDのまま更新します。"
                    + "失敗時は事前バックアップから復元します。";
            }

            if (EditorUtility.DisplayDialog(
                    "TTT Prefab化",
                    confirm + "\n\n実行しますか？",
                    overwrite ? "上書きして実行" : "実行",
                    "キャンセル"
                ) is false)
            {
                return;
            }

            var request = new PartAtlasPrefabRequest
            {
                SourcePrefabAsset = _sourcePrefab,
                ExtractionRootPath = _extractionRootPath ?? "",
                ExtractionOptions = _options,
                AtlasSettings = _atlasSettings,
                OutputName = sanitizedOutputName,
                OverwriteExistingOutput = overwrite,
            };

            var keptKeys = _entries
                .Where(entry => entry.Keep)
                .Select(entry => PartAtlasPrefabPipeline.GetRendererKey(
                    _extractionRoot,
                    entry.Renderer
                ))
                .ToHashSet(StringComparer.Ordinal);

            foreach (var key in keptKeys)
                request.KeepRendererKeys.Add(key);

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
            if (EditorUtility.IsPersistent(source) is false
                || PrefabUtility.IsPartOfPrefabAsset(source) is false)
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

            BuildValidRootPaths();
            _hierarchyView?.SetRoot(_loadedPrefabRoot);

            _extractionRootPath = null;
            _extractionRoot = null;
            _entries.Clear();
            _outputName = "";
            RefreshMaterialCandidates(preserveSelection: false);
            InvalidateAnalysis();
        }

        private void BuildValidRootPaths()
        {
            _validRootPaths.Clear();
            if (_loadedPrefabRoot == null) return;

            var root = _loadedPrefabRoot.transform;
            var validRoots = new HashSet<Transform>();

            foreach (var renderer in _loadedPrefabRoot.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer is not SkinnedMeshRenderer && renderer is not MeshRenderer)
                    continue;
                if (RendererMesh(renderer) == null)
                    continue;

                var current = renderer.transform;
                while (current != null)
                {
                    validRoots.Add(current);
                    if (current == root) break;
                    current = current.parent;
                }
            }

            foreach (var transform in _loadedPrefabRoot.GetComponentsInChildren<Transform>(true))
            {
                if (validRoots.Contains(transform))
                    _validRootPaths.Add(RelativePath(root, transform));
            }
        }

        private bool IsValidExtractionRoot(Transform? transform)
        {
            if (_loadedPrefabRoot == null || transform == null)
                return false;

            if (transform != _loadedPrefabRoot.transform
                && transform.IsChildOf(_loadedPrefabRoot.transform) is false)
            {
                return false;
            }

            var path = RelativePath(_loadedPrefabRoot.transform, transform);
            return _validRootPaths.Contains(path);
        }

        private void SetExtractionRoot(Transform transform)
        {
            if (_loadedPrefabRoot == null || IsValidExtractionRoot(transform) is false)
                return;

            _extractionRootPath = RelativePath(_loadedPrefabRoot.transform, transform);
            RefreshExtractionRoot();
            _hierarchyView?.SelectAndReveal(transform, flash: true);
        }

        private void RefreshExtractionRoot()
        {
            InvalidateAnalysis();
            _entries.Clear();

            if (_loadedPrefabRoot == null
                || _matsukawa == null
                || _extractionRootPath == null)
            {
                _extractionRoot = null;
                RefreshMaterialCandidates(preserveSelection: false);
                return;
            }

            _extractionRoot = string.IsNullOrEmpty(_extractionRootPath)
                ? _loadedPrefabRoot
                : _loadedPrefabRoot.transform.Find(_extractionRootPath)?.gameObject;

            if (_extractionRoot == null)
            {
                _extractionRootPath = null;
                RefreshMaterialCandidates(preserveSelection: false);
                return;
            }

            _entries = _matsukawa.CollectRenderers(_extractionRoot).ToList();
            _outputName = _extractionRoot.name;
            RefreshMaterialCandidates(preserveSelection: false);
        }

        private void RefreshMaterialCandidates(bool preserveSelection)
        {
            if (_atlasSettings == null)
            {
                _materialCandidates.Clear();
                _materialGroups.Clear();
                return;
            }

            var previousSelection = preserveSelection
                ? _atlasSettings.AtlasTargetMaterials
                    .Where(material => material != null)
                    .Cast<Material>()
                    .ToHashSet()
                : new HashSet<Material>();

            _materialCandidates.Clear();

            if (_extractionRoot != null)
            {
                _materialCandidates.AddRange(
                    _entries
                        .Where(entry => entry.Keep && entry.Renderer != null)
                        .SelectMany(entry => entry.Renderer.sharedMaterials)
                        .Where(material => material != null)
                        .Cast<Material>()
                        .Distinct()
                        .OrderBy(material => material.name, StringComparer.Ordinal)
                        .ThenBy(material => AssetDatabase.GetAssetPath(material), StringComparer.Ordinal)
                );
            }

            _atlasSettings.AtlasTargetMaterials = _materialCandidates
                .Where(previousSelection.Contains)
                .Cast<Material?>()
                .ToList();

            SanitizeIslandSizePriorityMaterials();
            RefreshMaterialGroups();

            EditorUtility.SetDirty(_atlasSettings);
            _atlasSettingsObject?.UpdateIfRequiredOrScript();
        }

        private void RefreshMaterialGroups()
        {
            _materialGroups.Clear();
            if (_atlasSettings == null || _materialCandidates.Count == 0)
                return;

            _materialGroups.AddRange(
                new MaterialGroupingContext(
                    _materialCandidates.ToHashSet(),
                    _atlasSettings.AtlasSetting.AtlasTargetUVChannel,
                    null
                )
                .GroupMaterials
                .Select(group => new List<Material>(group))
            );
        }

        private void SetSelectedMaterials(IEnumerable<Material> materials)
        {
            if (_atlasSettings == null) return;

            var candidateSet = _materialCandidates.ToHashSet();
            var selectedSet = materials
                .Where(material => material != null && candidateSet.Contains(material))
                .ToHashSet();

            _atlasSettings.AtlasTargetMaterials = _materialCandidates
                .Where(selectedSet.Contains)
                .Cast<Material?>()
                .ToList();

            SanitizeIslandSizePriorityMaterials();
            EditorUtility.SetDirty(_atlasSettings);
            _atlasSettingsObject?.UpdateIfRequiredOrScript();
        }

        private void SanitizeIslandSizePriorityMaterials()
        {
            if (_atlasSettings == null) return;

            var selected = _atlasSettings.AtlasTargetMaterials
                .Where(material => material != null)
                .Cast<Material>()
                .ToHashSet();

            foreach (var tuner in _atlasSettings.MaterialSizePriorityTuners)
            {
                if (tuner?.Materials == null) continue;

                tuner.Materials = tuner.Materials
                    .Where(material => material != null && selected.Contains(material))
                    .Distinct()
                    .ToList();
            }
        }

        private Renderer[] GetAutomaticAtlasRenderers()
        {
            if (_extractionRoot == null || _atlasSettings == null)
                return Array.Empty<Renderer>();

            var keptRenderers = _entries
                .Where(entry => entry.Keep && entry.Renderer != null)
                .Select(entry => entry.Renderer)
                .ToHashSet();

            if (keptRenderers.Count == 0)
                return Array.Empty<Renderer>();

            return PartAtlasPrefabPipeline
                .GetAtlasCandidateRenderers(_atlasSettings, _extractionRoot)
                .Where(keptRenderers.Contains)
                .ToArray();
        }

        private void UnloadSourcePrefab()
        {
            InvalidateAnalysis();
            _entries.Clear();
            _materialCandidates.Clear();
            _materialGroups.Clear();
            _outputName = "";
            _extractionRoot = null;
            _extractionRootPath = null;
            _validRootPaths.Clear();
            _rendererLinkHighlightObject = null;
            _rendererLinkHighlightUntil = 0d;

            _hierarchyView?.SetRoot(null);

            if (_atlasSettings != null)
            {
                _atlasSettings.AtlasTargetMaterials.Clear();
                _atlasSettings.MaterialSizePriorityTuners.Clear();
                EditorUtility.SetDirty(_atlasSettings);
                _atlasSettingsObject?.UpdateIfRequiredOrScript();
            }

            if (_loadedPrefabRoot != null)
            {
                try { PrefabUtility.UnloadPrefabContents(_loadedPrefabRoot); }
                catch (Exception e) { Debug.LogException(e); }
                _loadedPrefabRoot = null;
            }
        }

        private void OnHierarchySelectionChanged(Transform? transform)
        {
            Repaint();

            if (transform == null || _extractionRoot == null)
                return;

            var visible = VisibleEntries().ToList();
            var index = visible.FindIndex(entry =>
                entry.Renderer != null
                && entry.Renderer.gameObject == transform.gameObject
            );

            if (index < 0)
                return;

            _rendererScroll.y = Mathf.Max(0f, index * 18f - 36f);
            _rendererLinkHighlightObject = transform.gameObject;
            _rendererLinkHighlightUntil = EditorApplication.timeSinceStartup + 0.75d;
            Repaint();
        }

        private float BeginWideLabels()
        {
            var previous = EditorGUIUtility.labelWidth;
            var rightPaneWidth = Mathf.Max(
                0f,
                position.width - _leftPaneWidth - SplitterWidth
            );
            EditorGUIUtility.labelWidth = Mathf.Clamp(
                rightPaneWidth - 60f,
                150f,
                320f
            );
            return previous;
        }

        private static Mesh? RendererMesh(Renderer renderer)
        {
            return renderer switch
            {
                SkinnedMeshRenderer skinned => skinned.sharedMesh,
                MeshRenderer meshRenderer => meshRenderer.GetComponent<MeshFilter>()?.sharedMesh,
                _ => null,
            };
        }

        private bool IsAvatarRootExtraction()
        {
            if (_extractionRoot == null || _loadedPrefabRoot == null) return false;
            if (_extractionRoot != _loadedPrefabRoot) return false;

            return _extractionRoot
                .GetComponents<Component>()
                .Where(component => component != null)
                .Any(component => component.GetType().Name == "VRCAvatarDescriptor");
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
