#nullable enable
using System;
using System.Collections.Generic;
using net.rs64.TexTransTool.Editor;
using net.rs64.TexTransTool.TextureAtlas.IslandSizePriorityTuner;
using UnityEditor;
using UnityEngine;

namespace net.rs64.TexTransTool.TextureAtlas.Editor
{
    [Flags]
    internal enum AtlasSettingDrawChange
    {
        None = 0,
        TargetUVChannel = 1 << 0,
        IncludeDisabledRenderer = 1 << 1,
    }

    /// <summary>
    /// Component-independent Atlas editor controls shared by the regular
    /// AtlasTexture Inspector and the standalone Prefab extraction workflow.
    /// </summary>
    internal static class AtlasSettingsEditorGUI
    {
        internal static AtlasSettingDrawChange DrawAtlasSettingFields(
            SerializedProperty atlasSetting,
            bool includeDisabledRenderer)
        {
            var autoSize = atlasSetting.FindPropertyRelative(
                nameof(AtlasSetting.AutoAtlasTextureSize)
            );
            var textureSize = atlasSetting.FindPropertyRelative(
                nameof(AtlasSetting.AtlasTextureSize)
            );
            var customAspect = atlasSetting.FindPropertyRelative(
                nameof(AtlasSetting.CustomAspect)
            );
            var heightSize = atlasSetting.FindPropertyRelative(
                nameof(AtlasSetting.AtlasTextureHeightSize)
            );
            var uvChannel = atlasSetting.FindPropertyRelative(
                nameof(AtlasSetting.AtlasTargetUVChannel)
            );
            var usePrimaryMaximum = atlasSetting.FindPropertyRelative(
                nameof(AtlasSetting.UsePrimaryMaximumTexture)
            );
            var primaryTextureProperty = atlasSetting.FindPropertyRelative(
                nameof(AtlasSetting.PrimaryTextureProperty)
            );
            var padding = atlasSetting.FindPropertyRelative(
                nameof(AtlasSetting.IslandPadding)
            );
            var forceSizePriority = atlasSetting.FindPropertyRelative(
                nameof(AtlasSetting.ForceSizePriority)
            );
            var forceSetTexture = atlasSetting.FindPropertyRelative(
                nameof(AtlasSetting.ForceSetTexture)
            );
            var backgroundColor = atlasSetting.FindPropertyRelative(
                nameof(AtlasSetting.BackGroundColor)
            );
            var pixelNormalize = atlasSetting.FindPropertyRelative(
                nameof(AtlasSetting.PixelNormalize)
            );
            var textureFineTuning = atlasSetting.FindPropertyRelative(
                nameof(AtlasSetting.TextureFineTuning)
            );

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

            var changes = AtlasSettingDrawChange.None;

            using (var cc = new EditorGUI.ChangeCheckScope())
            {
                EditorGUILayout.PropertyField(
                    uvChannel,
                    "AtlasTexture:prop:AtlasTargetUVChannel".GlcV()
                );
                if (cc.changed)
                    changes |= AtlasSettingDrawChange.TargetUVChannel;
            }

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

            if (includeDisabledRenderer)
            {
                var includeDisabled = atlasSetting.FindPropertyRelative(
                    nameof(AtlasSetting.IncludeDisabledRenderer)
                );
                using (var cc = new EditorGUI.ChangeCheckScope())
                {
                    EditorGUILayout.PropertyField(
                        includeDisabled,
                        "AtlasTexture:prop:IncludeDisabledRenderer".GlcV()
                    );
                    if (cc.changed)
                        changes |= AtlasSettingDrawChange.IncludeDisabledRenderer;
                }
            }

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

            return changes;
        }

        internal static void DrawMaterialSettingFields(
            SerializedProperty mergeMaterialGroups,
            SerializedProperty allMaterialMergeReference,
            HashSet<Material> targetMaterials,
            bool useAtlasTextureMergeGroupEditor)
        {
            if (useAtlasTextureMergeGroupEditor)
            {
                using (new EditorGUI.IndentLevelScope(-1))
                    DrawMaterialMergeGroup(mergeMaterialGroups, targetMaterials);
            }
            else
            {
                EditorGUILayout.PropertyField(
                    mergeMaterialGroups,
                    "AtlasTexture:prop:MergeMaterialGroups".GlcV()
                );
            }

            EditorGUILayout.PropertyField(
                allMaterialMergeReference,
                "AtlasTexture:prop:AllMaterialMergeReference".GlcV()
            );
        }

        internal static void DrawAdvancedIslandSizePriorityTuners(
            SerializedProperty tuners,
            IEnumerable<Material> targetMaterials)
        {
            using var outer = new EditorGUILayout.VerticalScope(EditorStyles.helpBox);
            for (var i = 0; i < tuners.arraySize; i++)
            {
                using var inner = new EditorGUILayout.VerticalScope(EditorStyles.helpBox);
                var tuner = tuners.GetArrayElementAtIndex(i);

                switch (tuner.managedReferenceValue)
                {
                    case SetFromIslandSelector:
                        SetFromIslandSelectorDrawer.DrawNow(tuner);
                        break;
                    case SetFromMaterial:
                        SetFromMaterialDrawer.DrawNow(tuner, targetMaterials);
                        break;
                }
            }
        }

        internal static void DrawStandaloneMaterialSizePriorityTuners(
            SerializedProperty tuners,
            IReadOnlyCollection<Material> targetMaterials,
            float contentWidth)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("+ SetFromMaterial", EditorStyles.miniButton))
                {
                    var newIndex = tuners.arraySize;
                    tuners.arraySize += 1;
                    tuners.GetArrayElementAtIndex(newIndex).managedReferenceValue =
                        new SetFromMaterial
                        {
                            PriorityValue = 1f,
                            Materials = new List<Material>(),
                        };
                }

                using (new EditorGUI.DisabledScope(tuners.arraySize == 0))
                {
                    if (GUILayout.Button(
                            "-",
                            EditorStyles.miniButton,
                            GUILayout.Width(28f)))
                    {
                        tuners.DeleteArrayElementAtIndex(tuners.arraySize - 1);
                    }
                }
            }

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
                        contentWidth
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

        private static void DrawMaterialMergeGroup(
            SerializedProperty mergeMaterialGroups,
            HashSet<Material> targetMaterials)
        {
            EditorGUILayout.PropertyField(
                mergeMaterialGroups,
                "AtlasTexture:prop:MergeMaterialGroups".GlcV()
            );
            if (mergeMaterialGroups.isExpanded)
                return;

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("+"))
                {
                    var newIndex = mergeMaterialGroups.arraySize;
                    mergeMaterialGroups.arraySize += 1;

                    var group = mergeMaterialGroups.GetArrayElementAtIndex(newIndex);
                    group.FindPropertyRelative("Reference").objectReferenceValue = null;
                    group.FindPropertyRelative("Group").arraySize = 0;
                }

                if (GUILayout.Button("-"))
                    mergeMaterialGroups.arraySize -= 1;
            }

            using var outer = new EditorGUILayout.VerticalScope(EditorStyles.helpBox);
            for (var i = 0; i < mergeMaterialGroups.arraySize; i++)
            {
                using var inner = new EditorGUILayout.VerticalScope(EditorStyles.helpBox);
                var group = mergeMaterialGroups.GetArrayElementAtIndex(i);
                var reference = group.FindPropertyRelative("Reference");
                var materials = group.FindPropertyRelative("Group");

                EditorGUILayout.PropertyField(
                    reference,
                    "AtlasTexture:prop:MaterialMergeGroups:MergeReferenceMaterial".Glc()
                );

                TargetObjectSelector.DrawTargetSelectionSlimLayout(
                    materials,
                    targetMaterials
                );

                for (var j = 0; j < materials.arraySize; j++)
                {
                    targetMaterials.Remove(
                        materials.GetArrayElementAtIndex(j).objectReferenceValue as Material
                    );
                }
            }
        }
    }
}
