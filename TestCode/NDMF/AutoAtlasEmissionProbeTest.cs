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
