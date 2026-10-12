// Face Fix Part 8: eyebrow hair. The brow mesh (WigBuilder.BuildBrows) carries, per vertex, its position
// along the brow (uv.x, m, from the inner end) and across it (uv.y, m, + up); the shader draws short
// dark strokes there - hairs lying outward along the brow, rising a little at the inner end - and clips
// between them (alpha test: works with depth, shadows and SSAO), thinning toward the brow's edges and
// its outer tail.
// Lives in Resources so player builds include it (BodyMaterials loads it by name).
Shader "FPS/Brow"
{
    Properties
    {
        _HairColor ("Hair colour", Color) = (0.08, 0.06, 0.05, 1)
        _Density ("Density", Range(0, 1)) = 0.62
        _Width ("Brow half width (m)", Float) = 0.0035
        _Length ("Brow length (m)", Float) = 0.045
    }

    SubShader
    {
        Tags { "RenderType" = "TransparentCutout" "RenderPipeline" = "UniversalPipeline" "Queue" = "AlphaTest" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        CBUFFER_START(UnityPerMaterial)
            half4 _HairColor;
            half _Density;
            float _Width, _Length;
        CBUFFER_END

        float Hash1(float2 p) { return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453); }

        // 1 on a hair stroke, 0 between them.
        float Strokes(float2 uv)
        {
            float along = uv.x, across = uv.y;
            float t = saturate(along / max(_Length, 1e-4));
            // Hairs rise toward the inner end (up to ~55 degrees), lie flatter along the tail.
            float ang = lerp(0.95, 0.25, t);
            float s = along * cos(ang) + across * sin(ang);      // along a hair
            float r = -along * sin(ang) + across * cos(ang);     // across the hairs
            float row = floor(r / 0.00035);
            float h = Hash1(float2(row, 3.7));
            float seg = (s / 0.0045) + h * 7.0;
            float cell = floor(seg), f = frac(seg);
            float on = step(Hash1(float2(row, cell)), _Density) * step(0.12, f) * step(f, 0.9);
            // Thinner toward the edges across and along the tail; nothing past the ends.
            float edge = 1.0 - smoothstep(0.55, 1.0, abs(across) / max(_Width, 1e-4));
            float tail = 1.0 - smoothstep(0.75, 1.05, t);
            float thin = Hash1(float2(row * 1.3, cell + 0.5));
            return on * step(thin, edge * tail);
        }
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
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fog
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; float2 uv : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; float3 normalWS : TEXCOORD1; float2 uv : TEXCOORD2; float fogFactor : TEXCOORD3; };

            Varyings vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                VertexPositionInputs p = GetVertexPositionInputs(v.positionOS.xyz);
                o.positionCS = p.positionCS; o.positionWS = p.positionWS;
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                o.uv = v.uv;
                o.fogFactor = ComputeFogFactor(p.positionCS.z);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                clip(Strokes(i.uv) - 0.5);
                half3 n = normalize(i.normalWS);
                half3 albedo = _HairColor.rgb * 0.85h;
                Light l = GetMainLight(TransformWorldToShadowCoord(i.positionWS));
                half3 c = albedo * (SampleSH(n) + l.color * saturate(dot(n, l.direction)) * l.shadowAttenuation);
                return half4(MixFog(c, i.fogFactor), 1);
            }
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
            struct A { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
            struct V { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };
            V DV(A v) { V o; o.positionCS = TransformObjectToHClip(v.positionOS.xyz); o.uv = v.uv; return o; }
            half DF(V i) : SV_Target { clip(Strokes(i.uv) - 0.5); return 0; }
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
            struct A { float4 positionOS : POSITION; float3 normalOS : NORMAL; float2 uv : TEXCOORD0; };
            struct V { float4 positionCS : SV_POSITION; float3 normalWS : TEXCOORD0; float2 uv : TEXCOORD1; };
            V NV(A v) { V o; o.positionCS = TransformObjectToHClip(v.positionOS.xyz); o.normalWS = TransformObjectToWorldNormal(v.normalOS); o.uv = v.uv; return o; }
            half4 NF(V i) : SV_Target { clip(Strokes(i.uv) - 0.5); return half4(normalize(i.normalWS), 0); }
            ENDHLSL
        }
    }
}
