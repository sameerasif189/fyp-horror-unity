// Milestone M5: distortion pass for the webcam facecam (FeedbackCam.cs drives every property).
// A UI shader for the RawImage that shows the feed. All effects are 0 by default, so the feed is untouched until
// FeedbackCam runs an event: dim / flicker (_Dim), defocus (_Blur), tearing + RGB split (_Glitch), grain (_Noise),
// snow (_Static), VHS roll (_Roll), blockiness (_Pixelate), a bulge / pinch around the face (_Warp), desaturation +
// vignette (tunnel), a blend towards a stored frame (_EchoMix with _Echo: freeze, lag, time-slip), the camera's exposure
// (_Exposure: a light dip and the auto-exposure catching up), and the room layer.
// The room layer only ever darkens the camera's own pixels - a shadow crossing the wall (_Shadow*), a human-shaped patch
// of darkness in a dim corner (_SilA / _SilB) - and never where _PersonMask says the player is, so it sits behind them.
// A figure is also hidden where the background is much brighter than at its head (_SceneProbe vs _SilRef): something
// nearer the camera, like a pillow, is in front of it.
// Nothing textured is drawn into the picture: a pasted figure reads as a sticker (docs/research/m5-photoreal-stalker).
// Time comes from _FeedTime, which FeedbackCam advances only when the webcam delivers a frame, so grain, tearing and
// every moving shape step at the camera's frame rate rather than gliding at the game's.
// Mirroring is done here, not with RawImage.uvRect, so the vignette / warp / tear coordinates stay unflipped.
Shader "FYP/FeedDistortion"
{
    Properties
    {
        [PerRendererData] _MainTex ("Feed", 2D) = "black" {}
        _Echo ("Stored frame", 2D) = "black" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _Mirror ("Mirror X", Float) = 1
        _FlipY ("Flip Y", Float) = 0
        _Dim ("Dim", Range(0,1)) = 0
        _Blur ("Blur", Range(0,1)) = 0
        _Glitch ("Glitch", Range(0,1)) = 0
        _Noise ("Noise", Range(0,1)) = 0
        _Static ("Static", Range(0,1)) = 0
        _Desat ("Desaturate", Range(0,1)) = 0
        _Vignette ("Vignette", Range(0,1)) = 0
        _Roll ("Roll", Range(0,1)) = 0
        _EchoMix ("Stored frame mix", Range(0,1)) = 0
        _Warp ("Warp (+ bulge, - pinch)", Range(-1,1)) = 0
        _WarpCenter ("Warp centre xy, radius z", Vector) = (0.5, 0.58, 0.24, 0)
        _Pixelate ("Pixelate", Range(0,1)) = 0
        _Seed ("Seed", Float) = 0
        _Aspect ("Feed aspect (w/h)", Float) = 1.7778
        _FeedTime ("Time of the latest webcam frame", Float) = 0
        _Exposure ("Exposure multiplier", Float) = 1

        // Room layer (feed uv of the raw camera image): figure = head centre xy, head radius z (feed heights), edge softness w (head radii).
        _ShadowRect ("Shadow figure", Vector) = (0.5, 0.5, 0.1, 1.2)
        _ShadowAmt ("Shadow depth", Range(0,1)) = 0
        _SilA ("Figure A", Vector) = (0.5, 0.5, 0.1, 0.6)
        _SilAAmt ("Figure A depth", Range(0,1)) = 0
        _SilB ("Figure B", Vector) = (0.5, 0.5, 0.1, 0.6)
        _SilBAmt ("Figure B depth", Range(0,1)) = 0
        _SilRef ("Figure A / B: sqrt brightness of the background at the head (xy)", Vector) = (0, 0, 0, 0)
        _SceneProbe ("Scene brightness map (FeedScene)", 2D) = "black" {}
        _PersonMask ("Person mask", 2D) = "black" {}
        _MaskValid ("Mask valid", Float) = 0

        _StencilComp ("Stencil Comparison", Float) = 8
        _Stencil ("Stencil ID", Float) = 0
        _StencilOp ("Stencil Operation", Float) = 0
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
        _StencilReadMask ("Stencil Read Mask", Float) = 255
        _ColorMask ("Color Mask", Float) = 15
    }

    SubShader
    {
        Tags { "Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent" "PreviewType"="Plane" "CanUseSpriteAtlas"="True" }
        Stencil
        {
            Ref [_Stencil]
            Comp [_StencilComp]
            Pass [_StencilOp]
            ReadMask [_StencilReadMask]
            WriteMask [_StencilWriteMask]
        }
        Cull Off
        Lighting Off
        ZWrite Off
        ZTest [unity_GUIZTestMode]
        Blend SrcAlpha OneMinusSrcAlpha
        ColorMask [_ColorMask]

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; float4 color : COLOR; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float4 color : COLOR; float2 local : TEXCOORD0; };

            sampler2D _MainTex; float4 _MainTex_TexelSize;
            sampler2D _Echo;
            fixed4 _Color;
            float _Mirror, _FlipY, _Dim, _Blur, _Glitch, _Noise, _Static, _Desat, _Vignette, _Roll, _EchoMix, _Warp, _Pixelate, _Seed, _Aspect;
            float _FeedTime, _Exposure;
            float4 _WarpCenter;
            float4 _ShadowRect, _SilA, _SilB, _SilRef;
            sampler2D _SceneProbe;
            float _ShadowAmt, _SilAAmt, _SilBAmt, _MaskValid;
            sampler2D _PersonMask;

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.local = v.uv;
                o.color = v.color * _Color;
                return o;
            }

            float hash (float2 p) { return frac(sin(dot(p, float2(12.9898, 78.233))) * 43758.5453); }

            float2 ToFeed (float2 p) { return float2(_Mirror > 0.5 ? 1.0 - p.x : p.x, _FlipY > 0.5 ? 1.0 - p.y : p.y); }

            float sdBox (float2 p, float2 b) { float2 d = abs(p) - b; return length(max(d, 0.0)) + min(max(d.x, d.y), 0.0); }
            float smin (float a, float b, float k) { float h = saturate(0.5 + 0.5 * (b - a) / k); return lerp(b, a, h) - k * h * (1.0 - h); }

            // A person seen from the front, in head radii with the head centred on the origin: head, neck, shoulders
            // about 2.3 head widths across, and a torso running off the bottom.
            float Figure (float2 q)
            {
                float head = (length(q * float2(1.0, 0.82)) - 1.0) * 0.85;
                float neck = sdBox(q - float2(0.0, -1.3), float2(0.45, 0.6));
                float body = sdBox(q - float2(0.0, -6.0), float2(2.3 - 1.3, 4.3 - 1.3)) - 1.3;
                return smin(smin(head, neck, 0.35), body, 0.6);
            }

            // Soft coverage (0-1) of a figure r = (head centre uv, head radius in feed heights, softness in head radii).
            float Cover (float2 uv, float4 r)
            {
                float2 q = (uv - r.xy) * float2(_Aspect, 1.0) / max(r.z, 1e-4);
                return 1.0 - smoothstep(-r.w, r.w, Figure(q));
            }

            // A figure stands at the distance of the background at its head: where the local background is much
            // brighter (a pillow or chair nearer the camera) it is hidden. A stand-in for depth.
            float Behind (float2 uv, float refSqrt)
            {
                return 1.0 - smoothstep(0.07, 0.16, abs(sqrt(tex2D(_SceneProbe, uv).r) - refSqrt));
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float t = _FeedTime;
                float2 p = i.local;

                // VHS roll: the picture slides up and wraps.
                float roll = _Roll * frac(t * 0.8 + _Seed);
                p.y = frac(p.y + roll);

                // Tearing: random horizontal bands jump sideways, re-rolled ~12 times a second.
                float stepT = floor(t * 12.0 + _Seed * 7.0);
                float bands = lerp(8.0, 30.0, hash(float2(stepT, 1.7)));
                float band = floor(p.y * bands);
                float tear = hash(float2(band, stepT)) > 1.0 - _Glitch * 0.6 ? (hash(float2(band, stepT + 3.1)) - 0.5) * 0.28 * _Glitch : 0.0;
                p.x += tear;

                // Blockiness.
                if (_Pixelate > 0.001)
                {
                    float cells = lerp(180.0, 16.0, _Pixelate);
                    p = (floor(p * float2(cells, cells * 0.5625)) + 0.5) / float2(cells, cells * 0.5625);
                }

                // Bulge (+) or pinch (-) around where the face usually sits.
                float2 d = p - _WarpCenter.xy;
                float dist = length(d * float2(1.0, 0.75));
                if (dist < _WarpCenter.z && abs(_Warp) > 0.001)
                {
                    float k = 1.0 - dist / _WarpCenter.z;
                    p = _WarpCenter.xy + d * (1.0 - _Warp * k * k * 0.65);
                }

                float2 uv = ToFeed(p);
                float split = _Glitch * 0.012 + abs(tear) * 0.25;
                float3 col;
                if (_Blur > 0.001)
                {
                    float2 o = _MainTex_TexelSize.xy * lerp(0.0, 10.0, _Blur);
                    col = 0;
                    [unroll] for (int y = -2; y <= 2; y++)
                        [unroll] for (int x = -2; x <= 2; x++)
                            col += tex2D(_MainTex, uv + float2(x, y) * o).rgb;
                    col /= 25.0;
                }
                else
                {
                    col.r = tex2D(_MainTex, uv + float2(split, 0)).r;
                    col.g = tex2D(_MainTex, uv).g;
                    col.b = tex2D(_MainTex, uv - float2(split, 0)).b;
                }

                // Stored frame: frozen, lagging or time-slipped feed.
                col = lerp(col, tex2D(_Echo, uv).rgb, _EchoMix);

                // Room layer: less light reaching the wall behind the player - multiplied into the camera's own pixels,
                // so their grain, blur and compression stay exactly as they were.
                if (_ShadowAmt + _SilAAmt + _SilBAmt > 0.001)
                {
                    float room = (1.0 - _ShadowAmt * Cover(uv, _ShadowRect))
                               * (1.0 - _SilAAmt * Cover(uv, _SilA) * Behind(uv, _SilRef.x))
                               * (1.0 - _SilBAmt * Cover(uv, _SilB) * Behind(uv, _SilRef.y));
                    float person = _MaskValid > 0.5 ? smoothstep(0.25, 0.65, tex2D(_PersonMask, uv).r) : 0.0;
                    col *= lerp(room, 1.0, person);
                }

                // The camera: exposure (light dips and auto-exposure), then the grading effects.
                col *= _Exposure;
                float lum = dot(col, float3(0.299, 0.587, 0.114));
                col = lerp(col, lum.xxx, _Desat);
                col *= 1.0 - _Dim;
                float2 v = i.local - 0.5;
                col *= 1.0 - _Vignette * saturate(dot(v, v) * 2.4);

                col += (hash(i.local * 613.0 + frac(t * 7.13)) - 0.5) * _Noise * 0.35;
                float snow = hash(floor(i.local * float2(320.0, 180.0)) + floor(t * 30.0) * 1.37);
                col = lerp(col, snow.xxx * 0.85, _Static);

                // Sync bar while rolling, faint scanlines while glitching.
                float bar = 1.0 - _Roll * 0.85 * (1.0 - smoothstep(0.0, 0.05, frac(i.local.y + roll)));
                col *= bar * (1.0 - (_Roll + _Glitch) * 0.12 * (0.5 + 0.5 * sin(i.local.y * 720.0)));

                return fixed4(saturate(col), 1.0) * i.color;
            }
            ENDCG
        }
    }
}
