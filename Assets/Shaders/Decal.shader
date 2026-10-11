// Graphics Phase 5: procedural mesh decals. Quads laid just off walls and roads by the builders
// (CityDressing.ScatterDecals), merged into one mesh per decal type, alpha-blended over what's behind.
// No textures: every pattern is shader math on the quad's UV (0..1) and a per-quad seed (vertex
// colour r). Lit like the surfaces they sit on (ambient, main light, every additional light), fogged.
//   Types (_Type): 0 grime blotch, 1 leak / rust streak (runs down the quad), 2 poster, 3 graffiti,
//                  4 oil stain, 5 skid marks.
Shader "FPS/Decal"
{
    Properties
    {
        _Type ("Decal type", Float) = 0
        _Opacity ("Opacity", Range(0, 1)) = 1
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "AlphaTest+10" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }

        Pass
        {
            Name "ForwardDecal"
            Tags { "LightMode" = "UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off
            Offset -1, -2

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fog
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Assets/Shaders/SurfaceLib.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _Type;
                half _Opacity;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; float2 uv : TEXCOORD0; half4 color : COLOR; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float2 uv : TEXCOORD2;
                nointerpolation half4 color : TEXCOORD3;
                float fogFactor : TEXCOORD4;
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                VertexPositionInputs p = GetVertexPositionInputs(v.positionOS.xyz);
                o.positionCS = p.positionCS;
                o.positionWS = p.positionWS;
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                o.uv = v.uv;
                o.color = v.color;
                o.fogFactor = ComputeFogFactor(p.positionCS.z);
                return o;
            }

            float3 Hue(float h) { return saturate(abs(frac(h + float3(0.0, 2.0 / 3.0, 1.0 / 3.0)) * 6.0 - 3.0) - 1.0); }

            // Colour (albedo) and alpha of the decal at uv; seed in 0..1.
            float4 Pattern(int type, float2 uv, float seed)
            {
                float3 s3 = float3(uv * 4.0, seed * 97.0);
                float2 c = uv - 0.5;
                if (type == 0)          // grime blotch: soft-edged irregular dark patch, densest at the bottom
                {
                    float r = length(c * float2(1.0, 1.3)) + (Fbm(s3, 3) - 0.5) * 0.45;
                    float a = (1.0 - smoothstep(0.18, 0.48, r)) * lerp(1.0, 0.55, uv.y) * (0.5 + 0.5 * Fbm(s3 * 3.0, 2));
                    return float4(0.035, 0.03, 0.025, a * 0.75);
                }
                if (type == 1)          // leak: a narrow streak running down from the top, rusty or dark
                {
                    float x = c.x + (Fbm(float3(uv.y * 3.0, seed * 31.0, 0.0), 2) - 0.5) * 0.25;
                    float width = lerp(0.18, 0.06, uv.y < 0.5 ? 0.0 : 1.0) * lerp(0.7, 1.3, seed);
                    float a = exp(-x * x / (width * width)) * smoothstep(0.0, 0.6, uv.y) * (0.6 + 0.4 * VNoise(float3(uv.x * 20.0, uv.y * 2.0, seed * 13.0)));
                    float3 col = lerp(float3(0.03, 0.025, 0.02), float3(0.16, 0.07, 0.03), step(0.55, seed));
                    return float4(col, a * 0.8);
                }
                if (type == 2)          // poster: colour blocks, a title band, text lines, torn and aged edges
                {
                    float torn = (Fbm(float3(uv * 9.0, seed * 7.0), 2) - 0.5) * 0.06;
                    float inside = step(abs(c.x) + torn, 0.46) * step(abs(c.y) + torn, 0.47);
                    float3 bg = lerp(Hue(seed), float3(0.9, 0.88, 0.8), 0.35 + 0.4 * frac(seed * 5.3));
                    float3 ink = Hue(frac(seed + 0.45)) * 0.35;
                    float title = step(0.62, uv.y) * step(uv.y, 0.8);
                    float lines = step(0.15, uv.y) * step(uv.y, 0.5) * step(0.45, frac(uv.y * 14.0)) * step(abs(c.x), 0.35)
                                * step(0.25, VNoise(float3(uv.x * 25.0, floor(uv.y * 14.0), seed * 3.0)));
                    float block = step(abs(c.x - (frac(seed * 3.7) - 0.5) * 0.3), 0.2) * step(abs(uv.y - 0.55), 0.08);
                    float3 col = bg;
                    col = lerp(col, ink, saturate(title * 0.85 + lines * 0.9));
                    col = lerp(col, Hue(frac(seed * 2.1)) * 0.9, block);
                    col *= lerp(0.7, 1.0, Fbm(s3 * 2.0, 2));                                 // dirt and fading
                    return float4(col, inside * 0.95);
                }
                if (type == 3)          // graffiti: looping strokes in a bright colour with a dark outline
                {
                    float2 q = uv * float2(3.0, 1.6) + float2(seed * 10.0, 0.0);
                    float2 w = float2(Fbm(float3(q, seed * 5.0), 2), Fbm(float3(q + 7.3, seed * 5.0), 2)) - 0.5;
                    float wave = sin((uv.x * 9.0 + w.x * 4.0) * 3.14159) * 0.3 + w.y * 0.6;
                    float d = abs(c.y - wave * 0.6);
                    float mask = (1.0 - smoothstep(0.35, 0.5, abs(c.x))) * (1.0 - smoothstep(0.3, 0.45, abs(c.y)));
                    float stroke = 1.0 - smoothstep(0.05, 0.07, d);
                    float outline = 1.0 - smoothstep(0.07, 0.09, d);
                    float3 col = lerp(float3(0.02, 0.02, 0.02), Hue(seed) * 0.9 + 0.05, stroke);
                    float drips = step(0.8, VNoise(float3(uv.x * 30.0, 0.0, seed * 9.0))) * step(c.y, -0.05) * step(-0.4, c.y) * (1.0 - smoothstep(0.0, 0.25, -c.y - wave * 0.6));
                    return float4(col, saturate(max(outline, drips * 0.8)) * mask * 0.92);
                }
                if (type == 4)          // oil stain: dark, a lighter rim, rainbow-free
                {
                    float r = length(c) + (Fbm(s3 * 1.5, 3) - 0.5) * 0.3;
                    float a = (1.0 - smoothstep(0.2, 0.42, r));
                    float rim = smoothstep(0.25, 0.38, r) * a;
                    return float4(lerp(0.012, 0.04, rim).xxx, a * 0.8);
                }
                // skid marks: two parallel dark tyre streaks along v, fading at both ends
                float lane = min(abs(c.x - 0.22), abs(c.x + 0.22));
                float tyre = 1.0 - smoothstep(0.06, 0.1, lane + (VNoise(float3(uv.y * 8.0, uv.x * 3.0, seed * 11.0)) - 0.5) * 0.05);
                float ends = smoothstep(0.0, 0.25, uv.y) * smoothstep(0.0, 0.25, 1.0 - uv.y);
                return float4(0.015, 0.015, 0.015, tyre * ends * lerp(0.35, 0.7, seed));
            }

            half4 frag(Varyings i) : SV_Target
            {
                if (i.color.g < _QualityDecalCut) discard;                          // fewer decals at lower quality
                float4 pc = Pattern((int)_Type, i.uv, i.color.r);
                if (pc.a < 0.003) discard;
                float3 n = normalize(i.normalWS);
                float3 light = SampleSH(n);
                Light mainLight = GetMainLight();
                light += mainLight.color * saturate(dot(n, mainLight.direction));
                #if defined(_ADDITIONAL_LIGHTS)
                    InputData inputData = (InputData)0;
                    inputData.positionWS = i.positionWS;
                    inputData.normalWS = n;
                    inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(i.positionCS);
                    uint pixelLightCount = GetAdditionalLightsCount();
                    #if USE_CLUSTER_LIGHT_LOOP
                    [loop] for (uint li = 0; li < min(URP_FP_DIRECTIONAL_LIGHTS_COUNT, MAX_VISIBLE_LIGHTS); li++)
                    {
                        Light dl = GetAdditionalLight(li, i.positionWS, half4(1, 1, 1, 1));
                        light += dl.color * saturate(dot(n, dl.direction)) * dl.distanceAttenuation;
                    }
                    #endif
                    LIGHT_LOOP_BEGIN(pixelLightCount)
                        Light al = GetAdditionalLight(lightIndex, i.positionWS, half4(1, 1, 1, 1));
                        light += al.color * saturate(dot(n, al.direction)) * al.distanceAttenuation;
                    LIGHT_LOOP_END
                #endif
                // Posters (backlit boxes) and graffiti (fluorescent paint) glow faintly: the city at night.
                int type = (int)_Type;
                float glow = type == 2 ? 0.18 : (type == 3 ? 0.1 : 0.0);
                float3 col = MixFog(pc.rgb * (light + glow), i.fogFactor);
                return half4(col, pc.a * _Opacity);
            }
            ENDHLSL
        }
    }
}
