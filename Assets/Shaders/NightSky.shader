// Gradient night skybox for URP: dark top, a faint glow at the horizon (matching the fog), dark below.
// No textures. A sparse star field from a direction hash: soft points sized from fwidth, so they
// don't sparkle while the camera pans. Set _StarBrightness to 0 for no stars.
Shader "FPS/NightSky"
{
    Properties
    {
        _TopColor ("Top", Color) = (0.01, 0.012, 0.03, 1)
        _HorizonColor ("Horizon", Color) = (0.12, 0.08, 0.18, 1)
        _BottomColor ("Bottom", Color) = (0.03, 0.02, 0.05, 1)
        _Exponent ("Gradient Exponent", Float) = 0.45
        _StarDensity ("Star Density", Range(0, 1)) = 0.002
        _StarBrightness ("Star Brightness", Float) = 0.8
    }

    SubShader
    {
        Tags { "Queue" = "Background" "RenderType" = "Background" "PreviewType" = "Skybox" "RenderPipeline" = "UniversalPipeline" }
        Cull Off
        ZWrite Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "HashPCG.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _TopColor;
                float4 _HorizonColor;
                float4 _BottomColor;
                float _Exponent;
                float _StarDensity;
                float _StarBrightness;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings { float4 positionCS : SV_POSITION; float3 dir : TEXCOORD0; };

            Varyings vert(Attributes v)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.dir = v.positionOS.xyz;
                return o;
            }


            half4 frag(Varyings i) : SV_Target
            {
                float3 d = normalize(i.dir);
                float3 col = d.y >= 0.0
                    ? lerp(_HorizonColor.rgb, _TopColor.rgb, pow(saturate(d.y), _Exponent))
                    : lerp(_HorizonColor.rgb, _BottomColor.rgb, pow(saturate(-d.y * 4.0), 0.5));

                // Stars: one candidate per cell on a direction grid, only above the horizon.
                float3 g = d * 300.0;
                int3 cell = (int3)floor(g);
                uint3 r = HashInt3(cell);
                uint3 r2 = Pcg3d(r);
                float present = step(1.0 - _StarDensity, U01(r.x));
                float3 starPos = 0.25 + 0.5 * float3(U01(r.y), U01(r.z), U01(r2.x));
                float dist = length(frac(g) - starPos);
                float px = length(fwidth(g));
                float radius = 0.08 + px;                       // at least about a pixel wide
                float soft = 1.0 - smoothstep(0.0, radius, dist);
                float energy = saturate(0.08 / radius);         // dimmer when smeared over more pixels
                col += present * soft * energy * _StarBrightness * (0.4 + 0.6 * U01(r2.y)) * saturate(d.y * 4.0);
                return half4(col, 1.0);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
