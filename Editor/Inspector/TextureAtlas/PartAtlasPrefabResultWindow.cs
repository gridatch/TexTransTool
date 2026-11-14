#nullable enable
using System.IO;
using UnityEditor;
using UnityEngine;

namespace net.rs64.TexTransTool.TextureAtlas.Editor
{
    internal sealed class PartAtlasPrefabResultWindow : EditorWindow
    {
        private PartAtlasPrefabExecutionSummary? _summary;
        private Vector2 _scroll;

        internal static void ShowResult(
            PartAtlasPrefabExecutionSummary summary)
        {
            var window =
                CreateInstance<PartAtlasPrefabResultWindow>();
            window._summary = summary;
            window.titleContent =
                new GUIContent("Prefab抽出結果");
            window.minSize = new Vector2(560f, 440f);
            window.ShowUtility();
        }

        private void OnGUI()
        {
            if (_summary == null)
            {
                Close();
                return;
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            DrawSummary(_summary);
            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space(8f);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(
                        "閉じる",
                        GUILayout.Width(96f),
                        GUILayout.Height(26f)))
                {
                    Close();
                }
            }
            EditorGUILayout.Space(6f);
        }

        private static void DrawSummary(
            PartAtlasPrefabExecutionSummary summary)
        {
            var extraction = summary.Extraction;

            EditorGUILayout.LabelField(
                "Prefab抽出完了",
                EditorStyles.boldLabel);
            EditorGUILayout.Space(4f);
            DrawValue(
                "生成Prefab",
                Path.GetFileNameWithoutExtension(extraction.PrefabPath));

            EditorGUILayout.Space(10f);
            DrawSectionTitle("抽出結果");
            DrawValue(
                "GameObject",
                summary.InputGameObjectCount + " → "
                + summary.OutputGameObjectCount + " 個");
            DrawValue(
                "Renderer",
                summary.InputRendererCount + " → "
                + summary.OutputRendererCount + " 件");

            if (summary.BoneArrayChanges.Count == 1)
            {
                var change = summary.BoneArrayChanges[0];
                DrawValue(
                    "ボーン配列",
                    change.Before + " → " + change.After
                    + " 本（" + change.RendererPath + "）");
            }
            else if (summary.BoneArrayChanges.Count > 1)
            {
                DrawValue(
                    "ボーン配列を切り詰めたRenderer",
                    summary.BoneArrayChanges.Count + " 件");

                const int maxVisibleChanges = 4;
                var visibleCount = Mathf.Min(
                    maxVisibleChanges, summary.BoneArrayChanges.Count);
                for (var i = 0; i < visibleCount; i++)
                {
                    var change = summary.BoneArrayChanges[i];
                    EditorGUILayout.LabelField(
                        "・" + change.RendererPath + ": "
                        + change.Before + " → " + change.After + " 本",
                        EditorStyles.wordWrappedLabel);
                }

                var remaining = summary.BoneArrayChanges.Count - visibleCount;
                if (remaining > 0)
                {
                    EditorGUILayout.LabelField(
                        "ほか " + remaining
                        + " 件（詳細は抽出レポート.txtに記録）",
                        EditorStyles.wordWrappedLabel);
                }
            }

            if (summary.AutoAtlas is { Executed: true } autoAtlas)
            {
                EditorGUILayout.Space(10f);
                DrawSectionTitle("自動アトラス化");

                foreach (var message in autoAtlas.ReportMessages)
                {
                    EditorGUILayout.Space(4f);
                    EditorGUILayout.LabelField(
                        message.FormatLocalized(),
                        EditorStyles.wordWrappedLabel);
                }

                if (autoAtlas.HasChanges)
                {
                    EditorGUILayout.Space(6f);
                    DrawValue(
                        "生成アセット",
                        "Texture " + autoAtlas.TextureCount
                        + " 件 / Material " + autoAtlas.MaterialCount
                        + " 件 / Mesh " + autoAtlas.MeshCount + " 件");
                }
            }
        }

        private static void DrawSectionTitle(string title)
        {
            EditorGUILayout.LabelField(
                title,
                EditorStyles.boldLabel);
        }

        private static void DrawValue(
            string label,
            string value)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(
                    label,
                    GUILayout.Width(190f));
                GUILayout.Label(
                    value ?? "",
                    EditorStyles.wordWrappedLabel,
                    GUILayout.ExpandWidth(true));
            }
        }
    }
}
