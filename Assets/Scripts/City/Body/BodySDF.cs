using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

// Phase 3: the body as a signed distance field (negative inside, positive outside), layered like
// anatomy: core -> muscle -> fat -> skin.
//   Core   : a round cone per bone half (start->mid, mid->end) from the core girth profile; torso bones
//            are flattened front-to-back (depthRatio). Each bone smooth-unions only with its parent
//            (small radius), so joints flow and nothing else merges.
//   Muscle : ellipsoid blobs anchored in bone space (pectorals, deltoids, biceps, triceps, forearm,
//            trapezius, lats, a 2 x 3 abdominal grid, glutes, quads, hamstrings, calves), sized from the
//            bone's muscle layer x the group's DNA volume, smooth-unioned onto the core with a small radius.
//   Fat    : the same layers again with a blend radius that grows with the local fat thickness (rounds
//            over and softens the muscle shapes), then offset outward by that thickness. Thin fat =
//            muscle definition shows (ripped); thick fat = smooth and round.
//   Breasts: two ellipsoids on the chest (DNA size and droop), medium blend.
//   Head   : an egg (big end up, the jaw end narrower: Head spec Part C) scaled and elongated (up and
//            back) by INT, brow ridge, cheekbones, jaw, nose and chin blended in (seed sizes), eye sockets
//            carved (smooth subtraction) at EyeL / EyeR. Face positions are fractions of the egg's radii,
//            their x / z scaled by the egg's width at their height, so they follow any head.
//   Hands  : sausage hands: a rounded palm block, finger and thumb capsules (the digit bones) blended on
//            with a small radius, never with each other. Feet: rounded wedges.
// Built in the bind pose (an A-pose: arms out, legs slightly apart, so limbs stay clear of the body).
// All positions are in the body root's space.
// Evaluation lives in BodySdfKernel (unmanaged, shared with the Burst mesher); this class builds it.
// Dispose when done (the kernel holds native arrays).
public class BodySDF : IDisposable
{
    // ---------- tunables (BodyRules.sdf; copied per body, so they can be overridden on one) ----------
    public float jointBlend, jointBlendGirth, torsoBlend, muscleBlend, fatBlendPerMetre, fatBlendMin, fatBlendMax,
                 fatBlendFalloff, breastBlend, featureBlend, eyeCarveBlend, muscleReach;
    readonly SdfRules SR;

    struct Frame { public Vector3 o, x, y, z; public Vector3 ToLocal(Vector3 p) { p -= o; return new Vector3(Vector3.Dot(p, x), Vector3.Dot(p, y), Vector3.Dot(p, z)); } }
    struct Blob { public int bone; public Frame f; public Vector3 r; public MuscleGroup group; }
    struct Feature { public Frame f; public Vector3 r; }

    readonly BodyPlan plan;
    public readonly Vector3[] start, end;          // posed bone ends (root space)
    public readonly Matrix4x4[] bind;              // each joint's transform in this pose, relative to the root
    readonly Frame[] frames;                       // bone frames: y along the bone, z the body's front
    readonly float[] depthRatio;
    readonly List<Blob> blobs = new List<Blob>();
    readonly List<BodySdfKernel.FacePrim> faceParts = new List<BodySdfKernel.FacePrim>();
    public readonly List<string> FacePartNames = new List<string>();
    public IReadOnlyList<BodySdfKernel.FacePrim> FacePrims => faceParts;
    readonly Dictionary<int, float> snapOffsets = new Dictionary<int, float>();
    readonly Dictionary<int, Vector3> pushDirs = new Dictionary<int, Vector3>();   // presses standing out in a set direction (root space)   // snapped face parts: their offset from the skin (m)
    // Anatomy Rework Part 1: parts placed on the base (index, target, axis or tip, size (m), press (m), offset, root offset).
    readonly List<(int i, Vector3 target, Vector3 axis, Vector3 size, float press, float offset, float rootOffset)> pressed = new List<(int, Vector3, Vector3, Vector3, float, float, float)>();
    public Vector3 JawPivot, MouthCentre;   // the jaw's hinge and the mouth line's centre (root space)
    // Face Metrics: the snapped skin features (the grooves and ridges cut / added on the finished skin:
    // Philtrum, MouthLine, MouthLineSide, MouthCorner, Nasolabial, NoseLipCrease, AlarCrease, PhiltrumRidge,
    // VermilionBorder, ChinGroove, DoubleChinCrease) on or off - one switch, for measuring with and without.
    public static bool SkinFeatures = true;
    readonly List<Feature> breasts = new List<Feature>();
    Feature skull;                                 // the face's frame / profile egg (fat mask, wig, bake)
    Feature skullBase;                             // the egg without INT's growth (the brain layer scales between)
    Feature cranium;                               // Face Fix Part 2: the braincase ...
    BodySdfKernel.Mandible mandible;               // ... and the lower jaw (the head's core)
    float craniumBottom, craniumTop;
    public float EggTop, EggBottom;
    public Vector3 EyeL, EyeR;
    public float EyeRadius;
    public HeadCanon Canon;                        // Face Fix Part 1: the proportions the face is built on
    public EyeShape Eye;                           // Face Fix Part 3: the eye opening and lids
    BodySdfKernel.EyeLid LidL, LidR;
    BodySdfKernel.Ear EarLeft, EarRight;
    public Vector3 EarL => EarLeft.c;
    public Vector3 EarR => EarRight.c;
    public Bounds bounds;
    readonly float[] jointK;
    readonly int headIndex;
    readonly int kernelBones;                      // bones before the digits (the kernel's)
    BodySdfKernel kernel;
    // Hands: each meshed on its own grid (HandBounds), cut from the body (see BodySdfKernel).
    public readonly List<Bounds> HandBounds = new List<Bounds>();
    public int HandCount => HandBounds.Count;
    // Head spec Part D: the head meshed on its own finer grid (cut mode 2 + HandCount). Empty bounds = none.
    public Bounds HeadBounds;
    public bool HasHeadPiece => HeadBounds.size.x > 0f;
    public int HeadMode => 2 + HandCount;
    Vector3 headCutO, headCutAxis, headCutC; float headCutR;
    public Vector3 HeadCutOrigin => headCutO;
    public Vector3 HeadCutAxis => headCutAxis;
    public float HeadCutRadius => headCutR;

    public BodyPlan Plan => plan;
    public BodySdfKernel Kernel => kernel;

    // A-pose: arms out armSpread degrees, legs legSpread (local rotations per bone).
    public static Quaternion[] APose(BodyPlan plan)
    {
        var sr = BodyRules.Default.sdf;
        float ArmSpread = sr.armSpread, LegSpread = sr.legSpread;
        var rot = new Quaternion[plan.bones.Count];
        for (int i = 0; i < rot.Length; i++)
        {
            var b = plan.bones[i];
            rot[i] = b.restRotation;
            if (b.kind == BoneKind.UpperArm && b.side != 0) rot[i] = Quaternion.Euler(0f, 0f, b.side * ArmSpread) * rot[i];
            if (b.kind == BoneKind.Thigh) rot[i] = Quaternion.Euler(0f, 0f, b.side * LegSpread) * rot[i];
        }
        return rot;
    }

    public BodySDF(BodyPlan plan, Quaternion[] pose = null)
    {
        this.plan = plan;
        SR = BodyRules.Default.sdf;
        jointBlend = SR.jointBlend; jointBlendGirth = SR.jointBlendGirth; torsoBlend = SR.torsoBlend; muscleBlend = SR.muscleBlend;
        fatBlendPerMetre = SR.fatBlendPerMetre; fatBlendMin = SR.fatBlendMin; fatBlendMax = SR.fatBlendMax; fatBlendFalloff = SR.fatBlendFalloff;
        breastBlend = SR.breastBlend; featureBlend = SR.featureBlend; eyeCarveBlend = SR.eyeCarveBlend; muscleReach = SR.muscleReach;
        pose ??= APose(plan);
        int n = plan.bones.Count;
        kernelBones = 0;
        while (kernelBones < n && plan.bones[kernelBones].kind != BoneKind.Finger) kernelBones++;
        for (int i = kernelBones; i < n; i++)
            if (plan.bones[i].kind != BoneKind.Finger) throw new ArgumentException("digits must come after every other bone");
        if (kernelBones > BodySdfKernel.MaxBones) throw new ArgumentException($"{kernelBones} bones (max {BodySdfKernel.MaxBones})");
        start = new Vector3[n]; end = new Vector3[n]; frames = new Frame[n]; depthRatio = new float[n];
        jointK = new float[n];
        bind = new Matrix4x4[n];
        var world = new Quaternion[n];
        for (int i = 0; i < n; i++)
        {
            var b = plan.bones[i];
            Quaternion pr = b.parent >= 0 ? world[b.parent] : Quaternion.identity;
            Vector3 pp = b.parent >= 0 ? start[b.parent] : Vector3.zero;
            start[i] = pp + pr * b.localPos;
            world[i] = pr * pose[i];
            Vector3 dir = (world[i] * b.dir).normalized;
            end[i] = start[i] + dir * b.length;
            bind[i] = Matrix4x4.TRS(start[i], world[i], Vector3.one);
            Vector3 fwd = Vector3.ProjectOnPlane(world[i] * Vector3.forward, dir);
            if (b.kind == BoneKind.Foot) fwd = Vector3.ProjectOnPlane(Vector3.up, dir);
            if (fwd.sqrMagnitude < 1e-6f) fwd = Vector3.ProjectOnPlane(Vector3.forward, dir);
            fwd.Normalize();
            // x = the body's right for every bone (the cross product flips for bones pointing down).
            Vector3 right = Vector3.Cross(dir, fwd);
            if (Vector3.Dot(right, Vector3.right) < 0f) right = -right;
            frames[i] = new Frame { o = start[i], y = dir, z = fwd, x = right };
            depthRatio[i] = b.kind == BoneKind.Pelvis ? SR.pelvisDepth : b.kind == BoneKind.Lumbar ? SR.lumbarDepth : b.kind == BoneKind.Chest ? SR.chestDepth : 1f;
        }
        for (int i = 0; i < n; i++)
        {
            int pa = plan.bones[i].parent;
            if (pa < 0) continue;
            // Torso segments meet flat (their caps line up): a small blend. Limbs blend by girth.
            bool torsoPair = IsTorso(plan.bones[i].kind) && IsTorso(plan.bones[pa].kind);
            jointK[i] = torsoPair ? torsoBlend : jointBlend + jointBlendGirth * Mathf.Min(plan.bones[i].girth.y, plan.bones[pa].girth.y);
        }
        headIndex = plan.Index("Head");
        BuildMuscles();
        BuildHead();
        // The head meets the neck with a small blend, so the under-jaw plane turns down into the neck.
        if (headIndex >= 0) jointK[headIndex] = BodyRules.Default.face.neckBlend;
        BuildBreasts();
        // Bounds: every bone's ends padded by its outer girth.
        bounds = new Bounds(start[0], Vector3.zero);
        for (int i = 0; i < n; i++)
        {
            float pad = Mathf.Max(plan.bones[i].Outer.x, plan.bones[i].Outer.y, plan.bones[i].Outer.z) * 1.6f + 0.03f;
            bounds.Encapsulate(new Bounds(start[i], Vector3.one * pad * 2f));
            bounds.Encapsulate(new Bounds(end[i], Vector3.one * pad * 2f));
        }
        foreach (var br in breasts) bounds.Encapsulate(new Bounds(br.f.o, br.r * 2.4f));
        // Hand grids: a box round the forearm's overlap stretch, the palm and the digits (much smaller than
        // the cut's bounding sphere: the grid is fine).
        var hr = BodyRules.Default.hands;
        for (int i = 0; i < kernelBones; i++)
            if (plan.bones[i].kind == BoneKind.Hand)
            {
                var b = plan.bones[i]; var f = frames[i];
                const float pad = 0.004f;
                float wristR = Mathf.Max(b.Outer.x, b.parent >= 0 ? plan.bones[b.parent].Outer.y : 0f) * 1.4f + pad;
                Vector3 w0 = start[i] - f.y * hr.cutOverlap;
                var hb = new Bounds(w0, Vector3.one * wristR * 2f);
                hb.Encapsulate(new Bounds(start[i], Vector3.one * wristR * 2f));
                float R = b.Outer.y, Lp = b.length * hr.palmLength;
                Vector3 half = new Vector3(R * hr.palmThickness * 0.5f + pad, Lp * 0.5f + pad, R * hr.palmWidth * 0.5f + pad);
                Vector3 pc = start[i] + f.y * (Lp * 0.5f);
                for (int c = 0; c < 8; c++)
                    hb.Encapsulate(pc + f.x * ((c & 1) != 0 ? half.x : -half.x) + f.y * ((c & 2) != 0 ? half.y : -half.y) + f.z * ((c & 4) != 0 ? half.z : -half.z));
                for (int j = kernelBones; j < n; j++)
                {
                    if (HandOf(j) != i) continue;
                    Vector3 off = bind[j].MultiplyVector(plan.bones[j].shapeOffset);
                    float rr = plan.bones[j].girth.x + pad;
                    hb.Encapsulate(new Bounds(start[j] + off, Vector3.one * rr * 2f));
                    hb.Encapsulate(new Bounds(end[j] + off, Vector3.one * rr * 2f));
                }
                HandBounds.Add(hb);
            }
        bounds.Encapsulate(new Vector3(bounds.center.x, 0f, bounds.center.z)); // the soles
        if (headIndex >= 0)
        {
            var mr = BodyRules.Default.mesh;
            headCutAxis = frames[headIndex].y;
            headCutO = start[headIndex] - headCutAxis * mr.headCutBelow;
            headCutC = skull.f.o;
            // The cut sphere: at the plane it takes just the neck (and a margin), so everything it removes from
            // the body above the plane lies in the head's grid below; it still holds the whole head.
            var nb0 = plan.bones[plan.bones[headIndex].parent];
            float rN = Mathf.Max(nb0.Outer.x, Mathf.Max(nb0.Outer.y, nb0.Outer.z)) + Mathf.Max(nb0.fat.x, Mathf.Max(nb0.fat.y, nb0.fat.z)) + 0.025f;
            float dPlane = Vector3.Dot(headCutC - headCutO, headCutAxis);
            // (Face Metrics Fix 4: the neck's axis lies behind the skull's centre - measured round the neck's
            // own axis, the sphere clipped the nape above the plane and left a buried wall there.)
            int nk = plan.bones[headIndex].parent;
            Vector3 na = start[nk], nbv = start[headIndex];
            float tn = Mathf.Clamp01(Vector3.Dot(headCutO - na, nbv - na) / Mathf.Max(1e-6f, (nbv - na).sqrMagnitude));
            Vector3 neckAt = na + (nbv - na) * tn;
            float offN = Vector3.ProjectOnPlane(headCutC - neckAt, headCutAxis).magnitude;
            rN += offN;
            float big = (skull.f.o - headCutC).magnitude + Mathf.Max(skull.r.x, Mathf.Max(skull.r.y, skull.r.z)) * 1.2f + 0.03f;   // the whole egg (with INT's growth)
            headCutR = Mathf.Max(Mathf.Sqrt(dPlane * dPlane + rN * rN), big);
            // The head's grid: just the head above the cut (cranium, face, ears, the neck down to the cut, fat),
            // not the cut sphere's whole cube - a third of the cells at the head's fine spacing.
            var hb0 = plan.bones[headIndex];
            float fat = Mathf.Max(hb0.fat.x, Mathf.Max(hb0.fat.y, hb0.fat.z)) * 1.7f + 0.02f;
            float top = Mathf.Max(skull.f.o.y + skull.r.y * 1.05f, Canon.o.y + Canon.crown);
            float back = Mathf.Min(skull.f.o.z - skull.r.z * 1.1f, start[headIndex].z - Canon.halfD);
            // The sphere's width a little above the plane, where the body (a heavy neck's back, the trapezius)
            // still reaches into it: all of that must be in the grid too.
            float up = Mathf.Min(dPlane, 0.06f);
            float capR = Mathf.Sqrt(Mathf.Max(0f, headCutR * headCutR - (dPlane - up) * (dPlane - up))) + 0.01f;
            var lo = new Vector3(Mathf.Min(Canon.o.x - Canon.halfW * 1.45f - fat, headCutO.x - capR), headCutO.y - 0.01f, Mathf.Min(back - fat, headCutO.z - capR));
            var hi = new Vector3(Mathf.Max(Canon.o.x + Canon.halfW * 1.45f + fat, headCutO.x + capR), top + fat, Mathf.Max(Canon.o.z + Canon.halfD * 1.75f + fat, headCutO.z + capR));
            HeadBounds = new Bounds((lo + hi) * 0.5f, hi - lo);
        }
        kernel = BuildKernel(Allocator.Persistent);
        PressParts();
        SnapFaceParts();
        if (!SkinFeatures) SnapMouthCentre();
        BuildEars();
        FitHeadCut();
    }

    // (Without the skin features there's no snapped mouth line: the mouth's centre onto the skin from the front.)
    void SnapMouthCentre()
    {
        if (headIndex < 0) return;
        var fz = frames[headIndex].z;
        Vector3 p = MouthCentre + fz * 0.05f;
        for (int it = 0; it < 300; it++)
        {
            float d = kernel.Eval(p).skin;
            if (d < 0.0002f) break;
            p -= fz * Mathf.Clamp(d * 0.9f, 0.0002f, 0.01f);
        }
        MouthCentre = p;
    }

    // Face Metrics Fix 4: the head's cut sphere must hold the whole neck above the plane (a heavy nape isn't
    // round about the neck's bone): out from the neck's axis at a few heights above the plane, to the skin;
    // the sphere (and the head's grid) grow to hold every crossing with a margin. Else the sphere clips the
    // nape and leaves a buried wall in the head piece.
    void FitHeadCut()
    {
        if (!HasHeadPiece || headIndex < 0) return;
        int nk = plan.bones[headIndex].parent;
        Vector3 na = start[nk], nb = start[headIndex];
        var k = kernel; k.cutMode = 0;
        Vector3 ax = headCutAxis, t1 = Vector3.Cross(ax, Vector3.forward).sqrMagnitude > 1e-6f ? Vector3.Cross(ax, Vector3.forward).normalized : Vector3.right, t2 = Vector3.Cross(t1, ax);
        float need = headCutR; var lo = HeadBounds.min; var hi = HeadBounds.max;
        float dPlane = Vector3.Dot(headCutC - headCutO, ax);
        for (float h = 0f; h <= Mathf.Min(0.06f, dPlane); h += 0.015f)
        {
            Vector3 o = headCutO + ax * h;
            float tn = Mathf.Clamp01(Vector3.Dot(o - na, nb - na) / Mathf.Max(1e-6f, (nb - na).sqrMagnitude));
            Vector3 c0 = na + (nb - na) * tn; c0 += ax * Vector3.Dot(o - c0, ax);
            for (int a = 0; a < 24; a++)
            {
                float ang = a * Mathf.PI * 2f / 24f;
                Vector3 dir = t1 * Mathf.Cos(ang) + t2 * Mathf.Sin(ang);
                float r = 0f;
                for (int it = 0; it < 200 && r < 0.25f; it++)
                {
                    float sd = k.Eval(c0 + dir * r).skin;
                    if (sd > 0f) break;
                    r += Mathf.Clamp(-sd * 0.9f, 0.001f, 0.01f);
                }
                Vector3 p = c0 + dir * r;
                need = Mathf.Max(need, (p - headCutC).magnitude + 0.015f);
                lo = Vector3.Min(lo, p - Vector3.one * 0.015f); hi = Vector3.Max(hi, p + Vector3.one * 0.015f);
            }
        }
        if (need > headCutR)
        {
            headCutR = need;
            var cuts = kernel.cuts; var hc = cuts[cuts.Length - 1]; hc.radius = need; cuts[cuts.Length - 1] = hc;
            // (The grid holds the sphere's width a little above the plane: whatever the body loses to it.)
            float up = Mathf.Min(dPlane, 0.06f);
            float capR = Mathf.Sqrt(Mathf.Max(0f, need * need - (dPlane - up) * (dPlane - up))) + 0.01f;
            lo = Vector3.Min(lo, new Vector3(headCutO.x - capR, lo.y, headCutO.z - capR));
            hi = Vector3.Max(hi, new Vector3(headCutO.x + capR, hi.y, headCutO.z + capR));
        }
        HeadBounds = new Bounds((lo + hi) * 0.5f, hi - lo);
    }

    // Face Fix Part 5: the ears, on the canon (from the brow line to the nose base, their centre at the
    // canon's ear depth), their front edge on the side of the head (found on the finished head), tilted
    // back, the back edge flaring out by a seeded angle. Size and flare vary with the seed.
    void BuildEars()
    {
        if (headIndex < 0) return;
        var fd = BodyRules.Default.face;
        var f = frames[headIndex];
        var rng = new PcgRandom(plan.sheet.seed, "ears");
        float h = (Canon.brow - Canon.noseBase) * 0.5f * (1f + fd.earSizeVariation * rng.Signed());
        float w = h * fd.earWidth;
        float flare = (fd.earFlare + fd.earFlareVariation * rng.Signed()) * Mathf.Deg2Rad, tilt = fd.earTilt * Mathf.Deg2Rad;
        float yc = (Canon.brow + Canon.noseBase) * 0.5f;
        BodySdfKernel.Ear Make(float s)
        {
            // The attachment: the side of the head at the ear's front edge.
            Vector3 front = Canon.o + f.y * yc + f.z * (Canon.earZ + w * 0.55f);
            Vector3 p = front + f.x * (s * Canon.halfW * 2f);
            for (int it = 0; it < 400; it++)
            {
                float d = kernel.Eval(p).skin;
                if (d <= 0f) break;
                p -= f.x * s * Mathf.Clamp(d * 0.9f, 0.0002f, 0.02f);
            }
            Vector3 back = (f.x * (s * Mathf.Sin(flare)) - f.z * Mathf.Cos(flare)).normalized;
            Vector3 n = (f.x * (s * Mathf.Cos(flare)) + f.z * Mathf.Sin(flare)).normalized;
            Vector3 u = (f.y * Mathf.Cos(tilt) + back * Mathf.Sin(tilt)).normalized;
            Vector3 v = (back * Mathf.Cos(tilt) - f.y * Mathf.Sin(tilt)).normalized;
            float t = fd.earThickness;
            Vector3 c = p + v * w * 0.95f + n * (t * 0.5f);
            // How much muscle and fat lie under the root (the ear rides on them at the fill levels): in from
            // the skin to the muscle surface, then to the bone.
            Vector3 drift = f.x * s;
            float Depth(float ms, float fs)
            {
                var kk = kernel; kk.muscleScale = ms; kk.fatScale = fs; kk.earL.on = 0; kk.earR.on = 0;
                float dd = 0f;
                for (int it = 0; it < 200 && dd < 0.06f; it++)
                {
                    float sd = kk.Eval(p - drift * dd).skin;
                    if (sd <= 0f) break;
                    dd += Mathf.Clamp(sd * 0.9f, 0.0002f, 0.01f);
                }
                return dd;
            }
            float toMuscle = Depth(1f, 0f), toBone = Depth(0f, 0f);
            return new BodySdfKernel.Ear
            {
                drift = drift, driftF = toMuscle, driftM = Mathf.Max(0f, toBone - toMuscle),
                c = c, u = u, v = v, n = n, h = h, w = w, t = t, rimR = fd.earRim, lobeR = w * 0.34f,
                lobe = c - u * h * 0.78f - v * w * 0.15f,
                bowl = c + n * (t + 0.003f) - u * h * 0.1f - v * w * 0.1f,
                bowlR = new Vector3(w * 0.6f, h * 0.55f, 0.0065f),
                on = 1,
            };
        }
        EarLeft = Make(-1f);
        EarRight = Make(1f);
        kernel.earL = EarLeft; kernel.earR = EarRight;
    }

    // Anatomy Rework Part 1: presses, carves, muscles and pieces placed on the base (the egg and its
    // planes): each target snapped onto it along the line from the egg's centre, then
    //   press / muscle: a flattened ellipsoid, long axis along the surface, sunk so `press` stands out;
    //   carve: the same, sunk the other way (`press` deep);
    //   piece: centred `offset` metres out along the surface's normal; the nose (a round cone) from its
    //   snapped root to its snapped tip, `press` metres out.
    // Then the nostrils under the nose's tip.
    void PressParts()
    {
        if (headIndex < 0 || pressed.Count == 0) return;
        var face = kernel.face;
        var k = kernel;
        Vector3 c0 = skull.f.o;
        (Vector3 p, Vector3 n) Snap(Vector3 target)
        {
            Vector3 dir = (target - c0).normalized;
            Vector3 p = c0 + dir * 0.3f;
            for (int it = 0; it < 200; it++)
            {
                float d = k.HeadBase(p);
                if (Mathf.Abs(d) < 1e-4f) break;
                p -= dir * Mathf.Clamp(d * 0.9f, -0.02f, 0.02f);
            }
            const float h = 0.001f;
            Vector3 g = new Vector3(k.HeadBase(p + Vector3.right * h) - k.HeadBase(p - Vector3.right * h), k.HeadBase(p + Vector3.up * h) - k.HeadBase(p - Vector3.up * h),
                                    k.HeadBase(p + Vector3.forward * h) - k.HeadBase(p - Vector3.forward * h));
            return (p, g.sqrMagnitude > 1e-12f ? g.normalized : dir);
        }
        // The lips sit on the bone in front - the base plus the pressed bone placed so far (the teeth): snapped
        // onto that, not the bare egg.
        var placed = new List<int>();
        // (Each lip on its own arc: the upper lip on the upper jaw's, the lower on the lower's - the upper arc reaches
        // down past the lower lip.)
        string skipArc = null;
        float Bone(Vector3 p) { float d = k.HeadBase(p); foreach (int j in placed) if (FacePartNames[j] != skipArc) d = Mathf.Min(d, BodySdfKernel.FaceDist(face[j], p)); return d; }
        (Vector3 p, Vector3 n) SnapBone(Vector3 target)
        {
            Vector3 dir = (target - c0).normalized;
            Vector3 p = c0 + dir * 0.3f;
            for (int it = 0; it < 200; it++)
            {
                float d = Bone(p);
                if (Mathf.Abs(d) < 1e-4f) break;
                p -= dir * Mathf.Clamp(d * 0.9f, -0.02f, 0.02f);
            }
            const float h = 0.001f;
            Vector3 g = new Vector3(Bone(p + Vector3.right * h) - Bone(p - Vector3.right * h), Bone(p + Vector3.up * h) - Bone(p - Vector3.up * h),
                                    Bone(p + Vector3.forward * h) - Bone(p - Vector3.forward * h));
            return (p, g.sqrMagnitude > 1e-12f ? g.normalized : dir);
        }
        Vector3 noseTip = Vector3.zero, noseUp = Vector3.up, noseN = Vector3.forward; float noseR = 0f; bool nose = false;
        foreach (var e in pressed)
        {
            var fp = face[e.i];
            bool lip = FacePartNames[e.i].StartsWith("Lip");
            skipArc = FacePartNames[e.i].StartsWith("LipLower") ? "UpperJawArc" : FacePartNames[e.i].StartsWith("LipUpper") ? "LowerJawArc" : null;
            if (fp.op == (int)FacePartOp.Piece)
            {
                if (fp.shape == (int)FacePartShape.RoundCone)
                {
                    var (pr, nr) = Snap(e.target); var (pt, nt) = Snap(e.axis);
                    Vector3 a = pr + nr * e.rootOffset, b = pt + nt * e.press;
                    fp.o = (a + b) * 0.5f; fp.y = (b - a).normalized; fp.halfLen = (b - a).magnitude * 0.5f;
                    fp.x = Vector3.Cross(fp.y, nt).normalized; fp.z = Vector3.Cross(fp.x, fp.y).normalized;
                    fp.r = new Vector3(e.size.x, e.size.y, e.size.y);
                    noseTip = b; noseUp = fp.y; noseN = nt; noseR = e.size.y; nose = true;
                }
                else
                {
                    var (ps, ns) = lip ? SnapBone(e.target) : Snap(e.target);
                    fp.o = ps + ns * e.offset;
                }
            }
            else
            {
                // (Muscles ride on the bone - the base plus the pressed bone: the jaw arcs, the chin - not the bare egg.)
                var (ps, ns) = fp.op == (int)FacePartOp.Muscle ? SnapBone(e.target) : Snap(e.target);
                // (A set push direction: it stands out that way instead of along the base's normal.)
                if (pushDirs.TryGetValue(e.i, out var pd)) ns = pd;
                Vector3 x = Vector3.ProjectOnPlane(e.axis, ns);
                if (x.sqrMagnitude < 1e-8f) x = Vector3.ProjectOnPlane(skull.f.x, ns);
                x.Normalize();
                fp.z = ns; fp.x = x; fp.y = Vector3.Cross(ns, x).normalized;
                fp.r = e.size;
                float c = e.size.z, hgt = Mathf.Min(e.press, c * 0.95f);
                fp.o = fp.op == (int)FacePartOp.Carve ? ps + ns * (c - hgt) : ps - ns * (c - hgt);
                fp.press = hgt;
            }
            face[e.i] = fp; faceParts[e.i] = fp;
            if (fp.op == (int)FacePartOp.Press) placed.Add(e.i);
        }
        // The nostrils: under the tip, either side of the middle, sunk a little.
        if (nose)
            for (int j = 0; j < FacePartNames.Count; j++)
            {
                if (!FacePartNames[j].StartsWith("Nostril.")) continue;
                float s = FacePartNames[j].EndsWith(".L") ? -1f : 1f;
                var fp = face[j];
                Vector3 side = Vector3.Cross(noseUp, noseN).normalized;
                if (Vector3.Dot(side, skull.f.x) < 0f) side = -side;
                // (Set back under the tip and close to the middle: open downward, seen mainly from below.)
                fp.o = noseTip - noseN * (noseR * 0.95f) + side * (s * noseR * 0.42f) - noseUp * (noseR * 0.8f);
                face[j] = fp; faceParts[j] = fp;
            }
    }

    // Face Fix Part 4: grooves snapped onto the skin. Each snapped part is placed where the skin is along
    // the head's forward axis at its x / y (found with every snapped part switched off), then moved
    // its offset (metres) along that axis.
    void SnapFaceParts()
    {
        if (headIndex < 0 || snapOffsets.Count == 0) return;
        var f = frames[headIndex];
        var face = kernel.face;
        var ops = new int[face.Length];
        for (int j = 0; j < face.Length; j++) { ops[j] = face[j].op; if (snapOffsets.ContainsKey(j)) { var fp = face[j]; fp.op = (int)FacePartOp.Marker; face[j] = fp; } }
        float far = Canon.halfD * 2f;
        foreach (var kv in snapOffsets)
        {
            var fp = face[kv.Key];
            Vector3 o = fp.o - f.z * Vector3.Dot(fp.o - Canon.o, f.z);   // on the eye line's plane, at its x / y
            Vector3 p = o + f.z * far;
            float prev = kernel.Eval(p).skin;
            for (int it = 0; it < 400 && prev > 0f; it++)
            {
                float step = Mathf.Clamp(prev * 0.9f, 0.0002f, 0.02f);
                p -= f.z * step;
                prev = kernel.Eval(p).skin;
            }
            fp.o = p + f.z * kv.Value;
            faceParts[kv.Key] = fp;
        }
        for (int j = 0; j < face.Length; j++) { var fp = faceParts[j]; fp.op = ops[j]; faceParts[j] = fp; face[j] = fp; }
        for (int j = 0; j < FacePartNames.Count; j++) if (FacePartNames[j] == "MouthLine") MouthCentre = faceParts[j].o;
    }

    public void Dispose() => kernel.Dispose();

    // The unmanaged copy of everything Eval needs.
    // The hand a digit segment belongs to.
    int HandOf(int j)
    {
        while (j >= 0 && plan.bones[j].kind == BoneKind.Finger) j = plan.bones[j].parent;
        return j;
    }

    // A sphere round hand bone i (and its digits), with room for the stretch of forearm it overlaps.
    (Vector3 c, float r) HandSphere(int i)
    {
        var b = plan.bones[i];
        Vector3 c = start[i] + frames[i].y * (b.length * 0.45f);
        float r = b.length * 0.55f;
        for (int j = kernelBones; j < plan.bones.Count; j++)
            if (HandOf(j) == i) r = Mathf.Max(r, (end[j] - c).magnitude + plan.bones[j].girth.x);
        r = Mathf.Max(r, (start[i] - frames[i].y * BodyRules.Default.hands.cutOverlap - c).magnitude + b.Outer.x);
        return (c, r + 0.004f);
    }

    public BodySdfKernel BuildKernel(Allocator alloc)
    {
        int n = kernelBones;
        var hr = BodyRules.Default.hands;
        var k = new BodySdfKernel
        {
            bones = new NativeArray<BodySdfKernel.Bone>(n, alloc),
            blobs = new NativeArray<BodySdfKernel.Ellipsoid>(blobs.Count, alloc),
            face = new NativeArray<BodySdfKernel.FacePrim>(faceParts.ToArray(), alloc),
            socketClearance = BodyRules.Default.face.socketClearance, faceReach = FaceReach(), faceFatBlendMax = BodyRules.Default.face.faceFatBlendMax,
            breasts = new NativeArray<BodySdfKernel.Ellipsoid>(breasts.Count, alloc),
            skull = E(skull.f, skull.r, headIndex, 0), eggTop = EggTop, eggBottom = EggBottom,
            faceO = Canon.o, faceR = new Vector3(Canon.halfW, Canon.halfH, Canon.halfD),
            skullBase = E(skullBase.f, skullBase.r, headIndex, 0), eggShear = BodyRules.Default.face.eggShear,
            headMuscle = 1f + 0.3f * plan.sheet.S, headBlend = BodyRules.Default.face.headBlend, neckBlend = BodyRules.Default.face.neckBlend,
            muscleScale = 1f, fatScale = 1f, brainScale = 1f,
            headIndex = headIndex, eyeL = EyeL, eyeR = EyeR, eyeRadius = EyeRadius, lidL = LidL, lidR = LidR, earL = EarLeft, earR = EarRight,
            muscleBlend = muscleBlend, fatBlendPerMetre = fatBlendPerMetre, fatBlendMin = fatBlendMin,
            breastBlend = breastBlend, featureBlend = featureBlend, eyeCarveBlend = eyeCarveBlend, fatBlendFalloff = fatBlendFalloff,
            fatBlendMax = fatBlendMax, muscleReach = muscleReach,
            fingerBlend = hr.fingerBlend, cutBody = -hr.cutOverlap * 0.2f, cutHand = -hr.cutOverlap, cutShrink = hr.cutShrink,
        };
        int digitCount = plan.bones.Count - kernelBones;
        k.digits = new NativeArray<BodySdfKernel.Digit>(digitCount, alloc);
        var dg = k.digits;
        for (int j = 0; j < digitCount; j++)
        {
            int bi = kernelBones + j;
            var b = plan.bones[bi];
            float r = b.girth.x;
            Vector3 dir = (end[bi] - start[bi]).normalized;
            Vector3 off = bind[bi].MultiplyVector(b.shapeOffset);
            // Segments overlap at their joints (round ends of the same radius: smooth when straight, a
            // rounded knuckle when bent); the tip stops short by its radius so the digit keeps its length.
            int child = -1;
            for (int c = kernelBones; c < plan.bones.Count; c++) if (plan.bones[c].parent == bi) child = c;
            bool tip = child < 0;
            dg[j] = new BodySdfKernel.Digit { a = start[bi] + off, b = end[bi] + off - (tip ? dir * r : Vector3.zero), r = r, hand = HandOf(bi), bone = bi,
                                              parentBone = b.parent, childBone = child, length = b.length };
        }
        var cutList = new List<BodySdfKernel.HandCut>();
        for (int i = 0; i < n; i++)
            if (plan.bones[i].kind == BoneKind.Hand)
            {
                var (c, r) = HandSphere(i);
                cutList.Add(new BodySdfKernel.HandCut { o = start[i], axis = frames[i].y, centre = c, radius = r, hand = i });
            }
        // The head's cut goes last (cut mode 2 + HandCount): one plane for both pieces, no overlap - BodyBuild
        // welds the two neck rings into one surface (Face Fix Part 7).
        if (HasHeadPiece) cutList.Add(new BodySdfKernel.HandCut { o = headCutO, axis = headCutAxis, centre = headCutC, radius = headCutR, hand = headIndex,
                                                                  cutBody = 0f, cutHand = 0f, shrink = 0f, own = 1 });
        k.cuts = new NativeArray<BodySdfKernel.HandCut>(cutList.ToArray(), alloc);
        if (plan.dna != null)
        {
            k.hasDna = 1;
            k.fatWaist = plan.dna.Fat(FatRegion.Waist); k.fatBelly = plan.dna.Fat(FatRegion.Belly);
            k.fatHips = plan.dna.Fat(FatRegion.Hips); k.fatButtocks = plan.dna.Fat(FatRegion.Buttocks);
        }
        var bs = k.bones;
        for (int i = 0; i < n; i++)
        {
            var b = plan.bones[i];
            bs[i] = new BodySdfKernel.Bone
            {
                o = frames[i].o, x = frames[i].x, y = frames[i].y, z = frames[i].z, end = end[i],
                girth = b.girth, muscle = b.muscle, fat = b.fat, outer = b.Outer,
                length = b.length, depthRatio = depthRatio[i], jointK = jointK[i],
                kind = (int)b.kind, parent = b.parent, side = b.side,
            };
            if (b.kind == BoneKind.Hand)
            {
                float R = b.Outer.y, Lp = b.length * hr.palmLength;
                var hb = bs[i];
                hb.palm = new Vector3(R * hr.palmThickness * 0.5f, Lp * 0.5f, R * hr.palmWidth * 0.5f);
                hb.palmRound = hb.palm.x * 2f * hr.palmCorner;
                bs[i] = hb;
            }
        }
        // Each hand's segments (added hand by hand, so contiguous).
        for (int j = 0; j < digitCount; j++)
        {
            var hd = bs[dg[j].hand];
            if (hd.digitCount == 0) hd.digitStart = j;
            hd.digitCount++;
            bs[dg[j].hand] = hd;
        }
        var bl = k.blobs;
        for (int j = 0; j < blobs.Count; j++) bl[j] = E(blobs[j].f, blobs[j].r, blobs[j].bone, (int)blobs[j].group);
        var br = k.breasts;
        for (int j = 0; j < breasts.Count; j++) br[j] = E(breasts[j].f, breasts[j].r, plan.Index("Chest"), 0);
        return k;
    }

    // How far from the skull's centre a face part can still change the surface: its farthest extent, its
    // blend, the face fat blend and the thickest fat on the head (Face Fix Part 0).
    float FaceReach()
    {
        float reach = 0f;
        foreach (var fp in faceParts)
        {
            float ext = Mathf.Max(fp.r.x, Mathf.Max(fp.r.y, fp.r.z)) + fp.halfLen;
            reach = Mathf.Max(reach, (fp.o - skull.f.o).magnitude + ext + fp.blend);
        }
        float fat = headIndex >= 0 ? Mathf.Max(plan.bones[headIndex].fat.x, Mathf.Max(plan.bones[headIndex].fat.y, plan.bones[headIndex].fat.z)) * 1.7f : 0f;
        reach = Mathf.Max(reach, Mathf.Max(skull.r.x, Mathf.Max(skull.r.y, skull.r.z)) * 1.25f + (skull.f.o - Canon.o).magnitude);   // the whole egg
        return reach + BodyRules.Default.face.faceFatBlendMax + fat + 0.01f;
    }

    // The highest point the cranium reaches (root space y) and its centre: for sanity checks.
    public Vector3 CraniumCentre => cranium.f.o;
    public Vector3 CraniumRadii => cranium.r;
    // The head's egg (Anatomy Rework Part 1: the bone layer's base, with INT's growth).
    public Vector3 SkullCentre => skull.f.o;
    public Vector3 SkullRadii => skull.r;

    static BodySdfKernel.Ellipsoid E(Frame f, Vector3 r, int bone, int group) =>
        new BodySdfKernel.Ellipsoid { o = f.o, x = f.x, y = f.y, z = f.z, r = r, bone = bone, group = group };

    static bool IsTorso(BoneKind k) => k == BoneKind.Pelvis || k == BoneKind.Lumbar || k == BoneKind.Chest;

    // ---------- construction ----------

    static float Profile(Vector3 v, float t) => BodySdfKernel.Profile(v, t);
    static float Ellipsoid(Vector3 p, Vector3 r) => BodySdfKernel.EllipsoidDist(p, r);

    // A blob on bone i at t along it, `angle` degrees round it (0 = front, +90 = the body's right),
    // sitting on the core surface, `along` x bone length long, `width` x core radius wide.
    void AddBlob(int i, MuscleGroup g, float t, float angle, float along, float width, float thick = 1f)
    {
        var b = plan.bones[i];
        float coreR = Profile(b.girth, t), m = Profile(b.muscle, t);
        float avg = 0f; int cnt = 0;
        foreach (var gg in GroupsOf(b.kind)) { avg += plan.dna.Muscle(gg); cnt++; }
        avg = cnt > 0 ? avg / cnt : 1f;
        float rel = avg > 1e-4f ? plan.dna.Muscle(g) / avg : 1f;
        float th = m * thick * Mathf.Pow(Mathf.Max(rel, 0f), 0.7f);
        if (th < 0.001f) return;
        // A blob is a flat-ish ellipsoid on a curved body: its edges rise off the surface by about
        // (width x r)^2 / 2r. Keep that under ~60% of its thickness, or the edges stand off as rims.
        width = Mathf.Min(width, Mathf.Sqrt(SR.blobFit * th / Mathf.Max(coreR, 1e-3f)));
        var f = frames[i];
        float a = angle * Mathf.Deg2Rad, dr = depthRatio[i];
        // On the (flattened, elliptical) core cross-section: the point at this angle and the surface
        // normal there, so the blob lies flat on the body instead of tilting off a round one.
        Vector3 onSurface = (f.x * Mathf.Sin(a) + f.z * Mathf.Cos(a) * dr) * coreR;
        Vector3 radial = (f.x * Mathf.Sin(a) * dr + f.z * Mathf.Cos(a)).normalized;
        Vector3 c = f.o + f.y * (b.length * t) + onSurface + radial * th * SR.blobLift;
        Vector3 up = f.y;
        Vector3 side = Vector3.Cross(up, radial).normalized;
        blobs.Add(new Blob
        {
            bone = i, group = g,
            f = new Frame { o = c, x = side, y = up, z = radial },
            r = new Vector3(Mathf.Max(0.005f, coreR * width), Mathf.Max(0.005f, b.length * along * 0.5f), th),
        });
    }

    static MuscleGroup[] GroupsOf(BoneKind k)
    {
        switch (k)
        {
            case BoneKind.Chest: return new[] { MuscleGroup.Pectorals, MuscleGroup.Lats, MuscleGroup.Trapezius };
            case BoneKind.Lumbar: return new[] { MuscleGroup.Abdominals };
            case BoneKind.Pelvis: return new[] { MuscleGroup.Glutes };
            case BoneKind.Neck: return new[] { MuscleGroup.Trapezius };
            case BoneKind.UpperArm: return new[] { MuscleGroup.Deltoids, MuscleGroup.Biceps, MuscleGroup.Triceps };
            case BoneKind.Forearm: return new[] { MuscleGroup.Forearm };
            case BoneKind.Thigh: return new[] { MuscleGroup.Quadriceps, MuscleGroup.Hamstrings };
            case BoneKind.Shin: return new[] { MuscleGroup.Calves };
            default: return new MuscleGroup[0];
        }
    }

    // The muscle blobs of BodyRules.muscleBlobs, per bone; then men's pecs closer together and the
    // buttocks' projection by size.
    void BuildMuscles()
    {
        var rules = BodyRules.Default;
        for (int i = 0; i < plan.bones.Count; i++)
        {
            var b = plan.bones[i];
            // Outward for a limb = away from the body's midline.
            float o = b.side >= 0 ? 1f : -1f;
            foreach (var m in rules.muscleBlobs)
            {
                if (m.bone != b.kind) continue;
                if (!m.mirror) { AddBlob(i, m.group, m.t, m.outward ? o * m.angle : m.angle, m.along, m.width, m.thick); continue; }
                foreach (float s in new[] { -1f, 1f })
                {
                    int before = blobs.Count;
                    AddBlob(i, m.group, m.t, s * m.angle, m.along, m.width, m.thick);
                    if (blobs.Count == before) continue;
                    var bl = blobs[blobs.Count - 1];
                    // Men's pecs sit closer together (centres malePecInset nearer the midline each).
                    if (m.group == MuscleGroup.Pectorals && plan.sheet.sex == Sex.Male)
                        bl.f.o -= frames[i].x * s * rules.malePecInset;
                    // Like breasts: how far a buttock stands out grows with its size (glute thickness +
                    // buttock fat): lean ones sit close to the body, heavy ones project.
                    if (m.group == MuscleGroup.Glutes)
                    {
                        float size = bl.r.z + (plan.dna != null ? plan.dna.Fat(FatRegion.Buttocks) : 0f);
                        float k = Mathf.Lerp(rules.buttProjection.x, rules.buttProjection.y,
                                             Mathf.InverseLerp(rules.buttProjectionSizes.x, rules.buttProjectionSizes.y, size));
                        bl.f.o += bl.f.z * (k - 1f - SR.blobLift) * bl.r.z; // AddBlob leaves it standing out (1 + blobLift) x its depth
                    }
                    blobs[blobs.Count - 1] = bl;
                }
            }
        }
    }

    void BuildHead()
    {
        if (headIndex < 0) return;
        var b = plan.bones[headIndex];
        var f = frames[headIndex];
        var dna = plan.dna;
        // The canon (Face Fix Part 1): the face hangs from its lines; the eye line's centre is face space's origin.
        Canon = HeadCanon.From(plan, headIndex, f.o, f.x, f.y, f.z);
        float rx = Canon.halfW, ry = b.length * 0.5f, rz = Canon.halfD, hy = Canon.halfH;
        // INT elongation: the cranium grows up and toward the back (the face stays on the canon).
        float el = dna != null ? dna.headElongation : 0f;
        // The profile egg (the face's frame: fat mask, wig, bake): bottom on the chin line.
        Vector3 c = Canon.o + f.y * (ry * (1f + el * 0.3f) - hy) - f.z * rz * el * 0.5f;
        skull = new Feature { f = new Frame { o = c, x = f.x, y = f.y, z = f.z }, r = new Vector3(rx, ry * (1f + el * 0.3f), rz * (1f + el * 0.6f)) };
        skullBase = new Feature { f = new Frame { o = Canon.o, x = f.x, y = f.y, z = f.z }, r = new Vector3(rx, hy, rz) };
        // Egg: seed variation on both ends.
        var eggRng = new PcgRandom(plan.sheet.seed, "egg");
        EggTop = SR.eggTop + SR.eggVariation * eggRng.Signed();
        EggBottom = SR.eggBottom + SR.eggVariation * eggRng.Signed();
        float skullRy = skull.r.y;
        Vector3 O = Canon.o;
        // Face space -> root space: fractions of the half width / height / depth from the eye line's centre.
        Vector3 FacePoint(Vector3 at) => O + f.x * at.x * rx + f.y * at.y * hy + f.z * at.z * rz;
        float Anchor(FaceLine l) => Canon.Line(l) / hy;
        var fd = BodyRules.Default.face;
        bool fem = plan.sheet.sex == Sex.Female;
        // Face Fix Part 2: the cranium (braincase), an egg big end up and back; INT grows it up and back.
        {
            Vector3 cc = fd.craniumCentre, cr = fd.craniumRadii;
            Vector3 o = FacePoint(cc) + f.y * cr.y * hy * el * 0.8f - f.z * cr.z * rz * el * 0.4f;
            cranium = new Feature { f = new Frame { o = o, x = f.x, y = f.y, z = f.z }, r = new Vector3(cr.x * rx, cr.y * hy * (1f + el * 0.8f), cr.z * rz * (1f + el * 0.6f)) };
            craniumBottom = fd.craniumBottom; craniumTop = fd.craniumTop;
        }
        // The lower jaw: the menton on the chin line, the jaw angles (sex, STR, DNA, seed), the chin's width.
        {
            var jr = new PcgRandom(plan.sheet.seed, "jaw");
            float js = dna != null ? dna.jaw : 1f;
            Vector3 g = fem ? fd.femaleGonion : fd.gonion;
            float gx = (g.x + fd.jawStr * plan.sheet.S + fd.jawVariation * jr.Signed()) * js * rx;
            float cx = (fem ? fd.femaleChinHalfWidth : fd.chinHalfWidth) * js * rx * (1f + 0.15f * jr.Signed());
            Vector3 menton = O + f.y * (Canon.chin + fd.menton.y * hy) + f.z * (fd.menton.z * rz);
            Vector3 gon = O + f.y * (g.y + 0.03f * jr.Signed()) * hy + f.z * g.z * rz;
            Vector3 v = menton - gon;
            float a = Vector3.Dot(v, f.y), bz = Vector3.Dot(v, f.z);
            Vector3 n = (f.y * bz - f.z * a).normalized;
            float gz = Vector3.Dot(gon - menton, f.z);
            float round = fd.jawRound * hy;
            var poly = Inset(new[] { new Vector2(-cx, 0f), new Vector2(cx, 0f), new Vector2(gx, gz), new Vector2(-gx, gz) }, round);
            mandible = new BodySdfKernel.Mandible
            {
                menton = menton, x = f.x, y = f.y, z = f.z, jawN = n, q0 = poly[0], q1 = poly[1], q2 = poly[2], q3 = poly[3],
                round = round, angle = (fem ? fd.femaleJawAngle : fd.jawAngle) * hy, top = fd.jawTop * hy, blend = fd.jawBlend * hy, on = 1,
            };
        }
        // Face parts (Head spec Part D): data in face space -> primitives in root space. Per part: the
        // sex's factors, the seed's variation (mirrored pairs share it, plus a little asymmetry each),
        // the DNA face scale, then the character's own overrides.
        var faceDef = BodyRules.Default.face;
        bool female = plan.sheet.sex == Sex.Female;
        var overrides = plan.sheet.face;
        Vector3 W(Vector3 v) => f.x * v.x + f.y * v.y + f.z * v.z;
        float DnaScale(FacePartScale k) => dna == null ? 1f : k == FacePartScale.Brow ? dna.brow : k == FacePartScale.Cheekbones ? dna.cheekbones
                                         : k == FacePartScale.Jaw ? dna.jaw : k == FacePartScale.Nose ? dna.nose : k == FacePartScale.Chin ? dna.chin : 1f;
        Vector3 V3(PcgRandom r, Vector3 range) => new Vector3(range.x * r.Signed(), range.y * r.Signed(), range.z * r.Signed());
        foreach (var part in faceDef.parts)
        {
            if (!SkinFeatures && part.snap && (part.op == FacePartOp.Subtract || part.op == FacePartOp.Union)) continue;
            var rng = new PcgRandom(plan.sheet.seed, "face." + part.name);
            Vector3 dPos = V3(rng, part.positionVar), dRot = V3(rng, part.rotationVar), dSize = V3(rng, part.sizeVar);
            for (int side = part.mirrored ? -1 : 1; side <= 1; side += 2)
            {
                string inst = part.mirrored ? part.name + (side < 0 ? ".L" : ".R") : part.name;
                var asym = new PcgRandom(plan.sheet.seed, "face." + inst);
                Vector3 pos = part.position + dPos + V3(asym, part.positionVar) * faceDef.asymmetry + (female ? part.femaleOffset : Vector3.zero);
                pos.y += Anchor(part.anchor);
                Vector3 rot = part.rotationEuler + dRot + V3(asym, part.rotationVar) * faceDef.asymmetry;
                // (STR drives the lower face's volumes: size and press.)
                float strK = part.strength ? 1f + faceDef.strFace * plan.sheet.S : 1f;
                Vector3 size = Vector3.Scale(part.size, Vector3.one + dSize) * DnaScale(part.scaleBy) * strK;
                if (female) size = Vector3.Scale(size, part.femaleSize);
                if (overrides != null)
                    foreach (var ov in overrides)
                    {
                        if (ov.part != part.name && ov.part != inst) continue;
                        pos += ov.move; rot += ov.rotate;
                        size = Vector3.Scale(size, new Vector3(ov.scale.x == 0f ? 1f : ov.scale.x, ov.scale.y == 0f ? 1f : ov.scale.y, ov.scale.z == 0f ? 1f : ov.scale.z));
                    }
                if (side < 0) { pos.x = -pos.x; rot.y = -rot.y; rot.z = -rot.z; }
                var q = Quaternion.Euler(rot);
                if (part.snap) { snapOffsets[faceParts.Count] = pos.z; pos.z = 1f; }
                Vector3 r = part.shape == FacePartShape.Capsule || part.shape == FacePartShape.Sphere
                    ? new Vector3(size.x * rx, size.x * rx, size.x * rx) : new Vector3(size.x * rx, size.y * hy, size.z * rz);
                var prim = new BodySdfKernel.FacePrim
                {
                    o = FacePoint(pos), x = W(q * Vector3.right), y = W(q * Vector3.up), z = W(q * Vector3.forward), r = r,
                    halfLen = part.shape == FacePartShape.Capsule ? size.y * hy : 0f, blend = part.blend, shape = (int)part.shape, op = (int)part.op,
                };
                // Anatomy Rework Part 1: pressed shapes, planes and pieces are placed on the base once it exists.
                var op = part.op;
                float strF = strK;
                if (op == FacePartOp.Plane)
                {
                    Vector3 nrm = W(new Vector3(side < 0 ? -part.axis.x : part.axis.x, part.axis.y, part.axis.z)).normalized;
                    Vector3 t1 = Vector3.Cross(nrm, f.y).sqrMagnitude > 1e-6f ? Vector3.Cross(nrm, f.y).normalized : f.x;
                    prim.y = nrm; prim.x = t1; prim.z = Vector3.Cross(t1, nrm).normalized;
                    prim.r = size * rx;
                }
                else if (op == FacePartOp.Press || op == FacePartOp.Carve || op == FacePartOp.Muscle || op == FacePartOp.Piece)
                {
                    Vector3 ax = new Vector3(side < 0 ? -part.axis.x : part.axis.x, part.axis.y, part.axis.z);
                    if (part.shape == FacePartShape.RoundCone) ax.y += Anchor(part.anchor);
                    // (Snapped along the line from the egg's centre through the target: presses aim with their own
                    // depth, pieces - the nose, lips, brow bar - from the front.)
                    float aimZ = op == FacePartOp.Piece ? 0.9f : pos.z;
                    pressed.Add((faceParts.Count, FacePoint(new Vector3(pos.x, pos.y, aimZ)), op == FacePartOp.Piece && part.shape == FacePartShape.RoundCone ? FacePoint(new Vector3(ax.x, ax.y, 0.9f)) : W(ax),
                                 size * rx, part.press * (op == FacePartOp.Piece ? 1f : rx) * strF, pos.z, part.shape == FacePartShape.RoundCone ? size.z : 0f));
                }
                if (part.name == "Nose" || part.name == "Ala" || part.name == "NoseUnder") prim.noFat = 1;
                if (part.snap) prim.snapped = 1;
                if (part.pushDir != Vector3.zero) pushDirs[faceParts.Count] = W(part.pushDir).normalized;
                faceParts.Add(prim);
                FacePartNames.Add(inst);
            }
        }
        // The eyes on the canon: on the eye line, one eye-width either side of the midline, canon size.
        EyeRadius = Canon.eyeballRadius;
        float ex = Canon.eyeSpacing / rx;
        EyeL = FacePoint(new Vector3(-ex, 0f, faceDef.eyeDepth));
        EyeR = FacePoint(new Vector3(ex, 0f, faceDef.eyeDepth));
        // Lids (Face Fix Part 3): the crease is shallower the more fat the face carries.
        Eye = EyeShape.For(plan.sheet, EyeRadius);
        float faceFat = Mathf.Max(b.fat.x, Mathf.Max(b.fat.y, b.fat.z));
        Eye.creaseDepth *= 1f - Mathf.SmoothStep(0f, 1f, faceFat / 0.02f);
        LidL = new BodySdfKernel.EyeLid { c = EyeL, x = -f.x, y = f.y, z = f.z, r = EyeRadius, s = Eye };
        LidR = new BodySdfKernel.EyeLid { c = EyeR, x = f.x, y = f.y, z = f.z, r = EyeRadius, s = Eye };
        // The jaw hinges in front of the ears, a little above the nose base; the mouth line from its part.
        JawPivot = FacePoint(new Vector3(0f, Anchor(FaceLine.NoseBase) + 0.12f, -0.15f));
        MouthCentre = FacePoint(new Vector3(0f, Anchor(FaceLine.Mouth), 1.0f));
        for (int j = 0; j < FacePartNames.Count; j++) if (FacePartNames[j] == "MouthLine") MouthCentre = faceParts[j].o;
    }

    // A convex polygon's edges moved inward by r (vertices: the moved neighbouring edges' intersections).
    static Vector2[] Inset(Vector2[] v, float r)
    {
        int n = v.Length;
        Vector2 centre = Vector2.zero; foreach (var p in v) centre += p; centre /= n;
        var pts = new Vector2[n]; var nrm = new Vector2[n];
        for (int i = 0; i < n; i++)
        {
            Vector2 e = (v[(i + 1) % n] - v[i]).normalized, nn = new Vector2(-e.y, e.x);
            if (Vector2.Dot(centre - v[i], nn) < 0f) nn = -nn;
            nrm[i] = nn; pts[i] = v[i] + nn * r;
        }
        var res = new Vector2[n];
        for (int i = 0; i < n; i++)
        {
            int a = (i + n - 1) % n, b = i;   // vertex i joins edge a (v[a] -> v[i]) and edge b (v[i] -> v[i+1])
            Vector2 da = new Vector2(nrm[a].y, -nrm[a].x), db = new Vector2(nrm[b].y, -nrm[b].x);
            float den = da.x * db.y - da.y * db.x;
            if (Mathf.Abs(den) < 1e-9f) { res[i] = v[i] + nrm[b] * r; continue; }
            Vector2 w = pts[b] - pts[a];
            float t = (w.x * db.y - w.y * db.x) / den;
            res[i] = pts[a] + da * t;
        }
        return res;
    }

    void BuildBreasts()
    {
        if (plan.dna == null || plan.dna.breastSize <= 0f) return;
        int chest = plan.Index("Chest");
        float s = plan.dna.breastSize;
        foreach (var sock in plan.sockets)
        {
            if (!sock.name.StartsWith("breast.")) continue;
            var f = frames[chest];
            // Socket in the chest's rest space; the chest isn't posed, so its frame is axis-aligned.
            // How far the breast stands out from the chest grows with its size: small ones sit close
            // to the body, large (heavier) ones project further.
            var rules = BodyRules.Default;
            var cb = plan.bones[chest];
            float tAlong = Mathf.Clamp01(sock.localPos.y / Mathf.Max(1e-4f, cb.length));
            float front = Profile(cb.Outer, tAlong) * depthRatio[chest];
            float k = Mathf.Lerp(rules.breastProjection.x, rules.breastProjection.y,
                                 Mathf.InverseLerp(rules.breastProjectionSizes.x, rules.breastProjectionSizes.y, s));
            Vector3 p = start[chest] + new Vector3(sock.localPos.x, sock.localPos.y, 0f) + f.z * (front + (k - 0.6f) * s);
            breasts.Add(new Feature { f = new Frame { o = p, x = f.x, y = f.y, z = f.z }, r = new Vector3(s, s * 0.85f, s * 0.6f) });
        }
    }

    // ---------- evaluation (the kernel) ----------

    // Signed distance to the skin.
    public float Eval(Vector3 p) => kernel.Eval(p).skin;

    // Also the nearest bone and the local fat thickness (for regions and layer data).
    public float Eval(Vector3 p, out int nearest, out float fatThickness)
    {
        var smp = kernel.Eval(p);
        nearest = smp.nearest; fatThickness = smp.fat;
        return smp.skin;
    }

    // Muscle-only surface (no fat), for debugging the layers.
    public float EvalMuscle(Vector3 p) => kernel.Eval(p).muscle;

    public int BlobCount => blobs.Count;

    // How far the buttocks stand out behind the pelvis core (skin, at the glute centre height).
    public string ButtReport()
    {
        var g = blobs.FindAll(b => b.group == MuscleGroup.Glutes);
        if (g.Count == 0) return "no glutes";
        int pel = plan.Index("Hips");
        var pb = plan.bones[pel];
        Vector3 c = g[0].f.o;
        float coreBack = start[pel].z - pb.girth.y * depthRatio[pel];
        float z = -0.5f;
        while (z < 0.3f && Eval(new Vector3(c.x, c.y, z)) > 0f) z += 0.002f;
        return $"buttock size {(g[0].r.z + plan.dna.Fat(FatRegion.Buttocks)) * 100f:0.0} cm, skin {(coreBack - z) * 100f:0.0} cm behind the pelvis core";
    }

    // Breast size, how far its tip stands out from the chest's front surface, and the pec centre spacing.
    public string BreastReport()
    {
        int chest = plan.Index("Chest");
        var sb = new System.Text.StringBuilder();
        foreach (var br in breasts)
        {
            var cb = plan.bones[chest];
            float t = Mathf.Clamp01((br.f.o.y - start[chest].y) / cb.length);
            float front = start[chest].z + Profile(cb.Outer, t) * depthRatio[chest];
            sb.Append($"breast r {br.r.x * 100f:0.0} cm, tip {(br.f.o.z + br.r.z - front) * 100f:0.0} cm out; ");
            break;
        }
        var pecs = blobs.FindAll(b => b.group == MuscleGroup.Pectorals);
        if (pecs.Count == 2) sb.Append($"pec centres {Mathf.Abs(pecs[0].f.o.x - pecs[1].f.o.x) * 100f:0.0} cm apart");
        return sb.ToString();
    }

    // Debug: what is near a point (bones and blobs with their distances).
    public string DescribePoint(Vector3 p)
    {
        var sb = new System.Text.StringBuilder();
        float skin = Eval(p, out int nearest, out float fat);
        sb.Append($"skin {skin:0.000} fat {fat:0.000} nearest {plan.bones[nearest].name}; ");
        for (int i = 0; i < kernelBones; i++) { float db = kernel.BoneCore(i, p); if (db < 0.06f) sb.Append($"{plan.bones[i].name} {db:0.000}, "); }
        foreach (var bl in blobs) { float d = Ellipsoid(bl.f.ToLocal(p), bl.r); if (d < 0.03f) sb.Append($"{bl.group}@{plan.bones[bl.bone].name} {d:0.000}, "); }
        if (headIndex >= 0) for (int j = 0; j < faceParts.Count; j++) { float d = BodySdfKernel.FaceDist(faceParts[j], p); if (d < 0.03f) sb.Append($"{FacePartNames[j]} {d:0.000}, "); }
        return sb.ToString();
    }

    // Debug: blobs of one group (centre, radii, the muscle and skin distance at the centre and 1.2 x
    // its depth radius out).
    public string DescribeBlobs(MuscleGroup g)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var bl in blobs)
        {
            if (bl.group != g) continue;
            Vector3 tip = bl.f.o + bl.f.z * bl.r.z * 1.2f;
            sb.Append($"[c {bl.f.o.x:0.000},{bl.f.o.y:0.000},{bl.f.o.z:0.000} r {bl.r.x:0.000},{bl.r.y:0.000},{bl.r.z:0.000} muscle@c {EvalMuscle(bl.f.o):0.000} skin@tip {Eval(tip):0.000}] ");
        }
        return sb.ToString();
    }
}
