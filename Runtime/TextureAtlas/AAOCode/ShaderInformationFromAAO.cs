#nullable enable
using System;
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
        private const string OfficialLiltoonPackagePrefix =
            "Packages/jp.lilxyzw.liltoon/Shader/";
        private const string OfficialLiltoonLegacyPrefix =
            "Assets/lilToon/Shader/";
        private const string OfficialLiltoonLegacyRootGuid =
            "05d1d116436047941ad97d1b9064ee05";

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

        internal static bool IsOfficialLiltoonShaderIdentity(
            string? assetPath,
            string? legacyRootGuid)
        {
            if (string.IsNullOrEmpty(assetPath)
                || !assetPath.EndsWith(
                    ".shader",
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (assetPath.StartsWith(
                    OfficialLiltoonPackagePrefix,
                    StringComparison.Ordinal))
            {
                return true;
            }

            return assetPath.StartsWith(
                       OfficialLiltoonLegacyPrefix,
                       StringComparison.Ordinal)
                && string.Equals(
                    legacyRootGuid,
                    OfficialLiltoonLegacyRootGuid,
                    StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasVerifiedFullLiltoonSemantics(
            Shader shader,
            int? liltoonVersion)
        {
#if UNITY_EDITOR
            if (!IsSupportedLiltoonVersion(liltoonVersion))
                return false;

            var path = AssetDatabase.GetAssetPath(shader);
            var legacyRootGuid =
                path.StartsWith(
                    OfficialLiltoonLegacyPrefix,
                    StringComparison.Ordinal)
                    ? AssetDatabase.AssetPathToGUID("Assets/lilToon")
                    : null;
            return IsOfficialLiltoonShaderIdentity(
                path,
                legacyRootGuid);
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
