#nullable enable
using System;
using System.Collections.Generic;
using net.rs64.TexTransCore;
using UnityEngine;
using UnityEngine.Rendering;

namespace net.rs64.TexTransTool.TextureAtlas
{
    internal readonly struct AtlasSourceCoverageMask
    {
        internal readonly RectInt Bounds;
        internal readonly bool[] Pixels;

        internal AtlasSourceCoverageMask(RectInt bounds, bool[] pixels)
        {
            Bounds = bounds;
            Pixels = pixels;
        }

        internal bool IsCovered(int x, int y)
        {
            var localX = x - Bounds.x;
            var localY = y - Bounds.y;
            return localX >= 0
                && localX < Bounds.width
                && localY >= 0
                && localY < Bounds.height
                && Pixels[localY * Bounds.width + localX];
        }
    }

    /// <summary>
    /// Builds the source-texel coverage used by source-side Atlas
    /// optimizations. Coverage is rasterized on the GPU; CPU work is bounded
    /// by triangle count rather than texture pixels times triangle count.
    ///
    /// The polygon rendered for each UV triangle is the Minkowski sum of that
    /// triangle and a one-pixel axis-aligned square. This is the same
    /// conservative linear-filter support model previously evaluated by
    /// TriangleOverlapsTexelFilter. Pixels outside the source image are folded
    /// through both clamp and repeat addressing because shader sampler aliases
    /// are not exposed by the texture-usage registry.
    /// </summary>
    internal static class AtlasSourceCoverageGpu
    {
        private const string ShaderName =
            "Hidden/TexTransTool/AtlasSourceCoverage";
        private const int Padding = 4;
        private const float FilterRadius = 1f;

        internal static bool TryRender(
            IEnumerable<(Mesh mesh, int subMesh)> slots,
            int uvChannel,
            Matrix2x3 matrix,
            int width,
            int height,
            out AtlasSourceCoverageMask coverage)
        {
            coverage = default;
            if (width <= 0 || height <= 0)
                return false;
            if (!SystemInfo.SupportsRenderTextureFormat(
                    RenderTextureFormat.ARGB32))
                return false;

            var shader = Shader.Find(ShaderName);
            if (shader == null || !shader.isSupported)
                return false;

            Mesh? coverageMesh = null;
            Material? material = null;
            RenderTexture? rt = null;
            Texture2D? readback = null;
            CommandBuffer? command = null;
            var previousActive = RenderTexture.active;

            try
            {
                if (!TryBuildCoverageMesh(
                        slots,
                        uvChannel,
                        matrix,
                        width,
                        height,
                        out coverageMesh,
                        out var rawBounds))
                    return false;

                var rtWidth = width + Padding * 2;
                var rtHeight = height + Padding * 2;

                material = new Material(shader)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                };
                rt = RenderTexture.GetTemporary(
                    rtWidth,
                    rtHeight,
                    0,
                    RenderTextureFormat.ARGB32,
                    RenderTextureReadWrite.Linear,
                    1);
                rt.filterMode = FilterMode.Point;
                rt.antiAliasing = 1;

                command = new CommandBuffer
                {
                    name = "TTT Atlas Source UV Coverage",
                };
                command.SetRenderTarget(rt);
                command.ClearRenderTarget(true, true, Color.black);
                command.DrawMesh(
                    coverageMesh,
                    Matrix4x4.identity,
                    material,
                    0,
                    0);
                Graphics.ExecuteCommandBuffer(command);

                RenderTexture.active = rt;
                readback = new Texture2D(
                    rtWidth,
                    rtHeight,
                    TextureFormat.RGBA32,
                    false,
                    true)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                };
                readback.ReadPixels(
                    new Rect(0, 0, rtWidth, rtHeight),
                    0,
                    0,
                    false);
                var pixels = readback.GetPixels32(0);
                if (pixels.Length != rtWidth * rtHeight
                    || !TryResolveUvYOrientation(
                        pixels,
                        rtWidth,
                        rtHeight,
                        out var flipRows))
                    return false;

                var startX = Mathf.Max(
                    -Padding,
                    rawBounds.xMin);
                var endX = Mathf.Min(
                    width + Padding - 1,
                    rawBounds.xMax - 1);
                var startY = Mathf.Max(
                    -Padding,
                    rawBounds.yMin);
                var endY = Mathf.Min(
                    height + Padding - 1,
                    rawBounds.yMax - 1);

                Span<int> xs = stackalloc int[2];
                Span<int> ys = stackalloc int[2];
                var sourceMinX = width;
                var sourceMinY = height;
                var sourceMaxX = -1;
                var sourceMaxY = -1;

                // Derive the source readback bounds from pixels that the GPU
                // actually rasterized, not from a triangle AABB estimate.
                // This avoids turning a near-edge UV into an unnecessary
                // full-width/full-height source readback.
                for (var y = startY; y <= endY; y += 1)
                for (var x = startX; x <= endX; x += 1)
                {
                    if (!IsCoveragePixel(
                            pixels,
                            rtWidth,
                            rtHeight,
                            flipRows,
                            x,
                            y))
                        continue;

                    var xCount = AddressCandidates(x, width, xs);
                    var yCount = AddressCandidates(y, height, ys);
                    for (var yi = 0; yi < yCount; yi += 1)
                    for (var xi = 0; xi < xCount; xi += 1)
                    {
                        sourceMinX = Math.Min(sourceMinX, xs[xi]);
                        sourceMinY = Math.Min(sourceMinY, ys[yi]);
                        sourceMaxX = Math.Max(sourceMaxX, xs[xi]);
                        sourceMaxY = Math.Max(sourceMaxY, ys[yi]);
                    }
                }

                if (sourceMaxX < sourceMinX
                    || sourceMaxY < sourceMinY)
                    return false;

                var sourceBounds = new RectInt(
                    sourceMinX,
                    sourceMinY,
                    sourceMaxX - sourceMinX + 1,
                    sourceMaxY - sourceMinY + 1);
                var mask = new bool[
                    sourceBounds.width * sourceBounds.height];
                var count = 0;

                for (var y = startY; y <= endY; y += 1)
                for (var x = startX; x <= endX; x += 1)
                {
                    if (!IsCoveragePixel(
                            pixels,
                            rtWidth,
                            rtHeight,
                            flipRows,
                            x,
                            y))
                        continue;

                    var xCount = AddressCandidates(x, width, xs);
                    var yCount = AddressCandidates(y, height, ys);
                    for (var yi = 0; yi < yCount; yi += 1)
                    for (var xi = 0; xi < xCount; xi += 1)
                    {
                        var localX = xs[xi] - sourceBounds.x;
                        var localY = ys[yi] - sourceBounds.y;
                        var index =
                            localY * sourceBounds.width + localX;
                        if (mask[index])
                            continue;
                        mask[index] = true;
                        count += 1;
                    }
                }

                if (count == 0)
                    return false;

                coverage =
                    new AtlasSourceCoverageMask(sourceBounds, mask);
                return true;
            }
            catch
            {
                coverage = default;
                return false;
            }
            finally
            {
                RenderTexture.active = previousActive;
                if (command != null) command.Dispose();
                if (readback != null)
                    UnityEngine.Object.DestroyImmediate(readback);
                if (rt != null)
                    RenderTexture.ReleaseTemporary(rt);
                if (material != null)
                    UnityEngine.Object.DestroyImmediate(material);
                if (coverageMesh != null)
                    UnityEngine.Object.DestroyImmediate(coverageMesh);
            }
        }

        private static bool TryBuildCoverageMesh(
            IEnumerable<(Mesh mesh, int subMesh)> slots,
            int uvChannel,
            Matrix2x3 matrix,
            int width,
            int height,
            out Mesh? mesh,
            out RectInt rawBounds)
        {
            mesh = null;
            rawBounds = default;

            var vertices = new List<Vector3>();
            var colors = new List<Color32>();
            var indices = new List<int>();
            var uvs = new List<Vector2>();

            var rawMinX = int.MaxValue;
            var rawMinY = int.MaxValue;
            var rawMaxX = int.MinValue;
            var rawMaxY = int.MinValue;
            foreach (var (sourceMesh, subMesh) in slots)
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

                    a = new Vector2(
                        a.x * width - 0.5f,
                        a.y * height - 0.5f);
                    b = new Vector2(
                        b.x * width - 0.5f,
                        b.y * height - 0.5f);
                    c = new Vector2(
                        c.x * width - 0.5f,
                        c.y * height - 0.5f);

                    var hull = BuildExpandedHull(
                        a,
                        b,
                        c,
                        FilterRadius);
                    if (hull.Count < 3)
                        return false;

                    var triangleMinX = Mathf.FloorToInt(
                        hull[0].x);
                    var triangleMaxX = Mathf.CeilToInt(
                        hull[0].x);
                    var triangleMinY = Mathf.FloorToInt(
                        hull[0].y);
                    var triangleMaxY = Mathf.CeilToInt(
                        hull[0].y);
                    foreach (var point in hull)
                    {
                        triangleMinX = Mathf.Min(
                            triangleMinX,
                            Mathf.FloorToInt(point.x));
                        triangleMaxX = Mathf.Max(
                            triangleMaxX,
                            Mathf.CeilToInt(point.x));
                        triangleMinY = Mathf.Min(
                            triangleMinY,
                            Mathf.FloorToInt(point.y));
                        triangleMaxY = Mathf.Max(
                            triangleMaxY,
                            Mathf.CeilToInt(point.y));
                    }

                    rawMinX = Math.Min(rawMinX, triangleMinX);
                    rawMinY = Math.Min(rawMinY, triangleMinY);
                    rawMaxX = Math.Max(rawMaxX, triangleMaxX);
                    rawMaxY = Math.Max(rawMaxY, triangleMaxY);

                    var vertexStart = vertices.Count;
                    foreach (var point in hull)
                    {
                        vertices.Add(new Vector3(
                            ToClipCoordinate(
                                point.x,
                                width + Padding * 2),
                            ToClipCoordinate(
                                point.y,
                                height + Padding * 2),
                            0f));
                        colors.Add(new Color32(
                            255,
                            0,
                            0,
                            255));
                    }

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

            if (indices.Count == 0
                || rawMaxX < rawMinX
                || rawMaxY < rawMinY)
                return false;

            // Add a one-pixel green marker on the physical lower row of the
            // padded target. Coverage can reach at most two pixels beyond the
            // source bounds, while this marker lives four pixels outside.
            var markerStart = vertices.Count;
            vertices.Add(new Vector3(-1f, -1f, 0f));
            vertices.Add(new Vector3(1f, -1f, 0f));
            vertices.Add(new Vector3(
                1f,
                -1f + 2f / (height + Padding * 2),
                0f));
            vertices.Add(new Vector3(
                -1f,
                -1f + 2f / (height + Padding * 2),
                0f));
            for (var i = 0; i < 4; i += 1)
                colors.Add(new Color32(0, 255, 0, 255));
            indices.Add(markerStart);
            indices.Add(markerStart + 1);
            indices.Add(markerStart + 2);
            indices.Add(markerStart);
            indices.Add(markerStart + 2);
            indices.Add(markerStart + 3);

            mesh = new Mesh
            {
                hideFlags = HideFlags.HideAndDontSave,
                indexFormat = vertices.Count > 65535
                    ? IndexFormat.UInt32
                    : IndexFormat.UInt16,
            };
            mesh.SetVertices(vertices);
            mesh.SetColors(colors);
            mesh.SetTriangles(indices, 0, true);

            rawBounds = new RectInt(
                rawMinX,
                rawMinY,
                rawMaxX - rawMinX + 1,
                rawMaxY - rawMinY + 1);
            return true;
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
            return x != 0 ? x : left.y.CompareTo(right.y);
        }

        private static float Cross(
            Vector2 origin,
            Vector2 a,
            Vector2 b)
        {
            return (a.x - origin.x) * (b.y - origin.y)
                - (a.y - origin.y) * (b.x - origin.x);
        }

        private static float ToClipCoordinate(
            float sourceCoordinate,
            int targetSize)
        {
            return ((sourceCoordinate + Padding + 0.5f)
                    / targetSize)
                * 2f - 1f;
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

        private static bool IsCoveragePixel(
            Color32[] pixels,
            int renderWidth,
            int renderHeight,
            bool flipRows,
            int sourceX,
            int sourceY)
        {
            var renderX = sourceX + Padding;
            var logicalRenderY = sourceY + Padding;
            var readY = flipRows
                ? renderHeight - 1 - logicalRenderY
                : logicalRenderY;
            return renderX >= 0
                && renderX < renderWidth
                && readY >= 0
                && readY < renderHeight
                && pixels[readY * renderWidth + renderX].r >= 128;
        }

        private static int AddressCandidates(
            int value,
            int size,
            Span<int> output)
        {
            if (value >= 0 && value < size)
            {
                output[0] = value;
                return 1;
            }

            var clamp = Mathf.Clamp(value, 0, size - 1);
            var repeat = ((value % size) + size) % size;
            output[0] = clamp;
            if (repeat == clamp)
                return 1;
            output[1] = repeat;
            return 2;
        }

        internal static bool TryResolveUvYOrientation(
            Color32[] pixels,
            int width,
            int height,
            out bool flipRows)
        {
            flipRows = false;
            if (width <= 0
                || height < 2
                || pixels.Length != width * height)
                return false;

            var bottomMarker = 0;
            var topMarker = 0;
            var top = (height - 1) * width;
            for (var x = 0; x < width; x += 1)
            {
                if (pixels[x].g >= 128)
                    bottomMarker += 1;
                if (pixels[top + x].g >= 128)
                    topMarker += 1;
            }

            if (bottomMarker != 0 && topMarker == 0)
                return true;
            if (topMarker != 0 && bottomMarker == 0)
            {
                flipRows = true;
                return true;
            }

            return false;
        }
    }
}
