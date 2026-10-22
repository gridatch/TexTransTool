#nullable enable
using System.Collections.Generic;
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
