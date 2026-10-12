using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using net.rs64.TexTransTool.TextureAtlas;
using net.rs64.TexTransTool.TextureAtlas.FineTuning;

namespace net.rs64.TexTransTool.TestCode
{
    internal class AutoAtlasTextureAtlasSettingDefaultsAssumption
    {
        [Test]
        public void AtlasSettingDefaultsRemainCompatibleWithAutoAtlasTexture()
        {
            // AutoAtlasTexture intentionally inherits these regular AtlasTexture
            // defaults. If upstream changes one, review whether AutoAtlasTexture
            // should inherit that change before updating this expectation.
            var settings = new AtlasSetting();

            Assert.That(settings.PixelNormalize, Is.True);
            Assert.That(settings.BackGroundColor, Is.EqualTo(UnityEngine.Color.white));
            Assert.That(settings.ForceSetTexture, Is.False);
            Assert.That(settings.ForceSizePriority, Is.False);

            // Lossy resizing is opt-in. AutoAtlasTexture can use the same defaults
            // as regular AtlasTexture without removing a special-case Resize.
            var actualFineTuning = settings.TextureFineTuning
                .Select(tuning => tuning.GetType())
                .ToArray();

            CollectionAssert.AreEqual(
                new[]
                {
                    typeof(ReferenceCopy),
                    typeof(ColorSpace),
                    typeof(Compress),
                },
                actualFineTuning);
        }

        [TestCase(512, 512, 2048, 2048, 2048, 2048, 512, 512)]
        [TestCase(512, 512, 2048, 2048, 512, 512, 128, 128)]
        [TestCase(512, 512, 2048, 2048, 256, 256, 64, 64)]
        [TestCase(512, 512, 1024, 1024, 2048, 2048, 1024, 1024)]
        [TestCase(512, 512, 2048, 2048, 768, 768, 256, 256)]
        [TestCase(512, 256, 2048, 2048, 512, 512, 128, 64)]
        public void PropertyResolutionKeepsSourceTexelDensity(
            int layoutWidth,
            int layoutHeight,
            int primaryWidth,
            int primaryHeight,
            int sourceWidth,
            int sourceHeight,
            int expectedWidth,
            int expectedHeight)
        {
            var result = CalculatePropertySize(
                new UnityEngine.Vector2Int(layoutWidth, layoutHeight),
                autoSize: true,
                new UnityEngine.Vector2Int?(new UnityEngine.Vector2Int(primaryWidth, primaryHeight)),
                new UnityEngine.Vector2Int(sourceWidth, sourceHeight)
            );

            Assert.That(result, Is.EqualTo(
                new UnityEngine.Vector2Int(expectedWidth, expectedHeight)));
        }

        [Test]
        public void PropertyResolutionUsesLargestRequirementAcrossGroups()
        {
            var result = CalculatePropertySize(
                new UnityEngine.Vector2Int(512, 512),
                autoSize: true,
                new UnityEngine.Vector2Int?[]
                {
                    new UnityEngine.Vector2Int(2048, 2048),
                    new UnityEngine.Vector2Int(1024, 1024),
                },
                new[]
                {
                    new UnityEngine.Vector2Int(256, 256),
                    new UnityEngine.Vector2Int(512, 512),
                }
            );

            Assert.That(result, Is.EqualTo(new UnityEngine.Vector2Int(256, 256)));
        }

        [Test]
        public void PropertyResolutionDoesNotShrinkBelowFourPixels()
        {
            var result = CalculatePropertySize(
                new UnityEngine.Vector2Int(512, 4),
                autoSize: true,
                new UnityEngine.Vector2Int?(new UnityEngine.Vector2Int(4096, 4096)),
                new UnityEngine.Vector2Int(16, 16)
            );

            Assert.That(result, Is.EqualTo(new UnityEngine.Vector2Int(512, 4)));
        }

        [Test]
        public void PropertyResolutionKeepsLayoutSizeWhenPrimaryIsUnknown()
        {
            var result = CalculatePropertySize(
                new UnityEngine.Vector2Int(512, 256),
                autoSize: true,
                primarySize: null,
                new UnityEngine.Vector2Int(64, 64)
            );

            Assert.That(result, Is.EqualTo(new UnityEngine.Vector2Int(512, 256)));
        }

        [Test]
        public void PropertyResolutionKeepsLegacyUniformSizeWhenAutoSizeIsDisabled()
        {
            var result = CalculatePropertySize(
                new UnityEngine.Vector2Int(512, 256),
                autoSize: false,
                new UnityEngine.Vector2Int?(new UnityEngine.Vector2Int(2048, 2048)),
                new UnityEngine.Vector2Int(256, 256)
            );

            Assert.That(result, Is.EqualTo(new UnityEngine.Vector2Int(512, 256)));
        }

        private static UnityEngine.Vector2Int CalculatePropertySize(
            UnityEngine.Vector2Int layoutSize,
            bool autoSize,
            UnityEngine.Vector2Int? primarySize,
            UnityEngine.Vector2Int sourceSize)
        {
            return CalculatePropertySize(
                layoutSize,
                autoSize,
                new[] { primarySize },
                new[] { sourceSize }
            );
        }

        private static UnityEngine.Vector2Int CalculatePropertySize(
            UnityEngine.Vector2Int layoutSize,
            bool autoSize,
            UnityEngine.Vector2Int?[] primarySizes,
            UnityEngine.Vector2Int[] sourceSizes)
        {
            var planType = typeof(AtlasTexture).Assembly.GetType(
                "net.rs64.TexTransTool.TextureAtlas.AtlasTextureResolutionPlan",
                throwOnError: true
            );
            var method = planType.GetMethod(
                "CalculatePropertySize",
                BindingFlags.Static | BindingFlags.NonPublic
            );

            Assert.That(method, Is.Not.Null);
            return (UnityEngine.Vector2Int)method.Invoke(
                null,
                new object[] { layoutSize, autoSize, primarySizes, sourceSizes }
            );
        }
    }
}
