// Face Fix Part 9: hair cards (HairCards). Each card is a ribbon along a guide strand: uv.x across it
// (0..1), uv.y root (0) to tip (1); the tangent runs along the strand; uv2.x a per-card random.
//   Strands: many thin stripes across the card, each its own width and darkness, wavering a little;
//   alpha falls off toward the card's edges and its tip. Alpha clip with a dither (works with depth,
//   shadows and SSAO; no sorting).
//   Kajiya-Kay highlights: two bands along the hair's flow - a white one shifted toward the tip, a
//   tinted, sparkly one shifted toward the root.
//   Colour: darker at the roots, a little jitter per card; wrapped diffuse; SH ambient; SSAO.
// Lives in Resources so player builds include it (BodyMaterials loads it by name).
Shader "FPS/Hair"
{
    Properties
    {
        _HairColor ("Hair colour", Color) = (0.08, 0.06, 0.05, 1)
        _Strands ("Strands across a card", Float) = 14
        _Spec1 ("Primary highlight (white)", Range(0, 2)) = 0.18
        _Spec2 ("Secondary highlight (tinted)", Range(0, 2)) = 0.25
        _Shift1 ("Primary shift", Range(-1, 1)) = 0.1
        _Shift2 ("Secondary shift", Range(-1, 1)) = -0.15
    }

    SubShader
    {
        Tags { "RenderType" = "TransparentCutout" "RenderPipeline" = "UniversalPipeline" "Queue" = "AlphaTest" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        CBUFFER_START(UnityPerMaterial)
            half4 _HairColor;
            float _Strands;
            half _Spec1, _Spec2, _Shift1, _Shift2;
        CBUFFER_END

        float H1(float x) { return frac(sin(x * 127.1 + 311.7) * 43758.5453); }

        // Coverage (0..1) and the strand's own darkness at a card uv.
        // inner: the inner layer's cards are nearly solid (they hide the scalp), the outer ones stringy.
        float2 HairAlpha(float2 uv, float rnd, float outer)
        {
            float s = uv.x * _Strands + sin(uv.y * 11.0 + rnd * 40.0) * 0.35;
            float id = floor(s), f = frac(s);
            float w = lerp(lerp(0.85, 1.0, H1(id + rnd * 17.0)), lerp(0.45, 0.85, H1(id + rnd * 17.0)), outer);
            float stripe = 1.0 - smoothstep(w * 0.5, w * 0.5 + 0.15, abs(f - 0.5));
            // A strand may end early (ragged tips).
            float len = lerp(0.7, 1.0, H1(id * 3.1 + rnd * 7.0));
            float tip = 1.0 - smoothstep(len - 0.3 * outer - 0.15, len, uv.y);
            // (Edges: the outer cards' strands thin out toward both sides - no hard card outline; Face Polish Part 6.)
            float edge = smoothstep(0.0, lerp(0.08, 0.32, outer), uv.x) * smoothstep(1.0, lerp(0.92, 0.68, outer), uv.x);
            return float2(stripe * tip * edge, lerp(0.75, 1.15, H1(id + 5.3)));
        }

        // A screen-space dither threshold (interleaved gradient noise).
        float Dither(float4 positionCS) { return frac(52.9829189 * frac(dot(positionCS.xy, float2(0.06711056, 0.00583715)))); }
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; float4 tangentOS : TANGENT; float2 uv : TEXCOORD0; float2 uv2 : TEXCOORD1; };
            struct Varyings
            {
                float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; float3 normalWS : TEXCOORD1; float3 tangentWS : TEXCOORD2;
                float2 uv : TEXCOORD3; float2 uv2 : TEXCOORD4; float fogFactor : TEXCOORD5;
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                VertexPositionInputs p = GetVertexPositionInputs(v.positionOS.xyz);
                o.positionCS = p.positionCS; o.positionWS = p.positionWS;
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                o.tangentWS = TransformObjectToWorldDir(v.tangentOS.xyz);
                o.uv = v.uv; o.uv2 = v.uv2;
                o.fogFactor = ComputeFogFactor(p.positionCS.z);
                return o;
            }

            // Kajiya-Kay: the highlight of a cylinder along T, shifted along the normal.
            half KK(half3 T, half3 n, half3 h, half shift, half power)
            {
                half3 t = normalize(T + n * shift);
                half th = dot(t, h);
                return pow(saturate(sqrt(max(0.0h, 1.0h - th * th))), power);
            }

            half3 HairLight(Light l, half3 albedo, half3 n, half3 T, half3 v, half sparkle, half specK)
            {
                half3 h = SafeNormalize(l.direction + v);
                half ndl = dot(n, l.direction);
                half diff = saturate(ndl * 0.65h + 0.35h);
                half3 c = albedo * diff + (half3(1, 1, 1) * KK(T, n, h, _Shift1, 60.0h) * _Spec1 + albedo * 2.0h * KK(T, n, h, _Shift2, 20.0h) * _Spec2 * sparkle) * saturate(ndl + 0.3h) * specK;
                return c * l.color * (l.distanceAttenuation * l.shadowAttenuation);
            }

            half4 frag(Varyings i, bool front : SV_IsFrontFace) : SV_Target
            {
                float2 a = HairAlpha(i.uv, i.uv2.x, abs(i.uv2.y));
                clip(a.x - Dither(i.positionCS) * 0.9 - 0.05);
                half3 n = normalize(i.normalWS) * (front ? 1.0h : -1.0h);
                half3 T = normalize(i.tangentWS);
                half3 v = GetWorldSpaceNormalizeViewDir(i.positionWS);
                // Darker at the roots, per-card jitter, per-strand darkness; the inner layer a little darker.
                half3 albedo = _HairColor.rgb * lerp(0.4h, 1.0h, (half)smoothstep(0.0, 0.55, i.uv.y)) * lerp(0.8h, 1.2h, (half)i.uv2.x) * (half)a.y;
                albedo *= lerp(0.92h, 1.0h, (half)abs(i.uv2.y));
                // Face hair (uv2.y = -1: brows, beards; Face Polish Part 4): short tufts lying on the skin - a
                // little darker, faint highlights (flat-lying cards caught the full sheen and read silver).
                half face = i.uv2.y < -0.5 ? 1.0h : 0.0h;
                albedo *= lerp(1.0h, 0.8h, face);
                half specK = lerp(1.0h, 0.2h, face);
                half sparkle = lerp(0.7h, 1.0h, (half)H1(floor(i.uv.x * _Strands) + i.uv2.x * 9.0));
                float2 suv = GetNormalizedScreenSpaceUV(i.positionCS);
                AmbientOcclusionFactor ao = GetScreenSpaceAmbientOcclusion(suv);
                half3 c = SampleSH(n) * albedo * ao.indirectAmbientOcclusion;
                Light ml = GetMainLight(TransformWorldToShadowCoord(i.positionWS));
                ml.color *= ao.directAmbientOcclusion;
                c += HairLight(ml, albedo, n, T, v, sparkle, specK);
                #if defined(_ADDITIONAL_LIGHTS)
                    uint cnt = GetAdditionalLightsCount();
                    #if USE_CLUSTER_LIGHT_LOOP
                    [loop] for (uint li = 0; li < min(URP_FP_DIRECTIONAL_LIGHTS_COUNT, MAX_VISIBLE_LIGHTS); li++)
                        c += HairLight(GetAdditionalLight(li, i.positionWS, half4(1, 1, 1, 1)), albedo, n, T, v, sparkle, specK);
                    #endif
                    InputData inputData = (InputData)0; inputData.positionWS = i.positionWS; inputData.normalizedScreenSpaceUV = suv;
                    uint pixelLightCount = cnt;
                    LIGHT_LOOP_BEGIN(pixelLightCount)
                        c += HairLight(GetAdditionalLight(lightIndex, i.positionWS, half4(1, 1, 1, 1)), albedo, n, T, v, sparkle, specK);
                    LIGHT_LOOP_END
                #endif
                return half4(MixFog(c, i.fogFactor), 1);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On ZTest LEqual ColorMask 0 Cull Off
            HLSLPROGRAM
            #pragma vertex SV
            #pragma fragment SF
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            float3 _LightDirection; float3 _LightPosition;
            struct A { float4 positionOS : POSITION; float3 normalOS : NORMAL; float2 uv : TEXCOORD0; float2 uv2 : TEXCOORD1; };
            struct V { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; float2 uv2 : TEXCOORD1; };
            V SV(A v)
            {
                V o;
                float3 pw = TransformObjectToWorld(v.positionOS.xyz); float3 nw = TransformObjectToWorldNormal(v.normalOS);
                #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                float3 ld = normalize(_LightPosition - pw);
                #else
                float3 ld = _LightDirection;
                #endif
                o.positionCS = TransformWorldToHClip(ApplyShadowBias(pw, nw, ld));
                #if UNITY_REVERSED_Z
                o.positionCS.z = min(o.positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #else
                o.positionCS.z = max(o.positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #endif
                o.uv = v.uv; o.uv2 = v.uv2;
                return o;
            }
            half4 SF(V i) : SV_Target { clip(HairAlpha(i.uv, i.uv2.x, abs(i.uv2.y)).x - 0.5); return 0; }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On ColorMask R Cull Off
            HLSLPROGRAM
            #pragma vertex DV
            #pragma fragment DF
            struct A { float4 positionOS : POSITION; float2 uv : TEXCOORD0; float2 uv2 : TEXCOORD1; };
            struct V { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; float2 uv2 : TEXCOORD1; };
            V DV(A v) { V o; o.positionCS = TransformObjectToHClip(v.positionOS.xyz); o.uv = v.uv; o.uv2 = v.uv2; return o; }
            half DF(V i) : SV_Target { clip(HairAlpha(i.uv, i.uv2.x, abs(i.uv2.y)).x - Dither(i.positionCS) * 0.9 - 0.05); return 0; }
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }
            ZWrite On Cull Off
            HLSLPROGRAM
            #pragma vertex NV
            #pragma fragment NF
            struct A { float4 positionOS : POSITION; float3 normalOS : NORMAL; float2 uv : TEXCOORD0; float2 uv2 : TEXCOORD1; };
            struct V { float4 positionCS : SV_POSITION; float3 normalWS : TEXCOORD0; float2 uv : TEXCOORD1; float2 uv2 : TEXCOORD2; };
            V NV(A v) { V o; o.positionCS = TransformObjectToHClip(v.positionOS.xyz); o.normalWS = TransformObjectToWorldNormal(v.normalOS); o.uv = v.uv; o.uv2 = v.uv2; return o; }
            half4 NF(V i) : SV_Target { clip(HairAlpha(i.uv, i.uv2.x, abs(i.uv2.y)).x - Dither(i.positionCS) * 0.9 - 0.05); return half4(normalize(i.normalWS), 0); }
            ENDHLSL
        }
    }
}
