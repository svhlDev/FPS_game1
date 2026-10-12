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
        public float cutBody, cutHand, shrink;   // per cut (the head's overlap is longer); 0 = the kernel's
    }

    public struct Ellipsoid
    {
        public Vector3 o, x, y, z, r;
        public int bone, group;
        public Vector3 ToLocal(Vector3 p) { p -= o; return new Vector3(Vector3.Dot(p, x), Vector3.Dot(p, y), Vector3.Dot(p, z)); }
    }

    // A face part (Head spec Part D) in root space: capsule (r.x radius, halfLen along y), ellipsoid or
    // half-ellipsoid (radii r), sphere (r.x). Union parts are face muscle (under the fat), subtract parts
    // cut the skin.
    public struct FacePrim
    {
        public Vector3 o, x, y, z, r;
        public float halfLen, blend;
        public int shape, op;
    }

    // Face Fix Part 2: the lower jaw. Top view (x, z in the head frame): a trapezoid from the chin (front,
    // half width chin.x) back to the jaw angles (gonion: half width, z behind the menton), its vertical
    // edges rounded (inset polygon + round); cut below by the jaw-line plane (through the menton, normal
    // jawN: up and forward) with the jaw angle's rounding, and above at `top`. Its bottom is the under-jaw
    // plane; the back edge rising from each gonion is the ramus.
    public struct Mandible
    {
        public Vector3 menton, x, y, z, jawN;
        public Vector2 q0, q1, q2, q3;      // the inset polygon (x, z from the menton), convex
        public float round, angle, top, blend;
        public int on;
    }

    // Face Fix Part 3: an eye's socket and lids. Frame: x lateral (toward the outer corner), y up, z forward.
    public struct EyeLid
    {
        public Vector3 c, x, y, z;
        public float r;                     // the eyeball's radius (the socket's size)
        public EyeShape s;
    }

    // Face Fix Part 5: an ear. Frame: u up the ear (tilted back), v toward its back edge, n out of its outer
    // face. A flat oval disc (half height h, half width w, half thickness t), a C-shaped rim round its top,
    // back and bottom (open at the front, where it meets the face), a lobe, and the bowl (concha) cut into
    // its outer face.
    public struct Ear
    {
        public Vector3 c, u, v, n;
        public float h, w, t, rimR, lobeR;
        public Vector3 lobe;                // lobe centre (root space)
        public Vector3 bowl, bowlR;         // bowl centre (root space), radii (v, u, n)
        public int on;
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
    [ReadOnly] public NativeArray<Ellipsoid> blobs, breasts;
    [ReadOnly] public NativeArray<FacePrim> face;
    [ReadOnly] public NativeArray<Digit> digits;
    [ReadOnly] public NativeArray<HandCut> cuts;
    public float fingerBlend;
    public int cutMode;                 // 0 whole body, 1 body without hands, 2 + h hand h
    public float cutBody, cutHand, cutShrink;  // cut positions along the hand (m from the wrist), shrink (m)
    public Ellipsoid skull;             // the face's frame and profile egg (face space, fat mask, wig, bake)
    public float eggTop, eggBottom;     // the egg head: horizontal radii x eggBottom at the jaw .. x eggTop at the crown
    public Ellipsoid cranium;           // Face Fix Part 2: the braincase (an egg, big end up and back) ...
    public float craniumBottom, craniumTop;
    public Mandible jaw;                // ... and the lower jaw, smooth-unioned: the head's core
    public int headIndex;
    public Vector3 eyeL, eyeR;
    public float eyeRadius;
    public EyeLid lidL, lidR;           // Face Fix Part 3 (lids: r > 0)
    public Ear earL, earR;              // Face Fix Part 5
    public float muscleBlend, fatBlendPerMetre, fatBlendMin, breastBlend, featureBlend, eyeCarveBlend;
    public float socketClearance;   // eye sockets cut through the face fat in front of the eye, plus this
    public float faceReach;         // face parts are evaluated within this distance of the skull's centre (covers every part + blend + fat)
    public float faceFatBlendMax;   // the face's fat blend radius cap (independent of the body's)
    public float fatBlendFalloff;   // fat thickness blends across bones within about this distance
    public float fatBlendMax, muscleReach;
    public float fatWaist, fatBelly, fatHips, fatButtocks;
    public int hasDna;

    public const int MaxBones = 31;
    const int KPelvis = (int)BoneKind.Pelvis, KLumbar = (int)BoneKind.Lumbar, KChest = (int)BoneKind.Chest,
              KHead = (int)BoneKind.Head, KHand = (int)BoneKind.Hand, KFoot = (int)BoneKind.Foot, KUpperArm = (int)BoneKind.UpperArm;

    public void Dispose()
    {
        if (bones.IsCreated) bones.Dispose();
        if (blobs.IsCreated) blobs.Dispose();
        if (face.IsCreated) face.Dispose();
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

    // Egg (Head spec Part C): an ellipsoid whose horizontal radii scale with height, from `bottom` at -r.y
    // to `top` at +r.y (smoothstep): big end up (cranium), small end down (jaw). Evaluated in the unscaled
    // space and shrunk by the smaller scale (and a margin for the height-varying warp), so it stays a
    // lower bound of the true distance for sampling.
    public static float EggScale(float y, float ry, float bottom, float top)
    {
        float t = Mathf.Clamp01((y + ry) / Mathf.Max(1e-5f, 2f * ry));
        return Mathf.Lerp(bottom, top, t * t * (3f - 2f * t));
    }

    public static float EggDist(Vector3 p, Vector3 r, float bottom, float top)
    {
        if (bottom <= 0f || top <= 0f) return EllipsoidDist(p, r);
        float s = EggScale(p.y, r.y, bottom, top);
        float d = EllipsoidDist(new Vector3(p.x / s, p.y, p.z / s), r);
        return d * Mathf.Min(1f, Mathf.Min(bottom, top)) * 0.9f;
    }

    // Where face fat collects (multiplies the head's fat): pooled in the cheeks, the jowls and under the
    // chin, thin over the forehead, the nose, round the eyes and on the cranium.
    public float FaceFatMask(Vector3 p)
    {
        Vector3 l = skull.ToLocal(p);
        Vector3 u = new Vector3(l.x / skull.r.x, l.y / skull.r.y, l.z / skull.r.z);
        float ax = Mathf.Abs(u.x);
        float G(float x) => Mathf.Exp(-x * x);
        float front = Mathf.Clamp01(u.z * 2f + 0.6f);
        // Face Fix Part 0: the pools only thicken the front and sides of the face (never behind the jaw: the
        // back of the neck), and only near the face's surface (a window around the profile egg).
        float fwd = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-0.15f, 0.25f, u.z));
        float shell = G((u.magnitude - 1f) / 0.35f);
        float cheeks = G((ax - 0.36f) / 0.2f) * G((u.y + 0.15f) / 0.2f) * front;   // under the eyes, toward the nose
        float jowls = G((ax - 0.45f) / 0.3f) * G((u.y + 0.72f) / 0.22f) * fwd;
        float chin = G(ax / 0.55f) * G((u.y + 0.92f) / 0.25f) * fwd;
        float pool = Mathf.Max(cheeks, Mathf.Max(jowls, chin)) * shell;
        // Lips carry almost no fat (so they stay visible on any face).
        float mouth = G(ax / 0.32f) * G((u.y + 0.48f) / 0.1f) * front;
        return Mathf.Lerp(Mathf.Lerp(0.35f, 1.7f, pool), 0.12f, mouth);
    }

    // Signed distance to a polygon (Inigo Quilez's sdPolygon), 4 vertices.
    static float Poly4(Vector2 p, Vector2 a, Vector2 b, Vector2 c, Vector2 d)
    {
        float dd = (p - a).sqrMagnitude, s = 1f;
        PolyEdge(p, a, d, ref dd, ref s); PolyEdge(p, b, a, ref dd, ref s); PolyEdge(p, c, b, ref dd, ref s); PolyEdge(p, d, c, ref dd, ref s);
        return s * Mathf.Sqrt(dd);
    }

    static void PolyEdge(Vector2 p, Vector2 vi, Vector2 vj, ref float dd, ref float s)
    {
        Vector2 e = vj - vi, w = p - vi;
        Vector2 bb = w - e * Mathf.Clamp01(Vector2.Dot(w, e) / Mathf.Max(1e-12f, e.sqrMagnitude));
        dd = Mathf.Min(dd, bb.sqrMagnitude);
        bool c1 = p.y >= vi.y, c2 = p.y < vj.y, c3 = e.x * w.y > e.y * w.x;
        if ((c1 && c2 && c3) || (!c1 && !c2 && !c3)) s = -s;
    }

    public static float MandibleDist(in Mandible m, Vector3 p)
    {
        Vector3 d = p - m.menton;
        Vector2 q = new Vector2(Vector3.Dot(d, m.x), Vector3.Dot(d, m.z));
        float d2 = Poly4(q, m.q0, m.q1, m.q2, m.q3) - m.round;
        float below = -Vector3.Dot(d, m.jawN);
        float above = Vector3.Dot(d, m.y) - m.top;
        return Mathf.Max(SMax(d2, below, m.angle), above);
    }

    // The head's core: the cranium smooth-unioned with the lower jaw.
    public float HeadCore(Vector3 p)
    {
        float c = EggDist(cranium.ToLocal(p), cranium.r, craniumBottom, craniumTop);
        return jaw.on != 0 ? SMin(c, MandibleDist(jaw, p), jaw.blend) : c;
    }

    public static float FaceDist(FacePrim f, Vector3 p)
    {
        p -= f.o;
        var l = new Vector3(Vector3.Dot(p, f.x), Vector3.Dot(p, f.y), Vector3.Dot(p, f.z));
        switch (f.shape)
        {
            case (int)FacePartShape.Capsule: l.y -= Mathf.Clamp(l.y, -f.halfLen, f.halfLen); return l.magnitude - f.r.x;
            case (int)FacePartShape.Sphere: return l.magnitude - f.r.x;
            case (int)FacePartShape.HalfEllipsoidUpper: return Mathf.Max(EllipsoidDist(l, f.r), -l.y);
            case (int)FacePartShape.HalfEllipsoidLower: return Mathf.Max(EllipsoidDist(l, f.r), l.y);
            default: return EllipsoidDist(l, f.r);
        }
    }

    static float SegmentDist(Vector3 p, Vector3 a, Vector3 b)
    {
        Vector3 ab = b - a;
        float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / Mathf.Max(1e-8f, ab.sqrMagnitude));
        return (p - (a + ab * t)).magnitude;
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
        // The head: nothing more than 3 cm under its joint (the chin line). The egg's distance is a loose lower
        // bound far away: without this a big chest / breasts read as nearer the head than the chest, and
        // their skin would be labelled, split and weighted as head (Face Fix Part 0).
        if (b.kind == KHead) return Mathf.Max(HeadCore(p), Vector3.Dot(b.o - p, b.y) - 0.03f);

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
        float limb = Mathf.Min(d1, d2) * dr; // squashing z by 1/dr stretches distances: stay a lower bound
        // Arm roots: a capsule from the shoulder joint in toward the chest's axis, so a lean, narrow chest
        // never leaves the arm floating beside it (the joint blend alone can't bridge that gap).
        if (b.kind == KUpperArm && b.parent >= 0 && IsTorso(bones[b.parent].kind))
        {
            var pa = bones[b.parent];
            Vector3 inner = pa.o + pa.y * Vector3.Dot(b.o - pa.o, pa.y);
            limb = Mathf.Min(limb, SegmentDist(p, b.o, Vector3.Lerp(b.o, inner, 0.8f)) - b.girth.x * 0.85f);
        }
        return limb;
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
        // The face carries the head's own fat (DEX), not the neck's and chest's blended in: over the head
        // (closer to it than to its parent, the neck) the fat fades to the head's profile.
        if (headIndex >= 0)
        {
            int neck = bones[headIndex].parent;
            float m = neck >= 0 ? Mathf.Clamp01((d[neck] - d[headIndex]) / 0.03f + 0.5f) : 1f;
            if (m > 0f) fat = Mathf.Lerp(fat, FatAt(p, headIndex) * FaceFatMask(p), m);
        }
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
        // Face (Head spec Part D): the parts are face muscle on the egg (each with its own small blend), so
        // the face fat rounds over them like the body's blobs.
        // (Face Fix Part 0) Every part is evaluated wherever it could matter: within faceReach of the head
        // (its extent + blend + the thickest face fat), no hard cut-off closer in.
        bool nearHead = headIndex >= 0 && (p - skull.o).sqrMagnitude < faceReach * faceReach;
        if (nearHead)
        {
            // Each part blends onto the body's surface with its own small radius, never chained part onto
            // part (chained smooth mins add their bulges up: a snout where a dozen parts overlap). The fat
            // layer gets the parts' hard union once, with a capped blend.
            float muscleBase = muscle, faceHard = float.MaxValue;
            for (int j = 0; j < face.Length; j++)
            {
                int op = face[j].op;
                if (op == (int)FacePartOp.Union)
                {
                    float v = FaceDist(face[j], p);
                    muscle = Mathf.Min(muscle, SMin(muscleBase, v, face[j].blend));
                    faceHard = Mathf.Min(faceHard, v);
                }
            }
            if (faceHard < float.MaxValue) soft = SMin(soft, faceHard, Mathf.Min(kFat * 0.5f, faceFatBlendMax));
            for (int j = 0; j < face.Length; j++)
                if (face[j].op == (int)FacePartOp.Carve)
                {
                    // Hollows in the bone and muscle (temples): the fat fills them.
                    float v = -FaceDist(face[j], p);
                    muscle = SMax(muscle, v, face[j].blend);
                    soft = SMax(soft, v, face[j].blend);
                }
        }
        // Eye sockets (Face Fix Part 3): a hollow in the bone layer round each eyeball, deeper than wide,
        // inside the rim of the brow ridge, cheekbone and nose bridge; the fat follows it.
        if (lidL.r > 0f && nearHead)
        {
            float sk = Mathf.Min(Socket(lidL, p), Socket(lidR, p));
            muscle = SMax(muscle, -sk, 0.004f);
            soft = SMax(soft, -sk, 0.004f);
        }
        float skin = Mathf.Min(muscle, soft - fat);
        for (int j = 0; j < breasts.Length; j++) skin = SMin(skin, EllipsoidDist(breasts[j].ToLocal(p), breasts[j].r), breastBlend);
        if (nearHead)
            for (int j = 0; j < face.Length; j++)
                if (face[j].op == (int)FacePartOp.Subtract) skin = SMax(skin, -FaceDist(face[j], p), face[j].blend);
        if (eyeRadius > 0f)
        {
            // Eye sockets: the eyeball's sphere, and a channel straight out through the face fat (the local
            // fat, plus a clearance) so a fat face never buries the eyes.
            Vector3 outDir = skull.z * (fat + eyeRadius + socketClearance);
            if (lidL.r > 0f)
            {
                // Face Fix Part 3: a narrower channel (the lids close round it), then the lids and their opening.
                float rr = eyeRadius * 0.7f;
                skin = SMax(skin, -Mathf.Min(SegmentDist(p, eyeL, eyeL + outDir), SegmentDist(p, eyeR, eyeR + outDir)) + rr, eyeCarveBlend);
                skin = Lids(lidL, p, skin);
                skin = Lids(lidR, p, skin);
            }
            else
            {
                float rr = eyeRadius * 0.92f;
                skin = SMax(skin, -Mathf.Min((p - eyeL).magnitude - eyeRadius, SegmentDist(p, eyeL, eyeL + outDir) - rr), eyeCarveBlend);
                skin = SMax(skin, -Mathf.Min((p - eyeR).magnitude - eyeRadius, SegmentDist(p, eyeR, eyeR + outDir) - rr), eyeCarveBlend);
            }
        }
        // Ears (after the fat: they stay thin on any face), blended onto the side of the head.
        if (earL.on != 0 && nearHead) skin = SMin(skin, Mathf.Min(EarDist(earL, p), EarDist(earR, p)), 0.004f);
        if (cutMode > 0) skin = Cut(p, skin);
        return new Sample { skin = skin, muscle = muscle, core = core, fat = fat, nearest = nearest };
    }

    public static float EarDist(in Ear e, Vector3 p)
    {
        Vector3 d = p - e.c;
        float bound = d.magnitude - (e.h + 0.01f);
        if (bound > 0.01f) return bound;
        float a = Vector3.Dot(d, e.u), b = Vector3.Dot(d, e.v), c = Vector3.Dot(d, e.n);
        float disc = EllipsoidDist(new Vector3(b, a, c), new Vector3(e.w, e.h, e.t));
        // The rim: a tube along the oval's outline, standing a little out of the outer face; none at the front.
        float k0 = new Vector2(b / e.w, a / e.h).magnitude, k1 = new Vector2(b / (e.w * e.w), a / (e.h * e.h)).magnitude;
        float edge = k1 > 1e-6f ? k0 * (k0 - 1f) / k1 : -Mathf.Min(e.w, e.h);
        float rim = new Vector2(edge + e.rimR * 0.7f, c - e.t * 0.9f).magnitude - e.rimR;
        rim += Mathf.Max(0f, -b / e.w - 0.25f) * 0.03f;
        float ear = SMin(Mathf.Min(disc, rim), (p - e.lobe).magnitude - e.lobeR, 0.003f);
        // The bowl, cut into the outer face.
        Vector3 q = p - e.bowl;
        float bowl = EllipsoidDist(new Vector3(Vector3.Dot(q, e.v), Vector3.Dot(q, e.u), Vector3.Dot(q, e.n)), e.bowlR);
        return SMax(ear, -bowl, 0.0015f);
    }

    static Vector3 EyeLocal(in EyeLid e, Vector3 p) { p -= e.c; return new Vector3(Vector3.Dot(p, e.x), Vector3.Dot(p, e.y), Vector3.Dot(p, e.z)); }

    // The socket: an ellipsoid round the eyeball (a little behind its centre), 1.3 x its radius wide,
    // 1.2 tall, 1.4 deep.
    static float Socket(in EyeLid e, Vector3 p)
    {
        Vector3 l = EyeLocal(e, p) + new Vector3(0f, 0f, e.r * 0.15f);
        return EllipsoidDist(l, new Vector3(e.r * 1.3f, e.r * 1.2f, e.r * 1.4f));
    }

    // The lids: a skin shell round the eyeball (upper thickU, lower thickL), blended into the socket's
    // skin; the opening cut through it down to the eyeball, OpeningMargin wider than the almond all round
    // (the head's grid can't resolve the almond's points: the lid meshes on the eye, FaceAnimator.LidMesh,
    // make the exact opening and tuck under this skin); the crease above the upper lid (a groove,
    // shallower with face fat).
    public const float OpeningMargin = 0.003f;
    static float Lids(in EyeLid e, Vector3 p, float skin)
    {
        Vector3 l = EyeLocal(e, p);
        float R = l.magnitude;
        if (R > e.r * 3f) return skin;
        Vector3 d = R > 1e-6f ? l / R : Vector3.forward;
        float up = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-0.2f, 0.2f, d.y));
        float thick = Mathf.Lerp(e.s.thickL, e.s.thickU, up);
        skin = SMin(skin, R - (e.r + thick), 0.006f);
        // The opening: inside the (widened) almond, between the eyeball and just outside the lids.
        float ang = EyeShape.Opening(e.s, d) * R - OpeningMargin;
        float carve = Mathf.Max(ang, Mathf.Max(e.r * 0.8f - R, R - (e.r + thick + 0.004f)));
        skin = SMax(skin, -carve, 0.0007f);
        if (e.s.creaseDepth > 0f && d.z > 0f)
        {
            float phi = Mathf.Atan2(d.x, d.z), theta = Mathf.Asin(Mathf.Clamp(d.y, -1f, 1f));
            float t = EyeShape.T(e.s, phi);
            if (Mathf.Abs(t) < 1.2f)
            {
                float fade = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.8f, 1.2f, Mathf.Abs(t)));
                float along = (theta - (EyeShape.Upper(e.s, t) + e.s.crease)) * R - OpeningMargin;
                float groove = new Vector2(along, R - (e.r + e.s.thickU)).magnitude - e.s.creaseDepth * fade;
                skin = SMax(skin, -groove, 0.001f);
            }
        }
        return skin;
    }

    // The meshing cuts (see the top). s = distance along the hand from the wrist. Each cut may carry its
    // own positions and shrink (the head's seam needs a longer overlap: its grid is much finer than the body's).
    float Cut(Vector3 p, float skin)
    {
        if (cutMode == 1)
        {
            for (int h = 0; h < cuts.Length; h++)
            {
                var c = cuts[h];
                float sphere = (p - c.centre).magnitude - c.radius;
                if (sphere > 0.02f) continue;
                float cb = c.shrink > 0f ? c.cutBody : cutBody, ch = c.shrink > 0f ? c.cutHand : cutHand, sh = c.shrink > 0f ? c.shrink : cutShrink;
                float span = Mathf.Max(1e-4f, cb - ch);
                float s = Vector3.Dot(p - c.o, c.axis);
                // Shrinks under the hand's skin toward the cut, removed past it (inside the sphere).
                skin += sh * Mathf.Clamp01((s - ch) / span) * Mathf.Clamp01(-sphere / 0.02f);
                skin = Mathf.Max(skin, -Mathf.Max(cb - s, sphere));
            }
            return skin;
        }
        var k = cuts[cutMode - 2];
        float kb = k.shrink > 0f ? k.cutBody : cutBody, kh = k.shrink > 0f ? k.cutHand : cutHand, ks = k.shrink > 0f ? k.shrink : cutShrink;
        float kspan = Mathf.Max(1e-4f, kb - kh);
        float sk = Vector3.Dot(p - k.o, k.axis);
        skin += ks * Mathf.Clamp01((kb - sk) / kspan);
        return Mathf.Max(skin, Mathf.Max(kh - sk, (p - k.centre).magnitude - k.radius));
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
