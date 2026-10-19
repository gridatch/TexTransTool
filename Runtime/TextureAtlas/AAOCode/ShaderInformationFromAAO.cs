#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace net.rs64.TexTransTool.TextureAtlas.AAOCode
{
    internal static class ShaderInformationFromAAO
    {
        internal const int FullSemanticSupportedLiltoonVersion = 45;

        // Exact official shader assets audited for this semantic model.
        // New lilToon shader assets fail closed for AutoAtlas until reviewed,
        // while manual AtlasTexture can still use the legacy UV metadata.
        private static readonly HashSet<string>
            s_fullSemanticLiltoonShaderGuids =
                new(StringComparer.Ordinal)
                {
                    "544c75f56e9af8048b29a6ace5f52091",
                    "7ec9f85eb7ee04943adfe19c2ba5901f",
                    "00795bf598b44dc4e9bd363348e77085",
                    "3eef4aee6ba0de047b0d40409ea2891c",
                    "61b4f98a5d78b4a4a9d89180fac793fc",
                    "df12117ecd77c31469c224178886498e",
                    "dce3f3e248acc7b4daeda00daf616b4d",
                    "14006db8206fb304aa86110d57626d40",
                    "bdf24b2e925ce8a4fb0e903889a52e0e",
                    "fba17785d6b2c594ab6c0303c834da65",
                    "62df797f407281640a224388953448cc",
                    "afa1a194f5a2fd243bda3a17bca1b36e",
                    "b269573b9937b8340b3e9e191a3ba5a8",
                    "165365ab7100a044ca85fc8c33548a62",
                    "9294844b15dca184d914a632279b24e1",
                    "59b5e58e88aae8a4ca42d1a7253e2fb2",
                    "583a88005abb81a4ebbce757b4851a0d",
                    "82226adb1a0b8c4418f574cfdcf523da",
                    "7171688840c632447b22ec14e2bdef7e",
                    "c6d605ee23b18fc46903f38c67db701f",
                    "33aad051c4a3a844a8f9330addb86a97",
                    "0e3ece1bd59542743bccadb21f68318e",
                    "381af8ba8e1740a41b9768ccfb0416c2",
                    "d7af54cdd86902d41b8c240e06b93009",
                    "8cf5267d397b04846856f6d3d9561da0",
                    "90f83c35b0769a748abba5d0880f36d5",
                    "3c79b10c7e0b2784aaa4c2f8dd17d55e",
                    "85d6126cae43b6847aff4b13f4adb8ec",
                    "3b3957e6c393b114bab6f835b4ed8f5d",
                    "d28e4b78ba8368e49a44f86c0291df58",
                    "2683fad669f20ec49b8e9656954a33a8",
                    "7e61dbad981ad4f43a03722155db1c6a",
                    "67ed0252d63362a4ab23707a720508b7",
                    "7e398ea50f9b70045b1774e05b46a39f",
                    "7a7ac427f85673a45a3e4190fc10bc28",
                    "9b0c2630b12933248922527d4507cfa9",
                    "33e950d038b8dfd4f824f3985c2abfb7",
                    "efa77a80ca0344749b4f19fdd5891cbe",
                    "8a6ef0489c3ffbf46812460af3d52bb0",
                    "f8d9dfac6dbfaaf4c9c3aaf4bd8c955f",
                    "3fb94a39b2685ee4d9817dcaf6542d99",
                    "69f861c14129e724096c0955f8079012",
                    "3b4aa19949601f046a20ca8bdaee929f",
                    "0c762f24b85918a49812fc5690619178",
                    "ad219df2a46e841488aee6a013e84e36",
                    "55706696b2bdb5d4d8541b89e17085c8",
                    "94274b8ef5d3af842b9427384cba3a8f",
                    "fd68f52288a6b0243bf6c217bf0930ea",
                    "9cf054060007d784394b8b0bb703e441",
                    "6a77405f7dfdc1447af58854c7f43f39",
                    "bbfffd5515b843c41a85067191cbf687",
                    "dc9ded9f9d6f16c4e92cbb8f4269ae31",
                    "2bde4bd29a2a70a4d9cf98772a6717ac",
                    "34c2907eba944ed45a43970e0c11bcfd",
                    "a8d94439709469942bc7dcc9156ba110",
                    "701268c07d37f5441b25b2cb99fae4b3",
                    "51b2dee0ab07bd84d8147601ff89e511",
                    "1c12a37046f07ac4486881deaf0187ea",
                    "b957dce3d03ff5445ac989f8de643c7f",
                    "5ba517885727277409feada18effa4a6",
                    "8773c83ab40fff24b800f74360819a6c",
                    "54bc8b41278802d4a81b27fe402994e2",
                    "1e50f1bc4d1b0e34cbf16b82589f6407",
                    "f96a89829ccb1e54b85214550519a8d6",
                };

        private sealed class LegacyOnlyShaderInformation
            : ITTShaderTextureUsageInformation
        {
            private readonly ITTShaderTextureUsageInformation _inner;

            internal LegacyOnlyShaderInformation(
                ITTShaderTextureUsageInformation inner)
            {
                _inner = inner;
            }

            public void GetMaterialTextureUVUsage(
                ITTTextureUVUsageWriter writer)
            {
                _inner.GetMaterialTextureUVUsage(writer);
            }
        }

        [TexTransCoreEngineForUnity.TexTransInitialize]
        internal static void ShaderInformationRegistering()
        {
            var liltoonVersion = GetInstalledLiltoonVersion();

            foreach (var shader in TexTransCoreEngineForUnity.TexTransCoreRuntime.LoadAssetsAtType(typeof(Shader)).Cast<Shader>())
            {
                if (shader.name.Contains("lilToon"))
                {
                    var usesFurVertexId = shader.name.IndexOf(
                        "Fur",
                        StringComparison.OrdinalIgnoreCase) >= 0;
                    ITTShaderTextureUsageInformation information =
                        shader.name.Contains("Outline")
                            ? new LiltoonShaderInformationWithOutline(
                                usesFurVertexId)
                            : new LiltoonShaderInformationWithOutOutline(
                                usesFurVertexId);

                    if (!HasVerifiedFullLiltoonSemantics(
                            shader,
                            liltoonVersion))
                    {
                        information =
                            new LegacyOnlyShaderInformation(information);
                    }

                    TTShaderTextureUsageInformationRegistry
                        .RegisterTTShaderTextureUsageInformation(
                            shader,
                            information);
                }
            }

            StandardShaderInformation.Register();
            VRCSDKStandardLiteShaderInformation.Register();
            VRCSDKToonStandardShaderInformation.Register();
        }

        internal static bool IsSupportedLiltoonVersion(int? version)
        {
            return version.HasValue
                && version.Value <= FullSemanticSupportedLiltoonVersion;
        }

        internal static bool IsKnownFullSemanticLiltoonShaderGuid(
            string? guid)
        {
            return !string.IsNullOrEmpty(guid)
                && s_fullSemanticLiltoonShaderGuids.Contains(guid);
        }

        private static bool HasVerifiedFullLiltoonSemantics(
            Shader shader,
            int? liltoonVersion)
        {
#if UNITY_EDITOR
            if (!IsSupportedLiltoonVersion(liltoonVersion))
                return false;

            var path = AssetDatabase.GetAssetPath(shader);
            if (string.IsNullOrEmpty(path))
                return false;
            return IsKnownFullSemanticLiltoonShaderGuid(
                AssetDatabase.AssetPathToGUID(path));
#else
            return false;
#endif
        }

        private static int? GetInstalledLiltoonVersion()
        {
            var constantsType = Type.GetType(
                "lilToon.lilConstants, lilToon.Editor");
            var versionField = constantsType?.GetField(
                "currentVersionValue",
                BindingFlags.Public | BindingFlags.Static);
            return versionField?.GetValue(null) is int version
                ? version
                : null;
        }
    }
}
