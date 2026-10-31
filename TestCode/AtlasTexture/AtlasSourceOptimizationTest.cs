#nullable enable
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using net.rs64.TexTransTool.TextureAtlas;
using net.rs64.TexTransTool.TextureAtlas.AAOCode;
using UnityEngine;

namespace net.rs64.TexTransTool.TestCode
{
    internal class AtlasSourceOptimizationTest
    {
        [Test]
        public void AtlasMaterialsRetainUnchangedTexturelessTargets()
        {
            var shader = Shader.Find("Standard");
            Assert.That(shader, Is.Not.Null);

            var changed = new Material(shader);
            var unchanged = new Material(shader);
            var source = new Texture2D(8, 8);
            try
            {
                var original = new Dictionary<
                    Material, IReadOnlyDictionary<string, Texture>>
                {
                    [changed] = new Dictionary<string, Texture>
                    {
                        ["_MainTex"] = source,
                    },
                    [unchanged] = new Dictionary<string, Texture>(),
                };
                var projected = new Dictionary<
                    Material, IReadOnlyDictionary<string, Texture>>
                {
                    [changed] = new Dictionary<string, Texture>(),
                    [unchanged] = new Dictionary<string, Texture>(),
                };
                var plan = new AtlasSourceOptimizationPlan(
                    original,
                    projected,
                    new[]
                    {
                        new AtlasConstantTextureDecision(
                            changed,
                            "_MainTex",
                            source,
                            Color.white),
                    },
                    System.Array.Empty<AtlasFeatureDisableDecision>());

                Assert.That(
                    plan.GetOptimizationOnlyMaterials(
                        new[] { changed, unchanged }),
                    Is.EquivalentTo(new[] { changed }));
                Assert.That(
                    plan.GetAtlasMaterials(new[] { changed, unchanged }),
                    Is.EquivalentTo(new[] { unchanged }));
            }
            finally
            {
                Object.DestroyImmediate(source);
                Object.DestroyImmediate(changed);
                Object.DestroyImmediate(unchanged);
            }
        }

        [Test]
        public void SourceOptimizationOnlyReportIncludesRemainingSourceTextures()
        {
            var shader = Shader.Find("Standard");
            Assert.That(shader, Is.Not.Null);

            var material = new Material(shader);
            var sources = new[]
            {
                new Texture2D(512, 512),
                new Texture2D(512, 512),
                new Texture2D(512, 512),
                new Texture2D(512, 512),
                new Texture2D(512, 512),
            };

            try
            {
                var original = new Dictionary<
                    Material, IReadOnlyDictionary<string, Texture>>
                {
                    [material] = new Dictionary<string, Texture>
                    {
                        ["_MainTex"] = sources[0],
                        ["_DetailAlbedoMap"] = sources[1],
                        ["_EmissionMap"] = sources[2],
                        ["_ParallaxMap"] = sources[3],
                        ["_BumpMap"] = sources[4],
                    },
                };
                var projected = new Dictionary<
                    Material, IReadOnlyDictionary<string, Texture>>
                {
                    [material] = new Dictionary<string, Texture>
                    {
                        ["_MainTex"] = sources[0],
                        ["_DetailAlbedoMap"] = sources[1],
                        ["_EmissionMap"] = sources[2],
                        ["_ParallaxMap"] = sources[3],
                    },
                };
                var plan = new AtlasSourceOptimizationPlan(
                    original,
                    projected,
                    System.Array.Empty<AtlasConstantTextureDecision>(),
                    new[]
                    {
                        new AtlasFeatureDisableDecision(
                            material,
                            new AtlasFeatureDisableRule(
                                "_UseBumpMap",
                                "_BumpMap"),
                            "_BumpMap"),
                    });

                var routes =
                    AtlasResolutionRouteReport.CollectSourceOptimizationOnly(
                        plan,
                        System.Array.Empty<AtlasConstantTextureOutput>(),
                        new[] { material });

                var unchanged = routes.Single(route =>
                    route.Inputs.Length == 1
                    && route.Inputs[0].Width == 512
                    && route.Inputs[0].Height == 512
                    && route.Outputs.Length == 1
                    && route.Outputs[0].Width == 512
                    && route.Outputs[0].Height == 512);
                Assert.That(unchanged.Occurrences, Is.EqualTo(4));

                var removed = routes.Single(route =>
                    route.Inputs.Length == 1
                    && route.Inputs[0].Width == 512
                    && route.Inputs[0].Height == 512
                    && route.Outputs.Length == 1
                    && route.Outputs[0].Width == 0
                    && route.Outputs[0].Height == 0);
                Assert.That(removed.Occurrences, Is.EqualTo(1));
            }
            finally
            {
                foreach (var source in sources)
                    Object.DestroyImmediate(source);
                Object.DestroyImmediate(material);
            }
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
        public void GpuAnalyzerReductionIncludesPartialEdgeBlocks()
        {
            if (!SystemInfo.supportsAsyncGPUReadback)
                Assert.Ignore("Async GPU readback is unavailable.");

            var mesh = CreateQuadMesh(
                new Vector2(0f, 0f),
                new Vector2(1f, 1f));
            var source = CreateSource(
                7,
                5,
                (x, y) => x == 6 && y == 4
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
                        Matrix2x3.Identity,
                        7,
                        5,
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
        public void GpuAnalyzerRequiresExactRgbaIncludingAlpha()
        {
            if (!SystemInfo.supportsAsyncGPUReadback)
                Assert.Ignore("Async GPU readback is unavailable.");

            var mesh = CreateQuadMesh(
                new Vector2(0f, 0f),
                new Vector2(1f, 1f));
            var source = CreateSource(
                8,
                8,
                (x, y) => x == 7 && y == 7
                    ? new Color(0.2f, 0.3f, 0.4f, 0.50001f)
                    : new Color(0.2f, 0.3f, 0.4f, 0.5f));
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
        public void GpuAnalyzerZeroToleranceKeepsExactComparison()
        {
            if (!SystemInfo.supportsAsyncGPUReadback)
                Assert.Ignore("Async GPU readback is unavailable.");

            var mesh = CreateQuadMesh(Vector2.zero, Vector2.one);
            var baseColor = new Color(0.4f, 0.5f, 0.6f, 1f);
            var source = CreateSource(
                8,
                8,
                (x, y) => x == 7 && y == 7
                    ? new Color(
                        baseColor.r + 1f / 255f,
                        baseColor.g,
                        baseColor.b,
                        baseColor.a)
                    : baseColor);
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
                        out pending,
                        0),
                    Is.True);
                Assert.That(
                    pending!.TryGetFlags(out var flags),
                    Is.True);
                Assert.That(flags.IsConstant, Is.False);
                Assert.That(
                    flags.ApproximateConstantCandidate,
                    Is.False);
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
        public void GpuAnalyzerToleranceRejectsRgbOutsideCandidateEnvelope()
        {
            if (!SystemInfo.supportsAsyncGPUReadback)
                Assert.Ignore("Async GPU readback is unavailable.");

            var mesh = CreateQuadMesh(Vector2.zero, Vector2.one);
            var baseColor = new Color(0.4f, 0.5f, 0.6f, 1f);
            var source = CreateSource(
                8,
                8,
                (x, y) => x == 7 && y == 7
                    ? new Color(
                        baseColor.r + 11f / 255f,
                        baseColor.g,
                        baseColor.b,
                        baseColor.a)
                    : baseColor);
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
                        out pending,
                        2),
                    Is.True);
                Assert.That(
                    pending!.TryGetFlags(out var flags),
                    Is.True);
                Assert.That(
                    flags.ApproximateConstantCandidate,
                    Is.False);
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
        public void GpuAnalyzerToleranceAcceptsTinyRgbDeviation()
        {
            if (!SystemInfo.supportsAsyncGPUReadback)
                Assert.Ignore("Async GPU readback is unavailable.");

            var mesh = CreateQuadMesh(Vector2.zero, Vector2.one);
            var baseColor = new Color(0.4f, 0.5f, 0.6f, 1f);
            var source = CreateSource(
                8,
                8,
                (x, y) => x == 7 && y == 7
                    ? new Color(
                        baseColor.r + 1f / 255f,
                        baseColor.g,
                        baseColor.b,
                        baseColor.a)
                    : baseColor);
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
                        out pending,
                        2),
                    Is.True);
                Assert.That(
                    pending!.TryGetFlags(out var flags),
                    Is.True);
                Assert.That(flags.IsConstant, Is.False);
                Assert.That(
                    flags.ApproximateConstantCandidate,
                    Is.True);
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
        public void GpuAnalyzerToleranceStillRequiresExactAlpha()
        {
            if (!SystemInfo.supportsAsyncGPUReadback)
                Assert.Ignore("Async GPU readback is unavailable.");

            var mesh = CreateQuadMesh(Vector2.zero, Vector2.one);
            var baseColor = new Color(0.4f, 0.5f, 0.6f, 0.5f);
            var source = CreateSource(
                8,
                8,
                (x, y) => x == 7 && y == 7
                    ? new Color(
                        baseColor.r,
                        baseColor.g,
                        baseColor.b,
                        baseColor.a + 1f / 255f)
                    : baseColor);
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
                        out pending,
                        2),
                    Is.True);
                Assert.That(
                    pending!.TryGetFlags(out var flags),
                    Is.True);
                Assert.That(
                    flags.ApproximateConstantCandidate,
                    Is.False);
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
        public void GpuToleranceValidationUsesAdoptedCoverageThresholds()
        {
            if (!SystemInfo.supportsAsyncGPUReadback)
                Assert.Ignore("Async GPU readback is unavailable.");

            var baseColor = new Color(0.4f, 0.5f, 0.6f, 1f);
            var analyzer = AtlasSourceGpuAnalyzer.TryCreate();
            if (analyzer == null)
            {
                Assert.Ignore("GPU source analyzer is unavailable.");
                return;
            }

            try
            {
                AssertToleranceValidation(
                    analyzer,
                    baseColor,
                    index => index < 90
                        ? baseColor
                        : index < 99
                            ? new Color(
                                baseColor.r + 3f / 255f,
                                baseColor.g,
                                baseColor.b,
                                baseColor.a)
                            : new Color(
                                baseColor.r + 5f / 255f,
                                baseColor.g,
                                baseColor.b,
                                baseColor.a),
                    true);

                AssertToleranceValidation(
                    analyzer,
                    baseColor,
                    index => index < 89
                        ? baseColor
                        : index < 99
                            ? new Color(
                                baseColor.r + 3f / 255f,
                                baseColor.g,
                                baseColor.b,
                                baseColor.a)
                            : new Color(
                                baseColor.r + 5f / 255f,
                                baseColor.g,
                                baseColor.b,
                                baseColor.a),
                    false);

                AssertToleranceValidation(
                    analyzer,
                    baseColor,
                    index => index < 90
                        ? baseColor
                        : index < 98
                            ? new Color(
                                baseColor.r + 3f / 255f,
                                baseColor.g,
                                baseColor.b,
                                baseColor.a)
                            : new Color(
                                baseColor.r + 5f / 255f,
                                baseColor.g,
                                baseColor.b,
                                baseColor.a),
                    false);

                AssertToleranceValidation(
                    analyzer,
                    baseColor,
                    index => index < 90
                        ? baseColor
                        : index < 99
                            ? new Color(
                                baseColor.r + 3f / 255f,
                                baseColor.g,
                                baseColor.b,
                                baseColor.a)
                            : new Color(
                                baseColor.r + 6f / 255f,
                                baseColor.g,
                                baseColor.b,
                                baseColor.a),
                    false);

                AssertToleranceValidation(
                    analyzer,
                    baseColor,
                    index => index < 90
                        ? baseColor
                        : index < 99
                            ? new Color(
                                baseColor.r + 3f / 255f,
                                baseColor.g,
                                baseColor.b,
                                baseColor.a)
                            : new Color(
                                baseColor.r + 5f / 255f,
                                baseColor.g,
                                baseColor.b,
                                baseColor.a + 1f / 255f),
                    false);

                AssertToleranceValidation(
                    analyzer,
                    baseColor,
                    index => index < 90
                        ? baseColor
                        : index < 99
                            ? new Color(
                                baseColor.r + 5f / 255f,
                                baseColor.g,
                                baseColor.b,
                                baseColor.a)
                            : new Color(
                                baseColor.r + 7f / 255f,
                                baseColor.g,
                                baseColor.b,
                                baseColor.a),
                    true,
                    4);

                AssertToleranceValidation(
                    analyzer,
                    baseColor,
                    index => (index & 1) == 0
                        ? baseColor
                        : new Color(
                            baseColor.r + 1f / 255f,
                            baseColor.g,
                            baseColor.b,
                            baseColor.a),
                    false);
            }
            finally
            {
                analyzer.Dispose();
            }
        }

        [Test]
        public void WholeTextureProofHandlesOutOfRangeUvFallback()
        {
            if (!SystemInfo.supportsAsyncGPUReadback)
                Assert.Ignore("Async GPU readback is unavailable.");

            var mesh = CreateQuadMesh(
                new Vector2(0f, 0f),
                new Vector2(2f, 2f));
            var source = CreateSource(
                8,
                8,
                (_, _) => Color.red);
            var analyzer = AtlasSourceGpuAnalyzer.TryCreate();
            if (analyzer == null)
            {
                Object.DestroyImmediate(mesh);
                Object.DestroyImmediate(source);
                Assert.Ignore("GPU source analyzer is unavailable.");
                return;
            }

            AtlasSourceGpuGeometry? usedGeometry = null;
            AtlasSourceGpuGeometry? wholeGeometry = null;
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
                        out usedGeometry),
                    Is.False);
                Assert.That(
                    AtlasSourceGpuAnalyzer.TryBuildWholeTextureGeometry(
                        8,
                        8,
                        out wholeGeometry),
                    Is.True);
                Assert.That(wholeGeometry, Is.Not.Null);
                Assert.That(
                    analyzer.TryQueue(
                        source,
                        wholeGeometry!,
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
                usedGeometry?.Dispose();
                wholeGeometry?.Dispose();
                analyzer.Dispose();
                Object.DestroyImmediate(mesh);
                Object.DestroyImmediate(source);
            }
        }

        [Test]
        public void WholeTextureGeometryCoversPartialEdgeBlocks()
        {
            if (!SystemInfo.supportsAsyncGPUReadback)
                Assert.Ignore("Async GPU readback is unavailable.");

            var source = CreateSource(
                7,
                5,
                (x, y) => x == 6 && y == 4
                    ? Color.blue
                    : Color.red);
            var analyzer = AtlasSourceGpuAnalyzer.TryCreate();
            if (analyzer == null)
            {
                Object.DestroyImmediate(source);
                Assert.Ignore("GPU source analyzer is unavailable.");
                return;
            }

            AtlasSourceGpuGeometry? geometry = null;
            AtlasSourceGpuPending? pending = null;
            try
            {
                Assert.That(
                    AtlasSourceGpuAnalyzer.TryBuildWholeTextureGeometry(
                        7,
                        5,
                        out geometry),
                    Is.True);
                Assert.That(
                    analyzer.TryQueue(
                        source,
                        geometry!,
                        out pending,
                        0),
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
                Object.DestroyImmediate(source);
            }
        }

        [Test]
        public void WholeTextureToleranceUsesAdoptedCoverageThresholds()
        {
            if (!SystemInfo.supportsAsyncGPUReadback)
                Assert.Ignore("Async GPU readback is unavailable.");

            var baseColor = new Color(0.4f, 0.5f, 0.6f, 1f);
            var source = CreateSource(
                10,
                10,
                (x, y) =>
                {
                    var index = y * 10 + x;
                    return index < 90
                        ? baseColor
                        : index < 99
                            ? new Color(
                                baseColor.r + 3f / 255f,
                                baseColor.g,
                                baseColor.b,
                                baseColor.a)
                            : new Color(
                                baseColor.r + 5f / 255f,
                                baseColor.g,
                                baseColor.b,
                                baseColor.a);
                });
            var analyzer = AtlasSourceGpuAnalyzer.TryCreate();
            if (analyzer == null)
            {
                Object.DestroyImmediate(source);
                Assert.Ignore("GPU source analyzer is unavailable.");
                return;
            }

            AtlasSourceGpuGeometry? geometry = null;
            AtlasSourceGpuToleranceValidationPending? pending = null;
            try
            {
                Assert.That(
                    AtlasSourceGpuAnalyzer.TryBuildWholeTextureGeometry(
                        10,
                        10,
                        out geometry),
                    Is.True);
                Assert.That(
                    analyzer.TryQueueToleranceValidation(
                        source,
                        geometry!,
                        baseColor,
                        2,
                        out pending),
                    Is.True);
                Assert.That(
                    pending!.TryGetValidation(out var validation),
                    Is.True);
                Assert.That(validation.Accepted, Is.True);
            }
            finally
            {
                pending?.Dispose();
                geometry?.Dispose();
                analyzer.Dispose();
                Object.DestroyImmediate(source);
            }
        }

        [Test]
        public void PreservedPrimaryDensitySurvivesPrimaryTextureRemoval()
        {
            var shader = Shader.Find("Standard");
            Assert.That(shader, Is.Not.Null);

            var material = new Material(shader);
            var secondary = new Texture2D(512, 512);
            try
            {
                var projected = new Dictionary<
                    Material, IReadOnlyDictionary<string, Texture>>
                {
                    [material] = new Dictionary<string, Texture>
                    {
                        ["_DetailAlbedoMap"] = secondary,
                    },
                };
                var density = new Dictionary<Material, Vector2Int?>
                {
                    [material] = new Vector2Int(1024, 512),
                };
                var grouping = new MaterialGroupingContext(
                    new HashSet<Material> { material },
                    projected,
                    "_MainTex",
                    density);

                Assert.That(grouping.GetPrimaryTexture(0), Is.Null);
                Assert.That(
                    grouping.GetPrimaryDensitySize(0),
                    Is.EqualTo(new Vector2Int(1024, 512)));
            }
            finally
            {
                Object.DestroyImmediate(secondary);
                Object.DestroyImmediate(material);
            }
        }

        [Test]
        public void PreservedPrimaryDensityUsesComponentMaximumAfterGrouping()
        {
            var shader = Shader.Find("Standard");
            Assert.That(shader, Is.Not.Null);

            var first = new Material(shader);
            var second = new Material(shader);
            var shared = new Texture2D(512, 512);
            try
            {
                var projected = new Dictionary<
                    Material, IReadOnlyDictionary<string, Texture>>
                {
                    [first] = new Dictionary<string, Texture>
                    {
                        ["_DetailAlbedoMap"] = shared,
                    },
                    [second] = new Dictionary<string, Texture>
                    {
                        ["_DetailAlbedoMap"] = shared,
                    },
                };
                var density = new Dictionary<Material, Vector2Int?>
                {
                    [first] = new Vector2Int(1024, 256),
                    [second] = new Vector2Int(256, 1024),
                };
                var grouping = new MaterialGroupingContext(
                    new HashSet<Material> { first, second },
                    projected,
                    "_MainTex",
                    density);

                Assert.That(grouping.GroupMaterials.Length, Is.EqualTo(1));
                Assert.That(
                    grouping.GetPrimaryDensitySize(0),
                    Is.EqualTo(new Vector2Int(1024, 1024)));
            }
            finally
            {
                Object.DestroyImmediate(shared);
                Object.DestroyImmediate(first);
                Object.DestroyImmediate(second);
            }
        }

        private static void AssertToleranceValidation(
            AtlasSourceGpuAnalyzer analyzer,
            Color representative,
            System.Func<int, Color> pixel,
            bool expected,
            int tolerance = 2)
        {
            var mesh = CreateQuadMesh(Vector2.zero, Vector2.one);
            var source = CreateSource(
                10,
                10,
                (x, y) => pixel(y * 10 + x));
            AtlasSourceGpuGeometry? geometry = null;
            AtlasSourceGpuToleranceValidationPending? pending = null;
            try
            {
                Assert.That(
                    analyzer.TryBuildGeometry(
                        new[] { (mesh, 0) },
                        0,
                        Matrix2x3.Identity,
                        10,
                        10,
                        out geometry),
                    Is.True);
                Assert.That(
                    analyzer.TryQueueToleranceValidation(
                        source,
                        geometry!,
                        representative,
                        tolerance,
                        out pending),
                    Is.True);
                Assert.That(
                    pending!.TryGetValidation(out var validation),
                    Is.True);
                Assert.That(validation.Accepted, Is.EqualTo(expected));
            }
            finally
            {
                pending?.Dispose();
                geometry?.Dispose();
                Object.DestroyImmediate(mesh);
                Object.DestroyImmediate(source);
            }
        }

        [Test]
        public void GpuRepresentativeProbeSelectsDominantExactRgb()
        {
            if (!SystemInfo.supportsAsyncGPUReadback)
                Assert.Ignore("Async GPU readback is unavailable.");

            var mesh = CreateQuadMesh(Vector2.zero, Vector2.one);
            var baseColor = new Color(0.4f, 0.5f, 0.6f, 1f);
            var source = CreateSource(
                31,
                29,
                (x, y) => x == 30 && y == 28
                    ? new Color(
                        baseColor.r + 1f / 255f,
                        baseColor.g,
                        baseColor.b,
                        baseColor.a)
                    : baseColor);
            var analyzer = AtlasSourceGpuAnalyzer.TryCreate();
            if (analyzer == null)
            {
                Object.DestroyImmediate(mesh);
                Object.DestroyImmediate(source);
                Assert.Ignore("GPU source analyzer is unavailable.");
                return;
            }

            AtlasSourceGpuGeometry? geometry = null;
            AtlasSourceGpuProbePending? probe = null;
            try
            {
                Assert.That(
                    analyzer.TryBuildGeometry(
                        new[] { (mesh, 0) },
                        0,
                        Matrix2x3.Identity,
                        31,
                        29,
                        out geometry),
                    Is.True);
                Assert.That(
                    analyzer.TryQueueRepresentativeProbe(
                        source,
                        geometry!,
                        out probe),
                    Is.True);
                Assert.That(
                    probe!.TryGetRepresentative(
                        out var representative),
                    Is.True);
                Assert.That(representative.r, Is.EqualTo(baseColor.r));
                Assert.That(representative.g, Is.EqualTo(baseColor.g));
                Assert.That(representative.b, Is.EqualTo(baseColor.b));
            }
            finally
            {
                probe?.Dispose();
                geometry?.Dispose();
                analyzer.Dispose();
                Object.DestroyImmediate(mesh);
                Object.DestroyImmediate(source);
            }
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


    }
}
