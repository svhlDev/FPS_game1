// Procedural skyscraper facade for URP. One material for every building mass (SRP Batcher compatible).
// Windows come from WORLD-space position (triplanar on the side faces), so they tile on any box size.
//   - Facade STYLE per mass from uv2.x (integer, not interpolated): window cell, opening, lit fraction,
//     tint bias and reflection come from a small table (see kStyleCell / kStyleLook). Cell heights divide
//     10 m, so floors line up with the layer grid. In the underworld band most buildings switch to the
//     industrial style (by building seed).
//       0 grid office 2.5 x 3.33 | 1 ribbon 10 x 3.33 | 2 curtain wall 1.6 x 3.33 | 3 residential 3.33 x 3.33 | 4 industrial 5 x 5
//       5 storefront 5 x 3.33: wide warm shop windows, half of them lit. Every building's first
//         _StreetHeight m (the street level) uses it, whatever its own style.
//   - Per-window integer hash: lit or dark, warm or cool tint. Lit fraction = style's base value x band x
//     per-building occupancy (0.2-1, skewed low, from the seed) x _LitFraction (global scale). Big-opening
//     styles (ribbon, curtain wall) glow at half emission.
//   - Vertex colour: rgb = this building's wall tint, a = building seed (set by the builder, so per-building
//     variation survives static batching without MaterialPropertyBlocks).
//   - Roofs (up-facing) get no windows.
//   - Far away (window cells smaller than ~0.25-0.6 px) the pattern fades to its average colour,
//     so distant facades don't sparkle or crawl.
// Altitude bands (world Y): below _BandHeights.x = underworld, up to _BandHeights.y = traffic band, above = upper city.
Shader "FPS/CityFacade"
{
    Properties
    {
        _WallColor ("Wall Color (multiplies vertex colour)", Color) = (1, 1, 1, 1)
        _RoofColor ("Roof Color", Color) = (0.07, 0.07, 0.08, 1)
        _LitFraction ("Lit Fraction Scale", Range(0, 2)) = 1
        _EmissionStrength ("Emission Strength", Float) = 3.5
        [HDR] _WarmColor ("Warm Window", Color) = (1, 0.72, 0.42, 1)
        [HDR] _CoolColor ("Cool Window", Color) = (0.6, 0.78, 1, 1)
        [HDR] _SodiumColor ("Underworld Window", Color) = (1, 0.55, 0.2, 1)
        _GlassColor ("Dark Glass", Color) = (0.02, 0.03, 0.05, 1)
        _GlassReflect ("Dark Glass Reflection Tint", Color) = (0.1, 0.14, 0.22, 1)
        _BandHeights ("Band Heights (traffic min, traffic max)", Vector) = (90, 160, 0, 0)
        _BandLit ("Lit Multiplier (under, traffic, upper)", Vector) = (0.5, 1.2, 0.7, 0)
        _StreetHeight ("Storefront Height (m)", Float) = 10
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        CBUFFER_START(UnityPerMaterial)
            float4 _WallColor;
            float4 _RoofColor;
            float _LitFraction;
            float _EmissionStrength;
            float4 _WarmColor;
            float4 _CoolColor;
            float4 _SodiumColor;
            float4 _GlassColor;
            float4 _GlassReflect;
            float4 _BandHeights;
            float4 _BandLit;
            float _StreetHeight;
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
            #include "HashPCG.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float4 color : COLOR;
                float2 uv2 : TEXCOORD1;     // x = facade style id
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                nointerpolation float4 color : COLOR; // per-mesh constant: never interpolate the seed
                float fogFactor : TEXCOORD2;
                nointerpolation float style : TEXCOORD3;
            };

            // Per style: cell (w, h) in metres, opening (w, h) as a fraction of the cell.
            static const float4 kStyleCell[6] =
            {
                float4(2.5, 10.0 / 3.0, 0.70, 0.70),        // 0 grid office
                float4(10.0, 10.0 / 3.0, 0.97, 0.55),       // 1 ribbon: continuous glass bands per floor
                float4(1.6, 10.0 / 3.0, 0.92, 0.92),        // 2 curtain wall: nearly all glass
                float4(10.0 / 3.0, 10.0 / 3.0, 0.45, 0.55), // 3 residential: small punched windows
                float4(5.0, 5.0, 0.30, 0.25),               // 4 industrial: sparse slits
                float4(5.0, 10.0 / 3.0, 0.90, 0.75),        // 5 storefront: wide shop windows
            };
            // Per style: base lit fraction, warm bias, sodium bias, glass reflection multiplier.
            static const float4 kStyleLook[6] =
            {
                float4(0.15, 0.0, 0.00, 1.0),
                float4(0.06, 0.0, 0.00, 1.2),
                float4(0.05, 0.0, 0.00, 2.2),
                float4(0.18, 0.6, 0.00, 0.8),
                float4(0.05, 0.0, 0.85, 0.6),
                float4(0.50, 0.9, 0.00, 0.8),               // lit fraction used as is (no band / occupancy)
            };
            // Per style: emission multiplier (panorama windows glow rather than blaze).
            static const float kStyleEmission[6] = { 1.0, 0.5, 0.5, 1.0, 1.0, 0.8 };

            Varyings vert(Attributes v)
            {
                UNITY_SETUP_INSTANCE_ID(v);
                Varyings o;
                VertexPositionInputs p = GetVertexPositionInputs(v.positionOS.xyz);
                o.positionCS = p.positionCS;
                o.positionWS = p.positionWS;
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                o.color = v.color;
                o.style = v.uv2.x;
                o.fogFactor = ComputeFogFactor(p.positionCS.z);
                return o;
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
                    float y = i.positionWS.y;
                    uint seed = (uint)round(i.color.a * 255.0);
                    uint style = min((uint)round(i.style), 4u);
                    if (y < _BandHeights.x && (seed & 3u) != 0u) style = 4u;   // industrial dominates the underworld
                    bool storefront = y < _StreetHeight;
                    if (storefront) style = 5u;                                  // shops at street level
                    float4 cellDef = kStyleCell[style];
                    float4 look = kStyleLook[style];

                    // Side face: project onto the face's plane (Z-Y for X-facing, X-Y for Z-facing).
                    bool facesX = abs(n.x) > abs(n.z);
                    float2 uv = facesX ? i.positionWS.zy : i.positionWS.xy;
                    float2 g = uv / cellDef.xy;
                    float2 cell = floor(g);
                    float2 f = frac(g);

                    // Anti-aliased window rectangle inside the cell (thin mullions between).
                    float2 margin = (1.0 - cellDef.zw) * 0.5;
                    float2 aa = max(fwidth(g), 1e-4);
                    float2 w2 = smoothstep(margin - aa, margin + aa, f) * (1.0 - smoothstep(1.0 - margin - aa, 1.0 - margin + aa, f));
                    float win = w2.x * w2.y;

                    // Per-window randomness, integers only: cell + which face + this building's seed.
                    uint faceId = facesX ? (n.x > 0 ? 1u : 2u) : (n.z > 0 ? 3u : 4u);
                    int2 c = (int2)cell;
                    uint3 r = Pcg3d(uint3(c.x + 32768, c.y + 32768, faceId * 256u + seed));
                    float h = U01(r.x);
                    float h2 = U01(r.y);

                    float bandMul = y < _BandHeights.x ? _BandLit.x : (y < _BandHeights.y ? _BandLit.y : _BandLit.z);
                    // Per-building occupancy, skewed low: some towers are nearly dark.
                    float occupancy = lerp(0.2, 1.0, pow(U01(Pcg3d(uint3(seed, 9u, 41u)).x), 1.5));
                    float litFraction = storefront ? look.x : saturate(look.x * _LitFraction * bandMul * occupancy);
                    float emission = _EmissionStrength * kStyleEmission[style];
                    float isLit = step(h, litFraction);

                    float3 tint = lerp(_WarmColor.rgb, _CoolColor.rgb, step(0.5, h2));
                    float3 avgTint = 0.5 * (_WarmColor.rgb + _CoolColor.rgb);
                    tint = lerp(lerp(tint, _WarmColor.rgb, look.y), _SodiumColor.rgb, look.z);
                    avgTint = lerp(lerp(avgTint, _WarmColor.rgb, look.y), _SodiumColor.rgb, look.z);
                    if (storefront) { }                                                       // shops stay warm
                    else if (y < _BandHeights.x)                                              // sodium underworld
                    {
                        tint = lerp(tint, _SodiumColor.rgb, 0.75);
                        avgTint = lerp(avgTint, _SodiumColor.rgb, 0.75);
                    }
                    else if (y >= _BandHeights.y)                                             // cool upper city
                    {
                        tint = lerp(tint, _CoolColor.rgb, 0.7);
                        avgTint = lerp(avgTint, _CoolColor.rgb, 0.7);
                    }

                    // Most lit windows are dim, a few are bright.
                    float brightness = lerp(0.35, 1.0, pow(h2, 1.5));
                    float3 glass = _GlassColor.rgb * light + look.w * _GlassReflect.rgb * SampleSH(reflect(-GetWorldSpaceNormalizeViewDir(i.positionWS), n));
                    float3 window = isLit > 0.5 ? tint * emission * brightness : glass;
                    float3 nearColor = lerp(wall * light, window, win);

                    // Distance filtering: as cells shrink below a few pixels, blend to the pattern's average
                    // (this style's opening area and lit fraction).
                    // Mean brightness of lerp(0.35, 1, h^1.5) over uniform h is 0.35 + 0.65 / 2.5.
                    float px = max(fwidth(g).x, fwidth(g).y);
                    float3 avgWindow = avgTint * emission * (0.35 + 0.65 / 2.5) * litFraction + glass * (1.0 - litFraction);
                    float3 farColor = lerp(wall * light, avgWindow, cellDef.z * cellDef.w);
                    color = lerp(nearColor, farColor, smoothstep(0.25, 0.6, px));
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
