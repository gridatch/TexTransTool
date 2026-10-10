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
        public string PairSurveyDiagnostics = "";
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
            report.PairSurveyDiagnostics = BuildMaterialPairSurvey(root, renderers, animationMaterials, animationIndex);
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

        // Observational material-slot census. Deliberately includes all material
        // animations and compares ALL pairs, even when the existing strict
        // candidate filter excluded the renderer. This is not an AAO prediction.
        private static string BuildMaterialPairSurvey(
            GameObject root, SkinnedMeshRenderer[] renderers,
            HashSet<Material> referencedMaterials, AnimationIndex? animationIndex)
        {
            int slotsCompared = 0, sameReference = 0, differentShader = 0;
            int sameShader = 0, sameState = 0, differentState = 0;
            int textureDiffers = 0, textureSame = 0, singleDifference = 0;
            int topologySame = 0, topologyDifferent = 0, topologyUnknown = 0;
            int slotAnimation = 0, materialReferenceAnimation = 0, partialAAOPreconditions = 0;
            var frequency = new Dictionary<string, int>(StringComparer.Ordinal);
            var examples = new List<MaterialPairSurveyEntry>();

            foreach (var renderer in renderers)
            {
                if (renderer == null || !AtlasTexture.IsAtlasAllowedRenderer(renderer))
                    continue;
                var materials = renderer.sharedMaterials;
                var rendererPath = AnimationUtility.CalculateTransformPath(renderer.transform, root.transform);
                if (rendererPath.Length == 0) rendererPath = "(avatar root)";
                var (_, animatedSlots) = GetMaterialAnimationBindings(
                    animationIndex, root.transform, renderer);
                bool unverifiedSlots = animatedSlots.Length != 0;
                var mesh = renderer.sharedMesh;

                for (int i = 0; i < materials.Length; i++)
                for (int j = i + 1; j < materials.Length; j++)
                {
                    var left = materials[i];
                    var right = materials[j];
                    if (left == null || right == null) continue;
                    slotsCompared++;

                    if (left == right)
                    {
                        // AAO already receives one shared Material reference.
                        sameReference++;
                        continue;
                    }

                    if (left.shader == null || left.shader != right.shader)
                    {
                        differentShader++;
                        continue;
                    }

                    sameShader++;
                    var differences = CollectAllNonTextureDifferences(left, right);
                    var texturesDiffer = !HasSameTextureReferences(left, right);
                    bool usesAnimatedReference = referencedMaterials.Contains(left)
                        || referencedMaterials.Contains(right);

                    bool knownTopology = mesh != null
                        && i < mesh.subMeshCount && j < mesh.subMeshCount;
                    bool matchesTopology = knownTopology && mesh!.GetTopology(i) == mesh.GetTopology(j);
                    if (!knownTopology) topologyUnknown++;
                    else if (matchesTopology) topologySame++;
                    else topologyDifferent++;

                    if (unverifiedSlots) slotAnimation++;
                    if (usesAnimatedReference) materialReferenceAnimation++;

                    if (differences.Length == 0)
                    {
                        sameState++;
                        if (texturesDiffer) textureDiffers++;
                        else textureSame++;
                        if (matchesTopology && !unverifiedSlots && !usesAnimatedReference)
                            partialAAOPreconditions++;
                    }
                    else
                    {
                        differentState++;
                        if (differences.Length == 1) singleDifference++;
                        foreach (var difference in differences)
                        {
                            frequency.TryGetValue(difference, out int count);
                            frequency[difference] = count + 1;
                        }
                    }

                    examples.Add(new MaterialPairSurveyEntry(
                        rendererPath, i, j, left.name, right.name, left.shader.name,
                        differences, texturesDiffer, knownTopology, matchesTopology,
                        unverifiedSlots, usesAnimatedReference));
                }
            }

            var frequent = frequency.OrderByDescending(x => x.Value)
                .ThenBy(x => x.Key, StringComparer.Ordinal)
                .Take(24)
                .Select(x => x.Key + ": " + x.Value)
                .ToArray();

            // Up to two closest pairs per renderer keeps one complex outfit
            // from crowding out every other renderer in the sample.
            var chosen = examples.GroupBy(x => x.RendererPath)
                .SelectMany(g => g.OrderBy(x => x.Differences.Length)
                    .ThenBy(x => x.TextureReferencesDiffer ? 0 : 1)
                    .ThenBy(x => x.SlotA).ThenBy(x => x.SlotB).Take(2))
                .OrderBy(x => x.Differences.Length)
                .ThenBy(x => x.RendererPath, StringComparer.Ordinal)
                .ThenBy(x => x.SlotA).ThenBy(x => x.SlotB)
                .Take(24)
                .Select((x, index) =>
                {
                    var shown = x.Differences.Take(8).ToArray();
                    var mismatchDescription = x.Differences.Length == 0
                        ? "non-texture settings equal"
                        : x.Differences.Length + " differing settings: "
                          + string.Join(", ", shown)
                          + (x.Differences.Length > shown.Length ? ", ..." : "");
                    return (index + 1) + ". " + x.RendererPath
                        + " [" + x.SlotA + "/" + x.SlotB + "]: "
                        + x.MaterialA + " vs " + x.MaterialB
                        + " (" + x.ShaderName + "); " + mismatchDescription
                        + "; " + (x.TextureReferencesDiffer ? "texture refs differ" : "texture refs equal")
                        + "; " + (!x.TopologyKnown ? "topology unverified"
                            : x.TopologyMatches ? "topology equal" : "topology differs")
                        + (x.SlotAnimationUnverified ? "; slot animation/index unverified" : "")
                        + (x.MaterialReferenceAnimation ? "; material PPtr animation" : "");
                }).ToArray();

            return "MATERIAL DIFFERENCE SURVEY (all same-Renderer slot pairs; before AAO; read-only):\n"
                + "Total non-null slot pairs: " + slotsCompared
                + "; shared Material-ref pairs (AAO already sees reference): " + sameReference
                + "; different/missing shader pairs: " + differentShader
                + "; distinct-ref same-shader pairs: " + sameShader + "\n"
                + "Same shader + equal non-texture state: " + sameState
                + " (different texture refs: " + textureDiffers
                + "; equal texture refs: " + textureSame + ")\n"
                + "Same shader + different state: " + differentState
                + " (exactly one differing setting: " + singleDifference + ")\n"
                + "Same-shader topology: equal=" + topologySame
                + "; different=" + topologyDifferent
                + "; unknown=" + topologyUnknown + "\n"
                + "Same-shader pairs involving slot animation/unavailable index: " + slotAnimation
                + "; PPtr-animated Materials: " + materialReferenceAnimation + "\n"
                + "Equal-state pairs passing only basic topology + reference checks: "
                + partialAAOPreconditions + " (PAIR COUNT, NOT savings or certified compatibility)\n"
                + "All differing properties and states (frequency per same-shader pair; top 24): "
                + (frequent.Length == 0 ? "(none)" : string.Join("; ", frequent)) + "\n"
                + "Closest examples (at most 2 per Renderer, 24 total; NOT merge-safe verdicts):\n"
                + (chosen.Length == 0 ? "(none)" : string.Join("\n", chosen))
                + (examples.Count > chosen.Length ? "\n(Additional pairs included in full statistics)" : "")
                + "\nUnverified for ALL pairs: effective shader semantics, UV/texture quality,"
                + " live material-property animation, AAO options/merging, memory and draw-call delta.";
        }

        private sealed class MaterialPairSurveyEntry
        {
            internal readonly string RendererPath;
            internal readonly int SlotA, SlotB;
            internal readonly string MaterialA, MaterialB, ShaderName;
            internal readonly string[] Differences;
            internal readonly bool TextureReferencesDiffer, TopologyKnown, TopologyMatches;
            internal readonly bool SlotAnimationUnverified, MaterialReferenceAnimation;

            internal MaterialPairSurveyEntry(string rendererPath, int slotA, int slotB,
                string materialA, string materialB, string shaderName, string[] differences,
                bool textureReferencesDiffer, bool topologyKnown, bool topologyMatches,
                bool slotAnimationUnverified, bool materialReferenceAnimation)
            {
                RendererPath = rendererPath;
                SlotA = slotA; SlotB = slotB;
                MaterialA = materialA; MaterialB = materialB; ShaderName = shaderName;
                Differences = differences;
                TextureReferencesDiffer = textureReferencesDiffer;
                TopologyKnown = topologyKnown; TopologyMatches = topologyMatches;
                SlotAnimationUnverified = slotAnimationUnverified;
                MaterialReferenceAnimation = materialReferenceAnimation;
            }
        }

        private static string? DescribeNonTextureMismatch(Material left, Material right)
            => CollectAllNonTextureDifferences(left, right).FirstOrDefault();

        // Report serialized differences, never guess which shader properties are
        // inactive. The same comparator is used by strict candidate detection.
        private static string[] CollectAllNonTextureDifferences(Material left, Material right)
        {
            var diff = new List<string>();
            if (left == null || right == null) return new[] { "state.missingMaterial" };
            if (left.shader != right.shader) return new[] { "state.differentShader" };
            if (left.shader == null) return new[] { "state.missingShader" };
            if (left.renderQueue != right.renderQueue) diff.Add("state.renderQueue");
            if (left.enableInstancing != right.enableInstancing) diff.Add("state.enableInstancing");
            if (left.doubleSidedGI != right.doubleSidedGI) diff.Add("state.doubleSidedGI");
            if (left.globalIlluminationFlags != right.globalIlluminationFlags)
                diff.Add("state.globalIlluminationFlags");
            if (!left.shaderKeywords.ToHashSet(StringComparer.Ordinal)
                .SetEquals(right.shaderKeywords))
                diff.Add("state.shaderKeywords");

            foreach (var tag in new[] { "RenderType", "Queue", "RenderPipeline",
                         "IgnoreProjector", "DisableBatching", "ForceNoShadowCasting" })
            {
                if (left.GetTag(tag, false, "") != right.GetTag(tag, false, ""))
                    diff.Add("state.tag." + tag);
            }

            var shader = left.shader;
            for (int i = 0; i < shader.GetPropertyCount(); i++)
            {
                var name = shader.GetPropertyName(i);
                bool mismatch;
                switch (shader.GetPropertyType(i))
                {
                    case ShaderPropertyType.Color:
                        mismatch = left.GetColor(name) != right.GetColor(name); break;
                    case ShaderPropertyType.Vector:
                        mismatch = left.GetVector(name) != right.GetVector(name); break;
                    case ShaderPropertyType.Float:
                    case ShaderPropertyType.Range:
                        mismatch = left.GetFloat(name) != right.GetFloat(name); break;
                    case ShaderPropertyType.Int:
                        mismatch = left.GetInt(name) != right.GetInt(name); break;
                    case ShaderPropertyType.Texture:
                        continue;
                    default:
                        diff.Add("state.unsupportedPropertyType." + name); continue;
                }
                if (mismatch) diff.Add("property." + name);
            }
            return diff.ToArray();
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
            string crossRendererDiagnostics, string pairSurveyDiagnostics)
        {
            _details = new[] {
                summary, detail, observed, animationDiagnostics,
                candidateDiagnostics, crossRendererDiagnostics, pairSurveyDiagnostics
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
                    report.CrossRendererDiagnostics,
                    report.PairSurveyDiagnostics)));
        }
    }
}
