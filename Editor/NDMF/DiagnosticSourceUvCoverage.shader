Shader "Hidden/TexTransTool/DiagnosticSourceUvCoverage"
{
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Cull Off
        ZWrite Off
        ZTest Always
        // Red: source UV coverage. Green: known bottom-row orientation marker.
        // Additive blending preserves both when they overlap.
        Blend One One
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            fixed4 _MaskColor;
            struct appdata { float4 vertex : POSITION; };
            struct v2f { float4 position : SV_POSITION; };

            v2f vert(appdata v)
            {
                v2f o;
                // UVs are already converted to clip-space by the editor probe.
                o.position = float4(v.vertex.xy, 0.0, 1.0);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                return _MaskColor;
            }
            ENDCG
        }
    }
    Fallback Off
}
