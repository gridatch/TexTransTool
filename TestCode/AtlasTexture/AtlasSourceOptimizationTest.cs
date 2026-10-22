using NUnit.Framework;
using net.rs64.TexTransTool.TextureAtlas;
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
        public void GpuCoverageFoldsClampAndRepeatAtUvOrigin()
        {
            var mesh = new Mesh();
            try
            {
                mesh.vertices = new[]
                {
                    Vector3.zero,
                    Vector3.right,
                    Vector3.up,
                };
                mesh.uv = new[]
                {
                    Vector2.zero,
                    Vector2.right,
                    Vector2.up,
                };
                mesh.triangles = new[] { 0, 1, 2 };

                // default(Matrix2x3) maps every UV vertex to (0, 0).
                // The one-pixel filter footprint therefore extends below and
                // left of the image. Production coverage conservatively folds
                // those texels through both clamp and repeat addressing.
                Assert.That(
                    AtlasSourceCoverageGpu.TryRender(
                        new[] { (mesh, 0) },
                        0,
                        default,
                        8,
                        8,
                        out var coverage),
                    Is.True);

                Assert.That(coverage.IsCovered(0, 0), Is.True);
                Assert.That(coverage.IsCovered(7, 0), Is.True);
                Assert.That(coverage.IsCovered(0, 7), Is.True);
                Assert.That(coverage.IsCovered(7, 7), Is.True);
            }
            finally
            {
                Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void GpuCoverageOrientationMarkerDetectsBothDirections()
        {
            const int width = 4;
            const int height = 4;

            var bottom = new Color32[width * height];
            for (var x = 0; x < width; x += 1)
                bottom[x] = new Color32(0, 255, 0, 255);
            Assert.That(
                AtlasSourceCoverageGpu.TryResolveUvYOrientation(
                    bottom, width, height, out var bottomFlip),
                Is.True);
            Assert.That(bottomFlip, Is.False);

            var top = new Color32[width * height];
            var topOffset = (height - 1) * width;
            for (var x = 0; x < width; x += 1)
                top[topOffset + x] =
                    new Color32(0, 255, 0, 255);
            Assert.That(
                AtlasSourceCoverageGpu.TryResolveUvYOrientation(
                    top, width, height, out var topFlip),
                Is.True);
            Assert.That(topFlip, Is.True);
        }

        [Test]
        public void FilterCoverageIncludesTriangleTouchingTexelSupport()
        {
            var triangleA = new Vector2(0f, 0f);
            var triangleB = new Vector2(2f, 0f);
            var triangleC = new Vector2(0f, 2f);

            Assert.That(
                AtlasSourceOptimizer.TriangleOverlapsTexelFilter(
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
                AtlasSourceOptimizer.TriangleOverlapsTexelFilter(
                    new Vector2(10f, 10f),
                    triangleA,
                    triangleB,
                    triangleC,
                    1f),
                Is.False);
        }
    }
}
