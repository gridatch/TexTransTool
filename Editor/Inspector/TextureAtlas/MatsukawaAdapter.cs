#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace net.rs64.TexTransTool.TextureAtlas.Editor
{
    internal enum MatsukawaBoneMode
    {
        WeightedOnly,
        MeshReferenced,
    }

    internal sealed class MatsukawaOptions
    {
        internal bool WorkOnCopy = true;
        internal MatsukawaBoneMode BoneMode = MatsukawaBoneMode.WeightedOnly;
        internal float WeightThreshold = 0.0001f;
        internal bool KeepPhysBoneChains = true;
        internal bool KeepReferencedObjects = true;
        internal bool KeepHumanoidBones = false;
        internal bool ProtectOtherComponents = true;
        internal bool StripAvatarComponents = false;
        internal readonly List<string> ForceDeletePaths = new();
    }

    internal sealed class MatsukawaRendererEntry
    {
        internal Renderer Renderer = null!;
        internal string Path = "";
        internal string Category = "";
        internal bool Keep;
        internal bool IsSkinned;
        internal bool MeshReadable;
        internal int VertexCount;
        internal int BoneCount;

        internal object Raw = null!;
    }

    internal sealed class MatsukawaAnalysis
    {
        internal int TotalTransforms;
        internal int DeleteTransformCount;
        internal int DeleteRendererCount;
        internal int KeepBoneCount;
        internal int KeepCount;
        internal int DeleteRootCount;
        internal int ProtectedObjectCount;
        internal int EssentialCount;
        internal int TrimTargetCount;
        internal int WarningCount;
        internal IReadOnlyList<string> Warnings = Array.Empty<string>();

        internal object Raw = null!;
    }

    internal sealed class MatsukawaExecutionResult
    {
        internal bool Succeeded;
        internal GameObject? Result;
        internal string SourceName = "";
        internal string OutputFolder = "";
        internal string PrefabPath = "";
        internal string ReportText = "";
        internal IReadOnlyList<string> GeneratedMeshes = Array.Empty<string>();
        internal IReadOnlyList<string> Notes = Array.Empty<string>();

        internal object RawReport = null!;
    }

    /// <summary>
    /// Reflection-only adapter for HairCostumeExtractor Ver1.1.0's public API.
    /// TexTransTool intentionally has no compile-time reference to the separately
    /// distributed Matsukawa tool.
    /// </summary>
    internal sealed class MatsukawaAdapter
    {
        private readonly Api _api;

        private MatsukawaAdapter(Api api)
        {
            _api = api;
        }

        internal static bool TryCreate(out MatsukawaAdapter? adapter, out string error)
        {
            try
            {
                adapter = new MatsukawaAdapter(Api.Resolve());
                error = "";
                return true;
            }
            catch (Exception e)
            {
                adapter = null;
                error = Unwrap(e).Message;
                return false;
            }
        }

        internal IReadOnlyList<MatsukawaRendererEntry> CollectRenderers(GameObject root)
        {
            var rawList = _api.CollectRenderers.Invoke(null, new object[] { root });
            if (rawList is not IList list)
                throw new InvalidOperationException("HairCostumeExtractor.HceCore.CollectRenderers returned an unexpected value.");

            var result = new List<MatsukawaRendererEntry>(list.Count);
            foreach (var raw in list)
            {
                if (raw == null) continue;
                result.Add(new MatsukawaRendererEntry
                {
                    Raw = raw,
                    Renderer = (Renderer?)_api.EntryRenderer.GetValue(raw)
                        ?? throw new InvalidOperationException("HceRendererEntry.renderer is null."),
                    Path = (string?)_api.EntryPath.GetValue(raw) ?? "",
                    Category = _api.EntryGuess.GetValue(raw)?.ToString() ?? "",
                    Keep = (bool)(_api.EntryKeep.GetValue(raw) ?? false),
                    IsSkinned = (bool)(_api.EntryIsSkinned.GetValue(raw) ?? false),
                    MeshReadable = (bool)(_api.EntryMeshReadable.GetValue(raw) ?? false),
                    VertexCount = (int)(_api.EntryVertexCount.GetValue(raw) ?? 0),
                    BoneCount = (int)(_api.EntryBoneCount.GetValue(raw) ?? 0),
                });
            }

            return result;
        }

        internal MatsukawaAnalysis Analyze(
            GameObject root,
            IReadOnlyList<MatsukawaRendererEntry> entries,
            MatsukawaOptions options)
        {
            var rawEntries = BuildTypedEntryList(entries);
            var rawOptions = BuildOptions(options);

            var rawAnalysis = _api.Analyze.Invoke(null, new[] { (object)root, rawEntries, rawOptions })
                ?? throw new InvalidOperationException("HairCostumeExtractor.HceCore.Analyze returned null.");

            return ReadAnalysis(rawAnalysis);
        }

        internal void SetHierarchyHighlight(MatsukawaAnalysis analysis)
        {
            _api.HighlighterSet.Invoke(null, new[] { analysis.Raw });
        }

        internal void ClearHierarchyHighlight()
        {
            _api.HighlighterClear.Invoke(null, null);
        }

        internal bool HierarchyHighlightEnabled
        {
            get => (bool)(_api.HighlighterEnabled.GetValue(null) ?? false);
            set => _api.HighlighterEnabled.SetValue(null, value);
        }

        internal MatsukawaExecutionResult Execute(
            GameObject root,
            IReadOnlyList<MatsukawaRendererEntry> entries,
            MatsukawaOptions options)
        {
            var rawEntries = BuildTypedEntryList(entries);
            var rawOptions = BuildOptions(options);

            var rawReport = _api.Execute.Invoke(null, new[] { (object)root, rawEntries, rawOptions })
                ?? throw new InvalidOperationException("HairCostumeExtractor.HceExecutor.Execute returned null.");

            return ReadExecutionResult(rawReport);
        }

        internal string? SavePrefab(MatsukawaExecutionResult result)
        {
            var value = _api.SavePrefab.Invoke(null, new[] { result.RawReport });
            result.PrefabPath = value as string ?? "";
            return value as string;
        }

        internal string? ExportUnityPackage(MatsukawaExecutionResult result, string destination)
        {
            return _api.ExportUnityPackage.Invoke(null, new object[] { result.RawReport, destination }) as string;
        }

        internal string BuildReportText(MatsukawaExecutionResult result)
        {
            return _api.BuildReportText.Invoke(null, new[] { result.RawReport }) as string ?? "";
        }

        internal void AddReportNote(MatsukawaExecutionResult result, string note)
        {
            var notes = _api.ReportNotes.GetValue(result.RawReport) as IList
                ?? throw new InvalidOperationException("HceReport.notes is not IList.");
            notes.Add(note);
            result.Notes = ReadStringList(notes);
        }

        internal void SetReportText(MatsukawaExecutionResult result, string reportText)
        {
            _api.ReportReportText.SetValue(result.RawReport, reportText);
            result.ReportText = reportText;
        }

        private object BuildTypedEntryList(IReadOnlyList<MatsukawaRendererEntry> entries)
        {
            var listType = typeof(List<>).MakeGenericType(_api.Entry);
            var rawList = (IList)(Activator.CreateInstance(listType)
                ?? throw new InvalidOperationException("Failed to create Matsukawa renderer entry list."));

            foreach (var entry in entries)
            {
                _api.EntryKeep.SetValue(entry.Raw, entry.Keep);
                rawList.Add(entry.Raw);
            }

            return rawList;
        }

        private object BuildOptions(MatsukawaOptions options)
        {
            var raw = Activator.CreateInstance(_api.Options)
                ?? throw new InvalidOperationException("Failed to create HairCostumeExtractor.HceOptions.");

            _api.OptWorkOnCopy.SetValue(raw, options.WorkOnCopy);
            _api.OptBoneMode.SetValue(
                raw,
                Enum.Parse(
                    _api.BoneMode,
                    options.BoneMode == MatsukawaBoneMode.WeightedOnly
                        ? "WeightedOnly"
                        : "MeshReferenced"
                )
            );
            _api.OptThreshold.SetValue(raw, options.WeightThreshold);
            _api.OptKeepPhysBoneChains.SetValue(raw, options.KeepPhysBoneChains);
            _api.OptKeepReferencedObjects.SetValue(raw, options.KeepReferencedObjects);
            _api.OptKeepHumanoidBones.SetValue(raw, options.KeepHumanoidBones);
            _api.OptProtectOtherComponents.SetValue(raw, options.ProtectOtherComponents);
            _api.OptStripAvatarComponents.SetValue(raw, options.StripAvatarComponents);

            if (_api.OptForceDeletePaths.GetValue(raw) is IList forceDelete)
            {
                forceDelete.Clear();
                foreach (var path in options.ForceDeletePaths) forceDelete.Add(path);
            }

            return raw;
        }

        private MatsukawaAnalysis ReadAnalysis(object raw)
        {
            return new MatsukawaAnalysis
            {
                Raw = raw,
                TotalTransforms = ReadInt(_api.AnalysisTotalTransforms, raw),
                DeleteTransformCount = ReadInt(_api.AnalysisDeleteTransformCount, raw),
                DeleteRendererCount = ReadInt(_api.AnalysisDeleteRendererCount, raw),
                KeepBoneCount = ReadInt(_api.AnalysisKeepBoneCount, raw),
                KeepCount = Count(_api.AnalysisKeep.GetValue(raw)),
                DeleteRootCount = Count(_api.AnalysisDeleteRoots.GetValue(raw)),
                ProtectedObjectCount = Count(_api.AnalysisProtected.GetValue(raw)),
                EssentialCount = Count(_api.AnalysisEssential.GetValue(raw)),
                TrimTargetCount = Count(_api.AnalysisTrimTargets.GetValue(raw)),
                WarningCount = Count(_api.AnalysisWarnings.GetValue(raw)),
                Warnings = ReadStringList(_api.AnalysisWarnings.GetValue(raw)),
            };
        }

        private MatsukawaExecutionResult ReadExecutionResult(object rawReport)
        {
            return new MatsukawaExecutionResult
            {
                RawReport = rawReport,
                Succeeded = (bool)(_api.ReportSucceeded.GetValue(rawReport) ?? false),
                Result = _api.ReportResult.GetValue(rawReport) as GameObject,
                SourceName = (string?)_api.ReportSourceName.GetValue(rawReport) ?? "",
                OutputFolder = (string?)_api.ReportOutputFolder.GetValue(rawReport) ?? "",
                PrefabPath = (string?)_api.ReportPrefabPath.GetValue(rawReport) ?? "",
                ReportText = (string?)_api.ReportReportText.GetValue(rawReport) ?? "",
                GeneratedMeshes = ReadStringList(_api.ReportGeneratedMeshes.GetValue(rawReport)),
                Notes = ReadStringList(_api.ReportNotes.GetValue(rawReport)),
            };
        }

        private static int ReadInt(FieldInfo field, object instance)
        {
            return (int)(field.GetValue(instance) ?? 0);
        }

        private static int Count(object? value)
        {
            if (value == null) return 0;
            if (value is ICollection collection) return collection.Count;

            var property = value.GetType().GetProperty("Count", BindingFlags.Public | BindingFlags.Instance);
            if (property?.PropertyType == typeof(int))
                return (int)(property.GetValue(value) ?? 0);

            if (value is IEnumerable enumerable)
            {
                var count = 0;
                foreach (var _ in enumerable) count++;
                return count;
            }

            return -1;
        }

        private static IReadOnlyList<string> ReadStringList(object? value)
        {
            if (value is not IEnumerable enumerable) return Array.Empty<string>();
            var list = new List<string>();
            foreach (var item in enumerable)
            {
                if (item is string text) list.Add(text);
            }
            return list;
        }

        private static Exception Unwrap(Exception e)
        {
            while (e is TargetInvocationException invocation && invocation.InnerException != null)
                e = invocation.InnerException;
            return e;
        }

        private sealed class Api
        {
            internal Type Core = null!;
            internal Type Options = null!;
            internal Type Entry = null!;
            internal Type Analysis = null!;
            internal Type BoneMode = null!;
            internal Type Highlighter = null!;
            internal Type Executor = null!;
            internal Type Report = null!;

            internal MethodInfo CollectRenderers = null!;
            internal MethodInfo Analyze = null!;
            internal MethodInfo HighlighterSet = null!;
            internal MethodInfo HighlighterClear = null!;
            internal MethodInfo Execute = null!;
            internal MethodInfo SavePrefab = null!;
            internal MethodInfo ExportUnityPackage = null!;
            internal MethodInfo BuildReportText = null!;

            internal PropertyInfo HighlighterEnabled = null!;

            internal FieldInfo EntryRenderer = null!;
            internal FieldInfo EntryPath = null!;
            internal FieldInfo EntryGuess = null!;
            internal FieldInfo EntryKeep = null!;
            internal FieldInfo EntryIsSkinned = null!;
            internal FieldInfo EntryMeshReadable = null!;
            internal FieldInfo EntryVertexCount = null!;
            internal FieldInfo EntryBoneCount = null!;

            internal FieldInfo OptWorkOnCopy = null!;
            internal FieldInfo OptBoneMode = null!;
            internal FieldInfo OptThreshold = null!;
            internal FieldInfo OptKeepPhysBoneChains = null!;
            internal FieldInfo OptKeepReferencedObjects = null!;
            internal FieldInfo OptKeepHumanoidBones = null!;
            internal FieldInfo OptProtectOtherComponents = null!;
            internal FieldInfo OptStripAvatarComponents = null!;
            internal FieldInfo OptForceDeletePaths = null!;

            internal FieldInfo AnalysisKeep = null!;
            internal FieldInfo AnalysisDeleteRoots = null!;
            internal FieldInfo AnalysisProtected = null!;
            internal FieldInfo AnalysisEssential = null!;
            internal FieldInfo AnalysisTrimTargets = null!;
            internal FieldInfo AnalysisWarnings = null!;
            internal FieldInfo AnalysisTotalTransforms = null!;
            internal FieldInfo AnalysisDeleteTransformCount = null!;
            internal FieldInfo AnalysisDeleteRendererCount = null!;
            internal FieldInfo AnalysisKeepBoneCount = null!;

            internal FieldInfo ReportResult = null!;
            internal FieldInfo ReportSourceName = null!;
            internal FieldInfo ReportGeneratedMeshes = null!;
            internal FieldInfo ReportNotes = null!;
            internal FieldInfo ReportOutputFolder = null!;
            internal FieldInfo ReportPrefabPath = null!;
            internal FieldInfo ReportSucceeded = null!;
            internal FieldInfo ReportReportText = null!;

            internal static Api Resolve()
            {
                var api = new Api
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

                RequireEnumValue(api.BoneMode, "WeightedOnly");
                RequireEnumValue(api.BoneMode, "MeshReferenced");

                api.CollectRenderers = Method(api.Core, "CollectRenderers", typeof(GameObject));
                RequireReturn(api.CollectRenderers, type => IsListOf(type, api.Entry), "List<HceRendererEntry>");

                api.Analyze = Method(
                    api.Core,
                    "Analyze",
                    typeof(GameObject),
                    typeof(IList<>).MakeGenericType(api.Entry),
                    api.Options
                );
                RequireReturn(api.Analyze, type => type == api.Analysis, "HceAnalysis");

                api.HighlighterSet = Method(api.Highlighter, "Set", api.Analysis);
                api.HighlighterClear = Method(api.Highlighter, "Clear");
                api.HighlighterEnabled = Property(api.Highlighter, "Enabled", typeof(bool), isStatic: true);

                api.Execute = Method(
                    api.Executor,
                    "Execute",
                    typeof(GameObject),
                    typeof(IList<>).MakeGenericType(api.Entry),
                    api.Options
                );
                RequireReturn(api.Execute, type => type == api.Report, "HceReport");

                api.SavePrefab = Method(api.Executor, "SavePrefab", api.Report);
                RequireReturn(api.SavePrefab, type => type == typeof(string), "string");

                api.ExportUnityPackage = Method(api.Executor, "ExportUnityPackage", api.Report, typeof(string));
                RequireReturn(api.ExportUnityPackage, type => type == typeof(string), "string");

                api.BuildReportText = Method(api.Executor, "BuildReportText", api.Report);
                RequireReturn(api.BuildReportText, type => type == typeof(string), "string");

                api.EntryRenderer = Field(api.Entry, "renderer", typeof(Renderer));
                api.EntryPath = Field(api.Entry, "path", typeof(string));
                api.EntryGuess = Field(api.Entry, "guess", null);
                api.EntryKeep = Field(api.Entry, "keep", typeof(bool));
                api.EntryIsSkinned = Field(api.Entry, "isSkinned", typeof(bool));
                api.EntryMeshReadable = Field(api.Entry, "meshReadable", typeof(bool));
                api.EntryVertexCount = Field(api.Entry, "vertexCount", typeof(int));
                api.EntryBoneCount = Field(api.Entry, "boneCount", typeof(int));

                api.OptWorkOnCopy = Field(api.Options, "workOnCopy", typeof(bool));
                api.OptBoneMode = Field(api.Options, "boneMode", api.BoneMode);
                api.OptThreshold = Field(api.Options, "weightThreshold", typeof(float));
                api.OptKeepPhysBoneChains = Field(api.Options, "keepPhysBoneChains", typeof(bool));
                api.OptKeepReferencedObjects = Field(api.Options, "keepReferencedObjects", typeof(bool));
                api.OptKeepHumanoidBones = Field(api.Options, "keepHumanoidBones", typeof(bool));
                api.OptProtectOtherComponents = Field(api.Options, "protectOtherComponents", typeof(bool));
                api.OptStripAvatarComponents = Field(api.Options, "stripAvatarComponents", typeof(bool));
                api.OptForceDeletePaths = Field(api.Options, "forceDeletePaths", null);

                api.AnalysisKeep = Field(api.Analysis, "keep", null);
                api.AnalysisDeleteRoots = Field(api.Analysis, "deleteRoots", null);
                api.AnalysisProtected = Field(api.Analysis, "protectedObjects", null);
                api.AnalysisEssential = Field(api.Analysis, "essential", null);
                api.AnalysisTrimTargets = Field(api.Analysis, "trimTargets", null);
                api.AnalysisWarnings = Field(api.Analysis, "warnings", null);
                api.AnalysisTotalTransforms = Field(api.Analysis, "totalTransforms", typeof(int));
                api.AnalysisDeleteTransformCount = Field(api.Analysis, "deleteTransformCount", typeof(int));
                api.AnalysisDeleteRendererCount = Field(api.Analysis, "deleteRendererCount", typeof(int));
                api.AnalysisKeepBoneCount = Field(api.Analysis, "keepBoneCount", typeof(int));

                api.ReportResult = Field(api.Report, "result", typeof(GameObject));
                api.ReportSourceName = Field(api.Report, "sourceName", typeof(string));
                api.ReportGeneratedMeshes = Field(api.Report, "generatedMeshes", null);
                api.ReportNotes = Field(api.Report, "notes", null);
                api.ReportOutputFolder = Field(api.Report, "outputFolder", typeof(string));
                api.ReportPrefabPath = Field(api.Report, "prefabPath", typeof(string));
                api.ReportSucceeded = Field(api.Report, "succeeded", typeof(bool));
                api.ReportReportText = Field(api.Report, "ReportText", typeof(string));

                return api;
            }

            private static Type TypeOf(string fullName)
            {
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var type = assembly.GetType(fullName, throwOnError: false);
                    if (type != null) return type;
                }

                throw new TypeLoadException(fullName);
            }

            private static MethodInfo Method(Type type, string name, params Type[] parameterTypes)
            {
                return type.GetMethod(
                           name,
                           BindingFlags.Public | BindingFlags.Static,
                           binder: null,
                           types: parameterTypes,
                           modifiers: null
                       )
                       ?? throw new MissingMethodException(type.FullName, name);
            }

            private static FieldInfo Field(Type type, string name, Type? expectedType)
            {
                var field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance)
                    ?? throw new MissingFieldException(type.FullName, name);

                if (expectedType != null && field.FieldType != expectedType)
                    throw new InvalidOperationException(
                        $"{type.FullName}.{name} type mismatch: {field.FieldType.FullName}"
                    );

                return field;
            }

            private static PropertyInfo Property(Type type, string name, Type expectedType, bool isStatic)
            {
                var property = type.GetProperty(
                    name,
                    BindingFlags.Public | (isStatic ? BindingFlags.Static : BindingFlags.Instance)
                ) ?? throw new MissingMemberException(type.FullName, name);

                if (property.PropertyType != expectedType || property.CanRead is false || property.CanWrite is false)
                    throw new InvalidOperationException($"{type.FullName}.{name} property contract mismatch.");

                return property;
            }

            private static void RequireEnumValue(Type type, string name)
            {
                if (type.IsEnum is false || Enum.GetNames(type).Contains(name) is false)
                    throw new MissingMemberException(type.FullName, name);
            }

            private static void RequireReturn(MethodInfo method, Func<Type, bool> predicate, string expected)
            {
                if (predicate(method.ReturnType) is false)
                    throw new InvalidOperationException(
                        $"{method.DeclaringType?.FullName}.{method.Name} return type is not {expected}."
                    );
            }

            private static bool IsListOf(Type type, Type elementType)
            {
                return type.IsGenericType
                    && type.GetGenericTypeDefinition() == typeof(List<>)
                    && type.GetGenericArguments()[0] == elementType;
            }
        }
    }
}
