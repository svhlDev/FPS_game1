// Additive hologram / billboard for URP: two-colour gradient, blocky scrolling "content", scanlines and
// an occasional flicker, all driven by _Time. No lights, no colliders needed.
// With _Pattern = 0, _ScanlineStrength = 0 and _FlickerRate = 0 it is a plain soft additive glow
// (used for the underworld haze planes).
Shader "FPS/Hologram"
{
    Properties
    {
        [HDR] _ColorA ("Color A", Color) = (0.2, 0.9, 1, 1)
        [HDR] _ColorB ("Color B", Color) = (1, 0.2, 0.8, 1)
        _Intensity ("Intensity", Float) = 1.5
        _Pattern ("Content Pattern (0-1)", Range(0, 1)) = 1
        _PatternCells ("Pattern Cells (x, y)", Vector) = (6, 10, 0, 0)
        _ScrollSpeed ("Scroll Speed", Float) = 0.15
        _ScanlineDensity ("Scanlines per unit V", Float) = 120
        _ScanlineStrength ("Scanline Strength", Range(0, 1)) = 0.45
        _FlickerRate ("Flicker Chance (0 = never)", Range(0, 1)) = 0
        _EdgeFade ("Edge Fade", Range(0.001, 0.5)) = 0.06
        _Seed ("Seed", Float) = 0
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }
        Blend One One
        ZWrite Off
        Cull Off

        Pass
        {
            Name "Hologram"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "HashPCG.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _ColorA;
                float4 _ColorB;
                float _Intensity;
                float _Pattern;
                float4 _PatternCells;
                float _ScrollSpeed;
                float _ScanlineDensity;
                float _ScanlineStrength;
                float _FlickerRate;
                float _EdgeFade;
                float _Seed;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; float fogFactor : TEXCOORD1; float3 positionWS : TEXCOORD2; };

            // Integer coordinates in, [0, 1) out (PCG3D).
            float Hash(int a, int b, int c) { return U01(HashInt3(int3(a, b, c)).x); }

            Varyings vert(Attributes v)
            {
                UNITY_SETUP_INSTANCE_ID(v);
                Varyings o;
                VertexPositionInputs p = GetVertexPositionInputs(v.positionOS.xyz);
                o.positionCS = p.positionCS;
                o.positionWS = p.positionWS;
                o.uv = v.uv;
                o.fogFactor = ComputeFogFactor(p.positionCS.z);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                // Wrapped time keeps precision in long sessions.
                float t = frac(_Time.y / 1000.0) * 1000.0;
                // Per-object variation from world position, so many panels can share a material.
                int2 site = int2(floor(i.positionWS.xz / 50.0));
                int seedI = (int)(_Seed * 1000.0);
                float seed = frac(_Seed + Hash(site.x, site.y, seedI));
                int objI = seedI + site.x * 73 + site.y * 151;

                float3 col = lerp(_ColorA.rgb, _ColorB.rgb, saturate(i.uv.y + 0.25 * sin(t * 0.7 + seed * 6.28)));

                // Blocky scrolling content
                float2 cells = max(_PatternCells.xy, 1.0);
                int2 c = int2(floor(float2(i.uv.x, i.uv.y + t * _ScrollSpeed) * cells));
                float block = step(0.45, Hash(c.x + objI, c.y, (int)floor(t * 0.5)));
                float content = lerp(1.0, 0.35 + 0.65 * block, _Pattern);

                // Scanlines, faded out once they're too fine for the screen (no moire / crawling).
                float scanStrength = _ScanlineStrength * (1.0 - saturate((fwidth(i.uv.y) * _ScanlineDensity - 0.3) / 0.4));
                float scan = lerp(1.0, 0.5 + 0.5 * sin((i.uv.y + t * 0.05) * _ScanlineDensity * 6.2832), scanStrength);

                // Occasional flicker: a few frames at a time
                float flick = _FlickerRate > 0.0 ? step(1.0 - _FlickerRate, Hash((int)floor(t * 12.0), objI, 71)) : 0.0;
                float flicker = 1.0 - 0.7 * flick;

                // Soft edges
                float2 e = min(i.uv, 1.0 - i.uv);
                float edge = saturate(min(e.x, e.y) / _EdgeFade);

                col *= _Intensity * content * scan * flicker * edge;
                // Additive: fog fades it to nothing with distance.
                col = MixFogColor(col, float3(0, 0, 0), i.fogFactor);
                return half4(col, 1.0);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
