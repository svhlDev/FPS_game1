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
    public Vector3 JawPivot, MouthCentre;   // the jaw's hinge and the mouth line's centre (root space)
    readonly List<Feature> breasts = new List<Feature>();
    Feature skull;                                 // the face's frame / profile egg (fat mask, wig, bake)
    Feature cranium;                               // Face Fix Part 2: the braincase ...
    BodySdfKernel.Mandible mandible;               // ... and the lower jaw (the head's core)
    float craniumBottom, craniumTop;
    public float EggTop, EggBottom;
    public Vector3 EyeL, EyeR;
    public float EyeRadius;
    public HeadCanon Canon;                        // Face Fix Part 1: the proportions the face is built on
    public EyeShape Eye;                           // Face Fix Part 3: the eye opening and lids
    BodySdfKernel.EyeLid LidL, LidR;
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
            float big = Mathf.Max(skull.r.x, Mathf.Max(skull.r.y, skull.r.z)) * 1.4f;
            headCutR = Mathf.Max((headCutO - headCutC).magnitude + 0.045f, big);
            HeadBounds = new Bounds(headCutC, Vector3.one * headCutR * 2f);
        }
        kernel = BuildKernel(Allocator.Persistent);
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
            cranium = E(cranium.f, cranium.r, headIndex, 0), craniumBottom = craniumBottom, craniumTop = craniumTop, jaw = mandible,
            headIndex = headIndex, eyeL = EyeL, eyeR = EyeR, eyeRadius = EyeRadius, lidL = LidL, lidR = LidR,
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
        // The head's cut goes last (cut mode 2 + HandCount).
        // Its overlap is longer (4.5 cm) and its tuck deeper: the body's 1.5 cm grid edge is ragged.
        if (HasHeadPiece) cutList.Add(new BodySdfKernel.HandCut { o = headCutO, axis = headCutAxis, centre = headCutC, radius = headCutR, hand = headIndex,
                                                                  cutBody = 0.01f, cutHand = -0.035f, shrink = 0.012f });
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
        return reach + BodyRules.Default.face.faceFatBlendMax + fat + 0.01f;
    }

    // The highest point the cranium reaches (root space y) and its centre: for sanity checks.
    public Vector3 CraniumCentre => cranium.f.o;
    public Vector3 CraniumRadii => cranium.r;

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
            var rng = new PcgRandom(plan.sheet.seed, "face." + part.name);
            Vector3 dPos = V3(rng, part.positionVar), dRot = V3(rng, part.rotationVar), dSize = V3(rng, part.sizeVar);
            for (int side = part.mirrored ? -1 : 1; side <= 1; side += 2)
            {
                string inst = part.mirrored ? part.name + (side < 0 ? ".L" : ".R") : part.name;
                var asym = new PcgRandom(plan.sheet.seed, "face." + inst);
                Vector3 pos = part.position + dPos + V3(asym, part.positionVar) * faceDef.asymmetry + (female ? part.femaleOffset : Vector3.zero);
                pos.y += Anchor(part.anchor);
                Vector3 rot = part.rotationEuler + dRot + V3(asym, part.rotationVar) * faceDef.asymmetry;
                Vector3 size = Vector3.Scale(part.size, Vector3.one + dSize) * DnaScale(part.scaleBy);
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
                Vector3 r = part.shape == FacePartShape.Capsule || part.shape == FacePartShape.Sphere
                    ? new Vector3(size.x * rx, size.x * rx, size.x * rx) : new Vector3(size.x * rx, size.y * hy, size.z * rz);
                faceParts.Add(new BodySdfKernel.FacePrim
                {
                    o = FacePoint(pos), x = W(q * Vector3.right), y = W(q * Vector3.up), z = W(q * Vector3.forward), r = r,
                    halfLen = part.shape == FacePartShape.Capsule ? size.y * hy : 0f, blend = part.blend, shape = (int)part.shape, op = (int)part.op,
                });
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
