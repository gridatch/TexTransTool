#nullable enable
using UnityEditor;
using UnityEngine;
using net.rs64.TexTransTool.Editor;

namespace net.rs64.TexTransTool.TextureAtlas.Editor
{
    // Temporary validation aid. Remove together with the current
    // AutoAtlasTexture diagnostics once the merge planner is finalized.
    public static class AutoAtlasTextureDiagnosticsCache
    {
        private const string SessionKey =
            "net.rs64.TexTransTool.AutoAtlasTexture.LastDiagnostics";

        public static string LastDiagnostics
        {
            get => SessionState.GetString(SessionKey, string.Empty);
            set => SessionState.SetString(SessionKey, value ?? string.Empty);
        }

        public static void Clear()
        {
            SessionState.EraseString(SessionKey);
        }
    }

    [CustomEditor(typeof(AutoAtlasTexture))]
    internal sealed class AutoAtlasTextureEditor : TexTransMonoBaseEditor
    {
        private SerializedProperty _islandPadding = null!;
        private SerializedProperty _enableAdditionalAtlasMerging = null!;
        private SerializedProperty _excludedRenderers = null!;
        private SerializedProperty _excludedMaterials = null!;
        private bool _advanced;

        private void OnEnable()
        {
            _islandPadding = serializedObject.FindProperty(nameof(AutoAtlasTexture.IslandPadding));
            _enableAdditionalAtlasMerging = serializedObject.FindProperty(nameof(AutoAtlasTexture.EnableAdditionalAtlasMerging));
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

                EditorGUILayout.PropertyField(
                    _excludedRenderers, "AutoAtlasTexture:prop:ExcludedRenderers".GlcV(), true);
                EditorGUILayout.PropertyField(
                    _excludedMaterials, "AutoAtlasTexture:prop:ExcludedMaterials".GlcV(), true);

                using (new EditorGUI.DisabledScope(
                           string.IsNullOrEmpty(
                               AutoAtlasTextureDiagnosticsCache.LastDiagnostics)))
                {
                    if (GUILayout.Button(
                            "AutoAtlasTexture:button:CopyDiagnostics".GetLocalize()))
                    {
                        GUIUtility.systemCopyBuffer =
                            AutoAtlasTextureDiagnosticsCache.LastDiagnostics;
                    }
                }
            }
        }
    }
}
