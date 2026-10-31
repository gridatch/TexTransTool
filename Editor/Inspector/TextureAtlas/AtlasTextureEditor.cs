using UnityEngine;
using UnityEditor;
using System.Linq;
using net.rs64.TexTransTool.Editor;
using System.Collections.Generic;
using UnityEngine.UIElements;
using UnityEditor.UIElements;
using net.rs64.TexTransTool.Editor.OtherMenuItem;
using net.rs64.TexTransTool.Utils;
using UnityEngine.Profiling;
using net.rs64.TexTransTool.Editor.Decal;
using System;
using net.rs64.TexTransTool.TextureAtlas.IslandSizePriorityTuner;

namespace net.rs64.TexTransTool.TextureAtlas.Editor
{
    [CustomEditor(typeof(AtlasTexture), true)]
    internal class AtlasTextureEditor : TexTransMonoBaseEditor
    {
        private AtlasTexture thisTarget;
        private SerializedProperty sAtlasTargetMaterials;
        private SerializedProperty sBakeName;
        private SerializedProperty sBakeExcludedRenderers;
        private SerializedProperty sBakeIncludedInactiveRenderers;

        private SerializedProperty sIslandSizePriorityTuner;
        private SerializedProperty sMergeMaterialGroups, sAllMaterialMergeReference;
        private SerializedProperty sAtlasSetting;

        private SerializedProperty sAtlasTargetUVChannel;

        private bool _inactiveMaterialsExpanded;
        private bool _inactiveBakeRenderersExpanded;

        public void OnEnable()
        {
            thisTarget = target as AtlasTexture;
            var thisSObject = serializedObject;
            sAtlasSetting = thisSObject.FindProperty(nameof(AtlasTexture.AtlasSetting));

            // sLimitCandidateMaterials = thisSObject.FindProperty("LimitCandidateMaterials");
            sAtlasTargetMaterials = thisSObject.FindProperty(nameof(AtlasTexture.AtlasTargetMaterials));
            sBakeName = thisSObject.FindProperty(nameof(AtlasTexture.BakeName));
            sBakeExcludedRenderers = thisSObject.FindProperty(nameof(AtlasTexture.BakeExcludedRenderers));
            sBakeIncludedInactiveRenderers = thisSObject.FindProperty(
                nameof(AtlasTexture.BakeIncludedInactiveRenderers));
            sAtlasTargetUVChannel = sAtlasSetting.FindPropertyRelative(
                nameof(AtlasSetting.AtlasTargetUVChannel)
            );

            sIslandSizePriorityTuner = thisSObject.FindProperty(nameof(AtlasTexture.IslandSizePriorityTuner));


            sMergeMaterialGroups = thisSObject.FindProperty(nameof(AtlasTexture.MergeMaterialGroups));
            sAllMaterialMergeReference = thisSObject.FindProperty(
                nameof(AtlasTexture.AllMaterialMergeReference)
            );



        }
        protected override void OnTexTransComponentInspectorGUI()
        {
            using (new EditorGUI.IndentLevelScope(1))
                EditorGUILayout.PropertyField(sAtlasTargetMaterials, "AtlasTexture:prop:SelectedMaterialView".GlcV());

            if (sAtlasTargetMaterials.isExpanded is false && PreviewUtility.IsPreviewContains is false)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (_displayMaterial is not null)
                    {
                        var l = new[] { GUILayout.MaxWidth(64f + 18f), GUILayout.MinWidth(18f), GUILayout.Height(18f) };
                        if (GUILayout.Button("AtlasTexture:button:SelectAll".GlcV(), l)) { SelectAll(sAtlasTargetMaterials, _displayMaterial); }
                        if (GUILayout.Button("AtlasTexture:button:Invert".GlcV(), l)) { SelectInvert(sAtlasTargetMaterials, _displayMaterial); }
                    }
                    if (GUILayout.Button("AtlasTexture:button:RefreshMaterials".GetLocalize()) || _displayMaterial == null)
                        RefreshMaterials();
                }
                if (_displayMaterial is not null)
                    using (new PFScope("MaterialSelectEditor"))
                        MaterialSelectEditor(sAtlasTargetMaterials, _displayMaterial);

                if (_inactiveDisplayMaterial is { Count: > 0 })
                {
                    var foldoutRect = EditorGUILayout.GetControlRect();
                    using (new EditorGUI.IndentLevelScope(2))
                    {
                        _inactiveMaterialsExpanded = EditorGUI.Foldout(
                            foldoutRect,
                            _inactiveMaterialsExpanded,
                            "AtlasTexture:label:InactiveRendererMaterials".GlcV(),
                            true
                        );
                    }

                    if (_inactiveMaterialsExpanded)
                    {
                        using var inactiveScope =
                            new PFScope("InactiveMaterialSelectEditor");
                        MaterialSelectEditor(
                            sAtlasTargetMaterials,
                            _inactiveDisplayMaterial
                        );
                    }
                }
            }


            // ここ s_targetMatHash はめっちゃステートフルだから気をつけるようにね
            s_targetMatHash.Clear();
            for (var i = 0; sAtlasTargetMaterials.arraySize > i; i += 1)
                s_targetMatHash.Add(sAtlasTargetMaterials.GetArrayElementAtIndex(i).objectReferenceValue as Material);


            EditorGUILayout.LabelField("AtlasTexture:label:IslandSizePriority".Glc(), EditorStyles.boldLabel);
            using (new EditorGUI.IndentLevelScope(1))
            using (new PFScope("IslandSizePriority"))
            {
                EditorGUILayout.PropertyField(sIslandSizePriorityTuner, "AtlasTexture:prop:IslandSizePriorityTuner".GlcV());
                if (sIslandSizePriorityTuner.isExpanded is false && PreviewUtility.IsPreviewContains is false)
                {
                    using (new EditorGUI.IndentLevelScope(-1))
                        DrawIslandSizePriorityTunerWithAdvanced(sIslandSizePriorityTuner, s_targetMatHash);
                }
            }

            EditorGUILayout.LabelField("AtlasTexture:label:MaterialSettings".Glc(), EditorStyles.boldLabel);
            using (new EditorGUI.IndentLevelScope(1))
            using (new PFScope("MaterialSettings"))
            {
                using (new PFScope("DrawMaterialMergeGroup"))
                {
                    AtlasSettingsEditorGUI.DrawMaterialSettingFields(
                        sMergeMaterialGroups,
                        sAllMaterialMergeReference,
                        s_targetMatHash,
                        useAtlasTextureMergeGroupEditor: true
                    );
                }
            }

            using (new PFScope("DrawAtlasSettings"))
                DrawAtlasSettings();

            DrawBakeSection();

        }

        private void DrawBakeSection()
        {
            if (PreviewUtility.IsPreviewContains) { return; }

            EditorGUILayout.Space();
            using var section = new EditorGUILayout.VerticalScope(EditorStyles.helpBox);

            EditorGUILayout.LabelField(
                "AtlasTexture:label:BakeSection".Glc(),
                EditorStyles.boldLabel
            );

            EditorGUILayout.PropertyField(
                sBakeName,
                "AtlasTexture:prop:BakeName".GlcV()
            );

            EditorGUILayout.Space(2f);
            DrawBakeTargetRenderers();

            EditorGUILayout.Space(4f);
            if (GUILayout.Button(
                "AtlasTexture:button:BakeAtlas".Glc(),
                GUILayout.Height(EditorGUIUtility.singleLineHeight + 6f)
            ))
            {
                if (AtlasTextureBaker.Bake(thisTarget))
                {
                    sBakeName.stringValue = "";
                }
            }
        }

        private void DrawBakeTargetRenderers()
        {
            var domainRoot = DomainMarkerFinder.FindMarker(thisTarget.gameObject);
            if (domainRoot == null) { return; }

            var domainRenderers =
                domainRoot.GetComponentsInChildren<Renderer>(true);
            using var domain = new NotWorkDomain(domainRenderers, null);

            if (AtlasTextureBakeTargetResolver.EnsureSelectionMigrated(
                    thisTarget, domain))
            {
                serializedObject.Update();
            }

            var candidateRenderers =
                AtlasTextureBakeTargetResolver.GetCandidateRenderers(
                    thisTarget,
                    domain
                );
            var activeRenderers = candidateRenderers
                .Where(renderer =>
                    AtlasTextureBakeTargetResolver.IsCurrentlyActive(
                        domain, renderer))
                .ToArray();
            var inactiveRenderers = candidateRenderers
                .Where(renderer =>
                    !AtlasTextureBakeTargetResolver.IsCurrentlyActive(
                        domain, renderer))
                .ToArray();

            var includedCount = activeRenderers.Count(renderer =>
                    IsBakeRendererIncluded(renderer, isActive: true))
                + inactiveRenderers.Count(renderer =>
                    IsBakeRendererIncluded(renderer, isActive: false));

            EditorGUILayout.LabelField(
                string.Format(
                    "AtlasTexture:label:BakeTargetRenderers".GetLocalize(),
                    includedCount,
                    candidateRenderers.Length
                ),
                EditorStyles.boldLabel
            );

            if (candidateRenderers.Length == 0)
            {
                EditorGUILayout.HelpBox(
                    "AtlasTexture:info:NoBakeTargetRenderers".GetLocalize(),
                    MessageType.Info
                );
                return;
            }

            using var indent = new EditorGUI.IndentLevelScope(1);
            foreach (var renderer in activeRenderers)
            {
                DrawBakeRendererToggle(
                    renderer,
                    domainRoot,
                    isActive: true
                );
            }

            if (inactiveRenderers.Length == 0)
                return;

            _inactiveBakeRenderersExpanded = EditorGUILayout.Foldout(
                _inactiveBakeRenderersExpanded,
                "AtlasTexture:label:InactiveBakeRenderers".Glc(),
                true
            );
            if (!_inactiveBakeRenderersExpanded)
                return;

            using var inactiveIndent =
                new EditorGUI.IndentLevelScope(1);
            foreach (var renderer in inactiveRenderers)
            {
                DrawBakeRendererToggle(
                    renderer,
                    domainRoot,
                    isActive: false
                );
            }
        }

        private void DrawBakeRendererToggle(
            Renderer renderer,
            GameObject domainRoot,
            bool isActive)
        {
            var isIncluded = IsBakeRendererIncluded(
                renderer, isActive);

            var rendererPath = AnimationUtility.CalculateTransformPath(
                renderer.transform,
                domainRoot.transform
            );
            if (string.IsNullOrEmpty(rendererPath))
            {
                rendererPath = renderer.gameObject.name;
            }

            var label = new GUIContent(
                rendererPath,
                renderer.GetType().Name + "\n" + rendererPath
            );

            var nextIncluded = EditorGUILayout.ToggleLeft(
                label, isIncluded);
            if (nextIncluded == isIncluded)
                return;

            SetRendererIncluded(
                sBakeExcludedRenderers,
                sBakeIncludedInactiveRenderers,
                renderer,
                isActive,
                nextIncluded
            );
        }

        private bool IsBakeRendererIncluded(
            Renderer renderer,
            bool isActive)
        {
            if (FindRendererReferenceIndex(
                    sBakeExcludedRenderers, renderer) >= 0)
                return false;

            if (isActive)
                return true;

            return FindRendererReferenceIndex(
                sBakeIncludedInactiveRenderers, renderer) >= 0;
        }

        private static int FindRendererReferenceIndex(
            SerializedProperty rendererArray,
            Renderer renderer)
        {
            if (rendererArray == null) { return -1; }

            for (var i = 0; i < rendererArray.arraySize; i += 1)
            {
                if (rendererArray.GetArrayElementAtIndex(i).objectReferenceValue == renderer)
                {
                    return i;
                }
            }

            return -1;
        }

        private static void SetRendererIncluded(
            SerializedProperty excludedRenderers,
            SerializedProperty includedInactiveRenderers,
            Renderer renderer,
            bool isActive,
            bool included)
        {
            if (excludedRenderers == null
                || includedInactiveRenderers == null)
                return;

            if (included)
            {
                RemoveRendererReference(excludedRenderers, renderer);
                if (!isActive)
                {
                    AddRendererReference(
                        includedInactiveRenderers, renderer);
                }
                return;
            }

            AddRendererReference(excludedRenderers, renderer);
            RemoveRendererReference(
                includedInactiveRenderers, renderer);
        }

        private static void AddRendererReference(
            SerializedProperty rendererArray,
            Renderer renderer)
        {
            if (FindRendererReferenceIndex(
                    rendererArray, renderer) >= 0)
                return;

            var newIndex = rendererArray.arraySize;
            rendererArray.arraySize += 1;
            rendererArray
                .GetArrayElementAtIndex(newIndex)
                .objectReferenceValue = renderer;
        }

        private static void RemoveRendererReference(
            SerializedProperty rendererArray,
            Renderer renderer)
        {
            var index = FindRendererReferenceIndex(
                rendererArray, renderer);
            if (index < 0)
                return;

            var element = rendererArray.GetArrayElementAtIndex(index);
            element.objectReferenceValue = null;
            rendererArray.DeleteArrayElementAtIndex(index);
        }

        private void DrawIslandSizePriorityTunerWithAdvanced(
            SerializedProperty tuners,
            IEnumerable<Material> targetMaterials)
        {
            AtlasSettingsEditorGUI.DrawAdvancedIslandSizePriorityTuners(
                tuners,
                targetMaterials
            );
        }

        private void DrawAtlasSettings()
        {
            EditorGUILayout.LabelField(
                "AtlasTexture:label:AtlasSettings".Glc(),
                EditorStyles.boldLabel
            );

            using var indent = new EditorGUI.IndentLevelScope(1);
            var changes = AtlasSettingsEditorGUI.DrawAtlasSettingFields(
                sAtlasSetting,
                includeDisabledRenderer: true
            );

            if ((changes & (
                    AtlasSettingDrawChange.TargetUVChannel
                    | AtlasSettingDrawChange.IncludeDisabledRenderer)) != 0)
            {
                RefreshMaterials();
            }
        }

        static HashSet<Material> s_targetMatHash = new();



        List<List<Material>> _displayMaterial;
        List<List<Material>> _inactiveDisplayMaterial;

        void RefreshMaterials()
        {
            var domainFindPoint = target as AtlasTexture;
            var uvChannel =
                (UVChannel)sAtlasTargetUVChannel.enumValueIndex;

            _displayMaterial = null;
            _inactiveDisplayMaterial = null;

            var domainRoot =
                DomainMarkerFinder.FindMarker(domainFindPoint.gameObject);
            if (domainRoot == null) { return; }

            var candidates =
                AtlasTextureMaterialCandidateResolver.Resolve(domainRoot);

            _displayMaterial = new MaterialGroupingContext(
                    candidates.ActiveMaterials,
                    uvChannel,
                    null)
                .GroupMaterials
                .Select(group => new List<Material>(group))
                .ToList();

            _inactiveDisplayMaterial = new MaterialGroupingContext(
                    candidates.InactiveMaterials,
                    uvChannel,
                    null)
                .GroupMaterials
                .Select(group => new List<Material>(group))
                .ToList();
        }

        public static void MaterialSelectEditor(SerializedProperty targetMaterials, List<List<Material>> tempMaterialGroupAll)
        {
            foreach (var matGroup in tempMaterialGroupAll)
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    TargetObjectSelector.DrawTargetSelectionSlimLayout(targetMaterials, matGroup);
                }
        }

        internal static void MaterialSelectEditor(
            SerializedProperty targetMaterials,
            List<List<Material>> tempMaterialGroupAll,
            float availableWidth,
            float elementWidth = 128f)
        {
            var groupContentWidth = Mathf.Max(
                1f,
                availableWidth
                - EditorStyles.helpBox.padding.horizontal
                - EditorStyles.helpBox.margin.horizontal
            );

            foreach (var matGroup in tempMaterialGroupAll)
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    TargetObjectSelector.DrawTargetSelectionSlimLayout(
                        targetMaterials,
                        matGroup,
                        elementWidth,
                        groupContentWidth
                    );
                }
        }

        public static SerializedProperty FindMatSelector(SerializedProperty targetMaterialArray, Material material)
        {
            for (int i = 0; targetMaterialArray.arraySize > i; i += 1)
            {
                var materialElement = targetMaterialArray.GetArrayElementAtIndex(i);
                if (materialElement.objectReferenceValue == material)
                {
                    return materialElement;
                }
            }
            return null;
        }
        public static int FindMatSelectorIndex(SerializedProperty targetMaterialArray, Material material)
        {
            for (int i = 0; targetMaterialArray.arraySize > i; i += 1)
            {
                var materialElement = targetMaterialArray.GetArrayElementAtIndex(i);
                if (materialElement.objectReferenceValue == material) { return i; }
            }
            return -1;
        }
        private static void SelectAll(SerializedProperty sMatSelectors, List<List<Material>> tempMaterialGroupAll)
        {
            var materials = tempMaterialGroupAll.SelectMany(i => i);
            foreach (var m in materials)
            {
                if (FindMatSelector(sMatSelectors, m) is not null) { continue; }
                var newIndex = sMatSelectors.arraySize;
                sMatSelectors.arraySize += 1;
                var newSelector = sMatSelectors.GetArrayElementAtIndex(newIndex).objectReferenceValue = m;
            }
        }
        private static void SelectInvert(SerializedProperty sMatSelectors, List<List<Material>> tempMaterialGroupAll)
        {
            var enables = tempMaterialGroupAll.SelectMany(i => i).Where(m => FindMatSelector(sMatSelectors, m) is null).ToArray();
            sMatSelectors.arraySize = 0;
            foreach (var m in enables)
            {
                var newIndex = sMatSelectors.arraySize;
                sMatSelectors.arraySize += 1;
                var newSelector = sMatSelectors.GetArrayElementAtIndex(newIndex).objectReferenceValue = m;
            }
        }
    }
}
