using System.Collections.Generic;
using UnityEngine;

// Clothing Part B / Phase 8a-b: spring-bone chains for a garment's skirt (coat tails). Panels around the
// body (6 by default: front-left / right either side of the opening, sides, back), each a chain of
// `bones` bones from a ring at the hips down to the hem, parented to the pelvis. Built from the
// garment's skirt surface; the skirt's vertices are skinned to them (vertically between consecutive
// bones of a panel, round the body between the two nearest panels unless an opening or slit lies
// between them, into the pelvis near the ring).
public class ClothChainSet
{
    public int garment;               // the outfit garment (its submesh is 1 + garment)
    public int panels, bones;
    public float[] angles;            // panel angles round the body (deg, 0 = front, + = right)
    public Vector3[] joints;          // bind pose, root space: [panel * (bones + 1) + level], level 0 = the ring, `bones` = the hem
    public Vector2 axis;              // skirt axis (x, z)
    public float ringY, hemY, slitTopY;
    public float[] gaps;              // angles nothing blends across: the front opening and the slits (below slitTopY)
    public SkirtDef def;
    public int firstBone;             // renderer bone index of panel 0, bone 0 (after the plan's bones)

    public int BoneIndex(int panel, int bone) => firstBone + panel * bones + bone;
    public Vector3 Joint(int panel, int level) => joints[panel * (bones + 1) + level];
    public int BoneCount => panels * bones;

    // Measure the chains on a garment's skirt: each panel's joints just inside its outer surface.
    public static ClothChainSet Build(GarmentKernel k, GarmentDef g, int garment, float hemY, int firstBone)
    {
        var sk = g.skirt;
        var c = new ClothChainSet
        {
            garment = garment, panels = Mathf.Max(2, sk.panels), bones = Mathf.Max(1, sk.bonesPerPanel), def = sk, firstBone = firstBone,
            axis = k.axis, ringY = k.skirtTop - 0.03f, hemY = hemY + 0.01f, slitTopY = k.slitTopY,
        };
        c.angles = new float[c.panels];
        for (int p = 0; p < c.panels; p++) c.angles[p] = -180f + (p + 0.5f) * 360f / c.panels;
        var gaps = new List<float> { 0f };
        foreach (var a in sk.slitAngles) gaps.Add(a);
        c.gaps = gaps.ToArray();
        c.joints = new Vector3[c.panels * (c.bones + 1)];
        float off = g.thickness + g.gap;
        for (int p = 0; p < c.panels; p++)
        {
            float a = c.angles[p] * Mathf.Deg2Rad;
            Vector3 dir = new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a));
            for (int l = 0; l <= c.bones; l++)
            {
                float y = Mathf.Lerp(c.ringY, c.hemY, l / (float)c.bones);
                // Outward from the axis to the skirt's outer surface (the last point inside it).
                float r = 0.05f;
                for (float t = 0.02f; t < 0.7f; t += 0.004f)
                {
                    Vector3 q = new Vector3(c.axis.x, y, c.axis.y) + dir * t;
                    if (k.Shape(q) - off <= 0f) r = t;
                }
                c.joints[p * (c.bones + 1) + l] = new Vector3(c.axis.x, y, c.axis.y) + dir * Mathf.Max(0.05f, r - g.thickness * 0.5f);
            }
        }
        return c;
    }

    // Skin weights for a skirt vertex at bind position v; null above the skirt (keep the body's).
    public BoneWeight? Weight(Vector3 v, BoneWeight body, int pelvisBone)
    {
        float seg = (ringY - hemY) / bones;
        float s = (ringY - v.y) / seg;                  // levels below the ring
        if (s <= -0.3f) return null;
        // Round the body: the two panels either side of the vertex (not across an opening or slit).
        float ang = Mathf.Atan2(v.x - axis.x, v.z - axis.y) * Mathf.Rad2Deg;
        int p0 = 0; float best = float.MaxValue;
        for (int p = 0; p < panels; p++) { float d = Mathf.Abs(Mathf.DeltaAngle(ang, angles[p])); if (d < best) { best = d; p0 = p; } }
        int p1 = Mathf.DeltaAngle(angles[p0], ang) > 0f ? (p0 + 1) % panels : (p0 + panels - 1) % panels;
        float span = Mathf.Abs(Mathf.DeltaAngle(angles[p0], angles[p1]));
        float t = Mathf.Clamp01(Mathf.Abs(Mathf.DeltaAngle(angles[p0], ang)) / Mathf.Max(1f, span));
        foreach (var gap in gaps)
        {
            bool isSlit = gap != 0f;
            if (isSlit && v.y > slitTopY + 0.03f) continue;          // the slits only open below their tops
            if (Between(angles[p0], angles[p1], gap)) { t = 0f; break; }
        }
        // Down the panel: within bone b, blending into the one above (or the pelvis) near its top.
        int b = Mathf.Clamp(Mathf.FloorToInt(Mathf.Max(0f, s)), 0, bones - 1);
        float f = s - b;
        float u = b > 0 ? 0.5f * (1f - Mathf.SmoothStep(0f, 1f, f / 0.3f)) : 1f - Mathf.SmoothStep(0f, 1f, (s + 0.3f) / 0.8f);
        int upper0 = b > 0 ? BoneIndex(p0, b - 1) : pelvisBone, upper1 = b > 0 ? BoneIndex(p1, b - 1) : pelvisBone;
        var w = new Dictionary<int, float>();
        void Add(int i, float x) { if (x <= 0f) return; w.TryGetValue(i, out float o); w[i] = o + x; }
        Add(BoneIndex(p0, b), (1f - t) * (1f - u)); Add(BoneIndex(p1, b), t * (1f - u));
        if (b > 0) { Add(upper0, (1f - t) * u); Add(upper1, t * u); }
        else
        {
            // Near the ring: the body's own weights (pelvis, thighs ...), so there's no seam at the waist.
            Add(body.boneIndex0, body.weight0 * u); Add(body.boneIndex1, body.weight1 * u); Add(body.boneIndex2, body.weight2 * u); Add(body.boneIndex3, body.weight3 * u);
        }
        var list = new List<KeyValuePair<int, float>>(w);
        list.Sort((x, y) => y.Value.CompareTo(x.Value));
        float sum = 0f; for (int i = 0; i < Mathf.Min(4, list.Count); i++) sum += list[i].Value;
        var r = new BoneWeight();
        for (int i = 0; i < Mathf.Min(4, list.Count); i++)
        {
            float x = list[i].Value / Mathf.Max(1e-6f, sum);
            switch (i) { case 0: r.boneIndex0 = list[i].Key; r.weight0 = x; break; case 1: r.boneIndex1 = list[i].Key; r.weight1 = x; break;
                         case 2: r.boneIndex2 = list[i].Key; r.weight2 = x; break; default: r.boneIndex3 = list[i].Key; r.weight3 = x; break; }
        }
        return r;
    }

    // Is angle g strictly between a and b (the short way round)?
    static bool Between(float a, float b, float g)
    {
        float ab = Mathf.DeltaAngle(a, b), ag = Mathf.DeltaAngle(a, g);
        return ab > 0f ? ag > 0f && ag < ab : ag < 0f && ag > ab;
    }
}
