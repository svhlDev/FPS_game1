// Gradient night skybox for URP: dark top, a faint glow at the horizon (matching the fog), dark below.
// No textures. A sparse star field from a direction hash.
Shader "FPS/NightSky"
{
    Properties
    {
        _TopColor ("Top", Color) = (0.01, 0.012, 0.03, 1)
        _HorizonColor ("Horizon", Color) = (0.12, 0.08, 0.18, 1)
        _BottomColor ("Bottom", Color) = (0.03, 0.02, 0.05, 1)
        _Exponent ("Gradient Exponent", Float) = 0.45
        _StarDensity ("Star Density", Range(0, 1)) = 0.004
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

            float Hash31(float3 p)
            {
                p = frac(p * 0.3183099 + 0.1);
                p *= 17.0;
                return frac(p.x * p.y * p.z * (p.x + p.y + p.z));
            }

            half4 frag(Varyings i) : SV_Target
            {
                float3 d = normalize(i.dir);
                float3 col = d.y >= 0.0
                    ? lerp(_HorizonColor.rgb, _TopColor.rgb, pow(saturate(d.y), _Exponent))
                    : lerp(_HorizonColor.rgb, _BottomColor.rgb, pow(saturate(-d.y * 4.0), 0.5));

                // Stars: sparse cells on a direction grid, only above the horizon.
                float3 cell = floor(d * 300.0);
                float s = step(1.0 - _StarDensity, Hash31(cell)) * saturate(d.y * 4.0);
                col += s * _StarBrightness * Hash31(cell + 3.7);
                return half4(col, 1.0);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
