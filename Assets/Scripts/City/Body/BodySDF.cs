using System.Collections.Generic;
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
public class BodySDF
{
    // ---------- tunables (BodyRules could take these later) ----------
    public float jointBlend = 0.015f;      // core: bone with its parent (+ jointBlendGirth x the thinner core)
    public float jointBlendGirth = 0.6f;
    public float muscleBlend = 0.008f;     // small: muscle shapes stay distinct
    public float fatBlendPerMetre = 1.6f;  // fat blend radius = this x local fat thickness (+ minimum)
    public float fatBlendMin = 0.004f;
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
    readonly Frame[] frames;                       // bone frames: y along the bone, z the body's front
    readonly float[] depthRatio;
    readonly List<Blob> blobs = new List<Blob>();
    readonly List<Feature> headFeatures = new List<Feature>();
    readonly List<Feature> breasts = new List<Feature>();
    Feature skull;
    public Vector3 EyeL, EyeR;
    public float EyeRadius;
    public Bounds bounds;
    readonly float[] dBone, jointK, wFat;
    readonly int headIndex;

    public BodyPlan Plan => plan;

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
        start = new Vector3[n]; end = new Vector3[n]; frames = new Frame[n]; depthRatio = new float[n]; dBone = new float[n];
        jointK = new float[n]; wFat = new float[n];
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
            jointK[i] = torsoPair ? jointBlend : jointBlend + jointBlendGirth * Mathf.Min(plan.bones[i].girth.y, plan.bones[pa].girth.y);
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
    }

    static bool IsTorso(BoneKind k) => k == BoneKind.Pelvis || k == BoneKind.Lumbar || k == BoneKind.Chest;

    // ---------- construction ----------

    static Vector3 Lerp3(Vector3 v, float t) => t < 0.5f ? Vector3.Lerp(new Vector3(v.x, 0, 0), new Vector3(v.y, 0, 0), t * 2f) : Vector3.Lerp(new Vector3(v.y, 0, 0), new Vector3(v.z, 0, 0), t * 2f - 1f);
    static float Profile(Vector3 v, float t) => Lerp3(v, Mathf.Clamp01(t)).x;

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
                        AddBlob(i, MuscleGroup.Lats, 0.5f, s * 118f, 0.55f, 0.45f);
                        AddBlob(i, MuscleGroup.Trapezius, 0.95f, s * 150f, 0.3f, 0.55f, 0.9f);
                    }
                    break;
                case BoneKind.Lumbar:
                    // A 2 x 3 grid of small blobs: the gaps between them are the definition.
                    foreach (float s in new[] { -1f, 1f })
                        foreach (float t in new[] { 0.22f, 0.5f, 0.78f })
                            AddBlob(i, MuscleGroup.Abdominals, t, s * 14f, 0.26f, 0.3f, 0.5f);
                    break;
                case BoneKind.Pelvis:
                    foreach (float s in new[] { -1f, 1f }) AddBlob(i, MuscleGroup.Glutes, 0.6f, s * 148f, 1.3f, 0.5f, 1.2f);
                    break;
                case BoneKind.Neck:
                    foreach (float s in new[] { -1f, 1f }) AddBlob(i, MuscleGroup.Trapezius, 0.2f, s * 140f, 0.9f, 0.6f, 0.8f);
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
            Vector3 p = start[chest] + sock.localPos - f.z * s * 0.15f;
            breasts.Add(new Feature { f = new Frame { o = p, x = f.x, y = f.y, z = f.z }, r = new Vector3(s, s * 0.85f, s * 0.6f) });
        }
    }

    // ---------- evaluation ----------

    static float SMin(float a, float b, float k)
    {
        if (k <= 0f) return Mathf.Min(a, b);
        float h = Mathf.Max(k - Mathf.Abs(a - b), 0f) / k;
        return Mathf.Min(a, b) - h * h * k * 0.25f;
    }
    static float SMax(float a, float b, float k) => -SMin(-a, -b, k);

    static float Ellipsoid(Vector3 p, Vector3 r)
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
        float k = -b * qx + a * qy;   // dot(q, (-b, a))
        if (k < 0f) return new Vector2(qx, qy).magnitude - r1;
        if (k > a * h) return new Vector2(qx, qy - h).magnitude - r2;
        return qx * a + qy * b - r1;  // dot(q, (a, b)) - r1
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

    float BoneCore(int i, Vector3 p)
    {
        var b = plan.bones[i];
        var f = frames[i];
        Vector3 q = f.ToLocal(p);
        switch (b.kind)
        {
            case BoneKind.Hand:
            {
                // Mitten: palm + closed fingers as one rounded box, a thumb capsule on the front-inner side.
                float L = b.length, w = b.girth.y + b.fat.y;
                float palm = RoundBox(q - new Vector3(0f, L * 0.48f, 0f), new Vector3(w * 1.25f, L * 0.48f, w * 0.6f), w * 0.5f);
                float inner = b.side < 0 ? 1f : -1f; // towards the body's midline (x = the body's right)
                Vector3 t0 = f.o + f.y * L * 0.15f + f.z * w * 0.4f + f.x * inner * w * 0.9f;
                Vector3 t1 = f.o + f.y * L * 0.55f + f.z * w * 0.9f + f.x * inner * w * 1.1f;
                return SMin(palm, Capsule(p, t0, t1, w * 0.42f), 0.008f);
            }
            case BoneKind.Foot:
            {
                // Rounded wedge from heel to toe, sole on the ground, sloping down to the toes.
                float L = (end[i] - start[i]).magnitude;
                Vector3 ankle = start[i], toe = end[i];
                Vector3 fwd = Vector3.ProjectOnPlane(toe - ankle, Vector3.up).normalized;
                Vector3 side = Vector3.Cross(Vector3.up, fwd);
                float footLen = L / 0.72f * 0.95f;
                float h = ankle.y + b.girth.y * 0.6f;
                Vector3 centre = new Vector3(ankle.x, 0f, ankle.z) + fwd * (footLen * 0.5f - footLen * 0.28f) + Vector3.up * h * 0.5f;
                Vector3 lp = p - centre;
                Vector3 l = new Vector3(Vector3.Dot(lp, side), Vector3.Dot(lp, Vector3.up), Vector3.Dot(lp, fwd));
                float w = b.girth.y * 1.15f;
                float box = RoundBox(l, new Vector3(w, h * 0.5f, footLen * 0.5f), Mathf.Min(w, h * 0.5f) * 0.6f);
                // Slope: a plane from above the ankle down to the toe tip.
                Vector3 n = new Vector3(0f, footLen * 0.75f, h * 0.9f).normalized;
                float plane = Vector3.Dot(l - new Vector3(0f, h * 0.5f, -footLen * 0.1f), n);
                return SMax(box, plane, 0.01f);
            }
            case BoneKind.Head:
                return Ellipsoid(skull.f.ToLocal(p), skull.r); // features go on after the fat (Eval)
        }
        float dr = depthRatio[i];
        Vector3 s = new Vector3(q.x, q.y, q.z / dr);
        float L2 = b.length * 0.5f;
        if (IsTorso(b.kind))
        {
            // Torso: flat-capped cones, so a wide segment's end cap never bulges over its neighbour
            // (round caps would swallow the waist); a dome closes the open ends (buttocks, shoulders).
            float t1 = CappedCone(s, 0f, L2, b.girth.x, b.girth.y);
            float t2 = CappedCone(s, L2, b.length, b.girth.y, b.girth.z);
            float dt = Mathf.Min(t1, t2);
            if (b.kind != BoneKind.Lumbar)
            {
                float re = b.girth.z;
                float dome = Ellipsoid(s - new Vector3(0f, b.length, 0f), new Vector3(re, re * 0.5f, re));
                dt = SMin(dt, dome, re * 0.3f);
            }
            return dt * dr;
        }
        // Round cones start->mid and mid->end, flattened front-to-back by depthRatio.
        float d1 = RoundCone(s, b.girth.x, b.girth.y, L2);
        float d2 = RoundCone(s - new Vector3(0f, L2, 0f), b.girth.y, b.girth.z, L2);
        return Mathf.Min(d1, d2) * dr; // squashing z by 1/dr stretches distances by up to 1/dr: stay a lower bound
    }

    // Fat thickness here: the nearest bone's fat profile at the point's position along it, steered
    // round the torso (belly in front, buttocks behind, hips at the sides).
    float FatAt(Vector3 p, int bone)
    {
        if (bone < 0) return 0f;
        var b = plan.bones[bone];
        var f = frames[bone];
        Vector3 q = f.ToLocal(p);
        float t = Mathf.Clamp01(q.y / Mathf.Max(1e-4f, b.length));
        float th = Profile(b.fat, t);
        var dna = plan.dna;
        if (dna == null) return th;
        Vector2 dir = new Vector2(q.x, q.z);
        float front = dir.sqrMagnitude > 1e-8f ? dir.normalized.y : 0f;      // +1 front, -1 back
        float sideAmt = dir.sqrMagnitude > 1e-8f ? Mathf.Abs(dir.normalized.x) : 0f;
        switch (b.kind)
        {
            case BoneKind.Lumbar:
                th = Mathf.Lerp(dna.Fat(FatRegion.Waist), dna.Fat(FatRegion.Belly), Mathf.Clamp01(front)) * (front < 0f ? Mathf.Lerp(1f, 0.6f, -front) : 1f);
                break;
            case BoneKind.Pelvis:
                th = front < 0f ? Mathf.Lerp(dna.Fat(FatRegion.Hips), dna.Fat(FatRegion.Buttocks), -front)
                                : Mathf.Lerp(dna.Fat(FatRegion.Hips), dna.Fat(FatRegion.Belly) * 0.7f, front);
                break;
            case BoneKind.Chest:
                th = Mathf.Lerp(th, dna.Fat(FatRegion.Waist) * 0.6f, (1f - t) * 0.5f) * (1f - 0.3f * sideAmt);
                break;
        }
        return th;
    }

    // Signed distance to the skin.
    public float Eval(Vector3 p) => Eval(p, out _, out _);

    // Also the nearest bone and the local fat thickness (for regions and layer data).
    public float Eval(Vector3 p, out int nearest, out float fatThickness)
    {
        int n = plan.bones.Count;
        nearest = -1;
        float best = float.MaxValue;
        for (int i = 0; i < n; i++)
        {
            float d = BoneCore(i, p);
            dBone[i] = d;
            if (d < best) { best = d; nearest = i; }
        }
        // Fat thickness: blended over the nearby bones (weights fall off with distance), so it never
        // steps where the nearest bone changes.
        float wSum = 0f, fSum = 0f;
        for (int i = 0; i < n; i++)
        {
            float w = Mathf.Exp(-(dBone[i] - best) / 0.025f);
            if (w < 0.01f) continue;
            wSum += w; fSum += w * FatAt(p, i);
        }
        fatThickness = wSum > 0f ? fSum / wSum : 0f;
        float kFat = fatBlendMin + fatBlendPerMetre * fatThickness;
        // Core: each bone smooth-unions with its parent only; the soft (fat) version blends wider.
        float core = float.MaxValue, soft = float.MaxValue;
        for (int i = 0; i < n; i++)
        {
            int pa = plan.bones[i].parent;
            if (pa < 0) { core = Mathf.Min(core, dBone[i]); soft = Mathf.Min(soft, dBone[i]); continue; }
            core = Mathf.Min(core, SMin(dBone[i], dBone[pa], jointK[i]));
            soft = Mathf.Min(soft, SMin(dBone[i], dBone[pa], Mathf.Max(jointK[i], kFat)));
        }
        float muscle = core;
        foreach (var bl in blobs)
        {
            if (dBone[bl.bone] > 0.12f) continue; // far from this bone: its muscles can't matter
            float d = Ellipsoid(bl.f.ToLocal(p), bl.r);
            muscle = SMin(muscle, d, muscleBlend);
            soft = SMin(soft, d, kFat);
        }
        float skin = Mathf.Min(muscle, soft - fatThickness);
        foreach (var br in breasts) skin = SMin(skin, Ellipsoid(br.f.ToLocal(p), br.r), breastBlend);
        // Face features sit on the skin: pushed out by the face's fat so they stay visible.
        if (headIndex >= 0 && dBone[headIndex] < 0.08f)
        {
            float faceFat = Profile(plan.bones[headIndex].fat, 0.5f);
            foreach (var ft in headFeatures)
                skin = SMin(skin, Ellipsoid(ft.f.ToLocal(p) - new Vector3(0f, 0f, faceFat), ft.r), featureBlend);
        }
        if (EyeRadius > 0f)
        {
            skin = SMax(skin, -((p - EyeL).magnitude - EyeRadius), eyeCarveBlend);
            skin = SMax(skin, -((p - EyeR).magnitude - EyeRadius), eyeCarveBlend);
        }
        return skin;
    }

    // Muscle-only surface (no fat), for debugging the layers.
    public float EvalMuscle(Vector3 p)
    {
        int n = plan.bones.Count;
        for (int i = 0; i < n; i++) dBone[i] = BoneCore(i, p);
        float core = float.MaxValue;
        for (int i = 0; i < n; i++)
        {
            int pa = plan.bones[i].parent;
            core = Mathf.Min(core, pa >= 0 ? SMin(dBone[i], dBone[pa], jointK[i]) : dBone[i]);
        }
        foreach (var bl in blobs)
        {
            if (dBone[bl.bone] > 0.12f) continue;
            core = SMin(core, Ellipsoid(bl.f.ToLocal(p), bl.r), muscleBlend);
        }
        return core;
    }

    public int BlobCount => blobs.Count;

    // Debug: what is near a point (bones and blobs with their distances).
    public string DescribePoint(Vector3 p)
    {
        var sb = new System.Text.StringBuilder();
        float skin = Eval(p, out int nearest, out float fat);
        sb.Append($"skin {skin:0.000} fat {fat:0.000} nearest {plan.bones[nearest].name}; ");
        for (int i = 0; i < plan.bones.Count; i++) if (dBone[i] < 0.06f) sb.Append($"{plan.bones[i].name} {dBone[i]:0.000}, ");
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
