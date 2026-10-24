#nullable enable
using System;
using System.Collections.Generic;
using net.rs64.TexTransCore;
using net.rs64.TexTransTool.TextureAtlas.AAOCode;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using Color = UnityEngine.Color;

namespace net.rs64.TexTransTool.TextureAtlas
{
    internal readonly struct AtlasSourceGpuFlags
    {
        internal readonly bool Known;
        internal readonly bool IsConstant;
        internal readonly bool ApproximateConstantCandidate;
        internal readonly bool RgbZero;
        internal readonly bool AlphaZero;

        internal AtlasSourceGpuFlags(
            bool known,
            bool isConstant,
            bool approximateConstantCandidate,
            bool rgbZero,
            bool alphaZero)
        {
            Known = known;
            IsConstant = isConstant;
            ApproximateConstantCandidate = approximateConstantCandidate;
            RgbZero = rgbZero;
            AlphaZero = alphaZero;
        }
    }

    internal sealed class AtlasSourceGpuGeometry : IDisposable
    {
        internal readonly Mesh Mesh;
        internal readonly RectInt Bounds;
        internal readonly Vector2Int ReferenceCoord;

        internal AtlasSourceGpuGeometry(
            Mesh mesh,
            RectInt bounds,
            Vector2Int referenceCoord)
        {
            Mesh = mesh;
            Bounds = bounds;
            ReferenceCoord = referenceCoord;
        }

        public void Dispose()
        {
            UnityEngine.Object.DestroyImmediate(Mesh);
        }
    }

    internal sealed class AtlasSourceGpuPending : IDisposable
    {
        internal readonly RenderTexture Source;
        internal readonly AtlasSourceGpuGeometry Geometry;
        internal readonly Vector2Int ReferenceCoord;
        internal readonly RenderTexture Result;
        internal readonly AsyncGPUReadbackRequest Request;

        internal AtlasSourceGpuPending(
            RenderTexture source,
            AtlasSourceGpuGeometry geometry,
            Vector2Int referenceCoord,
            RenderTexture result,
            AsyncGPUReadbackRequest request)
        {
            Source = source;
            Geometry = geometry;
            ReferenceCoord = referenceCoord;
            Result = result;
            Request = request;
        }

        internal bool TryGetFlags(out AtlasSourceGpuFlags flags)
        {
            flags = default;
            try
            {
                var request = Request;
                request.WaitForCompletion();
                if (request.hasError)
                    return false;

                NativeArray<Color32> data =
                    request.GetData<Color32>();
                if (data.Length != 1)
                    return false;

                var pixel = data[0];
                if (pixel.a < 128)
                    return false;

                // R stores the worst constant-color comparison state:
                // 0 = exact, 1/4 = within configured T, 1/2 = inside the
                // tolerant-candidate envelope, 1 = outside that envelope.
                flags = new AtlasSourceGpuFlags(
                    true,
                    pixel.r < 32,
                    pixel.r < 224,
                    pixel.g < 128,
                    pixel.b < 128);
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal AsyncGPUReadbackRequest RequestReferenceColor()
        {
            return AsyncGPUReadback.Request(
                Source,
                0,
                ReferenceCoord.x,
                1,
                ReferenceCoord.y,
                1,
                0,
                1,
                TextureFormat.RGBAFloat,
                null);
        }

        public void Dispose()
        {
            UnityEngine.Object.DestroyImmediate(Result);
        }
    }

    internal sealed class AtlasSourceGpuProbePending : IDisposable
    {
        private readonly RenderTexture _result;
        private readonly AsyncGPUReadbackRequest _request;

        internal AtlasSourceGpuProbePending(
            RenderTexture result,
            AsyncGPUReadbackRequest request)
        {
            _result = result;
            _request = request;
        }

        internal bool TryGetRepresentative(out Color representative)
        {
            representative = default;
            try
            {
                var request = _request;
                request.WaitForCompletion();
                if (request.hasError)
                    return false;

                NativeArray<Color> data = request.GetData<Color>();
                if (data.Length == 0)
                    return false;

                var counts =
                    new Dictionary<(float r, float g, float b), int>();
                (float r, float g, float b) best = default;
                var bestCount = 0;
                var covered = 0;

                foreach (var pixel in data)
                {
                    if (pixel.a < 0.5f)
                        continue;

                    covered += 1;
                    var key = (pixel.r, pixel.g, pixel.b);
                    counts.TryGetValue(key, out var count);
                    count += 1;
                    counts[key] = count;
                    if (count > bestCount)
                    {
                        bestCount = count;
                        best = key;
                    }
                }

                if (covered == 0)
                    return false;

                representative =
                    new Color(best.r, best.g, best.b, 1f);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public void Dispose()
        {
            UnityEngine.Object.DestroyImmediate(_result);
        }
    }

    internal readonly struct AtlasSourceGpuToleranceValidation
    {
        internal readonly int Covered;
        internal readonly int ExactRgb;
        internal readonly int WithinPrimary;
        internal readonly int TailViolations;

        internal AtlasSourceGpuToleranceValidation(
            int covered,
            int exactRgb,
            int withinPrimary,
            int tailViolations)
        {
            Covered = covered;
            ExactRgb = exactRgb;
            WithinPrimary = withinPrimary;
            TailViolations = tailViolations;
        }

        internal bool Accepted =>
            Covered > 0
            && (long)ExactRgb * 10L >= (long)Covered * 9L
            && (long)WithinPrimary * 100L >= (long)Covered * 99L
            && TailViolations == 0;
    }

    internal sealed class AtlasSourceGpuToleranceValidationPending
        : IDisposable
    {
        private readonly RenderTexture _result;
        private readonly AsyncGPUReadbackRequest _request;

        internal AtlasSourceGpuToleranceValidationPending(
            RenderTexture result,
            AsyncGPUReadbackRequest request)
        {
            _result = result;
            _request = request;
        }

        internal bool TryGetValidation(
            out AtlasSourceGpuToleranceValidation validation)
        {
            validation = default;
            try
            {
                var request = _request;
                request.WaitForCompletion();
                if (request.hasError)
                    return false;

                var data = request.GetData<Color>();
                if (data.Length != 1)
                    return false;

                var pixel = data[0];
                validation = new AtlasSourceGpuToleranceValidation(
                    Mathf.RoundToInt(pixel.r),
                    Mathf.RoundToInt(pixel.g),
                    Mathf.RoundToInt(pixel.b),
                    Mathf.RoundToInt(pixel.a));
                return true;
            }
            catch
            {
                return false;
            }
        }

        public void Dispose()
        {
            UnityEngine.Object.DestroyImmediate(_result);
        }
    }

    /// <summary>
    /// GPU implementation of source-side Atlas texture inspection.
    ///
    /// CPU work is proportional to UV triangle count: it transforms triangles
    /// and expands them by the one-pixel linear-filter support. The expanded
    /// polygons are rasterized directly while the fragment shader evaluates the
    /// canonical TTT source texture. A 4x4 max reduction collapses the first-pass
    /// result to one RGBA32 pixel for exact/near-constant candidacy and exact
    /// zero proofs. Tolerant candidates then use a compact representative probe
    /// followed by a full-coverage GPU count that enforces the 90% exact,
    /// 99% within T+1, 100% within T+3, and exact-alpha rules.
    /// </summary>
    internal sealed class AtlasSourceGpuAnalyzer : IDisposable
    {
        private const string ShaderName =
            "Hidden/TexTransTool/AtlasSourceAnalysis";

        // One-pixel linear-filter support uses a strict boundary:
        // texel centers lying exactly on the expanded polygon edge are not
        // required coverage, so no additional epsilon is applied.
        private const float FilterRadius = 1f;
        // Unequal prime dimensions reduce aliasing with common 2x2 / 4x4
        // dither patterns while keeping the readback tiny.
        private const int RepresentativeProbeWidth = 31;
        private const int RepresentativeProbeHeight = 29;

        private sealed class SourceSlotData
        {
            internal readonly Vector2[] Uvs;
            internal readonly int[] TriangleIndices;

            internal SourceSlotData(
                Vector2[] uvs,
                int[] triangleIndices)
            {
                Uvs = uvs;
                TriangleIndices = triangleIndices;
            }
        }

        private readonly Material _material;
        private readonly Dictionary<
            (Mesh mesh, int subMesh, int uvChannel),
            SourceSlotData?> _sourceSlotCache = new();

        private AtlasSourceGpuAnalyzer(Material material)
        {
            _material = material;
        }

        internal static AtlasSourceGpuAnalyzer? TryCreate()
        {
            if (!SystemInfo.supportsAsyncGPUReadback
                || !SystemInfo.SupportsRenderTextureFormat(
                    RenderTextureFormat.ARGB32))
                return null;

            var shader = Shader.Find(ShaderName);
            if (shader == null || !shader.isSupported)
                return null;

            return new AtlasSourceGpuAnalyzer(
                new Material(shader)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                });
        }

        internal bool TryBuildGeometry(
            IEnumerable<(Mesh mesh, int subMesh)> slots,
            int uvChannel,
            Matrix2x3 matrix,
            int sourceWidth,
            int sourceHeight,
            out AtlasSourceGpuGeometry? geometry)
        {
            geometry = null;
            if (sourceWidth <= 0 || sourceHeight <= 0)
                return false;

            var logicalVertices = new List<Vector2>();
            var indices = new List<int>();
            var minX = int.MaxValue;
            var minY = int.MaxValue;
            var maxX = int.MinValue;
            var maxY = int.MinValue;
            Vector2Int? referenceCoord = null;
            Span<Vector2> hullBuffer = stackalloc Vector2[13];

            foreach (var (sourceMesh, subMesh) in slots)
            {
                try
                {
                    if (!TryGetSourceSlotData(
                            sourceMesh,
                            subMesh,
                            uvChannel,
                            out var slotData)
                        || slotData == null)
                        return false;

                    var uvs = slotData.Uvs;
                    var triangleIndices = slotData.TriangleIndices;
                    for (var index = 0;
                         index < triangleIndices.Length;
                         index += 3)
                    {
                        var i0 = triangleIndices[index];
                        var i1 = triangleIndices[index + 1];
                        var i2 = triangleIndices[index + 2];

                        var a = Transform(uvs[i0], matrix);
                        var b = Transform(uvs[i1], matrix);
                        var c = Transform(uvs[i2], matrix);
                        if (!InsideUnitUv(a)
                            || !InsideUnitUv(b)
                            || !InsideUnitUv(c))
                            return false;

                        a = ToPixelCenterSpace(
                            a,
                            sourceWidth,
                            sourceHeight);
                        b = ToPixelCenterSpace(
                            b,
                            sourceWidth,
                            sourceHeight);
                        c = ToPixelCenterSpace(
                            c,
                            sourceWidth,
                            sourceHeight);

                        referenceCoord ??= new Vector2Int(
                            Mathf.Clamp(
                                Mathf.FloorToInt(a.x),
                                0,
                                sourceWidth - 1),
                            Mathf.Clamp(
                                Mathf.FloorToInt(a.y),
                                0,
                                sourceHeight - 1));

                        var hullCount = BuildExpandedHull(
                            a,
                            b,
                            c,
                            FilterRadius,
                            hullBuffer);
                        if (hullCount < 3)
                            return false;

                        var vertexStart = logicalVertices.Count;
                        for (var hullIndex = 0;
                             hullIndex < hullCount;
                             hullIndex += 1)
                        {
                            var point = hullBuffer[hullIndex];
                            minX = Math.Min(
                                minX,
                                Mathf.FloorToInt(point.x));
                            minY = Math.Min(
                                minY,
                                Mathf.FloorToInt(point.y));
                            maxX = Math.Max(
                                maxX,
                                Mathf.CeilToInt(point.x));
                            maxY = Math.Max(
                                maxY,
                                Mathf.CeilToInt(point.y));
                            logicalVertices.Add(point);
                        }

                        for (var fan = 1;
                             fan + 1 < hullCount;
                             fan += 1)
                        {
                            indices.Add(vertexStart);
                            indices.Add(vertexStart + fan);
                            indices.Add(vertexStart + fan + 1);
                        }
                    }
                }
                catch
                {
                    return false;
                }
            }

            if (indices.Count == 0
                || referenceCoord == null
                || maxX < minX
                || maxY < minY)
                return false;

            var bounds = new RectInt(
                minX,
                minY,
                maxX - minX + 1,
                maxY - minY + 1);
            if (bounds.width <= 0
                || bounds.height <= 0
                || bounds.width > SystemInfo.maxTextureSize
                || bounds.height > SystemInfo.maxTextureSize)
                return false;

            var vertices = new Vector3[logicalVertices.Count];
            for (var index = 0;
                 index < logicalVertices.Count;
                 index += 1)
            {
                var point = logicalVertices[index];
                vertices[index] = new Vector3(
                    ToClipCoordinate(
                        point.x,
                        bounds.x,
                        bounds.width),
                    ToClipCoordinate(
                        point.y,
                        bounds.y,
                        bounds.height),
                    0f);
            }

            var mesh = new Mesh
            {
                hideFlags = HideFlags.HideAndDontSave,
                indexFormat = vertices.Length > 65535
                    ? IndexFormat.UInt32
                    : IndexFormat.UInt16,
            };
            mesh.vertices = vertices;
            mesh.SetUVs(0, logicalVertices);
            mesh.SetTriangles(indices, 0, true);

            geometry = new AtlasSourceGpuGeometry(
                mesh,
                bounds,
                referenceCoord.Value);
            return true;
        }

        internal bool TryQueue(
            RenderTexture source,
            AtlasSourceGpuGeometry geometry,
            out AtlasSourceGpuPending? pending,
            int rgbTolerance = 0,
            Color? referenceValue = null)
        {
            pending = null;
            if (source == null
                || source.width <= 0
                || source.height <= 0)
                return false;

            RenderTexture? analysis = null;
            RenderTexture? current = null;
            var currentIsTemporary = false;
            RenderTexture? result = null;
            var previous = RenderTexture.active;

            try
            {
                analysis = RenderTexture.GetTemporary(
                    geometry.Bounds.width,
                    geometry.Bounds.height,
                    0,
                    RenderTextureFormat.ARGB32,
                    RenderTextureReadWrite.Linear,
                    1);
                analysis.filterMode = FilterMode.Point;
                analysis.antiAliasing = 1;

                var properties = new MaterialPropertyBlock();
                properties.SetTexture("_Source", source);
                properties.SetVector(
                    "_SourceSize",
                    new Vector4(
                        source.width,
                        source.height,
                        1f / source.width,
                        1f / source.height));
                properties.SetVector(
                    "_ReferenceCoord",
                    new Vector4(
                        geometry.ReferenceCoord.x,
                        geometry.ReferenceCoord.y,
                        0f,
                        0f));
                properties.SetFloat(
                    "_Tolerance",
                    Mathf.Clamp(rgbTolerance, 0, 4) / 255f);
                properties.SetFloat(
                    "_UseReferenceValue",
                    referenceValue.HasValue ? 1f : 0f);
                properties.SetVector(
                    "_ReferenceValue",
                    referenceValue ?? default);

                // NDMF invokes this outside a camera render callback.
                // Submit the offscreen draw explicitly through a command buffer.
                using (var command = new CommandBuffer
                {
                    name = "TTT Atlas Source GPU Analysis",
                })
                {
                    command.SetRenderTarget(analysis);
                    command.ClearRenderTarget(false, true, Color.clear);
                    command.DrawMesh(
                        geometry.Mesh,
                        Matrix4x4.identity,
                        _material,
                        0,
                        0,
                        properties);
                    Graphics.ExecuteCommandBuffer(command);
                }

                result = new RenderTexture(
                    1,
                    1,
                    0,
                    RenderTextureFormat.ARGB32,
                    RenderTextureReadWrite.Linear)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                    filterMode = FilterMode.Point,
                    antiAliasing = 1,
                    useMipMap = false,
                    autoGenerateMips = false,
                };
                result.Create();

                current = analysis;
                currentIsTemporary = true;
                analysis = null;

                var currentWidth = geometry.Bounds.width;
                var currentHeight = geometry.Bounds.height;
                while (currentWidth > 1
                       || currentHeight > 1)
                {
                    var nextWidth =
                        Math.Max(1, (currentWidth + 3) / 4);
                    var nextHeight =
                        Math.Max(1, (currentHeight + 3) / 4);

                    RenderTexture next;
                    var nextIsTemporary =
                        nextWidth != 1 || nextHeight != 1;
                    if (nextIsTemporary)
                    {
                        next = RenderTexture.GetTemporary(
                            nextWidth,
                            nextHeight,
                            0,
                            RenderTextureFormat.ARGB32,
                            RenderTextureReadWrite.Linear,
                            1);
                        next.filterMode = FilterMode.Point;
                        next.antiAliasing = 1;
                    }
                    else
                    {
                        next = result;
                    }

                    Graphics.Blit(
                        current!,
                        next,
                        _material,
                        1);

                    if (currentIsTemporary)
                        RenderTexture.ReleaseTemporary(current!);

                    current = next;
                    currentIsTemporary = nextIsTemporary;
                    currentWidth = nextWidth;
                    currentHeight = nextHeight;
                }

                if (!ReferenceEquals(current, result))
                {
                    Graphics.Blit(
                        current!,
                        result,
                        _material,
                        1);
                    if (currentIsTemporary)
                        RenderTexture.ReleaseTemporary(current!);
                    current = result;
                    currentIsTemporary = false;
                }

                var request = AsyncGPUReadback.Request(
                    result,
                    0,
                    TextureFormat.RGBA32,
                    null);
                pending = new AtlasSourceGpuPending(
                    source,
                    geometry,
                    geometry.ReferenceCoord,
                    result,
                    request);
                result = null;
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                RenderTexture.active = previous;

                if (analysis != null)
                    RenderTexture.ReleaseTemporary(analysis);

                if (current != null
                    && currentIsTemporary)
                    RenderTexture.ReleaseTemporary(current);

                if (result != null)
                    UnityEngine.Object.DestroyImmediate(result);
            }
        }

        internal bool TryQueueRepresentativeProbe(
            RenderTexture source,
            AtlasSourceGpuGeometry geometry,
            out AtlasSourceGpuProbePending? pending)
        {
            pending = null;
            if (source == null
                || source.width <= 0
                || source.height <= 0
                || !SystemInfo.SupportsRenderTextureFormat(
                    RenderTextureFormat.ARGBFloat))
                return false;

            RenderTexture? result = null;
            try
            {
                result = new RenderTexture(
                    RepresentativeProbeWidth,
                    RepresentativeProbeHeight,
                    0,
                    RenderTextureFormat.ARGBFloat,
                    RenderTextureReadWrite.Linear)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                    filterMode = FilterMode.Point,
                    antiAliasing = 1,
                    useMipMap = false,
                    autoGenerateMips = false,
                };
                result.Create();

                var properties = new MaterialPropertyBlock();
                properties.SetTexture("_Source", source);
                properties.SetVector(
                    "_SourceSize",
                    new Vector4(
                        source.width,
                        source.height,
                        1f / source.width,
                        1f / source.height));

                using (var command = new CommandBuffer
                {
                    name = "TTT Atlas Source Representative Probe",
                })
                {
                    command.SetRenderTarget(result);
                    command.ClearRenderTarget(false, true, Color.clear);
                    command.DrawMesh(
                        geometry.Mesh,
                        Matrix4x4.identity,
                        _material,
                        0,
                        2,
                        properties);
                    Graphics.ExecuteCommandBuffer(command);
                }

                var request = AsyncGPUReadback.Request(
                    result,
                    0,
                    TextureFormat.RGBAFloat,
                    null);
                pending = new AtlasSourceGpuProbePending(
                    result,
                    request);
                result = null;
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (result != null)
                    UnityEngine.Object.DestroyImmediate(result);
            }
        }

        internal bool TryQueueToleranceValidation(
            RenderTexture source,
            AtlasSourceGpuGeometry geometry,
            Color representative,
            int rgbTolerance,
            out AtlasSourceGpuToleranceValidationPending? pending)
        {
            pending = null;
            var tolerance = Mathf.Clamp(rgbTolerance, 1, 4);
            RenderTexture? result = null;
            try
            {
                if (!TryQueueToleranceValidationPass(
                        source,
                        geometry,
                        representative,
                        4,
                        out result,
                        out var request,
                        (tolerance + 1f) / 255f,
                        (tolerance + 3f) / 255f)
                    || result == null)
                    return false;

                pending =
                    new AtlasSourceGpuToleranceValidationPending(
                        result,
                        request);
                result = null;
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (result != null)
                    UnityEngine.Object.DestroyImmediate(result);
            }
        }

        private bool TryQueueToleranceValidationPass(
            RenderTexture source,
            AtlasSourceGpuGeometry geometry,
            Color representative,
            int classificationPass,
            out RenderTexture? result,
            out AsyncGPUReadbackRequest request,
            float primaryTolerance = 0f,
            float tailTolerance = 0f)
        {
            result = null;
            request = default;
            RenderTexture? classification = null;
            RenderTexture? current = null;
            var currentIsTemporary = false;
            RenderTexture? final = null;

            try
            {
                classification = RenderTexture.GetTemporary(
                    geometry.Bounds.width,
                    geometry.Bounds.height,
                    0,
                    RenderTextureFormat.ARGB32,
                    RenderTextureReadWrite.Linear,
                    1);
                classification.filterMode = FilterMode.Point;
                classification.antiAliasing = 1;

                var properties = new MaterialPropertyBlock();
                properties.SetTexture("_Source", source);
                properties.SetVector(
                    "_SourceSize",
                    new Vector4(
                        source.width,
                        source.height,
                        1f / source.width,
                        1f / source.height));
                properties.SetVector(
                    "_ReferenceValue",
                    representative);
                properties.SetFloat(
                    "_PrimaryTolerance",
                    primaryTolerance);
                properties.SetFloat(
                    "_TailTolerance",
                    tailTolerance);

                using (var command = new CommandBuffer
                {
                    name = "TTT Atlas Constant Tolerance Validation",
                })
                {
                    command.SetRenderTarget(classification);
                    command.ClearRenderTarget(false, true, Color.clear);
                    command.DrawMesh(
                        geometry.Mesh,
                        Matrix4x4.identity,
                        _material,
                        0,
                        classificationPass,
                        properties);
                    Graphics.ExecuteCommandBuffer(command);
                }

                final = new RenderTexture(
                    1,
                    1,
                    0,
                    RenderTextureFormat.ARGBFloat,
                    RenderTextureReadWrite.Linear)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                    filterMode = FilterMode.Point,
                    antiAliasing = 1,
                    useMipMap = false,
                    autoGenerateMips = false,
                };
                final.Create();

                current = classification;
                currentIsTemporary = true;
                classification = null;

                var currentWidth = geometry.Bounds.width;
                var currentHeight = geometry.Bounds.height;
                while (true)
                {
                    var nextWidth =
                        Math.Max(1, (currentWidth + 3) / 4);
                    var nextHeight =
                        Math.Max(1, (currentHeight + 3) / 4);
                    var useFinal =
                        nextWidth == 1 && nextHeight == 1;

                    RenderTexture next;
                    var nextIsTemporary = !useFinal;
                    if (useFinal)
                    {
                        next = final;
                    }
                    else
                    {
                        next = RenderTexture.GetTemporary(
                            nextWidth,
                            nextHeight,
                            0,
                            RenderTextureFormat.ARGBFloat,
                            RenderTextureReadWrite.Linear,
                            1);
                        next.filterMode = FilterMode.Point;
                        next.antiAliasing = 1;
                    }

                    Graphics.Blit(
                        current!,
                        next,
                        _material,
                        3);

                    if (currentIsTemporary)
                        RenderTexture.ReleaseTemporary(current!);

                    current = next;
                    currentIsTemporary = nextIsTemporary;
                    currentWidth = nextWidth;
                    currentHeight = nextHeight;

                    if (useFinal)
                        break;
                }

                request = AsyncGPUReadback.Request(
                    final,
                    0,
                    TextureFormat.RGBAFloat,
                    null);
                result = final;
                final = null;
                current = null;
                currentIsTemporary = false;
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (classification != null)
                    RenderTexture.ReleaseTemporary(classification);
                if (current != null && currentIsTemporary)
                    RenderTexture.ReleaseTemporary(current);
                if (final != null)
                    UnityEngine.Object.DestroyImmediate(final);
            }
        }

        private bool TryGetSourceSlotData(
            Mesh sourceMesh,
            int subMesh,
            int uvChannel,
            out SourceSlotData? data)
        {
            data = null;
            if (sourceMesh == null)
                return false;

            var key = (sourceMesh, subMesh, uvChannel);
            if (_sourceSlotCache.TryGetValue(key, out data))
                return data != null;

            try
            {
                if (subMesh < 0
                    || subMesh >= sourceMesh.subMeshCount
                    || sourceMesh.GetTopology(subMesh)
                        != MeshTopology.Triangles)
                {
                    _sourceSlotCache[key] = null;
                    return false;
                }

                var uvList = new List<Vector2>();
                sourceMesh.GetUVs(uvChannel, uvList);
                var triangleIndices =
                    sourceMesh.GetTriangles(subMesh);
                if (uvList.Count == 0
                    || triangleIndices.Length == 0
                    || triangleIndices.Length % 3 != 0)
                {
                    _sourceSlotCache[key] = null;
                    return false;
                }

                foreach (var index in triangleIndices)
                {
                    if (index < 0 || index >= uvList.Count)
                    {
                        _sourceSlotCache[key] = null;
                        return false;
                    }
                }

                data = new SourceSlotData(
                    uvList.ToArray(),
                    triangleIndices);
                _sourceSlotCache.Add(key, data);
                return true;
            }
            catch
            {
                _sourceSlotCache[key] = null;
                return false;
            }
        }

        public void Dispose()
        {
            _sourceSlotCache.Clear();
            UnityEngine.Object.DestroyImmediate(_material);
        }

        private static Vector2 ToPixelCenterSpace(
            Vector2 uv,
            int width,
            int height)
        {
            return new Vector2(
                uv.x * width - 0.5f,
                uv.y * height - 0.5f);
        }

        private static float ToClipCoordinate(
            float logicalCoordinate,
            int logicalOrigin,
            int targetSize)
        {
            return ((logicalCoordinate
                     - logicalOrigin
                     + 0.5f)
                    / targetSize)
                * 2f - 1f;
        }

        private static int BuildExpandedHull(
            Vector2 a,
            Vector2 b,
            Vector2 c,
            float radius,
            Span<Vector2> hull)
        {
            Span<Vector2> points = stackalloc Vector2[12];
            var pointCount = 0;
            AddSquareCorners(points, ref pointCount, a, radius);
            AddSquareCorners(points, ref pointCount, b, radius);
            AddSquareCorners(points, ref pointCount, c, radius);

            // The input size is fixed at twelve. An insertion sort avoids the
            // per-triangle List allocations and comparer dispatch of List.Sort
            // while preserving the exact lexicographic ordering used before.
            for (var index = 1; index < pointCount; index += 1)
            {
                var value = points[index];
                var insert = index - 1;
                while (insert >= 0
                       && ComparePoint(points[insert], value) > 0)
                {
                    points[insert + 1] = points[insert];
                    insert -= 1;
                }
                points[insert + 1] = value;
            }

            var uniqueCount = 0;
            for (var index = 0; index < pointCount; index += 1)
            {
                var point = points[index];
                if (uniqueCount != 0
                    && points[uniqueCount - 1] == point)
                    continue;
                points[uniqueCount] = point;
                uniqueCount += 1;
            }

            if (uniqueCount <= 2)
            {
                for (var index = 0; index < uniqueCount; index += 1)
                    hull[index] = points[index];
                return uniqueCount;
            }

            // Build the monotonic-chain convex hull directly into a
            // caller-provided fixed buffer.
            var hullCount = 0;
            for (var index = 0; index < uniqueCount; index += 1)
            {
                var point = points[index];
                while (hullCount >= 2
                       && Cross(
                           hull[hullCount - 2],
                           hull[hullCount - 1],
                           point) <= 0f)
                    hullCount -= 1;
                hull[hullCount] = point;
                hullCount += 1;
            }

            var upperStart = hullCount + 1;
            for (var index = uniqueCount - 2;
                 index >= 0;
                 index -= 1)
            {
                var point = points[index];
                while (hullCount >= upperStart
                       && Cross(
                           hull[hullCount - 2],
                           hull[hullCount - 1],
                           point) <= 0f)
                    hullCount -= 1;
                hull[hullCount] = point;
                hullCount += 1;
            }

            // The standard monotonic-chain construction repeats the first
            // point as its last element.
            if (hullCount > 1)
                hullCount -= 1;
            return hullCount;
        }

        private static void AddSquareCorners(
            Span<Vector2> points,
            ref int count,
            Vector2 center,
            float radius)
        {
            points[count++] =
                center + new Vector2(-radius, -radius);
            points[count++] =
                center + new Vector2(-radius, radius);
            points[count++] =
                center + new Vector2(radius, -radius);
            points[count++] =
                center + new Vector2(radius, radius);
        }

        private static int ComparePoint(
            Vector2 left,
            Vector2 right)
        {
            var x = left.x.CompareTo(right.x);
            return x != 0
                ? x
                : left.y.CompareTo(right.y);
        }

        private static float Cross(
            Vector2 origin,
            Vector2 a,
            Vector2 b)
        {
            return (a.x - origin.x)
                    * (b.y - origin.y)
                - (a.y - origin.y)
                    * (b.x - origin.x);
        }

        private static Vector2 Transform(
            Vector2 uv,
            Matrix2x3 matrix)
        {
            return new Vector2(
                matrix.M00 * uv.x
                    + matrix.M01 * uv.y
                    + matrix.M02,
                matrix.M10 * uv.x
                    + matrix.M11 * uv.y
                    + matrix.M12);
        }

        private static bool InsideUnitUv(Vector2 uv)
        {
            return IsFinite(uv.x)
                && IsFinite(uv.y)
                && uv.x >= 0f
                && uv.x <= 1f
                && uv.y >= 0f
                && uv.y <= 1f;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value)
                && !float.IsInfinity(value);
        }
    }
}
