#nullable enable
using System.Linq;
using System.Text;
using net.rs64.TexTransTool.Editor;
using UnityEditor;
using UnityEngine;

namespace net.rs64.TexTransTool.TextureAtlas.Editor
{
    internal sealed class AutoAtlasTextureReportWindow : EditorWindow
    {
        private Vector2 _scroll;
        private readonly System.Collections.Generic.HashSet<int> _expanded = new();

        internal static void Open()
        {
            var window = GetWindow<AutoAtlasTextureReportWindow>("AutoAtlasTexture 結果");
            window.minSize = new Vector2(570f, 360f);
            window.Show();
        }

        private void OnGUI()
        {
            var report = AutoAtlasTextureReportStore.Latest;
            if (report == null)
            {
                EditorGUILayout.HelpBox("このUnityセッションには、まだ自動アトラス化の実行結果がありません。", MessageType.Info);
                return;
            }

            EditorGUILayout.LabelField("TTT AutoAtlasTexture — 実行結果", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Avatar", report.AvatarName);
            EditorGUILayout.LabelField("ビルド時刻", report.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"));
            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField(
                $"適用: {report.Completed.Count}グループ / {report.MaterialCount}マテリアル");
            EditorGUILayout.LabelField(
                $"テクスチャ数: {report.OriginalTextureCount} → {report.GeneratedTextureCount}");
            EditorGUILayout.HelpBox(
                "グループを展開すると、元テクスチャと生成テクスチャの名前・解像度、" +
                "上部の空き領域を確認できます。",
                MessageType.Info);

            if (GUILayout.Button("レポート全文をコピー"))
                EditorGUIUtility.systemCopyBuffer = BuildTextReport(report);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            for (var i = 0; i < report.Completed.Count; i++)
            {
                var group = report.Completed[i];
                var title = $"#{i + 1} {string.Join(", ", group.MaterialNames.Take(3))}" +
                            (group.MaterialNames.Length > 3 ? $" ほか{group.MaterialNames.Length - 3}件" : "") +
                            $"  |  上部の空き領域: {group.TopFreeFraction * 100f:F1}%";
                var expanded = _expanded.Contains(i);
                var next = EditorGUILayout.Foldout(expanded, title, true);
                if (next) _expanded.Add(i);
                else _expanded.Remove(i);
                if (!next) continue;

                using (new EditorGUI.IndentLevelScope())
                {
                    EditorGUILayout.LabelField("Material", EditorStyles.boldLabel);
                    foreach (var material in group.MaterialNames)
                        EditorGUILayout.LabelField(material, EditorStyles.wordWrappedLabel);

                    EditorGUILayout.LabelField("Renderer", EditorStyles.boldLabel);
                    foreach (var renderer in group.RendererNames)
                        EditorGUILayout.LabelField(renderer, EditorStyles.wordWrappedLabel);

                    EditorGUILayout.LabelField(
                        $"上部の空き領域: {group.TopFreeFraction * 100f:F1}%",
                        EditorStyles.boldLabel);
                    EditorGUILayout.LabelField("テクスチャ別の変更内容", EditorStyles.boldLabel);
                    foreach (var change in group.PropertyChanges)
                    {
                        var before = change.SourceTextures.Length == 0
                            ? "（元テクスチャなし）"
                            : string.Join(", ", change.SourceTextures
                                .Select(image => DescribeImage(image)));
                        EditorGUILayout.LabelField(
                            $"{string.Join(" / ", change.PropertyNames)}: {before} → {DescribeImage(change.GeneratedTexture)}",
                            EditorStyles.wordWrappedLabel);
                    }
                    EditorGUILayout.LabelField("元のテクスチャ", EditorStyles.boldLabel);
                    foreach (var image in group.SourceTextures)
                        EditorGUILayout.LabelField(DescribeImage(image), EditorStyles.wordWrappedLabel);

                    EditorGUILayout.LabelField("生成したテクスチャ", EditorStyles.boldLabel);
                    foreach (var image in group.GeneratedTextures)
                        EditorGUILayout.LabelField(DescribeImage(image), EditorStyles.wordWrappedLabel);
                    EditorGUILayout.LabelField(
                        $"参考・総画素数: {FormatPixels(group.SourcePixels)} → {FormatPixels(group.GeneratedPixels)} " +
                        $"({group.SavedPercentage:F1}% 削減。VRAM実測値ではありません)");
                }
                EditorGUILayout.Space(5f);
            }

            EditorGUILayout.EndScrollView();
        }

        private static string DescribeImage(AutoAtlasTextureImageReport image)
        {
            var property = string.IsNullOrEmpty(image.Properties) ? "" : $" [{image.Properties}]";
            return $"{image.Name}  {image.Width}×{image.Height}{property}";
        }

        private static string FormatPixels(long pixels)
        {
            return $"{pixels:N0} px ({pixels / 1_000_000.0:F2} Mpx)";
        }

        private static string BuildTextReport(AutoAtlasTextureBuildReport report)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"TTT AutoAtlasTexture / {report.AvatarName} / {report.CreatedAt:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"適用 {report.Completed.Count}グループ、{report.MaterialCount}マテリアル");
            sb.AppendLine($"対象テクスチャ {report.OriginalTextureCount} → {report.GeneratedTextureCount}");
            sb.AppendLine($"総画素数 {report.OriginalPixels:N0} → {report.GeneratedPixels:N0} (削減 {report.SavedPercentage:F1}%)");
            sb.AppendLine("注: 画素数の比較であり、VRAMの実測削減量ではありません。");
            foreach (var group in report.Completed.Select((value, index) => (value, index)))
            {
                sb.AppendLine($"\n#{group.index + 1}: {string.Join(", ", group.value.MaterialNames)}");
                sb.AppendLine("Renderers: " + string.Join(", ", group.value.RendererNames));
                sb.AppendLine($"上部の空き領域: {group.value.TopFreeFraction * 100f:F1}%");
                foreach (var change in group.value.PropertyChanges)
                {
                    var before = change.SourceTextures.Length == 0
                        ? "（元テクスチャなし）"
                        : string.Join(", ", change.SourceTextures
                            .Select(image => DescribeImage(image)));
                    sb.AppendLine($"  {string.Join(" / ", change.PropertyNames)}: {before} → {DescribeImage(change.GeneratedTexture)}");
                }
                sb.AppendLine("Before:");
                foreach (var image in group.value.SourceTextures)
                    sb.AppendLine("  " + DescribeImage(image));
                sb.AppendLine("After:");
                foreach (var image in group.value.GeneratedTextures)
                    sb.AppendLine("  " + DescribeImage(image));
                sb.AppendLine($"削減 {group.value.SavedPercentage:F1}%");
            }
            return sb.ToString();
        }
    }
}
