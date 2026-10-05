#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace net.rs64.TexTransTool.TextureAtlas.Editor
{
    internal static class MatsukawaIntegrationProbe
    {
        private const string MenuPath = "Tools/TexTransTool/WDT/Probe Matsukawa Integration";

        [MenuItem(MenuPath)]
        private static void RunFromMenu()
        {
            var selected = Selection.activeObject as GameObject;
            var report = Run(selected);
            Debug.Log(report);
            EditorGUIUtility.systemCopyBuffer = report;
            EditorUtility.DisplayDialog(
                "TexTransTool",
                report.Contains("RESULT: FAIL")
                    ? "松川ツール連携診断で問題を検出しました。詳細はConsoleに出力し、レポートをクリップボードへコピーしました。"
                    : "松川ツール連携診断が完了しました。詳細はConsoleに出力し、レポートをクリップボードへコピーしました。",
                "OK"
            );
        }

        [MenuItem(MenuPath, true)]
        private static bool ValidateMenu() => !EditorApplication.isPlayingOrWillChangePlaymode;

        internal static string Run(GameObject? prefabAsset)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Matsukawa / TTT integration probe");
            sb.AppendLine("============================================================");
            sb.AppendLine("This probe never calls HceExecutor.Execute / SavePrefab / TTT Atlas.");
            sb.AppendLine();

            Api api;
            try
            {
                api = Api.Resolve();
                sb.AppendLine("API contract: PASS");
                sb.AppendLine("Assembly: " + api.Core.Assembly.GetName().Name);
                sb.AppendLine("Required public API: PASS");
            }
            catch (Exception e)
            {
                sb.AppendLine("RESULT: FAIL");
                sb.AppendLine(Unwrap(e).ToString());
                return sb.ToString();
            }

            if (prefabAsset == null)
            {
                sb.AppendLine("Prefab probe: SKIPPED (Project上のPrefabを選択して実行するとAnalyzeまで確認できます)");
                sb.AppendLine("RESULT: API CONTRACT PASS");
                return sb.ToString();
            }

            try
            {
                ProbePrefab(sb, api, prefabAsset);
                sb.AppendLine("RESULT: PASS");
            }
            catch (Exception e)
            {
                sb.AppendLine("RESULT: FAIL");
                sb.AppendLine(Unwrap(e).ToString());
            }

            return sb.ToString();
        }

        private static void ProbePrefab(StringBuilder sb, Api api, GameObject prefabAsset)
        {
            if (!EditorUtility.IsPersistent(prefabAsset) || !PrefabUtility.IsPartOfPrefabAsset(prefabAsset))
                throw new InvalidOperationException("選択ObjectはProject上のPrefab Assetではありません。");

            var path = AssetDatabase.GetAssetPath(prefabAsset);
            if (string.IsNullOrEmpty(path)) throw new InvalidOperationException("Prefab Asset pathを取得できません。");

            sb.AppendLine("Prefab: " + path);
            sb.AppendLine("Prefab type: " + PrefabUtility.GetPrefabAssetType(prefabAsset));

            GameObject? root = null;
            try
            {
                root = PrefabUtility.LoadPrefabContents(path);
                if (root == null) throw new InvalidOperationException("LoadPrefabContents returned null.");

                var entriesObject = api.CollectRenderers.Invoke(null, new object[] { root });
                if (!(entriesObject is IList entries))
                    throw new InvalidOperationException("CollectRenderers result is not IList.");

                sb.AppendLine("CollectRenderers: PASS (" + entries.Count + ")");

                var skinned = 0;
                var readable = 0;
                var unreadable = 0;
                var reductionCandidates = 0;

                foreach (var entry in entries)
                {
                    if (entry == null) continue;
                    api.EntryKeep.SetValue(entry, true);
                    if (!(api.EntryRenderer.GetValue(entry) is SkinnedMeshRenderer smr)) continue;
                    skinned++;
                    var mesh = smr.sharedMesh;
                    if (mesh == null || !mesh.isReadable)
                    {
                        unreadable++;
                        continue;
                    }
                    var used = api.GetUsedBoneIndices.Invoke(null, new object[] { mesh, 0.0001f });
                    var usedCount = Count(used);
                    readable++;
                    if (smr.bones != null && usedCount < smr.bones.Length) reductionCandidates++;
                }

                sb.AppendLine("SkinnedMeshRenderer: " + skinned);
                sb.AppendLine("Weight-readable: " + readable);
                sb.AppendLine("Read/Write unavailable: " + unreadable);
                sb.AppendLine("Bone-table reduction candidates @ 0.0001: " + reductionCandidates);

                var options = Activator.CreateInstance(api.Options)
                    ?? throw new InvalidOperationException("HceOptionsを生成できません。");
                api.OptWorkOnCopy.SetValue(options, true);
                api.OptThreshold.SetValue(options, 0.0001f);
                api.OptBoneMode.SetValue(options, Enum.Parse(api.BoneMode, "WeightedOnly"));

                var analysis = api.Analyze.Invoke(null, new[] { (object)root, entriesObject!, options });
                if (analysis == null) throw new InvalidOperationException("Analyze returned null.");

                sb.AppendLine("Analyze: PASS");
                sb.AppendLine("  keep=" + Count(api.AnalysisKeep.GetValue(analysis)));
                sb.AppendLine("  deleteRoots=" + Count(api.AnalysisDeleteRoots.GetValue(analysis)));
                sb.AppendLine("  protectedObjects=" + Count(api.AnalysisProtected.GetValue(analysis)));
                sb.AppendLine("  essential=" + Count(api.AnalysisEssential.GetValue(analysis)));
                sb.AppendLine("  trimTargets=" + Count(api.AnalysisTrimTargets.GetValue(analysis)));
                sb.AppendLine("  warnings=" + Count(api.AnalysisWarnings.GetValue(analysis)));

                api.HighlighterSet.Invoke(null, new[] { analysis });
                api.HighlighterClear.Invoke(null, null);
                sb.AppendLine("HceHighlighter.Set/Clear: PASS");
                sb.AppendLine("Execute/SavePrefab: NOT CALLED (intentional)");
            }
            finally
            {
                try { api.HighlighterClear.Invoke(null, null); } catch { }
                if (root != null) PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static int Count(object? value)
        {
            if (value == null) return 0;
            if (value is ICollection c) return c.Count;
            var p = value.GetType().GetProperty("Count", BindingFlags.Public | BindingFlags.Instance);
            if (p != null && p.PropertyType == typeof(int)) return (int)(p.GetValue(value) ?? 0);
            if (value is IEnumerable e)
            {
                var n = 0;
                foreach (var _ in e) n++;
                return n;
            }
            return -1;
        }

        private static Exception Unwrap(Exception e)
        {
            while (e is TargetInvocationException tie && tie.InnerException != null) e = tie.InnerException;
            return e;
        }

        private sealed class Api
        {
            internal Type Core = null!, Options = null!, Entry = null!, Analysis = null!, BoneMode = null!,
                Highlighter = null!, Executor = null!, Report = null!;
            internal MethodInfo CollectRenderers = null!, GetUsedBoneIndices = null!, Analyze = null!,
                HighlighterSet = null!, HighlighterClear = null!;
            internal FieldInfo EntryRenderer = null!, EntryKeep = null!, OptWorkOnCopy = null!, OptBoneMode = null!,
                OptThreshold = null!, AnalysisKeep = null!, AnalysisDeleteRoots = null!, AnalysisProtected = null!,
                AnalysisEssential = null!, AnalysisTrimTargets = null!, AnalysisWarnings = null!;

            internal static Api Resolve()
            {
                var a = new Api
                {
                    Core = TypeOf("HairCostumeExtractor.HceCore"),
                    Options = TypeOf("HairCostumeExtractor.HceOptions"),
                    Entry = TypeOf("HairCostumeExtractor.HceRendererEntry"),
                    Analysis = TypeOf("HairCostumeExtractor.HceAnalysis"),
                    BoneMode = TypeOf("HairCostumeExtractor.HceBoneMode"),
                    Highlighter = TypeOf("HairCostumeExtractor.HceHighlighter"),
                    Executor = TypeOf("HairCostumeExtractor.HceExecutor"),
                    Report = TypeOf("HairCostumeExtractor.HceReport"),
                };

                EnumValue(a.BoneMode, "WeightedOnly");
                EnumValue(a.BoneMode, "MeshReferenced");

                a.CollectRenderers = Method(a.Core, "CollectRenderers", typeof(GameObject));
                Return(a.CollectRenderers, t => IsListOf(t, a.Entry), "List<HceRendererEntry>");
                a.GetUsedBoneIndices = Method(a.Core, "GetUsedBoneIndices", typeof(Mesh), typeof(float));
                Return(a.GetUsedBoneIndices, t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(HashSet<>) && t.GetGenericArguments()[0] == typeof(int), "HashSet<int>");
                a.Analyze = Method(a.Core, "Analyze", typeof(GameObject), typeof(IList<>).MakeGenericType(a.Entry), a.Options);
                Return(a.Analyze, t => t == a.Analysis, "HceAnalysis");

                a.HighlighterSet = Method(a.Highlighter, "Set", a.Analysis);
                a.HighlighterClear = Method(a.Highlighter, "Clear");
                BoolProperty(a.Highlighter, "Enabled");

                var execute = Method(a.Executor, "Execute", typeof(GameObject), typeof(IList<>).MakeGenericType(a.Entry), a.Options);
                Return(execute, t => t == a.Report, "HceReport");
                var save = Method(a.Executor, "SavePrefab", a.Report);
                Return(save, t => t == typeof(string), "string");
                var export = Method(a.Executor, "ExportUnityPackage", a.Report, typeof(string));
                Return(export, t => t == typeof(string), "string");

                a.EntryRenderer = Field(a.Entry, "renderer", typeof(Renderer));
                a.EntryKeep = Field(a.Entry, "keep", typeof(bool));
                a.OptWorkOnCopy = Field(a.Options, "workOnCopy", typeof(bool));
                a.OptBoneMode = Field(a.Options, "boneMode", a.BoneMode);
                a.OptThreshold = Field(a.Options, "weightThreshold", typeof(float));
                Field(a.Options, "keepPhysBoneChains", typeof(bool));
                Field(a.Options, "keepReferencedObjects", typeof(bool));
                Field(a.Options, "keepHumanoidBones", typeof(bool));
                Field(a.Options, "protectOtherComponents", typeof(bool));
                Field(a.Options, "stripAvatarComponents", typeof(bool));
                Field(a.Options, "forceDeletePaths", null);

                a.AnalysisKeep = Field(a.Analysis, "keep", null);
                a.AnalysisDeleteRoots = Field(a.Analysis, "deleteRoots", null);
                a.AnalysisProtected = Field(a.Analysis, "protectedObjects", null);
                a.AnalysisEssential = Field(a.Analysis, "essential", null);
                a.AnalysisTrimTargets = Field(a.Analysis, "trimTargets", null);
                a.AnalysisWarnings = Field(a.Analysis, "warnings", null);
                Field(a.Analysis, "keepBoneIndices", null);
                Field(a.Report, "result", typeof(GameObject));
                Field(a.Report, "succeeded", typeof(bool));
                Field(a.Report, "outputFolder", typeof(string));
                Field(a.Report, "prefabPath", typeof(string));
                return a;
            }

            private static Type TypeOf(string fullName)
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var t = asm.GetType(fullName, false);
                    if (t != null) return t;
                }
                throw new TypeLoadException(fullName);
            }

            private static MethodInfo Method(Type type, string name, params Type[] parameters)
            {
                var m = type.GetMethod(name, BindingFlags.Public | BindingFlags.Static, null, parameters, null);
                if (m == null) throw new MissingMethodException(type.FullName, name);
                return m;
            }

            private static FieldInfo Field(Type type, string name, Type? expected)
            {
                var f = type.GetField(name, BindingFlags.Public | BindingFlags.Instance)
                    ?? throw new MissingFieldException(type.FullName, name);
                if (expected != null && f.FieldType != expected)
                    throw new InvalidOperationException(type.FullName + "." + name + " type mismatch: " + f.FieldType.FullName);
                return f;
            }

            private static void BoolProperty(Type type, string name)
            {
                var p = type.GetProperty(name, BindingFlags.Public | BindingFlags.Static);
                if (p == null || p.PropertyType != typeof(bool) || !p.CanRead || !p.CanWrite)
                    throw new MissingMemberException(type.FullName, name);
            }

            private static void EnumValue(Type type, string name)
            {
                if (!type.IsEnum || !Enum.GetNames(type).Contains(name)) throw new MissingMemberException(type.FullName, name);
            }

            private static void Return(MethodInfo method, Func<Type, bool> predicate, string expected)
            {
                if (!predicate(method.ReturnType))
                    throw new InvalidOperationException(method.DeclaringType?.FullName + "." + method.Name + " return type is not " + expected);
            }

            private static bool IsListOf(Type type, Type element)
            {
                return type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>) && type.GetGenericArguments()[0] == element;
            }
        }
    }
}
