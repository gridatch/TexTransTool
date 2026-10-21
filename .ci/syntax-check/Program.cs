using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

internal static class Program
{
    // Unity 2022.3 uses C# 9. This is a syntax-only check, not a Unity build.
    private static readonly CSharpParseOptions BaseOptions =
        CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.CSharp9);

    private static readonly string[] SourceDirectories =
    {
        "Runtime",
        "Editor",
        "TestCode",
        "TTCE-Unity",
        "TTT-PSDImporter",
        "MultiLayerImageData",
        "ParserUtility",
    };

    // Each profile represents a possible project configuration. Running both
    // editor variants catches syntax hidden behind common #if directives.
    private static readonly string[] UnitySymbols =
    {
        "UNITY_2022_3", "UNITY_2022_3_OR_NEWER",
        "UNITY_2022_2_OR_NEWER", "UNITY_2021_3_OR_NEWER",
        "UNITY_2021_2_OR_NEWER", "UNITY_2020_3_OR_NEWER"
    };

    private static readonly string[] IntegrationSymbols =
    {
        "VRC_BASE", "VRC_AVATAR", "VRC_SDK_VRCSDK3",
        "CONTAINS_NDMF", "NDMF", "NDMF_ERROR_REPORT",
        "NDMF_DEPEND_VERSION", "NDMF_1_8_0_OR_NEWER",
        "CONTAINS_AAO", "CONTAINS_MA", "CONTAINS_LNU",
        "CONTAINS_TTCE_WGPU"
    };

    private static IEnumerable<(string Name, CSharpParseOptions Options)> Profiles()
    {
        yield return ("runtime", BaseOptions.WithPreprocessorSymbols(UnitySymbols));

        var editor = UnitySymbols.Concat(new[] { "UNITY_EDITOR", "UNITY_INCLUDE_TESTS" }).ToArray();
        yield return ("editor-base", BaseOptions.WithPreprocessorSymbols(editor));
        yield return ("editor-integrations", BaseOptions.WithPreprocessorSymbols(editor.Concat(IntegrationSymbols)));

        // Check conditional fallback branches for each optional integration too.
        foreach (string symbol in IntegrationSymbols)
        {
            string[] minusOne = IntegrationSymbols.Where(s => s != symbol).ToArray();
            yield return ("editor-without-" + symbol,
                BaseOptions.WithPreprocessorSymbols(editor.Concat(minusOne)));
        }
    }

    private static int Main(string[] args)
    {
        if (args.Length > 1)
        {
            Console.Error.WriteLine("Usage: dotnet run --project .ci/syntax-check/CSharpSyntaxCheck.csproj -- [repository root]");
            return 2;
        }

        // Confirm the parser genuinely detects the regression class that
        // motivated this CI: CS1002 (missing semicolon).
        var invalid = CSharpSyntaxTree.ParseText(
            "class SyntaxSentinel { void M() { int value = 1 } }", BaseOptions);
        if (!invalid.GetDiagnostics().Any(d => d.Id == "CS1002" && d.Severity == DiagnosticSeverity.Error))
        {
            Console.Error.WriteLine("Self-test failed: Roslyn did not report CS1002.");
            return 2;
        }

        string root = Path.GetFullPath(args.Length == 1 ? args[0] : ".");
        var files = SourceDirectories
            .Select(directory => Path.Combine(root, directory))
            .Where(Directory.Exists)
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        if (files.Length == 0)
        {
            Console.Error.WriteLine("No package C# files found. Refusing to report success.");
            return 2;
        }

        int errors = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var profiles = Profiles().ToArray();
        foreach (string file in files)
        {
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            string text = File.ReadAllText(file);
            foreach (var profile in profiles)
            {
                var tree = CSharpSyntaxTree.ParseText(text, profile.Options, path: relative);
                foreach (var diagnostic in tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error))
                {
                    var span = diagnostic.Location.GetLineSpan().StartLinePosition;
                    string key = relative + "|" + span.Line + "|" + span.Character
                        + "|" + diagnostic.Id + "|" + diagnostic.GetMessage();
                    // Avoid flooding logs when the same fault occurs in multiple profiles.
                    if (!seen.Add(key)) continue;
                    errors++;
                    Console.Error.WriteLine(
                        $"{relative}({span.Line + 1},{span.Character + 1}): error {diagnostic.Id}: " +
                        $"{diagnostic.GetMessage()} [profile: {profile.Name}]");
                }
            }
        }

        Console.WriteLine($"Roslyn C# 9 syntax check: {files.Length} files, {profiles.Length} profiles, {errors} distinct errors.");
        return errors == 0 ? 0 : 1;
    }
}
