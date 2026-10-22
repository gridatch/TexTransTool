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
