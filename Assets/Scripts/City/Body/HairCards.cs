using System;
using System.Collections.Generic;
using UnityEngine;

// Face Fix Part 9: hair as cards (the standard game technique): many narrow ribbons of polygons along
// guide strands, each drawn with procedural strands by FPS/Hair.
//   Scalp: roots scattered (blue noise: dart throwing over Fibonacci directions) on the skin inside the
//   hairline (outside the face window, above the nape line, above the ears).
//   Direction field: away from the parting and down the head, plus gravity; short hair lies close to
//   the scalp (each step pulled onto the tangent plane), long hair falls to its length.
//   Guide strands: stepped along the field, pushed out of the head / neck / shoulders (the body's SDF),
//   to a per-region length (fringe, top, sides, back).
//   Clumping: strands pull toward their clump's centre strand near the tips; a little wave per clump.
//   Cards: a ribbon per strand facing out from the head, twisting slightly, tapering to the tip. Two
//   layers: wide inner cards close to the scalp, narrower outer ones a little out for the silhouette
//   (the old shell, thin and darkened, stays under them: no scalp shows, and it is the far LOD).
// Long hair below the nape is skinned to strand-group spring chains (a head-anchored ClothChainSet,
// simulated by ClothDangle); everything else to the head.
[Serializable]
public class HairStyle
{
    public string name;
    [Tooltip("The parting: its x on the head (fractions of the half width), and how strongly hair combs away from it.")]
    public float parting = 0.25f, partingPull = 1f;
    [Tooltip("Lengths (m): fringe, top, sides, back.")]
    public float fringe = 0.04f, top = 0.05f, sides = 0.03f, back = 0.035f;
    [Tooltip("Gravity's pull (0 = lies along the scalp, 1 = falls), lift off the scalp on top (m), at the sides.")]
    public float gravity = 0.15f, liftTop = 0.008f, liftSides = 0.003f;
    [Tooltip("Comb direction on top: + forward (a fringe), - back.")]
    public float forward = -0.4f;
    [Tooltip("The nape line (fractions of the half height from the eye line; roots above it).")]
    public float nape = -0.45f;
    public int roots = 300, segments = 5;
    [Tooltip("Card width at the root (m), inner and outer layers; the outer layer's share of the strands.")]
    public float innerWidth = 0.022f, outerWidth = 0.014f, outerShare = 0.55f;
    public float clump = 0.5f, curl = 0.003f;
    [Tooltip("Spring chains for the hair below the nape (long hair).")]
    public bool chains;

    // (Face Polish Part 6: more, narrower outer cards - wide outer cards showed as flat shards.)
    public static HairStyle Short() => new HairStyle { name = "Short", roots = 420, outerWidth = 0.009f, outerShare = 0.65f };
    public static HairStyle Long() => new HairStyle
    {
        name = "Long", parting = 0.2f, fringe = 0.1f, top = 0.3f, sides = 0.3f, back = 0.34f, gravity = 0.85f, liftTop = 0.006f, liftSides = 0.006f,
        forward = -0.6f, nape = -0.55f, roots = 420, segments = 9, innerWidth = 0.026f, outerWidth = 0.011f, outerShare = 0.6f, clump = 0.6f, curl = 0.006f,
        chains = true,
    };

    // The card style for an old wig style (null: that style keeps its shell only, for now).
    public static HairStyle For(WigStyle s) => s == WigStyle.Crop ? Short() : s == WigStyle.Long ? Long() : null;
}

public static class HairCards
{
    static float Hash01(int i) { uint h = (uint)i * 2654435761u; h ^= h >> 15; h *= 2246822519u; h ^= h >> 13; return (h & 0xffffff) / 16777216f; }

    // The light spring preset for strand groups.
    static SkirtDef ChainDef() => new SkirtDef
    {
        panels = 9, bonesPerPanel = 3, stiffnessRoot = 12f, stiffnessTip = 2.5f, damping = 0.88f, gravityScale = 1f, drag = 1.1f,
        maxAngle = 60f, neighbour = 0.35f, simDistance = 25f, slitAngles = new float[0],
    };

    // The strand-group chains of long hair: panels round the sides and back of the head (none in front of
    // the face), from a ring at the ears' bottom down to the hair's tips, just outside the head, neck and
    // shoulders. firstBone: the renderer bone index of its first bone.
    public static ClothChainSet Chains(BodySdfKernel k, HairStyle st, HeadCanon c, int firstBone)
    {
        var d = ChainDef();
        var cs = new ClothChainSet { panels = d.panels, bones = d.bonesPerPanel, def = d, firstBone = firstBone, anchor = "Head", garment = int.MaxValue,
                                     gaps = new[] { 0f }, slitTopY = -10f };
        // Round the sides and back in order (none in front of the face; the front gap is at 0).
        cs.angles = new[] { 45f, 80f, 115f, 150f, 180f, -150f, -115f, -80f, -45f };
        cs.panels = cs.angles.Length;
        Vector3 axisP = c.o - c.z * c.halfD * 0.2f;
        cs.axis = new Vector2(axisP.x, axisP.z);
        cs.ringY = c.o.y + c.noseBase;
        cs.hemY = cs.ringY - (st.back - (c.crown - c.noseBase) * 0.6f);
        cs.joints = new Vector3[cs.panels * (cs.bones + 1)];
        k.cutMode = 0;
        for (int p = 0; p < cs.panels; p++)
        {
            float a = cs.angles[p] * Mathf.Deg2Rad;
            Vector3 dir = (c.x * Mathf.Sin(a) + c.z * Mathf.Cos(a)).normalized;
            for (int l = 0; l <= cs.bones; l++)
            {
                float y = Mathf.Lerp(cs.ringY, cs.hemY, l / (float)cs.bones);
                Vector3 centre = new Vector3(axisP.x, y, axisP.z);
                // Outward to the surface (head, neck, shoulders), then a little past it.
                float r = 0.03f;
                for (float t = 0.03f; t < 0.4f; )
                {
                    float sd = k.Skin(centre + dir * t);
                    r = t;
                    if (sd > 0f) break;
                    t += Mathf.Max(-sd * 0.9f, 0.002f);   // (inside: step by the distance to the surface)
                }
                cs.joints[p * (cs.bones + 1) + l] = centre + dir * (r + 0.012f);
            }
        }
        return cs;
    }

    // Builds the cards. headBone: the renderer bone index of the head; chains: long hair's chains or null.
    // Positions in root space (rest); tangents along the strand (w = 1), uv (across 0..1, root 0 .. tip 1),
    // uv2.x a per-card random (colour jitter).
    public static bool Build(BodySdfKernel k, HairStyle st, int seed, HeadCanon c, int headBone, ClothChainSet chains,
                             out Vector3[] verts, out Vector3[] normals, out Vector4[] tangents, out Vector2[] uv, out Vector2[] uv2, out BoneWeight[] weights, out int[] tris)
    {
        verts = null; normals = null; tangents = null; uv = null; uv2 = null; weights = null; tris = null;
        k.cutMode = 0;
        var sk = k.skull;
        var rng = new PcgRandom(seed, "hairCards");
        Vector3 U(Vector3 p) { Vector3 d = p - c.o; return new Vector3(Vector3.Dot(d, c.x) / c.halfW, Vector3.Dot(d, c.y) / c.halfH, Vector3.Dot(d, c.z) / c.halfD); }
        // The scalp inside the hairline (the wigs' face window, the nape line, not the ears).
        float Window(Vector3 u) => Mathf.Min(u.z - 0.05f, 1f - new Vector2(Mathf.Abs(u.x) / 0.86f, (u.y - 0.05f) / 0.72f).magnitude);
        bool Scalp(Vector3 u)
        {
            float ax = Mathf.Abs(u.x);
            float window = Window(u);
            if (window > -0.03f) return false;                       // the face
            if (u.y < st.nape) return false;
            if (ax > 0.7f && u.y < 0.15f && u.z > -0.6f) return false;     // round and below the ears (no sideburns hanging down)
            return true;
        }
        // Roots: Fibonacci directions onto the skin, then dart throwing to the target count.
        var cand = new List<Vector3>();
        const int N = 1400;
        Vector3 hc = c.o + c.y * c.halfH * 0.2f;
        for (int i = 0; i < N; i++)
        {
            float yy = 1f - 2f * (i + 0.5f) / N, rr = Mathf.Sqrt(1f - yy * yy), ph = i * 2.39996323f;
            Vector3 dir = (c.x * (Mathf.Cos(ph) * rr) + c.y * yy + c.z * (Mathf.Sin(ph) * rr)).normalized;
            if (Vector3.Dot(dir, c.y) < -0.75f) continue;
            Vector3 p = hc + dir * (c.halfH * 1.6f);
            bool hit = false;
            for (int it = 0; it < 80; it++)
            {
                float d = k.Skin(p);
                if (d < 0.0005f) { hit = true; break; }
                p -= dir * Mathf.Clamp(d * 0.9f, 0.0005f, 0.01f);
            }
            if (hit && Scalp(U(p))) cand.Add(p);
        }
        if (cand.Count < 20) return false;
        // Blue noise: shuffle, keep candidates no closer than the spacing the target count implies.
        for (int i = cand.Count - 1; i > 0; i--) { int j = rng.Range(0, i + 1); var t = cand[i]; cand[i] = cand[j]; cand[j] = t; }
        // (The spacing shrinks until the target count fits.)
        var roots = new List<Vector3>();
        for (float spacing = 0.02f; spacing > 0.002f; spacing *= 0.88f)
        {
            roots.Clear();
            foreach (var p in cand)
            {
                bool ok = true;
                foreach (var q in roots) if ((p - q).sqrMagnitude < spacing * spacing) { ok = false; break; }
                if (ok) roots.Add(p);
                if (roots.Count >= st.roots) break;
            }
            if (roots.Count >= st.roots) break;
        }
        // Guide strands.
        int S = Mathf.Max(2, st.segments);
        var strands = new List<Vector3[]>();
        var layer = new List<bool>();   // outer?
        foreach (var root in roots)
        {
            bool outer = rng.Value() < st.outerShare;
            Vector3 u0 = U(root);
            float ax = Mathf.Abs(u0.x);
            // Length by region.
            float frontness = Mathf.Clamp01((u0.z - 0.2f) / 0.5f) * Mathf.Clamp01((u0.y - 0.2f) / 0.3f);
            float backness = Mathf.Clamp01(-u0.z / 0.6f), sideness = Mathf.Clamp01((ax - 0.5f) / 0.4f) * (1f - backness);
            float topness = Mathf.Clamp01((u0.y - 0.5f) / 0.4f);
            float len = Mathf.Lerp(Mathf.Lerp(Mathf.Lerp(st.top, st.sides, sideness), st.back, backness), st.fringe, frontness);
            len = Mathf.Lerp(len, st.top, topness * (1f - frontness)) * (1f + 0.15f * rng.Signed());
            float lift = Mathf.Lerp(st.liftSides, st.liftTop, topness) + (outer ? 0.003f : 0f) + 0.0025f;
            var pts = new Vector3[S + 1];
            Vector3 p = root + k.Normal(root, 0.002f) * lift;
            pts[0] = p;
            // The comb direction: away from the parting, down the head, forward / back on top.
            float side = Mathf.Sign(u0.x - st.parting + 1e-4f);
            // (The fringe combs back off the forehead with the top, not down over it.)
            float topFront = Mathf.Max(topness, frontness);
            Vector3 comb = (c.x * side * st.partingPull * (1f - backness) - c.y * (0.6f + backness) * (1f - frontness) + c.z * st.forward * topFront).normalized;
            for (int s = 1; s <= S; s++)
            {
                float t = s / (float)S;
                Vector3 n = k.Normal(p, 0.002f);
                Vector3 along = Vector3.ProjectOnPlane(comb, n).normalized;
                Vector3 dir = Vector3.Lerp(along, -c.y, st.gravity * t).normalized;
                // Off the scalp, falling hair drops and hangs in a little toward the body (no mane).
                if (st.gravity > 0.5f && k.Skin(p) > lift * 1.5f)
                {
                    Vector3 radial = Vector3.ProjectOnPlane(p - hc, c.y).normalized;
                    dir = Vector3.Lerp(dir, (-c.y - radial * 0.2f).normalized, 0.7f).normalized;
                }
                p += dir * (len / S);
                // Out of the head / neck / shoulders, at the strand's lift.
                for (int it = 0; it < 4; it++)
                {
                    float d = k.Skin(p);
                    if (d >= lift) break;
                    p += k.Normal(p, 0.002f) * (lift - d);
                }
                pts[s] = p;
            }
            // (Face Polish Part 6: a strand lying on or hanging in front of the face - any point in the face
            // window below the forehead, or anywhere in front of the face down to the chin - is a stray on the
            // cheek: dropped. Sideburns are the beard's.)
            bool onFace = false;
            for (int s = 1; s <= S && !onFace; s++) { Vector3 us = U(pts[s]); onFace = (Window(us) > -0.02f && us.y < 0.35f) || (us.z > 0f && us.y < 0.1f && us.y > -1.15f && Mathf.Abs(us.x) < 0.95f); }
            if (onFace) continue;
            strands.Add(pts); layer.Add(outer);
        }
        // Clumps: each strand pulls toward the nearest of every ~5th strand near the tips; a wave per clump.
        var centres = new List<int>();
        for (int i = 0; i < strands.Count; i += 5) centres.Add(i);
        var clumped = new List<Vector3[]>(); var clumpOf = new List<int>();
        for (int i = 0; i < strands.Count; i++)
        {
            int best = centres.Count > 0 ? centres[0] : i; float bd = float.MaxValue;
            foreach (int ci in centres) { float d = (strands[ci][0] - strands[i][0]).sqrMagnitude; if (d < bd) { bd = d; best = ci; } }
            var a = strands[i]; var b = strands[best];
            var o = new Vector3[a.Length];
            float phase = best * 1.7f;
            for (int s = 0; s <= S; s++)
            {
                float t = s / (float)S;
                o[s] = Vector3.Lerp(a[s], b[s], st.clump * t * t);
                if (s > 0)
                {
                    Vector3 tan = (o[s] - o[s - 1]).normalized;
                    Vector3 perp = Vector3.Cross(tan, (o[s] - hc).normalized);
                    o[s] += perp * Mathf.Sin(t * 9f + phase) * st.curl * t;
                }
            }
            clumped.Add(o); clumpOf.Add(best);
        }
        // Cards.
        var V = new List<Vector3>(); var Nn = new List<Vector3>(); var T4 = new List<Vector4>(); var UV = new List<Vector2>(); var UV2 = new List<Vector2>();
        var W = new List<BoneWeight>(); var Tr = new List<int>();
        var headOnly = new BoneWeight { boneIndex0 = headBone, weight0 = 1f };
        for (int i = 0; i < clumped.Count; i++)
        {
            var pts = clumped[i];
            float w0 = (layer[i] ? st.outerWidth : st.innerWidth) * (1f + 0.25f * rng.Signed());
            // (Colour jitter: mostly the clump's, a little the card's.)
            float twist = 0.4f * rng.Signed(), rnd = Mathf.Lerp(Hash01(clumpOf[i]), rng.Value(), 0.35f);
            int baseV = V.Count;
            for (int s = 0; s <= S; s++)
            {
                float t = s / (float)S;
                Vector3 tan = (s < S ? pts[s + 1] - pts[s] : pts[s] - pts[s - 1]).normalized;
                Vector3 outward = Vector3.ProjectOnPlane((pts[s] - hc).normalized, tan).normalized;
                Vector3 sideV = Vector3.Cross(tan, outward).normalized;
                sideV = Quaternion.AngleAxis(twist * t * Mathf.Rad2Deg, tan) * sideV;
                Vector3 nrm = Vector3.Cross(sideV, tan).normalized;
                // (Lit as part of the hair's volume: the normal bent out from the head's centre.)
                Vector3 radial = (pts[s] - hc).normalized;
                if (Vector3.Dot(nrm, radial) < 0f) nrm = -nrm;
                nrm = Vector3.Lerp(nrm, radial, 0.6f).normalized;
                float w = w0 * (1f - 0.7f * t);
                for (int e = 0; e < 2; e++)
                {
                    Vector3 q = pts[s] + sideV * (e == 0 ? -0.5f : 0.5f) * w;
                    V.Add(q); Nn.Add(nrm); T4.Add(new Vector4(tan.x, tan.y, tan.z, 1f));
                    UV.Add(new Vector2(e, t)); UV2.Add(new Vector2(rnd, layer[i] ? 1f : 0f));
                    BoneWeight bw = headOnly;
                    if (chains != null) { var cw = chains.Weight(q, headOnly, headBone); if (cw.HasValue) bw = cw.Value; }
                    W.Add(bw);
                }
                if (s < S)
                {
                    int a = baseV + s * 2;
                    Tr.Add(a); Tr.Add(a + 2); Tr.Add(a + 1);
                    Tr.Add(a + 1); Tr.Add(a + 2); Tr.Add(a + 3);
                }
            }
        }
        verts = V.ToArray(); normals = Nn.ToArray(); tangents = T4.ToArray(); uv = UV.ToArray(); uv2 = UV2.ToArray(); weights = W.ToArray(); tris = Tr.ToArray();
        return tris.Length > 0;
    }
}
