#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using net.rs64.TexTransCore;
using net.rs64.TexTransCoreEngineForUnity;
using net.rs64.TexTransTool.TextureAtlas.AAOCode;
using net.rs64.TexTransTool.TextureAtlas.FineTuning;
using net.rs64.TexTransTool.Utils;
using UnityEngine;
using UnityEngine.Rendering;

namespace net.rs64.TexTransTool.TextureAtlas
{
    internal readonly struct AtlasConstantTextureDecision
    {
        internal readonly Material Material;
        internal readonly string Property;
        internal readonly Texture Source;
        internal readonly Color Color;

        internal AtlasConstantTextureDecision(
            Material material, string property, Texture source, Color color)
        {
            Material = material;
            Property = property;
            Source = source;
            Color = color;
        }
    }

    internal sealed class AtlasFeatureDisableRule
    {
        internal readonly string Name;
        internal readonly string SwitchProperty;
        internal readonly string[] TextureProperties;

        internal AtlasFeatureDisableRule(
            string name, string switchProperty, params string[] textureProperties)
        {
            Name = name;
            SwitchProperty = switchProperty;
            TextureProperties = textureProperties;
        }
    }

    internal readonly struct AtlasFeatureDisableDecision
    {
        internal readonly Material Material;
        internal readonly AtlasFeatureDisableRule Rule;

        internal AtlasFeatureDisableDecision(
            Material material, AtlasFeatureDisableRule rule)
        {
            Material = material;
            Rule = rule;
        }
    }

    internal sealed class AtlasSourceOptimizationPlan
    {
        internal readonly IReadOnlyDictionary<
            Material, IReadOnlyDictionary<string, Texture>> OriginalTextureUses;
        internal readonly IReadOnlyDictionary<
            Material, IReadOnlyDictionary<string, Texture>> ProjectedTextureUses;
        internal readonly AtlasConstantTextureDecision[] ConstantTextures;
        internal readonly AtlasFeatureDisableDecision[] FeatureDisables;

        internal AtlasSourceOptimizationPlan(
            IReadOnlyDictionary<Material, IReadOnlyDictionary<string, Texture>>
                originalTextureUses,
            IReadOnlyDictionary<Material, IReadOnlyDictionary<string, Texture>>
                projectedTextureUses,
            IEnumerable<AtlasConstantTextureDecision> constantTextures,
            IEnumerable<AtlasFeatureDisableDecision> featureDisables)
        {
            OriginalTextureUses = originalTextureUses;
            ProjectedTextureUses = projectedTextureUses;
            ConstantTextures = constantTextures.ToArray();
            FeatureDisables = featureDisables.ToArray();
        }

        internal bool HasChanges =>
            ConstantTextures.Length != 0 || FeatureDisables.Length != 0;

        internal HashSet<Material> GetAtlasMaterials(
            IEnumerable<Material> materials)
        {
            return materials
                .Where(material =>
                    ProjectedTextureUses.TryGetValue(material, out var textures)
                    && textures.Count != 0)
                .ToHashSet();
        }

        internal HashSet<Material> GetOptimizationOnlyMaterials(
            IEnumerable<Material> materials)
        {
            var changed = ConstantTextures.Select(item => item.Material)
                .Concat(FeatureDisables.Select(item => item.Material))
                .ToHashSet();
            return materials
                .Where(changed.Contains)
                .Where(material =>
                    !ProjectedTextureUses.TryGetValue(material, out var textures)
                    || textures.Count == 0)
                .ToHashSet();
        }

        internal HashSet<Texture> CollectOriginalTextures(
            IEnumerable<Material> materials)
        {
            var selected = materials.ToHashSet();
            var result = selected
                .Where(OriginalTextureUses.ContainsKey)
                .SelectMany(material => OriginalTextureUses[material].Values)
                .Where(texture => texture != null)
                .ToHashSet();

            // Feature removal can also release textures that were never atlas
            // inputs themselves (for example a non-mesh-UV MatCap texture
            // whose UV-space blend mask proves the whole feature inactive).
            foreach (var item in FeatureDisables)
            {
                if (!selected.Contains(item.Material)) continue;
                foreach (var property in item.Rule.TextureProperties)
                {
                    if (!item.Material.HasShaderTextureProperty(property))
                        continue;
                    var texture = item.Material.GetTexture(property);
                    if (texture is Texture2D || texture is RenderTexture)
                        result.Add(texture);
                }
            }

            return result;
        }

        internal HashSet<(Material material, string property)>
            RemovedBindings(IEnumerable<Material> materials)
        {
            var selected = materials.ToHashSet();
            var result = ConstantTextures
                .Where(item => selected.Contains(item.Material))
                .Select(item => (item.Material, item.Property))
                .ToHashSet();
            foreach (var item in FeatureDisables)
            {
                if (!selected.Contains(item.Material)) continue;
                foreach (var property in item.Rule.TextureProperties)
                    result.Add((item.Material, property));
            }
            return result;
        }
    }

    internal readonly struct AtlasConstantTextureOutput
    {
        internal readonly AtlasConstantTextureDecision Decision;
        internal readonly ITTRenderTexture RenderTexture;
        internal readonly TexTransToolTextureDescriptor Descriptor;

        internal AtlasConstantTextureOutput(
            AtlasConstantTextureDecision decision,
            ITTRenderTexture renderTexture,
            TexTransToolTextureDescriptor descriptor)
        {
            Decision = decision;
            RenderTexture = renderTexture;
            Descriptor = descriptor;
        }
    }

    internal static class AtlasSourceOptimizer
    {
        private const long MaxSourcePixels = 4096L * 4096L;
        private const long MaxTrianglePixelChecks = 24L * 1024L * 1024L;

        private readonly struct SampleResult
        {
            internal readonly bool Known;
            internal readonly bool RgbZero;
            internal readonly bool AlphaZero;
            internal readonly bool IsConstant;
            internal readonly Color ConstantColor;

            internal SampleResult(
                bool known, bool rgbZero, bool alphaZero,
                bool isConstant, Color constantColor)
            {
                Known = known;
                RgbZero = rgbZero;
                AlphaZero = alphaZero;
                IsConstant = isConstant;
                ConstantColor = constantColor;
            }
        }

        private readonly struct Candidate
        {
            internal readonly Material Material;
            internal readonly string Property;
            internal readonly Texture Texture;
            internal readonly Matrix2x3 Matrix;
            internal readonly HashSet<(Mesh mesh, int subMesh)> Slots;

            internal Candidate(
                Material material, string property, Texture texture,
                Matrix2x3 matrix, HashSet<(Mesh mesh, int subMesh)> slots)
            {
                Material = material;
                Property = property;
                Texture = texture;
                Matrix = matrix;
                Slots = slots;
            }
        }

        internal static AtlasSourceOptimizationPlan Analyze(
            ITexTransToolForUnity engine,
            IRendererTargeting targeting,
            Renderer[] targetRenderers,
            HashSet<Material> targetMaterials,
            UVChannel atlasTargetUvChannel,
            bool optimizeConstantTextures,
            bool disableVisuallyUnusedShaderFeatures,
            IReadOnlyCollection<Material>? excludedMaterials = null)
        {
            var original =
                MaterialGroupingContext.BuildContainsTextureDictionaries(
                    targetMaterials, atlasTargetUvChannel);

            if (!optimizeConstantTextures
                && !disableVisuallyUnusedShaderFeatures)
            {
                return new AtlasSourceOptimizationPlan(
                    original, original,
                    Array.Empty<AtlasConstantTextureDecision>(),
                    Array.Empty<AtlasFeatureDisableDecision>());
            }

            var excluded = excludedMaterials?.ToHashSet()
                ?? new HashSet<Material>();
            var slots = BuildMaterialSlots(
                targeting, targetRenderers, targetMaterials);
            var candidates = new List<Candidate>();

            foreach (var material in targetMaterials)
            {
                if (excluded.Contains(material)
                    || !original.TryGetValue(material, out var properties)
                    || !slots.TryGetValue(material, out var materialSlots)
                    || materialSlots.Count == 0)
                    continue;

                IReadOnlyDictionary<string, TextureUVUsage> usages;
                try
                {
                    usages = TTShaderTextureUsageInformationUtil
                        .GetContainsUVTransformUsage(material);
                }
                catch
                {
                    continue;
                }

                foreach (var pair in properties)
                {
                    if (pair.Value == null
                        || pair.Value.dimension != TextureDimension.Tex2D
                        || !usages.TryGetValue(pair.Key, out var usage)
                        || ((int)usage.UVChannel - 1) != (int)atlasTargetUvChannel
                        || usage.UVMatrix is not { } matrix)
                        continue;

                    candidates.Add(new Candidate(
                        material, pair.Key, pair.Value, matrix, materialSlots));
                }
            }

            var samples =
                new Dictionary<(Material material, string property), SampleResult>();

            foreach (var textureGroup in candidates.GroupBy(item => item.Texture))
            {
                if (!TryReadSourcePixels(
                        textureGroup.Key, out var pixels,
                        out var width, out var height))
                    continue;

                try
                {
                    foreach (var candidate in textureGroup)
                    {
                        if (!TryBuildCoverage(
                                candidate.Slots,
                                (int)atlasTargetUvChannel,
                                candidate.Matrix,
                                width,
                                height,
                                out var coverage))
                            continue;

                        samples[(candidate.Material, candidate.Property)] =
                            Scan(pixels, coverage);
                    }
                }
                finally
                {
                    pixels = Array.Empty<Color>();
                }
            }

            var constants = new List<AtlasConstantTextureDecision>();
            if (optimizeConstantTextures)
            {
                foreach (var candidate in candidates)
                {
                    if (!samples.TryGetValue(
                            (candidate.Material, candidate.Property),
                            out var sample)
                        || !sample.Known
                        || !sample.IsConstant)
                        continue;

                    if ((long)candidate.Texture.width
                            * candidate.Texture.height <= 16)
                        continue;

                    constants.Add(new AtlasConstantTextureDecision(
                        candidate.Material,
                        candidate.Property,
                        candidate.Texture,
                        sample.ConstantColor));
                }
            }

            var featureDisables = disableVisuallyUnusedShaderFeatures
                ? DetectFeatureDisables(targetMaterials, samples)
                : new List<AtlasFeatureDisableDecision>();

            var projected = original.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.ToDictionary(
                    property => property.Key,
                    property => property.Value)
                    as IReadOnlyDictionary<string, Texture>);

            foreach (var item in constants)
            {
                if (projected.TryGetValue(item.Material, out var readOnly))
                    ((Dictionary<string, Texture>)readOnly).Remove(item.Property);
            }

            foreach (var item in featureDisables)
            {
                if (!projected.TryGetValue(item.Material, out var readOnly))
                    continue;
                var properties = (Dictionary<string, Texture>)readOnly;
                foreach (var property in item.Rule.TextureProperties)
                    properties.Remove(property);
            }

            // A disabled feature supersedes any constant replacement belonging
            // to that feature; no 4x4 texture needs to be generated for it.
            var disabledBindings = featureDisables
                .SelectMany(item => item.Rule.TextureProperties.Select(
                    property => (item.Material, property)))
                .ToHashSet();
            constants.RemoveAll(item =>
                disabledBindings.Contains((item.Material, item.Property)));

            return new AtlasSourceOptimizationPlan(
                original, projected, constants, featureDisables);
        }

        internal static AtlasConstantTextureOutput[] CreateConstantOutputs(
            ITexTransToolForUnity engine,
            AtlasSourceOptimizationPlan plan,
            AtlasSetting atlasSetting,
            AtlasTextureExperimentalFeature? experimentalOptions,
            IReadOnlyCollection<Material> materials)
        {
            var selected = materials.ToHashSet();
            var outputs = new List<AtlasConstantTextureOutput>();

            foreach (var decision in plan.ConstantTextures
                         .Where(item => selected.Contains(item.Material)))
            {
                var created = engine.CreateRenderTexture(4, 4);
                created.Name = "AtlasConstant_" + decision.Property;
                engine.ColorFill(created, decision.Color.ToTTCore());

                var holders = TexFineTuningUtility.InitTexFineTuningHolders(
                    new[] { decision.Property });
                TexFineTuningUtility.ApplySourceMipMapDefaults(
                    new[] { decision.Material },
                    plan.OriginalTextureUses,
                    holders);

                foreach (var fineTuning in atlasSetting.TextureFineTuning)
                    fineTuning?.AddSetting(holders);
                if (experimentalOptions != null)
                    AtlasTexture.IndividualTuningAddSettings(
                        holders,
                        experimentalOptions.TextureIndividualFineTuning);

                var raw = new Dictionary<string, ITTRenderTexture>
                {
                    [decision.Property] = created,
                };
                var tuned = TexFineTuningUtility.ProcessingTextureFineTuning(
                    engine, holders, raw);

                if (!tuned.RenderTextures.TryGetValue(
                        decision.Property, out var final)
                    || !tuned.TextureDescriptors.TryGetValue(
                        final, out var descriptor))
                {
                    foreach (var texture in tuned.TextureDescriptors.Keys)
                        texture.Dispose();
                    if (!tuned.TextureDescriptors.ContainsKey(created))
                        created.Dispose();
                    continue;
                }

                foreach (var texture in tuned.TextureDescriptors.Keys
                             .Where(texture => !ReferenceEquals(texture, final))
                             .Distinct())
                    texture.Dispose();
                if (!ReferenceEquals(created, final)
                    && !tuned.TextureDescriptors.ContainsKey(created))
                    created.Dispose();

                outputs.Add(new AtlasConstantTextureOutput(
                    decision, final, descriptor));
            }

            return outputs.ToArray();
        }

        internal static void DisposeConstantOutputs(
            IEnumerable<AtlasConstantTextureOutput> outputs)
        {
            foreach (var texture in outputs
                         .Select(output => output.RenderTexture)
                         .Distinct())
                texture.Dispose();
        }

        internal static void ApplyToGeneratedMaterials(
            IReadOnlyDictionary<Material, Material> materialMap,
            IEnumerable<AtlasConstantTextureOutput> constantOutputs,
            IEnumerable<AtlasFeatureDisableDecision> featureDisables,
            ITexTransToolForUnity engine)
        {
            foreach (var output in constantOutputs)
            {
                if (!materialMap.TryGetValue(
                        output.Decision.Material, out var generated)
                    || !generated.HasShaderTextureProperty(
                        output.Decision.Property))
                    continue;

                generated.SetTexture(
                    output.Decision.Property,
                    engine.GetReferenceRenderTexture(output.RenderTexture));
            }

            foreach (var item in featureDisables)
            {
                if (!materialMap.TryGetValue(item.Material, out var generated))
                    continue;

                if (generated.HasInt(item.Rule.SwitchProperty))
                    generated.SetInt(item.Rule.SwitchProperty, 0);
                else if (generated.HasFloat(item.Rule.SwitchProperty))
                    generated.SetFloat(item.Rule.SwitchProperty, 0f);

                foreach (var property in item.Rule.TextureProperties)
                    if (generated.HasShaderTextureProperty(property))
                        generated.SetTexture(property, null);
            }
        }

        private static Dictionary<Material, HashSet<(Mesh mesh, int subMesh)>>
            BuildMaterialSlots(
                IRendererTargeting targeting,
                IEnumerable<Renderer> renderers,
                HashSet<Material> targetMaterials)
        {
            var result =
                new Dictionary<Material, HashSet<(Mesh mesh, int subMesh)>>();

            foreach (var renderer in renderers)
            {
                if (renderer == null) continue;
                var mesh = targeting.GetMesh(renderer);
                if (mesh == null || mesh.subMeshCount == 0) continue;
                var materials = targeting.GetMaterials(renderer);

                for (var slot = 0; slot < materials.Length; slot += 1)
                {
                    var material = materials[slot];
                    if (material == null
                        || !targetMaterials.Contains(material))
                        continue;

                    var subMesh = Math.Clamp(slot, 0, mesh.subMeshCount - 1);
                    if (mesh.GetSubMesh(subMesh).indexCount == 0) continue;
                    if (!result.TryGetValue(material, out var materialSlots))
                        result[material] = materialSlots =
                            new HashSet<(Mesh mesh, int subMesh)>();
                    materialSlots.Add((mesh, subMesh));
                }
            }

            return result;
        }

        private static bool TryReadSourcePixels(
            Texture texture,
            out Color[] pixels,
            out int width,
            out int height)
        {
            pixels = Array.Empty<Color>();
            width = texture.width;
            height = texture.height;

            var pixelCount = (long)width * height;
            if (width <= 0 || height <= 0
                || pixelCount <= 0
                || pixelCount > MaxSourcePixels
                || !SystemInfo.SupportsRenderTextureFormat(
                    RenderTextureFormat.ARGBFloat))
                return false;

            RenderTexture? staging = null;
            Texture2D? copy = null;
            var previous = RenderTexture.active;
            try
            {
                staging = RenderTexture.GetTemporary(
                    width, height, 0,
                    RenderTextureFormat.ARGBFloat,
                    RenderTextureReadWrite.Linear);
                Graphics.Blit(texture, staging);

                if (SystemInfo.supportsAsyncGPUReadback)
                {
                    try
                    {
                        var request = AsyncGPUReadback.Request(
                            staging, 0, TextureFormat.RGBAFloat);
                        request.WaitForCompletion();
                        if (!request.hasError)
                        {
                            var data = request.GetData<Color>();
                            if (data.Length == pixelCount)
                            {
                                pixels = data.ToArray();
                                return true;
                            }
                        }
                    }
                    catch
                    {
                        // Fall through to ReadPixels.
                    }
                }

                copy = new Texture2D(
                    width, height, TextureFormat.RGBAFloat, false, true);
                RenderTexture.active = staging;
                copy.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                var fallback = copy.GetPixels(0);
                if (fallback.Length != pixelCount) return false;
                pixels = fallback;
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                RenderTexture.active = previous;
                if (copy != null)
                    UnityEngine.Object.DestroyImmediate(copy);
                if (staging != null)
                    RenderTexture.ReleaseTemporary(staging);
            }
        }

        private static bool TryBuildCoverage(
            IEnumerable<(Mesh mesh, int subMesh)> slots,
            int uvChannel,
            Matrix2x3 matrix,
            int width,
            int height,
            out bool[] coverage)
        {
            coverage = Array.Empty<bool>();
            if (width <= 0 || height <= 0
                || (long)width * height > MaxSourcePixels)
                return false;

            coverage = new bool[width * height];
            var uvs = new List<Vector2>();
            long checks = 0;
            var any = false;

            foreach (var slot in slots)
            {
                try
                {
                    uvs.Clear();
                    slot.mesh.GetUVs(uvChannel, uvs);
                    var indices = slot.mesh.GetTriangles(slot.subMesh);
                    if (slot.mesh.GetTopology(slot.subMesh)
                            != MeshTopology.Triangles
                        || uvs.Count == 0
                        || indices.Length % 3 != 0)
                        return false;

                    for (var index = 0; index < indices.Length; index += 3)
                    {
                        var i0 = indices[index];
                        var i1 = indices[index + 1];
                        var i2 = indices[index + 2];
                        if (i0 < 0 || i1 < 0 || i2 < 0
                            || i0 >= uvs.Count || i1 >= uvs.Count
                            || i2 >= uvs.Count)
                            return false;

                        var a = Transform(uvs[i0], matrix);
                        var b = Transform(uvs[i1], matrix);
                        var c = Transform(uvs[i2], matrix);
                        if (!InsideUnitUv(a)
                            || !InsideUnitUv(b)
                            || !InsideUnitUv(c))
                            return false;

                        a = new Vector2(
                            a.x * width - 0.5f,
                            a.y * height - 0.5f);
                        b = new Vector2(
                            b.x * width - 0.5f,
                            b.y * height - 0.5f);
                        c = new Vector2(
                            c.x * width - 0.5f,
                            c.y * height - 0.5f);

                        // Always use the linear-filter support radius. This is
                        // conservative for point-sampled properties and avoids
                        // relying on shader sampler aliases not exposed by the
                        // texture-usage registry.
                        const float radius = 1f;
                        var x0 = Mathf.FloorToInt(
                            Mathf.Min(a.x, Mathf.Min(b.x, c.x)) - radius);
                        var x1 = Mathf.CeilToInt(
                            Mathf.Max(a.x, Mathf.Max(b.x, c.x)) + radius);
                        var y0 = Mathf.FloorToInt(
                            Mathf.Min(a.y, Mathf.Min(b.y, c.y)) - radius);
                        var y1 = Mathf.CeilToInt(
                            Mathf.Max(a.y, Mathf.Max(b.y, c.y)) + radius);

                        var triangleChecks =
                            (long)(x1 - x0 + 1) * (y1 - y0 + 1);
                        if (triangleChecks > MaxTrianglePixelChecks - checks)
                            return false;
                        checks += triangleChecks;

                        for (var y = y0; y <= y1; y += 1)
                        for (var x = x0; x <= x1; x += 1)
                        {
                            if (!TriangleOverlapsTexelFilter(
                                    new Vector2(x, y), a, b, c, radius))
                                continue;
                            MarkConservativeAddressing(
                                coverage, x, y, width, height);
                            any = true;
                        }
                    }
                }
                catch
                {
                    return false;
                }
            }

            return any && coverage.Any(value => value);
        }

        private static Vector2 Transform(Vector2 uv, Matrix2x3 matrix)
        {
            return new Vector2(
                matrix.M00 * uv.x + matrix.M01 * uv.y + matrix.M02,
                matrix.M10 * uv.x + matrix.M11 * uv.y + matrix.M12);
        }

        private static bool InsideUnitUv(Vector2 uv)
        {
            return IsFinite(uv.x)
                && IsFinite(uv.y)
                && uv.x >= 0f && uv.x <= 1f
                && uv.y >= 0f && uv.y <= 1f;
        }

        private static bool IsFinite(float value) =>
            !float.IsNaN(value) && !float.IsInfinity(value);

        private static void MarkConservativeAddressing(
            bool[] coverage, int x, int y, int width, int height)
        {
            Span<int> xs = stackalloc int[2];
            Span<int> ys = stackalloc int[2];
            var xCount = AddressCandidates(x, width, xs);
            var yCount = AddressCandidates(y, height, ys);

            for (var yi = 0; yi < yCount; yi += 1)
            for (var xi = 0; xi < xCount; xi += 1)
                coverage[ys[yi] * width + xs[xi]] = true;
        }

        private static int AddressCandidates(
            int value, int size, Span<int> output)
        {
            if (value >= 0 && value < size)
            {
                output[0] = value;
                return 1;
            }

            // Shader samplers can alias another texture's sampler state.
            // Include both clamp and repeat edge results instead of guessing
            // which addressing mode the shader uses.
            var clamp = Mathf.Clamp(value, 0, size - 1);
            var repeat = ((value % size) + size) % size;
            output[0] = clamp;
            if (repeat == clamp) return 1;
            output[1] = repeat;
            return 2;
        }

        internal static bool TriangleOverlapsTexelFilter(
            Vector2 pixel, Vector2 a, Vector2 b, Vector2 c, float radius)
        {
            var minX = Mathf.Min(a.x, Mathf.Min(b.x, c.x));
            var maxX = Mathf.Max(a.x, Mathf.Max(b.x, c.x));
            var minY = Mathf.Min(a.y, Mathf.Min(b.y, c.y));
            var maxY = Mathf.Max(a.y, Mathf.Max(b.y, c.y));

            if (maxX <= pixel.x - radius
                || minX >= pixel.x + radius
                || maxY <= pixel.y - radius
                || minY >= pixel.y + radius)
                return false;

            return OverlapAxis(pixel, a, b, c, radius)
                && OverlapAxis(pixel, b, c, a, radius)
                && OverlapAxis(pixel, c, a, b, radius);
        }

        private static bool OverlapAxis(
            Vector2 pixel, Vector2 a, Vector2 b, Vector2 c, float radius)
        {
            var edge = b - a;
            var normal = new Vector2(-edge.y, edge.x);
            var aa = Vector2.Dot(a, normal);
            var bb = Vector2.Dot(b, normal);
            var cc = Vector2.Dot(c, normal);
            var center = Vector2.Dot(pixel, normal);
            var projectedRadius =
                (Mathf.Abs(normal.x) + Mathf.Abs(normal.y)) * radius;

            return Mathf.Max(aa, Mathf.Max(bb, cc))
                       > center - projectedRadius
                   && Mathf.Min(aa, Mathf.Min(bb, cc))
                       < center + projectedRadius;
        }

        private static SampleResult Scan(Color[] pixels, bool[] coverage)
        {
            if (pixels.Length != coverage.Length)
                return default;

            var hasFirst = false;
            var first = default(Color);
            var constant = true;
            var rgbZero = true;
            var alphaZero = true;

            for (var index = 0; index < pixels.Length; index += 1)
            {
                if (!coverage[index]) continue;
                var pixel = pixels[index];
                if (!hasFirst)
                {
                    first = pixel;
                    hasFirst = true;
                }
                else if (constant
                         && (pixel.r != first.r
                             || pixel.g != first.g
                             || pixel.b != first.b
                             || pixel.a != first.a))
                {
                    constant = false;
                }

                if (pixel.r != 0f || pixel.g != 0f || pixel.b != 0f)
                    rgbZero = false;
                if (pixel.a != 0f)
                    alphaZero = false;
            }

            return new SampleResult(
                hasFirst,
                rgbZero,
                alphaZero,
                hasFirst && constant,
                first);
        }

        private static List<AtlasFeatureDisableDecision>
            DetectFeatureDisables(
                IEnumerable<Material> materials,
                IReadOnlyDictionary<
                    (Material material, string property), SampleResult> samples)
        {
            var result = new List<AtlasFeatureDisableDecision>();

            foreach (var material in materials)
            {
                if (!IsSupportedLilToon(material)) continue;

                bool RgbZero(string property) =>
                    samples.TryGetValue((material, property), out var sample)
                    && sample.Known && sample.RgbZero;
                bool AlphaZero(string property) =>
                    samples.TryGetValue((material, property), out var sample)
                    && sample.Known && sample.AlphaZero;
                bool Enabled(string property) =>
                    material.HasProperty(property)
                    && Mathf.RoundToInt(material.GetFloat(property)) != 0;
                bool AudioLinkTargets(string property) =>
                    material.HasProperty("_UseAudioLink")
                    && material.GetFloat("_UseAudioLink") != 0f
                    && material.HasProperty(property)
                    && material.GetFloat(property) != 0f;

                if (Enabled("_UseMatCap")
                    && RgbZero("_MatCapBlendMask"))
                {
                    result.Add(new AtlasFeatureDisableDecision(
                        material,
                        new AtlasFeatureDisableRule(
                            "MatCap1", "_UseMatCap",
                            "_MatCapTex",
                            "_MatCapBlendMask",
                            "_MatCapBumpMap")));
                }

                if (Enabled("_UseMatCap2nd")
                    && RgbZero("_MatCap2ndBlendMask"))
                {
                    result.Add(new AtlasFeatureDisableDecision(
                        material,
                        new AtlasFeatureDisableRule(
                            "MatCap2", "_UseMatCap2nd",
                            "_MatCap2ndTex",
                            "_MatCap2ndBlendMask",
                            "_MatCap2ndBumpMap")));
                }

                if (Enabled("_UseMain2ndTex")
                    && !AudioLinkTargets("_AudioLink2Main2nd")
                    && RgbZero("_Main2ndBlendMask")
                    && material.HasProperty("_Main2ndTexAlphaMode")
                    && material.GetFloat("_Main2ndTexAlphaMode") == 0f
                    && material.HasProperty("_Main2ndDissolveParams")
                    && material.GetVector("_Main2ndDissolveParams").x == 0f)
                {
                    result.Add(new AtlasFeatureDisableDecision(
                        material,
                        new AtlasFeatureDisableRule(
                            "Main2nd", "_UseMain2ndTex",
                            "_Main2ndTex",
                            "_Main2ndBlendMask",
                            "_Main2ndDissolveMask",
                            "_Main2ndDissolveNoiseMask")));
                }

                TryEmission(
                    "_UseEmission",
                    "_EmissionBlendMode",
                    "_EmissionMap",
                    "_EmissionBlendMask",
                    "_EmissionGradTex",
                    "_AudioLink2Emission",
                    "Emission1");
                TryEmission(
                    "_UseEmission2nd",
                    "_Emission2ndBlendMode",
                    "_Emission2ndMap",
                    "_Emission2ndBlendMask",
                    "_Emission2ndGradTex",
                    "_AudioLink2Emission2nd",
                    "Emission2");

                void TryEmission(
                    string switchProperty,
                    string blendModeProperty,
                    string mapProperty,
                    string maskProperty,
                    string gradProperty,
                    string audioLinkProperty,
                    string name)
                {
                    if (!Enabled(switchProperty)
                        || AudioLinkTargets(audioLinkProperty)
                        || !material.HasProperty(blendModeProperty))
                        return;

                    var mode = Mathf.RoundToInt(
                        material.GetFloat(blendModeProperty));
                    var mapZero = AlphaZero(mapProperty)
                        || ((mode == 1 || mode == 2)
                            && RgbZero(mapProperty));
                    var maskZero = AlphaZero(maskProperty)
                        || ((mode == 1 || mode == 2)
                            && RgbZero(maskProperty));
                    if (!mapZero && !maskZero) return;

                    result.Add(new AtlasFeatureDisableDecision(
                        material,
                        new AtlasFeatureDisableRule(
                            name, switchProperty,
                            mapProperty, maskProperty, gradProperty)));
                }
            }

            return result;
        }

        private static bool IsSupportedLilToon(Material material)
        {
            var shaderName = material.shader != null
                ? material.shader.name
                : string.Empty;

            return (shaderName == "lilToon"
                    || shaderName.StartsWith(
                        "Hidden/lilToon", StringComparison.Ordinal))
                   && !shaderName.Contains("Lite")
                   && !shaderName.Contains("Gem");
        }
    }
}
