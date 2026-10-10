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
                $"成功: {report.Completed.Count}グループ / {report.MaterialCount}マテリアル　見送り: {report.Skipped.Count}グループ");
            EditorGUILayout.LabelField(
                $"テクスチャ数: {report.OriginalTextureCount} → {report.GeneratedTextureCount}");
            EditorGUILayout.LabelField(
                $"対象テクスチャの総画素数: {FormatPixels(report.OriginalPixels)} → {FormatPixels(report.GeneratedPixels)}　" +
                $"削減 {report.SavedPercentage:F1}%");
            EditorGUILayout.HelpBox(
                "総画素数は、この処理によって置換される元テクスチャと生成テクスチャの合計です。" +
                "圧縮形式・MipMap・他のビルドツールによる変更は考慮しておらず、VRAMの実測値ではありません。",
                MessageType.Info);

            if (GUILayout.Button("レポート全文をコピー"))
                EditorGUIUtility.systemCopyBuffer = BuildTextReport(report);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            for (var i = 0; i < report.Completed.Count; i++)
            {
                var group = report.Completed[i];
                var title = $"#{i + 1} {string.Join(", ", group.MaterialNames.Take(3))}" +
                            (group.MaterialNames.Length > 3 ? $" ほか{group.MaterialNames.Length - 3}件" : "") +
                            $"  |  {group.SavedPercentage:F1}% 削減";
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

                    EditorGUILayout.LabelField("元のテクスチャ", EditorStyles.boldLabel);
                    foreach (var image in group.SourceTextures)
                        EditorGUILayout.LabelField(DescribeImage(image), EditorStyles.wordWrappedLabel);

                    EditorGUILayout.LabelField("生成したテクスチャ", EditorStyles.boldLabel);
                    foreach (var image in group.GeneratedTextures)
                        EditorGUILayout.LabelField(DescribeImage(image), EditorStyles.wordWrappedLabel);
                    EditorGUILayout.LabelField(
                        $"合計: {FormatPixels(group.SourcePixels)} → {FormatPixels(group.GeneratedPixels)} " +
                        $"({group.SavedPercentage:F1}% 削減)");
                }
                EditorGUILayout.Space(5f);
            }

            if (report.Skipped.Count != 0)
            {
                EditorGUILayout.Space(7f);
                EditorGUILayout.LabelField("変更を見送ったグループ", EditorStyles.boldLabel);
                foreach (var skipped in report.Skipped)
                    EditorGUILayout.LabelField(
                        $"{skipped.MaterialNames} — {skipped.Reason}", EditorStyles.wordWrappedLabel);
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
            sb.AppendLine($"成功 {report.Completed.Count}グループ、{report.MaterialCount}マテリアル、見送り {report.Skipped.Count}グループ");
            sb.AppendLine($"対象テクスチャ {report.OriginalTextureCount} → {report.GeneratedTextureCount}");
            sb.AppendLine($"総画素数 {report.OriginalPixels:N0} → {report.GeneratedPixels:N0} (削減 {report.SavedPercentage:F1}%)");
            sb.AppendLine("注: 画素数の比較であり、VRAMの実測削減量ではありません。");
            foreach (var group in report.Completed.Select((value, index) => (value, index)))
            {
                sb.AppendLine($"\n#{group.index + 1}: {string.Join(", ", group.value.MaterialNames)}");
                sb.AppendLine("Renderers: " + string.Join(", ", group.value.RendererNames));
                sb.AppendLine("Before:");
                foreach (var image in group.value.SourceTextures)
                    sb.AppendLine("  " + DescribeImage(image));
                sb.AppendLine("After:");
                foreach (var image in group.value.GeneratedTextures)
                    sb.AppendLine("  " + DescribeImage(image));
                sb.AppendLine($"削減 {group.value.SavedPercentage:F1}%");
            }
            if (report.Skipped.Count != 0)
            {
                sb.AppendLine("\n見送り:");
                foreach (var skip in report.Skipped)
                    sb.AppendLine($"  {skip.MaterialNames}: {skip.Reason}");
            }
            return sb.ToString();
        }
    }
}
