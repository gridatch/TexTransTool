Shader "Hidden/TexTransTool/AtlasSourceAnalysis"
{
    Properties
    {
        [HideInInspector] _Source ("Source", 2D) = "white" {}
        [HideInInspector] _MainTex ("Reduction Source", 2D) = "black" {}
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Cull Off
        ZWrite Off
        ZTest Always
        Blend Off

        // Pass 0: rasterize the expanded UV support while evaluating the
        // canonical source texture. RGBA stores:
        // R = differs from reference texel
        // G = any non-zero RGB
        // B = any non-zero alpha
        // A = covered
        Pass
        {
            CGPROGRAM
            #pragma target 4.0
            #pragma vertex vert
            #pragma fragment frag

            #include "UnityCG.cginc"

            sampler2D _Source;
            float4 _Source_TexelSize;
            float4 _SourceSize;
            float4 _ReferenceCoord;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 logical : TEXCOORD0;
            };

            struct v2f
            {
                float4 position : SV_POSITION;
                float2 logical : TEXCOORD0;
            };

            v2f vert(appdata input)
            {
                v2f output;
                output.position = float4(input.vertex.xy, 0.0, 1.0);
                output.logical = input.logical;
                return output;
            }

            float4 SampleSourceTexel(int2 coord)
            {
                float2 uv = (float2(coord) + 0.5) / _SourceSize.xy;
                #if UNITY_UV_STARTS_AT_TOP
                if (_Source_TexelSize.y < 0.0)
                    uv.y = 1.0 - uv.y;
                #endif
                return tex2Dlod(_Source, float4(uv, 0.0, 0.0));
            }

            void AccumulateSample(
                float4 sampleValue,
                float4 referenceValue,
                inout float differs,
                inout float rgbNonZero,
                inout float alphaNonZero)
            {
                if (any(sampleValue != referenceValue))
                    differs = 1.0;
                if (any(sampleValue.rgb != 0.0))
                    rgbNonZero = 1.0;
                if (sampleValue.a != 0.0)
                    alphaNonZero = 1.0;
            }

            float4 frag(v2f input) : SV_Target
            {
                int2 size = int2(_SourceSize.xy);
                // The varying is affine with the clip-space geometry, so the
                // value at each pixel center is its logical source texel
                // coordinate regardless of the graphics API's viewport Y
                // orientation.
                int2 logical =
                    int2(floor(input.logical + 0.5));

                int2 clamped = clamp(logical, int2(0, 0), size - 1);
                int2 repeated = int2(
                    ((logical.x % size.x) + size.x) % size.x,
                    ((logical.y % size.y) + size.y) % size.y);

                float4 referenceValue = SampleSourceTexel(
                    int2(_ReferenceCoord.xy));
                float differs = 0.0;
                float rgbNonZero = 0.0;
                float alphaNonZero = 0.0;

                AccumulateSample(
                    SampleSourceTexel(clamped),
                    referenceValue,
                    differs,
                    rgbNonZero,
                    alphaNonZero);

                if (repeated.x != clamped.x)
                    AccumulateSample(
                        SampleSourceTexel(int2(repeated.x, clamped.y)),
                        referenceValue,
                        differs,
                        rgbNonZero,
                        alphaNonZero);

                if (repeated.y != clamped.y)
                    AccumulateSample(
                        SampleSourceTexel(int2(clamped.x, repeated.y)),
                        referenceValue,
                        differs,
                        rgbNonZero,
                        alphaNonZero);

                if (repeated.x != clamped.x
                    && repeated.y != clamped.y)
                    AccumulateSample(
                        SampleSourceTexel(repeated),
                        referenceValue,
                        differs,
                        rgbNonZero,
                        alphaNonZero);

                return float4(
                    differs,
                    rgbNonZero,
                    alphaNonZero,
                    1.0);
            }
            ENDCG
        }

        // Pass 1: 4x4 max reduction. Orientation is irrelevant because max is
        // commutative and every source texel is visited exactly once.
        Pass
        {
            CGPROGRAM
            #pragma target 4.0
            #pragma vertex vert_img
            #pragma fragment fragReduce

            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_TexelSize;

            float4 fragReduce(v2f_img input) : SV_Target
            {
                int2 sourceSize = int2(_MainTex_TexelSize.zw);
                int2 destinationPixel = int2(floor(input.pos.xy));
                int2 sourceOrigin = destinationPixel * 4;
                float4 result = 0.0;

                [unroll]
                for (int y = 0; y < 4; y++)
                {
                    [unroll]
                    for (int x = 0; x < 4; x++)
                    {
                        int2 coord = sourceOrigin + int2(x, y);
                        if (coord.x >= sourceSize.x
                            || coord.y >= sourceSize.y)
                            continue;

                        float2 uv =
                            (float2(coord) + 0.5) / sourceSize;
                        result = max(
                            result,
                            tex2Dlod(
                                _MainTex,
                                float4(uv, 0.0, 0.0)));
                    }
                }

                return result;
            }
            ENDCG
        }
    }

    Fallback Off
}
