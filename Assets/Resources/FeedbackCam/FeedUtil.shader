// Milestone M5 helper passes for FeedVision / FeedScene (Graphics.Blit only, never drawn on screen).
//  Pass 0 - letterbox: fits the feed into BlazeFace's square input without stretching (black bars).
//  Pass 1 - scene probe: into a tiny target (FeedScene, 32x18), each texel = the average brightness of its patch of the
//           feed (r) and how much of the patch is the player (g, the patch's maximum), so dim, empty background can be
//           found on the CPU from a few hundred bytes.
Shader "Hidden/FYP/FeedUtil"
{
    Properties
    {
        _MainTex ("Source", 2D) = "black" {}
        _Mask ("Person mask", 2D) = "black" {}
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
            float4 _ScaleOffset;   // source uv = uv * xy + zw

            fixed4 frag (v2f_img i) : SV_Target
            {
                float2 s = i.uv * _ScaleOffset.xy + _ScaleOffset.zw;
                if (any(s < 0.0) || any(s > 1.0)) return fixed4(0, 0, 0, 1);
                return fixed4(tex2D(_MainTex, s).rgb, 1);
            }
            ENDCG
        }

        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex, _Mask;
            float4 _Cell;          // xy = size of one output texel in uv

            fixed4 frag (v2f_img i) : SV_Target
            {
                float lum = 0.0, person = 0.0;
                [unroll] for (int y = 0; y < 4; y++)
                    [unroll] for (int x = 0; x < 4; x++)
                    {
                        float2 uv = i.uv + (float2(x, y) - 1.5) * 0.25 * _Cell.xy;
                        lum += dot(tex2D(_MainTex, uv).rgb, float3(0.299, 0.587, 0.114));
                        person = max(person, tex2D(_Mask, uv).r);
                    }
                return fixed4(lum / 16.0, person, 0, 1);
            }
            ENDCG
        }
    }
}
