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
        private readonly HashSet<string> _extractionTargetPaths = new(StringComparer.Ordinal);
        private readonly HashSet<string> _validTargetPaths = new(StringComparer.Ordinal);
        private int _analysisScaffoldCount;

        private List<MatsukawaRendererEntry> _entries = new();
        private readonly List<Material> _materialCandidates = new();
        private readonly List<List<Material>> _materialGroups = new();
        private MatsukawaOptions _options = new();
        private MatsukawaAnalysis? _analysis;

        private TreeViewState? _hierarchyTreeState;
        private PartAtlasPrefabHierarchyView? _hierarchyView;
        private PartAtlasPrefabPreview? _preview;

        private string _outputName = "";
        private string _lastSuggestedOutputName = "";
        private bool _outputNameCustomized;
        private Vector2 _mainScroll;
        private Vector2 _rendererScroll;
        private bool _showOptions = true;
        private bool _showProtected = true;

        private float _leftPaneWidth = 320f;
        private float _hierarchyPaneFraction = 0.58f;
        private float _rightPaneContentWidth = RightPaneMinWidth;
        private GameObject? _rendererLinkHighlightObject;
        private double _rendererLinkHighlightUntil;

        private const float SplitterWidth = 5f;
        private const float MinHierarchyHeight = 190f;
        private const float MinPreviewHeight = 180f;
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
            _preview ??= new PartAtlasPrefabPreview(Repaint);
            InitializeAdapter();

            if (_loadedPrefabRoot != null)
            {
                _hierarchyView?.SetRoot(_loadedPrefabRoot);
                Refresh3DPreview();
            }
        }

        private void OnDisable()
        {
            UnloadSourcePrefab();
            _preview?.Dispose();
            _preview = null;
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
            _hierarchyView.ExtractionTargetsChanged += OnExtractionTargetsChanged;
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

            DrawHierarchyAndPreviewPane(leftRect);
            HandleSplitter(splitterRect);
            DrawRightPane(rightRect);

            if ((_hierarchyView?.HasActiveFlash ?? false)
                || EditorApplication.timeSinceStartup < _rendererLinkHighlightUntil)
            {
                Repaint();
            }
        }

        private void DrawHierarchyAndPreviewPane(Rect rect)
        {
            if (_hierarchyView == null) return;

            var available = Mathf.Max(0f, rect.height - SplitterWidth);
            var minHierarchy = Mathf.Min(MinHierarchyHeight, available * 0.5f);
            var minPreview = Mathf.Min(MinPreviewHeight, available - minHierarchy);
            var hierarchyHeight = Mathf.Clamp(
                available * _hierarchyPaneFraction,
                minHierarchy,
                available - minPreview
            );

            var hierarchyRect = new Rect(rect.x, rect.y, rect.width, hierarchyHeight);
            var splitterRect = new Rect(rect.x, hierarchyRect.yMax, rect.width, SplitterWidth);
            var previewRect = new Rect(rect.x, splitterRect.yMax, rect.width,
                Mathf.Max(0f, rect.yMax - splitterRect.yMax));

            _hierarchyView.Draw(hierarchyRect);
            HandleHierarchyPreviewSplitter(rect, splitterRect, available, minHierarchy, minPreview);
            _preview?.Draw(previewRect);
        }

        private void HandleHierarchyPreviewSplitter(
            Rect paneRect, Rect splitterRect, float availableHeight,
            float minHierarchy, float minPreview)
        {
            EditorGUIUtility.AddCursorRect(splitterRect, MouseCursor.ResizeVertical);

            if (Event.current.type == EventType.Repaint)
            {
                EditorGUI.DrawRect(splitterRect, EditorGUIUtility.isProSkin
                    ? new Color(0.16f, 0.16f, 0.16f, 1f)
                    : new Color(0.72f, 0.72f, 0.72f, 1f));
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
                if (availableHeight > 0f)
                {
                    _hierarchyPaneFraction = Mathf.Clamp(
                        (Event.current.mousePosition.y - paneRect.y) / availableHeight,
                        minHierarchy / availableHeight,
                        1f - minPreview / availableHeight
                    );
                }
                Repaint();
                Event.current.Use();
            }
            else if (eventType == EventType.MouseUp && GUIUtility.hotControl == controlId)
            {
                GUIUtility.hotControl = 0;
                Event.current.Use();
            }
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

            if (DrawInputSection(_rightPaneContentWidth) is false)
                return;

            if (DrawExtractionSection(_rightPaneContentWidth) is false)
                return;

            DrawAtlasSection(_rightPaneContentWidth);
            DrawExecutionSection(_rightPaneContentWidth);
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
            EditorGUILayout.Space(4f);
        }

        private GUIStyle? _majorSectionHeaderStyle;
        private GUIStyle? _mediumSectionLabelStyle;
        private GUIStyle? _mediumSectionFoldoutStyle;

        private GUIStyle MajorSectionHeaderStyle
        {
            get
            {
                if (_majorSectionHeaderStyle != null)
                    return _majorSectionHeaderStyle;

                _majorSectionHeaderStyle = new GUIStyle(EditorStyles.boldLabel)
                {
                    fontSize = 13,
                    fontStyle = FontStyle.Bold,
                    alignment = TextAnchor.MiddleLeft,
                };
                return _majorSectionHeaderStyle;
            }
        }

        private GUIStyle MediumSectionLabelStyle
        {
            get
            {
                if (_mediumSectionLabelStyle != null)
                    return _mediumSectionLabelStyle;

                // Derive from the normal Inspector label so the visible text starts
                // at the same x-position as labels on ObjectField/TextField controls.
                _mediumSectionLabelStyle = new GUIStyle(EditorStyles.label)
                {
                    fontStyle = FontStyle.Bold,
                };
                return _mediumSectionLabelStyle;
            }
        }

        private GUIStyle MediumSectionFoldoutStyle
        {
            get
            {
                if (_mediumSectionFoldoutStyle != null)
                    return _mediumSectionFoldoutStyle;

                _mediumSectionFoldoutStyle = new GUIStyle(EditorStyles.foldout)
                {
                    fontStyle = FontStyle.Bold,
                };
                return _mediumSectionFoldoutStyle;
            }
        }

        private void DrawMajorSectionHeader(string title)
        {
            EditorGUILayout.Space(8f);
            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField(
                    title,
                    MajorSectionHeaderStyle,
                    GUILayout.Height(EditorGUIUtility.singleLineHeight + 2f)
                );
            }
            EditorGUILayout.Space(2f);
        }

        private static float InspectorIndentWidth()
        {
            var previousIndent = EditorGUI.indentLevel;
            try
            {
                var probe = new Rect(
                    0f,
                    0f,
                    100f,
                    EditorGUIUtility.singleLineHeight
                );

                EditorGUI.indentLevel = 0;
                var baseX = EditorGUI.IndentedRect(probe).x;

                EditorGUI.indentLevel = 1;
                var indentedX = EditorGUI.IndentedRect(probe).x;

                return Mathf.Max(0f, indentedX - baseX);
            }
            finally
            {
                EditorGUI.indentLevel = previousIndent;
            }
        }

        private sealed class InspectorIndentScope : IDisposable
        {
            private readonly EditorGUILayout.HorizontalScope _horizontal;
            private readonly EditorGUILayout.VerticalScope _vertical;

            internal float ContentWidth { get; }

            internal InspectorIndentScope(float parentContentWidth)
            {
                var indentWidth = InspectorIndentWidth();

                _horizontal = new EditorGUILayout.HorizontalScope();
                GUILayout.Space(indentWidth);
                _vertical = new EditorGUILayout.VerticalScope();

                ContentWidth = Mathf.Max(1f, parentContentWidth - indentWidth);
            }

            public void Dispose()
            {
                _vertical.Dispose();
                _horizontal.Dispose();
            }
        }

        private bool DrawMediumSectionFoldout(bool expanded, string title)
        {
            var rect = EditorGUILayout.GetControlRect(
                false,
                EditorGUIUtility.singleLineHeight
            );

            // Put the disclosure triangle in the parent-indent gutter so the
            // foldout text itself aligns with the other medium-section labels.
            var indentWidth = InspectorIndentWidth();
            rect.x -= indentWidth;
            rect.width += indentWidth;

            return EditorGUI.Foldout(
                rect,
                expanded,
                title,
                true,
                MediumSectionFoldoutStyle
            );
        }

        private bool DrawInputSection(float contentWidth)
        {
            DrawMajorSectionHeader("入力");

            using var content = new InspectorIndentScope(contentWidth);

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
                return false;
            }

            if (_validTargetPaths.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "このPrefab内に抽出可能なMeshが見つかりません。",
                    MessageType.Warning
                );
                return false;
            }

            return true;
        }

        private bool DrawExtractionSection(float contentWidth)
        {
            DrawMajorSectionHeader("抽出");

            using var content = new InspectorIndentScope(contentWidth);

            if (_extractionTargetPaths.Count == 0)
            {
                EditorGUILayout.LabelField(
                    "抽出対象（0件）",
                    MediumSectionLabelStyle
                );

                using (new InspectorIndentScope(content.ContentWidth))
                {
                    EditorGUILayout.HelpBox(
                        "左のPrefab Hierarchy右端のチェックで、抽出したいGameObjectを1件以上選択してください。複数選択できます。",
                        MessageType.Info
                    );
                }

                EditorGUILayout.Space(4f);
                return false;
            }

            DrawExtractionTargetSection(content.ContentWidth);
            DrawRendererSection(content.ContentWidth);
            DrawExtractionOptionsSection(content.ContentWidth);
            return true;
        }

        private void DrawAtlasSection(float contentWidth)
        {
            DrawMajorSectionHeader("アトラス化");

            using var content = new InspectorIndentScope(contentWidth);
            DrawAtlasMaterialSection(content.ContentWidth);
            DrawAtlasSettings(content.ContentWidth);
        }

        private void DrawExecutionSection(float contentWidth)
        {
            DrawMajorSectionHeader("実行");

            using var content = new InspectorIndentScope(contentWidth);
            DrawOutputName();
            DrawAnalysis();
            DrawExecute();
        }

        private void DrawOutputName()
        {
            var nextOutputName = EditorGUILayout.TextField(
                new GUIContent(
                    "名前",
                    "生成物を識別する名前です。保存時に _extracted_<生成日時> が自動的に付与されます。"
                ),
                _outputName
            );
            if (!string.Equals(nextOutputName, _outputName, StringComparison.Ordinal))
            {
                _outputName = nextOutputName;
                _outputNameCustomized =
                    string.Equals(
                        _outputName,
                        _lastSuggestedOutputName,
                        StringComparison.Ordinal
                    ) is false;
            }

            var sanitized = _matsukawa!.SanitizeName(_outputName.Trim());

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

        private void DrawExtractionTargetSection(float contentWidth)
        {
            var paths = _extractionTargetPaths
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
            var names = paths
                .Select(path =>
                {
                    if (string.IsNullOrEmpty(path)) return "<Prefab Root>";
                    var separator = path.LastIndexOf('/');
                    return separator >= 0 && separator + 1 < path.Length
                        ? path.Substring(separator + 1)
                        : path;
                })
                .ToArray();

            const int visibleNameCount = 3;
            var visibleNames = names.Take(visibleNameCount).ToArray();
            var summary = string.Join(" / ", visibleNames);
            if (names.Length > visibleNameCount)
                summary += " / ほか " + (names.Length - visibleNameCount) + " 件";

            var tooltip = string.Join(
                "\n",
                paths.Select(path =>
                    string.IsNullOrEmpty(path) ? "<Prefab Root>" : path)
            );

            EditorGUILayout.LabelField(
                $"抽出対象（{paths.Length}件）",
                MediumSectionLabelStyle
            );
            using (new InspectorIndentScope(contentWidth))
            {
                EditorGUILayout.LabelField(
                    new GUIContent(summary, tooltip),
                    EditorStyles.miniLabel
                );
            }
        }

        private void DrawRendererSection(float contentWidth)
        {
            if (_extractionTargetPaths.Count == 0) return;

            var keptCount = _entries.Count(entry => entry.Keep);
            EditorGUILayout.LabelField(
                $"抽出するRenderer（{keptCount}/{_entries.Count}）",
                MediumSectionLabelStyle
            );

            using (new InspectorIndentScope(contentWidth))
            {
                const float rendererRowHeight = 18f;
                const float rendererListMaxHeight = 260f;
                var rendererListNaturalHeight =
                    Mathf.Max(1, _entries.Count) * rendererRowHeight
                    + GUI.skin.box.padding.vertical
                    + GUI.skin.box.margin.vertical;
                var rendererListNeedsScroll =
                    rendererListNaturalHeight > rendererListMaxHeight;

                if (rendererListNeedsScroll)
                {
                    using var view = new EditorGUILayout.ScrollViewScope(
                        _rendererScroll,
                        GUILayout.Height(rendererListMaxHeight)
                    );
                    _rendererScroll = view.scrollPosition;
                    DrawRendererListBox();
                }
                else
                {
                    _rendererScroll = Vector2.zero;
                    DrawRendererListBox();
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
            }

            EditorGUILayout.Space(4f);
        }

        private void DrawRendererListBox()
        {
            using (new EditorGUILayout.VerticalScope(GUI.skin.box))
            {
                if (_entries.Count == 0)
                {
                    EditorGUILayout.LabelField(
                        "メッシュが見つかりません。",
                        EditorStyles.miniLabel
                    );
                    return;
                }

                foreach (var entry in _entries)
                    DrawRendererRow(entry);
            }
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
                Refresh3DPreview();
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

        private void DrawAtlasMaterialSection(float contentWidth)
        {
            if (_extractionTargetPaths.Count == 0
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
                MediumSectionLabelStyle
            );

            using (var content = new InspectorIndentScope(contentWidth))
            {
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
                        content.ContentWidth,
                        256f
                    );
                }
                else
                {
                    EditorGUILayout.HelpBox(
                        "現在の抽出対象RendererにはMaterialがありません。",
                        MessageType.Info
                    );
                }
            }

            if (_atlasSettingsObject.ApplyModifiedProperties())
            {
                SanitizeIslandSizePriorityMaterials();
                EditorUtility.SetDirty(_atlasSettings);
                _atlasSettingsObject.UpdateIfRequiredOrScript();
            }

            EditorGUILayout.Space(4f);
        }

        private void DrawAtlasSettings(float contentWidth)
        {
            if (_atlasSettings == null || _atlasSettingsObject == null)
                return;

            var previousLabelWidth = BeginWideLabels(contentWidth);
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

                EditorGUILayout.LabelField(
                    "AtlasTexture:label:IslandSizePriority".Glc(),
                    MediumSectionLabelStyle
                );
                using (var content = new InspectorIndentScope(contentWidth))
                {
                    islandSizePriority.isExpanded = EditorGUILayout.Foldout(
                        islandSizePriority.isExpanded,
                        "AtlasTexture:prop:IslandSizePriorityTuner".Glc(),
                        true
                    );

                    if (islandSizePriority.isExpanded)
                    {
                        var targetMaterials = _atlasSettings.AtlasTargetMaterials
                            .Where(material => material != null)
                            .Cast<Material>()
                            .ToArray();

                        AtlasSettingsEditorGUI.DrawStandaloneMaterialSizePriorityTuners(
                            islandSizePriority,
                            targetMaterials,
                            content.ContentWidth
                        );
                    }
                }

                EditorGUILayout.LabelField(
                    "AtlasTexture:label:MaterialSettings".Glc(),
                    MediumSectionLabelStyle
                );
                using (new InspectorIndentScope(contentWidth))
                {
                    var targetMaterials = _atlasSettings.AtlasTargetMaterials
                        .Where(material => material != null)
                        .Cast<Material>()
                        .ToHashSet();

                    AtlasSettingsEditorGUI.DrawMaterialSettingFields(
                        mergeMaterialGroups,
                        allMaterialMergeReference,
                        targetMaterials,
                        useAtlasTextureMergeGroupEditor: false
                    );
                }

                EditorGUILayout.LabelField(
                    "AtlasTexture:label:AtlasSettings".Glc(),
                    MediumSectionLabelStyle
                );
                using (new InspectorIndentScope(contentWidth))
                {
                    var changes = AtlasSettingsEditorGUI.DrawAtlasSettingFields(
                        atlasSetting,
                        includeDisabledRenderer: false
                    );

                    if ((changes & AtlasSettingDrawChange.TargetUVChannel) != 0)
                        RefreshMaterialGroups();
                }

                if (_atlasSettingsObject.ApplyModifiedProperties())
                {
                    SanitizeIslandSizePriorityMaterials();
                    EditorUtility.SetDirty(_atlasSettings);
                    _atlasSettingsObject.UpdateIfRequiredOrScript();
                }
            }
            finally
            {
                EditorGUIUtility.labelWidth = previousLabelWidth;
            }

            EditorGUILayout.Space(4f);
        }

        private void DrawExtractionOptionsSection(float contentWidth)
        {
            if (_extractionTargetPaths.Count == 0) return;

            _showOptions = DrawMediumSectionFoldout(
                _showOptions,
                "抽出設定"
            );
            if (!_showOptions) return;

            using (new InspectorIndentScope(contentWidth))
            {
                var previousLabelWidth = BeginWideLabels(contentWidth);
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

                    if (EditorGUI.EndChangeCheck())
                        InvalidateAnalysis();
                }
                finally
                {
                    EditorGUIUtility.labelWidth = previousLabelWidth;
                }
            }

            EditorGUILayout.Space(4f);
        }

        private void DrawAnalysis()
        {
            if (_extractionTargetPaths.Count == 0) return;

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("解析（プレビュー）", GUILayout.Height(26f)))
                {
                    EditorApplication.delayCall += Analyze;
                    GUIUtility.ExitGUI();
                }

                using (new EditorGUI.DisabledScope(_analysis == null))
                {
                    if (GUILayout.Button("解析をクリア", GUILayout.Height(26f), GUILayout.Width(100f)))
                        InvalidateAnalysis();
                }
            }

            if (_analysis == null) return;

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                var total = Math.Max(
                    0,
                    _analysis.TotalTransforms - _analysisScaffoldCount
                );
                var after = Math.Max(
                    0,
                    _analysis.TotalTransforms
                    - _analysis.DeleteTransformCount
                    - _analysisScaffoldCount
                );
                EditorGUILayout.LabelField("解析結果", EditorStyles.boldLabel);
                EditorGUILayout.LabelField(
                    $"オブジェクト {total} → {after} / 削除Renderer {_analysis.DeleteRendererCount} / Trim {_analysis.TrimTargetCount}",
                    EditorStyles.wordWrappedLabel
                );

                foreach (var warning in _analysis.Warnings)
                    EditorGUILayout.HelpBox(warning, MessageType.Warning);
            }

            DrawProtectedObjects();
        }

        private void Analyze()
        {
            if (_matsukawa == null
                || _loadedPrefabRoot == null
                || _extractionTargetPaths.Count == 0)
            {
                return;
            }

            var targets = ResolveCurrentExtractionTargets();
            if (targets.Count == 0)
                return;

            using var staging = PartAtlasExtractionStaging.Create(
                _loadedPrefabRoot,
                targets
            );

            var analysisOptions = PartAtlasPrefabPipeline.CreateStagedOptions(
                _options,
                staging,
                IsAvatarSource()
            );

            // Preview must use the same complete HCE renderer set as execution.
            // Only the user's selected-target renderers are marked Keep=true; renderers
            // elsewhere in the source Prefab remain explicit HCE deletion candidates.
            var keptRenderers = _entries
                .Where(entry => entry.Keep && entry.Renderer != null)
                .Select(entry => entry.Renderer)
                .ToHashSet();
            var analysisEntries = _matsukawa
                .CollectRenderers(_loadedPrefabRoot)
                .ToList();
            foreach (var entry in analysisEntries)
                entry.Keep = keptRenderers.Contains(entry.Renderer);

            _analysis = _matsukawa.Analyze(
                _loadedPrefabRoot,
                analysisEntries,
                analysisOptions
            );
            _analysisScaffoldCount = staging.ScaffoldCount;
            _hierarchyView?.SetAnalysis(_analysis);
            Repaint();
        }

        private void InvalidateAnalysis()
        {
            _analysis = null;
            _analysisScaffoldCount = 0;
            _hierarchyView?.SetAnalysis(null);
            Repaint();
        }

        private void DrawProtectedObjects()
        {
            if (_analysis == null || _loadedPrefabRoot == null) return;

            _showProtected = EditorGUILayout.Foldout(
                _showProtected,
                $"保護して残すオブジェクト（{_analysis.ProtectedObjects.Count}）",
                true
            );
            if (!_showProtected) return;

            foreach (var gameObject in _analysis.ProtectedObjects
                         .Where(gameObject => gameObject != null)
                         .OrderBy(gameObject => RelativePath(_loadedPrefabRoot.transform, gameObject.transform)))
            {
                var path = RelativePath(_loadedPrefabRoot.transform, gameObject.transform);
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
            if (_analysis == null
                || _sourcePrefab == null
                || _loadedPrefabRoot == null
                || _extractionTargetPaths.Count == 0)
            {
                return;
            }

            EditorGUILayout.Space(8f);
            var atlasRequested = _atlasSettings != null
                && _atlasSettings.AtlasTargetMaterials.Any(material => material != null);
            var automaticTargets = GetAutomaticAtlasRenderers();

            using (new EditorGUI.DisabledScope(
                       _entries.All(entry => entry.Keep is false)
                       || (atlasRequested && automaticTargets.Length == 0)))
            {
                var executeLabel = atlasRequested
                    ? "抽出 → Atlas → Prefab保存"
                    : "抽出 → Prefab保存";
                if (GUILayout.Button(executeLabel, GUILayout.Height(32f)))
                    Execute();
            }

            if (atlasRequested && automaticTargets.Length == 0)
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
                || _loadedPrefabRoot == null
                || _extractionTargetPaths.Count == 0)
            {
                return;
            }

            var sanitizedOutputName = _matsukawa.SanitizeName(_outputName.Trim());
            if (string.IsNullOrWhiteSpace(sanitizedOutputName))
            {
                EditorUtility.DisplayDialog("TTT Prefab化", "名前を入力してください。", "OK");
                return;
            }

            var unknownUnchecked = _entries.Count(entry =>
                entry.Keep is false
                && entry.Category == "Unknown"
            );
            var atlasRequested = _atlasSettings.AtlasTargetMaterials
                .Any(material => material != null);
            var atlasCount = GetAutomaticAtlasRenderers().Length;

            var confirm =
                "抽出対象 "
                + _extractionTargetPaths.Count
                + " 件をPrefab化します。";
            if (atlasRequested)
            {
                confirm +=
                    "\nアトラス化するRenderer: "
                    + atlasCount
                    + " 件";
            }
            confirm += "\n\n名前: " + sanitizedOutputName;

            if (unknownUnchecked > 0)
            {
                confirm +=
                    "\n\n⚠ 用途が「不明」でチェックの入っていないRendererが "
                    + unknownUnchecked
                    + " 件あります。残すべきものが混じっていないか確認してください。";
            }

            if (EditorUtility.DisplayDialog(
                    "TTT Prefab化",
                    confirm + "\n\n実行しますか？",
                    "実行",
                    "キャンセル"
                ) is false)
            {
                return;
            }

            var request = new PartAtlasPrefabRequest
            {
                SourcePrefabAsset = _sourcePrefab,
                ExtractionOptions = _options,
                AtlasSettings = _atlasSettings,
                OutputName = sanitizedOutputName,
            };

            foreach (var path in _extractionTargetPaths
                         .OrderBy(path => path, StringComparer.Ordinal))
            {
                request.ExtractionTargetPaths.Add(path);
            }

            var keptKeys = _entries
                .Where(entry => entry.Keep)
                .Select(entry => PartAtlasPrefabPipeline.GetRendererKey(
                    _loadedPrefabRoot,
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

            PartAtlasPrefabPipeline.UnpackPrefabInstancesForStaging(_loadedPrefabRoot);

            _hierarchyView?.SetRoot(_loadedPrefabRoot);
            BuildValidTargetPaths();

            _extractionTargetPaths.Clear();
            _entries.Clear();
            _outputName = "";
            _lastSuggestedOutputName = "";
            _outputNameCustomized = false;
            RefreshMaterialCandidates(preserveSelection: false);
            InvalidateAnalysis();
        }

        private void BuildValidTargetPaths()
        {
            _validTargetPaths.Clear();
            if (_loadedPrefabRoot == null) return;

            var root = _loadedPrefabRoot.transform;
            var validTargets = new HashSet<Transform>();

            foreach (var renderer in _loadedPrefabRoot.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer is not SkinnedMeshRenderer && renderer is not MeshRenderer)
                    continue;
                if (RendererMesh(renderer) == null)
                    continue;

                var current = renderer.transform;
                while (current != null && current != root)
                {
                    validTargets.Add(current);
                    current = current.parent;
                }
            }

            foreach (var transform in _loadedPrefabRoot.GetComponentsInChildren<Transform>(true))
            {
                if (validTargets.Contains(transform))
                    _validTargetPaths.Add(RelativePath(root, transform));
            }

            _hierarchyView?.SetTargetCandidates(validTargets);
        }

        private void OnExtractionTargetsChanged(IReadOnlyList<Transform> targets)
        {
            if (_loadedPrefabRoot == null)
                return;

            _extractionTargetPaths.Clear();
            foreach (var transform in targets)
            {
                var path = RelativePath(_loadedPrefabRoot.transform, transform);
                if (_validTargetPaths.Contains(path))
                    _extractionTargetPaths.Add(path);
            }

            var normalized = PartAtlasPrefabPipeline.NormalizeExtractionTargetPaths(
                _extractionTargetPaths
            );
            _extractionTargetPaths.Clear();
            foreach (var path in normalized)
                _extractionTargetPaths.Add(path);

            _options.ForceDeletePaths.Clear();
            RefreshExtractionTargets();
        }

        private IReadOnlyList<Transform> ResolveCurrentExtractionTargets()
        {
            if (_loadedPrefabRoot == null)
                return Array.Empty<Transform>();

            var result = new List<Transform>();
            foreach (var path in _extractionTargetPaths
                         .OrderBy(path => path, StringComparer.Ordinal))
            {
                if (string.IsNullOrEmpty(path))
                    continue;

                var transform = _loadedPrefabRoot.transform.Find(path);
                if (transform != null)
                    result.Add(transform);
            }

            return result;
        }

        private void RefreshExtractionTargets()
        {
            InvalidateAnalysis();

            if (_loadedPrefabRoot == null
                || _matsukawa == null
                || _extractionTargetPaths.Count == 0)
            {
                _entries.Clear();
                RefreshMaterialCandidates(preserveSelection: false);
                Refresh3DPreview();
                return;
            }

            var previousKeep = _entries
                .Where(entry => entry.Renderer != null)
                .ToDictionary(entry => entry.Renderer, entry => entry.Keep);

            var targets = ResolveCurrentExtractionTargets();
            if (targets.Count == 0)
            {
                _entries.Clear();
                RefreshMaterialCandidates(preserveSelection: false);
                Refresh3DPreview();
                return;
            }

            _entries = _matsukawa
                .CollectRenderers(_loadedPrefabRoot)
                .Where(entry =>
                    entry.Renderer != null
                    && targets.Any(target =>
                        entry.Renderer.transform == target
                        || entry.Renderer.transform.IsChildOf(target)))
                .ToList();

            foreach (var entry in _entries)
            {
                entry.Keep = previousKeep.TryGetValue(entry.Renderer, out var keep)
                    ? keep
                    : true;
            }

            var suggestedOutputName = targets.Count == 1
                ? targets[0].name
                : "";

            if (_outputNameCustomized is false
                || string.IsNullOrWhiteSpace(_outputName)
                || string.Equals(
                    _outputName,
                    _lastSuggestedOutputName,
                    StringComparison.Ordinal
                ))
            {
                _outputName = suggestedOutputName;
                _outputNameCustomized = false;
            }

            _lastSuggestedOutputName = suggestedOutputName;

            RefreshMaterialCandidates(preserveSelection: true);
            Refresh3DPreview();
        }

        private void Refresh3DPreview()
        {
            _preview?.SetRenderers(_entries
                .Where(entry => entry.Keep && entry.Renderer != null)
                .Select(entry => entry.Renderer));
            Repaint();
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

            if (_extractionTargetPaths.Count > 0)
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
            if (_loadedPrefabRoot == null
                || _extractionTargetPaths.Count == 0
                || _atlasSettings == null)
            {
                return Array.Empty<Renderer>();
            }

            var keptRenderers = _entries
                .Where(entry => entry.Keep && entry.Renderer != null)
                .Select(entry => entry.Renderer)
                .ToHashSet();

            if (keptRenderers.Count == 0)
                return Array.Empty<Renderer>();

            return PartAtlasPrefabPipeline
                .GetAtlasCandidateRenderers(_atlasSettings, _loadedPrefabRoot)
                .Where(keptRenderers.Contains)
                .ToArray();
        }

        private void UnloadSourcePrefab()
        {
            // Release baked meshes before PrefabUtility destroys their source hierarchy.
            _preview?.Clear();
            _preview?.ResetView();
            InvalidateAnalysis();
            _entries.Clear();
            _materialCandidates.Clear();
            _materialGroups.Clear();
            _outputName = "";
            _lastSuggestedOutputName = "";
            _outputNameCustomized = false;
            _extractionTargetPaths.Clear();
            _validTargetPaths.Clear();
            _analysisScaffoldCount = 0;
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

            if (transform == null || _loadedPrefabRoot == null)
                return;

            var index = _entries.FindIndex(entry =>
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

        private static float BeginWideLabels(float contentWidth)
        {
            var previous = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = Mathf.Clamp(
                contentWidth - 60f,
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

        private bool IsAvatarSource()
        {
            if (_loadedPrefabRoot == null) return false;

            return _loadedPrefabRoot
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
