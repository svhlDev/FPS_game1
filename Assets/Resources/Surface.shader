// Graphics Phase 2: the main lit shader. One shader, one material per (type, colour, variant)
// (MaterialLibrary), every pattern procedural (SurfaceLib.hlsl). SRP Batcher compatible: all properties
// in UnityPerMaterial. The material type is a uniform branch (_Type), not a keyword, so every material
// shares one set of variants and batches with the rest.
//   Types: 0 concrete, 1 asphalt, 2 painted metal, 3 brushed metal, 4 chrome, 5 glass, 6 rubber / plastic,
//          7 car paint (clear coat, optional flakes), 8 cotton, 9 denim, 10 wool, 11 leather, 12 vinyl,
//          13 road paint (worn markings), 14 hazard stripes.
//   Pattern space (_Space): 0 world (static city), 1 object (rigid movers), 2 rest pose (TEXCOORD3, baked
//          by the body pipeline for skinned meshes; Phase 3).
//   Edges / cavities (_EdgeSource): 0 none, 1 box faces (per-face UVs: primitives and slabs, survives
//          static batching), 2 vertex colour (r convex edges, g cavity), 3 screen-space curvature.
//   Lighting: full URP PBR (main light + shadows, Forward+ additional lights, reflection probes with
//          blending and box projection, SSAO, fog), the clear-coat lobe (always compiled; zero cost in
//          shading terms when _Type isn't car paint), fabric sheen, global wetness (_GlobalWetness).
// Lives in Resources so player builds include it (runtime materials: weapons, figures).
Shader "FPS/Surface"
{
    Properties
    {
        _Type ("Material type", Float) = 0
        _BaseColor ("Base colour", Color) = (0.5, 0.5, 0.5, 1)
        _SecondColor ("Second colour (bare metal, faded, worn)", Color) = (0.55, 0.55, 0.57, 1)
        _SmoothMin ("Smoothness min", Range(0, 1)) = 0.2
        _SmoothMax ("Smoothness max", Range(0, 1)) = 0.4
        _Metallic ("Metallic", Range(0, 1)) = 0
        _DetailScale ("Detail scale", Float) = 1
        _DetailStrength ("Detail strength", Range(0, 2)) = 1
        _Wear ("Wear", Range(0, 1)) = 0.5
        _Grime ("Grime", Range(0, 1)) = 0.5
        _Flake ("Metallic flake (car paint)", Range(0, 1)) = 0
        _Sheen ("Sheen (fabric)", Range(0, 1)) = 0
        _EdgeWidth ("Edge wear width (m)", Float) = 0.04
        _Space ("Pattern space (0 world, 1 object, 2 rest pose)", Float) = 0
        _EdgeSource ("Edges (0 none, 1 box UVs, 2 vertex colour, 3 curvature)", Float) = 1
        [HDR] _EmissionColor ("Emission", Color) = (0, 0, 0, 1)
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        CBUFFER_START(UnityPerMaterial)
            float _Type;
            half4 _BaseColor;
            half4 _SecondColor;
            half _SmoothMin, _SmoothMax, _Metallic;
            float _DetailScale, _DetailStrength;
            half _Wear, _Grime, _Flake, _Sheen;
            float _EdgeWidth, _Space, _EdgeSource;
            half4 _EmissionColor;
        CBUFFER_END
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fog
            #pragma multi_compile_instancing

            // The clear-coat path of URP's BRDF / GI functions, for every type (mask 0 = no coat).
            #define _CLEARCOAT 1
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Assets/Shaders/SurfaceLib.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                float3 restPos : TEXCOORD3;
                half4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float3 patternPos : TEXCOORD2;
                float3 patternNrm : TEXCOORD3;
                float2 uv : TEXCOORD4;
                half4 color : TEXCOORD5;
                float fogFactor : TEXCOORD6;
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
                // Object space in metres: primitives are unit meshes scaled by the transform, so scale the
                // local position by the object's scale (otherwise patterns stretch with it).
                float3x3 m = (float3x3)GetObjectToWorldMatrix();
                float3 scale = float3(length(m._m00_m10_m20), length(m._m01_m11_m21), length(m._m02_m12_m22));
                o.patternPos = _Space < 0.5 ? p.positionWS : (_Space < 1.5 ? v.positionOS.xyz * scale : v.restPos);
                o.patternNrm = _Space < 0.5 ? o.normalWS : v.normalOS;
                o.uv = v.uv;
                o.color = v.color;
                o.fogFactor = ComputeFogFactor(p.positionCS.z);
                return o;
            }

            #define T_CONCRETE 0
            #define T_ASPHALT 1
            #define T_PAINTED 2
            #define T_BRUSHED 3
            #define T_CHROME 4
            #define T_GLASS 5
            #define T_RUBBER 6
            #define T_CARPAINT 7
            #define T_COTTON 8
            #define T_DENIM 9
            #define T_WOOL 10
            #define T_LEATHER 11
            #define T_VINYL 12
            #define T_ROADPAINT 13
            #define T_HAZARD 14

            // Pattern frequencies (cycles per metre at detail scale 1): fabric threads and leather grain.
            #define COTTON_F 420.0
            #define DENIM_F 260.0
            #define WOOL_F 60.0
            #define LEATHER_F 90.0

            // The type's height pattern (pattern space, already in detail-scale units), for the detail
            // normal. `w`: triplanar weights. `fade`: 0 far away (the caller also fades the strength).
            float HeightOf(int type, float3 p, float3 w)
            {
                [branch] switch (type)
                {
                    case T_CONCRETE: return Fbm(p * 5.0, 2) * 0.5 - (1.0 - smoothstep(0.0, 0.15, VNoise(p * 60.0))) * 0.15;
                    case T_ASPHALT:  return VNoise(p * 45.0) * 0.18;
                    case T_PAINTED:  return VNoise(p * 80.0) * 0.012;
                    case T_BRUSHED:  return VNoise(float3(p.x * 0.6, p.y * 140.0, p.z * 140.0)) * 0.1;
                    case T_RUBBER:   return VNoise(p * 12.0) * 0.08;
                    case T_COTTON:   return WeaveTri(p * COTTON_F, w) * 0.3;
                    case T_DENIM:    return TwillTri(p * DENIM_F, w) * 0.35;
                    case T_WOOL:     return Fbm(p * WOOL_F, 2) * 0.06;
                    case T_LEATHER:  return smoothstep(0.0, 0.25, Voronoi(p * LEATHER_F).y) * 0.07;
                    default:         return 0.0;
                }
            }

            struct Surf
            {
                half3 albedo; half metallic; half smoothness;
                half3 sheen; half coat; half coatSmooth; half porosity; half3 emission;
                float3 flakeGrad;
            };

            Surf Evaluate(int type, float3 p, float3 w, float edge, float cavity, BoxEdges box, bool hasBox, float3 nWS)
            {
                Surf s;
                half3 base = _BaseColor.rgb;
                s.albedo = base; s.metallic = _Metallic; s.smoothness = lerp(_SmoothMin, _SmoothMax, 0.5);
                s.sheen = 0; s.coat = 0; s.coatSmooth = 0; s.porosity = 1.0; s.flakeGrad = 0; s.emission = 0;
                float wear = edge * _Wear;

                [branch] switch (type)
                {
                    case T_CONCRETE:
                    {
                        float mottle = Fbm(p * 0.6, 3);
                        float fine = lerp(0.5, VNoise(p * 9.0), DetailFade(p, 9.0));
                        // Per-slab tone and dark joints every 1.5 m on floors (paving, decks), readable far off.
                        float floorMask = saturate(nWS.y * 5.0 - 4.0);
                        float2 slab = p.xz / 1.5;
                        float2 jf = abs(frac(slab) - 0.5);
                        float toJoint = 0.5 - max(jf.x, jf.y);                               // slab units, 0 on a joint
                        float jw = 0.008 + fwidth(slab.x);                                     // ~1 cm, at least a pixel
                        float joint = (1.0 - smoothstep(jw, jw * 2.0, toJoint)) * floorMask * DetailFade(p, 5.0);
                        float slabTone = lerp(1.0, lerp(0.85, 1.12, Hash01(int3(floor(slab.x), 0, floor(slab.y)))), floorMask);
                        s.albedo = base * lerp(0.65, 1.25, mottle) * lerp(0.9, 1.1, fine) * slabTone * (1.0 - joint * 0.5);
                        s.smoothness = lerp(_SmoothMin, _SmoothMax, VNoise(p * 2.0));
                        s.albedo = lerp(s.albedo, base * 1.25, wear * 0.5);               // chipped corners lighter
                        break;
                    }
                    case T_ASPHALT:
                    {
                        // Fine aggregate (fades out), a mid-scale grain that survives to ~50 m, and big
                        // features readable from far away: repair patches, oil stains, cracks.
                        float fade = DetailFade(p, 45.0);
                        float aggregate = lerp(0.3, smoothstep(0.62, 0.78, VNoise(p * 45.0)), fade);
                        float grain = lerp(0.5, VNoise(p * 6.0), DetailFade(p, 6.0));
                        // Repairs: a few rectangular patches (2.5 x 5 m grid, ~8 % of cells), slightly
                        // darker, soft-edged.
                        float2 pc = p.xz / float2(2.5, 5.0);
                        float ph = Hash01(int3(floor(pc.x), 7, floor(pc.y)));
                        float2 pf = abs(frac(pc) - 0.5);
                        float patch = step(ph, 0.08) * (1.0 - smoothstep(0.38, 0.45, max(pf.x, pf.y))) * saturate(nWS.y * 5.0 - 4.0);
                        float stain = smoothstep(0.66, 0.74, Fbm(p * 0.5 + 31.0, 3));
                        s.albedo = base * lerp(0.85, 1.6, aggregate) * lerp(0.85, 1.15, grain) * lerp(0.75, 1.25, Fbm(p * 0.25, 3))
                                 * (1.0 - patch * 0.25) * (1.0 - stain * 0.35);
                        s.smoothness = lerp(_SmoothMin, _SmoothMax, Fbm(p * 1.3, 2));
                        s.smoothness = lerp(s.smoothness, 0.45, stain);                     // oil is a little glossier
                        // Cracks: thin, jagged (a warped cell field), only in some areas.
                        float3 wp = p + (float3(Fbm(p * 0.8, 2), 0.0, Fbm(p * 0.8 + 11.0, 2)) - 0.5) * 1.2;
                        float3 vor = Voronoi(wp * 0.35);
                        float crack = (1.0 - smoothstep(0.0, 0.02, vor.y)) * smoothstep(0.58, 0.68, Fbm(p * 0.12 + 5.0, 2));
                        s.albedo *= 1.0 - crack * 0.6;
                        s.smoothness *= 1.0 - crack * 0.5;
                        break;
                    }
                    case T_PAINTED:
                    {
                        float chip = smoothstep(0.32, 0.4, wear * 1.6 - VNoise(p * 7.0) * 0.8 + 0.2);
                        s.albedo = lerp(base, _SecondColor.rgb, chip);
                        s.metallic = lerp(_Metallic, 1.0, chip);
                        s.smoothness = lerp(lerp(_SmoothMin, _SmoothMax, VNoise(p * 3.0)), 0.45, chip);
                        break;
                    }
                    case T_BRUSHED:
                    {
                        float fade = DetailFade(p, 140.0);
                        float streak = lerp(0.5, VNoise(float3(p.x * 0.6, p.y * 140.0, p.z * 140.0)), fade);
                        float scratch = smoothstep(0.93, 0.98, VNoise(float3(p.x * 1.2 + p.y * 0.8, p.y * 50.0 + p.x * 6.0, p.z * 50.0))) * fade;
                        s.metallic = 1.0;
                        s.albedo = base * lerp(0.92, 1.05, streak) + scratch * 0.15;
                        s.smoothness = lerp(_SmoothMin, _SmoothMax, streak) * (1.0 - scratch * 0.4);
                        break;
                    }
                    case T_CHROME:
                    {
                        float smudge = smoothstep(0.55, 0.8, Fbm(p * 1.5, 3));
                        s.metallic = 1.0;
                        s.smoothness = lerp(_SmoothMax, _SmoothMin, smudge);
                        break;
                    }
                    case T_GLASS:
                    {
                        s.metallic = 0.0;
                        s.smoothness = _SmoothMax;
                        s.porosity = 0.0;
                        break;
                    }
                    case T_RUBBER:
                    {
                        float f = Fbm(p * 0.8, 2);
                        s.albedo = base * lerp(0.9, 1.08, f);
                        s.smoothness = lerp(_SmoothMin, _SmoothMax, f);
                        s.porosity = 0.4;
                        break;
                    }
                    case T_CARPAINT:
                    {
                        s.smoothness = _SmoothMin;
                        s.coat = 1.0; s.coatSmooth = _SmoothMax;
                        s.porosity = 0.3;
                        // Flakes: one random micro-facet per 2 mm cell, a faint sparkle in highlights,
                        // gone (a slightly smoother base) beyond arm's length.
                        float fade = DetailFade(p, 500.0);
                        uint3 h = HashInt3((int3)floor(p * 500.0));
                        float3 r = float3(U01(h.x), U01(h.y), U01(h.z)) - 0.5;
                        s.flakeGrad = r * _Flake * fade * 0.25;
                        s.smoothness = lerp(s.smoothness, saturate(s.smoothness + 0.15), _Flake);
                        s.albedo = lerp(base, _SecondColor.rgb, wear * 0.6);
                        break;
                    }
                    case T_COTTON:
                    {
                        float weave = lerp(0.5, WeaveTri(p * COTTON_F, w), DetailFade(p, COTTON_F));
                        s.albedo = base * lerp(0.92, 1.03, weave);
                        s.albedo = lerp(s.albedo, base * 1.2 + 0.03, wear * 0.6);         // worn edges lighten
                        s.smoothness = _SmoothMin;
                        s.sheen = lerp(base, 1.0, 0.4) * _Sheen;
                        s.porosity = 0.25;
                        break;
                    }
                    case T_DENIM:
                    {
                        float twill = lerp(0.6, TwillTri(p * DENIM_F, w), DetailFade(p, DENIM_F));
                        half3 weft = base * 1.6 + 0.12;                                     // the white weft between the blue ribs
                        s.albedo = lerp(weft, base, 0.55 + 0.45 * twill);
                        float fadeMask = saturate(wear * 1.5 + (Fbm(p * 4.0, 3) - 0.5) * 0.6 * _Wear);
                        s.albedo = lerp(s.albedo, _SecondColor.rgb, fadeMask);
                        s.smoothness = _SmoothMin;
                        s.sheen = lerp(base, 1.0, 0.5) * _Sheen;
                        s.porosity = 0.3;
                        break;
                    }
                    case T_WOOL:
                    {
                        float fuzz = lerp(0.5, Fbm(p * WOOL_F, 3), DetailFade(p, WOOL_F));
                        s.albedo = base * lerp(0.82, 1.1, fuzz) * lerp(0.92, 1.05, Fbm(p * 3.0, 2));
                        s.smoothness = _SmoothMin;
                        s.sheen = lerp(base, 1.0, 0.5) * _Sheen;
                        s.porosity = 0.25;
                        break;
                    }
                    case T_LEATHER:
                    {
                        float fade = DetailFade(p, LEATHER_F);
                        float3 vor = Voronoi(p * LEATHER_F);
                        // Fine grain (cell borders slightly darker) plus broad creases darker still.
                        float grain = (1.0 - smoothstep(0.0, 0.12, vor.y)) * fade;
                        float crease = smoothstep(0.6, 0.75, Fbm(p * 3.0, 3));
                        s.albedo = base * (1.0 - grain * 0.15 - crease * 0.25) * lerp(0.95, 1.04, vor.z * fade + 0.5 * (1.0 - fade));
                        s.smoothness = lerp(_SmoothMin, _SmoothMax, saturate(wear * 1.5)) * (1.0 - crease * 0.3 - grain * 0.1);
                        s.albedo = lerp(s.albedo, base * 1.15, wear * 0.4);
                        s.sheen = 0.06 * _Sheen;
                        s.porosity = 0.2;
                        break;
                    }
                    case T_ROADPAINT:
                    {
                        // Worn through to the asphalt in patches and along the wheel-worn middle; a faint
                        // retroreflective glow keeps the markings readable at night.
                        float worn = smoothstep(0.5, 0.62, Fbm(p * 1.2, 3) * 0.75 + VNoise(p * 25.0) * 0.25 * DetailFade(p, 25.0) + _Wear * 0.25);
                        s.albedo = lerp(base, _SecondColor.rgb, worn);
                        s.smoothness = lerp(_SmoothMax, _SmoothMin, worn);
                        s.emission = base * 0.05 * (1.0 - worn);
                        s.porosity = 0.5;
                        break;
                    }
                    case T_HAZARD:
                    {
                        // 45-degree stripes, 20 cm wide, along whatever plane the surface lies in.
                        float stripe = step(0.5, frac((p.x + p.y + p.z) / 0.4));
                        float chip = smoothstep(0.32, 0.4, wear * 1.6 - VNoise(p * 7.0) * 0.8 + 0.2);
                        s.albedo = lerp(lerp(_SecondColor.rgb, base, stripe), half3(0.5, 0.5, 0.52), chip);
                        s.metallic = chip;
                        s.smoothness = lerp(_SmoothMax, 0.45, chip);
                        break;
                    }
                    case T_VINYL:
                    {
                        float f = Fbm(p * 0.6, 2);
                        s.smoothness = lerp(_SmoothMin, _SmoothMax, 0.6 + 0.4 * f);
                        s.porosity = 0.0;
                        break;
                    }
                }

                // Grime: darker and rougher in cavities, and (city surfaces) dust on top and streaks below edges.
                float g = cavity * _Grime;
                s.albedo *= 1.0 - g * 0.5;
                s.smoothness *= 1.0 - g * 0.4;
                if (hasBox && _Grime > 0.0)
                {
                    float streak = Streaks(p, box.top) * _Grime;
                    s.albedo *= 1.0 - streak * 0.45;
                    s.albedo = lerp(s.albedo, half3(0.2, 0.2, 0.19), Dust(nWS, p) * _Grime * 0.25 * (type <= T_CHROME ? 1.0 : 0.0));
                }
                return s;
            }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                int type = (int)_Type;
                float3 nGeom = normalize(i.normalWS);
                float3 v = GetWorldSpaceNormalizeViewDir(i.positionWS);
                float3 p = i.patternPos * _DetailScale;
                float3 w = TriWeights(normalize(i.patternNrm));
                float3x3 toWorld = _Space < 0.5 ? float3x3(1, 0, 0, 0, 1, 0, 0, 0, 1) : (float3x3)GetObjectToWorldMatrix();

                // Edges and cavities.
                BoxEdges box = (BoxEdges)0;
                bool hasBox = false;
                float edge = 0.0, cavity = 0.0, contact = 0.0;
                bool skinned = false;
                if (_EdgeSource > 0.5 && _EdgeSource < 1.5)
                {
                    box = EdgeFromUV(i.uv, i.positionWS);
                    hasBox = true;
                    edge = 1.0 - smoothstep(0.0, _EdgeWidth, box.edge);
                    cavity = exp(-box.bottom * 2.5) * 0.6;                                  // contact grime at the foot
                }
                else if (_EdgeSource < 2.5 && _EdgeSource > 0.5)
                {
                    // Baked per vertex (BodySurfaceBake). A mesh without colours reads white: ignore it.
                    half4 vc = dot(i.color, 1.0) > 3.99 ? half4(0, 0, 0, 0) : i.color;
                    edge = vc.r; cavity = vc.g; contact = vc.b; skinned = true;
                }
                else if (_EdgeSource > 2.5)
                {
                    float k = CurvatureFromDerivatives(nGeom, i.positionWS);
                    edge = saturate((k - 2.0) * 0.25); cavity = saturate(-k * 0.2);
                }
                // Break the edge up so wear isn't a clean line.
                edge *= smoothstep(0.2, 0.7, VNoise(p * 5.0) * 0.6 + edge * 0.6);

                Surf s = Evaluate(type, p, w, edge, cavity, box, hasBox, nGeom);

                // Garments (Phase 3): darker fold valleys, hem stitching ~1 cm in from every opening,
                // side seams (a darker groove with a stitch line each side), all fading with distance.
                if (skinned)
                {
                    s.albedo *= 1.0 - cavity * 0.35;
                    if (type >= T_COTTON && type <= T_LEATHER)
                    {
                        half3 thread = type == T_DENIM ? half3(0.75, 0.55, 0.22) : saturate(_BaseColor.rgb * 1.4 + 0.04);
                        float fade = DetailFade(p, 150.0);
                        float dash = step(0.4, frac(dot(p, float3(1.0, 1.3, 0.7)) * 90.0));
                        float hemD = i.uv.x;
                        float hw = 0.0008 + fwidth(hemD);
                        float hem = 1.0 - smoothstep(hw, hw * 2.0, abs(hemD - 0.012));
                        float seamC = i.uv.y;
                        float sw = 0.012 + fwidth(seamC);
                        float groove = (1.0 - smoothstep(sw, sw * 2.0, abs(seamC))) * step(abs(seamC), 0.99);
                        float seamStitch = (1.0 - smoothstep(sw * 0.5, sw, abs(abs(seamC) - 0.06))) * step(abs(seamC), 0.99);
                        s.albedo *= 1.0 - groove * 0.3 * fade;
                        s.albedo = lerp(s.albedo, thread, saturate(hem + seamStitch) * dash * fade * 0.8);
                    }
                }

                // Detail normal: finite differences of the type's height, fading with distance.
                float freq = type == T_COTTON ? COTTON_F : type == T_DENIM ? DENIM_F : type == T_WOOL ? WOOL_F : type == T_LEATHER ? LEATHER_F : type == T_BRUSHED ? 140.0 : type == T_PAINTED ? 80.0 : 40.0;
                float fade = DetailFade(p, freq);
                float3 n = nGeom;
                float strength = _DetailStrength * fade * (1.0 - _QualityDetailCut);
                if (strength > 0.01 && type != T_CHROME && type != T_GLASS && type != T_VINYL)
                {
                    float e = 0.25 / freq;
                    float h0 = HeightOf(type, p, w);
                    float3 g = float3(HeightOf(type, p + float3(e, 0, 0), w) - h0,
                                      HeightOf(type, p + float3(0, e, 0), w) - h0,
                                      HeightOf(type, p + float3(0, 0, e), w) - h0) / e;
                    n = DetailNormal(n, (g / freq) * strength * 4.0 + s.flakeGrad, toWorld);
                }
                else if (type == T_CARPAINT)
                    n = DetailNormal(n, s.flakeGrad, toWorld);

                // Wetness.
                Wet wet = Wetness(i.positionWS, nGeom, s.porosity);
                ApplyWetness(wet, s.albedo, s.smoothness, n, nGeom);

                // Lighting: URP PBR with the clear coat, SSAO, every light, probes, fog; plus sheen.
                half alpha = 1.0;
                BRDFData brdf;
                InitializeBRDFData(s.albedo, s.metallic, half3(0, 0, 0), s.smoothness, alpha, brdf);
                BRDFData coat;
                InitializeBRDFDataClearCoat(s.coat, s.coatSmooth, brdf, coat);

                float2 suv = GetNormalizedScreenSpaceUV(i.positionCS);
                AmbientOcclusionFactor ao = GetScreenSpaceAmbientOcclusion(suv);
                InputData inputData = (InputData)0;
                inputData.positionWS = i.positionWS;
                inputData.normalWS = n;
                inputData.normalizedScreenSpaceUV = suv;

                half3 sh = SampleSH(n);
                // Contact shadow (garment hems, baked): most of the ambient, a little of the direct light.
                half occlusion = ao.indirectAmbientOcclusion * (1.0 - contact * 0.7);
                half3 color = GlobalIllumination(brdf, coat, s.coat, sh, occlusion, i.positionWS, n, v, suv);
                half3 sheenLight = sh * occlusion;

                Light mainLight = GetMainLight(TransformWorldToShadowCoord(i.positionWS));
                mainLight.color *= ao.directAmbientOcclusion * (1.0 - contact * 0.3);
                color += LightingPhysicallyBased(brdf, coat, mainLight, n, v, s.coat, false);
                sheenLight += mainLight.color * mainLight.shadowAttenuation * saturate((dot(n, mainLight.direction) + 0.5) / 1.5);

                #if defined(_ADDITIONAL_LIGHTS)
                    uint pixelLightCount = GetAdditionalLightsCount();
                    #if USE_CLUSTER_LIGHT_LOOP
                    [loop] for (uint li = 0; li < min(URP_FP_DIRECTIONAL_LIGHTS_COUNT, MAX_VISIBLE_LIGHTS); li++)
                    {
                        Light dl = GetAdditionalLight(li, i.positionWS, half4(1, 1, 1, 1));
                        color += LightingPhysicallyBased(brdf, coat, dl, n, v, s.coat, false);
                        sheenLight += dl.color * dl.distanceAttenuation * dl.shadowAttenuation * saturate((dot(n, dl.direction) + 0.5) / 1.5);
                    }
                    #endif
                    LIGHT_LOOP_BEGIN(pixelLightCount)
                        Light al = GetAdditionalLight(lightIndex, i.positionWS, half4(1, 1, 1, 1));
                        color += LightingPhysicallyBased(brdf, coat, al, n, v, s.coat, false);
                        sheenLight += al.color * al.distanceAttenuation * al.shadowAttenuation * saturate((dot(n, al.direction) + 0.5) / 1.5);
                    LIGHT_LOOP_END
                #endif

                // Fabric sheen: brightens at grazing angles (fibres catching the light).
                half nv = saturate(dot(n, v));
                color += s.sheen * pow(1.0 - nv, 4.0) * sheenLight;
                color += _EmissionColor.rgb + s.emission;
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
