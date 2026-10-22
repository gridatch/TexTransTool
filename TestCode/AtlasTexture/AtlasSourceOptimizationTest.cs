using NUnit.Framework;
using net.rs64.TexTransTool.TextureAtlas;
using net.rs64.TexTransTool.TextureAtlas.AAOCode;
using UnityEngine;

namespace net.rs64.TexTransTool.TestCode
{
    internal class AtlasSourceOptimizationTest
    {
        [Test]
        public void ConstantDetectionIgnoresUncoveredPixels()
        {
            var pixels = new[]
            {
                new Color(1f, 0.25f, 0.5f, 0.75f),
                new Color(0f, 1f, 0f, 1f),
                new Color(1f, 0.25f, 0.5f, 0.75f),
                new Color(0f, 0f, 1f, 0f),
            };
            var coverage = new[]
            {
                true,
                false,
                true,
                false,
            };

            Assert.That(
                AtlasSourceOptimizer.TryGetExactConstantForCoverage(
                    pixels, coverage, out var color),
                Is.True);
            Assert.That(color, Is.EqualTo(
                new Color(1f, 0.25f, 0.5f, 0.75f)));
        }

        [Test]
        public void ConstantDetectionRequiresExactRgbaIncludingAlpha()
        {
            var pixels = new[]
            {
                new Color(0.2f, 0.3f, 0.4f, 0.5f),
                new Color(0.2f, 0.3f, 0.4f, 0.50001f),
            };
            var coverage = new[] { true, true };

            Assert.That(
                AtlasSourceOptimizer.TryGetExactConstantForCoverage(
                    pixels, coverage, out _),
                Is.False);
        }

        [Test]
        public void ConstantDetectionRejectsEmptyCoverage()
        {
            var pixels = new[]
            {
                Color.white,
                Color.white,
            };
            var coverage = new[] { false, false };

            Assert.That(
                AtlasSourceOptimizer.TryGetExactConstantForCoverage(
                    pixels, coverage, out _),
                Is.False);
        }

        [TestCase(0, false)]
        [TestCase(1, true)]
        [TestCase(2, true)]
        [TestCase(3, false)]
        public void BlackRgbNeutralityMatchesLilToonBlendMode(
            int blendMode, bool expected)
        {
            Assert.That(
                AtlasSourceOptimizer.IsZeroRgbNeutralBlendMode(blendMode),
                Is.EqualTo(expected));
        }

        [Test]
        public void CoverageBoundsContainOnlyMarkedTexels()
        {
            var coverage = new bool[4 * 3];
            coverage[1 * 4 + 1] = true;
            coverage[2 * 4 + 3] = true;

            Assert.That(
                AtlasSourceOptimizer.TryGetCoverageBounds(
                    coverage, 4, 3, out var bounds),
                Is.True);
            Assert.That(bounds, Is.EqualTo(
                new RectInt(1, 1, 3, 2)));
        }

        [Test]
        public void GpuAnalyzerDetectsVariableFullCoverage()
        {
            if (!SystemInfo.supportsAsyncGPUReadback)
                Assert.Ignore("Async GPU readback is unavailable.");

            var mesh = CreateQuadMesh(
                new Vector2(0f, 0f),
                new Vector2(1f, 1f));
            var source = CreateSource(
                8,
                8,
                (x, _) => x < 4 ? Color.red : Color.blue);
            var analyzer = AtlasSourceGpuAnalyzer.TryCreate();
            if (analyzer == null)
            {
                Object.DestroyImmediate(mesh);
                Object.DestroyImmediate(source);
                Assert.Ignore("GPU source analyzer is unavailable.");
                return;
            }

            AtlasSourceGpuGeometry? geometry = null;
            AtlasSourceGpuPending? pending = null;
            try
            {
                Assert.That(
                    analyzer.TryBuildGeometry(
                        new[] { (mesh, 0) },
                        0,
                        Matrix2x3.Identity,
                        8,
                        8,
                        out geometry),
                    Is.True);
                Assert.That(geometry, Is.Not.Null);
                Assert.That(
                    analyzer.TryQueue(
                        source,
                        geometry!,
                        out pending),
                    Is.True);
                Assert.That(pending, Is.Not.Null);
                Assert.That(
                    pending!.TryGetFlags(out var flags),
                    Is.True);
                Assert.That(flags.Known, Is.True);
                Assert.That(flags.IsConstant, Is.False);
            }
            finally
            {
                pending?.Dispose();
                geometry?.Dispose();
                analyzer.Dispose();
                Object.DestroyImmediate(mesh);
                Object.DestroyImmediate(source);
            }
        }

        [Test]
        public void GpuAnalyzerIgnoresUncoveredPixels()
        {
            if (!SystemInfo.supportsAsyncGPUReadback)
                Assert.Ignore("Async GPU readback is unavailable.");

            var mesh = CreateQuadMesh(
                new Vector2(0f, 0f),
                new Vector2(0.375f, 1f));
            var source = CreateSource(
                8,
                8,
                (x, _) => x < 4 ? Color.red : Color.blue);
            var analyzer = AtlasSourceGpuAnalyzer.TryCreate();
            if (analyzer == null)
            {
                Object.DestroyImmediate(mesh);
                Object.DestroyImmediate(source);
                Assert.Ignore("GPU source analyzer is unavailable.");
                return;
            }

            AtlasSourceGpuGeometry? geometry = null;
            AtlasSourceGpuPending? pending = null;
            try
            {
                Assert.That(
                    analyzer.TryBuildGeometry(
                        new[] { (mesh, 0) },
                        0,
                        Matrix2x3.Identity,
                        8,
                        8,
                        out geometry),
                    Is.True);
                Assert.That(
                    analyzer.TryQueue(
                        source,
                        geometry!,
                        out pending),
                    Is.True);
                Assert.That(
                    pending!.TryGetFlags(out var flags),
                    Is.True);
                Assert.That(flags.IsConstant, Is.True);
            }
            finally
            {
                pending?.Dispose();
                geometry?.Dispose();
                analyzer.Dispose();
                Object.DestroyImmediate(mesh);
                Object.DestroyImmediate(source);
            }
        }

        [Test]
        public void GpuAnalyzerDoesNotExpandPastStrictFilterBoundary()
        {
            if (!SystemInfo.supportsAsyncGPUReadback)
                Assert.Ignore("Async GPU readback is unavailable.");

            // x=3 is exactly on the radius=1 expanded boundary for this UV
            // extent and is excluded by the old CPU predicate's strict
            // inequalities. A >1 radius would incorrectly sample it.
            var mesh = CreateQuadMesh(
                new Vector2(0f, 0f),
                new Vector2(0.3125f, 1f));
            var source = CreateSource(
                8,
                8,
                (x, _) => x < 3 ? Color.red : Color.blue);
            var analyzer = AtlasSourceGpuAnalyzer.TryCreate();
            if (analyzer == null)
            {
                Object.DestroyImmediate(mesh);
                Object.DestroyImmediate(source);
                Assert.Ignore("GPU source analyzer is unavailable.");
                return;
            }

            AtlasSourceGpuGeometry? geometry = null;
            AtlasSourceGpuPending? pending = null;
            try
            {
                Assert.That(
                    analyzer.TryBuildGeometry(
                        new[] { (mesh, 0) },
                        0,
                        Matrix2x3.Identity,
                        8,
                        8,
                        out geometry),
                    Is.True);
                Assert.That(
                    analyzer.TryQueue(
                        source,
                        geometry!,
                        out pending),
                    Is.True);
                Assert.That(
                    pending!.TryGetFlags(out var flags),
                    Is.True);
                Assert.That(flags.IsConstant, Is.True);
            }
            finally
            {
                pending?.Dispose();
                geometry?.Dispose();
                analyzer.Dispose();
                Object.DestroyImmediate(mesh);
                Object.DestroyImmediate(source);
            }
        }

        [Test]
        public void GpuAnalyzerPreservesLogicalSourceYOrientation()
        {
            if (!SystemInfo.supportsAsyncGPUReadback)
                Assert.Ignore("Async GPU readback is unavailable.");

            var mesh = CreateQuadMesh(
                new Vector2(0f, 0f),
                new Vector2(1f, 0.375f));
            var source = CreateSource(
                8,
                8,
                (_, y) => y < 4 ? Color.red : Color.blue);
            var analyzer = AtlasSourceGpuAnalyzer.TryCreate();
            if (analyzer == null)
            {
                Object.DestroyImmediate(mesh);
                Object.DestroyImmediate(source);
                Assert.Ignore("GPU source analyzer is unavailable.");
                return;
            }

            AtlasSourceGpuGeometry? geometry = null;
            AtlasSourceGpuPending? pending = null;
            try
            {
                Assert.That(
                    analyzer.TryBuildGeometry(
                        new[] { (mesh, 0) },
                        0,
                        Matrix2x3.Identity,
                        8,
                        8,
                        out geometry),
                    Is.True);
                Assert.That(
                    analyzer.TryQueue(
                        source,
                        geometry!,
                        out pending),
                    Is.True);
                Assert.That(
                    pending!.TryGetFlags(out var flags),
                    Is.True);
                Assert.That(flags.IsConstant, Is.True);
            }
            finally
            {
                pending?.Dispose();
                geometry?.Dispose();
                analyzer.Dispose();
                Object.DestroyImmediate(mesh);
                Object.DestroyImmediate(source);
            }
        }

        [Test]
        public void GpuAnalyzerIncludesRepeatCandidateAcrossUvOrigin()
        {
            if (!SystemInfo.supportsAsyncGPUReadback)
                Assert.Ignore("Async GPU readback is unavailable.");

            // Collapse the drawable UVs to the origin. The one-pixel filter
            // support extends outside the source image, so the conservative
            // proof must inspect both clamp(0) and repeat(last) addresses.
            var mesh = CreateQuadMesh(
                Vector2.zero,
                Vector2.one);
            var source = CreateSource(
                8,
                8,
                (x, y) => x == 7 && y == 7
                    ? Color.blue
                    : Color.red);
            var analyzer = AtlasSourceGpuAnalyzer.TryCreate();
            if (analyzer == null)
            {
                Object.DestroyImmediate(mesh);
                Object.DestroyImmediate(source);
                Assert.Ignore("GPU source analyzer is unavailable.");
                return;
            }

            AtlasSourceGpuGeometry? geometry = null;
            AtlasSourceGpuPending? pending = null;
            try
            {
                Assert.That(
                    analyzer.TryBuildGeometry(
                        new[] { (mesh, 0) },
                        0,
                        default,
                        8,
                        8,
                        out geometry),
                    Is.True);
                Assert.That(
                    analyzer.TryQueue(
                        source,
                        geometry!,
                        out pending),
                    Is.True);
                Assert.That(
                    pending!.TryGetFlags(out var flags),
                    Is.True);
                Assert.That(flags.IsConstant, Is.False);
            }
            finally
            {
                pending?.Dispose();
                geometry?.Dispose();
                analyzer.Dispose();
                Object.DestroyImmediate(mesh);
                Object.DestroyImmediate(source);
            }
        }

        [Test]
        public void FilterCoverageIncludesTriangleTouchingTexelSupport()
        {
            var triangleA = new Vector2(0f, 0f);
            var triangleB = new Vector2(2f, 0f);
            var triangleC = new Vector2(0f, 2f);

            Assert.That(
                TriangleOverlapsTexelFilterReference(
                    new Vector2(1.5f, 1.5f),
                    triangleA,
                    triangleB,
                    triangleC,
                    1f),
                Is.True);
        }

        [Test]
        public void FilterCoverageRejectsDistantTexel()
        {
            var triangleA = new Vector2(0f, 0f);
            var triangleB = new Vector2(1f, 0f);
            var triangleC = new Vector2(0f, 1f);

            Assert.That(
                TriangleOverlapsTexelFilterReference(
                    new Vector2(10f, 10f),
                    triangleA,
                    triangleB,
                    triangleC,
                    1f),
                Is.False);
        }
        private static Mesh CreateQuadMesh(
            Vector2 uvMin,
            Vector2 uvMax)
        {
            var mesh = new Mesh();
            mesh.vertices = new[]
            {
                Vector3.zero,
                Vector3.right,
                Vector3.one,
                Vector3.up,
            };
            mesh.uv = new[]
            {
                new Vector2(uvMin.x, uvMin.y),
                new Vector2(uvMax.x, uvMin.y),
                new Vector2(uvMax.x, uvMax.y),
                new Vector2(uvMin.x, uvMax.y),
            };
            mesh.triangles = new[]
            {
                0, 1, 2,
                0, 2, 3,
            };
            return mesh;
        }

        private static RenderTexture CreateSource(
            int width,
            int height,
            System.Func<int, int, Color> pixel)
        {
            var texture = new Texture2D(
                width,
                height,
                TextureFormat.RGBAFloat,
                false,
                true);
            try
            {
                var pixels = new Color[width * height];
                for (var y = 0; y < height; y += 1)
                for (var x = 0; x < width; x += 1)
                    pixels[y * width + x] = pixel(x, y);
                texture.SetPixels(pixels);
                texture.Apply(false, false);

                var renderTexture = new RenderTexture(
                    width,
                    height,
                    0,
                    RenderTextureFormat.ARGBFloat,
                    RenderTextureReadWrite.Linear)
                {
                    filterMode = FilterMode.Point,
                    useMipMap = false,
                    autoGenerateMips = false,
                };
                renderTexture.Create();
                Graphics.Blit(texture, renderTexture);
                return renderTexture;
            }
            finally
            {
                Object.DestroyImmediate(texture);
            }
        }

        private static bool TriangleOverlapsTexelFilterReference(
            Vector2 pixel,
            Vector2 a,
            Vector2 b,
            Vector2 c,
            float radius)
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

            return OverlapAxisReference(pixel, a, b, c, radius)
                && OverlapAxisReference(pixel, b, c, a, radius)
                && OverlapAxisReference(pixel, c, a, b, radius);
        }

        private static bool OverlapAxisReference(
            Vector2 pixel,
            Vector2 a,
            Vector2 b,
            Vector2 c,
            float radius)
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

    }
}
