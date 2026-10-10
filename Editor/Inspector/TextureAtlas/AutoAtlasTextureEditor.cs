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

        private void OnEnable()
        {
            _maxAtlasSize = serializedObject.FindProperty(nameof(AutoAtlasTexture.MaxAtlasSize));
            _islandPadding = serializedObject.FindProperty(nameof(AutoAtlasTexture.IslandPadding));
            _excludedRenderers = serializedObject.FindProperty(nameof(AutoAtlasTexture.ExcludedRenderers));
            _excludedMaterials = serializedObject.FindProperty(nameof(AutoAtlasTexture.ExcludedMaterials));
        }

        // This is a build-time domain-wide operation, not an individual render effect.
        protected override bool DrawPreviewButton => false;

        protected override void OnTexTransComponentInspectorGUI()
        {
            EditorGUILayout.HelpBox(
                "ビルド時に使用されるUV領域を自動的にアトラス化します。対象マテリアルの手動選択は不要です。",
                MessageType.Info);

            EditorGUILayout.LabelField("対象マテリアル", "自動選択");
            EditorGUILayout.LabelField("アトラスサイズ", "無劣化の最小サイズを自動選択");
            EditorGUILayout.LabelField("マテリアル統合", "行わない");

            _advanced = EditorGUILayout.Foldout(_advanced, "詳細設定", true);
            if (!_advanced) return;

            using (new EditorGUI.IndentLevelScope())
            {
                EditorGUILayout.PropertyField(
                    _maxAtlasSize, new GUIContent("最大アトラスサイズ"));
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
