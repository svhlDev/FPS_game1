#ifndef FPS_SURFACE_LIB_INCLUDED
#define FPS_SURFACE_LIB_INCLUDED

// Graphics Phase 2: the shared surface library. Procedural only (no textures, no UVs needed for the
// patterns), integer hashing only (HashPCG), every pattern fades to its average below ~1 pixel.
//
//   Noise:      VNoise (value), Fbm, Voronoi (F1, F2 - F1), all on the integer lattice.
//   Triplanar:  TriWeights + the 2D fabric / streak patterns, projected in the pattern space the caller
//               picks: world (static city), object (rigid movers), rest pose (skinned: TEXCOORD3).
//   Normals:    DetailNormal (a pattern-space height gradient from finite differences, onto the surface
//               normal).
//   Wear:       EdgeFromUV (convex edges of boxes from their per-face UVs and screen derivatives, so it
//               survives static batching), CurvatureFromDerivatives (any smooth mesh), dust (up-facing),
//               streaks (down from top edges), cavity grime (vertex data, or near bottom edges).
//   Fade:       DetailFade (pattern frequency against the pixel footprint).
//   Wetness:    _GlobalWetness: darker, smoother, puddles on up-facing surfaces.

#include "HashPCG.hlsl"

float _GlobalWetness;
// Graphics quality (GraphicsQuality.cs): fractions cut, 0 = full quality (the default when never set).
float _QualityDetailCut, _QualityInteriorOff, _QualityDecalCut;

// ---------- noise ----------

float Hash01(int3 c) { return U01(HashInt3(c).x); }

// Value noise in [0, 1].
float VNoise(float3 p)
{
    float3 i = floor(p);
    float3 f = p - i;
    float3 u = f * f * (3.0 - 2.0 * f);
    int3 c = (int3)i;
    float a = Hash01(c),                b = Hash01(c + int3(1, 0, 0));
    float d = Hash01(c + int3(0, 1, 0)), e = Hash01(c + int3(1, 1, 0));
    float g = Hash01(c + int3(0, 0, 1)), h = Hash01(c + int3(1, 0, 1));
    float k = Hash01(c + int3(0, 1, 1)), l = Hash01(c + int3(1, 1, 1));
    return lerp(lerp(lerp(a, b, u.x), lerp(d, e, u.x), u.y), lerp(lerp(g, h, u.x), lerp(k, l, u.x), u.y), u.z);
}

// Fractal value noise in [0, 1], `octaves` up to 5.
float Fbm(float3 p, int octaves)
{
    float sum = 0.0, amp = 0.5, norm = 0.0;
    [unroll(5)] for (int o = 0; o < octaves; o++)
    {
        sum += VNoise(p) * amp; norm += amp;
        p = p * 2.03 + float3(17.1, 9.3, 4.7);
        amp *= 0.5;
    }
    return sum / norm;
}

// Cell noise: x = distance to the nearest feature point, y = F2 - F1 (0 on the cell borders: cracks,
// grain), z = the nearest cell's hash.
float3 Voronoi(float3 p)
{
    float3 i = floor(p);
    float3 f = p - i;
    int3 c = (int3)i;
    float f1 = 8.0, f2 = 8.0, id = 0.0;
    [unroll] for (int z = -1; z <= 1; z++)
    [unroll] for (int y = -1; y <= 1; y++)
    [unroll] for (int x = -1; x <= 1; x++)
    {
        int3 o = int3(x, y, z);
        uint3 h = HashInt3(c + o);
        float3 fp = float3(o) + float3(U01(h.x), U01(h.y), U01(h.z)) - f;
        float d = dot(fp, fp);
        if (d < f1) { f2 = f1; f1 = d; id = U01(h.x ^ h.z); }
        else if (d < f2) f2 = d;
    }
    f1 = sqrt(f1); f2 = sqrt(f2);
    return float3(f1, f2 - f1, id);
}

// ---------- distance fade ----------

// 1 while a pattern of `freq` cycles per unit of p is bigger than a few pixels, fading to 0 as one cycle
// shrinks below ~1 pixel (the caller lerps the detail toward its average). Same idea as the facade's
// window fade.
float DetailFade(float3 p, float freq)
{
    float px = length(fwidth(p)) * freq;
    return 1.0 - smoothstep(0.25, 0.7, px);
}

// ---------- triplanar ----------

float3 TriWeights(float3 n)
{
    float3 w = pow(abs(n), 4.0);
    return w / max(w.x + w.y + w.z, 1e-4);
}

// 2D patterns, all in [0, 1]. uv in pattern cycles.
float WeavePattern(float2 uv)
{
    // Plain weave: warp and weft threads crossing over and under each other.
    float2 f = frac(uv);
    float2 cell = floor(uv);
    float over = fmod(abs(cell.x + cell.y), 2.0);
    float warp = sin(f.x * PI), weft = sin(f.y * PI);
    return lerp(warp * (0.6 + 0.4 * weft), weft * (0.6 + 0.4 * warp), over);
}

float TwillPattern(float2 uv)
{
    // 2/1 twill: diagonal ribs.
    float d = frac((uv.x + uv.y * 2.0) / 3.0);
    float rib = smoothstep(0.0, 0.25, d) * (1.0 - smoothstep(0.6, 0.9, d));
    float thread = 0.75 + 0.25 * sin(frac(uv.y) * PI);
    return rib * thread;
}

float WeaveTri(float3 p, float3 w) { return WeavePattern(p.yz) * w.x + WeavePattern(p.xz) * w.y + WeavePattern(p.xy) * w.z; }
float TwillTri(float3 p, float3 w) { return TwillPattern(p.yz) * w.x + TwillPattern(p.xz) * w.y + TwillPattern(p.xy) * w.z; }

// ---------- detail normal ----------

// Perturbs n (world) by the height gradient g (pattern space, already scaled by the strength); `toWorld`
// maps pattern-space directions to world (identity for world space).
float3 DetailNormal(float3 n, float3 g, float3x3 toWorld)
{
    float3 gw = mul(toWorld, g);
    gw -= n * dot(gw, n);
    return normalize(n - gw);
}

// ---------- wear and grime inputs ----------

// Box edges from per-face UVs (Unity primitives and the builders' slabs: 0..1 across each face): the
// metres to the nearest face border, solved from screen derivatives (dP/du, dP/dv), so it still works
// after static batching has baked the scale into the vertices. Also returns the metres to the top border
// (for streaks) and to the bottom border (contact grime) on vertical faces, and the face size.
struct BoxEdges { float edge; float top; float bottom; float2 size; float2 uvMetres; float acrossIsU; };

BoxEdges EdgeFromUV(float2 uv, float3 posWS)
{
    BoxEdges b;
    float3 dp1 = ddx(posWS), dp2 = ddy(posWS);
    float2 du1 = ddx(uv), du2 = ddy(uv);
    float det = du1.x * du2.y - du1.y * du2.x;
    float invDet = abs(det) > 1e-12 ? 1.0 / det : 0.0;
    float3 dpdu = (dp1 * du2.y - dp2 * du1.y) * invDet;
    float3 dpdv = (dp2 * du1.x - dp1 * du2.x) * invDet;
    float2 size = float2(length(dpdu), length(dpdv));
    float2 m = min(uv, 1.0 - uv) * size;
    b.edge = min(m.x, m.y);
    // Which UV axis runs up the face (vertical faces), and which way.
    float upU = dpdu.y / max(size.x, 1e-4), upV = dpdv.y / max(size.y, 1e-4);
    bool vUp = abs(upV) >= abs(upU);
    float t = vUp ? (upV > 0.0 ? 1.0 - uv.y : uv.y) : (upU > 0.0 ? 1.0 - uv.x : uv.x);
    float sz = vUp ? size.y : size.x;
    float vertical = saturate(max(abs(upU), abs(upV)) * 2.0 - 0.5);
    b.top = lerp(1e4, t * sz, vertical);
    b.bottom = lerp(1e4, (1.0 - t) * sz, vertical);
    b.size = size;
    b.uvMetres = uv * size;
    b.acrossIsU = size.x < size.y ? 1.0 : 0.0;
    return b;
}

// Convexity of a smooth mesh from screen derivatives: > 0 on convex bends (edges of rounded boxes,
// knuckles), < 0 in concave ones. Unitless (1/m times the bend size); noisy, so callers smooth it.
float CurvatureFromDerivatives(float3 n, float3 posWS)
{
    float3 dn1 = ddx(n), dn2 = ddy(n);
    float3 dp1 = ddx(posWS), dp2 = ddy(posWS);
    float k = (dot(dn1, dp1) + dot(dn2, dp2)) / max(dot(dp1, dp1) + dot(dp2, dp2), 1e-8);
    return k;
}

// Up-facing dust in [0, 1].
float Dust(float3 nWS, float3 p) { return saturate(nWS.y * 1.4 - 0.4) * (0.6 + 0.4 * Fbm(p * 0.7, 2)); }

// Rain-like streaks running down from a top edge, in [0, 1] (`top`: metres below the top edge).
float Streaks(float3 p, float top)
{
    float s = VNoise(float3(p.x * 3.1 + p.z * 2.3, p.y * 0.12, p.z * 3.1 - p.x * 2.3));
    return smoothstep(0.55, 0.85, s) * exp(-top * 0.6);
}

// ---------- wetness ----------

struct Wet { float amount; float puddle; };

// Puddles: low-frequency noise on up-facing surfaces (world space so they line up across pieces).
Wet Wetness(float3 posWS, float3 nWS, float porosity)
{
    Wet w;
    w.amount = saturate(_GlobalWetness);
    float up = saturate(nWS.y * 5.0 - 4.0);
    float m = Fbm(float3(posWS.x, 0.0, posWS.z) * 0.18, 3);
    w.puddle = w.amount * up * smoothstep(0.52, 0.6, m + (w.amount - 1.0) * 0.2);
    w.amount *= porosity;
    return w;
}

void ApplyWetness(Wet w, inout half3 albedo, inout half smoothness, inout float3 n, float3 nGeom)
{
    float wet = max(w.amount, w.puddle);
    albedo *= lerp(1.0, 0.55, wet);
    smoothness = lerp(smoothness, 0.93, w.amount);
    smoothness = lerp(smoothness, 0.98, w.puddle);
    n = normalize(lerp(n, nGeom, w.puddle));
}

#endif
