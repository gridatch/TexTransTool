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
            int excludedSharedMaterials = 0;
            int excludedAnimatedMaterials = 0;
            int excludedAnimatedRenderers = 0;
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

                // Reject renderers with animated shader parameters or material
                // slots. A reference-only scan is insufficient for these curves.
                if (HasMaterialAnimations(animationIndex, root.transform, renderer))
                {
                    excludedAnimatedRenderers++;
                    continue;
                }

                var candidates = new List<Material>();
                foreach (var mat in renderer.sharedMaterials.Distinct())
                {
                    if (mat == null) continue;
                    if (!materialUsers.TryGetValue(mat, out var users)
                        || users.Length != 1 || users[0] != renderer)
                    {
                        excludedSharedMaterials++;
                        continue;
                    }

                    if (animationMaterials.Contains(mat))
                    {
                        excludedAnimatedMaterials++;
                        continue;
                    }

                    candidates.Add(mat);
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

                foreach (var bucket in buckets.Where(b => b.Count > 1))
                {
                    var differsInTexture = bucket.Skip(1)
                        .Any(mat => !HasSameTextureReferences(bucket[0], mat));
                    groups.Add(new GroupCandidate(
                        renderer, bucket.ToArray(), differsInTexture));
                }
            }

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
                + "; shared-material exclusions: " + excludedSharedMaterials
                + "; animated-material exclusions: " + excludedAnimatedMaterials
                + "; animated-renderer exclusions: " + excludedAnimatedRenderers
                + "; unsupported renderers: " + excludedUnsupportedRenderers;

            var detail = orderedGroups.Length == 0
                ? "(no candidate groups)"
                : string.Join("\n", orderedGroups.Select((g, i) =>
                    (i + 1) + ". "
                    + AnimationUtility.CalculateTransformPath(g.Renderer.transform, root.transform)
                    + " : [" + string.Join(", ", g.Materials.Select(m => m.name)) + "]"
                    + (g.RequiresAtlas ? " (atlas candidate)" : " (reference reuse candidate)")
                    + " — predicted " + (g.Materials.Length - 1) + " fewer slots"));

            // Defer the report until PlatformFinish. AAO has not run yet,
            // and would otherwise make the summary appear to be a final result.
            var report = context.GetState<AutoAtlasReportState>();
            report.HasAnalysis = true;
            report.Component = components[0];
            report.Summary = summary;
            report.Detail = detail;
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

            internal GroupCandidate(
                SkinnedMeshRenderer renderer,
                Material[] materials,
                bool requiresAtlas)
            {
                Renderer = renderer;
                Materials = materials;
                RequiresAtlas = requiresAtlas;
            }
        }

        private static bool HasMaterialAnimations(
            AnimationIndex? index,
            Transform avatarRoot,
            SkinnedMeshRenderer renderer)
        {
            // Lack of an animation index must not be treated as proof that a
            // renderer is static. This pass is strictly observational.
            if (index == null) return true;

            var path = AnimationUtility.CalculateTransformPath(
                renderer.transform, avatarRoot);
            foreach (var clip in index.GetClipsForObjectPath(path))
            {
                foreach (var binding in clip.GetFloatCurveBindings())
                {
                    if (IsMaterialBinding(binding.type, binding.propertyName))
                        return true;
                }
                foreach (var binding in clip.GetObjectCurveBindings())
                {
                    if (IsMaterialBinding(binding.type, binding.propertyName))
                        return true;
                }
            }
            return false;
        }

        private static bool IsMaterialBinding(Type type, string propertyName)
        {
            return (typeof(Renderer).IsAssignableFrom(type)
                    || type == typeof(Material))
                && (propertyName.StartsWith("material.", StringComparison.Ordinal)
                    || propertyName.StartsWith("m_Materials.", StringComparison.Ordinal));
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
        {
            if (left == null || right == null || left.shader != right.shader)
                return false;

            if (left.renderQueue != right.renderQueue
                || left.enableInstancing != right.enableInstancing
                || left.doubleSidedGI != right.doubleSidedGI
                || left.globalIlluminationFlags != right.globalIlluminationFlags)
                return false;

            if (!left.shaderKeywords.ToHashSet(StringComparer.Ordinal)
                .SetEquals(right.shaderKeywords))
                return false;

            // Compare commonly overridden render-state tags as well as the
            // shader's exposed properties. The full compatibility checker
            // for automated modification will be stricter than this preview.
            foreach (var tag in new[] { "RenderType", "Queue", "RenderPipeline",
                         "IgnoreProjector", "DisableBatching", "ForceNoShadowCasting" })
            {
                if (left.GetTag(tag, false, "") != right.GetTag(tag, false, ""))
                    return false;
            }

            var shader = left.shader;
            for (int i = 0; i < shader.GetPropertyCount(); i++)
            {
                var property = shader.GetPropertyName(i);
                switch (shader.GetPropertyType(i))
                {
                    case ShaderPropertyType.Color:
                        if (left.GetColor(property) != right.GetColor(property)) return false;
                        break;
                    case ShaderPropertyType.Vector:
                        if (left.GetVector(property) != right.GetVector(property)) return false;
                        break;
                    case ShaderPropertyType.Float:
                    case ShaderPropertyType.Range:
                        if (left.GetFloat(property) != right.GetFloat(property)) return false;
                        break;
                    case ShaderPropertyType.Int:
                        if (left.GetInt(property) != right.GetInt(property)) return false;
                        break;
                    case ShaderPropertyType.Texture:
                        break;
                    default:
                        return false;
                }
            }
            return true;
        }
    }

    /// <summary>
    /// Renders the normal localized NDMF report, with an additional button
    /// to copy its complete contents into a bug report or chat message.
    /// </summary>
    internal sealed class AutoMaterialAtlasConsoleReport : SimpleError
    {
        private readonly string[] _details;

        internal AutoMaterialAtlasConsoleReport(string summary, string detail, string observed)
        {
            _details = new[] { summary, detail, observed };
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
                    observed)));
        }
    }
}
