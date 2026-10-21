using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;

namespace net.rs64.TexTransTool.NDMF.TestCode
{
    internal sealed class AutoAtlasEmissionProbeTest
    {
        // The triangle spans texel-center coordinates
        // (1.5,1.5), (3.6,1.5), (1.5,3.6) in an 8x8 image.
        private static Mesh Triangle()
        {
            var mesh = new Mesh();
            mesh.vertices = new[]
            {
                Vector3.zero, Vector3.right, Vector3.up,
            };
            mesh.uv = new[]
            {
                new Vector2(0.25f, 0.25f),
                new Vector2(0.5125f, 0.25f),
                new Vector2(0.25f, 0.5125f),
            };
            mesh.SetTriangles(new[] { 0, 1, 2 }, 0);
            return mesh;
        }

        private static bool[] Coverage(Mesh mesh, FilterMode filterMode,
            TextureWrapMode wrapU = TextureWrapMode.Clamp,
            TextureWrapMode wrapV = TextureWrapMode.Clamp)
        {
            var probe = new AutoAtlasEmissionProbe();
            var slots = new HashSet<(Mesh, int)> { (mesh, 0) };
            bool success = probe.TrySourceCoverage(
                slots, Vector2.one, Vector2.zero, 8, 8,
                wrapU, wrapV, filterMode,
                out var coverage, out var count, out var reason);
            Assert.That(success, Is.True, reason);
            Assert.That(count, Is.GreaterThan(0));
            return coverage;
        }

        [Test]
        public void PixelNearUvBoundaryContributesToBilinearButNotPoint()
        {
            var a = new Vector2(1.5f, 1.5f);
            var b = new Vector2(3.6f, 1.5f);
            var c = new Vector2(1.5f, 3.6f);
            var adjacent = new Vector2(4f, 3f);
            Assert.That(AutoAtlasEmissionProbe.TriangleOverlapsTexelFilter(
                adjacent, a, b, c, 1f), Is.True);
            Assert.That(AutoAtlasEmissionProbe.TriangleOverlapsTexelFilter(
                adjacent, a, b, c, 0.5f), Is.False);
            // Shrinking the diagonal to x+y=5 makes (4,3) touch the
            // bilinear square at one corner with zero weight.
            Assert.That(AutoAtlasEmissionProbe.TriangleOverlapsTexelFilter(
                adjacent, a, new Vector2(3.5f, 1.5f),
                new Vector2(1.5f, 3.5f), 1f), Is.False);
            Assert.That(AutoAtlasEmissionProbe.TriangleOverlapsTexelFilter(
                new Vector2(7f, 7f), a, b, c, 1f), Is.False);
        }

        [Test]
        public void FloatSubByteEmissionAtUvBoundaryMustNotBeCalledZero()
        {
            var mesh = Triangle();
            try
            {
                var bilinear = Coverage(mesh, FilterMode.Bilinear);
                var point = Coverage(mesh, FilterMode.Point);
                int adjacentIndex = 3 * 8 + 4;
                Assert.That(bilinear[adjacentIndex], Is.True);
                Assert.That(point[adjacentIndex], Is.False);

                var pixels = new Color[64];
                for (int i = 0; i < pixels.Length; ++i)
                    pixels[i] = new Color(0f, 0f, 0f, 1f);

                // Much smaller than an RGBA32 channel step. 8-bit staging
                // can round this to zero, so a float pixel path is essential.
                const float tiny = 1f / 65535f;
                pixels[adjacentIndex] = new Color(tiny, 0f, 0f, 1f);
                var linearScan = AutoAtlasEmissionProbe.ScanCoveredSourcePixels(
                    pixels, bilinear);
                var pointScan = AutoAtlasEmissionProbe.ScanCoveredSourcePixels(
                    pixels, point);

                Assert.That(linearScan.rgbZero, Is.False);
                Assert.That(linearScan.nonzeroRgbTexels, Is.EqualTo(1));
                Assert.That(linearScan.maxAbsRgb, Is.EqualTo(tiny));
                Assert.That(pointScan.rgbZero, Is.True);
                Assert.That(pointScan.nonzeroRgbTexels, Is.Zero);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void NonzeroOutsideActualUvFootprintCannotDisqualifyCandidate()
        {
            var mesh = Triangle();
            try
            {
                var coverage = Coverage(mesh, FilterMode.Bilinear);
                var pixels = new Color[64];
                for (int i = 0; i < pixels.Length; ++i)
                    pixels[i] = new Color(0f, 0f, 0f, 1f);
                pixels[7 * 8 + 7] = Color.white;
                Assert.That(coverage[7 * 8 + 7], Is.False);
                var scan = AutoAtlasEmissionProbe.ScanCoveredSourcePixels(
                    pixels, coverage);
                Assert.That(scan.rgbZero, Is.True);
                Assert.That(scan.alphaZero, Is.False);
                Assert.That(scan.nonzeroRgbTexels, Is.Zero);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void ReadableFloatTextureDoesNotQuantizeMicroEmission()
        {
            var texture = new Texture2D(8, 8, TextureFormat.RGBAFloat,
                false, true);
            try
            {
                var pixels = new Color[64];
                for (int i = 0; i < pixels.Length; ++i)
                    pixels[i] = new Color(0f, 0f, 0f, 1f);
                pixels[3 * 8 + 4] = new Color(1f / 65535f, 0f, 0f, 1f);
                texture.SetPixels(pixels);
                texture.Apply(updateMipmaps: false, makeNoLongerReadable: false);
                var read = new AutoAtlasEmissionProbe().ReadOriginalForTest(texture);
                Assert.That(read.status, Is.EqualTo("source-CPU-float"));
                Assert.That(read.pixels, Is.Not.Null);
                Assert.That(read.pixels![3 * 8 + 4].r,
                    Is.GreaterThan(0f));
                Assert.That(read.pixels![3 * 8 + 4].r,
                    Is.LessThan(1f / 255f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        [Test]
        public void GpuStagingPreservesMicroEmissionIfFloatRenderTargetsSupported()
        {
            if (!SystemInfo.SupportsRenderTextureFormat(
                    RenderTextureFormat.ARGBFloat))
                Assert.Ignore("GPU does not support floating-point render targets.");

            var source = new Texture2D(8, 8, TextureFormat.RGBAFloat,
                false, true);
            var rt = RenderTexture.GetTemporary(8, 8, 0,
                RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            try
            {
                var pixels = new Color[64];
                for (int i = 0; i < pixels.Length; ++i)
                    pixels[i] = new Color(0f, 0f, 0f, 1f);
                pixels[3 * 8 + 4] = new Color(1f / 65535f, 0f, 0f, 1f);
                source.SetPixels(pixels);
                source.Apply(updateMipmaps: false, makeNoLongerReadable: false);
                Graphics.Blit(source, rt);
                // Input is a non-Texture2D GPU resource: this forces the
                // exact production staging / readback path.
                var read = new AutoAtlasEmissionProbe().ReadOriginalForTest(rt);
                Assert.That(read.status, Does.StartWith("source-GPU-staged-"));
                Assert.That(read.pixels, Is.Not.Null);
                Assert.That(read.pixels![3 * 8 + 4].r,
                    Is.GreaterThan(0f), read.status);
                Assert.That(read.pixels![3 * 8 + 4].r,
                    Is.LessThan(1f / 255f));
            }
            finally
            {
                RenderTexture.ReleaseTemporary(rt);
                UnityEngine.Object.DestroyImmediate(source);
            }
        }
        [Test]
        public void GenericSourceSurveyFindsUniformTexturesBeyondEmission()
        {
            var pixels = new Color[16];
            for (int i = 0; i < pixels.Length; ++i)
                pixels[i] = new Color(0.3f, 0.6f, 0.9f, 1f);

            var result = AutoAtlasEmissionProbe.ScanSourcePixels(pixels, null);
            Assert.That(result.isConstant, Is.True);
            Assert.That(result.rgbZero, Is.False);
            Assert.That(result.constantColor.r, Is.EqualTo(0.3f));
            Assert.That(result.sampledTexels, Is.EqualTo(16));

            pixels[15] = new Color(0.3f, 0.6f, 0.900001f, 1f);
            result = AutoAtlasEmissionProbe.ScanSourcePixels(pixels, null);
            Assert.That(result.isConstant, Is.False,
                "Even a small real RGB difference must remain visible.");
        }

        [Test]
        public void UsedUvCanBeConstantEvenWhenWholeTextureIsNot()
        {
            var mesh = Triangle();
            try
            {
                var covered = Coverage(mesh, FilterMode.Bilinear);
                var pixels = new Color[64];
                for (int i = 0; i < pixels.Length; ++i)
                    pixels[i] = new Color(0f, 0f, 0f, 1f);
                pixels[7 * 8 + 7] = Color.white;

                var whole = AutoAtlasEmissionProbe.ScanSourcePixels(
                    pixels, null);
                var used = AutoAtlasEmissionProbe.ScanCoveredSourcePixels(
                    pixels, covered);
                Assert.That(whole.isConstant, Is.False);
                Assert.That(used.isConstant, Is.True);
                Assert.That(used.constantColor.r, Is.Zero);
                Assert.That(used.sampledTexels,
                    Is.LessThan(whole.sampledTexels));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void SourceImageCacheEvictionDoesNotStarveLaterMaterials()
        {
            var first = new Texture2D(8, 8, TextureFormat.RGBAFloat, false, true);
            var second = new Texture2D(8, 8, TextureFormat.RGBAFloat, false, true);
            var third = new Texture2D(8, 8, TextureFormat.RGBAFloat, false, true);
            try
            {
                var pixels = new Color[64];
                for (int i = 0; i < pixels.Length; i++)
                    pixels[i] = new Color(0f, 0f, 0f, 1f);
                pixels[0] = new Color(0.5f, 0f, 0f, 1f);
                first.SetPixels(pixels);
                first.Apply(false, false);

                var probe = new AutoAtlasEmissionProbe(residentPixelBudget: 64);
                var initial = probe.ReadOriginalForTest(first);
                Assert.That(initial.status, Is.EqualTo("source-CPU-float"));
                Assert.That(initial.pixels, Is.Not.Null);
                Assert.That(initial.pixels![0].r, Is.EqualTo(0.5f));

                var later = probe.ReadOriginalForTest(second);
                Assert.That(later.status, Is.EqualTo("source-CPU-float"));
                Assert.That(later.pixels, Is.Not.Null);

                var evenLater = probe.ReadOriginalForTest(third);
                Assert.That(evenLater.status, Is.EqualTo("source-CPU-float"));
                Assert.That(evenLater.pixels, Is.Not.Null);

                var revisited = probe.ReadOriginalForTest(first);
                Assert.That(revisited.status, Is.EqualTo("source-CPU-float"));
                Assert.That(revisited.pixels, Is.Not.Null);
                Assert.That(revisited.pixels![0].r, Is.EqualTo(0.5f));

                var report = probe.Report();
                Assert.That(report, Does.Contain("evictions 3"));
                Assert.That(report, Does.Contain("reloads 1"));
                Assert.That(report, Does.Contain("Unique 2D source images loaded: 3"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(first);
                UnityEngine.Object.DestroyImmediate(second);
                UnityEngine.Object.DestroyImmediate(third);
            }
        }

        [Test]
        public void SingleOversizedImageIsUnknownWithoutBlockingSmallSources()
        {
            var oversized = new Texture2D(9, 8, TextureFormat.RGBAFloat,
                false, true);
            var small = new Texture2D(8, 8, TextureFormat.RGBAFloat,
                false, true);
            try
            {
                var probe = new AutoAtlasEmissionProbe(residentPixelBudget: 64);
                var failure = probe.ReadOriginalForTest(oversized);
                Assert.That(failure.pixels, Is.Null);
                Assert.That(failure.status,
                    Is.EqualTo("source-exceeds-per-image-memory-budget"));
                var success = probe.ReadOriginalForTest(small);
                Assert.That(success.pixels, Is.Not.Null);
                Assert.That(success.status, Is.EqualTo("source-CPU-float"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(oversized);
                UnityEngine.Object.DestroyImmediate(small);
            }
        }

        [Test]
        public void GpuUvOrientationMarkerDistinguishesBothRowOrders()
        {
            const int width = 8;
            const int height = 8;
            var pixels = new Color32[width * height];
            for (int x = 0; x < width; ++x)
                pixels[x] = new Color32(0, 255, 0, 255);

            Assert.That(DiagnosticSourceUvGpuMask.TryResolveUvYOrientation(
                pixels, width, height, out var flip), Is.True);
            Assert.That(flip, Is.False);

            Array.Clear(pixels, 0, pixels.Length);
            for (int x = 0; x < width; ++x)
                pixels[(height - 1) * width + x]
                    = new Color32(0, 255, 0, 255);
            Assert.That(DiagnosticSourceUvGpuMask.TryResolveUvYOrientation(
                pixels, width, height, out flip), Is.True);
            Assert.That(flip, Is.True);

            pixels[0] = new Color32(0, 255, 0, 255);
            Assert.That(DiagnosticSourceUvGpuMask.TryResolveUvYOrientation(
                pixels, width, height, out flip), Is.False,
                "Ambiguous markers must fail closed.");

            Array.Clear(pixels, 0, pixels.Length);
            Assert.That(DiagnosticSourceUvGpuMask.TryResolveUvYOrientation(
                pixels, width, height, out flip), Is.False,
                "Missing marker must not imply an orientation.");
        }

        [Test]
        public void GpuUvTriangleScreenDoesNotUseBoundingRectangleAsCoverage()
        {
            if (!SystemInfo.SupportsRenderTextureFormat(
                    RenderTextureFormat.ARGB32)
                || Shader.Find("Hidden/TexTransTool/DiagnosticSourceUvCoverage") == null)
                Assert.Ignore("GPU UV mask shader/format unavailable.");

            var mesh = Triangle();
            try
            {
                var slots = new HashSet<(Mesh, int)> { (mesh, 0) };
                bool ok = DiagnosticSourceUvGpuMask.TryRender(
                    slots, Vector2.one, Vector2.zero, 8, 8, 64,
                    out var gpu, out var used, out var reason);
                Assert.That(ok, Is.True, reason);
                Assert.That(used, Is.GreaterThan(0));
                Assert.That(used, Is.LessThan(64));
                Assert.That(gpu[7 * 8 + 7], Is.False,
                    "Pixels in the enclosing rectangle or source image"
                    + " must not become occupied by default.");

                var strict = Coverage(mesh, FilterMode.Bilinear);
                for (int i = 0; i < gpu.Length; ++i)
                    if (gpu[i]) Assert.That(strict[i], Is.True,
                        "GPU interior pixel must be included in strict"
                        + " bilinear contributors, index=" + i);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void GpuInteriorUniformCannotApproveDifferentBilinearBoundary()
        {
            if (!SystemInfo.SupportsRenderTextureFormat(
                    RenderTextureFormat.ARGB32)
                || Shader.Find("Hidden/TexTransTool/DiagnosticSourceUvCoverage") == null)
                Assert.Ignore("GPU UV mask shader/format unavailable.");

            var mesh = Triangle();
            try
            {
                var slots = new HashSet<(Mesh, int)> { (mesh, 0) };
                bool ok = DiagnosticSourceUvGpuMask.TryRender(
                    slots, Vector2.one, Vector2.zero, 8, 8, 64,
                    out var gpu, out _, out var reason);
                Assert.That(ok, Is.True, reason);
                var strict = Coverage(mesh, FilterMode.Bilinear);

                int boundary = -1;
                for (int i = 0; i < strict.Length; ++i)
                    if (strict[i] && !gpu[i])
                    {
                        boundary = i;
                        break;
                    }
                Assert.That(boundary, Is.GreaterThanOrEqualTo(0),
                    "Bilinear footprint must include neighboring texels.");

                var pixels = new Color[64];
                for (int i = 0; i < pixels.Length; ++i)
                    pixels[i] = new Color(0f, 0f, 0f, 1f);
                pixels[boundary] = Color.white;

                var interior = AutoAtlasEmissionProbe.ScanCoveredSourcePixels(
                    pixels, gpu);
                var complete = AutoAtlasEmissionProbe.ScanCoveredSourcePixels(
                    pixels, strict);
                Assert.That(interior.isConstant, Is.True,
                    "The initial GPU screen should find this candidate.");
                Assert.That(complete.isConstant, Is.False,
                    "Strict boundary validation must reject the candidate.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        private static Mesh TriangleWithUvs(Vector2 a, Vector2 b, Vector2 c)
        {
            var mesh = new Mesh();
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
            mesh.uv = new[] { a, b, c };
            mesh.SetTriangles(new[] { 0, 1, 2 }, 0);
            return mesh;
        }

        [Test]
        public void GpuFirstSurveySkipsCpuRasterForClearlyVariableInterior()
        {
            if (!SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGB32)
                || Shader.Find("Hidden/TexTransTool/DiagnosticSourceUvCoverage") == null)
                Assert.Ignore("GPU UV mask shader/format unavailable.");

            var mesh = Triangle();
            try
            {
                var pixels = new Color[64];
                for (int i = 0; i < pixels.Length; ++i)
                    pixels[i] = new Color(i / 64f, 0f, 0f, 1f);
                var slots = new HashSet<(Mesh, int)> { (mesh, 0) };
                var probe = new AutoAtlasEmissionProbe();
                var result = probe.EvaluateSourceUvGpuFirst(
                    slots, Vector2.one, Vector2.zero, 8, 8,
                    TextureWrapMode.Clamp, TextureWrapMode.Clamp,
                    FilterMode.Bilinear, pixels,
                    wholeSourceConstant: false, label: "variable-test");
                Assert.That(result.constant, Is.False);
                Assert.That(result.status,
                    Does.StartWith("GPU-UV0-interior-texels="));
                Assert.That(probe.Report(),
                    Does.Contain("Triangle pixel checks: 0 "),
                    "Variable interiors must skip CPU triangle-pixel work.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void GpuFirstUniformCandidateUsesStrictCpuBoundary()
        {
            if (!SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGB32)
                || Shader.Find("Hidden/TexTransTool/DiagnosticSourceUvCoverage") == null)
                Assert.Ignore("GPU UV mask shader/format unavailable.");

            var mesh = Triangle();
            try
            {
                var slots = new HashSet<(Mesh, int)> { (mesh, 0) };
                Assert.That(DiagnosticSourceUvGpuMask.TryRender(
                    slots, Vector2.one, Vector2.zero, 8, 8, 64,
                    out var gpu, out _, out var reason), Is.True, reason);
                var cpu = Coverage(mesh, FilterMode.Bilinear);
                int boundary = Array.FindIndex(cpu, i => i); 
                // Find one texel contributed through filtering but not
                // covered by the triangle's GPU interior.
                for (int i = 0; i < cpu.Length; ++i)
                    if (cpu[i] && !gpu[i])
                    {
                        boundary = i;
                        break;
                    }
                Assert.That(boundary, Is.GreaterThanOrEqualTo(0));
                Assert.That(gpu[boundary], Is.False);

                var pixels = new Color[64];
                for (int i = 0; i < pixels.Length; ++i)
                    pixels[i] = new Color(0f, 0f, 0f, 1f);
                pixels[boundary] = Color.white;

                var probe = new AutoAtlasEmissionProbe();
                var result = probe.EvaluateSourceUvGpuFirst(
                    slots, Vector2.one, Vector2.zero, 8, 8,
                    TextureWrapMode.Clamp, TextureWrapMode.Clamp,
                    FilterMode.Bilinear, pixels,
                    wholeSourceConstant: false, label: "candidate-test");
                Assert.That(result.constant, Is.False);
                Assert.That(result.status, Does.StartWith("UV0-texels="));
                Assert.That(probe.Report(),
                    Does.Contain("boundary rejected: 1"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void GpuFirstSubpixelMaskFallsBackToCpuCoverage()
        {
            if (!SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGB32)
                || Shader.Find("Hidden/TexTransTool/DiagnosticSourceUvCoverage") == null)
                Assert.Ignore("GPU UV mask shader/format unavailable.");

            var mesh = TriangleWithUvs(
                new Vector2(0.502f, 0.502f),
                new Vector2(0.508f, 0.502f),
                new Vector2(0.502f, 0.508f));
            try
            {
                var pixels = new Color[64];
                for (int i = 0; i < pixels.Length; ++i)
                    pixels[i] = Color.white;
                pixels[0] = Color.black;
                var probe = new AutoAtlasEmissionProbe();
                var result = probe.EvaluateSourceUvGpuFirst(
                    new HashSet<(Mesh, int)> { (mesh, 0) },
                    Vector2.one, Vector2.zero, 8, 8,
                    TextureWrapMode.Clamp, TextureWrapMode.Clamp,
                    FilterMode.Bilinear, pixels,
                    wholeSourceConstant: false, label: "subpixel-test");
                Assert.That(result.status, Does.StartWith("UV0-texels="));
                Assert.That(probe.Report(), Does.Contain("GPU fallback: 1"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void GpuSubpixelTriangleFallsBackInsteadOfReportingUnused()
        {
            if (!SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGB32)
                || Shader.Find("Hidden/TexTransTool/DiagnosticSourceUvCoverage") == null)
                Assert.Ignore("GPU UV mask shader/format unavailable.");

            // Entire triangle lies between neighboring pixel centers.
            var mesh = TriangleWithUvs(
                new Vector2(0.502f, 0.502f),
                new Vector2(0.508f, 0.502f),
                new Vector2(0.502f, 0.508f));
            try
            {
                var slots = new HashSet<(Mesh, int)> { (mesh, 0) };
                var ok = DiagnosticSourceUvGpuMask.TryRender(
                    slots, Vector2.one, Vector2.zero, 8, 8, 64,
                    out var coverage, out var count, out var reason);
                Assert.That(ok, Is.False);
                Assert.That(count, Is.Zero);
                Assert.That(reason, Is.EqualTo("gpu-mask-empty-or-subpixel"));
                Assert.That(coverage, Is.Empty);
                var cpu = Coverage(mesh, FilterMode.Bilinear);
                Assert.That(Array.Exists(cpu, pixel => pixel), Is.True,
                    "The GPU failure must fall back to the actual CPU footprint.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void GpuOverlappingAndReversedUvFacesAreUnioned()
        {
            if (!SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGB32)
                || Shader.Find("Hidden/TexTransTool/DiagnosticSourceUvCoverage") == null)
                Assert.Ignore("GPU UV mask shader/format unavailable.");

            var mesh = Triangle();
            try
            {
                var slot = new HashSet<(Mesh, int)> { (mesh, 0) };
                var firstOk = DiagnosticSourceUvGpuMask.TryRender(
                    slot, Vector2.one, Vector2.zero, 8, 8, 64,
                    out var initial, out var initialCount, out var reason);
                Assert.That(firstOk, Is.True, reason);
                mesh.SetTriangles(new[] { 0, 1, 2, 2, 1, 0, 0, 1, 2 }, 0);
                var againOk = DiagnosticSourceUvGpuMask.TryRender(
                    slot, Vector2.one, Vector2.zero, 8, 8, 64,
                    out var combined, out var combinedCount, out reason);
                Assert.That(againOk, Is.True, reason);
                Assert.That(combinedCount, Is.EqualTo(initialCount));
                CollectionAssert.AreEqual(initial, combined);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void GpuUvBorderTrianglesStayInsideCpuFilterSupport()
        {
            if (!SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGB32)
                || Shader.Find("Hidden/TexTransTool/DiagnosticSourceUvCoverage") == null)
                Assert.Ignore("GPU UV mask shader/format unavailable.");

            var mesh = TriangleWithUvs(
                Vector2.zero, new Vector2(0.375f, 0f),
                new Vector2(0f, 0.375f));
            try
            {
                var slots = new HashSet<(Mesh, int)> { (mesh, 0) };
                var ok = DiagnosticSourceUvGpuMask.TryRender(
                    slots, Vector2.one, Vector2.zero, 8, 8, 64,
                    out var gpu, out var count, out var reason);
                Assert.That(ok, Is.True, reason);
                Assert.That(count, Is.GreaterThan(0));
                var strict = Coverage(mesh, FilterMode.Bilinear);
                for (int i = 0; i < gpu.Length; ++i)
                    if (gpu[i]) Assert.That(strict[i], Is.True,
                        "GPU raster must stay inside CPU filter support at " + i);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void AtlasContextComparisonRejectsAnySingleTexelDifference()
        {
            var mesh = Triangle();
            try
            {
                foreach (var filter in new[]
                         { FilterMode.Point, FilterMode.Bilinear })
                {
                    var baseline = Coverage(mesh, filter);
                    var identical = (bool[])baseline.Clone();
                    Assert.That(
                        AutoAtlasEmissionProbe.CountCoverageDifferences(
                            baseline, identical), Is.Zero);

                    // A mismatch in a texel that the triangle does not touch
                    // still invalidates mask equivalence. A mere enclosing
                    // rectangle comparison could not establish that.
                    int untouched = 7 * 8 + 7;
                    Assert.That(baseline[untouched], Is.False);
                    identical[untouched] = true;
                    Assert.That(
                        AutoAtlasEmissionProbe.CountCoverageDifferences(
                            baseline, identical), Is.EqualTo(1));
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void AtlasContextComparisonRejectsDifferentMaskDimensions()
        {
            Assert.Throws<ArgumentException>(() =>
                AutoAtlasEmissionProbe.CountCoverageDifferences(
                    new bool[64], new bool[63]));
        }

        [Test]
        public void ZeroMaskInputCannotBeRemovedIfAnotherGroupMaterialNeedsIt()
        {
            var shader = Shader.Find("Standard") ??
                Shader.Find("Hidden/InternalErrorShader");
            Assert.That(shader, Is.Not.Null);
            var materialA = new Material(shader);
            var materialB = new Material(shader);
            var texture = new Texture2D(8, 8);
            try
            {
                var input = new Dictionary<Material,
                    IReadOnlyDictionary<string, Texture>>
                {
                    [materialA] = new Dictionary<string, Texture>
                        { ["_Emission2ndBlendMask"] = texture },
                    [materialB] = new Dictionary<string, Texture>
                        { ["_Emission2ndBlendMask"] = texture },
                };
                var proof = new HashSet<(Material material, string property)>
                {
                    (materialA, "_Emission2ndBlendMask")
                };
                Assert.That(AutoAtlasEmissionProbe
                    .AllGroupSourceConsumersHaveProof(
                        new[] { materialA, materialB }, input,
                        proof, "_Emission2ndBlendMask", texture), Is.False,
                    "One neutral material cannot eliminate a shared source.");

                proof.Add((materialB, "_Emission2ndBlendMask"));
                Assert.That(AutoAtlasEmissionProbe
                    .AllGroupSourceConsumersHaveProof(
                        new[] { materialA, materialB }, input,
                        proof, "_Emission2ndBlendMask", texture), Is.True);
                Assert.That(AutoAtlasEmissionProbe
                    .AllGroupSourceConsumersHaveProof(
                        new[] { materialA, materialB }, input,
                        proof, "_MainTex", texture), Is.False,
                    "No matching contributors must not prove removal.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(materialA);
                UnityEngine.Object.DestroyImmediate(materialB);
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        [Test]
        public void ExcludingSmallZeroMaskKeepsPrimaryButNotItsOwnInput()
        {
            var main = new Texture2D(32, 32);
            var mask = new Texture2D(8, 8);
            try
            {
                var inputs = new Dictionary<string, Texture>
                {
                    ["_MainTex"] = main,
                    ["_Emission2ndBlendMask"] = mask
                };
                var withoutMask =
                    AutoAtlasEmissionProbe.PrimaryTextureWithoutProperty(
                        inputs, "_Emission2ndBlendMask");
                Assert.That(withoutMask, Is.SameAs(main),
                    "Removing a small secondary mask does not resize"
                    + " UV islands scaled by the main texture.");

                var withoutMain =
                    AutoAtlasEmissionProbe.PrimaryTextureWithoutProperty(
                        inputs, "_MainTex");
                Assert.That(withoutMain, Is.SameAs(mask),
                    "Removing a dominant input changes the primary texture"
                    + " and requires shadow relocation.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(main);
                UnityEngine.Object.DestroyImmediate(mask);
            }
        }

        [Test]
        public void ProbeReportUsesNoNestedScrollView()
        {
            var error = new AutoAtlasEmissionProbeError("sample\nsample 2");
            var view = error.CreateVisualElement(null);
            Assert.That(
                view.Query<UnityEngine.UIElements.ScrollView>().ToList(),
                Is.Empty);
            Assert.That(
                view.Query<UnityEngine.UIElements.Button>().ToList().Count,
                Is.EqualTo(1), "Copy button must remain available.");
        }

    }
}
