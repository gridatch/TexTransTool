#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using nadena.dev.ndmf.localization;
using net.rs64.TexTransCore;
using net.rs64.TexTransCore.UVIsland;
using net.rs64.TexTransTool.TextureAtlas;
using net.rs64.TexTransTool.TextureAtlas.FineTuning;
using net.rs64.TexTransTool.Editor;
using net.rs64.TexTransTool.TextureAtlas.IslandSizePriorityTuner;
using net.rs64.TexTransTool.Utils;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.UIElements;

namespace net.rs64.TexTransTool.NDMF
{
    /// <summary>
    /// Runs after regular, explicitly configured AtlasTexture components.
    /// AutoAtlasTexture is deliberately not in the ordinary component phase list.
    /// </summary>
    internal sealed class AutoAtlasTexturePass : TTTPass<AutoAtlasTexturePass>
    {
        protected override void Execute(BuildContext context)
        {
            var configurations = AutoAtlasTextureProcessor.GetActiveConfigurations(
                context.AvatarRootObject);

            if (configurations.Length == 0) return;
            if (configurations.Length != 1)
            {
                TTTLog.Warning("AutoAtlasTexture:warn:MultipleConfigurations");
                return;
            }

            var session = TTTContext(context);
            var manualAtlases = session.PhaseAtList[TexTransPhase.Optimizing]
                .OfType<AtlasTexture>()
                .Where(atlas => TexTransBehaviorSearch.CheckIsActiveBehavior(
                    atlas, context.AvatarRootObject))
                .ToArray();
            var configuration = configurations[0];
            var animationServices =
                configuration.EnableAdditionalAtlasMerging
                && configuration.ExcludeVisibilityAnimatedItemsFromTextureMerging
                    ? context.Extension<AnimatorServicesContext>()
                    : null;
            AutoAtlasTextureProcessor.Execute(
                session.Domain,
                configuration,
                manualAtlases,
                animationServices?.AnimationIndex,
                animationServices?.ObjectPathRemapper,
                context.AvatarRootTransform);
        }
    }


    // TEMPORARY diagnostic only. Samples the pixel neighborhood of the
    // actual output-mesh UV triangles, not their bounding rectangles or the
    // whole atlas. It never modifies materials, textures or atlasing decisions.
    // Level-0 quantized samples are screening evidence, NOT lossless proof.
    internal sealed class AutoAtlasEmissionProbe
    {
        private const long MaxReadbackPixels = 8L * 1024 * 1024;
        private const long MaxRasterChecksPerObservation = 12L * 1024 * 1024;
        // Bilinear filtering at LOD 0 may sample texel centers within
        // +/-1 texel on each axis of an arbitrary point inside the UV triangle.
        private const float BilinearSupportHalfWidth = 1f;

        private long _readbackPixels;
        private long _readbackMilliseconds;
        private long _observationMilliseconds;
        private long _rasterChecks;
        private int _observedLayers;
        private int _candidateLayers;
        private int _safeFootprintCandidates;
        private int _fringeOnlyCandidates;
        private int _unknownLayers;
        private readonly List<string> _lines = new();
        private readonly Dictionary<RenderTexture, TextureSample> _textureSamples = new();
        private readonly Dictionary<RenderTexture, long?> _costs = new();
        private readonly HashSet<RenderTexture> _possibleUnusedOutputs = new();

        private sealed class TextureSample
        {
            internal string Status = "";
            internal Color32[]? Pixels;
            internal RenderTexture Texture = null!;
            internal long? OutputBytes;
        }

        private sealed class RegionResult
        {
            internal string Status = "";
            internal bool? RgbZero;
            internal bool? AlphaZero;
            internal bool? InsideRgbZero;
            internal bool? InsideAlphaZero;
            internal bool? FootprintRgbZero;
            internal bool? FootprintAlphaZero;
            internal int FringeOpaqueWhite;
            internal int FringeOtherNonzeroRgb;
            internal long? OutputBytes;
            internal RenderTexture? Texture;
            internal int CoveredPixels;
        }

        internal void Observe(
            IDomain domain,
            Renderer[] renderers,
            Mesh[] atlasedMeshes,
            Dictionary<Material, Material> materialMap,
            Dictionary<string, RenderTexture> resultingTextures,
            TexFineTuningResult tuningResult,
            AtlasContext atlasContext)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            _textureSamples.Clear();

            // Use the same mesh resolution used by the actual replacement pass.
            // Store ALL effective submeshes of each source material, including
            // repeated last-submesh slots and consumers on distinct renderers.
            var geometry = new Dictionary<Material, HashSet<(Mesh, int)>>();
            var unresolved = new HashSet<Material>();
            foreach (var renderer in renderers)
            {
                if (renderer == null) continue;
                var materials = domain.GetMaterials(renderer);
                if (!materials.Any(material => material != null
                        && materialMap.ContainsKey(material)))
                    continue;

                var resolved = AtlasTexture.ResolveAtlasedMeshForRenderer(
                    domain, renderer, atlasContext, atlasedMeshes);
                if (resolved is not { } meshPair)
                {
                    foreach (var material in materials)
                        if (material != null && materialMap.ContainsKey(material))
                            unresolved.Add(material);
                    continue;
                }

                var atlasMesh = meshPair.atlasMesh;
                for (var slot = 0; slot < materials.Length; slot++)
                {
                    var material = materials[slot];
                    if (material == null || !materialMap.ContainsKey(material))
                        continue;

                    if (atlasMesh.subMeshCount == 0)
                    {
                        unresolved.Add(material);
                        continue;
                    }
                    var effectiveSlot = Math.Min(slot, atlasMesh.subMeshCount - 1);
                    if (atlasMesh.GetSubMesh(effectiveSlot).indexCount == 0)
                        continue;

                    if (!geometry.TryGetValue(material, out var slots))
                        geometry.Add(material, slots = new HashSet<(Mesh, int)>());
                    slots.Add((atlasMesh, effectiveSlot));
                }
            }

            foreach (var entry in materialMap.OrderBy(
                         x => x.Key.name, StringComparer.Ordinal))
            {
                var original = entry.Key;
                var generated = entry.Value;
                if (original == null || generated == null || original.shader == null)
                    continue;
                var shaderName = original.shader.name;
                if (shaderName.IndexOf("lilToon", StringComparison.OrdinalIgnoreCase) < 0
                    || shaderName.IndexOf("Lite", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;

                geometry.TryGetValue(original, out var slots);
                var geometryKnown = !unresolved.Contains(original)
                    && slots != null
                    && slots.Count > 0;
                ObserveLayer(original, generated,
                    geometryKnown ? slots : null,
                    resultingTextures, tuningResult, atlasContext,
                    "_UseEmission", "_EmissionBlendMode",
                    "_EmissionMap", "_EmissionBlendMask", "1st");
                ObserveLayer(original, generated,
                    geometryKnown ? slots : null,
                    resultingTextures, tuningResult, atlasContext,
                    "_UseEmission2nd", "_Emission2ndBlendMode",
                    "_Emission2ndMap", "_Emission2ndBlendMask", "2nd");
            }
            watch.Stop();
            _observationMilliseconds += watch.ElapsedMilliseconds;
        }

        private void ObserveLayer(
            Material original, Material generated,
            HashSet<(Mesh, int)>? slots,
            IReadOnlyDictionary<string, RenderTexture> textures,
            TexFineTuningResult fineTuning,
            AtlasContext atlasContext,
            string enableProperty, string modeProperty,
            string mapProperty, string maskProperty, string layer)
        {
            if (!original.HasProperty(enableProperty)
                || original.GetFloat(enableProperty) == 0f)
                return;

            if (!textures.ContainsKey(maskProperty)
                && !textures.ContainsKey(mapProperty))
                return;

            ++_observedLayers;
            var mode = generated.HasProperty(modeProperty)
                ? Mathf.RoundToInt(generated.GetFloat(modeProperty)) : -1;

            RegionResult? map = InspectProperty(
                generated, slots, textures, fineTuning, atlasContext,
                mapProperty, false);
            RegionResult? mask = InspectProperty(
                generated, slots, textures, fineTuning, atlasContext,
                maskProperty, true);

            // Diagnostic categories are deliberately separate:
            // (1) Is the emission provably neutral at UV-interior texels?
            // (2) Do the wider bilinear-support texels ALSO remain neutral?
            // A white atlas background may affect (2), but MUST NOT silently
            // turn an observed UV-interior zero into "no candidate".
            var known = slots != null
                && (map == null || map.InsideRgbZero.HasValue)
                && (mask == null || mask.InsideRgbZero.HasValue);
            bool insideZeroAlpha = map?.InsideAlphaZero == true
                || mask?.InsideAlphaZero == true;
            bool insideZeroRgb = map?.InsideRgbZero == true
                || mask?.InsideRgbZero == true;
            bool insideCandidate = known
                && (insideZeroAlpha
                    || ((mode == 1 || mode == 2) && insideZeroRgb));
            bool footprintZeroAlpha = map?.FootprintAlphaZero == true
                || mask?.FootprintAlphaZero == true;
            bool footprintZeroRgb = map?.FootprintRgbZero == true
                || mask?.FootprintRgbZero == true;
            bool footprintCandidate = insideCandidate
                && (footprintZeroAlpha
                    || ((mode == 1 || mode == 2) && footprintZeroRgb));
            string classification;
            if (!known)
            {
                ++_unknownLayers;
                classification = "unknown";
            }
            else if (!insideCandidate)
            {
                classification = "nonzero-inside";
            }
            else
            {
                ++_candidateLayers;
                if (map?.Texture != null) _possibleUnusedOutputs.Add(map.Texture);
                if (mask?.Texture != null) _possibleUnusedOutputs.Add(mask.Texture);
                if (footprintCandidate)
                {
                    ++_safeFootprintCandidates;
                    classification = "inside-zero-and-footprint-zero";
                }
                else
                {
                    ++_fringeOnlyCandidates;
                    classification = "inside-zero-fringe-nonzero";
                }
            }

            _lines.Add(
                "material=" + original.name + ", emission=" + layer
                + ", blendMode=" + mode
                + ", triangles=" + (slots?.Count.ToString() ?? "unknown")
                + ", map=" + (map?.Status ?? "not-atlased")
                + ", mask=" + (mask?.Status ?? "not-atlased")
                + ", candidate=" + classification
                + ", atlas-output-bytes="
                + (map?.OutputBytes.GetValueOrDefault() ?? 0)
                + "+"
                + (mask?.OutputBytes.GetValueOrDefault() ?? 0));
        }

        private RegionResult? InspectProperty(
            Material material,
            HashSet<(Mesh, int)>? slots,
            IReadOnlyDictionary<string, RenderTexture> outputs,
            TexFineTuningResult fineTuning,
            AtlasContext atlasContext,
            string propertyName,
            bool mask)
        {
            if (!outputs.TryGetValue(propertyName, out var texture)
                || !material.HasProperty(propertyName)
                || !ReferenceEquals(material.GetTexture(propertyName), texture))
                return null;

            var tex = GetTextureSample(texture, fineTuning, atlasContext, propertyName);
            var result = new RegionResult
            {
                Texture = texture,
                OutputBytes = tex.OutputBytes,
            };
            if (tex.Pixels == null)
            {
                result.Status = tex.Status;
                return result;
            }
            if (slots == null)
            {
                result.Status = "no-output-UV-geometry";
                return result;
            }

            // Only UV0 affine mappings are supported in this probe. The
            // static ST transforms here match the respective lilToon paths:
            // emission texture = own ST; mask = main ST followed by mask ST.
            if (!TryGetStaticTransform(material, propertyName, mask,
                    out var scale, out var offset, out var reason))
            {
                result.Status = reason;
                return result;
            }

            if (!TryBuildCoverage(slots, scale, offset,
                    texture.width, texture.height, out var covered,
                    out var coveredCount, out var inside,
                    out var insideCount, out reason))
            {
                result.Status = reason;
                return result;
            }

            var supported = CountNonZero(tex.Pixels, covered,
                texture.width, texture.height, flippedY: false);
            var core = CountNonZero(tex.Pixels, inside,
                texture.width, texture.height, flippedY: false);
            var flippedCore = CountNonZero(tex.Pixels, inside,
                texture.width, texture.height, flippedY: true);
            var outer = new bool[covered.Length];
            for (int i = 0; i < outer.Length; i++)
                outer[i] = covered[i] && !inside[i];
            var fringe = CountNonZero(tex.Pixels, outer,
                texture.width, texture.height, flippedY: false);

            result.InsideRgbZero = core.NonzeroRgb == 0;
            result.InsideAlphaZero = core.NonzeroAlpha == 0;
            result.FootprintRgbZero = supported.NonzeroRgb == 0;
            result.FootprintAlphaZero = supported.NonzeroAlpha == 0;
            result.RgbZero = result.FootprintRgbZero;
            result.AlphaZero = result.FootprintAlphaZero;
            result.FringeOpaqueWhite = fringe.OpaqueWhite;
            result.FringeOtherNonzeroRgb = fringe.NonzeroNonWhiteRgb;
            result.CoveredPixels = coveredCount;
            result.Status = texture.width + "x" + texture.height
                + ":UV-triangles-covered=" + coveredCount + "/" + covered.Length
                + ",inside=" + insideCount
                + ",insideNonzero(R/G/B/A)=" + core.ChannelCounts()
                + ",fringeNonzero(R/G/B/A)=" + fringe.ChannelCounts()
                + ",fringeOpaqueWhite=" + fringe.OpaqueWhite
                + ",fringeOtherNonzeroRGB=" + fringe.NonzeroNonWhiteRgb
                + ",insideFlippedYNonzero(R/G/B/A)=" + flippedCore.ChannelCounts()
                + ",insideRGB" + (result.InsideRgbZero == true ? "=0" : "!=0")
                + ",insideA" + (result.InsideAlphaZero == true ? "=0" : "!=0")
                + ",supportRGB" + (result.RgbZero == true ? "=0" : "!=0")
                + ",supportA" + (result.AlphaZero == true ? "=0" : "!=0");
            return result;
        }


        private struct NonZeroCounts
        {
            public int NonzeroRed, NonzeroGreen, NonzeroBlue, NonzeroAlpha, NonzeroRgb;
            public int OpaqueWhite, NonzeroNonWhiteRgb;
            public string ChannelCounts() =>
                NonzeroRed + "/" + NonzeroGreen + "/"
                + NonzeroBlue + "/" + NonzeroAlpha;
        }

        private static NonZeroCounts CountNonZero(
            Color32[] pixels, bool[] covered, int width, int height, bool flippedY)
        {
            var counts = new NonZeroCounts();
            for (int i = 0; i < covered.Length; ++i)
            {
                if (!covered[i]) continue;
                var index = flippedY
                    ? (height - 1 - i / width) * width + i % width
                    : i;
                var pixel = pixels[index];
                if (pixel.r != 0) ++counts.NonzeroRed;
                if (pixel.g != 0) ++counts.NonzeroGreen;
                if (pixel.b != 0) ++counts.NonzeroBlue;
                if (pixel.a != 0) ++counts.NonzeroAlpha;
                bool rgbNonzero = pixel.r != 0 || pixel.g != 0 || pixel.b != 0;
                bool opaqueWhite = pixel.r == 255 && pixel.g == 255
                    && pixel.b == 255 && pixel.a == 255;
                if (rgbNonzero) ++counts.NonzeroRgb;
                if (opaqueWhite) ++counts.OpaqueWhite;
                else if (rgbNonzero) ++counts.NonzeroNonWhiteRgb;
            }
            return counts;
        }

        private static bool IsZeroScroll(Material material, string propertyName)
        {
            if (!material.HasProperty(propertyName)) return true;
            return material.GetVector(propertyName) == Vector4.zero;
        }

        private static bool TryGetStaticTransform(
            Material material, string propertyName, bool mask,
            out Vector2 scale, out Vector2 offset, out string reason)
        {
            scale = Vector2.one;
            offset = Vector2.zero;
            reason = "";

            if (mask)
            {
                // Both mask animation properties affect the shader's choice
                // of uvMain vs uv0 for BOTH emission layers.
                if (!IsZeroScroll(material, "_EmissionBlendMask_ScrollRotate")
                    || !IsZeroScroll(material, "_Emission2ndBlendMask_ScrollRotate")
                    || !IsZeroScroll(material, "_MainTex_ScrollRotate"))
                {
                    reason = "dynamic-or-rotated-mask-UV";
                    return false;
                }
                if (material.HasProperty("_MainTex"))
                {
                    var mainScale = material.GetTextureScale("_MainTex");
                    var mainOffset = material.GetTextureOffset("_MainTex");
                    scale = Vector2.Scale(scale, mainScale);
                    offset = Vector2.Scale(offset, mainScale) + mainOffset;
                }
            }
            else
            {
                var uvModeProp = propertyName == "_EmissionMap"
                    ? "_EmissionMap_UVMode" : "_Emission2ndMap_UVMode";
                var parallaxProp = propertyName == "_EmissionMap"
                    ? "_EmissionParallaxDepth" : "_Emission2ndParallaxDepth";
                if ((material.HasProperty(uvModeProp)
                        && (material.HasInt(uvModeProp)
                            ? material.GetInt(uvModeProp) != 0
                            : material.GetFloat(uvModeProp) != 0f))
                    || (material.HasProperty(parallaxProp)
                        && material.GetFloat(parallaxProp) != 0f)
                    || !IsZeroScroll(material, propertyName + "_ScrollRotate"))
                {
                    reason = "non-UV0-or-dynamic-emission-UV";
                    return false;
                }
            }

            var propertyScale = material.GetTextureScale(propertyName);
            var propertyOffset = material.GetTextureOffset(propertyName);
            offset = Vector2.Scale(offset, propertyScale) + propertyOffset;
            scale = Vector2.Scale(scale, propertyScale);
            if (float.IsNaN(scale.x) || float.IsInfinity(scale.x)
                || float.IsNaN(scale.y) || float.IsInfinity(scale.y)
                || float.IsNaN(offset.x) || float.IsInfinity(offset.x)
                || float.IsNaN(offset.y) || float.IsInfinity(offset.y))
            {
                reason = "invalid-UV-transform";
                return false;
            }
            return true;
        }

        private bool TryBuildCoverage(
            HashSet<(Mesh, int)> slots,
            Vector2 scale, Vector2 offset,
            int width, int height,
            out bool[] coverage, out int coveredCount,
            out bool[] inside, out int insideCount, out string reason)
        {
            coverage = Array.Empty<bool>();
            inside = Array.Empty<bool>();
            coveredCount = 0;
            insideCount = 0;
            reason = "";
            if (width <= 0 || height <= 0 || (long)width * height > MaxReadbackPixels)
            {
                reason = "texture-size-limit";
                return false;
            }

            coverage = new bool[width * height];
            inside = new bool[width * height];
            var uvs = new List<Vector2>();
            bool encounteredTriangle = false;
            foreach (var slot in slots)
            {
                try
                {
                    uvs.Clear();
                    slot.Item1.GetUVs(0, uvs);
                    var indices = slot.Item1.GetTriangles(slot.Item2);
                    if (uvs.Count == 0 || indices.Length % 3 != 0)
                    {
                        reason = "missing-UV0-or-invalid-triangles";
                        return false;
                    }

                    for (var t = 0; t < indices.Length; t += 3)
                    {
                        if (indices[t] >= uvs.Count || indices[t+1] >= uvs.Count
                            || indices[t+2] >= uvs.Count
                            || indices[t] < 0 || indices[t+1] < 0 || indices[t+2] < 0)
                        {
                            reason = "triangle-index-out-of-range";
                            return false;
                        }
                        encounteredTriangle = true;
                        var a = Vector2.Scale(uvs[indices[t]], scale) + offset;
                        var b = Vector2.Scale(uvs[indices[t+1]], scale) + offset;
                        var c = Vector2.Scale(uvs[indices[t+2]], scale) + offset;
                        if (!ValidUV(a) || !ValidUV(b) || !ValidUV(c))
                        {
                            reason = "UV-outside-0-to-1-or-invalid";
                            return false;
                        }

                        // Work in output-texel coordinates. Keep the exact
                        // triangle, then test mip-0 bilinear texel support
                        // against a 2x2 axis-aligned sampling neighborhood.
                        a = new Vector2(a.x*width-0.5f, a.y*height-0.5f);
                        b = new Vector2(b.x*width-0.5f, b.y*height-0.5f);
                        c = new Vector2(c.x*width-0.5f, c.y*height-0.5f);

                        int x0 = Math.Max(0, Mathf.FloorToInt(Mathf.Min(a.x,b.x,c.x) - BilinearSupportHalfWidth));
                        int x1 = Math.Min(width - 1, Mathf.CeilToInt(Mathf.Max(a.x,b.x,c.x) + BilinearSupportHalfWidth));
                        int y0 = Math.Max(0, Mathf.FloorToInt(Mathf.Min(a.y,b.y,c.y) - BilinearSupportHalfWidth));
                        int y1 = Math.Min(height - 1, Mathf.CeilToInt(Mathf.Max(a.y,b.y,c.y) + BilinearSupportHalfWidth));
                        var checks = (long)(x1 - x0 + 1) * (y1 - y0 + 1);
                        if (checks > MaxRasterChecksPerObservation - _rasterChecks)
                        {
                            reason = "triangle-raster-budget";
                            return false;
                        }
                        _rasterChecks += checks;
                        for (var y = y0; y <= y1; ++y)
                            for (var x = x0; x <= x1; ++x)
                            {
                                int index = y*width+x;
                                var texelCenter = new Vector2(x,y);
                                if (!inside[index]
                                    && NearTriangle(texelCenter, a,b,c, 0f))
                                {
                                    inside[index] = true;
                                    ++insideCount;
                                }
                                if (!coverage[index]
                                    && (inside[index]
                                        || TriangleIntersectsBilinearSupport(
                                            texelCenter, a, b, c)))
                                {
                                    coverage[index] = true;
                                    ++coveredCount;
                                }
                            }
                    }
                }
                catch (Exception ex)
                {
                    reason = "mesh-read-error:" + ex.GetType().Name;
                    return false;
                }
            }
            if (!encounteredTriangle || coveredCount == 0)
            {
                reason = "no-covered-UV-triangles";
                return false;
            }
            return true;
        }

        private static bool ValidUV(Vector2 uv) =>
            !float.IsNaN(uv.x) && !float.IsInfinity(uv.x)
            && !float.IsNaN(uv.y) && !float.IsInfinity(uv.y)
            && uv.x >= 0f && uv.x <= 1f && uv.y >= 0f && uv.y <= 1f;

        private static bool NearTriangle(
            Vector2 p, Vector2 a, Vector2 b, Vector2 c, float radius)
        {
            var ab = Cross(b-a,p-a);
            var bc = Cross(c-b,p-b);
            var ca = Cross(a-c,p-c);
            var inside = (ab >= 0f && bc >= 0f && ca >= 0f)
                         || (ab <= 0f && bc <= 0f && ca <= 0f);
            if (inside) return true;
            var r2 = radius*radius;
            return SegmentDistance2(p,a,b) <= r2
                   || SegmentDistance2(p,b,c) <= r2
                   || SegmentDistance2(p,c,a) <= r2;
        }

        // Exact triangle / axis-aligned square overlap by the Separating
        // Axis Theorem. This represents the mip-0 bilinear sampling support:
        // a texel may contribute only if some UV sample inside the triangle
        // is within 1 pixel in BOTH X and Y from that texel center. A
        // Euclidean circle around the triangle overcounts this footprint.
        private static bool TriangleIntersectsBilinearSupport(
            Vector2 center, Vector2 a, Vector2 b, Vector2 c)
        {
            float minX = Mathf.Min(a.x, b.x, c.x);
            float maxX = Mathf.Max(a.x, b.x, c.x);
            float minY = Mathf.Min(a.y, b.y, c.y);
            float maxY = Mathf.Max(a.y, b.y, c.y);
            if (maxX < center.x - BilinearSupportHalfWidth
                || minX > center.x + BilinearSupportHalfWidth
                || maxY < center.y - BilinearSupportHalfWidth
                || minY > center.y + BilinearSupportHalfWidth)
                return false;

            return OverlapsEdgeAxis(center, a, b, c)
                && OverlapsEdgeAxis(center, b, c, a)
                && OverlapsEdgeAxis(center, c, a, b);
        }

        private static bool OverlapsEdgeAxis(
            Vector2 center, Vector2 a, Vector2 b, Vector2 c)
        {
            var edge = b - a;
            var axis = new Vector2(-edge.y, edge.x);
            var pa = Vector2.Dot(a, axis);
            var pb = Vector2.Dot(b, axis);
            var pc = Vector2.Dot(c, axis);
            var projectedCenter = Vector2.Dot(center, axis);
            var projectedRadius =
                (Mathf.Abs(axis.x) + Mathf.Abs(axis.y))
                * BilinearSupportHalfWidth;
            return Mathf.Max(pa, pb, pc) >= projectedCenter - projectedRadius
                && Mathf.Min(pa, pb, pc) <= projectedCenter + projectedRadius;
        }

        private static float Cross(Vector2 a, Vector2 b) =>
            a.x*b.y - a.y*b.x;

        private static float SegmentDistance2(Vector2 p, Vector2 a, Vector2 b)
        {
            var ab = b-a;
            var len2 = ab.sqrMagnitude;
            var u = len2 <= 1e-12f ? 0f
                : Mathf.Clamp01(Vector2.Dot(p-a,ab)/len2);
            return (p-(a+ab*u)).sqrMagnitude;
        }

        private TextureSample GetTextureSample(
            RenderTexture texture, TexFineTuningResult tuningResult,
            AtlasContext atlasContext, string propertyName)
        {
            if (_textureSamples.TryGetValue(texture, out var cached))
                return cached;

            var sample = new TextureSample { Texture = texture };
            _textureSamples.Add(texture, sample);
            sample.OutputBytes = EstimateOutputBytes(
                tuningResult, atlasContext, propertyName);
            _costs[texture] = sample.OutputBytes;

            var pixels = (long)texture.width * texture.height;
            if (pixels <= 0 || pixels > MaxReadbackPixels - _readbackPixels)
            {
                sample.Status = "readback-budget-skip";
                return sample;
            }
            if (!SystemInfo.supportsAsyncGPUReadback)
            {
                sample.Status = "readback-unsupported";
                return sample;
            }

            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var request = AsyncGPUReadback.Request(
                    texture, 0, TextureFormat.RGBA32);
                request.WaitForCompletion();
                if (request.hasError)
                {
                    sample.Status = "readback-error";
                    return sample;
                }
                sample.Pixels = request.GetData<Color32>().ToArray();
                if (sample.Pixels.Length != pixels)
                {
                    sample.Pixels = null;
                    sample.Status = "readback-size-mismatch";
                    return sample;
                }
                sample.Status = "readback-ok";
                return sample;
            }
            catch (Exception ex)
            {
                sample.Status = "readback-exception:" + ex.GetType().Name;
                return sample;
            }
            finally
            {
                watch.Stop();
                _readbackPixels += pixels;
                _readbackMilliseconds += watch.ElapsedMilliseconds;
            }
        }

        private static long? EstimateOutputBytes(
            TexFineTuningResult tuningResult,
            AtlasContext atlasContext,
            string propertyName)
        {
            if (!tuningResult.RenderTextures.TryGetValue(
                    propertyName, out var source)
                || !tuningResult.TextureDescriptors.TryGetValue(
                    source, out var descriptor))
                return null;

            var references = tuningResult.RenderTextures
                .Where(entry => ReferenceEquals(entry.Value, source))
                .ToArray();
            var filtered = new TexFineTuningResult(
                references.ToDictionary(entry => entry.Key, entry => entry.Value),
                new Dictionary<ITTRenderTexture, TexTransToolTextureDescriptor>
                {
                    { source, descriptor },
                },
                references.ToDictionary(
                    entry => entry.Key,
                    entry => tuningResult.SourceProperties.TryGetValue(
                            entry.Key, out var names)
                        ? names
                        : (IReadOnlyCollection<string>)Array.Empty<string>()));

            return AtlasTexture.TryCalculateFinalTextureMemory(
                    atlasContext, filtered, out var bytes)
                ? bytes
                : (long?)null;
        }

        internal string Report()
        {
            var project = new System.IO.DirectoryInfo(Application.dataPath)
                .Parent?.Name ?? "(unknown)";
            long possibleBytes = 0;
            int unknownOutputs = 0;
            foreach (var rt in _possibleUnusedOutputs)
            {
                if (_costs.TryGetValue(rt, out var bytes) && bytes.HasValue)
                    possibleBytes += bytes.Value;
                else
                    ++unknownOutputs;
            }
            return "[Emission UV triangle probe: TEMPORARY; no assets modified]"
                + "\nUnity project: " + project
                + "\nLayers observed: " + _observedLayers
                + "\nUV-interior zero-emission candidates: " + _candidateLayers
                + "\nOf which footprint also zero at mip0: " + _safeFootprintCandidates
                + "\nOf which nonzero exists only in fringe: " + _fringeOnlyCandidates
                + "\nUnknown layers: " + _unknownLayers
                + "\nGPU readback pixels: " + _readbackPixels
                + "\nGPU readback elapsed: " + _readbackMilliseconds + "ms"
                + "\nProbe observation elapsed: " + _observationMilliseconds + "ms"
                + "\nTriangle pixel checks: " + _rasterChecks
                + "\nUV-interior-candidate output memory UPPER BOUND: "
                + AtlasTexture.FormatTextureMemory(possibleBytes)
                + (unknownOutputs == 0 ? "" : " (unknown outputs: " + unknownOutputs + ")")
                + "\nLimits: UV0 triangles plus geometric mip0 bilinear support;"
                + " no arbitrary pixel-count/percentage tolerance."
                + " inside/fringe counts distinguish UV interior from padding."
                + " fringeOpaqueWhite counts exact RGBA(255,255,255,255) texels;"
                + " such texels MAY originate from white atlas background,"
                + " but their provenance is not established by color alone."
                + " FlippedY counts are diagnostic only, NOT used to classify."
                + " Only static affine mask/emission UV; output readback RGBA32."
                + " Mipmap, compression, wrap beyond [0,1], material animation"
                + " and other shader side effects NOT proven; candidates"
                + " are NOT safe-to-delete decisions or realized savings."
                + "\n"
                + (_lines.Count == 0
                    ? "No transformed lilToon emission textures found."
                    : string.Join("\n", _lines));
        }
    }

    internal sealed class AutoAtlasEmissionProbeError : SimpleError
    {
        private readonly string _message;
        internal AutoAtlasEmissionProbeError(string message)
        {
            _message = message;
        }

        public override Localizer Localizer => TTTLog.NDMFLocalizer;
        public override ErrorSeverity Severity => ErrorSeverity.Information;
        public override string TitleKey =>
            "AutoAtlasTexture:info:EmissionProbeTitle";
        public override string[] TitleSubst => Array.Empty<string>();

        public override VisualElement CreateVisualElement(ErrorReport report)
        {
            var root = new VisualElement();
            root.style.flexGrow = 1;
            var scroll = new ScrollView(ScrollViewMode.VerticalAndHorizontal);
            scroll.style.height = 380;
            scroll.style.minWidth = 0;
            // Receive the event BEFORE ScrollView's own trickle-down handler.
            // A contentViewport callback runs too late on some Unity versions:
            // ScrollView has already rerouted the vertical wheel horizontally.
            var scrollHost = new VisualElement();
            scrollHost.style.minWidth = 0;
            scrollHost.RegisterCallback<WheelEvent>(evt =>
            {
                if (evt.shiftKey || Mathf.Abs(evt.delta.y) < 0.001f
                    || scroll.verticalScroller.highValue >
                       scroll.verticalScroller.lowValue + 0.5f)
                    return;
                evt.PreventDefault();
                evt.StopImmediatePropagation();
            }, TrickleDown.TrickleDown);
            scrollHost.Add(scroll);
            var lines = new VisualElement();
            foreach (var line in _message.Split(
                         new[] { '\n' }, StringSplitOptions.None))
            {
                var label = new Label(line) { enableRichText = false };
                label.style.whiteSpace = WhiteSpace.NoWrap;
                if (line.Length == 0)
                    label.style.minHeight = EditorGUIUtility.singleLineHeight;
                lines.Add(label);
            }
            scroll.Add(lines);
            root.Add(scrollHost);
            var copy = new Button(() =>
                EditorGUIUtility.systemCopyBuffer = _message)
            {
                text = "AutoAtlasTexture:button:EmissionProbeCopy".GetLocalize(),
            };
            root.Add(copy);
            return root;
        }
    }

    /// <summary>
    /// Detects visibility animation on renderers and their parent GameObjects.
    /// This only determines whether a property can take a value different from
    /// its current one; it does not infer runtime usage frequency or menu reachability.
    /// </summary>
    internal static class AutoAtlasTextureVisibilityAnimationDetector
    {
        internal static HashSet<Renderer> Detect(
            AnimationIndex animationIndex,
            ObjectPathRemapper pathRemapper,
            Transform avatarRoot,
            IEnumerable<Renderer> renderers)
        {
            return Detect(
                animationIndex,
                avatarRoot,
                renderers,
                transform =>
                {
                    // An NDMF ReplaceObject can map multiple historical animation
                    // paths to the same current object. Inspect every alias.
                    // Register new objects first, just as the original lookup did.
                    pathRemapper.GetVirtualPathForObject(transform);
                    return pathRemapper.GetAllPathsForObject(transform);
                });
        }

        // Allows tests to supply deterministic object paths without requiring
        // a live NDMF BuildContext.
        internal static HashSet<Renderer> Detect(
            AnimationIndex animationIndex,
            Transform avatarRoot,
            IEnumerable<Renderer> renderers,
            Func<Transform, IEnumerable<string>> pathsForObject)
        {
            var animatedRenderers = new HashSet<Renderer>();
            var gameObjectVisibility = new Dictionary<Transform, bool>();

            foreach (var renderer in renderers)
            {
                if (renderer == null) continue;

                var transform = renderer.transform;
                var cursor = transform;
                var insideAvatar = false;
                while (cursor != null)
                {
                    if (cursor == avatarRoot)
                    {
                        insideAvatar = true;
                        break;
                    }
                    cursor = cursor.parent;
                }
                if (!insideAvatar) continue;

                if (HasAnimatedVisibility(
                        animationIndex, pathsForObject(transform),
                        renderer.GetType(), "m_Enabled", renderer.enabled))
                {
                    animatedRenderers.Add(renderer);
                    continue;
                }

                for (cursor = transform; cursor != null; cursor = cursor.parent)
                {
                    if (!gameObjectVisibility.TryGetValue(cursor, out var animated))
                    {
                        animated = HasAnimatedVisibility(
                            animationIndex, pathsForObject(cursor),
                            typeof(GameObject), "m_IsActive",
                            cursor.gameObject.activeSelf);
                        gameObjectVisibility.Add(cursor, animated);
                    }

                    if (animated)
                    {
                        animatedRenderers.Add(renderer);
                        break;
                    }

                    if (cursor == avatarRoot) break;
                }
            }

            return animatedRenderers;
        }

        private static bool HasAnimatedVisibility(
            AnimationIndex index, IEnumerable<string> paths,
            Type componentType, string propertyName, bool currentValue)
        {
            foreach (var path in paths.Distinct(StringComparer.Ordinal))
            {
                foreach (var clip in index.GetClipsForObjectPath(path))
                {
                    foreach (var binding in clip.GetFloatCurveBindings())
                    {
                        if (binding.path != path
                            || binding.propertyName != propertyName
                            || binding.type == null
                            || !binding.type.IsAssignableFrom(componentType)
                            || (propertyName == "m_IsActive"
                                ? binding.type != typeof(GameObject)
                                : !typeof(Renderer).IsAssignableFrom(binding.type)))
                            continue;

                        if (CurveChangesVisibility(
                                clip.GetFloatCurve(binding), currentValue))
                            return true;
                    }
                }
            }

            return false;
        }

        internal static bool CurveChangesVisibility(
            AnimationCurve? curve, bool currentValue)
        {
            if (curve == null) return false;
            // Boolean animation properties are encoded as float 0 or 1.
            // Curves that keep the current state must not exclude an item.
            return curve.keys.Any(key =>
                !float.IsNaN(key.value)
                && !float.IsInfinity(key.value)
                && (key.value >= 0.5f) != currentValue);
        }
    }

    internal static class AutoAtlasTextureProcessor
    {
        internal sealed class ExecutionGroup
        {
            internal readonly HashSet<Material> Materials;
            internal readonly Renderer[] Renderers;
            internal readonly HashSet<Texture> SourceTextures;
            internal readonly HashSet<Texture> RetainedSourceTextures;
            internal readonly AutoAtlasTextureMergePlanner.MergeSelection? MergeSelection;

            internal ExecutionGroup(
                HashSet<Material> materials,
                Renderer[] renderers,
                HashSet<Texture> sourceTextures,
                HashSet<Texture> retainedSourceTextures,
                AutoAtlasTextureMergePlanner.MergeSelection? mergeSelection)
            {
                Materials = materials;
                Renderers = renderers;
                SourceTextures = sourceTextures;
                RetainedSourceTextures = retainedSourceTextures;
                MergeSelection = mergeSelection;
            }
        }

        internal static Queue<ExecutionGroup> CreateExecutionGroups(
            IReadOnlyList<AutoAtlasTextureMergePlanner.PreparedGroup> preparedGroups,
            IReadOnlyList<AutoAtlasTextureMergePlanner.MergeSelection> selections)
        {
            var selectedIds = selections.SelectMany(selection => selection.GroupIds)
                .ToHashSet();
            var queue = new Queue<ExecutionGroup>();

            foreach (var selection in selections)
            {
                var selected = selection.GroupIds
                    .Select(id => preparedGroups[id])
                    .ToArray();
                queue.Enqueue(new ExecutionGroup(
                    selected.SelectMany(group => group.Materials).ToHashSet(),
                    selected.SelectMany(group => group.Renderers).Distinct().ToArray(),
                    selected.SelectMany(group => group.SourceTextures).ToHashSet(),
                    selected.SelectMany(group => group.RetainedSourceTextures).ToHashSet(),
                    selection));
            }

            foreach (var prepared in preparedGroups.Where(
                         group => !selectedIds.Contains(group.Id)))
            {
                queue.Enqueue(new ExecutionGroup(
                    prepared.Materials,
                    prepared.Renderers,
                    prepared.SourceTextures,
                    prepared.RetainedSourceTextures,
                    null));
            }

            return queue;
        }

        internal static void EnqueueStandaloneFallback(
            Queue<ExecutionGroup> queue,
            AutoAtlasTextureMergePlanner.MergeSelection failedMerge,
            IReadOnlyList<AutoAtlasTextureMergePlanner.PreparedGroup> preparedGroups)
        {
            foreach (var id in failedMerge.GroupIds)
            {
                var prepared = preparedGroups[id];
                queue.Enqueue(new ExecutionGroup(
                    prepared.Materials,
                    prepared.Renderers,
                    prepared.SourceTextures,
                    prepared.RetainedSourceTextures,
                    null));
            }
        }

        private static string FormatLocalized(
            string key,
            params object[] args)
        {
            return string.Format(key.GetLocalize(), args);
        }

        internal static AutoAtlasTexture[] GetActiveConfigurations(GameObject root)
        {
            return root.GetComponentsInChildren<AutoAtlasTexture>(true)
                .Where(component => component != null &&
                    TexTransBehaviorSearch.CheckIsActive(component.gameObject, root))
                .ToArray();
        }

        internal static List<Renderer> GetAutoAtlasAllowedRenderers(
            IRendererTargeting domain, AutoAtlasTexture configuration)
        {
            // AutoAtlasTexture adds automatic material selection and lossless
            // sizing on top of AtlasTexture. Renderer eligibility must therefore
            // follow AtlasTexture's normal default target resolution instead of
            // inventing a broader "include disabled" rule.
            var excludedRenderers = domain
                .GetDomainsRenderers(configuration.ExcludedRenderers)
                .ToHashSet();
            return AtlasTexture.GetAtlasAllowedRenderers(
                    domain,
                    domain.EnumerateRenderer(),
                    includeDisabledRenderer: false)
                .Where(renderer => !excludedRenderers.Contains(renderer))
                .ToList();
        }

        internal static Renderer[] ResolveAutoAtlasTargetRenderers(
            IRendererTargeting domain,
            AutoAtlasTexture configuration,
            HashSet<Material> targetMaterials)
        {
            var allowedRenderers =
                GetAutoAtlasAllowedRenderers(domain, configuration);
            var targetRenderers = AtlasTexture.FilterTargetRenderers(
                domain, allowedRenderers, targetMaterials);
            return AtlasTexture.FilterExistUVChannel(
                domain, targetRenderers, UVChannel.UV0);
        }

        internal static Renderer[] GetAAONegotiationRenderers(
            IRendererTargeting domain, AutoAtlasTexture configuration)
        {
            return GetAutoAtlasAllowedRenderers(domain, configuration)
                .ToArray();
        }

        internal static void Execute(
            IDomain domain,
            AutoAtlasTexture configuration,
            IReadOnlyCollection<AtlasTexture> manualAtlases,
            AnimationIndex? animationIndex = null,
            ObjectPathRemapper? objectPathRemapper = null,
            Transform? avatarRoot = null)
        {
            var processingTimer = System.Diagnostics.Stopwatch.StartNew();

            // Store only the NDMF console summary for this build.
            var completed = new List<string>();

            // Temporary emission-content survey; never alters atlas selection.
            var emissionProbe = new AutoAtlasEmissionProbe();

            var twoDimensionalFallbackReasons = new List<string>();

            string OriginalMaterialName(Material material)
            {
                // Reuse NDMF's object identity; build-stage suffixes are not
                // stripped or guessed from the material name.
                var identity = ObjectRegistry.GetReference(material);
                if (identity?.Object is Material source && source != null) return source.name;
                // ObjectReference snapshots its display name at creation; this
                // remains available even if the original Unity object was destroyed.
                return identity?.ToString() ?? material.name;
            }

            var allRenderers = domain.EnumerateRenderer()
                .Where(renderer => renderer != null)
                .ToArray();

            // Auto-select materials from the same renderer population that a
            // normal AtlasTexture with its default IncludeDisabledRenderer=false
            // would use. Additional referenced materials are still inspected
            // separately by the source-texture exclusivity guard.
            var excludedRenderers = domain
                .GetDomainsRenderers(configuration.ExcludedRenderers)
                .ToHashSet();
            var atlasAllowedRenderers =
                GetAutoAtlasAllowedRenderers(domain, configuration);
            var candidateMaterials = atlasAllowedRenderers
                .SelectMany(domain.GetMaterials)
                .UOfType<Material>()
                .ToHashSet();
            var allReferencedMaterials = domain.GetAllMaterials();
            if (candidateMaterials.Count == 0)
            {
                processingTimer.Stop();
                TTTLog.ReportingObject(configuration, () =>
                    TTTLog.Info(
                        "AutoAtlasTexture:info:TargetNotFound",
                        processingTimer.ElapsedMilliseconds));
                return;
            }

            // Material grouping remains shader/property-aware, but it is now
            // scoped to the same renderer-selected materials as AtlasTexture.
            var textureUsages = new MaterialGroupingContext(
                candidateMaterials,
                UVChannel.UV0,
                primaryTexturePropertyOrMaximum: null);
            var propertyTextures = textureUsages.ContainsTextureDictionaries;

            var excludedMaterials = domain.GetDomainsMaterialsHashSet(
                configuration.ExcludedMaterials);
            var mergingExcludedRenderers = domain
                .GetDomainsRenderers(configuration.TextureMergingExcludedRenderers)
                .ToHashSet();
            var mergingExcludedMaterials = domain.GetDomainsMaterialsHashSet(
                configuration.TextureMergingExcludedMaterials);
            var animatedVisibilityRenderers =
                configuration.EnableAdditionalAtlasMerging
                && configuration.ExcludeVisibilityAnimatedItemsFromTextureMerging
                && animationIndex != null
                && objectPathRemapper != null
                && avatarRoot != null
                    ? AutoAtlasTextureVisibilityAnimationDetector.Detect(
                        animationIndex, objectPathRemapper, avatarRoot,
                        allRenderers)
                    : new HashSet<Renderer>();

            // Resolve manual AtlasTexture targets through TTT's existing lookup
            // rather than rescanning hierarchy and comparing raw user selections.
            var manuallySelectedMaterials = manualAtlases
                .SelectMany(atlas => atlas.GetTargetMaterials(domain,
                    AtlasTexture.GetAtlasAllowedRenderers(domain,
                        domain.EnumerateRenderer(), atlas.AtlasSetting.IncludeDisabledRenderer)))
                .ToHashSet();

            var settings = new AtlasSetting
            {
                AutoAtlasTextureSize = true,
                UsePrimaryMaximumTexture = true,
                IncludeDisabledRenderer = false,
                AtlasTargetUVChannel = UVChannel.UV0,
                IslandPadding = Mathf.Clamp(configuration.IslandPadding, 0f, 0.05f),
            };
            var groups = MakeTextureConnectedGroups(
                candidateMaterials, propertyTextures);
            var preparedGroups =
                new List<AutoAtlasTextureMergePlanner.PreparedGroup>();

            foreach (var group in groups)
            {
                if (group.Any(material => excludedMaterials.Contains(material)
                    || manuallySelectedMaterials.Contains(material)))
                {
                    continue;
                }

                // Preserve the explicit AutoAtlas renderer exclusion contract.
                // A shared material would otherwise be replaced globally even
                // though the user explicitly excluded one of its consumers.
                var explicitlyExcludedConsumers = excludedRenderers
                    .Where(renderer => domain.GetMaterials(renderer)
                        .UOfType<Material>()
                        .Any(group.Contains))
                    .ToArray();
                if (explicitlyExcludedConsumers.Length != 0)
                {
                    continue;
                }

                // Resolve renderers through the same AtlasTexture-equivalent
                // path that execution revalidation uses.
                var atlasRenderers = ResolveAutoAtlasTargetRenderers(
                    domain, configuration, group);
                if (atlasRenderers.Length == 0)
                {
                    continue;
                }

                // Material replacement is domain-wide. A currently
                // active consumer that cannot receive the corresponding UV
                // rewrite would otherwise get an atlased material on its
                // original mesh. Disabled/inactive consumers remain outside
                // this check to preserve AtlasTexture's default targeting.
                if (HasActiveUntransformedMaterialConsumer(
                        domain,
                        group,
                        atlasRenderers))
                {
                    continue;
                }

                var texturesToReplace = group
                    .SelectMany(material => propertyTextures[material].Values)
                    .Where(texture => texture != null)
                    .ToHashSet();
                if (texturesToReplace.Count == 0)
                {
                    continue;
                }

                var retainedSourceTextures =
                    FindRetainedSourceTextures(
                        allReferencedMaterials,
                        group,
                        propertyTextures,
                        texturesToReplace);

                preparedGroups.Add(new AutoAtlasTextureMergePlanner.PreparedGroup
                {
                    Id = preparedGroups.Count,
                    Materials = group,
                    Renderers = atlasRenderers,
                    SourceTextures = texturesToReplace,
                    RetainedSourceTextures = retainedSourceTextures,
                    OriginalPixels = texturesToReplace.Sum(texture =>
                        (long)texture.width * texture.height),
                    ExcludedFromTextureMerging = IsExcludedFromTextureMerging(
                        group, atlasRenderers, mergingExcludedMaterials,
                        mergingExcludedRenderers)
                        || atlasRenderers.Any(animatedVisibilityRenderers.Contains),
                });
            }

            var mergePlan = AutoAtlasTextureMergePlanner.Plan(
                domain,
                configuration,
                settings,
                propertyTextures,
                preparedGroups);

            var executionGroups = CreateExecutionGroups(
                preparedGroups, mergePlan.Selections);

            var materialBlock = "AutoAtlasTexture:info:MaterialBlock".GetLocalize();
            while (executionGroups.Count != 0)
            {
                var execution = executionGroups.Dequeue();
                var group = execution.Materials;

                // Revalidate against the exact same renderer-targeting rule
                // used during planning. Do not broaden execution to every
                // domain-wide material consumer; AutoAtlas is required to
                // preserve AtlasTexture's normal target semantics.
                var atlasRenderers = ResolveAutoAtlasTargetRenderers(
                    domain, configuration, group);
                var plannedRenderers = execution.Renderers
                    .Where(renderer => renderer != null)
                    .Distinct()
                    .ToHashSet();

                var hasActiveUntransformedConsumer =
                    HasActiveUntransformedMaterialConsumer(
                        domain,
                        group,
                        atlasRenderers);
                if (atlasRenderers.Length == 0
                    || !plannedRenderers.SetEquals(atlasRenderers)
                    || hasActiveUntransformedConsumer)
                {
                    if (execution.MergeSelection is { } invalidMerge)
                    {
                        var reason = hasActiveUntransformedConsumer
                            ? FormatLocalized(
                                "AutoAtlasTexture:fallback:ActiveUntransformedConsumer")
                            : FormatLocalized(
                                "AutoAtlasTexture:fallback:RendererSetChanged");
                        twoDimensionalFallbackReasons.Add(
                            $"groups={string.Join(",", invalidMerge.GroupIds.Select(id => "g" + id))}: "
                            + reason);
                        EnqueueStandaloneFallback(
                            executionGroups, invalidMerge, preparedGroups);
                    }
                    continue;
                }

                var originalPixels = execution.SourceTextures.Sum(texture =>
                    (long)texture.width * texture.height);

                var engine = domain.GetTexTransCoreEngineForUnity();

                IReadOnlyDictionary<Material, int>? mergeAtomicGroupByMaterial = null;
                if (execution.MergeSelection is { } selectedMerge)
                {
                    mergeAtomicGroupByMaterial = selectedMerge.GroupIds
                        .Select(id => preparedGroups[id])
                        .SelectMany(group => group.Materials.Select(
                            material => (material, group.Id)))
                        .ToDictionary(pair => pair.material, pair => pair.Id);
                }

                AutoAtlas2DMergeIslandRelocator? twoDimensionalRelocator = null;
                string? twoDimensionalSizeRejection = null;
                var executionMetrics = default(AtlasTexture.AtlasExecutionMetrics);
                var atlasResult = AtlasTexture.DoAtlasTexture(
                    domain,
                    engine,
                    group,
                    atlasRenderers,
                    new List<IIslandSizePriorityTuner?>(),
                    settings,
                    new AtlasTexture.AtlasExecutionOptions
                    {
                        RequireLossless = true,
                        ReportProgressInfo = false,
                        IslandRelocatorFactory = mergeAtomicGroupByMaterial != null
                            ? atlasContext =>
                            {
                                if (execution.MergeSelection?.PackedRegions
                                    is { } packedRegions)
                                {
                                    twoDimensionalRelocator =
                                        new AutoAtlas2DMergeIslandRelocator(
                                            atlasContext,
                                            mergeAtomicGroupByMaterial,
                                            packedRegions,
                                            execution.MergeSelection.PlannedLayoutSize.x);
                                    return twoDimensionalRelocator;
                                }

                                throw new InvalidOperationException(
                                    "Production merge candidates must have 2D packed regions.");
                            }
                            : null,
                        ForcedAutoAtlasWidth =
                            execution.MergeSelection?.PackedRegions != null
                                ? execution.MergeSelection.PlannedLayoutSize.x
                                : null,
                        AcceptAtlasSize = (atlasContext, size, metrics) =>
                        {
                            executionMetrics = metrics;
                            if (execution.MergeSelection is { } mergeSelection)
                            {
                                // The planner reserves an independently valid
                                // 2D rectangle for each atomic group. Accept only
                                // if execution stays within both the predicted
                                // layout and every predicted property-resolution
                                // envelope.
                                if (metrics.OutputTexturePixelCount <= 0
                                    || (mergeSelection.PackedRegions != null
                                        && metrics.LayoutSize.x
                                            != mergeSelection.PlannedLayoutSize.x)
                                    || metrics.LayoutSize.x
                                        > mergeSelection.PlannedLayoutSize.x
                                    || metrics.LayoutSize.y
                                        > mergeSelection.PlannedLayoutSize.y)
                                {
                                    if (mergeSelection.PackedRegions != null)
                                        twoDimensionalSizeRejection =
                                            FormatLocalized(
                                                "AutoAtlasTexture:fallback:LayoutMismatch",
                                                metrics.LayoutSize.x,
                                                metrics.LayoutSize.y,
                                                mergeSelection.PlannedLayoutSize.x,
                                                mergeSelection.PlannedLayoutSize.y,
                                                metrics.OutputTexturePixelCount);
                                    return false;
                                }

                                foreach (var property in metrics.PropertySizes)
                                {
                                    if (!mergeSelection.PlannedPropertySizes
                                            .TryGetValue(
                                                property.Key,
                                                out var plannedSize)
                                        || property.Value.x > plannedSize.x
                                        || property.Value.y > plannedSize.y)
                                    {
                                        if (mergeSelection.PackedRegions != null)
                                            twoDimensionalSizeRejection =
                                                FormatLocalized(
                                                    "AutoAtlasTexture:fallback:PropertyResolutionMismatch",
                                                    property.Key,
                                                    property.Value.x,
                                                    property.Value.y,
                                                    plannedSize.x,
                                                    plannedSize.y);
                                        return false;
                                    }
                                }

                                return true;
                            }

                            // For standalone AutoAtlas, the pre-FineTuning
                            // property count is not authoritative. Default
                            // ReferenceCopy (and other FineTuning) can remove or
                            // merge outputs. Defer the benefit check until after
                            // DoTextureFinTuning(), before any mesh/material
                            // mutation.
                            return metrics.OutputTexturePixelCount > 0;
                        },
                    });
                if (!atlasResult.IsSuccess)
                {
                    if (execution.MergeSelection is { } failedMerge)
                    {
                        if (failedMerge.PackedRegions != null)
                        {
                            var reason = twoDimensionalRelocator?.FailureDetail;
                            if (string.IsNullOrEmpty(reason))
                                reason = twoDimensionalSizeRejection
                                    ?? FormatLocalized(
                                        "AutoAtlasTexture:fallback:AtlasExecutionFailed");
                            twoDimensionalFallbackReasons.Add(
                                $"groups={string.Join(",", failedMerge.GroupIds.Select(id => "g" + id))}, layout={failedMerge.PlannedLayoutSize.x}×{failedMerge.PlannedLayoutSize.y}: {reason}");
                        }
                        // A merge plan is only an optimization. If its
                        // placement cannot be executed losslessly, retry the
                        // original atomic groups without merging them.
                        EnqueueStandaloneFallback(
                            executionGroups, failedMerge, preparedGroups);
                    }
                    continue;
                }

                using var atlasContext = atlasResult.AtlasContext!;
                var compiledTextures = atlasResult.CompiledAtlasTextures!;

                // ReplaceMesh silently skips a renderer when no corresponding
                // generated atlas mesh can be resolved. That is only harmless
                // when the target material has no drawable geometry on that
                // renderer. Otherwise the later domain-wide material replacement
                // would pair an atlased material with unchanged UVs.
                var unresolvedRequiredRenderer =
                    atlasRenderers.FirstOrDefault(renderer =>
                        RendererRequiresAtlasMesh(
                            domain,
                            renderer,
                            group)
                        && AtlasTexture.ResolveAtlasedMeshForRenderer(
                            domain,
                            renderer,
                            atlasContext,
                            atlasResult.AtlasedMeshes!) is null);
                if (unresolvedRequiredRenderer != null)
                {
                    foreach (var rt in compiledTextures.Values.Distinct())
                        rt.Dispose();
                    DestroyUnappliedAtlasedMeshes(
                        atlasResult.AtlasedMeshes!);

                    if (execution.MergeSelection is { } unresolvedMerge)
                    {
                        if (unresolvedMerge.PackedRegions != null)
                        {
                            twoDimensionalFallbackReasons.Add(
                                $"groups={string.Join(",", unresolvedMerge.GroupIds.Select(id => "g" + id))}, "
                                + $"layout={unresolvedMerge.PlannedLayoutSize.x}×{unresolvedMerge.PlannedLayoutSize.y}: "
                                + FormatLocalized(
                                    "AutoAtlasTexture:fallback:RequiredMeshResolutionFailed",
                                    unresolvedRequiredRenderer.name));
                        }

                        EnqueueStandaloneFallback(
                            executionGroups,
                            unresolvedMerge,
                            preparedGroups);
                    }

                    continue;
                }

                var tuningResult = AtlasTexture.DoTextureFinTuning(
                    engine, atlasContext, settings, compiledTextures, null);

                var finalOutputPixels = tuningResult.TextureDescriptors.Keys
                    .Distinct()
                    .Sum(texture => (long)texture.Width * texture.Hight);
                var retainedSourcePixels = execution.RetainedSourceTextures
                    .Sum(texture => (long)texture.width * texture.height);
                var finalTotalPixels =
                    finalOutputPixels + retainedSourcePixels;
                var pixelBaseline = execution.MergeSelection?.BaselinePixels
                    ?? originalPixels;

                if (finalOutputPixels <= 0
                    || finalTotalPixels >= pixelBaseline)
                {
                    foreach (var rt in compiledTextures.Values
                                 .Concat(tuningResult.TextureDescriptors.Keys)
                                 .Distinct())
                    {
                        rt.Dispose();
                    }
                    DestroyUnappliedAtlasedMeshes(
                        atlasResult.AtlasedMeshes!);

                    if (execution.MergeSelection is { } pixelCheckedMerge)
                    {
                        if (pixelCheckedMerge.PackedRegions != null)
                        {
                            twoDimensionalFallbackReasons.Add(
                                $"groups={string.Join(",", pixelCheckedMerge.GroupIds.Select(id => "g" + id))}, "
                                + $"layout={pixelCheckedMerge.PlannedLayoutSize.x}×{pixelCheckedMerge.PlannedLayoutSize.y}: "
                                + FormatLocalized(
                                    "AutoAtlasTexture:fallback:FinalPixelsNotReduced",
                                    finalTotalPixels,
                                    pixelCheckedMerge.BaselinePixels));
                        }

                        EnqueueStandaloneFallback(
                            executionGroups,
                            pixelCheckedMerge,
                            preparedGroups);
                    }

                    continue;
                }

                var hasExecutionTextureMemory =
                    AtlasTexture.TryCalculateFinalTextureMemory(
                        atlasContext,
                        tuningResult,
                        out var executionTextureMemoryBytes);
                var hasRetainedSourceMemory =
                    AtlasTexture.TryCalculateSourceTextureMemory(
                        domain,
                        execution.RetainedSourceTextures,
                        out var retainedSourceMemoryBytes);
                var hasSourceTextureMemory =
                    AtlasTexture.TryCalculateSourceTextureMemory(
                        domain,
                        execution.SourceTextures,
                        out var sourceTextureMemoryBytes);
                var hasMemoryBaseline =
                    execution.MergeSelection != null
                    || hasSourceTextureMemory;
                var memoryBaseline =
                    execution.MergeSelection?.BaselineBytes
                    ?? sourceTextureMemoryBytes;
                var finalTotalTextureMemoryBytes =
                    executionTextureMemoryBytes + retainedSourceMemoryBytes;

                if (!hasExecutionTextureMemory
                    || !hasRetainedSourceMemory
                    || !hasMemoryBaseline
                    || finalTotalTextureMemoryBytes >= memoryBaseline)
                {
                    foreach (var rt in compiledTextures.Values
                                 .Concat(tuningResult.TextureDescriptors.Keys)
                                 .Distinct())
                    {
                        rt.Dispose();
                    }
                    DestroyUnappliedAtlasedMeshes(
                        atlasResult.AtlasedMeshes!);

                    if (execution.MergeSelection is { } memoryCheckedMerge)
                    {
                        if (memoryCheckedMerge.PackedRegions != null)
                        {
                            var reason =
                                !hasExecutionTextureMemory
                                || !hasRetainedSourceMemory
                                    ? FormatLocalized(
                                        "AutoAtlasTexture:fallback:FinalMemoryUnknown")
                                    : FormatLocalized(
                                        "AutoAtlasTexture:fallback:FinalMemoryNotReduced",
                                        finalTotalTextureMemoryBytes,
                                        memoryCheckedMerge.BaselineBytes);
                            twoDimensionalFallbackReasons.Add(
                                $"groups={string.Join(",", memoryCheckedMerge.GroupIds.Select(id => "g" + id))}, "
                                + $"layout={memoryCheckedMerge.PlannedLayoutSize.x}×{memoryCheckedMerge.PlannedLayoutSize.y}: {reason}");
                        }

                        EnqueueStandaloneFallback(
                            executionGroups,
                            memoryCheckedMerge,
                            preparedGroups);
                    }

                    continue;
                }

                var resultingTextures = tuningResult.RenderTextures.ToDictionary(
                    item => item.Key,
                    item => engine.GetReferenceRenderTexture(item.Value));
                var retainedRenderTextures = tuningResult.TextureDescriptors.Keys.ToHashSet();

                var atlasMaterialMaps =
                    AtlasTexture.GenerateAtlasedMaterialMaps(
                        domain,
                        group,
                        settings,
                        (new List<AtlasTexture.MaterialMergeGroup>(), null, null),
                        resultingTextures,
                        atlasResult.PreserveBump2ndMaterials,
                        atlasResult.PreservedOriginalUVChannel);
                var generatedAtlasMaterials =
                    atlasMaterialMaps.materialMap.Values
                        .Concat(
                            atlasMaterialMaps
                                .domainsMaterial2ReplaceMaterial.Values)
                        .Distinct()
                        .ToArray();

                // References already classified as retained are part of the
                // cost model. Only an unexpected source reference invalidates
                // the prediction and forces a conservative fallback.
                var generatedRetainedSourceTextures =
                    CollectRetainedSourceTextures(
                        generatedAtlasMaterials,
                        execution.SourceTextures);
                if (generatedRetainedSourceTextures.Any(texture =>
                        !execution.RetainedSourceTextures.Contains(texture)))
                {
                    DestroyUnappliedAtlasedMaterials(
                        generatedAtlasMaterials);
                    foreach (var rt in compiledTextures.Values
                                 .Concat(tuningResult.TextureDescriptors.Keys)
                                 .Distinct())
                    {
                        rt.Dispose();
                    }
                    DestroyUnappliedAtlasedMeshes(
                        atlasResult.AtlasedMeshes!);

                    if (execution.MergeSelection is { } retainedSourceMerge)
                    {
                        if (retainedSourceMerge.PackedRegions != null)
                        {
                            twoDimensionalFallbackReasons.Add(
                                $"groups={string.Join(",", retainedSourceMerge.GroupIds.Select(id => "g" + id))}, "
                                + $"layout={retainedSourceMerge.PlannedLayoutSize.x}×{retainedSourceMerge.PlannedLayoutSize.y}: "
                                + FormatLocalized(
                                    "AutoAtlasTexture:fallback:SourceTextureRetained"));
                        }

                        EnqueueStandaloneFallback(
                            executionGroups,
                            retainedSourceMerge,
                            preparedGroups);
                    }

                    continue;
                }

                var resolutionRoutes = AtlasResolutionRouteReport
                    .Collect(atlasContext, tuningResult);
                var block = string.Format(materialBlock,
                    string.Join(", ", group.Select(OriginalMaterialName)
                        .Distinct().OrderBy(name => name, StringComparer.Ordinal)),
                    executionMetrics.TopFreeFraction,
                    TTTLog.FormatLocalizedLines(
                        AtlasResolutionRouteReport.CreateLocalizedLogLines(
                            resolutionRoutes)));

                if (hasSourceTextureMemory)
                {
                    block += "\n" + string.Format(
                        "AtlasTexture:info:TargetTextureMemory".GetLocalize(),
                        AtlasTexture.FormatTextureMemory(sourceTextureMemoryBytes),
                        AtlasTexture.FormatTextureMemory(
                            finalTotalTextureMemoryBytes));
                }

                // Diagnostic readback is intentionally after all execution
                // safety gates, and before generated textures are disposed.
                emissionProbe.Observe(domain, atlasRenderers, atlasResult.AtlasedMeshes!,
                    atlasMaterialMaps.materialMap, resultingTextures,
                    tuningResult, atlasContext);

                AtlasTexture.ReplaceMesh(
                    domain, atlasRenderers, atlasContext, atlasResult.AtlasedMeshes!);
                AtlasTexture.ApplyAtlasedMaterialMaps(
                    domain,
                    atlasMaterialMaps.materialMap,
                    atlasMaterialMaps.domainsMaterial2ReplaceMaterial);

                foreach (var rt in compiledTextures.Values)
                    if (!retainedRenderTextures.Contains(rt)) rt.Dispose();

                foreach (var descriptor in tuningResult.TextureDescriptors)
                    domain.RegisterPostProcessingAndLazyGPUReadBack(
                        descriptor.Key, descriptor.Value);

                completed.Add(block);
            }

            processingTimer.Stop();

            TTTLog.ReportingObject(configuration, () =>
                ErrorReport.ReportError(new AutoAtlasEmissionProbeError(
                    emissionProbe.Report())));

            if (twoDimensionalFallbackReasons.Count != 0)
            {
                TTTLog.ReportingObject(configuration, () =>
                    TTTLog.Info(
                        "AutoAtlasTexture:info:TwoDimensionalFallback",
                        string.Join("\n", twoDimensionalFallbackReasons)));
            }

            if (completed.Count == 0)
            {
                TTTLog.ReportingObject(configuration, () =>
                    TTTLog.Info("AutoAtlasTexture:info:TargetNotFound",
                        processingTimer.ElapsedMilliseconds));
                return;
            }

            TTTLog.ReportingObject(configuration, () =>
                TTTLog.Info("AutoAtlasTexture:info:RelocateResult",
                    string.Join("\n\n", completed),
                    processingTimer.ElapsedMilliseconds));
        }

        internal static bool RendererRequiresAtlasMesh(
            IRendererTargeting targeting,
            Renderer renderer,
            HashSet<Material> targetMaterials)
        {
            var mesh = targeting.GetMesh(renderer);
            if (mesh == null || mesh.subMeshCount == 0)
                return false;

            var materials = targeting.GetMaterials(renderer);
            for (var slot = 0; slot < materials.Length; slot += 1)
            {
                var material = materials[slot];
                if (material == null
                    || !targetMaterials.Contains(material))
                {
                    continue;
                }

                var effectiveSubMesh = Math.Clamp(
                    slot, 0, mesh.subMeshCount - 1);
                if (mesh.GetSubMesh(effectiveSubMesh).indexCount != 0)
                    return true;
            }

            return false;
        }

        internal static HashSet<Texture>
            CollectRetainedSourceTextures(
                IEnumerable<Material> materials,
                HashSet<Texture> sourceTextures)
        {
            return materials
                .Where(material => material != null)
                .SelectMany(material =>
                    material.EnumerateReferencedTextures())
                .Where(sourceTextures.Contains)
                .ToHashSet();
        }

        internal static bool RetainsAnySourceTexture(
            IEnumerable<Material> materials,
            HashSet<Texture> sourceTextures)
        {
            return CollectRetainedSourceTextures(
                materials, sourceTextures).Count != 0;
        }

        internal static void DestroyUnappliedAtlasedMaterials(
            IEnumerable<Material> materials)
        {
            foreach (var material in materials
                         .Where(material => material != null)
                         .Distinct())
            {
                UnityEngine.Object.DestroyImmediate(material);
            }
        }

        internal static bool HasActiveUntransformedMaterialConsumer(
            IRendererTargeting domain,
            IReadOnlyCollection<Material> materials,
            IReadOnlyCollection<Renderer> transformedRenderers)
        {
            var transformed = transformedRenderers.ToHashSet();

            foreach (var renderer in domain.EnumerateRenderer())
            {
                if (renderer == null || renderer.tag == "EditorOnly")
                    continue;

                if (!domain.IsEnable(renderer)
                    || !domain.IsActive(renderer.gameObject))
                {
                    continue;
                }

                if (!domain.GetMaterials(renderer)
                        .UOfType<Material>()
                        .Any(materials.Contains))
                {
                    continue;
                }

                if (!transformed.Contains(renderer))
                    return true;
            }

            return false;
        }

        internal static bool HasStrictMergePixelBenefit(
            long finalOutputPixels,
            long standaloneBaselinePixels)
        {
            return finalOutputPixels > 0
                && finalOutputPixels < standaloneBaselinePixels;
        }

        internal static void DestroyUnappliedAtlasedMeshes(
            IEnumerable<Mesh> meshes)
        {
            foreach (var mesh in meshes
                         .Where(mesh => mesh != null)
                         .Distinct())
            {
                UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        internal static bool IsExcludedFromTextureMerging(
            IReadOnlyCollection<Material> materials,
            IEnumerable<Renderer> renderers,
            HashSet<Material> excludedMaterials,
            HashSet<Renderer> excludedRenderers)
        {
            return materials.Any(excludedMaterials.Contains)
                || renderers.Any(excludedRenderers.Contains);
        }

        internal static HashSet<Texture> FindRetainedSourceTextures(
            HashSet<Material> allReferencedMaterials,
            HashSet<Material> group,
            IReadOnlyDictionary<
                Material,
                IReadOnlyDictionary<string, Texture>> propertyTextures,
            HashSet<Texture> replacedTextures)
        {
            var retained = new HashSet<Texture>();
            foreach (var material in allReferencedMaterials)
            {
                var isTarget = group.Contains(material);
                propertyTextures.TryGetValue(
                    material, out var atlasProperties);

                // A texture remains resident when any reference is outside the
                // exact property rewrite performed for this atlas group.
                foreach (var property in material.GetTextureReferences())
                {
                    if (!replacedTextures.Contains(property.Value)) continue;
                    if (!isTarget
                        || atlasProperties == null
                        || !atlasProperties.TryGetValue(
                            property.Key, out var atlasTexture)
                        || atlasTexture != property.Value)
                    {
                        retained.Add(property.Value);
                    }
                }
            }

            return retained;
        }

        private static List<HashSet<Material>> MakeTextureConnectedGroups(
            HashSet<Material> materials,
            IReadOnlyDictionary<Material, IReadOnlyDictionary<string, Texture>> properties)
        {
            var materialsByTexture = new Dictionary<Texture, HashSet<Material>>();
            foreach (var material in materials)
            {
                foreach (var texture in properties[material].Values)
                {
                    if (texture == null) continue;
                    if (!materialsByTexture.TryGetValue(texture, out var consumers))
                        materialsByTexture[texture] = consumers = new HashSet<Material>();
                    consumers.Add(material);
                }
            }

            var remaining = new HashSet<Material>(materials.Where(material =>
                properties[material].Count != 0));
            var results = new List<HashSet<Material>>();
            while (remaining.Count != 0)
            {
                var seed = remaining.First();
                remaining.Remove(seed);
                var group = new HashSet<Material> { seed };
                var pending = new Queue<Material>();
                pending.Enqueue(seed);

                while (pending.Count != 0)
                {
                    var current = pending.Dequeue();
                    foreach (var texture in properties[current].Values)
                    {
                        if (texture == null) continue;
                        foreach (var adjacent in materialsByTexture[texture])
                        {
                            if (!remaining.Remove(adjacent)) continue;
                            group.Add(adjacent);
                            pending.Enqueue(adjacent);
                        }
                    }
                }
                results.Add(group);
            }
            return results;
        }
    }
}
