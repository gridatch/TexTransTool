#nullable enable
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace net.rs64.TexTransTool.NDMF
{
    /// <summary>
    /// Temporary diagnostic: rasterize actual source UV triangles to a
    /// pixel-center coverage mask. It never reads the generated atlas and
    /// never decides whether to remove an original texture.
    /// This is a candidate screen, NOT a bilinear-filter footprint.
    /// </summary>
    internal static class DiagnosticSourceUvGpuMask
    {
        private const string ShaderName =
            "Hidden/TexTransTool/DiagnosticSourceUvCoverage";

        internal static bool TryRender(
            HashSet<(Mesh, int)> slots,
            Vector2 scale, Vector2 offset,
            int width, int height, long maxPixels,
            out bool[] coverage, out int count, out string reason) =>
            TryRender(slots, scale, offset, width, height, maxPixels,
                out coverage, out count, out reason, out _);

        // The marker determines the render target's actual Y orientation
        // without relying on GraphicsDeviceType or an assumed Unity backend.
        internal static bool TryRender(
            HashSet<(Mesh, int)> slots,
            Vector2 scale, Vector2 offset,
            int width, int height, long maxPixels,
            out bool[] coverage, out int count, out string reason,
            out bool verticalFlipCorrected)
        {
            coverage = Array.Empty<bool>();
            count = 0;
            reason = "";
            verticalFlipCorrected = false;
            if (width <= 0 || height <= 0
                || (long)width * height > maxPixels)
            {
                reason = "gpu-mask-size-limit";
                return false;
            }
            if (!SystemInfo.SupportsRenderTextureFormat(
                    RenderTextureFormat.ARGB32))
            {
                reason = "gpu-mask-format-unsupported";
                return false;
            }
            var shader = Shader.Find(ShaderName);
            if (shader == null || !shader.isSupported)
            {
                reason = "gpu-mask-shader-unavailable";
                return false;
            }

            var drawMeshes = new List<Mesh>();
            Mesh? orientationMarker = null;
            Material? material = null;
            RenderTexture? rt = null;
            Texture2D? readback = null;
            CommandBuffer? command = null;
            var previousActive = RenderTexture.active;
            try
            {
                // Validate all slots before drawing. No approximation by a
                // UV bounding rectangle, and no implicit clamp of UVs.
                foreach (var (sourceMesh, submesh) in slots)
                {
                    if (sourceMesh == null || submesh < 0
                        || submesh >= sourceMesh.subMeshCount
                        || sourceMesh.GetTopology(submesh) != MeshTopology.Triangles)
                    {
                        reason = "gpu-mask-nontriangle-submesh";
                        return false;
                    }
                    var uv = new List<Vector2>();
                    sourceMesh.GetUVs(0, uv);
                    var indices = sourceMesh.GetTriangles(submesh);
                    if (uv.Count == 0 || indices.Length == 0
                        || indices.Length % 3 != 0)
                    {
                        reason = "gpu-mask-no-UV-triangles";
                        return false;
                    }
                    var vertices = new Vector3[uv.Count];
                    // Match the strict baseline: unrelated, unreferenced
                    // UV vertices must not invalidate a drawable submesh.
                    foreach (int index in indices)
                    {
                        if (index < 0 || index >= vertices.Length)
                        {
                            reason = "gpu-mask-invalid-index";
                            return false;
                        }
                        var coord = Vector2.Scale(uv[index], scale) + offset;
                        if (float.IsNaN(coord.x) || float.IsNaN(coord.y)
                            || float.IsInfinity(coord.x) || float.IsInfinity(coord.y)
                            || coord.x < 0f || coord.x > 1f
                            || coord.y < 0f || coord.y > 1f)
                        {
                            reason = "gpu-mask-UV-outside-0-to-1";
                            return false;
                        }
                        vertices[index] = new Vector3(coord.x * 2f - 1f,
                            coord.y * 2f - 1f, 0f);
                    }

                    var draw = new Mesh
                    {
                        hideFlags = HideFlags.HideAndDontSave,
                        indexFormat = vertices.Length > 65535
                            ? IndexFormat.UInt32 : IndexFormat.UInt16
                    };
                    draw.vertices = vertices;
                    draw.SetTriangles(indices, 0);
                    drawMeshes.Add(draw);
                }

                material = new Material(shader)
                {
                    hideFlags = HideFlags.HideAndDontSave
                };
                rt = RenderTexture.GetTemporary(width, height, 0,
                    RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear, 1);
                rt.filterMode = FilterMode.Point;
                rt.antiAliasing = 1;
                // Issue offscreen rendering directly. This is called from
                // the NDMF editor build pass, not a camera render callback.
                command = new CommandBuffer
                {
                    name = "TTT Diagnostic Source UV Interior Mask"
                };
                command.SetRenderTarget(rt);
                command.ClearRenderTarget(true, true, Color.black);
                var sourceChannel = new MaterialPropertyBlock();
                sourceChannel.SetColor("_MaskColor", Color.red);
                foreach (var draw in drawMeshes)
                    command.DrawMesh(draw, Matrix4x4.identity, material, 0, 0,
                        sourceChannel);

                // A green quad in the LOWER quarter of UV space measures
                // vertical orientation. Its green channel never modifies
                // the red coverage mask, including on overlap.
                orientationMarker = new Mesh
                {
                    hideFlags = HideFlags.HideAndDontSave
                };
                orientationMarker.vertices = new[]
                {
                    new Vector3(-1f, -1f, 0f),
                    new Vector3( 1f, -1f, 0f),
                    new Vector3( 1f, -0.5f, 0f),
                    new Vector3(-1f, -0.5f, 0f),
                };
                orientationMarker.SetTriangles(
                    new[] { 0, 1, 2, 0, 2, 3 }, 0);
                var orientationChannel = new MaterialPropertyBlock();
                orientationChannel.SetColor("_MaskColor", Color.green);
                command.DrawMesh(orientationMarker, Matrix4x4.identity,
                    material, 0, 0, orientationChannel);
                Graphics.ExecuteCommandBuffer(command);

                RenderTexture.active = rt;
                readback = new Texture2D(width, height,
                    TextureFormat.RGBA32, false, true)
                {
                    hideFlags = HideFlags.HideAndDontSave
                };
                readback.ReadPixels(new Rect(0, 0, width, height),
                    0, 0, false);
                var pixels = readback.GetPixels32(0);
                if (pixels.Length != width * height)
                {
                    reason = "gpu-mask-readback-size-mismatch";
                    return false;
                }
                // Unity's readback orientation differs between rendering
                // backends. Orient rows by a rendered marker, NOT by the
                // triangle coverage itself or by the CPU baseline.
                if (!TryResolveUvYOrientation(pixels, width, height,
                        out verticalFlipCorrected))
                {
                    reason = "gpu-mask-y-orientation-unresolved";
                    return false;
                }
                var mask = new bool[pixels.Length];
                for (int y = 0; y < height; ++y)
                {
                    var readY = verticalFlipCorrected ? height - 1 - y : y;
                    for (int x = 0; x < width; ++x)
                    {
                        if (pixels[readY * width + x].r < 128) continue;
                        mask[y * width + x] = true;
                        ++count;
                    }
                }
                if (count == 0)
                {
                    reason = "gpu-mask-empty-or-subpixel";
                    return false;
                }
                coverage = mask;
                return true;
            }
            catch (Exception ex)
            {
                reason = "gpu-mask-error:" + ex.GetType().Name;
                return false;
            }
            finally
            {
                RenderTexture.active = previousActive;
                if (command != null) command.Dispose();
                if (readback != null) UnityEngine.Object.DestroyImmediate(readback);
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
                if (material != null) UnityEngine.Object.DestroyImmediate(material);
                if (orientationMarker != null)
                    UnityEngine.Object.DestroyImmediate(orientationMarker);
                foreach (var mesh in drawMeshes)
                    UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        internal static bool TryResolveUvYOrientation(
            Color32[] pixels, int width, int height, out bool flipRows)
        {
            flipRows = false;
            if (width <= 0 || height < 4 || pixels.Length != width * height)
                return false;
            int bottom = 0;
            int top = (height - 1) * width;
            int bottomMarker = 0;
            int topMarker = 0;
            for (int x = 0; x < width; ++x)
            {
                if (pixels[bottom + x].g >= 128) ++bottomMarker;
                if (pixels[top + x].g >= 128) ++topMarker;
            }
            // A marker appears on exactly one of the opposing rows.
            // Ambiguity returns unknown instead of silently misaligning UVs.
            if (bottomMarker != 0 && topMarker == 0) return true;
            if (topMarker != 0 && bottomMarker == 0)
            {
                flipRows = true;
                return true;
            }
            return false;
        }
    }
}
