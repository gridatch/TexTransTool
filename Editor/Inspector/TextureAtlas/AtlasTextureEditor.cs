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
        private SerializedProperty sAtlasExcludedRenderers;

        private SerializedProperty sIslandSizePriorityTuner;
        private SerializedProperty sMergeMaterialGroups, sAllMaterialMergeReference;
        private SerializedProperty sAtlasSetting;

        private SerializedProperty sAtlasTextureSize, sAutoAtlasTextureSize, sCustomAspect, sAtlasTextureHeightSize;
        private SerializedProperty sAtlasTargetUVChannel;
        private SerializedProperty sUsePrimaryMaximumTexture, sPrimaryTextureProperty;
        private SerializedProperty sPadding;
        private SerializedProperty sForceSetTexture;
        private SerializedProperty sForceSizePriority;
        private SerializedProperty sIncludeDisabledRenderer;
        private SerializedProperty sPixelNormalize;
        private SerializedProperty sTextureFineTuning;
        private SerializedProperty sBackGroundColor;

        public void OnEnable()
        {
            thisTarget = target as AtlasTexture;
            var thisSObject = serializedObject;
            sAtlasSetting = thisSObject.FindProperty("AtlasSetting");

            // sLimitCandidateMaterials = thisSObject.FindProperty("LimitCandidateMaterials");
            sAtlasTargetMaterials = thisSObject.FindProperty(nameof(AtlasTexture.AtlasTargetMaterials));
            sAtlasExcludedRenderers = thisSObject.FindProperty(nameof(AtlasTexture.AtlasExcludedRenderers));
            sAtlasTargetUVChannel = sAtlasSetting.FindPropertyRelative("AtlasTargetUVChannel");

            sIslandSizePriorityTuner = thisSObject.FindProperty(nameof(AtlasTexture.IslandSizePriorityTuner));


            sMergeMaterialGroups = thisSObject.FindProperty(nameof(AtlasTexture.MergeMaterialGroups));
            sAllMaterialMergeReference = thisSObject.FindProperty("AllMaterialMergeReference");



            sAtlasTextureSize = sAtlasSetting.FindPropertyRelative("AtlasTextureSize");
            sAutoAtlasTextureSize = sAtlasSetting.FindPropertyRelative("AutoAtlasTextureSize");
            sCustomAspect = sAtlasSetting.FindPropertyRelative("CustomAspect");
            sAtlasTextureHeightSize = sAtlasSetting.FindPropertyRelative("AtlasTextureHeightSize");


            sUsePrimaryMaximumTexture = sAtlasSetting.FindPropertyRelative("UsePrimaryMaximumTexture");
            sPrimaryTextureProperty = sAtlasSetting.FindPropertyRelative("PrimaryTextureProperty");

            sForceSetTexture = sAtlasSetting.FindPropertyRelative("ForceSetTexture");
            sPadding = sAtlasSetting.FindPropertyRelative("IslandPadding");
            sIncludeDisabledRenderer = sAtlasSetting.FindPropertyRelative("IncludeDisabledRenderer");
            sPixelNormalize = sAtlasSetting.FindPropertyRelative("PixelNormalize");
            sTextureFineTuning = sAtlasSetting.FindPropertyRelative("TextureFineTuning");
            sForceSizePriority = sAtlasSetting.FindPropertyRelative("ForceSizePriority");

            sBackGroundColor = sAtlasSetting.FindPropertyRelative("BackGroundColor");

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
            }


            DrawTargetRenderers();


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
                using (new EditorGUI.IndentLevelScope(-1))
                using (new PFScope("DrawMaterialMergeGroup"))
                    DrawMaterialMergeGroup(sMergeMaterialGroups);
                EditorGUILayout.PropertyField(sAllMaterialMergeReference, "AtlasTexture:prop:AllMaterialMergeReference".GlcV());
            }

            using (new PFScope("DrawAtlasSettings"))
                DrawAtlasSettings();

            if (PreviewUtility.IsPreviewContains is false)
            {
                EditorGUILayout.Space();
                if (GUILayout.Button("AtlasTexture:button:BakeAtlas".Glc()))
                    AtlasTextureExistingPartsReplacer.ReplaceExistingParts(thisTarget);
            }

        }

        private void DrawTargetRenderers()
        {
            var domainRoot = DomainMarkerFinder.FindMarker(thisTarget.gameObject);
            if (domainRoot == null) { return; }

            var domainRenderers = domainRoot.GetComponentsInChildren<Renderer>(true);
            using var domain = new NotWorkDomain(domainRenderers, null);

            var selectedMaterials = new List<Material?>();
            for (var i = 0; i < sAtlasTargetMaterials.arraySize; i += 1)
            {
                selectedMaterials.Add(
                    sAtlasTargetMaterials.GetArrayElementAtIndex(i).objectReferenceValue as Material
                );
            }

            var candidateRenderers = AtlasTexture.GetAtlasCandidateRenderers(
                domain,
                domainRenderers,
                selectedMaterials,
                sIncludeDisabledRenderer.boolValue,
                (UVChannel)sAtlasTargetUVChannel.enumValueIndex
            );

            EditorGUILayout.Space();
            EditorGUILayout.LabelField(
                string.Format(
                    "AtlasTexture:label:TargetRenderers".GetLocalize(),
                    candidateRenderers.Length
                ),
                EditorStyles.boldLabel
            );

            if (candidateRenderers.Length == 0)
            {
                EditorGUILayout.HelpBox(
                    "AtlasTexture:info:NoTargetRenderers".GetLocalize(),
                    MessageType.Info
                );
                return;
            }

            using var indent = new EditorGUI.IndentLevelScope(1);
            foreach (var renderer in candidateRenderers)
            {
                var excludedIndex = FindRendererReferenceIndex(sAtlasExcludedRenderers, renderer);
                var isIncluded = excludedIndex < 0;

                using var row = new EditorGUILayout.HorizontalScope();
                var nextIncluded = EditorGUILayout.Toggle(isIncluded, GUILayout.Width(18f));

                var path = AnimationUtility.CalculateTransformPath(
                    renderer.transform,
                    domainRoot.transform
                );
                if (string.IsNullOrEmpty(path)) { path = renderer.gameObject.name; }

                if (GUILayout.Button(
                    $"{path} [{renderer.GetType().Name}]",
                    EditorStyles.label
                ))
                {
                    EditorGUIUtility.PingObject(renderer.gameObject);
                }

                if (nextIncluded != isIncluded)
                {
                    SetRendererIncluded(
                        sAtlasExcludedRenderers,
                        renderer,
                        nextIncluded
                    );
                }
            }
        }

        private static int FindRendererReferenceIndex(
            SerializedProperty rendererArray,
            Renderer renderer)
        {
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
            Renderer renderer,
            bool included)
        {
            var existingIndex = FindRendererReferenceIndex(excludedRenderers, renderer);

            if (included)
            {
                if (existingIndex < 0) { return; }

                var element = excludedRenderers.GetArrayElementAtIndex(existingIndex);
                element.objectReferenceValue = null;
                excludedRenderers.DeleteArrayElementAtIndex(existingIndex);
                return;
            }

            if (existingIndex >= 0) { return; }

            var newIndex = excludedRenderers.arraySize;
            excludedRenderers.arraySize += 1;
            excludedRenderers.GetArrayElementAtIndex(newIndex).objectReferenceValue = renderer;
        }

        private void DrawIslandSizePriorityTunerWithAdvanced(SerializedProperty sIslandSizePriorityTuner, IEnumerable<Material> targetMaterials)
        {
            using var vs = new EditorGUILayout.VerticalScope(EditorStyles.helpBox);
            for (var i = 0; sIslandSizePriorityTuner.arraySize > i; i += 1)
            {
                using var vs2 = new EditorGUILayout.VerticalScope(EditorStyles.helpBox);
                var isPt = sIslandSizePriorityTuner.GetArrayElementAtIndex(i);
                switch (isPt.managedReferenceValue)
                {
                    case SetFromIslandSelector:
                        {
                            SetFromIslandSelectorDrawer.DrawNow(isPt);
                            break;
                        }
                    case SetFromMaterial:
                        {
                            SetFromMaterialDrawer.DrawNow(isPt, targetMaterials);
                            break;
                        }
                }
            }

        }

        private void DrawAtlasSettings()
        {
            using var pf = new PFScope("DrawAtlasSettings");
            EditorGUILayout.LabelField("AtlasTexture:label:AtlasSettings".Glc(), EditorStyles.boldLabel);
            using var t = new EditorGUI.IndentLevelScope(1);

            EditorGUILayout.PropertyField(sAutoAtlasTextureSize, "AtlasTexture:prop:AutoAtlasTextureSize".GlcV());
            using (new EditorGUI.DisabledScope(sAutoAtlasTextureSize.boolValue))
            {
                EditorGUILayout.PropertyField(sAtlasTextureSize, "AtlasTexture:prop:AtlasTextureSize".GlcV());
                if (sCustomAspect.boolValue) EditorGUILayout.PropertyField(sAtlasTextureHeightSize, "AtlasTexture:prop:AtlasTextureHeightSize".GlcV());
                EditorGUILayout.PropertyField(sCustomAspect, "AtlasTexture:prop:CustomAspect".GlcV());
            }

            using (var cc = new EditorGUI.ChangeCheckScope())
            {
                EditorGUILayout.PropertyField(sAtlasTargetUVChannel, "AtlasTexture:prop:AtlasTargetUVChannel".GlcV());
                if (cc.changed) RefreshMaterials();
            }

            EditorGUILayout.PropertyField(sUsePrimaryMaximumTexture, "AtlasTexture:prop:UsePrimaryMaximumTexture".GlcV());
            if (sUsePrimaryMaximumTexture.boolValue is false) EditorGUILayout.PropertyField(sPrimaryTextureProperty, "AtlasTexture:prop:PrimaryTextureProperty".GlcV());


            EditorGUILayout.PropertyField(sPadding, "AtlasTexture:prop:Padding".GlcV());

            using (var cc = new EditorGUI.ChangeCheckScope())
            {
                EditorGUILayout.PropertyField(sIncludeDisabledRenderer, "AtlasTexture:prop:IncludeDisabledRenderer".GlcV());
                if (cc.changed) RefreshMaterials();
            }
            EditorGUILayout.PropertyField(sForceSizePriority, "AtlasTexture:prop:ForceSizePriority".GlcV());


            EditorGUILayout.PropertyField(sForceSetTexture, "AtlasTexture:prop:ForceSetTexture".GlcV());
            EditorGUILayout.PropertyField(sBackGroundColor, "AtlasTexture:prop:BackGroundColor".GlcV());
            EditorGUILayout.PropertyField(sPixelNormalize, "AtlasTexture:prop:PixelNormalize".GlcV());

            pf.Split("TextureFineTuning");
            EditorGUILayout.PropertyField(sTextureFineTuning, "AtlasTexture:prop:TextureFineTuning".GlcV());
        }

        private static void DrawMaterialMergeGroup(SerializedProperty MergeMaterialGroups)
        {
            EditorGUILayout.PropertyField(MergeMaterialGroups, "AtlasTexture:prop:MergeMaterialGroups".GlcV());
            if (MergeMaterialGroups.isExpanded) { return; }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("+"))
                {
                    var newIndex = MergeMaterialGroups.arraySize;
                    MergeMaterialGroups.arraySize += 1;

                    var mmg = MergeMaterialGroups.GetArrayElementAtIndex(newIndex);
                    mmg.FindPropertyRelative("Reference").objectReferenceValue = null;
                    mmg.FindPropertyRelative("Group").arraySize = 0;
                }
                if (GUILayout.Button("-")) { MergeMaterialGroups.arraySize += -1; }
            }

            using var vs = new EditorGUILayout.VerticalScope(EditorStyles.helpBox);

            for (var i = 0; MergeMaterialGroups.arraySize > i; i += 1)
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    var mmg = MergeMaterialGroups.GetArrayElementAtIndex(i);
                    var mRef = mmg.FindPropertyRelative("Reference");
                    var mg = mmg.FindPropertyRelative("Group");
                    var mgGUIContent = "AtlasTexture:prop:MaterialMergeGroups:GroupMaterials".Glc();

                    EditorGUILayout.PropertyField(mRef, "AtlasTexture:prop:MaterialMergeGroups:MergeReferenceMaterial".Glc());

                    TargetObjectSelector.DrawTargetSelectionSlimLayout(mg, s_targetMatHash);
                    for (var mgi = 0; mg.arraySize > mgi; mgi += 1) { s_targetMatHash.Remove(mg.GetArrayElementAtIndex(mgi).objectReferenceValue as Material); }
                }
        }

        static HashSet<Material> s_targetMatHash = new();



        List<List<Material>> _displayMaterial;

        void RefreshMaterials()
        {
            var domainFindPoint = target as AtlasTexture;
            var includeDisabledRenderer = thisTarget.AtlasSetting.IncludeDisabledRenderer;
            var uvChannel = (UVChannel)sAtlasTargetUVChannel.enumValueIndex;

            _displayMaterial = null;

            var domainRoot = DomainMarkerFinder.FindMarker(domainFindPoint.gameObject);
            if (domainRoot == null) { return; }

            var nwDomain = new NotWorkDomain(Array.Empty<Renderer>(), null);
            var domainRenderers = domainRoot.GetComponentsInChildren<Renderer>(true)
                .Where(AtlasTexture.IsAtlasAllowedRenderer)
                .Where(r => AtlasTexture.CheckRendererActive(nwDomain, r, domainFindPoint.AtlasSetting.IncludeDisabledRenderer));

            List<Material> filteredMaterials = RendererUtility.GetMaterials(domainRenderers).Distinct().Where(m => m != null).ToList();

            _displayMaterial = new MaterialGroupingContext(filteredMaterials.ToHashSet(), uvChannel, null).GroupMaterials.Select(i => new List<Material>(i)).ToList();
        }

        public static void MaterialSelectEditor(SerializedProperty targetMaterials, List<List<Material>> tempMaterialGroupAll)
        {
            foreach (var matGroup in tempMaterialGroupAll)
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    TargetObjectSelector.DrawTargetSelectionSlimLayout(targetMaterials, matGroup);
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
