using System.Collections.Generic;
using UnityEngine;

// Face Polish Part 4: face hair as tiny cards (drawn by FPS/Hair, appended to the head's hair cards):
// the brows (and the beards, Part 5). Each card is a tuft of a few mm lying close to the skin, rooted on
// it and following a direction field; under the brows the skin gets a soft hair-coloured tint (the bake's
// stubble channel) so gaps between tufts don't show bare skin.
// Brows: a curve through three points per side (eye-relative, in half eye-widths):
//   head  above the inner eye corner          (hairs point up)
//   arch  above the iris's outer edge         (hairs turn outward along the brow)
//   tail  near the outer eye corner, a little higher than the head (outward and slightly down)
// Male: flatter, lower, thicker; female: a higher arch, a thinner tail. Seed varies the points.
// Thickness (the band's height) and density taper from the head and body to a thin tail.
// Beards (Part 5): a beard zone map in the canon's frame (moustache, chin, jaw and lower cheeks below the
// cheek line, under the jaw to the Adam's apple, sideburns); stubble is the skin shader's (a blue-grey
// shadow with follicle dots, strength 0..1 in the bake's stubble channel); beards are short tufts rooted
// in the zone, by style.
public enum BeardStyle { None, Stubble, ShortFull, Goatee, Moustache, Long }

public static class FaceHair
{
    // Output buffers in HairCards' layout.
    public class Cards
    {
        public readonly List<Vector3> v = new List<Vector3>(), n = new List<Vector3>();
        public readonly List<Vector4> t = new List<Vector4>();
        public readonly List<Vector2> uv = new List<Vector2>(), uv2 = new List<Vector2>();
        public readonly List<BoneWeight> w = new List<BoneWeight>();
        public readonly List<int> tri = new List<int>();

        // A card along pts (root first), lying across `side`, its width tapering to 30% at the tip.
        public void Add(Vector3[] pts, Vector3 side, Vector3 nrm, float width, float rnd, BoneWeight bw)
        {
            int b = v.Count, S = pts.Length - 1;
            for (int s = 0; s <= S; s++)
            {
                float f = s / (float)S;
                Vector3 tan = (s < S ? pts[s + 1] - pts[s] : pts[s] - pts[s - 1]).normalized;
                float wd = width * (1f - 0.7f * f);
                for (int e = 0; e < 2; e++)
                {
                    v.Add(pts[s] + side * (e == 0 ? -0.5f : 0.5f) * wd); n.Add(nrm); t.Add(new Vector4(tan.x, tan.y, tan.z, 1f));
                    uv.Add(new Vector2(e, f)); uv2.Add(new Vector2(rnd, -1f)); w.Add(bw);   // (uv2.y -1: face hair)
                }
                if (s < S) { int a = b + s * 2; tri.Add(a); tri.Add(a + 2); tri.Add(a + 1); tri.Add(a + 1); tri.Add(a + 2); tri.Add(a + 3); }
            }
        }

        // Appends these cards to arrays (null arrays = none yet).
        public void AppendTo(ref Vector3[] V, ref Vector3[] N, ref Vector4[] T, ref Vector2[] UV, ref Vector2[] UV2, ref BoneWeight[] W, ref int[] Tri)
        {
            int o = V?.Length ?? 0;
            V = Cat(V, v); N = Cat(N, n); T = Cat(T, t); UV = Cat(UV, uv); UV2 = Cat(UV2, uv2); W = Cat(W, w);
            var tr = new List<int>(Tri ?? new int[0]);
            foreach (int i in tri) tr.Add(o + i);
            Tri = tr.ToArray();
        }

        static X[] Cat<X>(X[] a, List<X> b) { var l = new List<X>(a ?? new X[0]); l.AddRange(b); return l.ToArray(); }
    }

    // Onto the skin: in from the front along the face's forward axis.
    static Vector3 SnapFront(BodySdfKernel k, Vector3 p, Vector3 fwd)
    {
        Vector3 q = p + fwd * 0.06f;
        for (int it = 0; it < 200; it++)
        {
            float d = k.Skin(q);
            if (d < 0.0002f) break;
            q -= fwd * Mathf.Clamp(d * 0.9f, 0.0002f, 0.01f);
        }
        return q;
    }

    // Onto the skin along its normal.
    static Vector3 SnapNormal(BodySdfKernel k, Vector3 p)
    {
        for (int it = 0; it < 4; it++) p -= k.Normal(p, 0.0015f) * k.Skin(p);
        return p;
    }

    // The brow's thickness (band height, half eye-widths) along it (0 head .. 1 tail).
    static float BrowHeight(float t, bool female)
    {
        float h0 = female ? 0.42f : 0.55f, h1 = female ? 0.34f : 0.46f, h2 = female ? 0.07f : 0.13f;
        return t < 0.55f ? Mathf.Lerp(h0, h1, t / 0.55f) : Mathf.Lerp(h1, h2, Mathf.SmoothStep(0f, 1f, (t - 0.55f) / 0.45f));
    }

    // The brows' centre lines on the skin (head end first), one per side, 24 points each.
    public static List<Vector3[]> BrowLines(BodySdfKernel k, HeadCanon c, Vector3 eyeL, Vector3 eyeR, int seed, bool female)
    {
        var res = new List<Vector3[]>();
        var rng = new PcgRandom(seed, "browShape");
        float ew = c.eyeWidth * 0.5f;
        // (head x, head y, arch x, arch y, tail x, tail y): x outward from the eye's centre, y up.
        float hx = -1.0f + 0.08f * rng.Signed(), hy = (female ? 1.32f : 1.18f) + 0.1f * rng.Signed();
        float ax = (female ? 0.6f : 0.45f) + 0.12f * rng.Signed(), ay = (female ? 1.8f : 1.45f) + 0.12f * rng.Signed();
        float tx = 1.32f + 0.08f * rng.Signed(), ty = hy + (female ? 0.14f : 0.1f) + 0.08f * rng.Signed();
        ay = Mathf.Max(ay, Mathf.Max(hy, ty) + 0.08f);
        float ta = (ax - hx) / (tx - hx);
        foreach (var eye in new[] { eyeL, eyeR })
        {
            Vector3 xo = c.x * Mathf.Sign(Vector3.Dot(eye - c.o, c.x) + 1e-6f);
            var pts = new Vector3[24];
            for (int i = 0; i < pts.Length; i++)
            {
                float t = i / (float)(pts.Length - 1);
                float x = Mathf.Lerp(hx, tx, t);
                // Through the three points (a parabola in t, the arch at ta).
                float y = hy * (t - ta) * (t - 1f) / (ta * 1f) + ay * t * (t - 1f) / (ta * (ta - 1f)) + ty * t * (t - ta) / (1f - ta);
                pts[i] = SnapFront(k, eye + xo * (x * ew) + c.y * (y * ew), c.z);
            }
            res.Add(pts);
        }
        return res;
    }

    // The brow tint's strength at a skin point (0..1): inside the band, soft at its edges.
    public static float BrowTint(List<Vector3[]> lines, HeadCanon c, Vector3 p, bool female)
    {
        if (lines == null) return 0f;
        float ew = c.eyeWidth * 0.5f, best = 0f;
        foreach (var pts in lines)
            for (int i = 0; i + 1 < pts.Length; i++)
            {
                Vector3 a = pts[i], b = pts[i + 1], ab = b - a;
                float h = Mathf.Clamp01(Vector3.Dot(p - a, ab) / ab.sqrMagnitude);
                float d = (p - (a + ab * h)).magnitude;
                float t = (i + h) / (pts.Length - 1);
                float half = BrowHeight(t, female) * ew * 0.5f;
                best = Mathf.Max(best, 1f - Mathf.SmoothStep(0f, 1f, (d - half * 0.6f) / (half * 0.8f + 0.001f)));
            }
        return best;
    }

    // The brows' tufts.
    public static void Brows(BodySdfKernel k, HeadCanon c, List<Vector3[]> lines, int seed, bool female, int headBone, Cards outCards)
    {
        if (lines == null) return;
        k.cutMode = 0;
        var rng = new PcgRandom(seed, "browHair");
        float ew = c.eyeWidth * 0.5f;
        var bw = new BoneWeight { boneIndex0 = headBone, weight0 = 1f };
        int count = female ? 85 : 110;
        foreach (var pts in lines)
        {
            Vector3 P(float t) { float f = t * (pts.Length - 1); int i = Mathf.Min((int)f, pts.Length - 2); return Vector3.Lerp(pts[i], pts[i + 1], f - i); }
            for (int made = 0, guard = 0; made < count && guard < count * 20; guard++)
            {
                float t = rng.Value();
                // Density follows the thickness (the tail thins out).
                if (rng.Value() > BrowHeight(t, female) / BrowHeight(0f, female)) continue;
                made++;
                Vector3 p = P(t), T = (P(Mathf.Min(1f, t + 0.02f)) - P(Mathf.Max(0f, t - 0.02f))).normalized;
                Vector3 nrm = k.Normal(p, 0.0015f);
                T = Vector3.ProjectOnPlane(T, nrm).normalized;
                Vector3 U = Vector3.Cross(nrm, T).normalized;
                if (Vector3.Dot(U, c.y) < 0f) U = -U;
                float hh = BrowHeight(t, female) * ew;
                float a = rng.Signed() * 0.5f;                       // across the band (-0.5 bottom .. 0.5 top)
                Vector3 root = SnapNormal(k, p + U * (a * hh));
                nrm = k.Normal(root, 0.0015f);
                // Direction: up at the head, outward along the body (converging on the centre line), out and
                // a little down at the tail.
                Vector3 dir;
                if (t < 0.2f) dir = Vector3.Slerp(U, (T + U * 0.4f).normalized, t / 0.2f);
                else dir = (T + U * (0.4f * Mathf.Clamp01(1f - (t - 0.2f) / 0.3f) - a * 0.5f)).normalized;
                if (t > 0.75f) dir = (dir - U * (0.35f * (t - 0.75f) / 0.25f)).normalized;
                dir = (Vector3.ProjectOnPlane(dir, nrm).normalized + nrm * 0.2f).normalized;
                float len = (t < 0.15f ? 0.006f : t > 0.8f ? 0.0055f : 0.0085f) * (1f + 0.2f * rng.Signed()) * (female ? 0.85f : 1f);
                float width = (female ? 0.0017f : 0.0022f) * (t > 0.8f ? 0.6f : 1f);
                var card = new Vector3[3];
                card[0] = root + nrm * 0.0003f;
                card[1] = card[0] + dir * (len * 0.5f);
                // (The tip curls back down toward the skin.)
                card[2] = card[1] + (dir - nrm * 0.45f).normalized * (len * 0.5f);
                Vector3 side = Vector3.Cross(nrm, dir).normalized;
                outCards.Add(card, side, nrm, width, rng.Value(), bw);
            }
        }
    }

    // ---------------- Face Polish Part 5: stubble and beards ----------------

    public static BeardStyle? ForceBeard;   // test hook: every male beard this style

    // Seed choice: NPC men mostly stubble or nothing, some short beards, few long ones; women none.
    // stubble: the shader's shadow strength in the beard zone (0..1).
    public static BeardStyle PickBeard(CharacterSheet sheet, out float stubble)
    {
        stubble = 0f;
        if (sheet.sex != Sex.Male) return BeardStyle.None;
        var r = new PcgRandom(sheet.seed, "beardStyle");
        float v = r.Value();
        var s = ForceBeard ?? (v < 0.38f ? BeardStyle.None : v < 0.76f ? BeardStyle.Stubble : v < 0.87f ? BeardStyle.ShortFull
                             : v < 0.92f ? BeardStyle.Goatee : v < 0.97f ? BeardStyle.Moustache : BeardStyle.Long);
        stubble = s == BeardStyle.None ? (r.Value() < 0.5f ? r.Range(0.1f, 0.25f) : 0f)   // (a clean shave: a faint shadow at most)
                : s == BeardStyle.Stubble ? r.Range(0.5f, 0.95f) : 0.45f;                     // (under a beard: shaved-short hair)
        return s;
    }

    // The beard zone in the canon's frame, by part (0..1 each, soft edges). Never the nose, lips, upper
    // cheeks, eyes or forehead.
    public struct BeardZone
    {
        public float moustache, chin, jaw, neck, sideburn;
        public float All => Mathf.Max(Mathf.Max(moustache, chin), Mathf.Max(Mathf.Max(jaw, neck), sideburn));
    }

    static float Sm(float a, float b, float x) => Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(a, b, x));

    public static BeardZone Zone(HeadCanon c, Vector3 p)
    {
        Vector3 d = p - c.o;
        float x = Vector3.Dot(d, c.x) / c.halfW, ax = Mathf.Abs(x);
        float y = Vector3.Dot(d, c.y), z = Vector3.Dot(d, c.z);
        float zf = z / c.halfD;
        var b = new BeardZone();
        float front = Sm(0.15f, 0.35f, zf);
        // The lips (never): the mouth's band, a little wider than the mouth.
        float lipX = c.mouthHalfWidth / c.halfW * 1.08f;
        float lips = (1f - Sm(lipX - 0.04f, lipX + 0.04f, ax)) * Sm(c.mouth - 0.013f, c.mouth - 0.009f, y) * (1f - Sm(c.mouth + 0.006f, c.mouth + 0.009f, y)) * front;
        // Moustache: the upper lip, below the nose.
        b.moustache = (1f - Sm(0.42f, 0.5f, ax)) * Sm(c.mouth + 0.004f, c.mouth + 0.008f, y) * (1f - Sm(c.noseBase - 0.007f, c.noseBase - 0.003f, y)) * front;
        // Chin: below the lower lip to the chin's underside.
        b.chin = (1f - Sm(0.36f, 0.46f, ax)) * (1f - Sm(c.mouth - 0.016f, c.mouth - 0.011f, y)) * Sm(c.chin - 0.02f, c.chin - 0.008f, y) * Sm(0.0f, 0.2f, zf);
        // The cheek line: from the sideburn (high, at the ear) down to the mouth's corner.
        float line = Mathf.Lerp(c.mouth + 0.006f, c.noseBase * 0.45f, Sm(0.38f, 0.9f, ax));
        b.jaw = Sm(0.3f, 0.42f, ax) * (1f - Sm(line - 0.006f, line + 0.002f, y)) * Sm(c.chin - 0.03f, c.chin - 0.012f, y) * Sm(c.earZ + 0.006f, c.earZ + 0.016f, z);
        // Under the jaw and the neck's front, down to the Adam's apple.
        b.neck = (1f - Sm(c.chin + 0.004f, c.chin + 0.012f, y)) * Sm(c.chin - 0.065f, c.chin - 0.05f, y) * Sm(c.earZ + 0.0f, c.earZ + 0.02f, z) * (1f - Sm(0.75f, 0.9f, ax));
        // Sideburns: in front of the ear, from the cheek line up to the eye line (meeting the hairline).
        b.sideburn = Sm(0.78f, 0.86f, ax) * Sm(line - 0.004f, line + 0.004f, y) * (1f - Sm(-0.004f, 0.004f, y))
                     * Sm(c.earZ + 0.004f, c.earZ + 0.01f, z) * (1f - Sm(c.earZ + 0.03f, c.earZ + 0.04f, z));
        float keep = 1f - lips;
        b.moustache *= keep; b.chin *= keep; b.jaw *= keep;
        return b;
    }

    // How much of the zone a style covers.
    static float Covered(BeardStyle s, BeardZone z, HeadCanon c, Vector3 p)
    {
        switch (s)
        {
            case BeardStyle.ShortFull: case BeardStyle.Long: return z.All;
            case BeardStyle.Goatee:
            {
                float ax = Mathf.Abs(Vector3.Dot(p - c.o, c.x)) / c.halfW;
                return Mathf.Max(z.moustache, z.chin * (1f - Sm(0.26f, 0.32f, ax)));
            }
            case BeardStyle.Moustache: return z.moustache;
            default: return 0f;
        }
    }

    // Beard tufts, rooted on the head mesh's skin (v, n, w, tris: root space) in the style's zone. Short
    // tufts lie close to the skin; the direction field runs mostly down - down and forward on the chin,
    // outward on the jaw and the moustache. A long beard's tufts fall from the chin and jaw (pushed out of
    // the body). Each tuft takes its skin's bone weights (the jaw's for the chin).
    public static void Beard(BodySdfKernel k, HeadCanon c, BeardStyle style, int seed, Vector3[] v, Vector3[] n, BoneWeight[] w, int[] tris, Cards outCards)
    {
        if (style == BeardStyle.None || style == BeardStyle.Stubble || v == null || tris == null) return;
        k.cutMode = 0;
        var rng = new PcgRandom(seed, "beardHair");
        int nt = tris.Length / 3;
        var cum = new float[nt];
        float total = 0f;
        for (int t = 0; t < nt; t++)
        {
            Vector3 a = v[tris[t * 3]], b = v[tris[t * 3 + 1]], cc = v[tris[t * 3 + 2]];
            Vector3 ctr = (a + b + cc) / 3f;
            float cov = Covered(style, Zone(c, ctr), c, ctr);
            total += cov > 0.05f ? Vector3.Cross(b - a, cc - a).magnitude * 0.5f * cov : 0f;
            cum[t] = total;
        }
        if (total <= 0f) return;
        bool longB = style == BeardStyle.Long;
        // Tufts per square metre of zone (a full short beard ~ 1500).
        int count = Mathf.Clamp(Mathf.RoundToInt(total * (longB ? 30000f : 80000f)), 60, longB ? 900 : 2000);
        for (int i = 0; i < count; i++)
        {
            float r = rng.Value() * total;
            int lo = 0, hi = nt - 1;
            while (lo < hi) { int mid = (lo + hi) / 2; if (cum[mid] < r) lo = mid + 1; else hi = mid; }
            int i0 = tris[lo * 3], i1 = tris[lo * 3 + 1], i2 = tris[lo * 3 + 2];
            float u1 = rng.Value(), u2 = rng.Value();
            if (u1 + u2 > 1f) { u1 = 1f - u1; u2 = 1f - u2; }
            Vector3 root = v[i0] + (v[i1] - v[i0]) * u1 + (v[i2] - v[i0]) * u2;
            Vector3 nrm = (n[i0] * (1f - u1 - u2) + n[i1] * u1 + n[i2] * u2).normalized;
            var bw = w[(1f - u1 - u2) >= Mathf.Max(u1, u2) ? i0 : u1 >= u2 ? i1 : i2];
            var z = Zone(c, root);
            float sx = Mathf.Sign(Vector3.Dot(root - c.o, c.x) + 1e-6f);
            // Direction: down; down and forward on the chin; outward on the jaw; out and down on the moustache.
            Vector3 dir = -c.y + c.z * (0.6f * z.chin) + c.x * sx * (0.35f * z.jaw + 0.9f * z.moustache);
            dir = Vector3.ProjectOnPlane(dir.normalized, nrm).normalized;
            if (dir.sqrMagnitude < 0.5f) dir = Vector3.ProjectOnPlane(-c.y + c.z, nrm).normalized;
            bool chinJaw = z.chin + z.jaw + z.neck > z.moustache + z.sideburn;
            float len = style == BeardStyle.Moustache ? 0.010f : style == BeardStyle.Goatee ? 0.010f : 0.0065f;
            if (longB) len = chinJaw ? Mathf.Lerp(0.03f, 0.075f, Mathf.Max(z.chin, z.neck)) : 0.013f;
            len *= 1f + 0.25f * rng.Signed();
            float width = longB && chinJaw ? 0.005f : 0.0034f;
            int S = longB && chinJaw ? 4 : 2;
            float lift = 0.0004f;
            var pts = new Vector3[S + 1];
            Vector3 p = root + nrm * lift;
            pts[0] = p;
            Vector3 d0 = (dir + nrm * (longB && chinJaw ? 0.3f : 0.15f)).normalized;
            for (int s = 1; s <= S; s++)
            {
                float t = s / (float)S;
                Vector3 dd = longB && chinJaw ? Vector3.Lerp(d0, -c.y, 0.75f * t).normalized : (d0 - nrm * 0.5f * t).normalized;
                p += dd * (len / S);
                // (Out of the skin: the jaw, the neck, the chest.)
                for (int it = 0; it < 3; it++) { float sd = k.Skin(p); if (sd >= lift) break; p += k.Normal(p, 0.002f) * (lift - sd); }
                pts[s] = p;
            }
            Vector3 side = Vector3.Cross(nrm, d0).normalized;
            outCards.Add(pts, side, nrm, width, rng.Value(), bw);
        }
    }
}
