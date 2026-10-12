// Face Fix Part 3: the eyeball of generated bodies (a sphere, +z = the gaze), all procedural in object space.
//   Sclera: warm grey, pinker toward the corners (the horizontal extremes), faint veins.
//   Iris: 0.44 x the eyeball's diameter, radial fibres, a dark limbal ring, the pupil; _Glow adds the INT
//   glow (HDR, blooms) over the iris.
//   The lids' shadow: darker toward the top (the upper lid overhangs) and a little toward the bottom.
//   Wet: sharp GGX highlights from every light, Fresnel-weighted reflection probes; SSAO.
// Lives in Resources so player builds include it (BodyMaterials loads it by name).
Shader "FPS/Eye"
{
    Properties
    {
        _IrisColor ("Iris", Color) = (0.3, 0.4, 0.6, 1)
        [HDR] _Glow ("Glow (INT)", Color) = (0, 0, 0, 0)
        _ScleraColor ("Sclera", Color) = (0.84, 0.8, 0.76, 1)
        _Smoothness ("Smoothness", Range(0, 1)) = 0.92
        _LidShadow ("Lid shadow", Range(0, 1)) = 0.55
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        CBUFFER_START(UnityPerMaterial)
            half4 _IrisColor, _Glow, _ScleraColor;
            half _Smoothness, _LidShadow;
        CBUFFER_END
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fog
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float3 dirOS : TEXCOORD2;
                float fogFactor : TEXCOORD3;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_TRANSFER_INSTANCE_ID(v, o);
                VertexPositionInputs p = GetVertexPositionInputs(v.positionOS.xyz);
                o.positionCS = p.positionCS;
                o.positionWS = p.positionWS;
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                o.dirOS = v.positionOS.xyz;
                o.fogFactor = ComputeFogFactor(p.positionCS.z);
                return o;
            }

            float Hash(float2 p) { return frac(sin(dot(p, float2(12.9898, 78.233))) * 43758.5453); }
            float Noise1(float x) { float i = floor(x), f = frac(x); f = f * f * (3.0 - 2.0 * f); return lerp(Hash(float2(i, 0.0)), Hash(float2(i + 1.0, 0.0)), f); }

            half3 EyeLight(Light light, half3 albedo, half3 n, half3 v, half roughness)
            {
                half ndl = saturate(dot(n, light.direction));
                half atten = light.distanceAttenuation * light.shadowAttenuation;
                half3 h = SafeNormalize(light.direction + v);
                half nh = saturate(dot(n, h));
                half a2 = roughness * roughness;
                half d = nh * nh * (a2 - 1.0h) + 1.0h;
                half ggx = a2 / max(PI * d * d, 1e-4h);
                return (albedo * ndl + 0.04h * ggx * ndl * 0.25h) * light.color * atten;
            }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                float3 d = normalize(i.dirOS);
                // Front disc coordinates (the iris and pupil), and the angle round it (fibres).
                float rho = length(d.xy) * step(0.0, d.z) + step(d.z, 0.0) * 2.0;
                float ang = atan2(d.y, d.x);
                const float irisR = 0.44, pupilR = 0.15;
                // Sclera: warm grey, pinker toward the corners, faint veins there.
                half3 sclera = _ScleraColor.rgb;
                float corner = smoothstep(0.45, 0.95, abs(d.x)) * step(-0.2, d.z);
                float veins = smoothstep(0.82, 0.97, Noise1(ang * 9.0 + d.z * 13.0)) * corner;
                sclera = lerp(sclera, sclera * half3(1.05, 0.78, 0.76), corner * 0.6h);
                sclera = lerp(sclera, half3(0.62, 0.2, 0.2), veins * 0.35h);
                // Iris: fibres (radial streaks), lighter toward the pupil; the limbal ring; the pupil.
                float fib = Noise1(ang * 24.0) * 0.6 + Noise1(ang * 57.0 + 3.0) * 0.4;
                half3 iris = _IrisColor.rgb * lerp(0.65h, 1.25h, (half)fib) * lerp(1.25h, 0.85h, (half)smoothstep(pupilR, irisR, rho));
                float limbal = smoothstep(irisR - 0.09, irisR, rho);
                iris = lerp(iris, iris * 0.22h, (half)limbal);
                float inIris = 1.0 - smoothstep(irisR - 0.01, irisR + 0.012, rho);
                float inPupil = 1.0 - smoothstep(pupilR - 0.012, pupilR + 0.008, rho);
                half3 albedo = lerp(sclera, iris, (half)inIris);
                albedo = lerp(albedo, half3(0.01, 0.01, 0.012), (half)inPupil);
                // The lids' shadow (the upper lid overhangs more).
                half lid = (half)(smoothstep(0.15, 0.55, d.y) * 0.8 + smoothstep(0.25, 0.6, -d.y) * 0.4) * _LidShadow;
                albedo *= 1.0h - lid;

                half3 n = normalize(i.normalWS);
                half3 v = GetWorldSpaceNormalizeViewDir(i.positionWS);
                half perceptual = 1.0h - _Smoothness;
                half roughness = max(perceptual * perceptual, 0.01h);
                InputData inputData = (InputData)0;
                inputData.positionWS = i.positionWS;
                inputData.normalWS = n;
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(i.positionCS);
                AmbientOcclusionFactor ao = GetScreenSpaceAmbientOcclusion(inputData.normalizedScreenSpaceUV);
                half nv = saturate(dot(n, v));
                half fresnel = 0.04h + 0.96h * pow(1.0h - nv, 5.0h);
                half3 color = (SampleSH(n) * albedo + GlossyEnvironmentReflection(reflect(-v, n), i.positionWS, perceptual, 1.0h, inputData.normalizedScreenSpaceUV) * fresnel * (1.0h - lid))
                              * ao.indirectAmbientOcclusion;
                Light mainLight = GetMainLight(TransformWorldToShadowCoord(i.positionWS));
                mainLight.color *= ao.directAmbientOcclusion;
                color += EyeLight(mainLight, albedo, n, v, roughness);
                #if defined(_ADDITIONAL_LIGHTS)
                    uint pixelLightCount = GetAdditionalLightsCount();
                    #if USE_CLUSTER_LIGHT_LOOP
                    [loop] for (uint li = 0; li < min(URP_FP_DIRECTIONAL_LIGHTS_COUNT, MAX_VISIBLE_LIGHTS); li++)
                    {
                        Light dl = GetAdditionalLight(li, i.positionWS, half4(1, 1, 1, 1));
                        color += EyeLight(dl, albedo, n, v, roughness);
                    }
                    #endif
                    LIGHT_LOOP_BEGIN(pixelLightCount)
                        Light light = GetAdditionalLight(lightIndex, i.positionWS, half4(1, 1, 1, 1));
                        color += EyeLight(light, albedo, n, v, roughness);
                    LIGHT_LOOP_END
                #endif
                // INT glow over the iris (HDR: blooms).
                color += _Glow.rgb * (half)(inIris * (1.0 - inPupil * 0.6));
                color = MixFog(color, i.fogFactor);
                return half4(color, 1);
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On ColorMask R

            HLSLPROGRAM
            #pragma vertex DepthVert
            #pragma fragment DepthFrag
            #pragma multi_compile_instancing
            struct Attributes { float4 positionOS : POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };
            float4 DepthVert(Attributes v) : SV_POSITION { UNITY_SETUP_INSTANCE_ID(v); return TransformObjectToHClip(v.positionOS.xyz); }
            half DepthFrag() : SV_Target { return 0; }
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }
            ZWrite On

            HLSLPROGRAM
            #pragma vertex NormalsVert
            #pragma fragment NormalsFrag
            #pragma multi_compile_instancing
            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS : SV_POSITION; float3 normalWS : TEXCOORD0; };
            Varyings NormalsVert(Attributes v)
            {
                UNITY_SETUP_INSTANCE_ID(v);
                Varyings o;
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                return o;
            }
            half4 NormalsFrag(Varyings i) : SV_Target { return half4(normalize(i.normalWS), 0); }
            ENDHLSL
        }
    }
}
