#nullable enable
using UnityEditor;
using UnityEngine;
using net.rs64.TexTransTool.Editor;

namespace net.rs64.TexTransTool.TextureAtlas.Editor
{
    [CustomEditor(typeof(AutoAtlasTexture))]
    internal sealed class AutoAtlasTextureEditor : TexTransMonoBaseEditor
    {
        private SerializedProperty _islandPadding = null!;
        private SerializedProperty _enableAdditionalAtlasMerging = null!;
        private SerializedProperty _minimumMergeHostFreeAreaRatio = null!;
        private SerializedProperty _minimumAdditionalMergeSavingsRate = null!;
        private SerializedProperty _excludedRenderers = null!;
        private SerializedProperty _excludedMaterials = null!;
        private bool _advanced;

        private void OnEnable()
        {
            _islandPadding = serializedObject.FindProperty(nameof(AutoAtlasTexture.IslandPadding));
            _enableAdditionalAtlasMerging = serializedObject.FindProperty(nameof(AutoAtlasTexture.EnableAdditionalAtlasMerging));
            _minimumMergeHostFreeAreaRatio = serializedObject.FindProperty(nameof(AutoAtlasTexture.MinimumMergeHostFreeAreaRatio));
            _minimumAdditionalMergeSavingsRate = serializedObject.FindProperty(nameof(AutoAtlasTexture.MinimumAdditionalMergeSavingsRate));
            _excludedRenderers = serializedObject.FindProperty(nameof(AutoAtlasTexture.ExcludedRenderers));
            _excludedMaterials = serializedObject.FindProperty(nameof(AutoAtlasTexture.ExcludedMaterials));
        }

        // This is a build-time domain-wide operation, not an individual render effect.
        protected override bool DrawPreviewButton => false;

        protected override void OnTexTransComponentInspectorGUI()
        {
            _advanced = EditorGUILayout.Foldout(
                _advanced, "AutoAtlasTexture:label:AdvancedSettings".Glc(), true);
            if (!_advanced) return;

            using (new EditorGUI.IndentLevelScope())
            {
                EditorGUILayout.PropertyField(
                    _islandPadding, "AtlasTexture:prop:Padding".GlcV());
                EditorGUILayout.PropertyField(
                    _enableAdditionalAtlasMerging,
                    "AutoAtlasTexture:prop:EnableAdditionalAtlasMerging".GlcV());

                if (_enableAdditionalAtlasMerging.boolValue)
                {
                    using (new EditorGUI.IndentLevelScope())
                    {
                        EditorGUILayout.PropertyField(
                            _minimumMergeHostFreeAreaRatio,
                            "AutoAtlasTexture:prop:MinimumMergeHostFreeAreaRatio".GlcV());
                        EditorGUILayout.PropertyField(
                            _minimumAdditionalMergeSavingsRate,
                            "AutoAtlasTexture:prop:MinimumAdditionalMergeSavingsRate".GlcV());
                        EditorGUILayout.HelpBox(
                            "AutoAtlasTexture:help:AdditionalAtlasMerging".Glc(),
                            MessageType.Info);
                    }
                }

                EditorGUILayout.PropertyField(
                    _excludedRenderers, "AutoAtlasTexture:prop:ExcludedRenderers".GlcV(), true);
                EditorGUILayout.PropertyField(
                    _excludedMaterials, "AutoAtlasTexture:prop:ExcludedMaterials".GlcV(), true);
            }
        }
    }
}
