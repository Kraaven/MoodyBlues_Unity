// Decodes a Unity lightmap into linear HDR colour for the scene export (see LightmapExporter).
// Blit-only: it never renders scene geometry, so it has no render-pipeline dependency. It lives
// in a Resources folder so a Player build includes it and a runtime export can load it.
Shader "Hidden/MoodyBlues/LightmapDecode"
{
    Properties
    {
        _MainTex ("Lightmap", 2D) = "black" {}
        // 0 = full HDR (BC6H / half / float: the sample already is linear colour),
        // 1 = RGBM, 2 = double-LDR. Unity's own lightmap sampling does the same split.
        _Mode ("Mode", Float) = 0
        // Unity's unity_Lightmap_HDR: x = multiplier, y = exponent (RGBM only).
        _Decode ("Decode instructions", Vector) = (1, 1, 0, 0)
    }

    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float _Mode;
            float4 _Decode;

            float4 frag(v2f_img i) : SV_Target
            {
                float4 c = tex2D(_MainTex, i.uv);
                float3 rgb = c.rgb;
                if (_Mode > 1.5)
                    rgb = c.rgb * _Decode.x;
                else if (_Mode > 0.5)
                    rgb = c.rgb * (_Decode.x * pow(c.a, _Decode.y));
                return float4(rgb, 1);
            }
            ENDCG
        }
    }
}
