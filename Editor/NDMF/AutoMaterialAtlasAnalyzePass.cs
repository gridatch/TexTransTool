#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using net.rs64.TexTransTool.TextureAtlas;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UIElements;

namespace net.rs64.TexTransTool.NDMF
{
    /// <summary>
    /// First milestone of Auto Material Atlas: conservative, read-only analysis.
    /// No mutation is allowed in this pass. It records potential reductions to
    /// NDMF Console; all numbers are estimates, never measured AAO savings.
    /// </summary>
    internal sealed class AutoAtlasReportState
    {
        public bool HasAnalysis;
        public AutoMaterialAtlas? Component;
        public string Summary = "";
        public string Detail = "";
        public string AnimationDiagnostics = "";
        public string CandidateDiagnostics = "";
        public string CrossRendererDiagnostics = "";
        public int BeforeMaterialSlots;
    }

    internal sealed class AutoMaterialAtlasAnalyzePass : Pass<AutoMaterialAtlasAnalyzePass>
    {
        public override string DisplayName => "TTT: Analyze Auto Material Atlas";

        protected override void Execute(BuildContext context)
        {
            var components = context.AvatarRootObject
                .GetComponentsInChildren<AutoMaterialAtlas>(true)
                .Where(component => component != null && component.enabled)
                .ToArray();
            if (components.Length == 0) return;

            if (components.Length != 1)
            {
                TTTLog.ReportingObject(context.AvatarRootObject, () =>
                    TTTLog.Warning("AutoMaterialAtlas:warn:MultipleComponents", components.Length));
                return;
            }

            var root = context.AvatarRootObject;
            var renderers = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            // A global usage map is required: changing a Material referenced by
            // another renderer without moving that renderer's UVs is unsafe.
            var materialUsers = root.GetComponentsInChildren<Renderer>(true)
                .Where(r => r != null)
                .SelectMany(r => r.sharedMaterials
                    .Where(m => m != null)
                    .Select(m => (material: m, renderer: r)))
                .GroupBy(pair => pair.material)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(pair => pair.renderer).Distinct().ToArray()
                );

            var animationIndex = context.Extension<AnimatorServicesContext>().AnimationIndex;
            var animationMaterials = animationIndex == null
                ? new HashSet<Material>()
                : animationIndex.GetPPtrReferencedObjects
                    .OfType<Material>().ToHashSet();

            var groups = new List<GroupCandidate>();
            // Keep a separate cross-renderer view. These groups are *not*
            // considered ready for AAO AutoMergeSkinnedMesh; renderer
            // compatibility, animated property behavior and UVs remain
            // unverified.
            var crossRendererMaterialUsers =
                new Dictionary<Material, HashSet<SkinnedMeshRenderer>>();
            int observedSharedMaterialReferences = 0;
            int observedSharedMaterialRenderers = 0;
            int excludedAnimatedMaterials = 0;
            int sameShaderButSettingsMismatchRenderers = 0;
            int insufficientMaterialRenderers = 0;
            int differentShaderOnlyRenderers = 0;
            var candidateDiagnostics = new List<string>();
            int excludedAnimatedRenderers = 0;
            int observedAnimatedPropertyRenderers = 0;
            var animatedRendererDiagnostics = new List<string>();
            int excludedUnsupportedRenderers = 0;
            int analyzedRenderers = 0;

            foreach (var renderer in renderers)
            {
                if (renderer == null || !AtlasTexture.IsAtlasAllowedRenderer(renderer))
                {
                    excludedUnsupportedRenderers++;
                    continue;
                }

                analyzedRenderers++;

                // AAO's MergeMaterialSlots distinguishes object-reference
                // animation of material slots from animated shader properties.
                // Only slot-reference animation excludes the renderer from this
                // preview. Float material curves are recorded as a compatibility
                // concern; merging them safely is NOT yet proven.
                var (propertyBindings, slotBindings) = GetMaterialAnimationBindings(
                    animationIndex, root.transform, renderer);
                var rendererPath = AnimationUtility.CalculateTransformPath(
                    renderer.transform, root.transform);
                var displayPath = string.IsNullOrEmpty(rendererPath)
                    ? "(avatar root)" : rendererPath;
                if (propertyBindings.Length != 0)
                {
                    observedAnimatedPropertyRenderers++;
                    animatedRendererDiagnostics.Add(
                        displayPath + " [property animation; not excluded]: "
                        + FormatBindings(propertyBindings));
                }
                if (slotBindings.Length != 0)
                {
                    excludedAnimatedRenderers++;
                    animatedRendererDiagnostics.Add(
                        displayPath + " [material slot animation; excluded]: "
                        + FormatBindings(slotBindings));
                    continue;
                }

                // A shared material is unsafe for the current global replacement
                // executor, but sharing alone cannot disqualify it from a
                // READ-ONLY candidate analysis. Track the references and mark
                // those groups as needing isolation before any real mutation.
                var candidates = new List<Material>();
                var sharedMaterialsHere = new HashSet<Material>();
                foreach (var mat in renderer.sharedMaterials.Distinct())
                {
                    if (mat == null) continue;
                    if (materialUsers.TryGetValue(mat, out var users)
                        && users.Length > 1)
                    {
                        observedSharedMaterialReferences++;
                        sharedMaterialsHere.Add(mat);
                    }

                    if (animationMaterials.Contains(mat))
                    {
                        excludedAnimatedMaterials++;
                        continue;
                    }

                    candidates.Add(mat);
                }
                if (sharedMaterialsHere.Count != 0)
                    observedSharedMaterialRenderers++;

                foreach (var mat in candidates)
                {
                    if (!crossRendererMaterialUsers.TryGetValue(mat, out var users))
                    {
                        users = new HashSet<SkinnedMeshRenderer>();
                        crossRendererMaterialUsers.Add(mat, users);
                    }
                    users.Add(renderer);
                }

                // Candidate equivalence: same shader and all non-texture
                // settings. This is NOT yet a UV/texture-density proof.
                var buckets = new List<List<Material>>();
                foreach (var mat in candidates)
                {
                    var bucket = buckets.FirstOrDefault(
                        b => HasSameNonTextureState(b[0], mat));
                    if (bucket == null)
                    {
                        bucket = new List<Material>();
                        buckets.Add(bucket);
                    }
                    bucket.Add(mat);
                }

                var viableBuckets = buckets.Where(b => b.Count > 1).ToArray();
                foreach (var bucket in viableBuckets)
                {
                    var differsInTexture = bucket.Skip(1)
                        .Any(mat => !HasSameTextureReferences(bucket[0], mat));
                    groups.Add(new GroupCandidate(
                        renderer, bucket.ToArray(), differsInTexture,
                        propertyBindings.Length != 0,
                        bucket.Any(sharedMaterialsHere.Contains)));
                }

                // Give a concrete reason for zero groups instead of emitting
                // only an undifferentiated "no candidate groups" message.
                if (viableBuckets.Length == 0)
                {
                    if (candidates.Count < 2)
                    {
                        insufficientMaterialRenderers++;
                    }
                    else
                    {
                        var sameShader = candidates.GroupBy(m => m.shader)
                            .OrderByDescending(g => g.Count())
                            .FirstOrDefault(g => g.Count() > 1);
                        if (sameShader == null)
                        {
                            differentShaderOnlyRenderers++;
                        }
                        else
                        {
                            sameShaderButSettingsMismatchRenderers++;
                            var examples = sameShader.Take(2).ToArray();
                            candidateDiagnostics.Add(displayPath + ": "
                                + examples[0].name + " vs " + examples[1].name
                                + " (" + examples[0].shader.name + ") — "
                                + (DescribeNonTextureMismatch(examples[0], examples[1])
                                    ?? "(no mismatch identified)"));
                        }
                    }
                }
            }

            // Across-renderer opportunities are diagnostic only. Distinct
            // Material references are required: repeating the same Material
            // reference does not require atlasing and AAO already sees it.
            var crossBuckets = new List<List<Material>>();
            foreach (var material in crossRendererMaterialUsers.Keys
                         .OrderBy(m => m.shader.name, StringComparer.Ordinal)
                         .ThenBy(m => m.name, StringComparer.Ordinal))
            {
                var bucket = crossBuckets.FirstOrDefault(
                    b => HasSameNonTextureState(b[0], material));
                if (bucket == null)
                {
                    bucket = new List<Material>();
                    crossBuckets.Add(bucket);
                }
                bucket.Add(material);
            }

            var crossGroups = crossBuckets.Where(bucket =>
                bucket.Count > 1
                && bucket.SelectMany(material => crossRendererMaterialUsers[material])
                    .Distinct().Skip(1).Any()).ToArray();

            const int maxCrossGroups = 20;
            var crossLines = crossGroups.Take(maxCrossGroups).Select((bucket, index) =>
            {
                var userCount = bucket.SelectMany(m => crossRendererMaterialUsers[m])
                    .Distinct().Count();
                var differsInTexture = bucket.Skip(1)
                    .Any(m => !HasSameTextureReferences(bucket[0], m));
                return (index + 1) + ". " + bucket[0].shader.name
                    + "; " + bucket.Count + " distinct Material references"
                    + "; " + userCount + " renderers"
                    + "; " + (differsInTexture ? "textures differ (atlas candidate)"
                                            : "same textures (reference reuse candidate)")
                    + "; examples: "
                    + string.Join(", ", bucket.Take(4).Select(m => m.name))
                    + (bucket.Count > 4 ? " ..." : "");
            }).ToArray();

            var orderedGroups = groups
                .OrderBy(group => AnimationUtility.CalculateTransformPath(
                    group.Renderer.transform, root.transform), StringComparer.Ordinal)
                .ThenBy(group => group.Materials[0].name, StringComparer.Ordinal)
                .ToArray();

            var summary =
                "Renderers: " + analyzedRenderers + "/" + renderers.Length
                + "; candidate groups: " + orderedGroups.Length
                + "; potential slot reductions: "
                + orderedGroups.Sum(group => group.Materials.Length - 1)
                + "; shared-material references (included): " + observedSharedMaterialReferences
                + "; renderers using shared materials: " + observedSharedMaterialRenderers
                + "; animated-material exclusions: " + excludedAnimatedMaterials
                + "; renderers with insufficient distinct materials: " + insufficientMaterialRenderers
                + "; renderers with no shared shader: " + differentShaderOnlyRenderers
                + "; renderers with shader-setting mismatch: " + sameShaderButSettingsMismatchRenderers
                + "; cross-renderer candidate groups (AAO eligibility not checked): " + crossGroups.Length
                + "; material-slot animation renderer exclusions: " + excludedAnimatedRenderers
                + "; animated-property renderers (not excluded): " + observedAnimatedPropertyRenderers
                + "; unsupported renderers: " + excludedUnsupportedRenderers;

            var detail = orderedGroups.Length == 0
                ? "(no candidate groups)"
                : string.Join("\n", orderedGroups.Select((g, i) =>
                    (i + 1) + ". "
                    + AnimationUtility.CalculateTransformPath(g.Renderer.transform, root.transform)
                    + " : [" + string.Join(", ", g.Materials.Select(m => m.name)) + "]"
                    + (g.RequiresAtlas ? " (atlas candidate)" : " (reference reuse candidate)")
                    + (g.HasAnimatedProperties ? " (animated properties; merge compatibility unverified)" : "")
                    + (g.UsesSharedMaterials ? " (shared material; per-renderer isolation required)" : "")
                    + " — theoretical maximum " + (g.Materials.Length - 1) + " fewer slots"));

            // Defer the report until PlatformFinish. AAO has not run yet,
            // and would otherwise make the summary appear to be a final result.
            var report = context.GetState<AutoAtlasReportState>();
            report.HasAnalysis = true;
            report.Component = components[0];
            report.Summary = summary;
            report.Detail = detail;
            report.CrossRendererDiagnostics = crossLines.Length == 0
                ? "(none)"
                : string.Join("\n", crossLines)
                    + (crossGroups.Length > maxCrossGroups
                        ? "\n... " + (crossGroups.Length - maxCrossGroups)
                            + " additional groups omitted" : "");
            const int maxCandidateDiagnostics = 20;
            report.CandidateDiagnostics = candidateDiagnostics.Count == 0
                ? "(none)"
                : string.Join("\n", candidateDiagnostics.Take(maxCandidateDiagnostics))
                    + (candidateDiagnostics.Count > maxCandidateDiagnostics
                        ? "\n... " + (candidateDiagnostics.Count - maxCandidateDiagnostics)
                            + " additional mismatches omitted" : "");
            const int maxDiagnosticRenderers = 12;
            report.AnimationDiagnostics = animatedRendererDiagnostics.Count == 0
                ? "(none)"
                : string.Join("\n", animatedRendererDiagnostics.Take(maxDiagnosticRenderers))
                    + (animatedRendererDiagnostics.Count > maxDiagnosticRenderers
                        ? "\n... " + (animatedRendererDiagnostics.Count - maxDiagnosticRenderers)
                            + " additional renderers omitted" : "");
            report.BeforeMaterialSlots = CountMaterialSlots(root);
        }

        internal static int CountMaterialSlots(GameObject root)
        {
            return root.GetComponentsInChildren<Renderer>(true)
                .Where(r => r != null)
                .Sum(r => r.sharedMaterials.Length);
        }

        private sealed class GroupCandidate
        {
            internal readonly SkinnedMeshRenderer Renderer;
            internal readonly Material[] Materials;
            internal readonly bool RequiresAtlas;
            internal readonly bool HasAnimatedProperties;
            internal readonly bool UsesSharedMaterials;

            internal GroupCandidate(
                SkinnedMeshRenderer renderer,
                Material[] materials,
                bool requiresAtlas,
                bool hasAnimatedProperties,
                bool usesSharedMaterials)
            {
                Renderer = renderer;
                Materials = materials;
                RequiresAtlas = requiresAtlas;
                HasAnimatedProperties = hasAnimatedProperties;
                UsesSharedMaterials = usesSharedMaterials;
            }
        }

        private static string FormatBindings(string[] bindings)
        {
            return string.Join("; ", bindings.Take(3))
                + (bindings.Length > 3
                    ? " (+" + (bindings.Length - 3) + " more)" : "");
        }

        private static (string[] propertyBindings, string[] slotBindings)
            GetMaterialAnimationBindings(
                AnimationIndex? index,
                Transform avatarRoot,
                SkinnedMeshRenderer renderer)
        {
            // Treat an unavailable animation index as unverified rather than
            // mistakenly concluding that this renderer is static.
            if (index == null)
                return (Array.Empty<string>(),
                    new[] { "(animation index unavailable)" });

            var path = AnimationUtility.CalculateTransformPath(
                renderer.transform, avatarRoot);
            var properties = new HashSet<string>(StringComparer.Ordinal);
            var materialSlots = new HashSet<string>(StringComparer.Ordinal);

            foreach (var clip in index.GetClipsForObjectPath(path))
            {
                foreach (var binding in clip.GetFloatCurveBindings())
                    AddBinding(binding, "float");
                foreach (var binding in clip.GetObjectCurveBindings())
                    AddBinding(binding, "object");
            }

            return (
                properties.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                materialSlots.OrderBy(x => x, StringComparer.Ordinal).ToArray()
            );

            void AddBinding(EditorCurveBinding binding, string kind)
            {
                if (!string.Equals(binding.path, path, StringComparison.Ordinal))
                    return;

                if (!typeof(Renderer).IsAssignableFrom(binding.type)
                    && binding.type != typeof(Material))
                    return;

                var label = kind + " " + binding.type.Name + "."
                    + binding.propertyName;

                if (binding.propertyName.StartsWith("m_Materials.", StringComparison.Ordinal))
                    materialSlots.Add(label);
                else if (binding.propertyName.StartsWith("material.", StringComparison.Ordinal))
                    properties.Add(label);
            }
        }

        private static bool HasSameTextureReferences(Material left, Material right)
        {
            foreach (var property in left.GetTexturePropertyNames())
            {
                if (left.GetTexture(property) != right.GetTexture(property))
                    return false;
                if (left.GetTextureOffset(property) != right.GetTextureOffset(property))
                    return false;
                if (left.GetTextureScale(property) != right.GetTextureScale(property))
                    return false;
            }
            return true;
        }

        private static bool HasSameNonTextureState(Material left, Material right)
            => DescribeNonTextureMismatch(left, right) == null;

        // Return the first mismatch in the same order as the compatibility
        // predicate. This is diagnostic, not a shader-compatibility guarantee.
        private static string? DescribeNonTextureMismatch(Material left, Material right)
        {
            if (left == null || right == null) return "missing material";
            if (left.shader != right.shader) return "different shader";
            if (left.renderQueue != right.renderQueue) return "renderQueue";
            if (left.enableInstancing != right.enableInstancing) return "enableInstancing";
            if (left.doubleSidedGI != right.doubleSidedGI) return "doubleSidedGI";
            if (left.globalIlluminationFlags != right.globalIlluminationFlags)
                return "globalIlluminationFlags";

            if (!left.shaderKeywords.ToHashSet(StringComparer.Ordinal)
                .SetEquals(right.shaderKeywords))
                return "shaderKeywords";

            foreach (var tag in new[] { "RenderType", "Queue", "RenderPipeline",
                         "IgnoreProjector", "DisableBatching", "ForceNoShadowCasting" })
            {
                if (left.GetTag(tag, false, "") != right.GetTag(tag, false, ""))
                    return "shader tag " + tag;
            }

            var shader = left.shader;
            for (int i = 0; i < shader.GetPropertyCount(); i++)
            {
                var property = shader.GetPropertyName(i);
                switch (shader.GetPropertyType(i))
                {
                    case ShaderPropertyType.Color:
                        if (left.GetColor(property) != right.GetColor(property))
                            return property + " (color)";
                        break;
                    case ShaderPropertyType.Vector:
                        if (left.GetVector(property) != right.GetVector(property))
                            return property + " (vector)";
                        break;
                    case ShaderPropertyType.Float:
                    case ShaderPropertyType.Range:
                        if (left.GetFloat(property) != right.GetFloat(property))
                            return property + " (float/range)";
                        break;
                    case ShaderPropertyType.Int:
                        if (left.GetInt(property) != right.GetInt(property))
                            return property + " (int)";
                        break;
                    case ShaderPropertyType.Texture:
                        break;
                    default:
                        return property + " (unsupported property type)";
                }
            }
            return null;
        }
    }

    /// <summary>
    /// Renders the normal localized NDMF report, with an additional button
    /// to copy its complete contents into a bug report or chat message.
    /// </summary>
    internal sealed class AutoMaterialAtlasConsoleReport : SimpleError
    {
        private readonly string[] _details;

        internal AutoMaterialAtlasConsoleReport(
            string summary, string detail, string observed,
            string animationDiagnostics, string candidateDiagnostics,
            string crossRendererDiagnostics)
        {
            _details = new[] {
                summary, detail, observed, animationDiagnostics,
                candidateDiagnostics, crossRendererDiagnostics
            };
        }

        public override nadena.dev.ndmf.localization.Localizer Localizer => TTTLog.NDMFLocalizer;
        public override ErrorSeverity Severity => ErrorSeverity.Information;
        public override string TitleKey => "AutoMaterialAtlas:info:AnalysisOnly";
        public override string[] DetailsSubst => _details;

        public override VisualElement CreateVisualElement(ErrorReport report)
        {
            var element = base.CreateVisualElement(report);
            var copyButton = new Button(() =>
            {
                EditorGUIUtility.systemCopyBuffer = ToMessage();
            });

            copyButton.text = Localizer.GetLocalizedString(
                "AutoMaterialAtlas:action:CopyResult");
            element.Add(copyButton);
            return element;
        }
    }

    /// <summary>
    /// Runs after AAO. The before/after difference includes all AAO
    /// optimizations, not savings attributable to Auto Material Atlas.
    /// </summary>
    internal sealed class AutoMaterialAtlasReportPass : Pass<AutoMaterialAtlasReportPass>
    {
        public override string DisplayName => "TTT: Auto Material Atlas Report";

        protected override void Execute(BuildContext context)
        {
            var report = context.GetState<AutoAtlasReportState>();
            if (!report.HasAnalysis) return;

            var after = AutoMaterialAtlasAnalyzePass.CountMaterialSlots(
                context.AvatarRootObject);
            var observed = report.BeforeMaterialSlots + " -> " + after;

            TTTLog.ReportingObject(report.Component != null
                ? report.Component : context.AvatarRootObject,
                () => ErrorReport.ReportError(new AutoMaterialAtlasConsoleReport(
                    report.Summary,
                    report.Detail,
                    observed,
                    report.AnimationDiagnostics,
                    report.CandidateDiagnostics,
                    report.CrossRendererDiagnostics)));
        }
    }
}
