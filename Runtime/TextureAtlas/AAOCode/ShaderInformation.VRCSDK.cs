// https://github.com/anatawa12/AvatarOptimizer/blob/6a63910a423d5b7d73e726fcccb4940716f5ee0d/Editor/APIInternal/ShaderInformation.VRCSDK.cs

using net.rs64.TexTransCoreEngineForUnity;
using UnityEngine;


namespace net.rs64.TexTransTool.TextureAtlas.AAOCode
{
    // VRChat SDK Mobile Shaders
    internal class VRCSDKStandardLiteShaderInformation : ITTShaderTextureUsageInformation
    {

        internal static void Register()
        {
            var information = new VRCSDKStandardLiteShaderInformation();
            var shader = TexTransCoreRuntime.LoadAsset("0b7113dea2069fc4e8943843eff19f70", typeof(Shader)) as Shader;
            if (shader == null) { return; }
            TTShaderTextureUsageInformationRegistry.RegisterTTShaderTextureUsageInformation(shader, information);
        }
        public void GetMaterialTextureUVUsage(ITTTextureUVUsageWriter writer)
        {
            GetMaterialInformation(new MaterialInformationTranslator(writer));
        }
        public void GetMaterialInformation(IMaterialInformationCallbackAbstractionInterface matInfo)
        {
            var mainTexST = matInfo.GetVector("_MainTex_ST");
            Matrix2x3? mainTexSTMat = mainTexST is { } st ? Matrix2x3.NewScaleOffset(st) : null;

            matInfo.RegisterTextureUVUsage("_MetallicGlossMap", "_MetallicGlossMap", UsingUVChannels.UV0, mainTexSTMat);
            matInfo.RegisterTextureUVUsage("_MainTex", "_MainTex", UsingUVChannels.UV0, mainTexSTMat);
            matInfo.RegisterTextureUVUsage("_BumpMap", "_BumpMap", UsingUVChannels.UV0, mainTexSTMat);
            matInfo.RegisterTextureUVUsage("_OcclusionMap", "_OcclusionMap", UsingUVChannels.UV0, mainTexSTMat);
            matInfo.RegisterTextureUVUsage("_EmissionMap", "_EmissionMap", UsingUVChannels.UV0, mainTexSTMat);
            matInfo.RegisterTextureUVUsage("_DetailMask", "_DetailMask", UsingUVChannels.UV0, mainTexSTMat);

            var detailMapST = matInfo.GetVector("_DetailAlbedoMap_ST");
            Matrix2x3? detailMapSTMat = detailMapST is { } st2 ? Matrix2x3.NewScaleOffset(st2) : null;
            matInfo.RegisterTextureUVUsage("_DetailAlbedoMap", "_DetailAlbedoMap", UsingUVChannels.UV0, mainTexSTMat);

            var detailMapUV = matInfo.GetFloat("_UVSec") switch
            {
                null => UsingUVChannels.UV0 | UsingUVChannels.UV1,
                0 => UsingUVChannels.UV0,
                _ => UsingUVChannels.UV1,
            };

            matInfo.RegisterTextureUVUsage("_DetailAlbedoMap", "_DetailAlbedoMap", detailMapUV, detailMapSTMat);
            matInfo.RegisterTextureUVUsage("_DetailNormalMap", "_DetailNormalMap", detailMapUV, detailMapSTMat);
        }
    }

    internal class VRCSDKToonLitShaderInformation : ITTShaderTextureUsageInformation
    {
        internal static void Register()
        {
            var information = new VRCSDKToonLitShaderInformation();
            var shader = TexTransCoreRuntime.LoadAsset("affc81f3d164d734d8f13053effb1c5c", typeof(Shader)) as Shader;
            if (shader == null) { return; }
            TTShaderTextureUsageInformationRegistry.RegisterTTShaderTextureUsageInformation(shader, information);
        }
        public void GetMaterialTextureUVUsage(ITTTextureUVUsageWriter writer)
        {
            GetMaterialInformation(new MaterialInformationTranslator(writer));
        }
        public void GetMaterialInformation(IMaterialInformationCallbackAbstractionInterface matInfo)
        {
            var mainTexST = matInfo.GetVector("_MainTex_ST");
            Matrix2x3? mainTexSTMat = mainTexST is { } st ? Matrix2x3.NewScaleOffset(st) : null;
            matInfo.RegisterTextureUVUsage("_MainTex", "_MainTex", UsingUVChannels.UV0, mainTexSTMat);
        }
    }
    internal class VRCSDKToonStandardShaderInformation : ITTShaderTextureUsageInformation
    {
        private readonly bool _withOutline;

        public VRCSDKToonStandardShaderInformation(bool withOutline)
        {
            _withOutline = withOutline;
        }

        internal static void Register()
        {
            Register("e765db0afa7ecfc44ade2e4e2491f65a", false);
            Register("051a0ed2f2aedd741aa8186ae92f97e0", true);

            static void Register(string guid, bool withOutline)
            {
                var shader = TexTransCoreRuntime.LoadAsset(guid, typeof(Shader)) as Shader;
                if (shader == null) { return; }
                TTShaderTextureUsageInformationRegistry.RegisterTTShaderTextureUsageInformation(
                    shader,
                    new VRCSDKToonStandardShaderInformation(withOutline)
                );
            }
        }

        public void GetMaterialTextureUVUsage(ITTTextureUVUsageWriter writer)
        {
            GetMaterialInformation(new MaterialInformationTranslator(writer));
        }

        public void GetMaterialInformation(IMaterialInformationCallbackAbstractionInterface matInfo)
        {
            void Register(string name, UsingUVChannels uv)
            {
                var st = matInfo.GetVector(name + "_ST");
                Matrix2x3? stMat = st is { } st2 ? Matrix2x3.NewScaleOffset(st2) : null;
                matInfo.RegisterTextureUVUsage(name, name, uv, stMat);
            }

            Register("_MainTex", UsingUVChannels.UV0);
            Register("_Ramp", UsingUVChannels.NonMesh);

            if (matInfo.IsShaderKeywordEnabled("USE_NORMAL_MAPS") != false)
                Register("_BumpMap", UsingUVChannels.UV0);

            if (matInfo.IsShaderKeywordEnabled("USE_SPECULAR") != false)
            {
                Register("_MetallicMap", UsingUVChannels.UV0);
                Register("_GlossMap", UsingUVChannels.UV0);
            }

            if (matInfo.IsShaderKeywordEnabled("USE_MATCAP") != false)
            {
                Register("_Matcap", UsingUVChannels.NonMesh);
                Register("_MatcapMask", UsingUVChannels.UV0);
            }

            var emissionUV = matInfo.GetFloat("_EmissionUV") switch
            {
                0 => UsingUVChannels.UV0,
                1 => UsingUVChannels.UV1,
                _ => UsingUVChannels.UV0 | UsingUVChannels.UV1,
            };
            Register("_EmissionMap", emissionUV);

            if (matInfo.IsShaderKeywordEnabled("USE_OCCLUSION_MAP") != false)
                Register("_OcclusionMap", UsingUVChannels.UV0);

            if (matInfo.IsShaderKeywordEnabled("USE_DETAIL_MAPS") != false)
            {
                var detailUV = matInfo.GetFloat("_DetailUV") switch
                {
                    0 => UsingUVChannels.UV0,
                    1 => UsingUVChannels.UV1,
                    _ => UsingUVChannels.UV0 | UsingUVChannels.UV1,
                };

                Register("_DetailMask", UsingUVChannels.UV0);
                Register("_DetailAlbedoMap", detailUV);

                if (matInfo.IsShaderKeywordEnabled("USE_NORMAL_MAPS") != false)
                    Register("_DetailNormalMap", detailUV);
            }

            if (matInfo.IsShaderKeywordEnabled("USE_HUE_SHIFT") != false)
                Register("_HueShiftMask", UsingUVChannels.UV0);

            if (matInfo.IsShaderKeywordEnabled("USE_COLOR_MASK") != false)
                Register("_ColorMask", UsingUVChannels.UV0);

            if (matInfo.IsShaderKeywordEnabled("USE_AUDIOLINK") != false)
            {
                var audioLinkUV = SelectUV4(matInfo.GetFloat("_ALMaskUVChannel"))
                    | SelectUV4(matInfo.GetFloat("_ALEffectUVChannel"));
                Register("_AudioLinkMask", audioLinkUV);

                static UsingUVChannels SelectUV4(float? channel) => channel switch
                {
                    0 => UsingUVChannels.UV0,
                    1 => UsingUVChannels.UV1,
                    2 => UsingUVChannels.UV2,
                    3 => UsingUVChannels.UV3,
                    _ => UsingUVChannels.UV0 | UsingUVChannels.UV1 | UsingUVChannels.UV2 | UsingUVChannels.UV3,
                };
            }

            if (_withOutline)
                Register("_OutlineMask", UsingUVChannels.UV0);
        }
    }

}
