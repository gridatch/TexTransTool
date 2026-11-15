#nullable enable
using System;
using System.Collections.Generic;
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
                    DrawIndentedText(
                        "・" + change.RendererPath + ": "
                        + change.Before + " → " + change.After + " 本");
                }

                var remaining = summary.BoneArrayChanges.Count - visibleCount;
                if (remaining > 0)
                {
                    DrawIndentedText(
                        "ほか " + remaining
                        + " 件（詳細は抽出レポート.txtに記録）");
                }
            }

            if (summary.AutoAtlas is { Executed: true } autoAtlas)
            {
                EditorGUILayout.Space(10f);
                DrawSectionTitle("自動アトラス化");

                DrawAutoAtlasMessages(autoAtlas);

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

        private static void DrawAutoAtlasMessages(
            PartAutoAtlasExecutionResult autoAtlas)
        {
            foreach (var message in autoAtlas.ReportMessages)
            {
                EditorGUILayout.Space(4f);
                switch (message.LocalizationKey)
                {
                    case "AutoAtlasTexture:info:RelocateResult":
                        if (message.Arguments.Length > 0
                            && message.Arguments[0] is string groups)
                        {
                            DrawAutoAtlasGroups(groups);
                        }
                        else
                        {
                            DrawValue("詳細", message.FormatLocalized());
                        }
                        break;

                    case "AutoAtlasTexture:info:TargetNotFound":
                        DrawValue("結果", "アトラス化対象が存在しません。");
                        break;

                    case "AutoAtlasTexture:info:TwoDimensionalFallback":
                        DrawValue("補足", message.FormatLocalized());
                        break;

                    default:
                        DrawValue("詳細", message.FormatLocalized());
                        break;
                }
            }

            DrawValue(
                "自動アトラス総処理時間",
                autoAtlas.ProcessingMilliseconds + "ms");
        }

        private static void DrawAutoAtlasGroups(string report)
        {
            var blocks = report.Replace("\r\n", "\n")
                .Replace('\r', '\n')
                .Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries);

            for (var blockIndex = 0; blockIndex < blocks.Length; blockIndex++)
            {
                if (blockIndex > 0)
                    EditorGUILayout.Space(8f);

                var textureRoutes = new List<string>();
                foreach (var rawLine in blocks[blockIndex].Split('\n'))
                {
                    var line = rawLine.Trim();
                    if (line.Length == 0)
                        continue;

                    if (line.Contains("×") && line.Contains("→"))
                    {
                        textureRoutes.Add(line);
                        continue;
                    }

                    DrawTextureRoutes(textureRoutes);

                    var colon = line.IndexOf(':');
                    if (colon > 0)
                    {
                        DrawValue(
                            line.Substring(0, colon).Trim(),
                            line.Substring(colon + 1).Trim());
                    }
                    else
                    {
                        DrawIndentedText(line);
                    }
                }

                DrawTextureRoutes(textureRoutes);
            }
        }

        private static void DrawTextureRoutes(List<string> routes)
        {
            if (routes.Count == 0)
                return;

            DrawValue("テクスチャサイズ", string.Join("\n", routes));
            routes.Clear();
        }

        private static void DrawIndentedText(string text)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(16f);
                EditorGUILayout.LabelField(
                    text,
                    EditorStyles.wordWrappedLabel,
                    GUILayout.ExpandWidth(true));
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
                GUILayout.Space(16f);
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
