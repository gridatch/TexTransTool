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
        private PartAtlasPrefabSettings? _atlasSettings;
        private SerializedObject? _atlasSettingsObject;
        private MatsukawaAdapter? _matsukawa;
        private string _adapterError = "";

        private GameObject? _sourcePrefab;
        private GameObject? _loadedPrefabRoot;
        private GameObject? _extractionRoot;
        private string[] _rootPaths = Array.Empty<string>();
        private string[] _rootLabels = Array.Empty<string>();
        private int _rootIndex;

        private List<MatsukawaRendererEntry> _entries = new();
        private readonly List<Material> _materialCandidates = new();
        private MatsukawaOptions _options = new();
        private MatsukawaAnalysis? _analysis;

        private string _filter = "";
        private string _outputName = "";
        private Vector2 _mainScroll;
        private Vector2 _rendererScroll;
        private Vector2 _materialScroll;
        private Vector2 _hierarchyScroll;
        private bool _showOptions = true;
        private bool _showAtlasSettings = true;
        private bool _showAdvancedAtlasSettings;
        private bool _showHierarchy = true;
        private bool _showProtected = true;

        [MenuItem("Tools/TexTransTool/WDT/Prefab抽出・アトラス化...")]
        private static void OpenFromMenu()
        {
            Open();
        }

        internal static void Open()
        {
            var window = GetWindow<PartAtlasPrefabWindow>();
            window.titleContent = new GUIContent("TTT Prefab抽出");
            window.minSize = new Vector2(560f, 680f);
            window.EnsureAtlasSettings();
            window.InitializeAdapter();
            window.Show();
        }

        private void OnEnable()
        {
            EnsureAtlasSettings();
            InitializeAdapter();
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
            _atlasSettings.hideFlags = HideFlags.HideAndDontSave;
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
            if (_atlasSettings == null || _atlasSettingsObject == null)
            {
                EditorGUILayout.HelpBox(
                    "アトラス設定用の一時設定オブジェクトを初期化できませんでした。",
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
            DrawAtlasMaterialSelection();
            DrawAtlasSettings();
            DrawOptions();
            DrawAnalysis();
            DrawExecute();
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
            }

            if (_loadedPrefabRoot == null)
            {
                EditorGUILayout.HelpBox("Project上のPrefab Assetを指定してください。", MessageType.None);
                return;
            }

            if (_rootPaths.Length == 0)
            {
                EditorGUILayout.HelpBox(
                    "このPrefab内に抽出可能なMeshが見つかりません。",
                    MessageType.Warning
                );
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
            if (Event.current.type == EventType.Repaint && entry.Keep)
                EditorGUI.DrawRect(rect, new Color(0.25f, 0.75f, 0.40f, 0.12f));

            var x = rect.x + 2f;
            var toggleRect = new Rect(x, rect.y, 18f, rect.height);
            EditorGUI.BeginChangeCheck();
            entry.Keep = EditorGUI.Toggle(toggleRect, entry.Keep);
            if (EditorGUI.EndChangeCheck())
            {
                RefreshMaterialCandidates(preserveSelection: true);
                _analysis = null;
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
                EditorGUIUtility.PingObject(entry.Renderer.gameObject);
                Selection.activeGameObject = entry.Renderer.gameObject;
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
            if (_extractionRoot == null || _atlasSettings == null) return;

            EditorGUILayout.LabelField(
                $"アトラス化するMaterial（{_atlasSettings.AtlasTargetMaterials.Count(material => material != null)}/{_materialCandidates.Count}）",
                EditorStyles.boldLabel
            );
            EditorGUILayout.LabelField(
                "抽出対象Rendererで実際に使用されているMaterialから選択します。",
                EditorStyles.wordWrappedMiniLabel
            );

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("全選択", EditorStyles.miniButton))
                {
                    SetSelectedMaterials(_materialCandidates);
                }

                if (GUILayout.Button("全解除", EditorStyles.miniButton))
                {
                    SetSelectedMaterials(Array.Empty<Material>());
                }

                if (GUILayout.Button("反転", EditorStyles.miniButton))
                {
                    var selected = _atlasSettings.AtlasTargetMaterials
                        .Where(material => material != null)
                        .Cast<Material>()
                        .ToHashSet();
                    SetSelectedMaterials(
                        _materialCandidates.Where(material => selected.Contains(material) is false)
                    );
                }
            }

            if (_materialCandidates.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "現在の抽出対象RendererにはMaterialがありません。",
                    MessageType.Info
                );
                EditorGUILayout.Space(4f);
                return;
            }

            var selectedMaterials = _atlasSettings.AtlasTargetMaterials
                .Where(material => material != null)
                .Cast<Material>()
                .ToHashSet();

            using var view = new EditorGUILayout.ScrollViewScope(
                _materialScroll,
                GUILayout.MinHeight(100f),
                GUILayout.MaxHeight(260f)
            );
            _materialScroll = view.scrollPosition;

            foreach (var material in _materialCandidates)
            {
                if (material == null) continue;

                var selected = selectedMaterials.Contains(material);
                var assetPath = AssetDatabase.GetAssetPath(material);
                var nextSelected = EditorGUILayout.ToggleLeft(
                    new GUIContent(
                        material.name,
                        string.IsNullOrEmpty(assetPath) ? material.name : assetPath
                    ),
                    selected
                );

                if (nextSelected == selected) continue;

                if (nextSelected) selectedMaterials.Add(material);
                else selectedMaterials.Remove(material);

                SetSelectedMaterials(selectedMaterials);
                GUIUtility.ExitGUI();
            }

            var automaticTargets = GetAutomaticAtlasRenderers();
            if (selectedMaterials.Count == 0)
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
            else
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

            _showAtlasSettings = EditorGUILayout.Foldout(
                _showAtlasSettings,
                "Atlas設定",
                true
            );
            if (!_showAtlasSettings) return;

            _atlasSettingsObject.Update();

            var atlasSetting = _atlasSettingsObject.FindProperty(nameof(PartAtlasPrefabSettings.AtlasSetting));
            var materialPriorities = _atlasSettingsObject.FindProperty(nameof(PartAtlasPrefabSettings.MaterialSizePriorities));
            var mergeMaterialGroups = _atlasSettingsObject.FindProperty(nameof(PartAtlasPrefabSettings.MergeMaterialGroups));
            var allMaterialMergeReference = _atlasSettingsObject.FindProperty(nameof(PartAtlasPrefabSettings.AllMaterialMergeReference));

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

            using var box = new EditorGUILayout.VerticalScope(EditorStyles.helpBox);
            EditorGUILayout.LabelField(
                "この抽出処理だけに使用するAtlas設定です。",
                EditorStyles.wordWrappedMiniLabel
            );

            EditorGUI.BeginChangeCheck();

            EditorGUILayout.LabelField("基本設定", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(autoSize, "AtlasTexture:prop:AutoAtlasTextureSize".GlcV());
            using (new EditorGUI.DisabledScope(autoSize.boolValue))
            {
                EditorGUILayout.PropertyField(textureSize, "AtlasTexture:prop:AtlasTextureSize".GlcV());
                if (customAspect.boolValue)
                    EditorGUILayout.PropertyField(heightSize, "AtlasTexture:prop:AtlasTextureHeightSize".GlcV());
                EditorGUILayout.PropertyField(customAspect, "AtlasTexture:prop:CustomAspect".GlcV());
            }
            EditorGUILayout.PropertyField(padding, "AtlasTexture:prop:Padding".GlcV());
            EditorGUILayout.PropertyField(
                pixelNormalize,
                "AtlasTexture:prop:PixelNormalize".GlcV()
            );

            EditorGUILayout.Space(5f);
            DrawMaterialSizePriorities(materialPriorities);

            var hasCustomPriority = HasCustomMaterialPriority(materialPriorities);
            if (hasCustomPriority is false)
                forceSizePriority.boolValue = false;

            EditorGUILayout.Space(5f);
            EditorGUILayout.LabelField("Material統合", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(
                mergeMaterialGroups,
                "AtlasTexture:prop:MergeMaterialGroups".GlcV(),
                true
            );
            EditorGUILayout.PropertyField(
                allMaterialMergeReference,
                "AtlasTexture:prop:AllMaterialMergeReference".GlcV()
            );

            EditorGUILayout.Space(5f);
            EditorGUILayout.LabelField("Texture Fine Tuning", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(
                textureFineTuning,
                "AtlasTexture:prop:TextureFineTuning".GlcV(),
                true
            );

            EditorGUILayout.Space(5f);
            _showAdvancedAtlasSettings = EditorGUILayout.Foldout(
                _showAdvancedAtlasSettings,
                "詳細設定",
                true
            );
            if (_showAdvancedAtlasSettings)
            {
                using var indent = new EditorGUI.IndentLevelScope(1);

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

                if (hasCustomPriority)
                {
                    EditorGUILayout.PropertyField(
                        forceSizePriority,
                        "AtlasTexture:prop:ForceSizePriority".GlcV()
                    );
                }

                EditorGUILayout.PropertyField(
                    forceSetTexture,
                    "AtlasTexture:prop:ForceSetTexture".GlcV()
                );
                EditorGUILayout.PropertyField(
                    backgroundColor,
                    "AtlasTexture:prop:BackGroundColor".GlcV()
                );
            }

            if (EditorGUI.EndChangeCheck())
            {
                _atlasSettingsObject.ApplyModifiedProperties();
                Repaint();
            }
            else
            {
                _atlasSettingsObject.ApplyModifiedProperties();
            }

            EditorGUILayout.Space(4f);
        }

        private static void DrawMaterialSizePriorities(SerializedProperty priorities)
        {
            EditorGUILayout.LabelField("解像度配分", EditorStyles.boldLabel);

            if (priorities.arraySize == 0)
            {
                EditorGUILayout.LabelField(
                    "Atlas対象Materialを選択すると、Materialごとの島サイズ優先度を設定できます。",
                    EditorStyles.wordWrappedMiniLabel
                );
                return;
            }

            EditorGUILayout.LabelField(
                "1.00を基準として、MaterialごとにAtlas内の島サイズ優先度を調整します。",
                EditorStyles.wordWrappedMiniLabel
            );

            for (var i = 0; i < priorities.arraySize; i++)
            {
                var element = priorities.GetArrayElementAtIndex(i);
                var material = element.FindPropertyRelative(nameof(PartAtlasMaterialPriority.Material));
                var priority = element.FindPropertyRelative(nameof(PartAtlasMaterialPriority.Priority));
                var materialObject = material.objectReferenceValue as Material;
                var label = materialObject != null ? materialObject.name : "(Missing Material)";

                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(
                        new GUIContent(
                            label,
                            materialObject != null
                                ? AssetDatabase.GetAssetPath(materialObject)
                                : label
                        ),
                        GUILayout.MinWidth(120f)
                    );
                    priority.floatValue = EditorGUILayout.Slider(
                        priority.floatValue,
                        0f,
                        1f,
                        GUILayout.MinWidth(180f)
                    );
                }
            }
        }

        private static bool HasCustomMaterialPriority(SerializedProperty priorities)
        {
            for (var i = 0; i < priorities.arraySize; i++)
            {
                var priority = priorities
                    .GetArrayElementAtIndex(i)
                    .FindPropertyRelative(nameof(PartAtlasMaterialPriority.Priority));

                if (Mathf.Approximately(priority.floatValue, 1f) is false)
                    return true;
            }

            return false;
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
            var avatarRootExtraction = IsAvatarRootExtraction();
            using (new EditorGUI.DisabledScope(avatarRootExtraction))
            {
                var stripValue = avatarRootExtraction ? true : _options.StripAvatarComponents;
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
                ExtractionRootPath = _rootPaths.Length > _rootIndex ? _rootPaths[_rootIndex] : "",
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

            if (_rootPaths.Length == 0)
            {
                _extractionRoot = null;
                _entries.Clear();
                RefreshMaterialCandidates(preserveSelection: false);
                return;
            }

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

            if (validRoots.Count == 0)
            {
                _rootPaths = Array.Empty<string>();
                _rootLabels = Array.Empty<string>();
                return;
            }

            var paths = _loadedPrefabRoot
                .GetComponentsInChildren<Transform>(true)
                .Where(validRoots.Contains)
                .Select(transform => RelativePath(root, transform))
                .ToArray();

            _rootPaths = paths;
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
            _outputName = _extractionRoot.name;
            RefreshMaterialCandidates(preserveSelection: false);
        }

        private void RefreshMaterialCandidates(bool preserveSelection)
        {
            if (_atlasSettings == null)
            {
                _materialCandidates.Clear();
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

            SyncMaterialSizePriorities();
            EditorUtility.SetDirty(_atlasSettings);
            _atlasSettingsObject?.UpdateIfRequiredOrScript();
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

            SyncMaterialSizePriorities();
            EditorUtility.SetDirty(_atlasSettings);
            _atlasSettingsObject?.UpdateIfRequiredOrScript();
        }

        private void SyncMaterialSizePriorities()
        {
            if (_atlasSettings == null) return;

            var previous = _atlasSettings.MaterialSizePriorities
                .Where(priority => priority != null && priority.Material != null)
                .GroupBy(priority => priority.Material!)
                .ToDictionary(group => group.Key, group => group.First());

            var next = new List<PartAtlasMaterialPriority>();
            foreach (var material in _atlasSettings.AtlasTargetMaterials)
            {
                if (material == null) continue;

                if (previous.TryGetValue(material, out var existing))
                {
                    next.Add(existing);
                }
                else
                {
                    next.Add(new PartAtlasMaterialPriority
                    {
                        Material = material,
                        Priority = 1f,
                    });
                }
            }

            _atlasSettings.MaterialSizePriorities = next;
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
            _analysis = null;
            _entries.Clear();
            _materialCandidates.Clear();
            _outputName = "";
            _extractionRoot = null;
            _rootPaths = Array.Empty<string>();
            _rootLabels = Array.Empty<string>();

            if (_atlasSettings != null)
            {
                _atlasSettings.AtlasTargetMaterials.Clear();
                _atlasSettings.MaterialSizePriorities.Clear();
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
