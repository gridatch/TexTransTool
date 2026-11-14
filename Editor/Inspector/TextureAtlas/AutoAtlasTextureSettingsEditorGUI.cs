#nullable enable
using UnityEditor;

namespace net.rs64.TexTransTool.TextureAtlas.Editor
{
    internal static class AutoAtlasTextureSettingsEditorGUI
    {
        internal static void DrawCoreSettings(
            SerializedProperty islandPadding,
            SerializedProperty optimizeConstantTextures,
            SerializedProperty constantTextureColorTolerance,
            SerializedProperty disableVisuallyUnusedShaderFeatures,
            SerializedProperty enableAdditionalAtlasMerging)
        {
            EditorGUILayout.PropertyField(
                islandPadding,
                "AtlasTexture:prop:Padding".GlcV());

            EditorGUILayout.PropertyField(
                optimizeConstantTextures,
                "AtlasTexture:prop:OptimizeConstantTextures".GlcV());

            using (new EditorGUI.DisabledScope(
                       !optimizeConstantTextures.boolValue))
            using (new EditorGUI.IndentLevelScope())
            {
                EditorGUILayout.PropertyField(
                    constantTextureColorTolerance,
                    "AtlasTexture:prop:ConstantTextureColorTolerance".GlcV());
            }

            EditorGUILayout.PropertyField(
                disableVisuallyUnusedShaderFeatures,
                "AtlasTexture:prop:DisableVisuallyUnusedShaderFeatures".GlcV());

            EditorGUILayout.PropertyField(
                enableAdditionalAtlasMerging,
                "AutoAtlasTexture:prop:EnableAdditionalAtlasMerging".GlcV());
        }
    }
}
