#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using UnityEngine;
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

            internal RendererSnapshot(Renderer renderer)
            {
                Renderer = renderer;
                Mesh = renderer.GetMesh();
                Materials = renderer.sharedMaterials.ToArray();
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

        private readonly record struct PropertySnapshot(
            ShaderPropertyType Type,
            Texture? Texture,
            Vector4 Value,
            Vector2 Scale,
            Vector2 Offset);

        private readonly Dictionary<Renderer, RendererSnapshot> _renderers = new();
        private readonly Dictionary<Material, MaterialSnapshot> _materials = new();
        private readonly HashSet<Material> _targetedMaterials = new();
        private readonly Dictionary<Renderer, HashSet<Material>> _targetedByRenderer = new();
        private readonly Dictionary<Renderer, string> _autoAaoNegotiation = new();

        internal bool Enabled { get; private set; }
        internal AutoAtlasTexture? Configuration { get; private set; }

        internal void Begin(
            AutoAtlasTexture configuration,
            IEnumerable<Renderer> renderers)
        {
            Enabled = true;
            Configuration = configuration;
            _renderers.Clear();
            _materials.Clear();
            _targetedMaterials.Clear();
            _targetedByRenderer.Clear();

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
        }

        internal void MarkAutoAAONegotiation(
            Renderer renderer,
            bool uvEvacuationRequested,
            bool meshChanged)
        {
            if (renderer == null) return;
            _autoAaoNegotiation[renderer] =
                $"uvEvacuation={uvEvacuationRequested}, meshChanged={meshChanged}";
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
            }
        }

        internal void Report(string stage)
        {
            if (!Enabled || Configuration == null) return;

            var lines = new List<string>();

            foreach (var pair in _autoAaoNegotiation)
            {
                var renderer = pair.Key;
                if (renderer == null || _targetedByRenderer.ContainsKey(renderer))
                    continue;

                lines.Add(
                    $"[{stage}] AAO negotiated but AutoAtlas not applied: " +
                    $"{DescribeRenderer(renderer)} ({pair.Value})");
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

            TTTLog.ReportingObject(Configuration, () =>
                TTTLog.Info(
                    "AutoAtlasTexture:info:TemporaryIsolationDiagnostic",
                    string.Join("\n", lines)));
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

    internal static class AutoAtlasTextureIsolationDiagnostic
    {
        internal static AutoAtlasTextureIsolationDiagnosticState State(
            BuildContext context)
        {
            return context.GetState(_ =>
                new AutoAtlasTextureIsolationDiagnosticState());
        }
    }
}
