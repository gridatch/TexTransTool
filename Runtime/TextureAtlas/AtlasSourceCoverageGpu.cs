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
        internal readonly bool RgbZero;
        internal readonly bool AlphaZero;

        internal AtlasSourceGpuFlags(
            bool known,
            bool isConstant,
            bool rgbZero,
            bool alphaZero)
        {
            Known = known;
            IsConstant = isConstant;
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
        internal readonly Vector2Int ReferenceCoord;
        internal readonly RenderTexture Result;
        internal readonly AsyncGPUReadbackRequest Request;

        internal AtlasSourceGpuPending(
            RenderTexture source,
            Vector2Int referenceCoord,
            RenderTexture result,
            AsyncGPUReadbackRequest request)
        {
            Source = source;
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

                flags = new AtlasSourceGpuFlags(
                    true,
                    pixel.r < 128,
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

    /// <summary>
    /// GPU implementation of source-side Atlas texture inspection.
    ///
    /// CPU work is proportional to UV triangle count: it transforms triangles
    /// and expands them by the one-pixel linear-filter support. The expanded
    /// polygons are rasterized directly while the fragment shader evaluates the
    /// canonical TTT source texture. A 4x4 max reduction collapses the result to
    /// one RGBA32 pixel:
    /// R = any covered texel differs from the reference texel
    /// G = any covered texel has non-zero RGB
    /// B = any covered texel has non-zero alpha
    /// A = coverage exists
    ///
    /// Only that one pixel is read back for the proof. The actual constant color
    /// is read separately from one canonical source texel after a constant proof
    /// succeeds, preserving the existing RGBAFloat source representation.
    /// </summary>
    internal sealed class AtlasSourceGpuAnalyzer : IDisposable
    {
        private const string ShaderName =
            "Hidden/TexTransTool/AtlasSourceAnalysis";

        // Match the old CPU predicate exactly. Its overlap tests use
        // strict inequalities, so texel centers lying exactly on the expanded
        // polygon boundary are not required coverage and need no extra bias.
        private const float FilterRadius = 1f;

        private readonly Material _material;

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
            var uvs = new List<Vector2>();
            var minX = int.MaxValue;
            var minY = int.MaxValue;
            var maxX = int.MinValue;
            var maxY = int.MinValue;
            Vector2Int? referenceCoord = null;

            foreach (var (sourceMesh, subMesh) in slots)
            {
                try
                {
                    if (sourceMesh == null
                        || subMesh < 0
                        || subMesh >= sourceMesh.subMeshCount
                        || sourceMesh.GetTopology(subMesh)
                            != MeshTopology.Triangles)
                        return false;

                    uvs.Clear();
                    sourceMesh.GetUVs(uvChannel, uvs);
                    var triangleIndices =
                        sourceMesh.GetTriangles(subMesh);
                    if (uvs.Count == 0
                        || triangleIndices.Length == 0
                        || triangleIndices.Length % 3 != 0)
                        return false;

                    for (var index = 0;
                         index < triangleIndices.Length;
                         index += 3)
                    {
                        var i0 = triangleIndices[index];
                        var i1 = triangleIndices[index + 1];
                        var i2 = triangleIndices[index + 2];
                        if (i0 < 0 || i1 < 0 || i2 < 0
                            || i0 >= uvs.Count
                            || i1 >= uvs.Count
                            || i2 >= uvs.Count)
                            return false;

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

                        var hull = BuildExpandedHull(
                            a,
                            b,
                            c,
                            FilterRadius);
                        if (hull.Count < 3)
                            return false;

                        foreach (var point in hull)
                        {
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
                        }

                        var vertexStart = logicalVertices.Count;
                        logicalVertices.AddRange(hull);
                        for (var fan = 1;
                             fan + 1 < hull.Count;
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
            out AtlasSourceGpuPending? pending)
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

                RenderTexture.active = analysis;
                GL.Clear(false, true, Color.clear);

                _material.SetTexture("_Source", source);
                _material.SetVector(
                    "_SourceSize",
                    new Vector4(
                        source.width,
                        source.height,
                        1f / source.width,
                        1f / source.height));
                _material.SetVector(
                    "_ReferenceCoord",
                    new Vector4(
                        geometry.ReferenceCoord.x,
                        geometry.ReferenceCoord.y,
                        0f,
                        0f));

                if (!_material.SetPass(0))
                    return false;
                Graphics.DrawMeshNow(
                    geometry.Mesh,
                    Matrix4x4.identity);

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

        public void Dispose()
        {
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

        private static List<Vector2> BuildExpandedHull(
            Vector2 a,
            Vector2 b,
            Vector2 c,
            float radius)
        {
            var points = new List<Vector2>(12);
            AddSquareCorners(points, a, radius);
            AddSquareCorners(points, b, radius);
            AddSquareCorners(points, c, radius);
            points.Sort(ComparePoint);

            var unique = new List<Vector2>(points.Count);
            foreach (var point in points)
            {
                if (unique.Count != 0
                    && unique[unique.Count - 1] == point)
                    continue;
                unique.Add(point);
            }

            if (unique.Count <= 2)
                return unique;

            var lower = new List<Vector2>();
            foreach (var point in unique)
            {
                while (lower.Count >= 2
                       && Cross(
                           lower[lower.Count - 2],
                           lower[lower.Count - 1],
                           point) <= 0f)
                    lower.RemoveAt(lower.Count - 1);
                lower.Add(point);
            }

            var upper = new List<Vector2>();
            for (var index = unique.Count - 1;
                 index >= 0;
                 index -= 1)
            {
                var point = unique[index];
                while (upper.Count >= 2
                       && Cross(
                           upper[upper.Count - 2],
                           upper[upper.Count - 1],
                           point) <= 0f)
                    upper.RemoveAt(upper.Count - 1);
                upper.Add(point);
            }

            lower.RemoveAt(lower.Count - 1);
            upper.RemoveAt(upper.Count - 1);
            lower.AddRange(upper);
            return lower;
        }

        private static void AddSquareCorners(
            List<Vector2> points,
            Vector2 center,
            float radius)
        {
            points.Add(center + new Vector2(-radius, -radius));
            points.Add(center + new Vector2(-radius, radius));
            points.Add(center + new Vector2(radius, -radius));
            points.Add(center + new Vector2(radius, radius));
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
