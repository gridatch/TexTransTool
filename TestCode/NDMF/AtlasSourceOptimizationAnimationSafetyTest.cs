using NUnit.Framework;

namespace net.rs64.TexTransTool.NDMF.TestCode
{
    internal class AtlasSourceOptimizationAnimationSafetyTest
    {
        [Test]
        public void ExactProofPropertyAnimationIsRelevant()
        {
            Assert.That(
                AtlasSourceOptimizationAnimationSafety.AffectsProofProperty(
                    "material._EmissionBlend",
                    "_EmissionBlend",
                    includeSamplingControls: false),
                Is.True);
        }

        [Test]
        public void UnrelatedMaterialAnimationIsIgnored()
        {
            Assert.That(
                AtlasSourceOptimizationAnimationSafety.AffectsProofProperty(
                    "material._Color.r",
                    "_EmissionBlend",
                    includeSamplingControls: false),
                Is.False);
        }

        [Test]
        public void TextureOwnStAnimationInvalidatesUvRegionProof()
        {
            Assert.That(
                AtlasSourceOptimizationAnimationSafety.AffectsProofProperty(
                    "material._EmissionMap_ST.x",
                    "_EmissionMap",
                    includeSamplingControls: true),
                Is.True);
        }

        [Test]
        public void MainUvMaskTracksMainTextureStAnimation()
        {
            Assert.That(
                AtlasSourceOptimizationAnimationSafety.AffectsProofProperty(
                    "material._MainTex_ST.x",
                    "_EmissionBlendMask",
                    includeSamplingControls: true),
                Is.True);
        }

        [Test]
        public void IndependentEmissionMapIgnoresMainTextureStAnimation()
        {
            Assert.That(
                AtlasSourceOptimizationAnimationSafety.AffectsProofProperty(
                    "material._MainTex_ST.x",
                    "_EmissionMap",
                    includeSamplingControls: true),
                Is.False);
        }
    }
}
