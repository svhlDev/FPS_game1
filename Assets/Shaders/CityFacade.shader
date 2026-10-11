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
//   - Graphics Phase 4: full URP PBR (main light + shadows, every additional light so neon lights the
//     towers, reflection probes in the glass, SSAO, fog); wall weathering (macro variation per building,
//     darker toward the street, rain streaks under the windows); interior mapping (a room box behind
//     every window: lit rooms glow, dark ones show faint shapes, some blinds down); recessed windows
//     (a 10 cm reveal by normal bending, a shadow under the lintel).
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
            #pragma multi_compile _ _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fog
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Assets/Shaders/SurfaceLib.hlsl"

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


            // Interior mapping: the room behind a window, as a box (window-sized, ROOM_DEPTH deep) seen
            // through the glass. wf: position in the window (0..1), d: the view ray in (window u, window v,
            // depth) units. Returns the shade of the face hit (ceiling brightest, floor darkest) and which
            // face (for variety).
            #define ROOM_DEPTH 4.0
            float RoomShade(float2 wf, float3 d, out float face)
            {
                float3 o = float3(wf, 0.0);
                float tx = ((d.x > 0.0 ? 1.0 : 0.0) - o.x) / (abs(d.x) > 1e-5 ? d.x : 1e-5);
                float ty = ((d.y > 0.0 ? 1.0 : 0.0) - o.y) / (abs(d.y) > 1e-5 ? d.y : 1e-5);
                float tz = 1.0 / max(d.z, 1e-4);
                // Nearest face, picked by comparison (not equality: seams would speckle).
                float t = tz; face = 0.0;
                if (tx < t) { t = tx; face = 1.0; }
                if (ty < t) { t = ty; face = d.y > 0.0 ? 2.0 : 3.0; }
                float z = saturate(d.z * t);
                if (face < 0.5) return 0.9;                                       // back wall
                if (face < 1.5) return 0.65 - 0.2 * z;                            // side walls, darker deeper
                return face < 2.5 ? 1.15 - 0.4 * z : 0.45 - 0.15 * z;             // ceiling / floor
            }

            half4 frag(Varyings i) : SV_Target
            {
                float3 nGeom = normalize(i.normalWS);
                float3 n = nGeom;
                float3 P = i.positionWS;
                float3 V = GetWorldSpaceNormalizeViewDir(P);
                uint seed = (uint)round(i.color.a * 255.0);
                float y = P.y;

                // Wall weathering (every face): macro variation per building, darker toward the street.
                float macro = Fbm(P * 0.035 + float(seed) * 7.13, 2);
                float toStreet = lerp(0.6, 1.0, saturate(y / 45.0));
                half3 wallAlb = _WallColor.rgb * i.color.rgb * lerp(0.72, 1.18, macro) * toStreet;
                half wallSmooth = 0.15;

                half3 albedo = wallAlb;
                half smooth = wallSmooth;
                half3 emission = 0;
                half occl = 1.0;

                if (n.y > 0.5)
                {
                    albedo = _RoofColor.rgb * lerp(0.8, 1.2, Fbm(P * 0.15, 2));
                    smooth = 0.2;
                }
                else if (n.y >= -0.5)
                {
                    uint style = min((uint)round(i.style), 4u);
                    if (y < _BandHeights.x && (seed & 3u) != 0u) style = 4u;   // industrial dominates the underworld
                    bool storefront = y < _StreetHeight;
                    if (storefront) style = 5u;                                  // shops at street level
                    float4 cellDef = kStyleCell[style];
                    float4 look = kStyleLook[style];

                    // Side face: project onto the face's plane (Z-Y for X-facing, X-Y for Z-facing).
                    bool facesX = abs(n.x) > abs(n.z);
                    float3 T = facesX ? float3(0, 0, 1) : float3(1, 0, 0);
                    float3 B = float3(0, 1, 0);
                    float2 uv = facesX ? P.zy : P.xy;
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
                    float h3 = U01(r.z);

                    float bandMul = y < _BandHeights.x ? _BandLit.x : (y < _BandHeights.y ? _BandLit.y : _BandLit.z);
                    // Per-building occupancy, skewed low: some towers are nearly dark.
                    float occupancy = lerp(0.2, 1.0, pow(U01(Pcg3d(uint3(seed, 9u, 41u)).x), 1.5));
                    float litFraction = storefront ? look.x : saturate(look.x * _LitFraction * bandMul * occupancy);
                    float emissionK = _EmissionStrength * kStyleEmission[style];
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

                    // Pixel footprint of one window cell: full detail when cells are big on screen; the
                    // interior, reveals and streaks fade first, then the pattern fades to its average.
                    float px = max(fwidth(g).x, fwidth(g).y);
                    float detail = 1.0 - smoothstep(0.03, 0.12, px);
                    float nearK = 1.0 - smoothstep(0.25, 0.6, px);

                    // ---- near: the window, its reveal, the room behind it ----
                    float2 ws = cellDef.xy * cellDef.zw;                                      // window size (m)
                    float2 wf = saturate((f - margin) / cellDef.zw);                          // 0..1 across the window
                    float2 toEdge = min(wf, 1.0 - wf) * ws;                                   // metres to the window edge
                    // Recessed window: a 10 cm reveal inside the opening, its faces bent toward the centre.
                    float reveal = (1.0 - smoothstep(0.07, 0.1, min(toEdge.x, toEdge.y))) * win * detail;
                    float3 toCentre = toEdge.x < toEdge.y ? T * sign(0.5 - wf.x) : B * sign(0.5 - wf.y);
                    float3 nNear = normalize(nGeom + toCentre * reveal * 2.5);
                    float topShadow = (1.0 - smoothstep(0.0, 0.12, (1.0 - wf.y) * ws.y)) * win;  // under the lintel
                    float glassMask = win * (1.0 - reveal);

                    // The room: the view ray in window units (u along T, v up, depth into the wall).
                    float3 rd = -V;
                    float3 d = float3(dot(rd, T) / ws.x, dot(rd, B) / ws.y, dot(rd, -nGeom) / ROOM_DEPTH);
                    float face;
                    float shade = RoomShade(wf, d, face);
                    float roomVar = lerp(0.6, 1.1, U01(r.z ^ ((uint)face * 747796405u)));
                    // Some windows have blinds down partway (flat; glowing softly when lit).
                    float blind = step(0.7, h3) * step(1.0 - lerp(0.2, 1.0, frac(h3 * 7.3)), wf.y);
                    float3 litRoom = tint * emissionK * brightness * lerp(shade * roomVar, 0.55, blind);
                    float3 darkRoom = (0.004 + 0.01 * shade * roomVar) * lerp(1.0, 2.0, blind) * avgTint;
                    float3 room = isLit > 0.5 ? litRoom : darkRoom;
                    float3 flatWindow = isLit > 0.5 ? tint * emissionK * brightness : 0.0;
                    float3 winEmission = lerp(flatWindow, room, detail);

                    // Rain streaks under each window (in its column), and faint ones down the wall.
                    float below = margin.y - f.y;
                    float under = step(0.0, below) * (1.0 - smoothstep(0.0, margin.y * 1.6, below))
                                * step(margin.x, f.x) * step(f.x, 1.0 - margin.x);
                    float streak = VNoise(float3(uv.x * 9.0, uv.y * 0.35, float(seed))) * under * detail;
                    float wallStreak = Streaks(float3(uv.x, uv.y, float(seed)), 6.0) * 0.6;
                    half3 nearWall = wallAlb * (1.0 - streak * 0.45) * (1.0 - wallStreak * 0.3);

                    // ---- far: the pattern's average ----
                    float open = cellDef.z * cellDef.w;
                    float3 farEmission = avgTint * emissionK * (0.35 + 0.65 / 2.5) * litFraction * open;
                    half3 farAlbedo = lerp(wallAlb, _GlassColor.rgb, open);
                    half farSmooth = lerp(wallSmooth, 0.9, open);

                    // Glass (dark, very smooth: the reflection probes show in it) over the room; the reveal
                    // is wall; a darker strip under the lintel.
                    half3 nearAlbedo = lerp(nearWall, _GlassColor.rgb, glassMask);
                    half nearSmooth = lerp(wallSmooth, 0.95, glassMask);
                    albedo = lerp(farAlbedo, nearAlbedo, nearK);
                    smooth = lerp(farSmooth, nearSmooth, nearK);
                    emission = lerp(farEmission, winEmission * glassMask, nearK);
                    n = normalize(lerp(nGeom, nNear, nearK));
                    occl = 1.0 - topShadow * 0.5 * nearK * detail;
                }

                // Lighting: URP PBR (main light + shadows, every additional light, reflection probes on the
                // glass, SSAO, fog), plus the windows' emission.
                half alpha = 1.0;
                BRDFData brdf;
                InitializeBRDFData(albedo, 0.0, half3(0, 0, 0), smooth, alpha, brdf);
                float2 suv = GetNormalizedScreenSpaceUV(i.positionCS);
                AmbientOcclusionFactor ao = GetScreenSpaceAmbientOcclusion(suv);
                InputData inputData = (InputData)0;
                inputData.positionWS = P;
                inputData.normalWS = n;
                inputData.normalizedScreenSpaceUV = suv;

                half3 color = GlobalIllumination(brdf, (BRDFData)0, 0.0, SampleSH(n), ao.indirectAmbientOcclusion * occl, P, n, V, suv);
                Light mainLight = GetMainLight(TransformWorldToShadowCoord(P));
                mainLight.color *= ao.directAmbientOcclusion;
                color += LightingPhysicallyBased(brdf, mainLight, n, V);
                #if defined(_ADDITIONAL_LIGHTS)
                    uint pixelLightCount = GetAdditionalLightsCount();
                    #if USE_CLUSTER_LIGHT_LOOP
                    [loop] for (uint li = 0; li < min(URP_FP_DIRECTIONAL_LIGHTS_COUNT, MAX_VISIBLE_LIGHTS); li++)
                        color += LightingPhysicallyBased(brdf, GetAdditionalLight(li, P, half4(1, 1, 1, 1)), n, V);
                    #endif
                    LIGHT_LOOP_BEGIN(pixelLightCount)
                        color += LightingPhysicallyBased(brdf, GetAdditionalLight(lightIndex, P, half4(1, 1, 1, 1)), n, V);
                    LIGHT_LOOP_END
                #endif
                color += emission;
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
