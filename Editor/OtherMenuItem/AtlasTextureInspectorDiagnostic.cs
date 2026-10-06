#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;

namespace net.rs64.TexTransTool.Editor.OtherMenuItem
{
    internal static class AtlasTextureInspectorDiagnostic
    {
        private const string AtlasTextureTypeName =
            "net.rs64.TexTransTool.TextureAtlas.AtlasTexture";
        private const string AtlasTextureEditorTypeName =
            "net.rs64.TexTransTool.TextureAtlas.Editor.AtlasTextureEditor";
        private const string BaseEditorTypeName =
            "net.rs64.TexTransTool.Editor.TexTransMonoBaseEditor";

        [MenuItem("Tools/TexTransTool/WDT/Diagnose AtlasTexture Inspector")]
        private static void Run()
        {
            var report = BuildReport();
            Debug.Log(report);
            EditorGUIUtility.systemCopyBuffer = report;
            EditorUtility.DisplayDialog(
                "AtlasTexture Inspector Diagnostic",
                "診断レポートをConsoleへ出力し、クリップボードへコピーしました。",
                "OK"
            );
        }

        internal static string BuildReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine("AtlasTexture Inspector Diagnostic");
            sb.AppendLine("============================================================");
            sb.AppendLine("Unity: " + Application.unityVersion);
            sb.AppendLine("Time: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"));
            sb.AppendLine();

            var packages = PackageInfo.GetAllRegisteredPackages()
                .Where(p =>
                    p.name.Contains("tex-trans-tool", StringComparison.OrdinalIgnoreCase)
                    || p.resolvedPath.Contains("tex-trans-tool", StringComparison.OrdinalIgnoreCase)
                )
                .OrderBy(p => p.name, StringComparer.Ordinal)
                .ToArray();

            sb.AppendLine("[Registered packages]");
            if (packages.Length == 0)
            {
                sb.AppendLine("  (none)");
            }
            else
            {
                foreach (var package in packages)
                {
                    sb.AppendLine(
                        "  "
                        + package.name
                        + " @ "
                        + package.version
                        + " | "
                        + package.source
                        + " | "
                        + package.resolvedPath
                    );
                }
            }
            sb.AppendLine();

            var allTypes = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(GetTypesSafe)
                .ToArray();

            var atlasTypes = allTypes
                .Where(t => t.FullName == AtlasTextureTypeName)
                .ToArray();
            var atlasEditorTypes = allTypes
                .Where(t => t.FullName == AtlasTextureEditorTypeName)
                .ToArray();
            var baseEditorTypes = allTypes
                .Where(t => t.FullName == BaseEditorTypeName)
                .ToArray();

            sb.AppendLine("[Loaded AtlasTexture runtime types]");
            AppendTypes(sb, atlasTypes, includeFields: true);
            sb.AppendLine();

            sb.AppendLine("[Loaded AtlasTextureEditor types]");
            AppendTypes(sb, atlasEditorTypes, includeFields: false);
            sb.AppendLine();

            sb.AppendLine("[Loaded TexTransMonoBaseEditor types]");
            AppendTypes(sb, baseEditorTypes, includeFields: false);
            sb.AppendLine();

            var customEditorTypes = TypeCache.GetTypesWithAttribute<CustomEditor>()
                .Where(t =>
                    t.FullName == AtlasTextureEditorTypeName
                    || t.FullName == BaseEditorTypeName
                )
                .ToArray();

            sb.AppendLine("[Unity TypeCache CustomEditor entries]");
            AppendTypes(sb, customEditorTypes, includeFields: false);
            sb.AppendLine();

            var scripts = AssetDatabase.FindAssets("AtlasTexture t:MonoScript")
                .Select(guid => (guid, path: AssetDatabase.GUIDToAssetPath(guid)))
                .Where(x => string.IsNullOrEmpty(x.path) is false)
                .OrderBy(x => x.path, StringComparer.Ordinal)
                .ToArray();

            sb.AppendLine("[AtlasTexture MonoScript assets]");
            if (scripts.Length == 0)
            {
                sb.AppendLine("  (none)");
            }
            else
            {
                foreach (var script in scripts)
                {
                    var mono = AssetDatabase.LoadAssetAtPath<MonoScript>(script.path);
                    Type? scriptClass = null;
                    try { scriptClass = mono != null ? mono.GetClass() : null; }
                    catch (Exception e)
                    {
                        sb.AppendLine(
                            "  "
                            + script.path
                            + " | GUID="
                            + script.guid
                            + " | GetClass ERROR: "
                            + e.GetType().Name
                            + ": "
                            + e.Message
                        );
                        continue;
                    }

                    sb.AppendLine(
                        "  "
                        + script.path
                        + " | GUID="
                        + script.guid
                        + " | class="
                        + (scriptClass?.AssemblyQualifiedName ?? "<null>")
                    );
                }
            }
            sb.AppendLine();

            AppendExpectedSourceFile(
                sb,
                "Packages/net.gridatch.tex-trans-tool/Runtime/TextureAtlas/AtlasTexture.cs",
                "public string BakeName"
            );
            AppendExpectedSourceFile(
                sb,
                "Packages/net.gridatch.tex-trans-tool/Editor/Inspector/TextureAtlas/AtlasTextureEditor.cs",
                "DrawBakeSection"
            );
            AppendExpectedSourceFile(
                sb,
                "Packages/net.rs64.tex-trans-tool/Runtime/TextureAtlas/AtlasTexture.cs",
                "public string BakeName"
            );
            AppendExpectedSourceFile(
                sb,
                "Packages/net.rs64.tex-trans-tool/Editor/Inspector/TextureAtlas/AtlasTextureEditor.cs",
                "DrawBakeSection"
            );
            sb.AppendLine();

            var selected = FindSelectedAtlasTextureComponent();
            sb.AppendLine("[Selected AtlasTexture]");
            if (selected == null)
            {
                sb.AppendLine(
                    "  NOT FOUND - AtlasTextureコンポーネントのGameObjectを選択して再実行すると、"
                    + "MonoScript binding / resolved Editorまで診断できます。"
                );
            }
            else
            {
                var selectedType = selected.GetType();
                sb.AppendLine("  GameObject: " + selected.gameObject.name);
                sb.AppendLine("  Type: " + selectedType.AssemblyQualifiedName);
                sb.AppendLine("  Assembly: " + DescribeAssembly(selectedType.Assembly));
                sb.AppendLine(
                    "  Has BakeName field: "
                    + (selectedType.GetField(
                        "BakeName",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                    ) != null)
                );
                sb.AppendLine(
                    "  Has BakeExcludedRenderers field: "
                    + (selectedType.GetField(
                        "BakeExcludedRenderers",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                    ) != null)
                );

                if (selected is MonoBehaviour behaviour)
                {
                    var mono = MonoScript.FromMonoBehaviour(behaviour);
                    var path = mono != null ? AssetDatabase.GetAssetPath(mono) : "";
                    var guid = string.IsNullOrEmpty(path)
                        ? ""
                        : AssetDatabase.AssetPathToGUID(
                            path,
                            AssetPathToGUIDOptions.OnlyExistingAssets
                        );
                    sb.AppendLine("  MonoScript path: " + (path ?? "<null>"));
                    sb.AppendLine("  MonoScript GUID: " + guid);
                }

                UnityEditor.Editor? editor = null;
                try
                {
                    editor = UnityEditor.Editor.CreateEditor(selected);
                    sb.AppendLine(
                        "  Editor.CreateEditor resolved: "
                        + (editor?.GetType().AssemblyQualifiedName ?? "<null>")
                    );
                }
                catch (Exception e)
                {
                    sb.AppendLine(
                        "  Editor.CreateEditor ERROR: "
                        + e.GetType().Name
                        + ": "
                        + e.Message
                    );
                }
                finally
                {
                    if (editor != null) UnityEngine.Object.DestroyImmediate(editor);
                }
            }
            sb.AppendLine();

            sb.AppendLine("[Classification]");
            foreach (var line in Classify(
                         packages,
                         atlasTypes,
                         atlasEditorTypes,
                         customEditorTypes,
                         selected
                     ))
            {
                sb.AppendLine("  " + line);
            }

            return sb.ToString();
        }

        private static Component? FindSelectedAtlasTextureComponent()
        {
            var go = Selection.activeGameObject;
            if (go == null) return null;

            return go.GetComponents<Component>()
                .FirstOrDefault(component =>
                    component != null
                    && component.GetType().FullName == AtlasTextureTypeName
                );
        }

        private static IEnumerable<string> Classify(
            PackageInfo[] packages,
            Type[] atlasTypes,
            Type[] atlasEditorTypes,
            Type[] customEditorTypes,
            Component? selected)
        {
            var messages = new List<string>();

            var normalPackage = packages.Any(p => p.name == "net.rs64.tex-trans-tool");
            var wdtPackage = packages.Any(p => p.name == "net.gridatch.tex-trans-tool");
            if (normalPackage && wdtPackage)
            {
                messages.Add(
                    "PACKAGE_ID_CONFLICT: normal TTT と WDT が同一Projectに同時登録されています。"
                );
            }

            if (atlasTypes.Length == 0)
                messages.Add("RUNTIME_TYPE_MISSING: AtlasTexture runtime type がロードされていません。");
            else if (atlasTypes.Length > 1)
                messages.Add("RUNTIME_TYPE_DUPLICATED: AtlasTexture runtime type が複数ロードされています。");

            if (atlasEditorTypes.Length == 0)
                messages.Add("ATLAS_EDITOR_TYPE_MISSING: AtlasTextureEditor type がロードされていません。");
            else if (atlasEditorTypes.Length > 1)
                messages.Add("ATLAS_EDITOR_TYPE_DUPLICATED: AtlasTextureEditor type が複数ロードされています。");

            if (customEditorTypes.All(t => t.FullName != AtlasTextureEditorTypeName))
                messages.Add("CUSTOM_EDITOR_NOT_REGISTERED: Unity TypeCache に AtlasTextureEditor がありません。");

            if (selected != null)
            {
                var type = selected.GetType();
                var hasBakeName = type.GetField(
                    "BakeName",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                ) != null;

                if (wdtPackage && hasBakeName is false)
                {
                    messages.Add(
                        "WDT_RUNTIME_SCHEMA_NOT_ACTIVE: WDT 0.1.8+ package が登録されているのに、"
                        + "選択中AtlasTexture runtime type に BakeName がありません。"
                    );
                }

                if (selected is MonoBehaviour behaviour)
                {
                    var mono = MonoScript.FromMonoBehaviour(behaviour);
                    var path = mono != null ? AssetDatabase.GetAssetPath(mono) : "";
                    if (wdtPackage
                        && string.IsNullOrEmpty(path) is false
                        && path.StartsWith(
                            "Packages/net.gridatch.tex-trans-tool/",
                            StringComparison.Ordinal
                        ) is false)
                    {
                        messages.Add(
                            "MONOSCRIPT_BINDING_NOT_WDT: 選択中AtlasTextureのMonoScriptはWDT package pathを指していません: "
                            + path
                        );
                    }
                }

                UnityEditor.Editor? editor = null;
                try
                {
                    editor = UnityEditor.Editor.CreateEditor(selected);
                    var editorName = editor?.GetType().FullName ?? "";
                    if (editorName != AtlasTextureEditorTypeName)
                    {
                        messages.Add(
                            "WRONG_EDITOR_RESOLUTION: AtlasTextureに対して解決されたEditorは "
                            + (string.IsNullOrEmpty(editorName) ? "<null>" : editorName)
                            + " です。"
                        );
                    }
                }
                catch (Exception e)
                {
                    messages.Add(
                        "EDITOR_RESOLUTION_EXCEPTION: "
                        + e.GetType().Name
                        + ": "
                        + e.Message
                    );
                }
                finally
                {
                    if (editor != null) UnityEngine.Object.DestroyImmediate(editor);
                }
            }

            if (messages.Count == 0)
                messages.Add("NO_MISMATCH_DETECTED: この診断項目では不整合を検出しませんでした。");

            return messages;
        }

        private static void AppendTypes(
            StringBuilder sb,
            IEnumerable<Type> types,
            bool includeFields)
        {
            var array = types.ToArray();
            if (array.Length == 0)
            {
                sb.AppendLine("  (none)");
                return;
            }

            foreach (var type in array)
            {
                sb.AppendLine("  " + type.AssemblyQualifiedName);
                sb.AppendLine("    " + DescribeAssembly(type.Assembly));

                if (includeFields)
                {
                    var fields = type.GetFields(
                            BindingFlags.Public
                            | BindingFlags.NonPublic
                            | BindingFlags.Instance
                            | BindingFlags.DeclaredOnly
                        )
                        .Select(field => field.Name + ":" + field.FieldType.FullName)
                        .OrderBy(x => x, StringComparer.Ordinal);
                    sb.AppendLine("    Declared fields: " + string.Join(", ", fields));
                }
            }
        }

        private static string DescribeAssembly(Assembly assembly)
        {
            var location = "";
            try { location = assembly.Location; }
            catch { location = "<unavailable>"; }

            var stamp = "";
            try
            {
                if (string.IsNullOrEmpty(location) is false && File.Exists(location))
                    stamp = " | mtime=" + File.GetLastWriteTimeUtc(location).ToString("O");
            }
            catch { }

            return "Assembly="
                + assembly.FullName
                + " | Location="
                + location
                + stamp;
        }

        private static IEnumerable<Type> GetTypesSafe(Assembly assembly)
        {
            try { return assembly.GetTypes(); }
            catch (ReflectionTypeLoadException e)
            {
                return e.Types.Where(t => t != null).Cast<Type>();
            }
            catch { return Array.Empty<Type>(); }
        }

        private static void AppendExpectedSourceFile(
            StringBuilder sb,
            string assetPath,
            string expectedMarker)
        {
            var fullPath = AssetPathToFullPath(assetPath);
            var exists = File.Exists(fullPath);
            var marker = false;
            if (exists)
            {
                try { marker = File.ReadAllText(fullPath).Contains(expectedMarker); }
                catch { }
            }

            sb.AppendLine(
                "[Source] "
                + assetPath
                + " | exists="
                + exists
                + " | contains '"
                + expectedMarker
                + "'="
                + marker
            );
        }

        private static string AssetPathToFullPath(string assetPath)
        {
            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? "";
            return Path.GetFullPath(Path.Combine(projectRoot, assetPath));
        }
    }
}
