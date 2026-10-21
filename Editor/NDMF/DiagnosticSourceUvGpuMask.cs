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
            out bool[] coverage, out int count, out string reason)
        {
            coverage = Array.Empty<bool>();
            count = 0;
            reason = "";
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
            Material? material = null;
            RenderTexture? rt = null;
            Texture2D? readback = null;
            var previousActive = RenderTexture.active;
            bool pushedMatrix = false;
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
                RenderTexture.active = rt;
                GL.Clear(true, true, Color.black);
                GL.PushMatrix();
                pushedMatrix = true;
                GL.LoadIdentity();
                if (!material.SetPass(0))
                {
                    reason = "gpu-mask-setpass-failed";
                    return false;
                }
                foreach (var draw in drawMeshes)
                    Graphics.DrawMeshNow(draw, Matrix4x4.identity);

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
                var mask = new bool[pixels.Length];
                for (int i = 0; i < pixels.Length; ++i)
                {
                    if (pixels[i].r < 128) continue;
                    mask[i] = true;
                    ++count;
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
                if (pushedMatrix) GL.PopMatrix();
                RenderTexture.active = previousActive;
                if (readback != null) UnityEngine.Object.DestroyImmediate(readback);
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
                if (material != null) UnityEngine.Object.DestroyImmediate(material);
                foreach (var mesh in drawMeshes)
                    UnityEngine.Object.DestroyImmediate(mesh);
            }
        }
    }
}
