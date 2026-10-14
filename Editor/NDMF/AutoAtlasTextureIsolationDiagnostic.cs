#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.localization;
using nadena.dev.ndmf.animator;
using net.rs64.TexTransTool.TextureAtlas;
using net.rs64.TexTransTool.Utils;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.Rendering;

namespace net.rs64.TexTransTool.NDMF
{
    internal sealed class AutoAtlasTextureIsolationDiagnosticState
    {
        private sealed class RendererSnapshot
        {
            internal readonly Renderer Renderer;
            internal readonly Mesh? Mesh;
            internal readonly Material?[] Materials;
            internal readonly SlotUvSnapshot[] SlotUv0;

            internal RendererSnapshot(Renderer renderer)
            {
                Renderer = renderer;
                Mesh = renderer.GetMesh();
                Materials = renderer.sharedMaterials.ToArray();
                SlotUv0 = CaptureSlotUv0(Mesh, Materials.Length);
            }
        }

        private sealed class AnimatedMaterialBindingSnapshot
        {
            internal readonly EditorCurveBinding Binding;
            internal readonly Renderer? Renderer;
            internal readonly int? Slot;
            internal readonly HashSet<Material> Materials;

            internal AnimatedMaterialBindingSnapshot(
                EditorCurveBinding binding,
                Renderer? renderer,
                int? slot,
                HashSet<Material> materials)
            {
                Binding = binding;
                Renderer = renderer;
                Slot = slot;
                Materials = materials;
            }
        }

        private sealed class MaterialSnapshot
        {
            internal readonly Material Material;
            internal readonly Shader? Shader;
            internal readonly string[] Keywords;
            internal readonly Dictionary<string, PropertySnapshot> Properties;

            internal MaterialSnapshot(Material material)
            {
                Material = material;
                Shader = material.shader;
                Keywords = material.shaderKeywords
                    .OrderBy(keyword => keyword, StringComparer.Ordinal)
                    .ToArray();
                Properties = CaptureProperties(material);
            }
        }

        private sealed class SlotUvSnapshot
        {
            internal readonly bool Available;
            internal readonly MeshTopology Topology;
            internal readonly Vector2[] IndexedUv;

            internal SlotUvSnapshot(
                bool available,
                MeshTopology topology,
                Vector2[] indexedUv)
            {
                Available = available;
                Topology = topology;
                IndexedUv = indexedUv;
            }
        }

        private readonly struct PropertySnapshot
        {
            internal readonly ShaderPropertyType Type;
            internal readonly Texture? Texture;
            internal readonly Vector4 Value;
            internal readonly Vector2 Scale;
            internal readonly Vector2 Offset;

            internal PropertySnapshot(
                ShaderPropertyType type,
                Texture? texture,
                Vector4 value,
                Vector2 scale,
                Vector2 offset)
            {
                Type = type;
                Texture = texture;
                Value = value;
                Scale = scale;
                Offset = offset;
            }
        }

        private readonly Dictionary<Renderer, RendererSnapshot> _renderers = new();
        private readonly Dictionary<Material, MaterialSnapshot> _materials = new();
        private readonly HashSet<Material> _targetedMaterials = new();
        private readonly Dictionary<Renderer, HashSet<Material>> _targetedByRenderer = new();
        private readonly Dictionary<Renderer, (bool UvEvacuation, bool MeshChanged)>
            _autoAaoNegotiation = new();
        private readonly List<AnimatedMaterialBindingSnapshot>
            _animatedMaterialBindings = new();
        private readonly Dictionary<(Renderer Renderer, int Slot), HashSet<Material>>
            _appliedTargetMaterialsBySlot = new();

        internal bool Enabled { get; private set; }
        internal AutoAtlasTexture? Configuration { get; private set; }

        internal void Begin(
            BuildContext context,
            AutoAtlasTexture configuration,
            IEnumerable<Renderer> renderers,
            IEnumerable<Material> referencedMaterials)
        {
            Enabled = true;
            Configuration = configuration;
            _renderers.Clear();
            _materials.Clear();
            _targetedMaterials.Clear();
            _targetedByRenderer.Clear();
            _animatedMaterialBindings.Clear();
            _appliedTargetMaterialsBySlot.Clear();

            CaptureAnimatedMaterialBindings(context);

            foreach (var renderer in renderers.Where(renderer => renderer != null))
            {
                var snapshot = new RendererSnapshot(renderer);
                _renderers[renderer] = snapshot;
                foreach (var material in snapshot.Materials)
                {
                    if (material == null || _materials.ContainsKey(material)) continue;
                    _materials[material] = new MaterialSnapshot(material);
                }
            }

            foreach (var material in referencedMaterials.Where(material => material != null))
            {
                if (_materials.ContainsKey(material)) continue;
                _materials[material] = new MaterialSnapshot(material);
            }
        }

        internal void MarkAutoAAONegotiation(
            Renderer renderer,
            bool uvEvacuationRequested,
            bool meshChanged)
        {
            if (renderer == null) return;
            _autoAaoNegotiation[renderer] =
                (uvEvacuationRequested, meshChanged);
        }

        internal void MarkApplied(
            IEnumerable<Material> materials,
            IEnumerable<Renderer> renderers)
        {
            var materialSet = materials.Where(material => material != null).ToHashSet();
            _targetedMaterials.UnionWith(materialSet);

            foreach (var renderer in renderers.Where(renderer => renderer != null))
            {
                if (!_targetedByRenderer.TryGetValue(renderer, out var targeted))
                    _targetedByRenderer[renderer] = targeted = new HashSet<Material>();
                targeted.UnionWith(materialSet);

                if (!_renderers.TryGetValue(renderer, out var rendererSnapshot))
                    continue;

                for (var slot = 0; slot < rendererSnapshot.Materials.Length; slot++)
                {
                    var material = rendererSnapshot.Materials[slot];
                    if (material == null || !materialSet.Contains(material))
                        continue;

                    var key = (renderer, slot);
                    if (!_appliedTargetMaterialsBySlot.TryGetValue(
                            key, out var slotMaterials))
                    {
                        _appliedTargetMaterialsBySlot[key] =
                            slotMaterials = new HashSet<Material>();
                    }

                    slotMaterials.Add(material);
                }
            }
        }

        internal void Report(string stage)
        {
            if (!Enabled) return;

            var lines = new List<string>();

            if (stage == "after AutoAtlasTexture")
                AppendAnimatedMaterialBoundaryChanges(lines, stage);

            var comparedReplacementMaterials =
                new HashSet<(Material Before, Material After)>();

            foreach (var pair in _autoAaoNegotiation)
            {
                var renderer = pair.Key;
                if (renderer == null || _targetedByRenderer.ContainsKey(renderer))
                    continue;
                if (!pair.Value.UvEvacuation && !pair.Value.MeshChanged)
                    continue;

                lines.Add(
                    $"[{stage}] AAO negotiated but AutoAtlas not applied: " +
                    $"{DescribeRenderer(renderer)} " +
                    $"(uvEvacuation={pair.Value.UvEvacuation}, " +
                    $"meshChanged={pair.Value.MeshChanged})");
            }

            foreach (var rendererSnapshot in _renderers.Values)
            {
                var renderer = rendererSnapshot.Renderer;
                if (renderer == null)
                {
                    lines.Add($"[{stage}] Renderer destroyed: {DescribeRenderer(rendererSnapshot.Renderer)}");
                    continue;
                }

                var currentMaterials = renderer.sharedMaterials;
                var maxSlots = Math.Max(
                    rendererSnapshot.Materials.Length,
                    currentMaterials.Length);

                for (var slot = 0; slot < maxSlots; slot++)
                {
                    var before = slot < rendererSnapshot.Materials.Length
                        ? rendererSnapshot.Materials[slot]
                        : null;
                    var after = slot < currentMaterials.Length
                        ? currentMaterials[slot]
                        : null;

                    if (before == null || _targetedMaterials.Contains(before))
                        continue;

                    if (!ReferenceEquals(before, after))
                    {
                        lines.Add(
                            $"[{stage}] Non-target material slot changed: " +
                            $"{DescribeRenderer(renderer)} slot={slot} " +
                            $"{DescribeMaterial(before)} -> {DescribeMaterial(after)}");

                        if (after != null
                            && _materials.TryGetValue(before, out var beforeSnapshot)
                            && comparedReplacementMaterials.Add((before, after)))
                        {
                            AppendReplacementMaterialChanges(
                                beforeSnapshot,
                                after,
                                stage,
                                renderer,
                                slot,
                                lines);
                        }
                    }
                }

                for (var slot = 0; slot < rendererSnapshot.Materials.Length; slot++)
                {
                    var material = rendererSnapshot.Materials[slot];
                    if (material == null || _targetedMaterials.Contains(material))
                        continue;

                    var beforeUv = slot < rendererSnapshot.SlotUv0.Length
                        ? rendererSnapshot.SlotUv0[slot]
                        : UnavailableSlotUvSnapshot();
                    var afterUv = CaptureSlotUv0(renderer.GetMesh(), currentMaterials.Length);
                    var currentUv = slot < afterUv.Length
                        ? afterUv[slot]
                        : UnavailableSlotUvSnapshot();

                    if (!SlotUvEquals(beforeUv, currentUv))
                    {
                        lines.Add(
                            $"[{stage}] Non-target slot UV0 changed: " +
                            $"{DescribeRenderer(renderer)} slot={slot} " +
                            $"{DescribeMaterial(material)} " +
                            $"{DescribeSlotUv(beforeUv)} -> {DescribeSlotUv(currentUv)}");
                    }
                }

                var hadTargetMaterial = rendererSnapshot.Materials
                    .Any(material => material != null
                        && _targetedMaterials.Contains(material));
                if (!hadTargetMaterial
                    && !ReferenceEquals(rendererSnapshot.Mesh, renderer.GetMesh()))
                {
                    lines.Add(
                        $"[{stage}] Non-target renderer mesh changed: " +
                        $"{DescribeRenderer(renderer)} " +
                        $"{DescribeMesh(rendererSnapshot.Mesh)} -> " +
                        $"{DescribeMesh(renderer.GetMesh())}");
                }
                else if (hadTargetMaterial
                    && rendererSnapshot.Materials.Any(material =>
                        material != null
                        && !_targetedMaterials.Contains(material))
                    && !ReferenceEquals(rendererSnapshot.Mesh, renderer.GetMesh()))
                {
                    lines.Add(
                        $"[{stage}] Mixed renderer mesh changed: " +
                        $"{DescribeRenderer(renderer)} " +
                        $"{DescribeMesh(rendererSnapshot.Mesh)} -> " +
                        $"{DescribeMesh(renderer.GetMesh())}");
                }
            }

            foreach (var materialSnapshot in _materials.Values)
            {
                var material = materialSnapshot.Material;
                if (material == null || _targetedMaterials.Contains(material))
                    continue;

                AppendMaterialChanges(materialSnapshot, stage, lines);
            }

            if (lines.Count == 0)
            {
                lines.Add($"[{stage}] No direct non-target material/slot/mesh changes detected.");
            }

            void ReportDiagnostic()
            {
                ErrorReport.ReportError(
                    new AutoAtlasTextureIsolationDiagnosticError(
                        string.Join("\n", lines)));
            }

            if (Configuration != null)
                TTTLog.ReportingObject(Configuration, ReportDiagnostic);
            else
                ReportDiagnostic();
        }

        private static void AppendMaterialChanges(
            MaterialSnapshot before,
            string stage,
            List<string> lines)
        {
            var material = before.Material;
            if (material == null) return;

            if (!ReferenceEquals(before.Shader, material.shader))
            {
                lines.Add(
                    $"[{stage}] Non-target material shader changed: " +
                    $"{DescribeMaterial(material)} " +
                    $"{DescribeShader(before.Shader)} -> {DescribeShader(material.shader)}");
            }

            var currentKeywords = material.shaderKeywords
                .OrderBy(keyword => keyword, StringComparer.Ordinal)
                .ToArray();
            if (!before.Keywords.SequenceEqual(currentKeywords))
            {
                lines.Add(
                    $"[{stage}] Non-target material keywords changed: " +
                    $"{DescribeMaterial(material)}");
            }

            var afterProperties = CaptureProperties(material);
            foreach (var propertyName in before.Properties.Keys
                         .Union(afterProperties.Keys)
                         .OrderBy(name => name, StringComparer.Ordinal))
            {
                var hasBefore = before.Properties.TryGetValue(propertyName, out var oldValue);
                var hasAfter = afterProperties.TryGetValue(propertyName, out var newValue);
                if (!hasBefore || !hasAfter)
                {
                    lines.Add(
                        $"[{stage}] Non-target material property set changed: " +
                        $"{DescribeMaterial(material)}.{propertyName}");
                    continue;
                }

                if (oldValue.Equals(newValue)) continue;

                lines.Add(
                    $"[{stage}] Non-target material property changed: " +
                    $"{DescribeMaterial(material)}.{propertyName} " +
                    $"{DescribeProperty(oldValue)} -> {DescribeProperty(newValue)}");
            }
        }

        private static void AppendReplacementMaterialChanges(
            MaterialSnapshot before,
            Material after,
            string stage,
            Renderer renderer,
            int slot,
            List<string> lines)
        {
            var context =
                $"{DescribeRenderer(renderer)} slot={slot} " +
                $"{DescribeMaterial(before.Material)} -> {DescribeMaterial(after)}";

            if (!ReferenceEquals(before.Shader, after.shader))
            {
                lines.Add(
                    $"[{stage}] Replaced non-target material shader changed: " +
                    $"{context} {DescribeShader(before.Shader)} -> " +
                    $"{DescribeShader(after.shader)}");
            }

            var afterKeywords = after.shaderKeywords
                .OrderBy(keyword => keyword, StringComparer.Ordinal)
                .ToArray();
            if (!before.Keywords.SequenceEqual(afterKeywords))
            {
                lines.Add(
                    $"[{stage}] Replaced non-target material keywords changed: " +
                    context);
            }

            var afterProperties = CaptureProperties(after);
            foreach (var propertyName in before.Properties.Keys
                         .Union(afterProperties.Keys)
                         .OrderBy(name => name, StringComparer.Ordinal))
            {
                var hasBefore = before.Properties.TryGetValue(
                    propertyName, out var oldValue);
                var hasAfter = afterProperties.TryGetValue(
                    propertyName, out var newValue);

                if (!hasBefore || !hasAfter)
                {
                    lines.Add(
                        $"[{stage}] Replaced non-target material property set changed: " +
                        $"{context}.{propertyName}");
                    continue;
                }

                if (oldValue.Equals(newValue)) continue;

                lines.Add(
                    $"[{stage}] Replaced non-target material property changed: " +
                    $"{context}.{propertyName} " +
                    $"{DescribeProperty(oldValue)} -> " +
                    $"{DescribeProperty(newValue)}");
            }
        }

        private void CaptureAnimatedMaterialBindings(BuildContext context)
        {
            var animationIndex =
                context.Extension<AnimatorServicesContext>().AnimationIndex;
            var root = context.AvatarRootObject.transform;

            foreach (var group in animationIndex
                         .GetPPtrReferencedObjectsWithBinding
                         .Where(pair => pair.obj is Material)
                         .GroupBy(pair => pair.binding))
            {
                var materials = group
                    .Select(pair => pair.obj)
                    .OfType<Material>()
                    .Where(material => material != null)
                    .ToHashSet();
                if (materials.Count == 0) continue;

                var binding = group.Key;
                Transform? targetTransform;
                if (string.IsNullOrEmpty(binding.path))
                    targetTransform = root;
                else
                    targetTransform = root.Find(binding.path);

                Renderer? renderer = null;
                if (targetTransform != null
                    && typeof(Renderer).IsAssignableFrom(binding.type))
                {
                    renderer = targetTransform.GetComponent(binding.type) as Renderer;
                }

                _animatedMaterialBindings.Add(
                    new AnimatedMaterialBindingSnapshot(
                        binding,
                        renderer,
                        ParseMaterialSlot(binding.propertyName),
                        materials));
            }
        }

        private void AppendAnimatedMaterialBoundaryChanges(
            List<string> lines,
            string stage)
        {
            foreach (var animated in _animatedMaterialBindings)
            {
                if (animated.Renderer == null || !animated.Slot.HasValue)
                    continue;

                var renderer = animated.Renderer;
                var slot = animated.Slot.Value;
                _appliedTargetMaterialsBySlot.TryGetValue(
                    (renderer, slot), out var appliedTargets);
                appliedTargets ??= new HashSet<Material>();

                var animatedTargets = animated.Materials
                    .Where(_targetedMaterials.Contains)
                    .ToArray();
                var animatedNonTargets = animated.Materials
                    .Where(material => !_targetedMaterials.Contains(material))
                    .ToArray();

                var targetOutsideAppliedSlot = animatedTargets
                    .Any(material => !appliedTargets.Contains(material));
                var nonTargetIntoAppliedSlot =
                    appliedTargets.Count != 0 && animatedNonTargets.Length != 0;

                if (!targetOutsideAppliedSlot && !nonTargetIntoAppliedSlot)
                    continue;

                var staticMaterial =
                    _renderers.TryGetValue(renderer, out var snapshot)
                    && slot >= 0
                    && slot < snapshot.Materials.Length
                        ? snapshot.Materials[slot]
                        : null;

                lines.Add(
                    $"[{stage}] Animated material slot crosses AutoAtlas boundary: " +
                    $"{DescribeRenderer(renderer)} slot={slot} " +
                    $"binding={animated.Binding.path}:{animated.Binding.propertyName} " +
                    $"static={DescribeMaterial(staticMaterial)} " +
                    $"appliedTargets=[{string.Join(", ", appliedTargets.Select(DescribeMaterial))}] " +
                    $"curveValues=[{string.Join(", ", animated.Materials.Select(DescribeMaterial))}]");
            }
        }

        private static int? ParseMaterialSlot(string propertyName)
        {
            const string prefix = "m_Materials.Array.data[";
            if (!propertyName.StartsWith(prefix, StringComparison.Ordinal)
                || !propertyName.EndsWith("]", StringComparison.Ordinal))
                return null;

            var slotText = propertyName.Substring(
                prefix.Length,
                propertyName.Length - prefix.Length - 1);
            return int.TryParse(slotText, out var slot) ? slot : null;
        }

        private static SlotUvSnapshot[] CaptureSlotUv0(
            Mesh? mesh,
            int materialSlotCount)
        {
            var result = new SlotUvSnapshot[materialSlotCount];
            if (mesh == null || mesh.subMeshCount <= 0 || !mesh.HasUV(0))
            {
                for (var i = 0; i < result.Length; i++)
                    result[i] = UnavailableSlotUvSnapshot();
                return result;
            }

            var uv = mesh.uv;
            for (var slot = 0; slot < result.Length; slot++)
            {
                var subMesh = Math.Clamp(slot, 0, mesh.subMeshCount - 1);
                var topology = mesh.GetTopology(subMesh);
                var indices = mesh.GetIndices(subMesh);
                var indexedUv = new Vector2[indices.Length];
                var available = true;
                for (var i = 0; i < indices.Length; i++)
                {
                    var index = indices[i];
                    if (index < 0 || index >= uv.Length)
                    {
                        available = false;
                        indexedUv = Array.Empty<Vector2>();
                        break;
                    }

                    indexedUv[i] = uv[index];
                }

                result[slot] = new SlotUvSnapshot(
                    available,
                    topology,
                    indexedUv);
            }

            return result;
        }

        private static SlotUvSnapshot UnavailableSlotUvSnapshot()
        {
            return new SlotUvSnapshot(
                false,
                MeshTopology.Triangles,
                Array.Empty<Vector2>());
        }

        private static bool SlotUvEquals(
            SlotUvSnapshot left,
            SlotUvSnapshot right)
        {
            return left.Available == right.Available
                && left.Topology == right.Topology
                && left.IndexedUv.SequenceEqual(right.IndexedUv);
        }

        private static string DescribeSlotUv(SlotUvSnapshot value)
        {
            if (!value.Available) return "<unavailable>";
            unchecked
            {
                var hash = 17;
                foreach (var uv in value.IndexedUv)
                {
                    hash = hash * 31 + uv.x.GetHashCode();
                    hash = hash * 31 + uv.y.GetHashCode();
                }

                return $"{value.Topology} indices={value.IndexedUv.Length} hash={hash}";
            }
        }

        private static Dictionary<string, PropertySnapshot> CaptureProperties(
            Material material)
        {
            var result = new Dictionary<string, PropertySnapshot>(
                StringComparer.Ordinal);
            var shader = material.shader;
            if (shader == null) return result;

            for (var i = 0; i < shader.GetPropertyCount(); i++)
            {
                var propertyName = shader.GetPropertyName(i);
                var propertyType = shader.GetPropertyType(i);
                switch (propertyType)
                {
                    case ShaderPropertyType.Texture:
                    {
                        result[propertyName] = new PropertySnapshot(
                            propertyType,
                            material.GetTexture(propertyName),
                            default,
                            material.GetTextureScale(propertyName),
                            material.GetTextureOffset(propertyName));
                        break;
                    }
                    case ShaderPropertyType.Color:
                    {
                        result[propertyName] = new PropertySnapshot(
                            propertyType,
                            null,
                            material.GetColor(propertyName),
                            default,
                            default);
                        break;
                    }
                    case ShaderPropertyType.Vector:
                    {
                        result[propertyName] = new PropertySnapshot(
                            propertyType,
                            null,
                            material.GetVector(propertyName),
                            default,
                            default);
                        break;
                    }
                    case ShaderPropertyType.Float:
                    case ShaderPropertyType.Range:
                    {
                        var value = material.GetFloat(propertyName);
                        result[propertyName] = new PropertySnapshot(
                            propertyType,
                            null,
                            new Vector4(value, 0f, 0f, 0f),
                            default,
                            default);
                        break;
                    }
                    case ShaderPropertyType.Int:
                    {
                        var value = material.GetInt(propertyName);
                        result[propertyName] = new PropertySnapshot(
                            propertyType,
                            null,
                            new Vector4(value, 0f, 0f, 0f),
                            default,
                            default);
                        break;
                    }
                }
            }

            return result;
        }

        private static string DescribeProperty(PropertySnapshot value)
        {
            return value.Type == ShaderPropertyType.Texture
                ? $"{DescribeTexture(value.Texture)} scale={value.Scale} offset={value.Offset}"
                : value.Value.ToString();
        }

        private static string DescribeRenderer(Renderer? renderer)
        {
            if (renderer == null) return "<null>";
            return renderer.name + "#" + renderer.GetInstanceID();
        }

        private static string DescribeMaterial(Material? material)
        {
            if (material == null) return "<null>";
            return material.name + "#" + material.GetInstanceID();
        }

        private static string DescribeMesh(Mesh? mesh)
        {
            if (mesh == null) return "<null>";
            return mesh.name + "#" + mesh.GetInstanceID();
        }

        private static string DescribeTexture(Texture? texture)
        {
            if (texture == null) return "<null>";
            return texture.name + "#" + texture.GetInstanceID();
        }

        private static string DescribeShader(Shader? shader)
        {
            if (shader == null) return "<null>";
            return shader.name + "#" + shader.GetInstanceID();
        }
    }

    internal sealed class AutoAtlasTextureIsolationDiagnosticError : SimpleError
    {
        private readonly string _message;

        internal AutoAtlasTextureIsolationDiagnosticError(string message)
        {
            _message = message;
        }

        public override Localizer Localizer => TTTLog.NDMFLocalizer;
        public override ErrorSeverity Severity => ErrorSeverity.Information;
        public override string TitleKey =>
            "AutoAtlasTexture:info:TemporaryIsolationDiagnostic";
        public override string[] TitleSubst => new[] { _message };

        public override VisualElement CreateVisualElement(ErrorReport report)
        {
            var root = new VisualElement();
            root.style.flexGrow = 1;
            root.Add(base.CreateVisualElement(report));

            var copyButton = new Button(() =>
                EditorGUIUtility.systemCopyBuffer = FormatTitle())
            {
                text = "AutoAtlasTexture:info:TemporaryIsolationDiagnosticCopy"
                    .GetLocalize(),
            };
            copyButton.style.alignSelf = Align.FlexEnd;
            root.Add(copyButton);

            return root;
        }
    }

    internal static class AutoAtlasTextureIsolationDiagnostic
    {
        internal static AutoAtlasTextureIsolationDiagnosticState State(
            BuildContext context)
        {
            return context.GetState(_ =>
                new AutoAtlasTextureIsolationDiagnosticState());
        }
    }

    internal sealed class AutoAtlasTexturePostAAODiagnosticPass
        : Pass<AutoAtlasTexturePostAAODiagnosticPass>
    {
        protected override void Execute(BuildContext context)
        {
            AutoAtlasTextureIsolationDiagnostic.State(context)
                .Report("after AvatarOptimizer");
        }
    }
}
