#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace net.rs64.TexTransTool.TextureAtlas.Editor
{
    internal sealed class MatsukawaReportSummary
    {
        internal string SourceName = "";
        internal string ResultName = "";
        internal string OutputFolder = "";
        internal string PrefabPath = "";

        internal IReadOnlyList<string> KeptRendererPaths = Array.Empty<string>();

        internal int DeletedObjectCount;
        internal int DeletedRendererCount;
        internal int StrippedRendererCount;
        internal int DroppedComponentCount;
        internal int OutputGameObjectCount;

        internal IReadOnlyList<string> TrimLog = Array.Empty<string>();
        internal IReadOnlyList<string> ProtectedPaths = Array.Empty<string>();
        internal IReadOnlyList<string> DeletedRoots = Array.Empty<string>();
        internal IReadOnlyList<string> GeneratedMeshes = Array.Empty<string>();
        internal IReadOnlyList<string> Notes = Array.Empty<string>();
        internal MatsukawaDiagnosisSummary? Diagnosis;
    }

    internal sealed class MatsukawaDiagnosisSummary
    {
        internal int ProblemCount;
        internal bool Passed;
        internal float MaxDeviation;
        internal int ComparedRenderers;
        internal int SkippedUnreadable;
        internal IReadOnlyList<string> DeviationOver = Array.Empty<string>();
        internal IReadOnlyList<string> VertexCountChanged = Array.Empty<string>();
        internal IReadOnlyList<string> MissingRenderers = Array.Empty<string>();
        internal IReadOnlyList<string> NullBones = Array.Empty<string>();
        internal IReadOnlyList<string> LostRootBone = Array.Empty<string>();
        internal IReadOnlyList<string> NullMaterials = Array.Empty<string>();
        internal IReadOnlyList<string> BlendShapeLost = Array.Empty<string>();
        internal IReadOnlyList<string> BrokenPhysBones = Array.Empty<string>();
        internal IReadOnlyList<string> RemainingPhysBones = Array.Empty<string>();
    }

    internal readonly struct PartAtlasBoneArrayChange
    {
        internal readonly string RendererPath;
        internal readonly int Before;
        internal readonly int After;

        internal PartAtlasBoneArrayChange(string rendererPath, int before, int after)
        {
            RendererPath = rendererPath;
            Before = before;
            After = after;
        }
    }

    internal sealed class PartAtlasPrefabExecutionSummary
    {
        internal DateTime CompletedAt;
        internal MatsukawaReportSummary Extraction = null!;
        internal PartAutoAtlasExecutionResult? AutoAtlas;
        internal string AtlasOutputFolder = "";
        internal int InputGameObjectCount;
        internal int OutputGameObjectCount;
        internal int InputRendererCount;
        internal int OutputRendererCount;
        internal IReadOnlyList<PartAtlasBoneArrayChange> BoneArrayChanges =
            Array.Empty<PartAtlasBoneArrayChange>();
    }

    internal static class PartAtlasPrefabReportFormatter
    {
        internal static string Format(
            PartAtlasPrefabExecutionSummary summary)
        {
            var extraction = summary.Extraction;
            var sb = new StringBuilder();

            sb.AppendLine(
                "TexTransTool Prefab抽出・アトラス化 実行レポート");
            sb.AppendLine(
                "========================================================================");
            sb.AppendLine(
                "日時          : "
                + summary.CompletedAt.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("入力Prefab     : " + extraction.SourceName);
            sb.AppendLine("生成物         : " + extraction.ResultName);
            sb.AppendLine("出力フォルダ   : " + extraction.OutputFolder);
            if (!string.IsNullOrEmpty(extraction.PrefabPath))
                sb.AppendLine("生成Prefab     : " + extraction.PrefabPath);

            sb.AppendLine();
            sb.AppendLine(
                "抽出したRenderer ("
                + extraction.KeptRendererPaths.Count
                + " 件)");
            foreach (var item in extraction.KeptRendererPaths)
                sb.AppendLine("  ・" + item);

            sb.AppendLine();
            sb.AppendLine("処理結果");
            sb.AppendLine(
                "  削除したGameObject          : "
                + extraction.DeletedObjectCount
                + " 個");
            sb.AppendLine(
                "  削除したRenderer            : "
                + extraction.DeletedRendererCount
                + " 件");
            sb.AppendLine(
                "  Renderer Componentのみ削除  : "
                + extraction.StrippedRendererCount
                + " 件");
            sb.AppendLine(
                "  除外したComponent           : "
                + extraction.DroppedComponentCount
                + " 個");
            sb.AppendLine(
                "  出力内GameObject            : "
                + extraction.OutputGameObjectCount
                + " 個");

            AppendDiagnosisSection(sb, extraction.Diagnosis);

            AppendAutoAtlasSection(
                sb,
                summary.AutoAtlas,
                summary.AtlasOutputFolder);

            AppendListSection(
                sb,
                "ボーン配列の切り詰め",
                extraction.TrimLog);

            AppendListSection(
                sb,
                "保護して残したGameObject ("
                + extraction.ProtectedPaths.Count
                + " 件)",
                extraction.ProtectedPaths);

            AppendListSection(
                sb,
                "削除したHierarchy Root ("
                + extraction.DeletedRoots.Count
                + " 件)",
                extraction.DeletedRoots);

            AppendListSection(
                sb,
                "生成したMesh",
                extraction.GeneratedMeshes);

            AppendListSection(
                sb,
                "補足",
                extraction.Notes);

            return sb.ToString();
        }

        private static void AppendDiagnosisSection(
            StringBuilder sb,
            MatsukawaDiagnosisSummary? diagnosis)
        {
            sb.AppendLine();
            sb.AppendLine("HCE抽出段階の自己診断");
            sb.AppendLine("  HCE抽出時点の結果です。後続の自動アトラス化後のPrefabを診断した結果ではありません。");
            if (diagnosis == null)
            {
                sb.AppendLine("  診断結果 : 未取得");
                return;
            }

            sb.AppendLine("  診断結果 : " + (diagnosis.Passed ? "問題検出なし" : "問題あり"));
            sb.AppendLine("  指摘件数 : " + diagnosis.ProblemCount + " 件");
            sb.AppendLine("  頂点比較したRenderer : " + diagnosis.ComparedRenderers + " 件");
            sb.AppendLine("  頂点データ未取得で比較を省略したRenderer : " + diagnosis.SkippedUnreadable + " 件");
            sb.AppendLine("  最大頂点差 : " + (diagnosis.ComparedRenderers > 0
                ? diagnosis.MaxDeviation.ToString("G9", CultureInfo.InvariantCulture) + " m"
                : "比較なし"));
            sb.AppendLine("  残存PhysBone : " + diagnosis.RemainingPhysBones.Count + " 件");

            AppendListSection(sb, "頂点差超過", diagnosis.DeviationOver);
            AppendListSection(sb, "頂点数変化", diagnosis.VertexCountChanged);
            AppendListSection(sb, "Renderer欠落", diagnosis.MissingRenderers);
            AppendListSection(sb, "Bone参照欠落", diagnosis.NullBones);
            AppendListSection(sb, "rootBone欠落", diagnosis.LostRootBone);
            AppendListSection(sb, "Material参照欠落", diagnosis.NullMaterials);
            AppendListSection(sb, "BlendShape数変化", diagnosis.BlendShapeLost);
            AppendListSection(sb, "PhysBone参照切れ", diagnosis.BrokenPhysBones);
            AppendListSection(sb, "残存PhysBoneのパス", diagnosis.RemainingPhysBones);
        }

        private static void AppendAutoAtlasSection(
            StringBuilder sb,
            PartAutoAtlasExecutionResult? autoAtlas,
            string atlasOutputFolder)
        {
            if (autoAtlas == null || !autoAtlas.Executed)
                return;

            sb.AppendLine();
            sb.AppendLine("自動アトラス化");

            foreach (var message in autoAtlas.ReportMessages)
            {
                sb.AppendLine();
                foreach (var line in SplitLines(message.FormatLocalized()))
                    sb.AppendLine("  " + line);
            }

            if (!autoAtlas.HasChanges)
                return;

            sb.AppendLine();
            sb.AppendLine(
                "  生成Texture  : "
                + autoAtlas.TextureCount
                + " 件");
            sb.AppendLine(
                "  生成Material : "
                + autoAtlas.MaterialCount
                + " 件");
            sb.AppendLine(
                "  生成Mesh     : "
                + autoAtlas.MeshCount
                + " 件");
            if (!string.IsNullOrEmpty(atlasOutputFolder))
                sb.AppendLine("  Asset出力先  : " + atlasOutputFolder);
        }

        private static void AppendListSection(
            StringBuilder sb,
            string title,
            IReadOnlyList<string> values)
        {
            if (values.Count == 0)
                return;

            sb.AppendLine();
            sb.AppendLine(title);
            foreach (var value in values)
                sb.AppendLine("  ・" + value);
        }

        private static IEnumerable<string> SplitLines(string text)
        {
            return (text ?? "")
                .Replace("\r\n", "\n")
                .Replace('\r', '\n')
                .Split('\n');
        }
    }
}
