using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using net.rs64.TexTransTool.TextureAtlas;
using net.rs64.TexTransTool.TextureAtlas.AAOCode;
using net.rs64.TexTransTool.TextureAtlas.FineTuning;
using net.rs64.TexTransTool.Utils;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace net.rs64.TexTransTool.NDMF.TestCode
{
    internal class AutoAtlasTextureMergePlannerSafetyTest
    {
        private sealed class TestShaderUsageInformation
            : ITTShaderTextureUsageInformation
        {
            internal UsageUVChannel TextureUvChannel = UsageUVChannel.UV0;
            internal Matrix2x3? TextureUvMatrix = Matrix2x3.Identity;
            internal bool UseLegacyTextureRegistration;
            internal UsingUVChannels OtherUvUsage;
            internal bool UsesVertexIndex;

            public void GetMaterialTextureUVUsage(
                ITTTextureUVUsageWriter writer)
            {
                if (UseLegacyTextureRegistration)
                {
                    writer.WriteTextureUVUsage(
                        "_MainTex",
                        TextureUvChannel);
                    return;
                }

                var translator = new MaterialInformationTranslator(writer);
                translator.RegisterTextureUVUsage(
                    "_MainTex",
                    SamplerStateInformation.LinearRepeatSampler,
                    TextureUvChannel switch
                    {
                        UsageUVChannel.UV0 => UsingUVChannels.UV0,
                        UsageUVChannel.UV1 => UsingUVChannels.UV1,
                        UsageUVChannel.UV2 => UsingUVChannels.UV2,
                        UsageUVChannel.UV3 => UsingUVChannels.UV3,
                        UsageUVChannel.UV4 => UsingUVChannels.UV4,
                        UsageUVChannel.UV5 => UsingUVChannels.UV5,
                        UsageUVChannel.UV6 => UsingUVChannels.UV6,
                        UsageUVChannel.UV7 => UsingUVChannels.UV7,
                        UsageUVChannel.NonMesh => UsingUVChannels.NonMesh,
                        _ => UsingUVChannels.Unknown,
                    },
                    TextureUvMatrix);

                if (OtherUvUsage != 0)
                    translator.RegisterOtherUVUsage(OtherUvUsage);
                if (UsesVertexIndex)
                    translator.RegisterVertexIndexUsage();
            }
        }

        private sealed class LiltoonMaterialInformationProbe
            : IMaterialInformationCallbackAbstractionInterface
        {
            internal readonly Dictionary<string, int?> IntValues = new();
            internal readonly Dictionary<string, float?> FloatValues = new();
            internal readonly Dictionary<string, Vector4?> VectorValues = new();
            internal readonly Dictionary<string, bool?> KeywordValues = new();
            internal readonly Dictionary<string, Matrix2x3?> TextureMatrices =
                new();
            internal readonly Dictionary<string, UsingUVChannels>
                TextureUvChannels = new();
            internal UsingUVChannels OtherUvUsage;
            internal bool UsesVertexIndex;

            public int? GetInteger(
                string propertyName,
                bool considerAnimation = true)
                => IntValues.GetValueOrDefault(propertyName, 0);

            public int? GetInt(
                string propertyName,
                bool considerAnimation = true)
                => IntValues.GetValueOrDefault(propertyName, 0);

            public float? GetFloat(
                string propertyName,
                bool considerAnimation = true)
                => FloatValues.GetValueOrDefault(propertyName, 0f);

            public Vector4? GetVector(
                string propertyName,
                bool considerAnimation = true)
            {
                if (VectorValues.TryGetValue(propertyName, out var value))
                    return value;
                if (propertyName.EndsWith("_ST"))
                    return new Vector4(1f, 1f, 0f, 0f);
                if (propertyName.EndsWith("_ScrollRotate"))
                    return Vector4.zero;
                if (propertyName.EndsWith("DecalAnimation"))
                    return new Vector4(1f, 1f, 1f, 30f);
                if (propertyName.EndsWith("DecalSubParam"))
                    return new Vector4(1f, 1f, 0f, 1f);
                return Vector4.zero;
            }

            public bool? IsShaderKeywordEnabled(string keywordName)
                => KeywordValues.TryGetValue(keywordName, out var value)
                    ? value
                    : false;

            public void RegisterOtherUVUsage(UsingUVChannels uvChannel)
            {
                OtherUvUsage |= uvChannel;
            }

            public void RegisterTextureUVUsage(
                string textureMaterialPropertyName,
                SamplerStateInformation samplerState,
                UsingUVChannels uvChannels,
                Matrix2x3? uvMatrix)
            {
                TextureMatrices[textureMaterialPropertyName] = uvMatrix;
                TextureUvChannels[textureMaterialPropertyName] = uvChannels;
            }

            public void RegisterVertexIndexUsage()
            {
                UsesVertexIndex = true;
            }
        }

        [Test]
        public void LegacyTextureUsageDoesNotProveIdentityTransform()
        {
            var shader = Shader.Find("Standard");
            Assert.That(shader, Is.Not.Null);

            var material = new Material(shader);
            var texture = new Texture2D(4, 4);
            var registry = TTShaderTextureUsageInformationRegistry.s_information;
            var hadPrevious = registry.TryGetValue(shader, out var previous);
            try
            {
                material.SetTexture("_MainTex", texture);
                registry[shader] = new TestShaderUsageInformation
                {
                    UseLegacyTextureRegistration = true,
                };

                var usage = TTShaderTextureUsageInformationUtil
                    .GetContainsUVTransformUsage(material)["_MainTex"];
                Assert.That(
                    TTShaderTextureUsageInformationUtil
                        .IsIdentityUVMatrix(usage.UVMatrix),
                    Is.True,
                    "Manual AtlasTexture keeps the legacy identity assumption.");
                Assert.That(
                    TTShaderTextureUsageInformationUtil
                        .IsKnownIdentityUVMatrix(usage),
                    Is.False,
                    "AutoAtlas must distinguish an assumed identity from an explicitly reported one.");

                var grouping = new MaterialGroupingContext(
                    new HashSet<Material> { material },
                    UVChannel.UV0,
                    primaryTexturePropertyOrMaximum: null);
                Assert.That(
                    AutoAtlasTextureProcessor.HasUnsupportedTextureUvSampling(
                        new[] { material },
                        grouping.ContainsTextureDictionaries,
                        UVChannel.UV0),
                    Is.True);
            }
            finally
            {
                if (hadPrevious)
                    registry[shader] = previous;
                else
                    registry.Remove(shader);
                Object.DestroyImmediate(texture);
                Object.DestroyImmediate(material);
            }
        }

        [Test]
        public void RegisteredShaderUsageMayOmitUnsampledAssignedTexture()
        {
            var shader = Shader.Find("Standard");
            Assert.That(shader, Is.Not.Null);

            var material = new Material(shader);
            var mainTexture = new Texture2D(4, 4);
            var unusedTexture = new Texture2D(4, 4);
            var registry = TTShaderTextureUsageInformationRegistry.s_information;
            var hadPrevious = registry.TryGetValue(shader, out var previous);
            try
            {
                material.SetTexture("_MainTex", mainTexture);
                material.SetTexture("_BumpMap", unusedTexture);
                registry[shader] = new TestShaderUsageInformation();

                var grouping = new MaterialGroupingContext(
                    new HashSet<Material> { material },
                    UVChannel.UV0,
                    primaryTexturePropertyOrMaximum: null);

                Assert.That(
                    AutoAtlasTextureProcessor.HasUnsupportedTextureUvSampling(
                        new[] { material },
                        grouping.ContainsTextureDictionaries,
                        UVChannel.UV0),
                    Is.False,
                    "A registered provider may omit an assigned texture when the current shader state does not sample that property.");
            }
            finally
            {
                if (hadPrevious)
                    registry[shader] = previous;
                else
                    registry.Remove(shader);
                Object.DestroyImmediate(unusedTexture);
                Object.DestroyImmediate(mainTexture);
                Object.DestroyImmediate(material);
            }
        }

        [Test]
        public void AutoAtlasRejectsUnregisteredShaderUsage()
        {
            var shader = Shader.Find("Standard");
            Assert.That(shader, Is.Not.Null);

            var material = new Material(shader);
            var texture = new Texture2D(4, 4);
            var registry = TTShaderTextureUsageInformationRegistry.s_information;
            var hadPrevious = registry.TryGetValue(shader, out var previous);
            try
            {
                material.SetTexture("_MainTex", texture);
                registry.Remove(shader);

                var grouping = new MaterialGroupingContext(
                    new HashSet<Material> { material },
                    UVChannel.UV0,
                    primaryTexturePropertyOrMaximum: null);

                Assert.That(
                    AutoAtlasTextureProcessor.HasUnsupportedTextureUvSampling(
                        new[] { material },
                        grouping.ContainsTextureDictionaries,
                        UVChannel.UV0),
                    Is.True);
            }
            finally
            {
                if (hadPrevious)
                    registry[shader] = previous;
                else
                    registry.Remove(shader);
                Object.DestroyImmediate(texture);
                Object.DestroyImmediate(material);
            }
        }

        [Test]
        public void AutoAtlasRejectsReportedNonTextureUseOfTargetUv()
        {
            var shader = Shader.Find("Standard");
            Assert.That(shader, Is.Not.Null);

            var material = new Material(shader);
            var texture = new Texture2D(4, 4);
            var registry = TTShaderTextureUsageInformationRegistry.s_information;
            var hadPrevious = registry.TryGetValue(shader, out var previous);
            try
            {
                material.SetTexture("_MainTex", texture);
                var provider = new TestShaderUsageInformation
                {
                    OtherUvUsage = UsingUVChannels.UV1,
                };
                registry[shader] = provider;

                var grouping = new MaterialGroupingContext(
                    new HashSet<Material> { material },
                    UVChannel.UV0,
                    primaryTexturePropertyOrMaximum: null);

                Assert.That(
                    AutoAtlasTextureProcessor.HasUnsupportedTextureUvSampling(
                        new[] { material },
                        grouping.ContainsTextureDictionaries,
                        UVChannel.UV0),
                    Is.False);

                provider.OtherUvUsage = UsingUVChannels.UV0;
                Assert.That(
                    AutoAtlasTextureProcessor.HasUnsupportedTextureUvSampling(
                        new[] { material },
                        grouping.ContainsTextureDictionaries,
                        UVChannel.UV0),
                    Is.True);
            }
            finally
            {
                if (hadPrevious)
                    registry[shader] = previous;
                else
                    registry.Remove(shader);
                Object.DestroyImmediate(texture);
                Object.DestroyImmediate(material);
            }
        }

        [Test]
        public void AutoAtlasProtectsVertexIndexOnlyWhenMeshWillBeReindexed()
        {
            var shader = Shader.Find("Standard");
            Assert.That(shader, Is.Not.Null);

            var material = new Material(shader);
            var texture = new Texture2D(4, 4);
            var root = new GameObject("VertexIndexSafety");
            var simpleMesh = new Mesh();
            var crossMesh = new Mesh();
            var registry = TTShaderTextureUsageInformationRegistry.s_information;
            var hadPrevious = registry.TryGetValue(shader, out var previous);
            try
            {
                material.SetTexture("_MainTex", texture);
                registry[shader] = new TestShaderUsageInformation
                {
                    UsesVertexIndex = true,
                };

                void SetGeometry(Mesh mesh, bool crossSubMesh)
                {
                    mesh.vertices = new[]
                    {
                        Vector3.zero,
                        Vector3.right,
                        Vector3.up,
                        Vector3.one,
                    };
                    mesh.uv = new[]
                    {
                        Vector2.zero,
                        Vector2.right,
                        Vector2.up,
                        Vector2.one,
                    };

                    if (crossSubMesh)
                    {
                        mesh.subMeshCount = 2;
                        mesh.SetTriangles(new[] { 0, 1, 2 }, 0);
                        mesh.SetTriangles(new[] { 0, 2, 3 }, 1);
                    }
                    else
                    {
                        mesh.subMeshCount = 1;
                        mesh.SetTriangles(new[] { 0, 1, 2 }, 0);
                    }
                }

                MeshRenderer AddRenderer(
                    string name,
                    Mesh mesh,
                    Material[] materials)
                {
                    var go = new GameObject(name);
                    go.transform.SetParent(root.transform, false);
                    var filter = go.AddComponent<MeshFilter>();
                    filter.sharedMesh = mesh;
                    var renderer = go.AddComponent<MeshRenderer>();
                    renderer.sharedMaterials = materials;
                    return renderer;
                }

                SetGeometry(simpleMesh, crossSubMesh: false);
                var simple = AddRenderer(
                    "simple",
                    simpleMesh,
                    new[] { material });
                var simpleTargeting = new RendererTargetingForTest(
                    new Renderer[] { simple });
                Assert.That(
                    AutoAtlasTextureProcessor.HasUnsafeVertexIndexUsage(
                        simpleTargeting,
                        new Renderer[] { simple }),
                    Is.False);

                SetGeometry(crossMesh, crossSubMesh: true);
                var cross = AddRenderer(
                    "cross",
                    crossMesh,
                    new[] { material, material });
                var crossTargeting = new RendererTargetingForTest(
                    new Renderer[] { cross });
                Assert.That(
                    AutoAtlasTextureProcessor.HasUnsafeVertexIndexUsage(
                        crossTargeting,
                        new Renderer[] { cross }),
                    Is.True);
            }
            finally
            {
                if (hadPrevious)
                    registry[shader] = previous;
                else
                    registry.Remove(shader);
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(simpleMesh);
                Object.DestroyImmediate(crossMesh);
                Object.DestroyImmediate(texture);
                Object.DestroyImmediate(material);
            }
        }

        [Test]
        public void PreservedOriginalUvChannelRejectsExistingShaderUsage()
        {
            var shader = Shader.Find("Standard");
            Assert.That(shader, Is.Not.Null);

            var material = new Material(shader);
            var texture = new Texture2D(4, 4);
            var root = new GameObject("PreservedUvSafety");
            var mesh = new Mesh();
            var registry = TTShaderTextureUsageInformationRegistry.s_information;
            var hadPrevious = registry.TryGetValue(shader, out var previous);
            try
            {
                material.SetTexture("_MainTex", texture);
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
                mesh.SetTriangles(new[] { 0, 1, 2 }, 0);

                var filter = root.AddComponent<MeshFilter>();
                filter.sharedMesh = mesh;
                var renderer = root.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = material;
                var targeting = new RendererTargetingForTest(
                    new Renderer[] { renderer });

                registry[shader] = new TestShaderUsageInformation
                {
                    TextureUvChannel = UsageUVChannel.UV0,
                };
                Assert.That(
                    AutoAtlasTextureProcessor
                        .HasUnsafePreservedOriginalUvUsage(
                            targeting,
                            new Renderer[] { renderer },
                            preservedUvChannel: 1),
                    Is.False);

                registry[shader] = new TestShaderUsageInformation
                {
                    TextureUvChannel = UsageUVChannel.UV1,
                };
                Assert.That(
                    AutoAtlasTextureProcessor
                        .HasUnsafePreservedOriginalUvUsage(
                            targeting,
                            new Renderer[] { renderer },
                            preservedUvChannel: 1),
                    Is.True);
            }
            finally
            {
                if (hadPrevious)
                    registry[shader] = previous;
                else
                    registry.Remove(shader);
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(mesh);
                Object.DestroyImmediate(texture);
                Object.DestroyImmediate(material);
            }
        }

        [Test]
        public void LiltoonParallaxMarksMainUvTransformUnknown()
        {
            var baseline = new LiltoonMaterialInformationProbe();
            LiltoonShaderInformation.GetMaterialInformation(
                baseline,
                isOutline: false);
            Assert.That(
                baseline.TextureMatrices["_MainTex"],
                Is.EqualTo(Matrix2x3.Identity));

            var parallax = new LiltoonMaterialInformationProbe();
            parallax.IntValues["_UseParallax"] = 1;
            LiltoonShaderInformation.GetMaterialInformation(
                parallax,
                isOutline: false);
            Assert.That(
                parallax.TextureMatrices["_MainTex"],
                Is.Null);
        }

        [Test]
        public void LiltoonDecalModesMarkSubTextureUvTransformUnknown()
        {
            var baseline = new LiltoonMaterialInformationProbe();
            baseline.IntValues["_UseMain2ndTex"] = 1;
            LiltoonShaderInformation.GetMaterialInformation(
                baseline,
                isOutline: false);
            Assert.That(
                baseline.TextureMatrices["_Main2ndTex"],
                Is.EqualTo(Matrix2x3.Identity));

            var decal = new LiltoonMaterialInformationProbe();
            decal.IntValues["_UseMain2ndTex"] = 1;
            decal.FloatValues["_Main2ndTexIsDecal"] = 1f;
            LiltoonShaderInformation.GetMaterialInformation(
                decal,
                isOutline: false);
            Assert.That(
                decal.TextureMatrices["_Main2ndTex"],
                Is.Null);

            var animated = new LiltoonMaterialInformationProbe();
            animated.IntValues["_UseMain2ndTex"] = 1;
            animated.VectorValues["_Main2ndTexDecalSubParam"] =
                new Vector4(0.5f, 1f, 0f, 1f);
            LiltoonShaderInformation.GetMaterialInformation(
                animated,
                isOutline: false);
            Assert.That(
                animated.TextureMatrices["_Main2ndTex"],
                Is.Null);
        }

        [Test]
        public void LiltoonDissolveShapeReportsRawUv0Usage()
        {
            var maskOnly = new LiltoonMaterialInformationProbe();
            maskOnly.VectorValues["_DissolveParams"] =
                new Vector4(1f, 0f, 0.5f, 0.1f);
            LiltoonShaderInformation.GetMaterialInformation(
                maskOnly,
                isOutline: false);
            Assert.That(
                maskOnly.OtherUvUsage & UsingUVChannels.UV0,
                Is.EqualTo((UsingUVChannels)0));

            var uvShape = new LiltoonMaterialInformationProbe();
            uvShape.VectorValues["_DissolveParams"] =
                new Vector4(2f, 0f, 0.5f, 0.1f);
            LiltoonShaderInformation.GetMaterialInformation(
                uvShape,
                isOutline: false);
            Assert.That(
                uvShape.OtherUvUsage & UsingUVChannels.UV0,
                Is.EqualTo(UsingUVChannels.UV0));
        }

        [Test]
        public void LiltoonGlitterReportsProceduralAndTextureUvSemantics()
        {
            var probe = new LiltoonMaterialInformationProbe();
            probe.IntValues["_UseGlitter"] = 1;
            probe.IntValues["_GlitterUVMode"] = 0;
            probe.IntValues["_GlitterColorTex_UVMode"] = 2;

            LiltoonShaderInformation.GetMaterialInformation(
                probe,
                isOutline: false);

            Assert.That(
                probe.OtherUvUsage & UsingUVChannels.UV0,
                Is.EqualTo(UsingUVChannels.UV0));
            Assert.That(
                probe.TextureUvChannels["_GlitterColorTex"],
                Is.EqualTo(UsingUVChannels.UV2));
        }

        [Test]
        public void LiltoonIdMaskReportsSelectedRawUvOrVertexId()
        {
            var uvBacked = new LiltoonMaterialInformationProbe();
            uvBacked.IntValues["_IDMask1"] = 1;
            uvBacked.IntValues["_IDMaskFrom"] = 0;

            LiltoonShaderInformation.GetMaterialInformation(
                uvBacked,
                isOutline: false);

            Assert.That(
                uvBacked.OtherUvUsage & UsingUVChannels.UV0,
                Is.EqualTo(UsingUVChannels.UV0));
            Assert.That(uvBacked.UsesVertexIndex, Is.False);

            var vertexBacked = new LiltoonMaterialInformationProbe();
            vertexBacked.IntValues["_IDMask1"] = 1;
            vertexBacked.IntValues["_IDMaskFrom"] = 8;

            LiltoonShaderInformation.GetMaterialInformation(
                vertexBacked,
                isOutline: false);

            Assert.That(vertexBacked.UsesVertexIndex, Is.True);
        }

        [Test]
        public void LiltoonAudioLinkMode2ReportsRawUv0Usage()
        {
            var fragment = new LiltoonMaterialInformationProbe();
            fragment.IntValues["_UseAudioLink"] = 1;
            fragment.IntValues["_AudioLink2Main2nd"] = 1;
            fragment.IntValues["_AudioLinkUVMode"] = 2;

            LiltoonShaderInformation.GetMaterialInformation(
                fragment,
                isOutline: false);

            Assert.That(
                fragment.OtherUvUsage & UsingUVChannels.UV0,
                Is.EqualTo(UsingUVChannels.UV0));

            var vertex = new LiltoonMaterialInformationProbe();
            vertex.IntValues["_UseAudioLink"] = 1;
            vertex.IntValues["_AudioLink2Vertex"] = 1;
            vertex.IntValues["_AudioLinkVertexUVMode"] = 2;

            LiltoonShaderInformation.GetMaterialInformation(
                vertex,
                isOutline: false);

            Assert.That(
                vertex.OtherUvUsage & UsingUVChannels.UV0,
                Is.EqualTo(UsingUVChannels.UV0));
        }

        [Test]
        public void LiltoonFurRandomizeReportsVertexIndexUsage()
        {
            var disabledRandomize = new LiltoonMaterialInformationProbe();
            LiltoonShaderInformation.GetMaterialInformation(
                disabledRandomize,
                isOutline: false,
                shaderUsesFurVertexId: true);
            Assert.That(disabledRandomize.UsesVertexIndex, Is.False);

            var dedicatedFur = new LiltoonMaterialInformationProbe();
            dedicatedFur.FloatValues["_FurRandomize"] = 0.5f;
            LiltoonShaderInformation.GetMaterialInformation(
                dedicatedFur,
                isOutline: false,
                shaderUsesFurVertexId: true);
            Assert.That(dedicatedFur.UsesVertexIndex, Is.True);

            var multiFur = new LiltoonMaterialInformationProbe();
            multiFur.IntValues["_TransparentMode"] = 4;
            multiFur.FloatValues["_FurRandomize"] = 0.5f;
            LiltoonShaderInformation.GetMaterialInformation(
                multiFur,
                isOutline: false);
            Assert.That(multiFur.UsesVertexIndex, Is.True);

            var multiNonFur = new LiltoonMaterialInformationProbe();
            multiNonFur.IntValues["_TransparentMode"] = 2;
            multiNonFur.FloatValues["_FurRandomize"] = 0.5f;
            LiltoonShaderInformation.GetMaterialInformation(
                multiNonFur,
                isOutline: false);
            Assert.That(multiNonFur.UsesVertexIndex, Is.False);
        }

        [Test]
        public void LiltoonMain2ndUsesItsOwnUvMode()
        {
            var probe = new LiltoonMaterialInformationProbe();
            probe.IntValues["_UseMain2ndTex"] = 1;
            probe.IntValues["_Main2ndTex_UVMode"] = 2;
            probe.IntValues["_Main3rdTex_UVMode"] = 0;

            LiltoonShaderInformation.GetMaterialInformation(
                probe,
                isOutline: false);

            Assert.That(
                probe.TextureUvChannels["_Main2ndTex"],
                Is.EqualTo(UsingUVChannels.UV2));
        }

        [Test]
        public void LiltoonMain3rdUsesItsOwnUvMode()
        {
            var probe = new LiltoonMaterialInformationProbe();
            probe.IntValues["_UseMain3rdTex"] = 1;
            probe.IntValues["_Main2ndTex_UVMode"] = 0;
            probe.IntValues["_Main3rdTex_UVMode"] = 1;

            LiltoonShaderInformation.GetMaterialInformation(
                probe,
                isOutline: false);

            Assert.That(
                probe.TextureUvChannels["_Main3rdTex"],
                Is.EqualTo(UsingUVChannels.UV1));
        }

        [Test]
        public void LiltoonAudioLinkMaskUsesItsOwnUvMode()
        {
            var probe = new LiltoonMaterialInformationProbe();
            probe.IntValues["_UseAudioLink"] = 1;
            probe.IntValues["_AudioLink2Main2nd"] = 1;
            probe.IntValues["_AudioLinkUVMode"] = 3;
            probe.IntValues["_AudioLinkMask_UVMode"] = 1;
            probe.IntValues["_OutlineVectorUVMode"] = 0;

            LiltoonShaderInformation.GetMaterialInformation(
                probe,
                isOutline: false);

            Assert.That(
                probe.TextureUvChannels["_AudioLinkMask"],
                Is.EqualTo(UsingUVChannels.UV1));
        }

        [Test]
        public void LiltoonOutlineVectorUsesItsOwnUvMode()
        {
            var probe = new LiltoonMaterialInformationProbe();
            probe.IntValues["_OutlineVectorUVMode"] = 2;
            probe.IntValues["_AudioLinkMask_UVMode"] = 0;

            LiltoonShaderInformation.GetMaterialInformation(
                probe,
                isOutline: true);

            Assert.That(
                probe.TextureUvChannels["_OutlineVectorTex"],
                Is.EqualTo(UsingUVChannels.UV2));
        }

        [Test]
        public void UnknownMemoryBlockLayoutsFailClosed()
        {
            Assert.That(
                AtlasTexture.TryGetMemoryBlockLayout(
                    (TextureFormat)int.MaxValue,
                    out _),
                Is.False);
            Assert.That(
                AtlasTexture.TryGetMemoryBlockLayout(
                    (GraphicsFormat)int.MaxValue,
                    out _),
                Is.False);

            Assert.That(
                AtlasTexture.TryGetMemoryBlockLayout(
                    TextureFormat.RGBA32,
                    out var rgbaBlock),
                Is.True);
            Assert.That(rgbaBlock, Is.EqualTo((1u, 1u, 4u)));
        }

        [Test]
        public void SourceTextureMemoryUsesActualMipCount()
        {
            var texture = new Texture2D(
                16,
                16,
                TextureFormat.RGBA32,
                2,
                false);
            try
            {
                Assert.That(texture.mipmapCount, Is.EqualTo(2));

                var descriptor = new TexTransToolTextureDescriptor
                {
                    UseMipMap = true,
                };
                Assert.That(
                    AtlasTexture.GetSourceTextureMipCount(
                        texture,
                        descriptor),
                    Is.EqualTo(2));

                descriptor.UseMipMap = false;
                Assert.That(
                    AtlasTexture.GetSourceTextureMipCount(
                        texture,
                        descriptor),
                    Is.EqualTo(1));
            }
            finally
            {
                Object.DestroyImmediate(texture);
            }
        }

        [Test]
        public void RetainedSourceTextureIsDetectedBeforeMaterialReplacement()
        {
            var shader = Shader.Find("Standard");
            Assert.That(shader, Is.Not.Null);

            var source = new Texture2D(4, 4);
            var replacement = new Texture2D(4, 4);
            var material = new Material(shader);
            try
            {
                material.SetTexture("_MainTex", source);
                var sourceTextures = new HashSet<Texture> { source };

                Assert.That(
                    AutoAtlasTextureProcessor.RetainsAnySourceTexture(
                        new[] { material },
                        sourceTextures),
                    Is.True);

                material.SetTexture("_MainTex", replacement);
                Assert.That(
                    AutoAtlasTextureProcessor.RetainsAnySourceTexture(
                        new[] { material },
                        sourceTextures),
                    Is.False);
            }
            finally
            {
                Object.DestroyImmediate(material);
                Object.DestroyImmediate(source);
                Object.DestroyImmediate(replacement);
            }
        }

        [Test]
        public void DestroyUnappliedAtlasedMaterialsDestroysTemporaryMaterials()
        {
            var shader = Shader.Find("Hidden/InternalErrorShader");
            Assert.That(shader, Is.Not.Null);

            var first = new Material(shader);
            var second = new Material(shader);

            AutoAtlasTextureProcessor.DestroyUnappliedAtlasedMaterials(
                new[] { first, first, second });

            Assert.That(first == null, Is.True);
            Assert.That(second == null, Is.True);
        }

        [Test]
        public void DestroyUnappliedAtlasedMeshesDestroysTemporaryMeshes()
        {
            var first = new Mesh();
            var second = new Mesh();

            AutoAtlasTextureProcessor.DestroyUnappliedAtlasedMeshes(
                new[] { first, first, second });

            Assert.That(first == null, Is.True);
            Assert.That(second == null, Is.True);
        }

        [Test]
        public void GeneratedPropertyApplicationRejectsExcludedSameNamedTexture()
        {
            var shader = Shader.Find("Standard");
            Assert.That(shader, Is.Not.Null);

            var first = new Material(shader);
            var second = new Material(shader);
            var firstTexture = new Texture2D(4, 4);
            var secondTexture = new Texture2D(4, 4);
            try
            {
                first.SetTexture("_MainTex", firstTexture);
                second.SetTexture("_MainTex", secondTexture);

                var unsafeProperties =
                    new Dictionary<
                        Material,
                        IReadOnlyDictionary<string, Texture>>
                    {
                        [first] = new Dictionary<string, Texture>
                        {
                            ["_MainTex"] = firstTexture,
                        },
                        [second] = new Dictionary<string, Texture>(),
                    };

                Assert.That(
                    AutoAtlasTextureProcessor
                        .HasUnsafeGeneratedPropertyApplication(
                            new[] { first, second },
                            unsafeProperties,
                            UVChannel.UV0),
                    Is.True);

                var safeProperties =
                    new Dictionary<
                        Material,
                        IReadOnlyDictionary<string, Texture>>
                    {
                        [first] = new Dictionary<string, Texture>
                        {
                            ["_MainTex"] = firstTexture,
                        },
                        [second] = new Dictionary<string, Texture>
                        {
                            ["_MainTex"] = secondTexture,
                        },
                    };

                Assert.That(
                    AutoAtlasTextureProcessor
                        .HasUnsafeGeneratedPropertyApplication(
                            new[] { first, second },
                            safeProperties,
                            UVChannel.UV0),
                    Is.False);
            }
            finally
            {
                Object.DestroyImmediate(firstTexture);
                Object.DestroyImmediate(secondTexture);
                Object.DestroyImmediate(first);
                Object.DestroyImmediate(second);
            }
        }

        [Test]
        public void GeneratedPropertySafetySkipsPropertiesAbsentFromCurrentShader()
        {
            var shader = Shader.Find("Standard");
            Assert.That(shader, Is.Not.Null);

            var material = new Material(shader);
            var texture = new Texture2D(4, 4);
            try
            {
                Assert.That(
                    material.HasShaderTextureProperty("_MainTex"),
                    Is.True);
                Assert.That(
                    material.HasShaderTextureProperty("_FurNoiseMask"),
                    Is.False);

                var properties =
                    new Dictionary<
                        Material,
                        IReadOnlyDictionary<string, Texture>>
                    {
                        [material] = new Dictionary<string, Texture>
                        {
                            ["_FurNoiseMask"] = texture,
                        },
                    };

                Assert.That(
                    AutoAtlasTextureProcessor
                        .HasUnsafeGeneratedPropertyApplication(
                            new[] { material },
                            properties,
                            UVChannel.UV0),
                    Is.False,
                    "A generated property that does not exist in the material's current shader is not an application conflict.");
            }
            finally
            {
                Object.DestroyImmediate(texture);
                Object.DestroyImmediate(material);
            }
        }

        [Test]
        public void ReferenceCopyRejectsCrossGroupTextureRedirection()
        {
            var shader = Shader.Find("Standard");
            Assert.That(shader, Is.Not.Null);

            var mainMaterial = new Material(shader);
            var bumpMaterial = new Material(shader);
            var mainTexture = new Texture2D(4, 4);
            var bumpTexture = new Texture2D(4, 4);
            try
            {
                mainMaterial.SetTexture("_MainTex", mainTexture);
                mainMaterial.SetTexture("_BumpMap", null);
                bumpMaterial.SetTexture("_MainTex", null);
                bumpMaterial.SetTexture("_BumpMap", bumpTexture);

                var settings = new AtlasSetting
                {
                    TextureFineTuning = new List<ITextureFineTuning>
                    {
                        new ReferenceCopy
                        {
                            SourcePropertyName = new PropertyName("_MainTex"),
                            TargetPropertyNameList =
                                new List<PropertyName>
                                {
                                    new PropertyName("_BumpMap"),
                                },
                        },
                    },
                };

                var unsafeProperties =
                    new Dictionary<
                        Material,
                        IReadOnlyDictionary<string, Texture>>
                    {
                        [mainMaterial] = new Dictionary<string, Texture>
                        {
                            ["_MainTex"] = mainTexture,
                        },
                        [bumpMaterial] = new Dictionary<string, Texture>
                        {
                            ["_BumpMap"] = bumpTexture,
                        },
                    };

                Assert.That(
                    AutoAtlasTextureMergePlanner
                        .PreservesReferenceCopySemantics(
                            new[] { mainMaterial, bumpMaterial },
                            unsafeProperties,
                            settings),
                    Is.False,
                    "Merging makes _MainTex exist and would redirect the other atomic group's _BumpMap.");

                bumpMaterial.SetTexture("_BumpMap", mainTexture);
                var equivalentSourcePixels =
                    new Dictionary<
                        Material,
                        IReadOnlyDictionary<string, Texture>>
                    {
                        [mainMaterial] = new Dictionary<string, Texture>
                        {
                            ["_MainTex"] = mainTexture,
                        },
                        [bumpMaterial] = new Dictionary<string, Texture>
                        {
                            ["_BumpMap"] = mainTexture,
                        },
                    };

                Assert.That(
                    AutoAtlasTextureMergePlanner
                        .PreservesReferenceCopySemantics(
                            new[] { mainMaterial, bumpMaterial },
                            equivalentSourcePixels,
                            settings),
                    Is.True,
                    "ReferenceCopy is safe when the material group source atlas is drawn from the exact texture already sampled by the target.");

                bumpMaterial.SetTextureScale(
                    "_BumpMap",
                    new Vector2(2f, 1f));
                Assert.That(
                    AutoAtlasTextureMergePlanner
                        .PreservesReferenceCopySemantics(
                            new[] { mainMaterial, bumpMaterial },
                            equivalentSourcePixels,
                            settings),
                    Is.False,
                    "Equivalent source pixels are insufficient when the target property applies a different UV transform.");
            }
            finally
            {
                Object.DestroyImmediate(mainTexture);
                Object.DestroyImmediate(bumpTexture);
                Object.DestroyImmediate(mainMaterial);
                Object.DestroyImmediate(bumpMaterial);
            }
        }

        [Test]
        public void RequiredAtlasMeshIgnoresEmptyTargetSubmeshButKeepsRepeatedGeometry()
        {
            var shader = Shader.Find("Hidden/InternalErrorShader");
            Assert.That(shader, Is.Not.Null);

            var target = new Material(shader);
            var other = new Material(shader);
            var mesh = new Mesh();
            var gameObject = new GameObject("RequiredAtlasMesh");
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
                mesh.subMeshCount = 2;
                mesh.SetTriangles(System.Array.Empty<int>(), 0);
                mesh.SetTriangles(new[] { 0, 1, 2 }, 1);

                var filter = gameObject.AddComponent<MeshFilter>();
                filter.sharedMesh = mesh;
                var renderer = gameObject.AddComponent<MeshRenderer>();

                renderer.sharedMaterials = new[] { target, other };
                var targeting = new RendererTargetingForTest(
                    new Renderer[] { renderer });

                Assert.That(
                    AutoAtlasTextureProcessor.RendererRequiresAtlasMesh(
                        targeting,
                        renderer,
                        new HashSet<Material> { target }),
                    Is.False,
                    "A target material used only by an empty effective submesh has no UVs to rewrite.");

                renderer.sharedMaterials = new[] { other, target };
                Assert.That(
                    AutoAtlasTextureProcessor.RendererRequiresAtlasMesh(
                        targeting,
                        renderer,
                        new HashSet<Material> { target }),
                    Is.True);

                mesh.subMeshCount = 1;
                mesh.SetTriangles(new[] { 0, 1, 2 }, 0);
                renderer.sharedMaterials = new[] { other, target };
                Assert.That(
                    AutoAtlasTextureProcessor.RendererRequiresAtlasMesh(
                        targeting,
                        renderer,
                        new HashSet<Material> { target }),
                    Is.True,
                    "Extra material slots repeat the final non-empty submesh.");
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
                Object.DestroyImmediate(mesh);
                Object.DestroyImmediate(target);
                Object.DestroyImmediate(other);
            }
        }

        [Test]
        public void AtlasSubSetIgnoresTargetMaterialOnEmptySubMesh()
        {
            var shader = Shader.Find("Hidden/InternalErrorShader");
            Assert.That(shader, Is.Not.Null);

            var material = new Material(shader);
            var mesh = new Mesh();
            try
            {
                mesh.vertices = new[]
                {
                    Vector3.zero,
                    Vector3.right,
                    Vector3.up,
                };
                mesh.subMeshCount = 1;
                mesh.SetTriangles(System.Array.Empty<int>(), 0);

                var targetMaterials = new HashSet<Material> { material };
                var grouping = new MaterialGroupingContext(
                    targetMaterials,
                    new Dictionary<
                        Material,
                        IReadOnlyDictionary<string, Texture>>
                    {
                        [material] =
                            new Dictionary<string, Texture>(),
                    },
                    null);

                var subSet =
                    AtlasSubMeshIndexIDSetContext.BuildAtlasSubSet(
                        mesh,
                        new Material[] { material },
                        7,
                        targetMaterials,
                        grouping);

                Assert.That(subSet.All(item => !item.HasValue), Is.True);
            }
            finally
            {
                Object.DestroyImmediate(mesh);
                Object.DestroyImmediate(material);
            }
        }

        [Test]
        public void AtlasSubSetKeepsNonEmptySlotsAndRepeatedLastSubMesh()
        {
            var shader = Shader.Find("Hidden/InternalErrorShader");
            Assert.That(shader, Is.Not.Null);

            var material = new Material(shader);
            var mesh = new Mesh();
            try
            {
                mesh.vertices = new[]
                {
                    Vector3.zero,
                    Vector3.right,
                    Vector3.up,
                };
                mesh.subMeshCount = 2;
                mesh.SetTriangles(new[] { 0, 1, 2 }, 0);
                mesh.SetTriangles(System.Array.Empty<int>(), 1);

                var targetMaterials = new HashSet<Material> { material };
                var grouping = new MaterialGroupingContext(
                    targetMaterials,
                    new Dictionary<
                        Material,
                        IReadOnlyDictionary<string, Texture>>
                    {
                        [material] =
                            new Dictionary<string, Texture>(),
                    },
                    null);

                var mixed =
                    AtlasSubMeshIndexIDSetContext.BuildAtlasSubSet(
                        mesh,
                        new Material[] { material, material },
                        3,
                        targetMaterials,
                        grouping);
                Assert.That(mixed[0].HasValue, Is.True);
                Assert.That(mixed[1].HasValue, Is.False);

                mesh.SetTriangles(new[] { 0, 1, 2 }, 1);
                var repeated =
                    AtlasSubMeshIndexIDSetContext.BuildAtlasSubSet(
                        mesh,
                        new Material[] { material, material, material },
                        3,
                        targetMaterials,
                        grouping);
                Assert.That(repeated.All(item => item.HasValue), Is.True);
                Assert.That(repeated[2]!.Value.SubMeshIndex, Is.EqualTo(2));
            }
            finally
            {
                Object.DestroyImmediate(mesh);
                Object.DestroyImmediate(material);
            }
        }

        [TestCase(1L, 2L, true)]
        [TestCase(2L, 2L, false)]
        [TestCase(3L, 2L, false)]
        [TestCase(0L, 2L, false)]
        public void FinalMergePixelBenefitMustBeStrict(
            long finalPixels,
            long baselinePixels,
            bool expected)
        {
            Assert.That(
                AutoAtlasTextureProcessor.HasStrictMergePixelBenefit(
                    finalPixels,
                    baselinePixels),
                Is.EqualTo(expected));
        }

        [Test]
        public void CandidateAcceptsStrictlyBetterNonRegressingMerge()
        {
            var baseline = Plan(
                new[]
                {
                    Entry(64, 64, 1),
                    Entry(64, 64, 1),
                },
                new Dictionary<int, double>
                {
                    [0] = 4d,
                    [1] = 4d,
                });
            var merged = Plan(
                new[]
                {
                    Entry(64, 64, 1),
                },
                new Dictionary<int, double>
                {
                    [0] = 4d,
                    [1] = 4d,
                });

            Assert.That(Candidate(baseline, merged).IsAdoptable, Is.True);
        }

        [Test]
        public void CandidateRejectsWhenPixelCountDoesNotDecrease()
        {
            var baseline = Plan(new[]
            {
                Entry(64, 64, 1, blockSize: 4),
                Entry(64, 64, 1, blockSize: 4),
            });
            var merged = Plan(new[]
            {
                Entry(64, 64, 1, blockSize: 2),
                Entry(64, 64, 1, blockSize: 2),
            });

            Assert.That(Candidate(baseline, merged).IsAdoptable, Is.False);
        }

        [Test]
        public void CandidateRejectsWhenResidentMemoryDoesNotDecrease()
        {
            var baseline = Plan(new[]
            {
                Entry(64, 64, 1, blockSize: 2),
                Entry(64, 64, 1, blockSize: 2),
            });
            var merged = Plan(new[]
            {
                Entry(64, 64, 1, blockSize: 4),
            });

            Assert.That(Candidate(baseline, merged).IsAdoptable, Is.False);
        }

        [Test]
        public void CandidateAllowsMaximumTextureDimensionIncreaseWhenOtherCostsImprove()
        {
            var baseline = Plan(new[]
            {
                Entry(128, 128, 1),
                Entry(128, 128, 1),
            });
            var merged = Plan(new[]
            {
                Entry(256, 32, 1),
            });

            Assert.That(Candidate(baseline, merged).IsAdoptable, Is.True);
        }

        [Test]
        public void MergeSelectionCarriesPredictedCostMetrics()
        {
            var baseline = Plan(new[]
            {
                Entry(128, 128, 1),
                Entry(128, 128, 1),
            });
            var merged = Plan(new[]
            {
                Entry(256, 64, 1),
            });
            var candidate = new AutoAtlasTextureMergePlanner.Candidate(
                new[] { 2, 5 },
                baseline,
                merged,
                new Vector2Int(256, 64),
                new Dictionary<string, Vector2Int>
                {
                    ["_MainTex"] = new Vector2Int(256, 64),
                });
            var selection =
                new AutoAtlasTextureMergePlanner.MergeSelection(candidate);

            Assert.That(selection.BaselinePixels,
                Is.EqualTo(baseline.PixelCount));
            Assert.That(selection.PredictedMergedPixels,
                Is.EqualTo(merged.PixelCount));
            Assert.That(selection.BaselineBytes,
                Is.EqualTo(baseline.FullResidentBytes));
            Assert.That(selection.PredictedMergedBytes,
                Is.EqualTo(merged.FullResidentBytes));
            Assert.That(selection.PredictedSavingBytes,
                Is.EqualTo(
                    baseline.FullResidentBytes
                    - merged.FullResidentBytes));
        }

        [Test]
        public void CandidateRejectsSamplingCostRegression()
        {
            var baseline = Plan(
                new[]
                {
                    Entry(64, 64, 1),
                    Entry(64, 64, 1),
                },
                new Dictionary<int, double>
                {
                    [0] = 4d,
                    [1] = 4d,
                });
            var merged = Plan(
                new[]
                {
                    Entry(64, 64, 1),
                },
                new Dictionary<int, double>
                {
                    [0] = 8d,
                    [1] = 4d,
                });

            Assert.That(Candidate(baseline, merged).IsAdoptable, Is.False);
        }

        [Test]
        public void AtlasRendererEligibilityRejectsMeshWithoutSubMeshes()
        {
            var shader = Shader.Find("Hidden/InternalErrorShader");
            Assert.That(shader, Is.Not.Null);

            var material = new Material(shader);
            var mesh = new Mesh();
            var gameObject = new GameObject("NoSubMeshes");
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
                mesh.subMeshCount = 0;

                var filter = gameObject.AddComponent<MeshFilter>();
                filter.sharedMesh = mesh;
                var renderer = gameObject.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = material;

                Assert.That(
                    AtlasTexture.IsAtlasAllowedRenderer(renderer),
                    Is.False);
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
                Object.DestroyImmediate(mesh);
                Object.DestroyImmediate(material);
            }
        }

        [Test]
        public void AtlasRendererEligibilityRequiresDrawableTriangles()
        {
            var shader = Shader.Find("Hidden/InternalErrorShader");
            Assert.That(shader, Is.Not.Null);

            var material = new Material(shader);
            var mesh = new Mesh();
            var gameObject = new GameObject("Topology");
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
                mesh.subMeshCount = 1;

                var filter = gameObject.AddComponent<MeshFilter>();
                filter.sharedMesh = mesh;
                var renderer = gameObject.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = material;

                mesh.SetIndices(
                    System.Array.Empty<int>(),
                    MeshTopology.Triangles,
                    0);
                Assert.That(
                    AtlasTexture.IsAtlasAllowedRenderer(renderer),
                    Is.False);

                mesh.SetIndices(
                    System.Array.Empty<int>(),
                    MeshTopology.Lines,
                    0);
                Assert.That(
                    AtlasTexture.IsAtlasAllowedRenderer(renderer),
                    Is.False,
                    "Even an empty non-triangle submesh is unsupported because AtlasTexture reads every submesh through triangle-only APIs.");

                mesh.SetIndices(
                    new[] { 0, 1 },
                    MeshTopology.Lines,
                    0);
                Assert.That(
                    AtlasTexture.IsAtlasAllowedRenderer(renderer),
                    Is.False);

                mesh.SetTriangles(new[] { 0, 1, 2 }, 0);
                Assert.That(
                    AtlasTexture.IsAtlasAllowedRenderer(renderer),
                    Is.True);
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
                Object.DestroyImmediate(mesh);
                Object.DestroyImmediate(material);
            }
        }

        [Test]
        public void AutoAtlasRendererEligibilityMatchesManualAtlasDefault()
        {
            var shader = Shader.Find("Hidden/InternalErrorShader");
            Assert.That(shader, Is.Not.Null);

            var material = new Material(shader);
            var mesh = new Mesh();
            var root = new GameObject("root");
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
                mesh.SetTriangles(new[] { 0, 1, 2 }, 0);

                MeshRenderer AddRenderer(string name)
                {
                    var go = new GameObject(name);
                    go.transform.SetParent(root.transform, false);
                    var filter = go.AddComponent<MeshFilter>();
                    filter.sharedMesh = mesh;
                    var renderer = go.AddComponent<MeshRenderer>();
                    renderer.sharedMaterial = material;
                    return renderer;
                }

                var active = AddRenderer("active");
                var disabled = AddRenderer("disabled");
                disabled.enabled = false;
                var inactive = AddRenderer("inactive");
                inactive.gameObject.SetActive(false);
                var explicitlyExcluded = AddRenderer("explicitlyExcluded");

                var configuration =
                    root.AddComponent<TextureAtlas.AutoAtlasTexture>();
                configuration.ExcludedRenderers.Add(explicitlyExcluded);

                var targeting = new RendererTargetingForTest(new Renderer[]
                {
                    active,
                    disabled,
                    inactive,
                    explicitlyExcluded,
                });

                var manualDefault = TextureAtlas.AtlasTexture
                    .GetAtlasAllowedRenderers(
                        targeting,
                        targeting.EnumerateRenderer(),
                        includeDisabledRenderer: false)
                    .Where(renderer =>
                        renderer != explicitlyExcluded)
                    .ToHashSet();

                var auto = AutoAtlasTextureProcessor
                    .GetAutoAtlasAllowedRenderers(targeting, configuration)
                    .ToHashSet();

                Assert.That(auto.SetEquals(manualDefault), Is.True);
                Assert.That(auto.Contains(active), Is.True);
                Assert.That(auto.Contains(disabled), Is.False);
                Assert.That(auto.Contains(inactive), Is.False);
                Assert.That(auto.Contains(explicitlyExcluded), Is.False);

                var resolved = AutoAtlasTextureProcessor
                    .ResolveAutoAtlasTargetRenderers(
                        targeting,
                        configuration,
                        new HashSet<Material> { material })
                    .ToHashSet();
                Assert.That(resolved.SetEquals(auto), Is.True);

                var aao = AutoAtlasTextureProcessor
                    .GetAAONegotiationRenderers(targeting, configuration)
                    .ToHashSet();
                Assert.That(aao.SetEquals(auto), Is.True);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                UnityEngine.Object.DestroyImmediate(mesh);
                UnityEngine.Object.DestroyImmediate(material);
            }
        }

        [Test]
        public void ActiveUntransformedMaterialConsumerFailsClosed()
        {
            var shader = Shader.Find("Hidden/InternalErrorShader");
            Assert.That(shader, Is.Not.Null);

            var material = new Material(shader);
            var root = new GameObject("active-consumer-root");
            try
            {
                MeshRenderer AddRenderer(string name)
                {
                    var go = new GameObject(name);
                    go.transform.SetParent(root.transform, false);
                    var renderer = go.AddComponent<MeshRenderer>();
                    renderer.sharedMaterial = material;
                    return renderer;
                }

                var transformed = AddRenderer("transformed");
                var unsupported = AddRenderer("unsupported");
                var disabled = AddRenderer("disabled");
                disabled.enabled = false;

                var targeting = new RendererTargetingForTest(
                    new Renderer[]
                    {
                        transformed,
                        unsupported,
                        disabled,
                    });
                var group = new HashSet<Material> { material };

                Assert.That(
                    AutoAtlasTextureProcessor
                        .HasActiveUntransformedMaterialConsumer(
                            targeting,
                            group,
                            new Renderer[] { transformed }),
                    Is.True);

                unsupported.enabled = false;
                Assert.That(
                    AutoAtlasTextureProcessor
                        .HasActiveUntransformedMaterialConsumer(
                            targeting,
                            group,
                            new Renderer[] { transformed }),
                    Is.False);

                unsupported.enabled = true;
                unsupported.gameObject.SetActive(false);
                Assert.That(
                    AutoAtlasTextureProcessor
                        .HasActiveUntransformedMaterialConsumer(
                            targeting,
                            group,
                            new Renderer[] { transformed }),
                    Is.False);
            }
            finally
            {
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(material);
            }
        }

        private sealed class RendererTargetingForTest : IRendererTargeting
        {
            private readonly Renderer[] _renderers;

            internal RendererTargetingForTest(Renderer[] renderers)
            {
                _renderers = renderers;
            }

            public IEnumerable<Renderer> EnumerateRenderer() => _renderers;

            public bool OriginEqual(
                UnityEngine.Object left,
                UnityEngine.Object right)
            {
                return left == right;
            }
        }

        [Test]
        public void CandidateRejectsUnexecutableAtomicGroupMapping()
        {
            var baseline = Plan(new[]
            {
                Entry(64, 64, 1),
                Entry(64, 64, 1),
            });
            var merged = Plan(new[]
            {
                Entry(64, 64, 1),
            });

            var candidate = new AutoAtlasTextureMergePlanner.Candidate(
                new[] { 0, 1 },
                baseline,
                merged,
                new Vector2Int(64, 64),
                new Dictionary<string, Vector2Int>(),
                preservesAtomicGroupMapping: false);

            Assert.That(candidate.IsAdoptable, Is.False);
            Assert.That(
                candidate.RejectionReason,
                Is.EqualTo(
                    AutoAtlasTextureMergePlanner
                        .CandidateRejectionReason
                        .AtomicGroupMapping));
        }

        [Test]
        public void MaterialGroupingMustPreserveAtomicGroupBoundaries()
        {
            var shader = Shader.Find("Standard");
            Assert.That(shader, Is.Not.Null);

            var textureA = new Texture2D(4, 4);
            var textureB = new Texture2D(4, 4);
            var materialA = new Material(shader);
            var materialB = new Material(shader);
            try
            {
                var groups = new[]
                {
                    new AutoAtlasTextureMergePlanner.PreparedGroup
                    {
                        Id = 0,
                        Materials = new HashSet<Material> { materialA },
                    },
                    new AutoAtlasTextureMergePlanner.PreparedGroup
                    {
                        Id = 1,
                        Materials = new HashSet<Material> { materialB },
                    },
                };

                var disjointProperties =
                    new Dictionary<
                        Material,
                        IReadOnlyDictionary<string, Texture>>
                    {
                        [materialA] =
                            new Dictionary<string, Texture>
                            {
                                ["_MainTex"] = textureA,
                            },
                        [materialB] =
                            new Dictionary<string, Texture>
                            {
                                ["_BumpMap"] = textureB,
                            },
                    };

                Assert.That(
                    AutoAtlasTextureMergePlanner
                        .PreservesAtomicGroupBoundaries(
                            groups,
                            disjointProperties),
                    Is.False,
                    "Compatible disjoint properties are grouped together, so one material group would span two planned atomic regions.");

                var conflictingSharedProperty =
                    new Dictionary<
                        Material,
                        IReadOnlyDictionary<string, Texture>>
                    {
                        [materialA] =
                            new Dictionary<string, Texture>
                            {
                                ["_MainTex"] = textureA,
                            },
                        [materialB] =
                            new Dictionary<string, Texture>
                            {
                                ["_MainTex"] = textureB,
                            },
                    };

                Assert.That(
                    AutoAtlasTextureMergePlanner
                        .PreservesAtomicGroupBoundaries(
                            groups,
                            conflictingSharedProperty),
                    Is.True,
                    "Different textures on the same property force separate material groups and keep the atomic regions executable.");
            }
            finally
            {
                Object.DestroyImmediate(materialA);
                Object.DestroyImmediate(materialB);
                Object.DestroyImmediate(textureA);
                Object.DestroyImmediate(textureB);
            }
        }

        [Test]
        public void CandidateRejectsComplementaryPropertyDensityInflation()
        {
            // Two groups have opposite high/low requirements across two
            // properties. Putting both properties on one wider shared layout
            // raises both generated property textures to the larger envelope.
            var baseline = Plan(new[]
            {
                Entry(64, 64, 1),
                Entry(32, 32, 1),
                Entry(32, 32, 1),
                Entry(64, 64, 1),
            });
            var merged = Plan(new[]
            {
                Entry(128, 64, 1),
                Entry(128, 64, 1),
            });

            Assert.That(merged.PixelCount, Is.GreaterThan(baseline.PixelCount));
            Assert.That(Candidate(baseline, merged).IsAdoptable, Is.False);
        }

        [Test]
        public void CandidateCanBecomeBeneficialAfterAddingThirdGroup()
        {
            // The pair is not useful by itself: the shared output only ties the
            // two standalone textures. A third group can make the same shared
            // output strictly cheaper than keeping all three independently.
            var pairBaseline = Plan(new[]
            {
                Entry(64, 64, 1),
                Entry(64, 64, 1),
            });
            var pairMerged = Plan(new[]
            {
                Entry(128, 64, 1),
            });

            Assert.That(
                Candidate(pairBaseline, pairMerged, new[] { 0, 1 }).IsAdoptable,
                Is.False);

            var tripleBaseline = Plan(new[]
            {
                Entry(64, 64, 1),
                Entry(64, 64, 1),
                Entry(128, 64, 1),
            });
            var tripleMerged = Plan(new[]
            {
                Entry(128, 64, 1),
            });

            Assert.That(
                tripleMerged.PixelCount,
                Is.LessThan(tripleBaseline.PixelCount));
            Assert.That(
                Candidate(
                    tripleBaseline,
                    tripleMerged,
                    new[] { 0, 1, 2 }).IsAdoptable,
                Is.True);
        }

        [Test]
        public void TextureMergingExclusionAppliesToRendererWithoutRemovingGroup()
        {
            var root = new GameObject("TextureMergingExclusionTest");
            try
            {
                var a = root.AddComponent<MeshRenderer>();
                var bObject = new GameObject("Other");
                bObject.transform.SetParent(root.transform);
                var b = bObject.AddComponent<MeshRenderer>();
                var exclusions = new HashSet<Renderer> { a };

                Assert.That(AutoAtlasTextureProcessor.IsExcludedFromTextureMerging(
                    new HashSet<Material>(), new Renderer[] { a, b },
                    new HashSet<Material>(), exclusions), Is.True);
                Assert.That(AutoAtlasTextureProcessor.IsExcludedFromTextureMerging(
                    new HashSet<Material>(), new Renderer[] { b },
                    new HashSet<Material>(), exclusions), Is.False);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void TextureMergingExclusionAppliesToAnyMaterialInAtomicGroup()
        {
            var shader = Shader.Find("Hidden/InternalErrorShader");
            Assert.That(shader, Is.Not.Null);
            var material = new Material(shader);
            try
            {
                Assert.That(AutoAtlasTextureProcessor.IsExcludedFromTextureMerging(
                    new HashSet<Material> { material },
                    System.Array.Empty<Renderer>(),
                    new HashSet<Material> { material },
                    new HashSet<Renderer>()), Is.True);
                Assert.That(AutoAtlasTextureProcessor.IsExcludedFromTextureMerging(
                    new HashSet<Material> { material },
                    System.Array.Empty<Renderer>(),
                    new HashSet<Material>(),
                    new HashSet<Renderer>()), Is.False);
            }
            finally
            {
                Object.DestroyImmediate(material);
            }
        }

        [Test]
        public void SkylineDominanceRequiresEveryColumnNoHigher()
        {
            System.Func<IReadOnlyList<int>, IReadOnlyList<int>, bool> dominates =
                AutoAtlasTextureMergePlanner.SkylineDominates;
            Assert.That(dominates(new[] { 128, 256 }, new[] { 256, 256 }),
                Is.True);
            Assert.That(dominates(new[] { 256, 256 }, new[] { 128, 256 }),
                Is.False);
            Assert.That(dominates(new[] { 128, 256 }, new[] { 256, 128 }),
                Is.False);
            Assert.That(dominates(new[] { 128, 256 }, new[] { 128, 256 }),
                Is.True);
            Assert.That(dominates(new[] { 128 }, new[] { 128, 128 }),
                Is.False);
        }

        [Test]
        public void TwoDimensionalRegionPaddingPreservesAtomicPixelDistance()
        {
            const float profilePadding = 0.01f;
            const int atlasWidth = 4096;
            const int regionWidth = 2048;

            var atlasUvPadding =
                AutoAtlas2DMergeIslandRelocator.GetRegionPaddingInAtlasUv(
                    profilePadding,
                    atlasWidth,
                    regionWidth);

            Assert.That(
                atlasUvPadding,
                Is.EqualTo(0.005f).Within(0.000001f));
            Assert.That(
                atlasUvPadding * atlasWidth,
                Is.EqualTo(profilePadding * regionWidth)
                    .Within(0.00001f));
        }

        [Test]
        public void TwoDimensionalPackingRejectsOverlapAndOutOfBounds()
        {
            var valid = new Dictionary<int, RectInt>
            {
                [3] = new RectInt(0, 0, 512, 512),
                [6] = new RectInt(512, 0, 512, 1024),
            };
            Assert.That(AutoAtlas2DMergeIslandRelocator.ValidatePlannedRegions(
                valid, 1024), Is.True);

            var overlap = new Dictionary<int, RectInt>
            {
                [3] = new RectInt(0, 0, 512, 512),
                [6] = new RectInt(256, 256, 512, 512),
            };
            Assert.That(AutoAtlas2DMergeIslandRelocator.ValidatePlannedRegions(
                overlap, 1024), Is.False);

            var outside = new Dictionary<int, RectInt>
            {
                [3] = new RectInt(0, 0, 512, 512),
                [6] = new RectInt(512, 0, 1024, 512),
            };
            Assert.That(AutoAtlas2DMergeIslandRelocator.ValidatePlannedRegions(
                outside, 1024), Is.False);

            Assert.That(AutoAtlas2DMergeIslandRelocator.ValidatePlannedRegions(
                new Dictionary<int, RectInt> { [3] = valid[3] }, 1024),
                Is.False);
        }

        [Test]
        public void TwoDimensionalPackingAcceptsValidNfdhPlacementNearBorder()
        {
            // Reproduces the measured g3 box from Seiran_Koiboshi:
            // the island is within its assigned 1024x371 region, but the
            // previous check added another full 0.01 padding to its right
            // edge and rejected it.
            var island = new net.rs64.TexTransCore.UVIsland.IslandTransform
            {
                Position = new System.Numerics.Vector2(
                    0.8077679f, 0.00999999f),
                Size = new System.Numerics.Vector2(
                    0.9929619f - 0.8077679f,
                    0.06580675f - 0.00999999f),
            };

            Assert.That(
                AutoAtlas2DMergeIslandRelocator.IsWithinPlannedRegion(
                    island, new RectInt(0, 0, 1024, 371), 1024, 0.01f),
                Is.True);
        }

        [Test]
        public void TwoDimensionalPackingProtectsInternalRegionPadding()
        {
            // Internal region boundaries must preserve the complete
            // pixel-equivalent transfer padding. For a 512-wide region in a
            // 1024 atlas, 0.01 profile padding becomes 0.005 atlas UV.
            var island = new net.rs64.TexTransCore.UVIsland.IslandTransform
            {
                Position = new System.Numerics.Vector2(0.491f, 0.01f),
                Size = new System.Numerics.Vector2(0.005f, 0.04f),
            };
            Assert.That(
                AutoAtlas2DMergeIslandRelocator.IsWithinPlannedRegion(
                    island, new RectInt(0, 0, 512, 512), 1024, 0.01f),
                Is.False);

            island.Position = new System.Numerics.Vector2(0.49f, 0.01f);
            island.Size = new System.Numerics.Vector2(0.005f, 0.04f);
            Assert.That(
                AutoAtlas2DMergeIslandRelocator.IsWithinPlannedRegion(
                    island, new RectInt(0, 0, 512, 512), 1024, 0.01f),
                Is.True);
        }


        [Test]
        public void TwoDimensionalPackingAllowsMeasuredOuterEdgePaddingClip()
        {
            // Seiran_ShuwashuwaPearl g5 measured box. The region reaches the
            // right edge of the final atlas, so transfer padding may be clipped
            // there exactly as in ordinary AtlasTexture.
            var islands = new[]
            {
                new net.rs64.TexTransCore.UVIsland.IslandTransform
                {
                    Position = new System.Numerics.Vector2(
                        0.9964194f, 0.7842883f),
                    Size = new System.Numerics.Vector2(
                        0.9970903f - 0.9964194f,
                        0.7842968f - 0.7842883f),
                },
                new net.rs64.TexTransCore.UVIsland.IslandTransform
                {
                    Position = new System.Numerics.Vector2(0.51f, 0.745f),
                    Size = new System.Numerics.Vector2(0.05f, 0.025f),
                },
            };
            var region = new RectInt(512, 751, 512, 83);
            foreach (var island in islands)
                Assert.That(
                    AutoAtlas2DMergeIslandRelocator.IsWithinPlannedRegion(
                        island, region, 1024, 0.01f), Is.True);

            var before = islands.Select(island => island.Position).ToArray();
            Assert.That(
                AutoAtlas2DMergeIslandRelocator.TryTranslateIslandsWithinPlannedRegion(
                    islands, region, 1024, 0.01f), Is.True);
            Assert.That(islands.Select(island => island.Position),
                Is.EqualTo(before));
        }

        [Test]
        public void TwoDimensionalPackingLeavesValidGroupUnchanged()
        {
            var island = new net.rs64.TexTransCore.UVIsland.IslandTransform
            {
                Position = new System.Numerics.Vector2(0.55f, 0.75f),
                Size = new System.Numerics.Vector2(0.1f, 0.025f),
            };
            var previous = island.Position;
            Assert.That(
                AutoAtlas2DMergeIslandRelocator.TryTranslateIslandsWithinPlannedRegion(
                    new[] { island }, new RectInt(512, 751, 512, 83),
                    1024, 0.01f), Is.True);
            Assert.That(island.Position, Is.EqualTo(previous));
        }

        [Test]
        public void TwoDimensionalPackingRejectsUntranslatableInternalGroupWithoutMutation()
        {
            var islands = new[]
            {
                new net.rs64.TexTransCore.UVIsland.IslandTransform
                {
                    Position = new System.Numerics.Vector2(0.257f, 0.30f),
                    Size = new System.Numerics.Vector2(0.01f, 0.01f),
                },
                new net.rs64.TexTransCore.UVIsland.IslandTransform
                {
                    Position = new System.Numerics.Vector2(0.745f, 0.30f),
                    Size = new System.Numerics.Vector2(0.004f, 0.01f),
                },
            };
            var before0 = islands[0].Position;
            var before1 = islands[1].Position;
            Assert.That(
                AutoAtlas2DMergeIslandRelocator.TryTranslateIslandsWithinPlannedRegion(
                    islands, new RectInt(256, 256, 512, 512),
                    1024, 0.01f), Is.False);
            Assert.That(islands[0].Position, Is.EqualTo(before0));
            Assert.That(islands[1].Position, Is.EqualTo(before1));
        }

        [Test]
        public void TwoDimensionalPackingPreservesRotatedIslandGeometry()
        {
            var island = new net.rs64.TexTransCore.UVIsland.IslandTransform
            {
                Position = new System.Numerics.Vector2(0.994f, 0.77f),
                Size = new System.Numerics.Vector2(0.006f, 0.003f),
                Rotation = -(float)System.Math.PI / 2f,
            };
            var originalSize = island.Size;
            var originalRotation = island.Rotation;
            var region = new RectInt(512, 751, 512, 83);
            Assert.That(
                AutoAtlas2DMergeIslandRelocator.TryTranslateIslandsWithinPlannedRegion(
                    new[] { island }, region, 1024, 0.01f), Is.True);
            Assert.That(AutoAtlas2DMergeIslandRelocator.IsWithinPlannedRegion(
                island, region, 1024, 0.01f), Is.True);
            Assert.That(island.Size.X, Is.EqualTo(originalSize.X));
            Assert.That(island.Size.Y, Is.EqualTo(originalSize.Y));
            Assert.That(island.Rotation, Is.EqualTo(originalRotation));
        }

        [Test]
        public void TwoDimensionalPackingRejectsExcessiveHeightWithoutMutation()
        {
            var island = new net.rs64.TexTransCore.UVIsland.IslandTransform
            {
                Position = new System.Numerics.Vector2(0.52f, 0.735f),
                Size = new System.Numerics.Vector2(0.1f, 0.076f),
            };
            var originalPosition = island.Position;
            Assert.That(
                AutoAtlas2DMergeIslandRelocator.TryTranslateIslandsWithinPlannedRegion(
                    new[] { island }, new RectInt(512, 751, 512, 83),
                    1024, 0.01f), Is.False);
            Assert.That(island.Position, Is.EqualTo(originalPosition));
        }

        [Test]
        public void TwoDimensionalSelectionCarriesPlannedRegions()
        {
            var baseline = Plan(new[]
            {
                Entry(64, 64, 1),
                Entry(64, 64, 1),
            });
            var merged = Plan(new[]
            {
                Entry(64, 64, 1),
            });
            var candidate = Candidate(baseline, merged, new[] { 3, 6 });
            var regions = new Dictionary<int, RectInt>
            {
                [3] = new RectInt(0, 0, 256, 128),
                [6] = new RectInt(256, 0, 256, 128),
            };
            var selection = new AutoAtlasTextureMergePlanner.MergeSelection(
                candidate, regions);

            Assert.That(selection.PackedRegions, Is.Not.Null);
            Assert.That(selection.GroupIds, Is.EqualTo(new[] { 3, 6 }));
            Assert.That(selection.PackedRegions[6].x, Is.EqualTo(256));
            Assert.That(selection.PredictedSavingBytes, Is.GreaterThan(0));
        }

        [Test]
        public void ProductionSelectionUsesTwoDimensionalCandidatesOnly()
        {
            var baseline = Plan(new[]
            {
                Entry(128, 128, 1),
                Entry(128, 128, 1),
            });
            var merged = Plan(new[]
            {
                Entry(128, 128, 1),
            });
            var candidate = Candidate(baseline, merged, new[] { 1, 2 });
            var band = new AutoAtlasTextureMergePlanner.MergeSelection(candidate);
            var regions = new Dictionary<int, RectInt>
            {
                [1] = new RectInt(0, 0, 256, 128),
                [2] = new RectInt(256, 0, 256, 128),
            };
            var twoD = new AutoAtlasTextureMergePlanner.MergeSelection(
                candidate, regions);

            var selected = AutoAtlasTextureMergePlanner
                .SelectTwoDimensionalMerges(new[] { band, twoD });

            Assert.That(selected.Count, Is.EqualTo(1));
            Assert.That(selected[0], Is.SameAs(twoD));
        }

        [Test]
        public void ExactSelectionCombinesDisjointCandidatesWhenGreedyMissesSaving()
        {
            // A wins individually, but B+C are disjoint and save more.
            var a = TwoGroupSelection(0, 1, 16);
            var b = TwoGroupSelection(0, 2, 24);
            var c = TwoGroupSelection(1, 3, 24);
            Assert.That(a.PredictedSavingBytes, Is.GreaterThan(b.PredictedSavingBytes));
            Assert.That(a.PredictedSavingBytes, Is.LessThan(
                b.PredictedSavingBytes + c.PredictedSavingBytes));

            var selected = AutoAtlasTextureMergePlanner
                .SelectTwoDimensionalMerges(new[] { a, b, c });

            Assert.That(selected, Is.EquivalentTo(new[] { b, c }));
            Assert.That(selected[0], Is.SameAs(b));
            Assert.That(selected[1], Is.SameAs(c));

        }

        [Test]
        public void ExactSelectionPreservesGreedyOnEqualTotalSaving()
        {
            var a = TwoGroupSelection(0, 1, 16);
            var b = TwoGroupSelection(0, 2, 16);
            var independent = TwoGroupSelection(3, 4, 16);

            var selected = AutoAtlasTextureMergePlanner
                .SelectTwoDimensionalMerges(new[] { a, b, independent });

            Assert.That(selected, Is.EqualTo(new[] { a, independent }));

        }

        [Test]
        public void LargeCandidateSetUsesExactGroupMaskWhenGroupUniverseIsBounded()
        {
            var candidates = new List<AutoAtlasTextureMergePlanner.MergeSelection>();
            for (var id = 1; id <= 17; id++)
                candidates.Add(TwoGroupSelection(
                    0, id, id % 2 == 0 ? 16 : 24));

            var selected = AutoAtlasTextureMergePlanner
                .SelectTwoDimensionalMerges(candidates);

            Assert.That(selected.Count, Is.EqualTo(1));
            Assert.That(selected[0], Is.SameAs(candidates[1]));
        }

        [Test]
        public void ExactGroupMaskImprovesGreedyWithMoreThanSixteenCandidates()
        {
            var a = TwoGroupSelection(0, 1, 16);
            var b = TwoGroupSelection(0, 2, 24);
            var c = TwoGroupSelection(1, 3, 24);
            var candidates =
                new List<AutoAtlasTextureMergePlanner.MergeSelection>
                {
                    a, b, c,
                };
            for (var id = 4; id <= 17; id++)
                candidates.Add(TwoGroupSelection(0, id, 32));

            Assert.That(candidates.Count, Is.EqualTo(17));

            var selected = AutoAtlasTextureMergePlanner
                .SelectTwoDimensionalMerges(candidates);

            Assert.That(selected, Is.EqualTo(new[] { b, c }));
        }

        [Test]
        public void LargeGroupUniverseKeepsBoundedGreedySelection()
        {
            var candidates = new List<AutoAtlasTextureMergePlanner.MergeSelection>();
            for (var id = 1; id <= 21; id++)
                candidates.Add(TwoGroupSelection(
                    0, id, id % 2 == 0 ? 16 : 24));

            var selected = AutoAtlasTextureMergePlanner
                .SelectTwoDimensionalMerges(candidates);

            Assert.That(selected.Count, Is.EqualTo(1));
            Assert.That(selected[0], Is.SameAs(candidates[1]));
        }

        private static AutoAtlasTextureMergePlanner.MergeSelection
            TwoGroupSelection(int left, int right, int mergedWidth)
        {
            var baseline = Plan(new[]
            {
                Entry(32, 32, 1),
                Entry(32, 32, 1),
            });
            var merged = Plan(new[]
            {
                Entry(mergedWidth, mergedWidth, 1),
            });
            var candidate = Candidate(baseline, merged, new[] { left, right });
            Assert.That(candidate.IsAdoptable, Is.True);
            return new AutoAtlasTextureMergePlanner.MergeSelection(
                candidate,
                new Dictionary<int, RectInt>
                {
                    [left] = new RectInt(0, 0, 32, 32),
                    [right] = new RectInt(32, 0, 32, 32),
                });
        }

        [Test]
        public void ExecutionQueuePreservesDisjointMergedAndStandaloneGroups()
        {
            var textures = new[]
            {
                new Texture2D(2, 2),
                new Texture2D(2, 2),
                new Texture2D(2, 2),
                new Texture2D(2, 2),
                new Texture2D(2, 2),
            };
            try
            {
                var groups = new AutoAtlasTextureMergePlanner.PreparedGroup[textures.Length];
                for (var id = 0; id < groups.Length; id++)
                {
                    groups[id] = new AutoAtlasTextureMergePlanner.PreparedGroup
                    {
                        Id = id,
                        SourceTextures = new HashSet<Texture> { textures[id] },
                    };
                }

                var first = TwoGroupSelection(0, 1, 16);
                var second = TwoGroupSelection(3, 4, 16);
                var queue = AutoAtlasTextureProcessor.CreateExecutionGroups(
                    groups, new[] { first, second });

                Assert.That(queue.Count, Is.EqualTo(3));
                var mergedFirst = queue.Dequeue();
                Assert.That(mergedFirst.MergeSelection, Is.SameAs(first));
                Assert.That(mergedFirst.SourceTextures, Is.EquivalentTo(
                    new Texture[] { textures[0], textures[1] }));
                var mergedSecond = queue.Dequeue();
                Assert.That(mergedSecond.MergeSelection, Is.SameAs(second));
                Assert.That(mergedSecond.SourceTextures, Is.EquivalentTo(
                    new Texture[] { textures[3], textures[4] }));
                var standalone = queue.Dequeue();
                Assert.That(standalone.MergeSelection, Is.Null);
                Assert.That(standalone.SourceTextures,
                    Is.SameAs(groups[2].SourceTextures));
                Assert.That(queue.Count, Is.Zero);
            }
            finally
            {
                foreach (var texture in textures)
                    UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        [Test]
        public void FailedMergeQueuesOriginalAtomicGroupsWithoutTouchingOtherWork()
        {
            var textures = new[]
            {
                new Texture2D(2, 2),
                new Texture2D(2, 2),
                new Texture2D(2, 2),
                new Texture2D(2, 2),
                new Texture2D(2, 2),
            };
            try
            {
                var groups = new AutoAtlasTextureMergePlanner.PreparedGroup[textures.Length];
                for (var id = 0; id < groups.Length; id++)
                {
                    groups[id] = new AutoAtlasTextureMergePlanner.PreparedGroup
                    {
                        Id = id,
                        SourceTextures = new HashSet<Texture> { textures[id] },
                    };
                }

                var failed = TwoGroupSelection(0, 1, 16);
                var unaffected = TwoGroupSelection(3, 4, 16);
                var queue = AutoAtlasTextureProcessor.CreateExecutionGroups(
                    groups, new[] { failed, unaffected });

                Assert.That(queue.Dequeue().MergeSelection, Is.SameAs(failed));
                AutoAtlasTextureProcessor.EnqueueStandaloneFallback(
                    queue, failed, groups);

                // Other selected merges and originally standalone groups stay
                // ahead of the failed merge's individual retries.
                Assert.That(queue.Count, Is.EqualTo(4));
                Assert.That(queue.Dequeue().MergeSelection, Is.SameAs(unaffected));
                var originalStandalone = queue.Dequeue();
                Assert.That(originalStandalone.MergeSelection, Is.Null);
                Assert.That(originalStandalone.SourceTextures,
                    Is.SameAs(groups[2].SourceTextures));
                var retry0 = queue.Dequeue();
                var retry1 = queue.Dequeue();
                Assert.That(retry0.MergeSelection, Is.Null);
                Assert.That(retry1.MergeSelection, Is.Null);
                Assert.That(retry0.SourceTextures, Is.SameAs(groups[0].SourceTextures));
                Assert.That(retry1.SourceTextures, Is.SameAs(groups[1].SourceTextures));
                Assert.That(queue.Count, Is.Zero);
            }
            finally
            {
                foreach (var texture in textures)
                    UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        [Test]
        public void VisibilityAnimationDetectorFindsParentActiveToggleAndRendererToggle()
        {
            var root = new GameObject("Avatar");
            var outfit = new GameObject("Outfit");
            var underOutfit = new GameObject("Part");
            var accessory = new GameObject("Accessory");
            var staticItem = new GameObject("Static");
            try
            {
                outfit.transform.SetParent(root.transform);
                underOutfit.transform.SetParent(outfit.transform);
                accessory.transform.SetParent(root.transform);
                staticItem.transform.SetParent(root.transform);

                var outfitRenderer = underOutfit.AddComponent<MeshRenderer>();
                var accessoryRenderer = accessory.AddComponent<MeshRenderer>();
                var staticRenderer = staticItem.AddComponent<MeshRenderer>();

                var clip = nadena.dev.ndmf.animator.VirtualClip.Create("toggles");
                clip.SetFloatCurve(
                    UnityEditor.EditorCurveBinding.FloatCurve(
                        "Outfit", typeof(GameObject), "m_IsActive"),
                    AnimationCurve.Constant(0f, 1f, 0f));
                clip.SetFloatCurve(
                    UnityEditor.EditorCurveBinding.FloatCurve(
                        "Accessory", typeof(MeshRenderer), "m_Enabled"),
                    AnimationCurve.Constant(0f, 1f, 0f));

                var index = new nadena.dev.ndmf.animator.AnimationIndex(
                    new nadena.dev.ndmf.animator.VirtualNode[] { clip });
                var paths = new Dictionary<Transform, string>
                {
                    [root.transform] = "",
                    [outfit.transform] = "Outfit",
                    [underOutfit.transform] = "Outfit/Part",
                    [accessory.transform] = "Accessory",
                    [staticItem.transform] = "Static",
                };

                var detected = AutoAtlasTextureVisibilityAnimationDetector.Detect(
                    index, root.transform,
                    new Renderer[] { outfitRenderer, accessoryRenderer, staticRenderer },
                    transform => new[] { paths[transform] });

                Assert.That(detected, Is.EquivalentTo(
                    new Renderer[] { outfitRenderer, accessoryRenderer }));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void VisibilityAnimationDetectorChecksAllMappedAliases()
        {
            var root = new GameObject("Avatar");
            var clothing = new GameObject("NewClothing");
            var clothingPart = new GameObject("Part");
            var accessory = new GameObject("NewAccessory");
            var untouched = new GameObject("Untouched");
            try
            {
                clothing.transform.SetParent(root.transform);
                clothingPart.transform.SetParent(clothing.transform);
                accessory.transform.SetParent(root.transform);
                untouched.transform.SetParent(root.transform);

                var clothingRenderer = clothingPart.AddComponent<MeshRenderer>();
                var accessoryRenderer = accessory.AddComponent<MeshRenderer>();
                var untouchedRenderer = untouched.AddComponent<MeshRenderer>();

                // The old virtual aliases remain in the AnimationIndex after
                // NDMF replaces objects whose current paths differ.
                var clip = nadena.dev.ndmf.animator.VirtualClip.Create("old-aliases");
                clip.SetFloatCurve(
                    UnityEditor.EditorCurveBinding.FloatCurve(
                        "OldClothing", typeof(GameObject), "m_IsActive"),
                    AnimationCurve.Constant(0f, 1f, 0f));
                clip.SetFloatCurve(
                    UnityEditor.EditorCurveBinding.FloatCurve(
                        "OldAccessory", typeof(MeshRenderer), "m_Enabled"),
                    AnimationCurve.Constant(0f, 1f, 0f));
                var index = new nadena.dev.ndmf.animator.AnimationIndex(
                    new nadena.dev.ndmf.animator.VirtualNode[] { clip });
                var paths = new Dictionary<Transform, string[]>
                {
                    [root.transform] = new[] { "" },
                    [clothing.transform] = new[] { "NewClothing", "OldClothing" },
                    [clothingPart.transform] = new[] { "NewClothing/Part" },
                    [accessory.transform] = new[] { "NewAccessory", "OldAccessory" },
                    [untouched.transform] = new[] { "Untouched" },
                };

                var renderers = new Renderer[]
                {
                    clothingRenderer, accessoryRenderer, untouchedRenderer,
                };
                var firstPathsOnly = AutoAtlasTextureVisibilityAnimationDetector
                    .Detect(index, root.transform, renderers,
                        transform => paths[transform].Take(1));
                Assert.That(firstPathsOnly, Is.Empty);

                var withAliases = AutoAtlasTextureVisibilityAnimationDetector
                    .Detect(index, root.transform, renderers,
                        transform => paths[transform]);
                Assert.That(withAliases, Is.EquivalentTo(
                    new Renderer[] { clothingRenderer, accessoryRenderer }));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void VisibilityAnimationDetectorIgnoresConstantCurrentState()
        {
            var clip = nadena.dev.ndmf.animator.VirtualClip.Create("always-on");
            clip.SetFloatCurve(
                UnityEditor.EditorCurveBinding.FloatCurve(
                    "Accessory", typeof(MeshRenderer), "m_Enabled"),
                AnimationCurve.Constant(0f, 1f, 1f));

            var root = new GameObject("Avatar");
            var child = new GameObject("Accessory");
            try
            {
                child.transform.SetParent(root.transform);
                var renderer = child.AddComponent<MeshRenderer>();
                var index = new nadena.dev.ndmf.animator.AnimationIndex(
                    new nadena.dev.ndmf.animator.VirtualNode[] { clip });

                var detected = AutoAtlasTextureVisibilityAnimationDetector.Detect(
                    index, root.transform,
                    new Renderer[] { renderer },
                    transform => transform == root.transform
                        ? new[] { "" }
                        : new[] { "Accessory" });

                Assert.That(detected, Is.Empty);
                renderer.enabled = false;
                detected = AutoAtlasTextureVisibilityAnimationDetector.Detect(
                    index, root.transform,
                    new Renderer[] { renderer },
                    transform => transform == root.transform
                        ? new[] { "" }
                        : new[] { "Accessory" });
                Assert.That(detected, Is.EquivalentTo(new Renderer[] { renderer }));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void VisibilityAnimationDetectorCurveThreshold()
        {
            Assert.That(
                AutoAtlasTextureVisibilityAnimationDetector.CurveChangesVisibility(
                    AnimationCurve.Constant(0f, 1f, 1f), true), Is.False);
            Assert.That(
                AutoAtlasTextureVisibilityAnimationDetector.CurveChangesVisibility(
                    AnimationCurve.Constant(0f, 1f, 0f), true), Is.True);
            Assert.That(
                AutoAtlasTextureVisibilityAnimationDetector.CurveChangesVisibility(
                    AnimationCurve.Constant(0f, 1f, 0f), false), Is.False);
        }

        [Test]
        public void GroupLayoutCacheKeyMatchesDistinctEquivalentArrays()
        {
            var key = new AutoAtlasTextureMergePlanner.GroupLayoutKey(
                new[] { 0, 1, 8 },
                new Vector2Int(512, 256));
            var same = new AutoAtlasTextureMergePlanner.GroupLayoutKey(
                new[] { 0, 1, 8 },
                new Vector2Int(512, 256));
            var differentGroup = new AutoAtlasTextureMergePlanner.GroupLayoutKey(
                new[] { 0, 2, 8 },
                new Vector2Int(512, 256));
            var differentWidth = new AutoAtlasTextureMergePlanner.GroupLayoutKey(
                new[] { 0, 1, 8 },
                new Vector2Int(1024, 256));
            var differentHeight = new AutoAtlasTextureMergePlanner.GroupLayoutKey(
                new[] { 0, 1, 8 },
                new Vector2Int(512, 512));

            Assert.That(key.Equals(same), Is.True);
            Assert.That(key.GetHashCode(), Is.EqualTo(same.GetHashCode()));
            Assert.That(key.Equals(differentGroup), Is.False);
            Assert.That(key.Equals(differentWidth), Is.False);
            Assert.That(key.Equals(differentHeight), Is.False);

            var cache = new Dictionary<
                AutoAtlasTextureMergePlanner.GroupLayoutKey,
                int> { [key] = 42 };
            Assert.That(cache.TryGetValue(same, out var value), Is.True);
            Assert.That(value, Is.EqualTo(42));
        }

        private static AutoAtlasTextureMergePlanner.Candidate Candidate(
            AutoAtlasTextureMergePlanner.TextureCostPlan baseline,
            AutoAtlasTextureMergePlanner.TextureCostPlan merged,
            int[] groupIds = null)
        {
            return new AutoAtlasTextureMergePlanner.Candidate(
                groupIds ?? new[] { 0, 1 },
                baseline,
                merged,
                new Vector2Int(64, 64),
                new Dictionary<string, Vector2Int>());
        }

        private static AutoAtlasTextureMergePlanner.TextureCostPlan Plan(
            IEnumerable<AutoAtlasTextureMergePlanner.TextureCostEntry> entries,
            IReadOnlyDictionary<int, double> sampling = null)
        {
            return new AutoAtlasTextureMergePlanner.TextureCostPlan(
                entries,
                sampling);
        }

        private static AutoAtlasTextureMergePlanner.TextureCostEntry Entry(
            int width,
            int height,
            int mipCount,
            int blockSize = 4)
        {
            return new AutoAtlasTextureMergePlanner.TextureCostEntry(
                width,
                height,
                (1u, 1u, (uint)blockSize),
                mipCount);
        }
    }
}
