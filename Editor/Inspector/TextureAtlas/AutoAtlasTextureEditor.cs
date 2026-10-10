#nullable enable
using UnityEditor;
using UnityEngine;
using net.rs64.TexTransTool.Editor;

namespace net.rs64.TexTransTool.TextureAtlas.Editor
{
    [CustomEditor(typeof(AutoAtlasTexture))]
    internal sealed class AutoAtlasTextureEditor : TexTransMonoBaseEditor
    {
        private SerializedProperty _maxAtlasSize = null!;
        private SerializedProperty _islandPadding = null!;
        private SerializedProperty _excludedRenderers = null!;
        private SerializedProperty _excludedMaterials = null!;
        private bool _advanced;

        private static readonly GUIContent[] s_maxAtlasSizeLabels =
        {
            new("256"), new("512"), new("1024"), new("2048"), new("4096"),
        };
        private static readonly int[] s_maxAtlasSizeValues =
        {
            256, 512, 1024, 2048, 4096,
        };

        private void OnEnable()
        {
            _maxAtlasSize = serializedObject.FindProperty(nameof(AutoAtlasTexture.MaxAtlasSize));
            _islandPadding = serializedObject.FindProperty(nameof(AutoAtlasTexture.IslandPadding));
            _excludedRenderers = serializedObject.FindProperty(nameof(AutoAtlasTexture.ExcludedRenderers));
            _excludedMaterials = serializedObject.FindProperty(nameof(AutoAtlasTexture.ExcludedMaterials));
        }

        // This is a build-time domain-wide operation, not an individual render effect.
        protected override bool DrawPreviewButton => false;
        protected override bool DrawExperimentalWarning => false;

        protected override void OnTexTransComponentInspectorGUI()
        {
            _advanced = EditorGUILayout.Foldout(_advanced, "詳細設定", true);
            if (!_advanced) return;

            using (new EditorGUI.IndentLevelScope())
            {
                EditorGUILayout.IntPopup(
                    _maxAtlasSize, s_maxAtlasSizeLabels, s_maxAtlasSizeValues,
                    new GUIContent("最大アトラスサイズ"));
                EditorGUILayout.PropertyField(
                    _islandPadding, new GUIContent("アイランドパディング"));
                EditorGUILayout.PropertyField(
                    _excludedRenderers, new GUIContent("除外レンダラー"), true);
                EditorGUILayout.PropertyField(
                    _excludedMaterials, new GUIContent("除外マテリアル"), true);
            }
        }
    }
}
