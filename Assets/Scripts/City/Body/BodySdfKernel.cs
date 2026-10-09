using System;
using Unity.Collections;
using UnityEngine;

// The body SDF's evaluation as unmanaged data + code, so Burst jobs (BodyMesher) and the managed
// BodySDF share one implementation. Built by BodySDF (see there for what each layer is).
// Up to 31 bones (scratch is a FixedList128Bytes<float>): five arms need 26. Digits (finger bones)
// aren't kernel bones: they're capsules of their hand's core.
// Cuts (cutMode), for meshing each hand on its own finer grid: 1 = the body without its hands (cut just
// above each wrist), 2 + h = hand h alone (from a little further up the forearm). The two overlap over
// a short stretch where each shrinks slightly under the other, so neither cap nor seam shows.
public struct BodySdfKernel : IDisposable
{
    public struct Bone
    {
        public Vector3 o, x, y, z;      // joint and frame (y along the bone, z the body's front, x its right)
        public Vector3 end;
        public Vector3 girth, muscle, fat, outer;
        public float length, depthRatio, jointK;
        public int kind, parent, side;
        public Vector3 palm;            // hands: palm half extents (thickness, length, width)
        public float palmRound;
        public int digitStart, digitCount;  // hands: their segments in digits (contiguous)
    }

    public struct Digit
    {
        public Vector3 a, b;            // capsule (bind pose)
        public float r;
        public int hand, bone;          // kernel index of the hand, plan / skin bone index of the segment
        public int parentBone, childBone;  // the segment before (or the hand) and after (-1 at the tip)
        public float length;            // joint to joint
    }

    public struct HandCut
    {
        public Vector3 o, axis;         // the wrist, along the hand
        public Vector3 centre;          // a sphere round the hand that bounds the cut
        public float radius;
        public int hand;
    }

    public struct Ellipsoid
    {
        public Vector3 o, x, y, z, r;
        public int bone, group;
        public Vector3 ToLocal(Vector3 p) { p -= o; return new Vector3(Vector3.Dot(p, x), Vector3.Dot(p, y), Vector3.Dot(p, z)); }
    }

    public struct Sample
    {
        public float skin;      // signed distance to the skin
        public float muscle;    // ... to the muscle surface (no fat)
        public float core;      // ... to the core
        public float fat;       // local fat thickness
        public int nearest;     // nearest bone
    }

    [ReadOnly] public NativeArray<Bone> bones;
    [ReadOnly] public NativeArray<Ellipsoid> blobs, features, breasts;
    [ReadOnly] public NativeArray<Digit> digits;
    [ReadOnly] public NativeArray<HandCut> cuts;
    public float fingerBlend;
    public int cutMode;                 // 0 whole body, 1 body without hands, 2 + h hand h
    public float cutBody, cutHand, cutShrink;  // cut positions along the hand (m from the wrist), shrink (m)
    public Ellipsoid skull;
    public int headIndex;
    public Vector3 eyeL, eyeR;
    public float eyeRadius;
    public float muscleBlend, fatBlendPerMetre, fatBlendMin, breastBlend, featureBlend, eyeCarveBlend;
    public float fatBlendFalloff;   // fat thickness blends across bones within about this distance
    public float fatBlendMax, muscleReach;
    public float fatWaist, fatBelly, fatHips, fatButtocks;
    public int hasDna;

    public const int MaxBones = 31;
    const int KPelvis = (int)BoneKind.Pelvis, KLumbar = (int)BoneKind.Lumbar, KChest = (int)BoneKind.Chest,
              KHead = (int)BoneKind.Head, KHand = (int)BoneKind.Hand, KFoot = (int)BoneKind.Foot;

    public void Dispose()
    {
        if (bones.IsCreated) bones.Dispose();
        if (blobs.IsCreated) blobs.Dispose();
        if (features.IsCreated) features.Dispose();
        if (breasts.IsCreated) breasts.Dispose();
        if (digits.IsCreated) digits.Dispose();
        if (cuts.IsCreated) cuts.Dispose();
    }

    // ---------- primitives ----------

    public static float SMin(float a, float b, float k)
    {
        if (k <= 0f) return Mathf.Min(a, b);
        float h = Mathf.Max(k - Mathf.Abs(a - b), 0f) / k;
        return Mathf.Min(a, b) - h * h * k * 0.25f;
    }
    public static float SMax(float a, float b, float k) => -SMin(-a, -b, k);

    public static float EllipsoidDist(Vector3 p, Vector3 r)
    {
        float k0 = new Vector3(p.x / r.x, p.y / r.y, p.z / r.z).magnitude;
        float k1 = new Vector3(p.x / (r.x * r.x), p.y / (r.y * r.y), p.z / (r.z * r.z)).magnitude;
        return k1 > 1e-8f ? k0 * (k0 - 1f) / k1 : -Mathf.Min(r.x, Mathf.Min(r.y, r.z));
    }

    // Round cone from the origin along +y to (0, h, 0), radii r1 -> r2 (Inigo Quilez).
    static float RoundCone(Vector3 p, float r1, float r2, float h)
    {
        if (h < 1e-5f) return p.magnitude - Mathf.Max(r1, r2);
        float b = Mathf.Clamp((r1 - r2) / h, -0.99f, 0.99f), a = Mathf.Sqrt(1f - b * b);
        float qx = new Vector2(p.x, p.z).magnitude, qy = p.y;
        float k = -b * qx + a * qy;
        if (k < 0f) return new Vector2(qx, qy).magnitude - r1;
        if (k > a * h) return new Vector2(qx, qy - h).magnitude - r2;
        return qx * a + qy * b - r1;
    }

    // Cone along +y from y0 (radius ra) to y1 (radius rb), flat ends (Inigo Quilez's capped cone).
    static float CappedCone(Vector3 p, float y0, float y1, float ra, float rb)
    {
        float h = (y1 - y0) * 0.5f;
        if (h < 1e-5f) return float.MaxValue;
        Vector2 q = new Vector2(new Vector2(p.x, p.z).magnitude, p.y - (y0 + y1) * 0.5f);
        Vector2 k1 = new Vector2(rb, h), k2 = new Vector2(rb - ra, 2f * h);
        Vector2 ca = new Vector2(q.x - Mathf.Min(q.x, q.y < 0f ? ra : rb), Mathf.Abs(q.y) - h);
        Vector2 cb = q - k1 + k2 * Mathf.Clamp01(Vector2.Dot(k1 - q, k2) / k2.sqrMagnitude);
        float sgn = cb.x < 0f && ca.y < 0f ? -1f : 1f;
        return sgn * Mathf.Sqrt(Mathf.Min(ca.sqrMagnitude, cb.sqrMagnitude));
    }

    static float RoundBox(Vector3 p, Vector3 half, float r)
    {
        Vector3 q = new Vector3(Mathf.Abs(p.x), Mathf.Abs(p.y), Mathf.Abs(p.z)) - half + Vector3.one * r;
        return Vector3.Max(q, Vector3.zero).magnitude + Mathf.Min(Mathf.Max(q.x, Mathf.Max(q.y, q.z)), 0f) - r;
    }

    static float Capsule(Vector3 p, Vector3 a, Vector3 b, float r)
    {
        Vector3 pa = p - a, ba = b - a;
        float h = Mathf.Clamp01(Vector3.Dot(pa, ba) / Mathf.Max(1e-8f, ba.sqrMagnitude));
        return (pa - ba * h).magnitude - r;
    }

    // Girth-style profile (start, middle, end) at t in 0..1.
    public static float Profile(Vector3 v, float t)
    {
        t = Mathf.Clamp01(t);
        return t < 0.5f ? Mathf.Lerp(v.x, v.y, t * 2f) : Mathf.Lerp(v.y, v.z, t * 2f - 1f);
    }

    static bool IsTorso(int k) => k == KPelvis || k == KLumbar || k == KChest;

    // ---------- the layers ----------

    public float BoneCore(int i, Vector3 p)
    {
        var b = bones[i];
        Vector3 d0 = p - b.o;
        Vector3 q = new Vector3(Vector3.Dot(d0, b.x), Vector3.Dot(d0, b.y), Vector3.Dot(d0, b.z));
        if (b.kind == KHand)
        {
            // Sausage hand: a palm block (rounded box), the digits' capsules blended on with a small
            // radius (never with each other). Far away, a bounding sphere (a lower bound) is enough.
            float L = b.length;
            Vector3 c = b.o + b.y * (L * 0.5f);
            float bound = (p - c).magnitude - L * 0.75f;
            if (bound > 0.04f) return bound;
            float palm = RoundBox(q - new Vector3(0f, b.palm.y, 0f), b.palm, b.palmRound);
            float fingers = float.MaxValue;
            for (int j = b.digitStart; j < b.digitStart + b.digitCount; j++)
                fingers = Mathf.Min(fingers, Capsule(p, digits[j].a, digits[j].b, digits[j].r));
            return fingers < float.MaxValue ? SMin(palm, fingers, fingerBlend) : palm;
        }
        if (b.kind == KFoot)
        {
            // Rounded wedge from heel to toe, sole on the ground, sloping down to the toes.
            Vector3 ankle = b.o, toe = b.end;
            float L = (toe - ankle).magnitude;
            Vector3 fwd = Vector3.ProjectOnPlane(toe - ankle, Vector3.up).normalized;
            Vector3 side = Vector3.Cross(Vector3.up, fwd);
            float footLen = L / 0.72f * 0.95f;
            float h = ankle.y + b.girth.y * 0.6f;
            Vector3 centre = new Vector3(ankle.x, 0f, ankle.z) + fwd * (footLen * 0.22f) + Vector3.up * h * 0.5f;
            Vector3 lp = p - centre;
            Vector3 l = new Vector3(Vector3.Dot(lp, side), Vector3.Dot(lp, Vector3.up), Vector3.Dot(lp, fwd));
            float w = b.girth.y * 1.15f;
            float box = RoundBox(l, new Vector3(w, h * 0.5f, footLen * 0.5f), Mathf.Min(w, h * 0.5f) * 0.6f);
            Vector3 n = new Vector3(0f, footLen * 0.75f, h * 0.9f).normalized;
            float plane = Vector3.Dot(l - new Vector3(0f, h * 0.5f, -footLen * 0.1f), n);
            return SMax(box, plane, 0.01f);
        }
        if (b.kind == KHead) return EllipsoidDist(skull.ToLocal(p), skull.r);

        float dr = b.depthRatio;
        Vector3 s = new Vector3(q.x, q.y, q.z / dr);
        float L2 = b.length * 0.5f;
        if (IsTorso(b.kind))
        {
            // Torso: flat-capped cones (round caps would swallow the waist); domes close the open ends.
            float dt = Mathf.Min(CappedCone(s, 0f, L2, b.girth.x, b.girth.y), CappedCone(s, L2, b.length, b.girth.y, b.girth.z));
            if (b.kind != KLumbar)
            {
                float re = b.girth.z;
                dt = SMin(dt, EllipsoidDist(s - new Vector3(0f, b.length, 0f), new Vector3(re, re * 0.5f, re)), re * 0.3f);
            }
            return dt * dr;
        }
        float d1 = RoundCone(s, b.girth.x, b.girth.y, L2);
        float d2 = RoundCone(s - new Vector3(0f, L2, 0f), b.girth.y, b.girth.z, L2);
        return Mathf.Min(d1, d2) * dr; // squashing z by 1/dr stretches distances: stay a lower bound
    }

    // Fat thickness by one bone: its fat profile, steered round the torso (belly in front, buttocks
    // behind, hips at the sides).
    float FatAt(Vector3 p, int i)
    {
        var b = bones[i];
        Vector3 d0 = p - b.o;
        Vector3 q = new Vector3(Vector3.Dot(d0, b.x), Vector3.Dot(d0, b.y), Vector3.Dot(d0, b.z));
        float t = Mathf.Clamp01(q.y / Mathf.Max(1e-4f, b.length));
        float th = Profile(b.fat, t);
        if (hasDna == 0) return th;
        Vector2 dir = new Vector2(q.x, q.z);
        float m = dir.magnitude;
        // Smooth weights round the body (squares, no abs or clamp: a kink in the thickness would show
        // as a crease in thick fat): fr = front-ness, bk = back-ness, sd = side-ness.
        float front = m > 1e-4f ? dir.y / m : 0f;
        float fr = front > 0f ? front * front : 0f, bk = front < 0f ? front * front : 0f, sd = 1f - front * front;
        if (b.kind == KLumbar) th = Mathf.Lerp(fatWaist, fatBelly, fr) * Mathf.Lerp(1f, 0.6f, bk);
        else if (b.kind == KPelvis) th = Mathf.Lerp(Mathf.Lerp(fatHips, fatButtocks, bk), fatBelly * 0.7f, fr);
        else if (b.kind == KChest) th = Mathf.Lerp(th, fatWaist * 0.6f, (1f - t) * 0.5f) * (1f - 0.3f * sd);
        return th;
    }

    public Sample Eval(Vector3 p)
    {
        int n = bones.Length;
        var d = new FixedList128Bytes<float>();
        int nearest = 0;
        float best = float.MaxValue;
        for (int i = 0; i < n; i++)
        {
            float v = BoneCore(i, p);
            d.Add(v);
            if (v < best) { best = v; nearest = i; }
        }
        // Fat thickness blended over the nearby bones, so it never steps where the nearest bone changes.
        float wSum = 0f, fSum = 0f;
        for (int i = 0; i < n; i++)
        {
            float w = Mathf.Exp(-(d[i] - best) / fatBlendFalloff);
            if (w < 0.01f) continue;
            wSum += w; fSum += w * FatAt(p, i);
        }
        float fat = wSum > 0f ? fSum / wSum : 0f;
        // Hands carry their fat in the palm and finger sizes: a fat offset (and its wide blend) past the
        // wrist would fuse the fingers into a mitten.
        float handMask = 1f;
        if (bones[nearest].kind == KHand)
        {
            var hb = bones[nearest];
            float t = Vector3.Dot(p - hb.o, hb.y) / Mathf.Max(1e-4f, hb.palm.y * 2f);
            handMask = 1f - Mathf.SmoothStep(0f, 1f, t / 0.35f);
        }
        fat *= handMask;
        // Capped: polynomial smooth-min chained over many blobs with a huge radius piles up bulges.
        float kFat = Mathf.Min(fatBlendMin + fatBlendPerMetre * fat, fatBlendMax) * handMask;
        // Core: each bone smooth-unions with its parent only; the soft (fat) version blends wider.
        float core = float.MaxValue, soft = float.MaxValue;
        for (int i = 0; i < n; i++)
        {
            int pa = bones[i].parent;
            if (pa < 0) { core = Mathf.Min(core, d[i]); soft = Mathf.Min(soft, d[i]); continue; }
            float k = bones[i].jointK;
            core = Mathf.Min(core, SMin(d[i], d[pa], k));
            soft = Mathf.Min(soft, SMin(d[i], d[pa], Mathf.Max(k, kFat)));
        }
        float muscle = core;
        for (int j = 0; j < blobs.Length; j++)
        {
            var bl = blobs[j];
            // Far from this bone its muscles can't matter: beyond the blob's reach plus the widest blend
            // that can reach them (skipping inside the blend range would make jumps in the surface).
            if (d[bl.bone] > muscleReach + kFat) continue;
            float v = EllipsoidDist(bl.ToLocal(p), bl.r);
            muscle = SMin(muscle, v, muscleBlend);
            soft = SMin(soft, v, kFat);
        }
        float skin = Mathf.Min(muscle, soft - fat);
        for (int j = 0; j < breasts.Length; j++) skin = SMin(skin, EllipsoidDist(breasts[j].ToLocal(p), breasts[j].r), breastBlend);
        // Face features sit on the skin, pushed out by the face's fat so they stay visible.
        if (headIndex >= 0 && d[headIndex] < 0.08f)
        {
            float faceFat = Profile(bones[headIndex].fat, 0.5f);
            for (int j = 0; j < features.Length; j++)
                skin = SMin(skin, EllipsoidDist(features[j].ToLocal(p) - new Vector3(0f, 0f, faceFat), features[j].r), featureBlend);
        }
        if (eyeRadius > 0f)
        {
            skin = SMax(skin, -((p - eyeL).magnitude - eyeRadius), eyeCarveBlend);
            skin = SMax(skin, -((p - eyeR).magnitude - eyeRadius), eyeCarveBlend);
        }
        if (cutMode > 0) skin = Cut(p, skin);
        return new Sample { skin = skin, muscle = muscle, core = core, fat = fat, nearest = nearest };
    }

    // The meshing cuts (see the top). s = distance along the hand from the wrist.
    float Cut(Vector3 p, float skin)
    {
        float span = Mathf.Max(1e-4f, cutBody - cutHand);
        if (cutMode == 1)
        {
            for (int h = 0; h < cuts.Length; h++)
            {
                var c = cuts[h];
                float sphere = (p - c.centre).magnitude - c.radius;
                if (sphere > 0.02f) continue;
                float s = Vector3.Dot(p - c.o, c.axis);
                // Shrinks under the hand's skin toward the cut, removed past it (inside the sphere).
                skin += cutShrink * Mathf.Clamp01((s - cutHand) / span) * Mathf.Clamp01(-sphere / 0.02f);
                skin = Mathf.Max(skin, -Mathf.Max(cutBody - s, sphere));
            }
            return skin;
        }
        var k = cuts[cutMode - 2];
        float sk = Vector3.Dot(p - k.o, k.axis);
        skin += cutShrink * Mathf.Clamp01((cutBody - sk) / span);
        return Mathf.Max(skin, Mathf.Max(cutHand - sk, (p - k.centre).magnitude - k.radius));
    }

    public float Skin(Vector3 p) => Eval(p).skin;

    // Unit gradient of the skin distance (central differences): the surface normal.
    public Vector3 Normal(Vector3 p, float h)
    {
        Vector3 g = new Vector3(
            Skin(p + new Vector3(h, 0f, 0f)) - Skin(p - new Vector3(h, 0f, 0f)),
            Skin(p + new Vector3(0f, h, 0f)) - Skin(p - new Vector3(0f, h, 0f)),
            Skin(p + new Vector3(0f, 0f, h)) - Skin(p - new Vector3(0f, 0f, h)));
        float m = g.magnitude;
        return m > 1e-9f ? g / m : Vector3.up;
    }
}
