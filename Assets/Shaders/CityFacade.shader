// Procedural skyscraper facade for URP. One material for every building mass (SRP Batcher compatible).
// Windows come from WORLD-space position (triplanar on the side faces), so they tile on any box size.
//   - Cell _WindowSize.xy (2.5 m x 3.33 m = three floors per 10 m layer), opening _WindowSize.zw of the cell.
//   - Per-window hash: lit or dark (_LitFraction, scaled per altitude band), warm or cool tint.
//   - Vertex colour: rgb = this building's wall tint, a = building seed (set by the builder, so per-building
//     variation survives static batching without MaterialPropertyBlocks).
//   - Roofs (up-facing) get no windows.
// Altitude bands (world Y): below _BandHeights.x = underworld, up to _BandHeights.y = traffic band, above = upper city.
Shader "FPS/CityFacade"
{
    Properties
    {
        _WallColor ("Wall Color (multiplies vertex colour)", Color) = (1, 1, 1, 1)
        _RoofColor ("Roof Color", Color) = (0.07, 0.07, 0.08, 1)
        _WindowSize ("Window Cell (w, h) and Opening (x, y)", Vector) = (2.5, 3.3333, 0.7, 0.7)
        _LitFraction ("Lit Fraction", Range(0, 1)) = 0.35
        _EmissionStrength ("Emission Strength", Float) = 3
        [HDR] _WarmColor ("Warm Window", Color) = (1, 0.72, 0.42, 1)
        [HDR] _CoolColor ("Cool Window", Color) = (0.6, 0.78, 1, 1)
        [HDR] _SodiumColor ("Underworld Window", Color) = (1, 0.55, 0.2, 1)
        _GlassColor ("Dark Glass", Color) = (0.02, 0.03, 0.05, 1)
        _GlassReflect ("Dark Glass Reflection Tint", Color) = (0.1, 0.14, 0.22, 1)
        _BandHeights ("Band Heights (traffic min, traffic max)", Vector) = (90, 160, 0, 0)
        _BandLit ("Lit Multiplier (under, traffic, upper)", Vector) = (0.35, 1.4, 0.8, 0)
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        CBUFFER_START(UnityPerMaterial)
            float4 _WallColor;
            float4 _RoofColor;
            float4 _WindowSize;
            float _LitFraction;
            float _EmissionStrength;
            float4 _WarmColor;
            float4 _CoolColor;
            float4 _SodiumColor;
            float4 _GlassColor;
            float4 _GlassReflect;
            float4 _BandHeights;
            float4 _BandLit;
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
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fog
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float4 color : COLOR;
                float fogFactor : TEXCOORD2;
            };

            Varyings vert(Attributes v)
            {
                UNITY_SETUP_INSTANCE_ID(v);
                Varyings o;
                VertexPositionInputs p = GetVertexPositionInputs(v.positionOS.xyz);
                o.positionCS = p.positionCS;
                o.positionWS = p.positionWS;
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                o.color = v.color;
                o.fogFactor = ComputeFogFactor(p.positionCS.z);
                return o;
            }

            float Hash21(float2 p)
            {
                p = frac(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }

            half4 frag(Varyings i) : SV_Target
            {
                float3 n = normalize(i.normalWS);
                float3 wall = _WallColor.rgb * i.color.rgb;

                Light mainLight = GetMainLight(TransformWorldToShadowCoord(i.positionWS));
                float3 light = SampleSH(n) + mainLight.color * saturate(dot(n, mainLight.direction)) * mainLight.shadowAttenuation;

                float3 color;
                if (n.y > 0.5)
                {
                    color = _RoofColor.rgb * light;
                }
                else if (n.y < -0.5)
                {
                    color = wall * light;
                }
                else
                {
                    // Side face: project onto the face's plane (Z-Y for X-facing, X-Y for Z-facing).
                    bool facesX = abs(n.x) > abs(n.z);
                    float2 uv = facesX ? i.positionWS.zy : i.positionWS.xy;
                    float2 g = uv / _WindowSize.xy;
                    float2 cell = floor(g);
                    float2 f = frac(g);

                    // Anti-aliased window rectangle inside the cell (thin mullions between).
                    float2 margin = (1.0 - _WindowSize.zw) * 0.5;
                    float2 aa = max(fwidth(g), 1e-4);
                    float2 w2 = smoothstep(margin - aa, margin + aa, f) * (1.0 - smoothstep(1.0 - margin - aa, 1.0 - margin + aa, f));
                    float win = w2.x * w2.y;

                    // Per-window randomness: cell + which face + this building.
                    float faceId = facesX ? (n.x > 0 ? 1.0 : 2.0) : (n.z > 0 ? 3.0 : 4.0);
                    float2 key = cell + float2(faceId * 37.1, i.color.a * 911.0);
                    float h = Hash21(key);
                    float h2 = Hash21(key * 1.73 + 5.11);

                    float y = i.positionWS.y;
                    float bandMul = y < _BandHeights.x ? _BandLit.x : (y < _BandHeights.y ? _BandLit.y : _BandLit.z);
                    float isLit = step(h, saturate(_LitFraction * bandMul));

                    float3 tint = lerp(_WarmColor.rgb, _CoolColor.rgb, step(0.5, h2));
                    if (y < _BandHeights.x) tint = lerp(tint, _SodiumColor.rgb, 0.75);       // sodium underworld
                    else if (y >= _BandHeights.y) tint = lerp(tint, _CoolColor.rgb, 0.7);    // cool upper city

                    float3 glass = _GlassColor.rgb * light + _GlassReflect.rgb * SampleSH(reflect(-GetWorldSpaceNormalizeViewDir(i.positionWS), n)) ;
                    float3 window = isLit > 0.5 ? tint * _EmissionStrength * (0.55 + 0.45 * h2) : glass;
                    color = lerp(wall * light, window, win);
                }

                color = MixFog(color, i.fogFactor);
                return half4(color, 1.0);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On
            ZTest LEqual
            ColorMask 0

            HLSLPROGRAM
            #pragma vertex ShadowVert
            #pragma fragment ShadowFrag
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

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
            ZWrite On
            ColorMask R

            HLSLPROGRAM
            #pragma vertex DepthVert
            #pragma fragment DepthFrag
            #pragma multi_compile_instancing

            struct Attributes { float4 positionOS : POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };

            float4 DepthVert(Attributes v) : SV_POSITION
            {
                UNITY_SETUP_INSTANCE_ID(v);
                return TransformObjectToHClip(v.positionOS.xyz);
            }

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

            half4 NormalsFrag(Varyings i) : SV_Target { return half4(normalize(i.normalWS), 0.0); }
            ENDHLSL
        }
    }
    FallBack Off
}
