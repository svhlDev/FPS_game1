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
//   Head   : an ellipsoid scaled and elongated (up and back) by INT, brow ridge, cheekbones, jaw, nose
//            and chin blended in (seed sizes), eye sockets carved (smooth subtraction) at EyeL / EyeR.
//   Hands  : mittens (rounded palm box + thumb capsule). Feet: rounded wedges.
// Built in the bind pose (an A-pose: arms out, legs slightly apart, so limbs stay clear of the body).
// All positions are in the body root's space.
// Evaluation lives in BodySdfKernel (unmanaged, shared with the Burst mesher); this class builds it.
// Dispose when done (the kernel holds native arrays).
public class BodySDF : IDisposable
{
    // ---------- tunables (BodyRules could take these later) ----------
    public float jointBlend = 0.015f;      // core: bone with its parent (+ jointBlendGirth x the thinner core)
    public float jointBlendGirth = 0.6f;
    public float torsoBlend = 0.04f;       // pelvis / lumbar / chest seams (rounds the steps between them)
    public float muscleBlend = 0.022f;     // small: muscle shapes stay distinct
    public float fatBlendPerMetre = 1.6f;  // fat blend radius = this x local fat thickness (+ minimum)
    public float fatBlendMin = 0.018f;
    public float fatBlendFalloff = 0.06f;  // fat thickness blends across nearby bones (no ridges where they meet)
    public float breastBlend = 0.06f;
    public float featureBlend = 0.012f;
    public float eyeCarveBlend = 0.006f;
    public static float ArmSpread = 38f;   // A-pose: degrees out from the sides
    public static float LegSpread = 4f;

    struct Frame { public Vector3 o, x, y, z; public Vector3 ToLocal(Vector3 p) { p -= o; return new Vector3(Vector3.Dot(p, x), Vector3.Dot(p, y), Vector3.Dot(p, z)); } }
    struct Blob { public int bone; public Frame f; public Vector3 r; public MuscleGroup group; }
    struct Feature { public Frame f; public Vector3 r; }

    readonly BodyPlan plan;
    public readonly Vector3[] start, end;          // posed bone ends (root space)
    public readonly Matrix4x4[] bind;              // each joint's transform in this pose, relative to the root
    readonly Frame[] frames;                       // bone frames: y along the bone, z the body's front
    readonly float[] depthRatio;
    readonly List<Blob> blobs = new List<Blob>();
    readonly List<Feature> headFeatures = new List<Feature>();
    readonly List<Feature> breasts = new List<Feature>();
    Feature skull;
    public Vector3 EyeL, EyeR;
    public float EyeRadius;
    public Bounds bounds;
    readonly float[] jointK;
    readonly int headIndex;
    BodySdfKernel kernel;

    public BodyPlan Plan => plan;
    public BodySdfKernel Kernel => kernel;

    // A-pose: arms out ArmSpread degrees, legs LegSpread (local rotations per bone).
    public static Quaternion[] APose(BodyPlan plan)
    {
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
        pose ??= APose(plan);
        int n = plan.bones.Count;
        if (n > BodySdfKernel.MaxBones) throw new ArgumentException($"{n} bones (max {BodySdfKernel.MaxBones})");
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
            depthRatio[i] = b.kind == BoneKind.Pelvis ? 0.75f : b.kind == BoneKind.Lumbar ? 0.7f : b.kind == BoneKind.Chest ? 0.68f : 1f;
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
        bounds.Encapsulate(new Vector3(bounds.center.x, 0f, bounds.center.z)); // the soles
        kernel = BuildKernel(Allocator.Persistent);
    }

    public void Dispose() => kernel.Dispose();

    // The unmanaged copy of everything Eval needs.
    public BodySdfKernel BuildKernel(Allocator alloc)
    {
        int n = plan.bones.Count;
        var k = new BodySdfKernel
        {
            bones = new NativeArray<BodySdfKernel.Bone>(n, alloc),
            blobs = new NativeArray<BodySdfKernel.Ellipsoid>(blobs.Count, alloc),
            features = new NativeArray<BodySdfKernel.Ellipsoid>(headFeatures.Count, alloc),
            breasts = new NativeArray<BodySdfKernel.Ellipsoid>(breasts.Count, alloc),
            skull = E(skull.f, skull.r, headIndex, 0),
            headIndex = headIndex, eyeL = EyeL, eyeR = EyeR, eyeRadius = EyeRadius,
            muscleBlend = muscleBlend, fatBlendPerMetre = fatBlendPerMetre, fatBlendMin = fatBlendMin,
            breastBlend = breastBlend, featureBlend = featureBlend, eyeCarveBlend = eyeCarveBlend, fatBlendFalloff = fatBlendFalloff,
        };
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
        }
        var bl = k.blobs;
        for (int j = 0; j < blobs.Count; j++) bl[j] = E(blobs[j].f, blobs[j].r, blobs[j].bone, (int)blobs[j].group);
        var fe = k.features;
        for (int j = 0; j < headFeatures.Count; j++) fe[j] = E(headFeatures[j].f, headFeatures[j].r, headIndex, 0);
        var br = k.breasts;
        for (int j = 0; j < breasts.Count; j++) br[j] = E(breasts[j].f, breasts[j].r, plan.Index("Chest"), 0);
        return k;
    }

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
        width = Mathf.Min(width, Mathf.Sqrt(1.2f * th / Mathf.Max(coreR, 1e-3f)));
        var f = frames[i];
        float a = angle * Mathf.Deg2Rad, dr = depthRatio[i];
        // On the (flattened, elliptical) core cross-section: the point at this angle and the surface
        // normal there, so the blob lies flat on the body instead of tilting off a round one.
        Vector3 onSurface = (f.x * Mathf.Sin(a) + f.z * Mathf.Cos(a) * dr) * coreR;
        Vector3 radial = (f.x * Mathf.Sin(a) * dr + f.z * Mathf.Cos(a)).normalized;
        Vector3 c = f.o + f.y * (b.length * t) + onSurface + radial * th * 0.15f;
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

    void BuildMuscles()
    {
        for (int i = 0; i < plan.bones.Count; i++)
        {
            var b = plan.bones[i];
            // Outward for a limb = away from the body's midline.
            float o = b.side >= 0 ? 1f : -1f;
            switch (b.kind)
            {
                case BoneKind.Chest:
                    foreach (float s in new[] { -1f, 1f })
                    {
                        AddBlob(i, MuscleGroup.Pectorals, 0.72f, s * 36f, 0.55f, 0.52f, 0.45f);
                        // Men's pecs sit closer together (centres pecInset nearer the midline each).
                        if (plan.sheet.sex == Sex.Male && blobs.Count > 0 && blobs[blobs.Count - 1].group == MuscleGroup.Pectorals)
                        {
                            var bl = blobs[blobs.Count - 1];
                            bl.f.o -= frames[i].x * s * BodyRules.Default.malePecInset;
                            blobs[blobs.Count - 1] = bl;
                        }
                        AddBlob(i, MuscleGroup.Lats, 0.45f, s * 125f, 0.75f, 0.6f, 0.45f);
                        AddBlob(i, MuscleGroup.Trapezius, 0.92f, s * 155f, 0.35f, 0.7f, 0.5f);
                    }
                    break;
                case BoneKind.Lumbar:
                    // A 2 x 3 grid of small blobs: the gaps between them are the definition.
                    foreach (float s in new[] { -1f, 1f })
                        foreach (float t in new[] { 0.22f, 0.5f, 0.78f })
                            AddBlob(i, MuscleGroup.Abdominals, t, s * 14f, 0.26f, 0.3f, 0.5f);
                    break;
                case BoneKind.Pelvis:
                    foreach (float s in new[] { -1f, 1f })
                    {
                        AddBlob(i, MuscleGroup.Glutes, 0.6f, s * 148f, 1.3f, 0.5f, 1.2f);
                        // Like breasts: how far the buttock stands out grows with its size (glute
                        // thickness + buttock fat): lean ones sit close to the body, heavy ones project.
                        if (blobs.Count > 0 && blobs[blobs.Count - 1].group == MuscleGroup.Glutes)
                        {
                            var rules = BodyRules.Default;
                            var bl = blobs[blobs.Count - 1];
                            float size = bl.r.z + (plan.dna != null ? plan.dna.Fat(FatRegion.Buttocks) : 0f);
                            float k = Mathf.Lerp(rules.buttProjection.x, rules.buttProjection.y,
                                                 Mathf.InverseLerp(rules.buttProjectionSizes.x, rules.buttProjectionSizes.y, size));
                            bl.f.o += bl.f.z * (k - 1.15f) * bl.r.z; // AddBlob leaves it standing out 1.15 x its depth
                            blobs[blobs.Count - 1] = bl;
                        }
                    }
                    break;
                case BoneKind.Neck:
                    foreach (float s in new[] { -1f, 1f }) AddBlob(i, MuscleGroup.Trapezius, 0.2f, s * 140f, 0.9f, 0.6f, 0.5f);
                    break;
                case BoneKind.UpperArm:
                    AddBlob(i, MuscleGroup.Deltoids, 0.1f, o * 90f, 0.32f, 0.95f, 1.2f);
                    AddBlob(i, MuscleGroup.Biceps, 0.55f, 0f, 0.5f, 0.6f);
                    AddBlob(i, MuscleGroup.Triceps, 0.45f, 180f, 0.55f, 0.65f);
                    break;
                case BoneKind.Forearm:
                    AddBlob(i, MuscleGroup.Forearm, 0.28f, o * 30f, 0.5f, 0.8f);
                    break;
                case BoneKind.Thigh:
                    AddBlob(i, MuscleGroup.Quadriceps, 0.5f, o * 15f, 0.75f, 0.8f);
                    AddBlob(i, MuscleGroup.Hamstrings, 0.5f, 180f, 0.65f, 0.7f, 0.8f);
                    break;
                case BoneKind.Shin:
                    AddBlob(i, MuscleGroup.Calves, 0.3f, 180f, 0.45f, 0.75f, 1.1f);
                    break;
            }
        }
    }

    void BuildHead()
    {
        if (headIndex < 0) return;
        var b = plan.bones[headIndex];
        var f = frames[headIndex];
        var dna = plan.dna;
        float rx = b.Outer.y, ry = b.length * 0.5f, rz = b.Outer.y * 1.08f;
        // INT elongation: longer up and toward the back of the skull.
        float el = dna != null ? dna.headElongation : 0f;
        Vector3 c = f.o + f.y * ry * (1f + el * 0.3f) - f.z * rz * el * 0.5f;
        skull = new Feature { f = new Frame { o = c, x = f.x, y = f.y, z = f.z }, r = new Vector3(rx, ry * (1f + el * 0.3f), rz * (1f + el * 0.6f)) };
        Vector3 R = new Vector3(rx, ry, rz);
        void F(Vector3 at, Vector3 size, float scale)
        {
            Vector3 p = c + f.x * at.x * rx + f.y * at.y * ry + f.z * at.z * rz + f.z * rz * el * 0.5f;
            headFeatures.Add(new Feature { f = new Frame { o = p, x = f.x, y = f.y, z = f.z }, r = Vector3.Scale(size, R) * scale });
        }
        float brow = dna?.brow ?? 1f, cheek = dna?.cheekbones ?? 1f, jaw = dna?.jaw ?? 1f, nose = dna?.nose ?? 1f, chin = dna?.chin ?? 1f;
        F(new Vector3(0f, 0.18f, 0.82f), new Vector3(0.75f, 0.11f, 0.16f), brow);
        foreach (float s in new[] { -1f, 1f }) F(new Vector3(s * 0.55f, -0.08f, 0.68f), new Vector3(0.24f, 0.14f, 0.24f), cheek);
        F(new Vector3(0f, -0.48f, 0.32f), new Vector3(0.72f, 0.36f, 0.68f), jaw);
        F(new Vector3(0f, -0.12f, 0.98f), new Vector3(0.11f, 0.22f, 0.2f), nose);
        F(new Vector3(0f, -0.8f, 0.72f), new Vector3(0.28f, 0.15f, 0.2f), chin);
        EyeRadius = rx * 0.17f;
        Vector3 e = c + f.y * 0.06f * ry + f.z * rz * (0.86f + el * 0.5f);
        EyeL = e - f.x * rx * 0.38f;
        EyeR = e + f.x * rx * 0.38f;
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
        for (int i = 0; i < plan.bones.Count; i++) { float db = kernel.BoneCore(i, p); if (db < 0.06f) sb.Append($"{plan.bones[i].name} {db:0.000}, "); }
        foreach (var bl in blobs) { float d = Ellipsoid(bl.f.ToLocal(p), bl.r); if (d < 0.03f) sb.Append($"{bl.group}@{plan.bones[bl.bone].name} {d:0.000}, "); }
        if (headIndex >= 0) foreach (var ft in headFeatures) { float d = Ellipsoid(ft.f.ToLocal(p), ft.r); if (d < 0.03f) sb.Append($"feature {d:0.000}, "); }
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
