using System.Collections.Generic;
using NUnit.Framework;
using net.rs64.TexTransTool.TextureAtlas.AAOCode;
using net.rs64.TexTransTool.TextureAtlas;
using net.rs64.TexTransTool.TextureAtlas.FineTuning;
using UnityEngine;

namespace net.rs64.TexTransTool.TestCode
{
    internal class EquivalentOutputReferenceCopyTest
    {
        [Test]
        public void IdenticalSourceMappingAndFineTuningShareMainTexture()
        {
            using var fixture = new Fixture();
            fixture.Set("_MainTex", fixture.SharedTexture);
            fixture.Set("_OutlineTex", fixture.SharedTexture);

            var copies = fixture.Apply();

            Assert.That(copies["_OutlineTex"], Is.EqualTo("_MainTex"));
            Assert.That(
                fixture.Holders["_OutlineTex"]
                    .Find<ReferenceCopyData>()!.CopySource,
                Is.EqualTo("_MainTex"));
        }

        [Test]
        public void DifferentOutputResolutionDoesNotShare()
        {
            using var fixture = new Fixture();
            fixture.Set("_MainTex", fixture.SharedTexture);
            fixture.Set("_OutlineTex", fixture.SharedTexture);
            fixture.Sizes["_OutlineTex"] = new Vector2Int(1024, 1024);

            var copies = fixture.Apply();

            Assert.That(copies.ContainsKey("_OutlineTex"), Is.False);
        }

        [Test]
        public void DifferentColorSpaceDoesNotShare()
        {
            using var fixture = new Fixture();
            fixture.Set("_MainTex", fixture.SharedTexture);
            fixture.Set("_OutlineTex", fixture.SharedTexture);
            fixture.Holders["_OutlineTex"]
                .Get<ColorSpaceData>().AsLinear = true;

            var copies = fixture.Apply();

            Assert.That(copies.ContainsKey("_OutlineTex"), Is.False);
        }

        [Test]
        public void DifferentMipMapSettingDoesNotShare()
        {
            using var fixture = new Fixture();
            fixture.Set("_MainTex", fixture.SharedTexture);
            fixture.Set("_OutlineTex", fixture.SharedTexture);
            fixture.Holders["_OutlineTex"]
                .Get<MipMapData>().UseMipMap = false;

            var copies = fixture.Apply();

            Assert.That(copies.ContainsKey("_OutlineTex"), Is.False);
        }

        [Test]
        public void DifferentCompressionSettingDoesNotShare()
        {
            using var fixture = new Fixture();
            fixture.Set("_MainTex", fixture.SharedTexture);
            fixture.Set("_OutlineTex", fixture.SharedTexture);
            fixture.Holders["_OutlineTex"]
                .Get<TextureCompressionTuningData>()
                .FormatQualityValue = FormatQuality.High;

            var copies = fixture.Apply();

            Assert.That(copies.ContainsKey("_OutlineTex"), Is.False);
        }

        [Test]
        public void EquivalentUvUsageRequiresKnownMatchingTransform()
        {
            var known = new TextureUVUsage(
                UsageUVChannel.UV0,
                Matrix2x3.Identity);
            var unknown = new TextureUVUsage(
                UsageUVChannel.UV0,
                null);
            var otherChannel = new TextureUVUsage(
                UsageUVChannel.UV1,
                Matrix2x3.Identity);

            Assert.That(
                TexFineTuningUtility.AreEquivalentTextureUvUsages(
                    known, known),
                Is.True);
            Assert.That(
                TexFineTuningUtility.AreEquivalentTextureUvUsages(
                    known, unknown),
                Is.False);
            Assert.That(
                TexFineTuningUtility.AreEquivalentTextureUvUsages(
                    known, otherChannel),
                Is.False);
        }

        [Test]
        public void DifferentSourceMappingDoesNotShare()
        {
            using var fixture = new Fixture();
            var other = new Texture2D(4, 4);
            try
            {
                fixture.Set("_MainTex", fixture.SharedTexture);
                fixture.Set("_OutlineTex", other);

                var copies = fixture.Apply();

                Assert.That(copies.ContainsKey("_OutlineTex"), Is.False);
            }
            finally
            {
                Object.DestroyImmediate(other);
            }
        }

        [Test]
        public void ExistingReferenceCopyIsNotOverwritten()
        {
            using var fixture = new Fixture();
            fixture.Set("_MainTex", fixture.SharedTexture);
            fixture.Set("_OutlineTex", fixture.SharedTexture);
            fixture.Holders["_OutlineTex"]
                .Get<ReferenceCopyData>().CopySource = "_ExistingSource";

            var copies = fixture.Apply();

            Assert.That(copies.ContainsKey("_OutlineTex"), Is.False);
            Assert.That(
                fixture.Holders["_OutlineTex"]
                    .Find<ReferenceCopyData>()!.CopySource,
                Is.EqualTo("_ExistingSource"));
        }

        [Test]
        public void MergeTextureParticipantDoesNotShare()
        {
            using var fixture = new Fixture();
            fixture.Set("_MainTex", fixture.SharedTexture);
            fixture.Set("_OutlineTex", fixture.SharedTexture);
            fixture.Holders["_OutlineTex"]
                .Get<MergeTextureData>().MargeParent = "_MainTex";

            var copies = fixture.Apply();

            Assert.That(copies.ContainsKey("_OutlineTex"), Is.False);
        }

        private sealed class Fixture : System.IDisposable
        {
            internal readonly Texture2D SharedTexture = new(4, 4);
            internal readonly Material Material;
            internal readonly Dictionary<string, Vector2Int> Sizes = new()
            {
                ["_MainTex"] = new Vector2Int(2048, 2048),
                ["_OutlineTex"] = new Vector2Int(2048, 2048),
            };
            internal readonly Dictionary<string, TexFineTuningHolder> Holders;

            private readonly Dictionary<string, Texture> _textures = new();

            internal Fixture()
            {
                var shader = Shader.Find("Hidden/InternalErrorShader");
                Assert.That(shader, Is.Not.Null);
                Material = new Material(shader);
                Holders = TexFineTuningUtility.InitTexFineTuningHolders(
                    Sizes.Keys);
            }

            internal void Set(string property, Texture texture)
            {
                _textures[property] = texture;
            }

            internal Dictionary<string, string> Apply()
            {
                var propertyTextures =
                    new Dictionary<
                        Material,
                        IReadOnlyDictionary<string, Texture>>
                    {
                        [Material] = _textures,
                    };

                var uvUsages =
                    new Dictionary<
                        Material,
                        IReadOnlyDictionary<string, TextureUVUsage>>
                    {
                        [Material] = new Dictionary<string, TextureUVUsage>
                        {
                            ["_MainTex"] = new TextureUVUsage(
                                UsageUVChannel.UV0,
                                Matrix2x3.Identity),
                            ["_OutlineTex"] = new TextureUVUsage(
                                UsageUVChannel.UV0,
                                Matrix2x3.Identity),
                        },
                    };

                return TexFineTuningUtility
                    .AddEquivalentOutputReferenceCopies(
                        new[] { Material },
                        propertyTextures,
                        uvUsages,
                        Sizes,
                        Holders);
            }

            public void Dispose()
            {
                Object.DestroyImmediate(Material);
                Object.DestroyImmediate(SharedTexture);
            }
        }
    }
}
