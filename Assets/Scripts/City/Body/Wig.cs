using System.Collections.Generic;
using UnityEngine;

// Hair as a wig (replaces Head spec Part E's garment hair): a rigid mesh generated per body from its own
// small distance field around the egg head, set on the head joint ("plop it on"). Styles by seed and sex;
// every style keeps the face window clear and stays off the neck and shoulders, so there's nothing to
// clip or drape there.
//   Buzz      a thin cap (4 mm)
//   Crop      a short cap, fuller on top, a side part
//   Bob       the cap, hanging straight down to the jaw at the sides and back
//   Long      the cap, falling behind the neck to the upper back, leaning back to clear it
//   Ponytail  a crop, plus a tapered tail from the back of the crown
//   Bun       a crop, plus a ball at the back of the crown
public enum WigStyle { None, Buzz, Crop, Bob, Long, Ponytail, Bun }

public static class WigBuilder
{
    public const float Cell = 0.005f;
    public const int TargetTriangles = 2200;

    public static WigStyle? Force;   // test hook: every wig this style

    public static WigStyle Pick(CharacterSheet sheet)
    {
        if (Force.HasValue) return Force.Value;
        var r = new PcgRandom(sheet.seed, "wig");
        float v = r.Value();
        if (sheet.sex == Sex.Male)
            return v < 0.1f ? WigStyle.None : v < 0.35f ? WigStyle.Buzz : v < 0.8f ? WigStyle.Crop : v < 0.9f ? WigStyle.Ponytail : WigStyle.Bob;
        return v < 0.12f ? WigStyle.Crop : v < 0.4f ? WigStyle.Bob : v < 0.7f ? WigStyle.Long : v < 0.85f ? WigStyle.Ponytail : WigStyle.Bun;
    }

    // The wig's mesh in the head joint's space (its rest frame is unrotated: subtract the joint's rest
    // position). k: the body's kernel (the egg: skull frame, radii, egg scales). Safe on a worker thread.
    public static bool Build(BodySdfKernel k, WigStyle style, int seed, Vector3 headJoint, out Vector3[] verts, out Vector3[] normals, out int[] tris)
    {
        verts = null; normals = null; tris = null;
        if (style == WigStyle.None) return false;
        var sk = k.skull;
        Vector3 R = sk.r;
        var rng = new PcgRandom(seed, "wigShape");
        float part = (rng.Value() < 0.5f ? -1f : 1f) * rng.Range(0.002f, 0.005f);
        float thick = style == WigStyle.Buzz ? 0.009f : rng.Range(0.01f, 0.015f);
        float top = style == WigStyle.Buzz ? 0f : rng.Range(0.006f, 0.014f);
        const float gap = 0.0015f;
        // The scalp lies a little outside the bare egg (the head's fat): measure it over the cranium, so
        // the wig sits on the skin, not under it.
        float scalp = 0f;
        for (int i = 0; i < 24; i++)
        {
            float a = i * 0.785f, el = 0.15f + 0.7f * (i % 3) / 2f;               // round the top, three heights
            Vector3 dir = new Vector3(Mathf.Cos(a) * Mathf.Cos(el), Mathf.Sin(el), Mathf.Sin(a) * Mathf.Cos(el));
            Vector3 l = new Vector3(dir.x * R.x * k.eggTop, dir.y * R.y, dir.z * R.z * k.eggTop);
            Vector3 w = sk.o + sk.x * l.x + sk.y * l.y + sk.z * l.z;
            scalp = Mathf.Max(scalp, -k.Skin(w));
        }
        scalp = Mathf.Min(scalp, 0.02f);

        // In skull space (metres, the egg's frame); u = fractions of the radii.
        float Sdf(Vector3 l)
        {
            Vector3 u = new Vector3(l.x / R.x, l.y / R.y, l.z / R.z);
            float ax = Mathf.Abs(u.x);
            // Ragged: the edges break into strands (noise along the edge).
            float ragged = (Pcg.Noise3(new Vector3(l.x * 160f, l.y * 30f, l.z * 160f), seed + 7) - 0.5f) * 0.12f;
            float window = Mathf.Min(u.z - 0.05f, 1f - new Vector2(ax / 0.86f, (u.y + 0.2f) / 0.78f).magnitude + ragged);
            float cut = window;
            bool shortStyle = style == WigStyle.Buzz || style == WigStyle.Crop || style == WigStyle.Ponytail || style == WigStyle.Bun;
            if (shortStyle)
            {
                cut = Mathf.Max(cut, Mathf.Min(Mathf.Min(ax - 0.6f, 0.02f - u.y), u.z + 0.45f));     // ears and temples
                cut = Mathf.Max(cut, -0.35f - u.y - (style == WigStyle.Ponytail ? 2f : 0f) * Mathf.Clamp01(-u.z - 0.8f)); // the nape (the tail passes)
            }
            else
            {
                cut = Mathf.Max(cut, Mathf.Min(u.z + 0.25f, -0.8f - u.y));                         // below the jaw: behind the neck only
                cut = Mathf.Max(cut, Mathf.Min(u.z - 0.12f, -0.2f - u.y));                         // nothing hangs in front of the cheeks
            }
            float cutAll = cut + (shortStyle ? ragged * 0.5f : ragged * 0.3f);
            float egg = BodySdfKernel.EggDist(l, R, k.eggBottom, k.eggTop) - scalp;
            float t = thick + top * Mathf.Clamp01(u.y) + part * Mathf.Clamp01(0.5f + 0.5f * u.x) * Mathf.Clamp01(u.y);
            // Thinner toward the hairline (no thick rim): full thickness 0.2 radii in from the edge.
            t *= Mathf.Lerp(0.25f, 1f, Mathf.Clamp01(-cutAll / 0.2f));
            // Locks: lumpy clumps stretched along the fall of the hair (down from the crown), a few mm.
            float locks = style == WigStyle.Buzz ? 0f : (Pcg.Noise3(new Vector3(l.x * 90f, l.y * 18f + (l.z < 0f ? l.z * 18f : 0f), l.z * 90f), seed) - 0.5f) * 0.006f;
            float d = Mathf.Max(egg - t - locks, gap - egg);                          // the cap: a shell on the skull
            // Hanging hair (bob, long): a straight curtain from the head's widest ring down, leaning back.
            if (style == WigStyle.Bob || style == WigStyle.Long)
            {
                float bottom = style == WigStyle.Bob ? -0.85f * R.y : -2.4f * R.y;
                float lean = style == WigStyle.Long ? 0.35f : 0.05f;
                float zc = l.z + lean * Mathf.Max(0f, -l.y);                          // lean back as it falls
                float A = R.x * k.eggTop * 1.04f + t, B = R.z * k.eggTop * 1.02f + t;
                float ring = (new Vector2(l.x / A, zc / B).magnitude - 1f) * Mathf.Min(A, B);
                float curtain = Mathf.Abs(ring + t * 0.5f) - t * 0.5f;
                curtain = Mathf.Max(curtain, Mathf.Max(l.y - 0.1f * R.y, bottom - l.y));
                d = BodySdfKernel.SMin(d, curtain - locks, 0.025f);
            }
            if (style == WigStyle.Ponytail)
            {
                Vector3 a = new Vector3(0f, 0.35f * R.y, -1.0f * R.z), b = new Vector3(0f, -1.9f * R.y, -1.35f * R.z);
                Vector3 ab = b - a; float h = Mathf.Clamp01(Vector3.Dot(l - a, ab) / ab.sqrMagnitude);
                float tail = (l - (a + ab * h)).magnitude - Mathf.Lerp(0.03f, 0.012f, h);
                d = BodySdfKernel.SMin(d, tail, 0.012f);
            }
            if (style == WigStyle.Bun)
                d = BodySdfKernel.SMin(d, (l - new Vector3(0f, 0.6f * R.y, -0.95f * R.z)).magnitude - 0.045f, 0.012f);
            // The hairline: the face window (an ellipse in front: forehead arc, temples) is never covered.
            return Mathf.Max(d, cutAll * R.x);
        }

        // Sample a grid round the head (long hair reaches down the back).
        float down = style == WigStyle.Long ? 2.6f * R.y : style == WigStyle.Ponytail ? 2.1f * R.y : 1.2f * R.y;
        Vector3 lo = new Vector3(-R.x * 1.5f, -down, -R.z * 1.9f), hi = new Vector3(R.x * 1.5f, R.y * 1.5f, R.z * 1.3f);
        int nx = Mathf.CeilToInt((hi.x - lo.x) / Cell) + 1, ny = Mathf.CeilToInt((hi.y - lo.y) / Cell) + 1, nz = Mathf.CeilToInt((hi.z - lo.z) / Cell) + 1;
        var grid = new float[nx * ny * nz];
        for (int z = 0; z < nz; z++)
            for (int y = 0; y < ny; y++)
                for (int x = 0; x < nx; x++)
                    grid[x + nx * (y + ny * z)] = Sdf(lo + new Vector3(x, y, z) * Cell);
        var v = new List<Vector3>(); var t = new List<int>();
        SurfaceNets.Extract(grid, nx, ny, nz, lo, Cell, v, t);
        if (t.Count == 0) return false;
        if (t.Count / 3 > TargetTriangles)
        {
            var imp = new float[v.Count]; for (int i = 0; i < imp.Length; i++) imp[i] = 1f;
            MeshDecimator.Run(v, t, imp, TargetTriangles, out var dv, out var dt, out _);
            v = dv; t = dt;
        }
        // Skull space -> head joint space (the head's rest frame is axis-aligned with the root).
        verts = new Vector3[v.Count]; normals = new Vector3[v.Count];
        Vector3 origin = sk.o - headJoint;
        for (int i = 0; i < v.Count; i++)
        {
            Vector3 l = v[i];
            const float h = 0.002f;
            Vector3 g = new Vector3(Sdf(l + new Vector3(h, 0, 0)) - Sdf(l - new Vector3(h, 0, 0)),
                                    Sdf(l + new Vector3(0, h, 0)) - Sdf(l - new Vector3(0, h, 0)),
                                    Sdf(l + new Vector3(0, 0, h)) - Sdf(l - new Vector3(0, 0, h)));
            Vector3 nl = g.sqrMagnitude > 1e-12f ? g.normalized : Vector3.up;
            verts[i] = origin + sk.x * l.x + sk.y * l.y + sk.z * l.z;
            normals[i] = (sk.x * nl.x + sk.y * nl.y + sk.z * nl.z).normalized;
        }
        tris = t.ToArray();
        return true;
    }
}
