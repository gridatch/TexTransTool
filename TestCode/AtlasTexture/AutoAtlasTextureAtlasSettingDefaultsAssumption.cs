using System.Linq;
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

            // Resize is the only default fine tuning AutoAtlasTexture removes.
            var actualFineTuning = settings.TextureFineTuning
                .Select(tuning => tuning.GetType())
                .ToArray();

            CollectionAssert.AreEqual(
                new[]
                {
                    typeof(Resize),
                    typeof(ReferenceCopy),
                    typeof(ColorSpace),
                    typeof(Compress),
                },
                actualFineTuning);
        }
    }
}
