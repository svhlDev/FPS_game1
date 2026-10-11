// Basic skin for generated bodies (phase 7; full procedural materials come later).
//   PBR-ish: GGX specular at smoothness ~0.4 (F0 0.028), Fresnel-weighted reflection probes.
//   Wrap lighting: diffuse light bleeds _Wrap past the shadow line (faking light scattered under the
//   skin), and the bled part is tinted _ScatterColor (warm red), so terminators glow a little.
//   Lights: the main light (with shadows), every additional light (Forward+ cluster loop or the classic
//   per-object loop), SH ambient and reflection probes - so neon lights skin like the facades.
// Graphics Phase 3: pores and fine micro relief in rest-pose space (TEXCOORD3, so they stick to the
// skin), bumped through screen derivatives and fading with distance; per-vertex data from
// BodySurfaceBake: redness (knuckles, nose, ears, creases), crease darkening, contact shadow just outside
// cuffs and collars; SSAO.
// Lives in Resources so player builds include it (BodyMaterials loads it by name).
Shader "FPS/BodySkin"
{
    Properties
    {
        _BaseColor ("Skin tone", Color) = (0.82, 0.64, 0.52, 1)
        _Smoothness ("Smoothness", Range(0, 1)) = 0.4
        _Wrap ("Wrap (light past the shadow line)", Range(0, 1)) = 0.45
        _ScatterColor ("Scatter tint", Color) = (0.9, 0.28, 0.16, 1)
        _ScatterStrength ("Scatter strength", Range(0, 2)) = 0.9
        _PoreStrength ("Pore / micro relief strength", Range(0, 3)) = 1
        _Redness ("Redness (knuckles, nose, ears, creases)", Range(0, 1)) = 0.6
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        CBUFFER_START(UnityPerMaterial)
            half4 _BaseColor;
            half _Smoothness, _Wrap, _ScatterStrength;
            half4 _ScatterColor;
            half _PoreStrength, _Redness;
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
            #include "Assets/Shaders/SurfaceLib.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION; float3 normalOS : NORMAL;
                float3 restPos : TEXCOORD3; half4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float fogFactor : TEXCOORD2;
                float3 restPos : TEXCOORD3;
                half4 color : TEXCOORD4;
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
                o.fogFactor = ComputeFogFactor(p.positionCS.z);
                o.restPos = v.restPos;
                o.color = v.color;
                return o;
            }

            // Pores (~1.5 mm) over a finer-than-cm micro relief, in rest-pose metres; 0..1.
            float SkinHeight(float3 r)
            {
                float pores = 1.0 - smoothstep(0.0, 0.3, VNoise(r * 650.0));
                return VNoise(r * 170.0) * 0.5 - pores * 0.5 * DetailFade(r, 650.0);
            }

            // One light: wrapped diffuse with the wrap tinted, plus GGX specular.
            half3 SkinLight(Light light, half3 albedo, half3 n, half3 v, half roughness, half3 spec)
            {
                half ndl = dot(n, light.direction);
                half lambert = saturate(ndl);
                half wrapped = saturate((ndl + _Wrap) / (1.0h + _Wrap));
                half3 scatter = (wrapped - lambert) * _ScatterColor.rgb * _ScatterStrength;
                half atten = light.distanceAttenuation * light.shadowAttenuation;
                half3 diffuse = albedo * (lambert + scatter);
                half3 h = SafeNormalize(light.direction + v);
                half nh = saturate(dot(n, h));
                half a2 = roughness * roughness;
                half d = nh * nh * (a2 - 1.0h) + 1.0h;
                half ggx = a2 / max(PI * d * d, 1e-4h);
                half3 specular = spec * ggx * lambert * 0.25h;
                return (diffuse + specular) * light.color * atten;
            }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                half3 n = normalize(i.normalWS);
                half3 v = GetWorldSpaceNormalizeViewDir(i.positionWS);
                half3 albedo = _BaseColor.rgb;
                // Baked per-vertex data (a mesh without colours reads white: ignore it).
                half4 vc = dot(i.color, 1.0) > 3.99 ? half4(0, 0, 0, 0) : i.color;
                albedo = lerp(albedo, albedo * half3(1.18, 0.78, 0.76), vc.a * _Redness);
                albedo *= 1.0h - vc.g * 0.25h;

                // Micro relief: height in rest-pose space, bumped with screen derivatives (surface
                // gradient), so it follows the skin whatever the pose. Fades out with distance.
                float fade = DetailFade(i.restPos, 170.0);
                half smoothVar = 1.0h;
                if (fade > 0.01)
                {
                    float h = SkinHeight(i.restPos) * 0.0004 * _PoreStrength * fade;          // metres
                    float3 dpdx = ddx(i.positionWS), dpdy = ddy(i.positionWS);
                    float3 r1 = cross(dpdy, n), r2 = cross(n, dpdx);
                    float det = dot(dpdx, r1);
                    float3 grad = sign(det) * (ddx(h) * r1 + ddy(h) * r2);
                    n = normalize(abs(det) * n - grad);
                    smoothVar = lerp(1.0h, 0.85h, (half)saturate(-h / 0.0002));
                }
                half perceptual = 1.0h - _Smoothness * smoothVar;
                half roughness = max(perceptual * perceptual, 0.02h);
                half3 spec = half3(0.028h, 0.028h, 0.028h);

                InputData inputData = (InputData)0;
                inputData.positionWS = i.positionWS;
                inputData.normalWS = n;
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(i.positionCS);

                // Ambient (SH) and reflections, Fresnel-weighted; SSAO and the baked contact shadow darken
                // both (and a little of the direct light).
                AmbientOcclusionFactor ao = GetScreenSpaceAmbientOcclusion(inputData.normalizedScreenSpaceUV);
                ao.indirectAmbientOcclusion *= 1.0h - vc.b * 0.75h;
                ao.directAmbientOcclusion *= 1.0h - vc.b * 0.35h;
                half nv = saturate(dot(n, v));
                half fresnel = 0.028h + (1.0h - 0.028h) * pow(1.0h - nv, 5.0h) * _Smoothness;
                half3 color = (SampleSH(n) * albedo
                            + GlossyEnvironmentReflection(reflect(-v, n), i.positionWS, perceptual, 1.0h, inputData.normalizedScreenSpaceUV) * fresnel)
                            * ao.indirectAmbientOcclusion;

                Light mainLight = GetMainLight(TransformWorldToShadowCoord(i.positionWS));
                mainLight.color *= ao.directAmbientOcclusion;
                color += SkinLight(mainLight, albedo, n, v, roughness, spec);

                #if defined(_ADDITIONAL_LIGHTS)
                    uint pixelLightCount = GetAdditionalLightsCount();
                    #if USE_CLUSTER_LIGHT_LOOP
                    [loop] for (uint li = 0; li < min(URP_FP_DIRECTIONAL_LIGHTS_COUNT, MAX_VISIBLE_LIGHTS); li++)
                    {
                        Light dl = GetAdditionalLight(li, i.positionWS, half4(1, 1, 1, 1));
                        color += SkinLight(dl, albedo, n, v, roughness, spec);
                    }
                    #endif
                    LIGHT_LOOP_BEGIN(pixelLightCount)
                        Light light = GetAdditionalLight(lightIndex, i.positionWS, half4(1, 1, 1, 1));
                        color += SkinLight(light, albedo, n, v, roughness, spec);
                    LIGHT_LOOP_END
                #endif

                color = MixFog(color, i.fogFactor);
                return half4(color, 1);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On ZTest LEqual ColorMask 0

            HLSLPROGRAM
            #pragma vertex ShadowVert
            #pragma fragment ShadowFrag
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            float3 _LightDirection;
            float3 _LightPosition;
            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; UNITY_VERTEX_INPUT_INSTANCE_ID };
            float4 ShadowVert(Attributes v) : SV_POSITION
            {
                UNITY_SETUP_INSTANCE_ID(v);
                float3 positionWS = TransformObjectToWorld(v.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(v.normalOS);
                #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                float3 lightDir = normalize(_LightPosition - positionWS);
                #else
                float3 lightDir = _LightDirection;
                #endif
                float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDir));
                #if UNITY_REVERSED_Z
                positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #else
                positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #endif
                return positionCS;
            }
            half4 ShadowFrag() : SV_Target { return 0; }
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
