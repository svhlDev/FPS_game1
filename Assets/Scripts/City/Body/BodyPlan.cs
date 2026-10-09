using System;
using System.Collections.Generic;
using UnityEngine;

// Phase 1 of character generation: a body plan built by rules, validated (generate-and-test), then
// turned into a bone hierarchy by BodySkeleton.
//   Spine   : a chain pelvis -> lumbar -> chest -> neck -> head (humans: fixed segments; variable
//             spine and neck lengths).
//   Legs    : at least minLegs (humans: exactly 2). Plantigrade or digitigrade (later species).
//   Arms    : 0..maxArms. The first pair on the shoulders; more pairs on extra girdles lower on the
//             chest; an odd arm goes on one side or the centre of the chest (seed-chosen).
//   Bones   : joint position (rest, in the parent joint's space), rest rotation, direction, length and
//             girth profiles (radius at start, middle, end) for the three layers: core (bone and
//             base bulk), muscle (thickness on top) and fat (thickness on top of that). Outer = skin.
// Phase 2: lengths and layers come from BodyDNA (sex + sheet + seed), see BodyDNA.cs.
//   Hands   : digits (thumb, index, middle, ring, little) as chains of segment bones (fingers 3, the
//             thumb 2) under the hand, added after every other bone (the SDF kernel takes the bones
//             before them; each segment is a capsule of the hand).
//   Sockets : named attach points on bones (grips, eyes, holster) for gear, clothing and cyberware later.
// Joint rotations rest at identity in root space, exactly like CharacterFigure, so FigureAnimator's
// convention holds: limbs hang along -Y, a negative X rotation swings them forward.
// Species hook: ISpeciesTemplate (body plan rules + trait distributions); only HumanTemplate exists.
public enum BoneKind { Pelvis, Lumbar, Chest, Neck, Head, UpperArm, Forearm, Hand, Thigh, Shin, Foot, Finger }
public enum LegType { Plantigrade, Digitigrade }

[Serializable]
public class BoneSpec
{
    public string name;
    public BoneKind kind;
    public int parent = -1;          // index into BodyPlan.bones
    public int side;                 // -1 left, 0 centre, +1 right
    public int limb = -1;            // index into arms / legs (-1 for the spine)
    public Vector3 localPos;         // joint, in the parent joint's space
    public Quaternion restRotation = Quaternion.identity;
    public Vector3 dir;              // bone direction in its joint's space
    public float length;
    public Vector3 girth;            // core radius at start, middle, end
    public Vector3 muscle;           // muscle layer thickness at start, middle, end
    public Vector3 fat;              // fat layer thickness at start, middle, end
    public BodyPart.Location location;
    public Vector3 shapeOffset;      // the shape's offset from the joint (digits: the knuckle pivot sits toward the palm)
    public Vector3 MuscleSurface => girth + muscle;
    public Vector3 Outer => girth + muscle + fat;
}

[Serializable]
public class LimbSpec
{
    public int side;                 // -1 / 0 / +1
    public int girdle;               // 0 = shoulders / hips, 1+ = extra girdles
    public int root, mid, end, tip;  // bone indices: upper, lower, hand/foot (tip = end bone)
    public int[] digits;             // hands: thumb, index, middle, ring, little (their first segment's bone; after every other bone)
    public List<int> digitBones;     // hands: every digit segment's bone
}

[Serializable]
public struct Socket
{
    public string name;
    public int bone;
    public Vector3 localPos;
}

[Serializable]
public class BodyPlan
{
    public CharacterSheet sheet;
    public BodyDNA dna;
    public string species;
    public float height;
    public int spineSegments;
    public LegType legType;
    public List<BoneSpec> bones = new List<BoneSpec>();
    public List<LimbSpec> arms = new List<LimbSpec>(), legs = new List<LimbSpec>();
    public List<Socket> sockets = new List<Socket>();
    public int rerolls;              // generate-and-test attempts that failed validation
    public string nudged;            // what the planner had to fix, if anything

    public int Index(string name) => bones.FindIndex(b => b.name == name);

    public int Add(BoneSpec b) { bones.Add(b); return bones.Count - 1; }

    // Joint position in root space (rest pose; all rest rotations are identity for humans).
    public Vector3 JointPos(int i)
    {
        Vector3 p = Vector3.zero;
        Quaternion r = Quaternion.identity;
        var chain = new List<int>();
        for (int b = i; b >= 0; b = bones[b].parent) chain.Add(b);
        for (int k = chain.Count - 1; k >= 0; k--)
        {
            var b = bones[chain[k]];
            p += r * b.localPos;
            r *= b.restRotation;
        }
        return p;
    }

    public Vector3 BoneEnd(int i) => JointPos(i) + RestRot(i) * bones[i].dir * bones[i].length;

    Quaternion RestRot(int i)
    {
        Quaternion r = Quaternion.identity;
        for (int b = i; b >= 0; b = bones[b].parent) r = bones[b].restRotation * r;
        return r;
    }

    // Rough centre of mass: bone midpoints weighted by volume (girth^2 x length).
    public Vector3 CenterOfMass()
    {
        Vector3 sum = Vector3.zero; float w = 0f;
        for (int i = 0; i < bones.Count; i++)
        {
            var b = bones[i];
            float m = b.Outer.y * b.Outer.y * b.length;
            sum += (JointPos(i) + BoneEnd(i)) * 0.5f * m;
            w += m;
        }
        return w > 0f ? sum / w : Vector3.zero;
    }
}

public interface ISpeciesTemplate
{
    string Name { get; }
    BodyPlan Sample(CharacterSheet sheet, BodyRules rules, int attempt);
}

public static class BodyPlanner
{
    public static readonly ISpeciesTemplate Human = new HumanTemplate();

    // Generate-and-test: sample, validate, re-roll (a new attempt salt) up to maxRerolls, then nudge.
    public static BodyPlan Generate(CharacterSheet sheet, BodyRules rules = null, ISpeciesTemplate template = null)
    {
        rules ??= BodyRules.Default;
        template ??= Human;
        BodyPlan plan = null;
        for (int attempt = 0; attempt <= rules.maxRerolls; attempt++)
        {
            plan = template.Sample(sheet, rules, attempt);
            plan.rerolls = attempt;
            if (Validate(plan, rules, out _)) return plan;
        }
        Nudge(plan, rules);
        return plan;
    }

    public static bool Validate(BodyPlan plan, BodyRules rules, out string why)
    {
        why = null;
        if (plan.legs.Count < rules.minLegs) { why = $"{plan.legs.Count} legs (< {rules.minLegs})"; return false; }
        if (plan.arms.Count > rules.maxArms) { why = $"{plan.arms.Count} arms (> {rules.maxArms})"; return false; }
        if (!LegsUnderMass(plan, out why)) return false;
        if (plan.arms.Count > 0 && !HeadReachable(plan)) { why = "no hand reaches the head"; return false; }
        return true;
    }

    // The centre of mass over the feet's footprint (expanded a little).
    static bool LegsUnderMass(BodyPlan plan, out string why)
    {
        why = null;
        Vector3 com = plan.CenterOfMass();
        float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
        foreach (var leg in plan.legs)
            foreach (var p in new[] { plan.JointPos(leg.tip), plan.BoneEnd(leg.tip) })
            {
                minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x);
                minZ = Mathf.Min(minZ, p.z); maxZ = Mathf.Max(maxZ, p.z);
            }
        float margin = 0.05f * plan.height;
        bool ok = com.x > minX - margin && com.x < maxX + margin && com.z > minZ - margin && com.z < maxZ + margin;
        if (!ok) why = $"centre of mass ({com.x:0.00}, {com.z:0.00}) outside the feet";
        return ok;
    }

    static bool HeadReachable(BodyPlan plan)
    {
        int head = plan.Index("Head");
        if (head < 0) return true;
        Vector3 hc = (plan.JointPos(head) + plan.BoneEnd(head)) * 0.5f;
        foreach (var arm in plan.arms)
        {
            float reach = plan.bones[arm.root].length + plan.bones[arm.mid].length + plan.bones[arm.end].length;
            if (Vector3.Distance(plan.JointPos(arm.root), hc) <= reach) return true;
        }
        return false;
    }

    // Last resort after the re-rolls: lengthen arms until a hand reaches the head, pull the feet under
    // the body.
    static void Nudge(BodyPlan plan, BodyRules rules)
    {
        var fixes = new List<string>();
        for (int k = 0; k < 20 && plan.arms.Count > 0 && !HeadReachable(plan); k++)
            foreach (var arm in plan.arms) { plan.bones[arm.root].length *= 1.05f; plan.bones[arm.mid].length *= 1.05f; }
        if (plan.arms.Count > 0) fixes.Add("arms lengthened");
        if (!LegsUnderMass(plan, out _))
        {
            Vector3 com = plan.CenterOfMass();
            foreach (var leg in plan.legs)
            {
                var hip = plan.bones[leg.root];
                hip.localPos = new Vector3(hip.localPos.x, hip.localPos.y, hip.localPos.z + com.z * 0.5f);
            }
            fixes.Add("legs moved under the mass");
        }
        plan.nudged = string.Join(", ", fixes);
    }
}

// Humans: 5 spine segments, 2 plantigrade legs, armCount arms (2; more only for testing the plan rules).
public class HumanTemplate : ISpeciesTemplate
{
    public int armCount = 2;
    public string Name => "Human";

    public BodyPlan Sample(CharacterSheet sheet, BodyRules rules, int attempt)
    {
        var t = rules.Template(sheet.sex);
        var dna = BodyDNA.Compute(sheet, rules);
        string salt = attempt == 0 ? "" : "#" + attempt;
        var plan = new BodyPlan { sheet = sheet, dna = dna, species = Name, spineSegments = 5, legType = LegType.Plantigrade };
        float H = dna.height;

        // Per-bone noise: a shared draw per bone kind plus a smaller per-side one (left and right differ
        // a little, never a lot).
        float Noise(string key, float amount, int side)
        {
            float n = new PcgRandom(sheet.seed, key + salt).Signed();
            if (side != 0) n += 0.3f * new PcgRandom(sheet.seed, key + side + salt).Signed();
            return 1f + amount * n;
        }
        float Len(string key, float frac, int side = 0) => frac * H * Noise("len." + key, rules.lengthNoise, side);

        // Layers. The template girth (x house style) is an average body's skin; it splits into core,
        // muscle (share of the girth) and fat (the sex's average fat for the region). The DNA then sets
        // muscle (STR) and fat (DEX) for this body; the core only grows a little with STR.
        float avgFatScale = t.baseFat * H;
        void Layers(BoneSpec b, string key, Vector3 template, float house, float muscleShare, float muscleAmount, float fatNow, float fatAvgWeight, int side, bool taper)
        {
            Vector3 G = template * H * house * Noise("girth." + key, rules.girthNoise, side);
            Vector3 avgMuscle = G * muscleShare;
            float avgFat = avgFatScale * fatAvgWeight;
            Vector3 core = G - avgMuscle - Vector3.one * avgFat;
            core = Vector3.Max(core, G * rules.coreMinFraction) * dna.coreGirth;
            Vector3 muscle = avgMuscle * Mathf.Pow(Mathf.Max(0f, muscleAmount), rules.muscleThicknessExponent);
            Vector3 fat = Vector3.one * fatNow;
            if (taper)
            {
                // Forearms and shins narrow into the small hands and feet: the end ring is extremityTaper
                // of the middle, with little muscle and fat at the wrist / ankle.
                muscle.z = muscle.y * 0.25f; fat.z = fat.y * 0.4f;
                core.z = Mathf.Max(core.y * rules.extremityTaper - muscle.z - fat.z, core.y * 0.3f);
            }
            b.girth = core; b.muscle = muscle; b.fat = fat;
        }
        float M(params MuscleGroup[] gs) { float v = 0f; foreach (var g in gs) v += dna.Muscle(g); return v / gs.Length; }
        float F(FatRegion r) => dna.Fat(r);
        float FA(FatRegion r) => t.fat[(int)r];
        float hg = rules.houseGirth, ex = rules.extremityScale;
        float torso = rules.torsoMuscleShare, limb = rules.limbMuscleShare;

        // ---------- spine ----------
        float limbL = dna.limbLength;
        float ankleH = Len("ankle", t.ankleHeight), shin = Len("shin", t.shin) * limbL, thigh = Len("thigh", t.thigh) * limbL;
        float hipY = ankleH + shin + thigh;
        float lumbar = Len("lumbar", t.lumbar), chest = Len("chest", t.chest), neck = Len("neck", t.neck);
        float head = Len("head", t.head) * dna.headScale * (1f + dna.headElongation);
        var hipsB = new BoneSpec { name = "Hips", kind = BoneKind.Pelvis, localPos = new Vector3(0f, hipY, 0f), dir = Vector3.down,
                                   length = Len("pelvis", t.pelvis), location = BodyPart.Location.Body };
        Layers(hipsB, "pelvis", t.pelvisGirth, hg, torso, M(MuscleGroup.Glutes), (F(FatRegion.Hips) + F(FatRegion.Buttocks)) * 0.5f, (FA(FatRegion.Hips) + FA(FatRegion.Buttocks)) * 0.5f, 0, false);
        int hips = plan.Add(hipsB);
        var spineB = new BoneSpec { name = "Spine", kind = BoneKind.Lumbar, parent = hips, dir = Vector3.up, length = lumbar, location = BodyPart.Location.Body };
        Layers(spineB, "lumbar", t.lumbarGirth, hg, torso, M(MuscleGroup.Abdominals), (F(FatRegion.Belly) + F(FatRegion.Waist)) * 0.5f, (FA(FatRegion.Belly) + FA(FatRegion.Waist)) * 0.5f, 0, false);
        int spine = plan.Add(spineB);
        var chestSpec = new BoneSpec { name = "Chest", kind = BoneKind.Chest, parent = spine, localPos = new Vector3(0f, lumbar, 0f), dir = Vector3.up, length = chest, location = BodyPart.Location.Body };
        Layers(chestSpec, "chest", t.chestGirth, hg, torso, M(MuscleGroup.Pectorals, MuscleGroup.Lats, MuscleGroup.Trapezius),
               (F(FatRegion.Breasts) + F(FatRegion.Waist)) * 0.4f, (FA(FatRegion.Breasts) + FA(FatRegion.Waist)) * 0.4f, 0, false);
        int chestB = plan.Add(chestSpec);
        var neckSpec = new BoneSpec { name = "Neck", kind = BoneKind.Neck, parent = chestB, localPos = new Vector3(0f, chest, t.neckForward * H), dir = Vector3.up, length = neck, location = BodyPart.Location.Head };
        Layers(neckSpec, "neck", t.neckGirth, hg, rules.neckMuscleShare, M(MuscleGroup.Trapezius), F(FatRegion.Neck), FA(FatRegion.Neck), 0, false);
        int neckB = plan.Add(neckSpec);
        var headSpec = new BoneSpec { name = "Head", kind = BoneKind.Head, parent = neckB, localPos = new Vector3(0f, neck, 0f), dir = Vector3.up, length = head, location = BodyPart.Location.Head };
        Layers(headSpec, "head", t.headGirth * dna.headScale, 1f, 0f, 0f, F(FatRegion.Face) * 0.5f, FA(FatRegion.Face) * 0.5f, 0, false);
        int headB = plan.Add(headSpec);

        // ---------- legs ----------
        float footLen = Len("foot", t.foot) * ex * dna.extremity;
        foreach (int side in new[] { -1, 1 })
        {
            string S = side < 0 ? "L" : "R";
            var leg = new LimbSpec { side = side };
            int li = plan.legs.Count;
            var thighB = new BoneSpec { name = "Hip" + S, kind = BoneKind.Thigh, parent = hips, side = side, limb = li,
                                        localPos = new Vector3(side * t.hipHalfWidth * H, 0f, 0f), dir = Vector3.down, length = thigh, location = BodyPart.Location.Leg };
            Layers(thighB, "thigh", t.thighGirth, hg, limb, M(MuscleGroup.Quadriceps, MuscleGroup.Hamstrings), F(FatRegion.Thighs), FA(FatRegion.Thighs), side, false);
            leg.root = plan.Add(thighB);
            var shinB = new BoneSpec { name = "Knee" + S, kind = BoneKind.Shin, parent = leg.root, side = side, limb = li,
                                       localPos = new Vector3(0f, -thigh, 0f), dir = Vector3.down, length = shin, location = BodyPart.Location.Leg };
            Layers(shinB, "shin", t.shinGirth, hg, limb, M(MuscleGroup.Calves), F(FatRegion.Thighs) * 0.35f, FA(FatRegion.Thighs) * 0.35f, side, true);
            leg.mid = plan.Add(shinB);
            // Foot: from the ankle forward and down to the toes on the ground (heel behind, via girth).
            Vector3 toe = new Vector3(0f, -ankleH, footLen * 0.72f);
            var footB = new BoneSpec { name = "Ankle" + S, kind = BoneKind.Foot, parent = leg.mid, side = side, limb = li,
                                       localPos = new Vector3(0f, -shin, 0f), dir = toe.normalized, length = toe.magnitude, location = BodyPart.Location.Foot };
            Layers(footB, "foot", t.footGirth, ex * dna.extremity, 0f, 0f, F(FatRegion.Face) * 0.3f, FA(FatRegion.Face) * 0.3f, side, false);
            leg.end = leg.tip = plan.Add(footB);
            plan.legs.Add(leg);
        }

        // ---------- arms ----------
        float upper = Len("upperArm", t.upperArm) * limbL, fore = Len("forearm", t.forearm) * limbL, hand = Len("hand", t.hand) * ex * dna.extremity;
        var slots = ArmSlots(armCount, sheet.seed, salt);
        foreach (var (side, girdle) in slots)
        {
            string S = (side < 0 ? "L" : side > 0 ? "R" : "C") + (girdle > 0 ? (girdle + 1).ToString() : "");
            float y = chest - t.shoulderDrop * H - girdle * rules.extraGirdleStep * chest;
            float x = side * t.shoulderHalfWidth * H * (girdle > 0 ? 0.9f : 1f);
            float z = side == 0 ? chestSpec.Outer.y : 0f; // a centred arm sits on the chest's front
            var arm = new LimbSpec { side = side, girdle = girdle };
            int li = plan.arms.Count;
            var upperB = new BoneSpec { name = "Shoulder" + S, kind = BoneKind.UpperArm, parent = chestB, side = side, limb = li,
                                        localPos = new Vector3(x, y, z), dir = Vector3.down, length = upper, location = BodyPart.Location.Arm };
            Layers(upperB, "upperArm", t.upperArmGirth, hg, limb, M(MuscleGroup.Deltoids, MuscleGroup.Biceps, MuscleGroup.Triceps), F(FatRegion.UpperArms), FA(FatRegion.UpperArms), side, false);
            arm.root = plan.Add(upperB);
            var foreB = new BoneSpec { name = "Elbow" + S, kind = BoneKind.Forearm, parent = arm.root, side = side, limb = li,
                                       localPos = new Vector3(0f, -upper, 0f), dir = Vector3.down, length = fore, location = BodyPart.Location.Arm };
            Layers(foreB, "forearm", t.forearmGirth, hg, limb, M(MuscleGroup.Forearm), F(FatRegion.UpperArms) * 0.5f, FA(FatRegion.UpperArms) * 0.5f, side, true);
            arm.mid = plan.Add(foreB);
            var handB = new BoneSpec { name = "Wrist" + S, kind = BoneKind.Hand, parent = arm.mid, side = side, limb = li,
                                       localPos = new Vector3(0f, -fore, 0f), dir = Vector3.down, length = hand, location = BodyPart.Location.Hand };
            Layers(handB, "hand", t.handGirth, ex * dna.extremity, 0f, 0f, F(FatRegion.Face) * 0.3f, FA(FatRegion.Face) * 0.3f, side, false);
            arm.end = arm.tip = plan.Add(handB);
            plan.arms.Add(arm);
            plan.sockets.Add(new Socket { name = "hand." + S + ".grip", bone = arm.end, localPos = Vector3.down * hand * 0.5f });
        }

        // ---------- digits (after every other bone) ----------
        var hr = rules.hands;
        foreach (var arm in plan.arms)
        {
            var hb = plan.bones[arm.end];
            string S = hb.name.Substring("Wrist".Length);
            float Lp = hb.length * hr.palmLength, R = hb.Outer.y;
            float W = R * hr.palmWidth, Th = R * hr.palmThickness;
            float inner = hb.side <= 0 ? 1f : -1f; // towards the midline (x = the body's right)
            float r = Mathf.Min(W * hr.fingerRadius + hr.fingerFat * hb.fat.y, W * 0.105f); // gaps stay >= 4% of the width
            float fl = Lp * hr.fingerLength;
            arm.digits = new int[5];
            arm.digitBones = new List<int>();
            string[] names = { "Thumb", "Index", "Middle", "Ring", "Little" };
            for (int k = 0; k < 5; k++)
            {
                Vector3 pos, dir; float len, rad = r;
                if (k == 0)
                {
                    pos = new Vector3(inner * Th * 0.25f, -Lp * hr.thumbBase, W * 0.42f);
                    dir = new Vector3(inner * hr.thumbDir.x, -hr.thumbDir.y, hr.thumbDir.z).normalized;
                    len = fl * hr.fingerScale.y * hr.thumbLength;
                    rad = r * hr.thumbRadius;
                }
                else
                {
                    // Index at the front, little at the back, a quarter of the palm's width apart.
                    pos = new Vector3(0f, -Lp + r, (2.5f - k) * W * 0.25f);
                    dir = Vector3.down;
                    len = fl * hr.fingerScale[k - 1];
                }
                len *= Noise("digit." + k, hr.fingerNoise, hb.side);
                // Joints pivot on the palm side of the sausage, so a fist folds it onto the palm.
                Vector3 pivot = k == 0 ? Vector3.zero : new Vector3(inner * rad * hr.knucklePivot, 0f, 0f);
                // Segments: a chain along the digit (each joint at the previous segment's end).
                Vector4 split = k == 0 ? new Vector4(hr.thumbSegments.x, hr.thumbSegments.y, 0f, 0f)
                                       : new Vector4(hr.fingerSegments.x, hr.fingerSegments.y, hr.fingerSegments.z, 0f);
                float total = split.x + split.y + split.z;
                int parent = arm.end;
                Vector3 at = pos + pivot;
                for (int sgi = 0; sgi < 3; sgi++)
                {
                    if (split[sgi] <= 0f) break;
                    float sl = len * split[sgi] / total;
                    var d = new BoneSpec { name = names[k] + (sgi + 1) + S, kind = BoneKind.Finger, parent = parent, side = hb.side, limb = hb.limb,
                                           localPos = at, shapeOffset = -pivot, dir = dir, length = sl, girth = Vector3.one * rad, location = BodyPart.Location.Hand };
                    parent = plan.Add(d);
                    arm.digitBones.Add(parent);
                    if (sgi == 0) arm.digits[k] = parent;
                    at = dir * sl;
                }
            }
        }

        var hd = plan.bones[headB];
        plan.sockets.Add(new Socket { name = "head.eyes", bone = headB, localPos = new Vector3(0f, head * 0.45f, hd.Outer.y * 0.8f) });
        plan.sockets.Add(new Socket { name = "hip.R.holster", bone = hips, localPos = new Vector3(plan.bones[hips].Outer.y, -0.02f * H, 0f) });
        plan.sockets.Add(new Socket { name = "back", bone = chestB, localPos = new Vector3(0f, chest * 0.6f, -chestSpec.Outer.y) });
        if (dna.breastSize > 0f)
            foreach (int side in new[] { -1, 1 })
                plan.sockets.Add(new Socket { name = side < 0 ? "breast.L" : "breast.R", bone = chestB,
                                              localPos = new Vector3(side * chestSpec.Outer.y * 0.45f, chest * 0.45f - dna.breastDroop, chestSpec.Outer.y * 0.8f) });
        plan.height = hipY + lumbar + chest + neck + head;
        return plan;
    }

    // Where the arms go: pairs (left, right) per girdle from the shoulders down; an odd arm on one
    // side or centred, seed-chosen.
    static List<(int side, int girdle)> ArmSlots(int count, int seed, string salt)
    {
        var slots = new List<(int, int)>();
        int pairs = count / 2;
        for (int g = 0; g < pairs; g++) { slots.Add((-1, g)); slots.Add((1, g)); }
        if (count % 2 == 1)
        {
            float r = new PcgRandom(seed, "oddArm" + salt).Value();
            slots.Add((r < 0.4f ? 0 : r < 0.7f ? -1 : 1, pairs));
        }
        return slots;
    }
}
