#nullable enable
using System.Linq;
using UnityEngine;

namespace net.rs64.TexTransTool.TextureAtlas.AAOCode
{
    internal static class ShaderInformationFromAAO
    {
        [TexTransCoreEngineForUnity.TexTransInitialize]
        internal static void ShaderInformationRegistering()
        {
            foreach (var shader in TexTransCoreEngineForUnity.TexTransCoreRuntime.LoadAssetsAtType(typeof(Shader)).Cast<Shader>())
            {
                if (shader.name.Contains("lilToon"))
                {
                    var usesFurVertexId = shader.name.IndexOf(
                        "Fur",
                        System.StringComparison.OrdinalIgnoreCase) >= 0;
                    ITTShaderTextureUsageInformation information =
                        shader.name.Contains("Outline")
                            ? new LiltoonShaderInformationWithOutline(
                                usesFurVertexId)
                            : new LiltoonShaderInformationWithOutOutline(
                                usesFurVertexId);
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
    }
}
