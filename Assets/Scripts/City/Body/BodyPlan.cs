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
//             a girth profile (radius at start, middle, end).
//   Sockets : named attach points on bones (grips, eyes, holster) for gear, clothing and cyberware later.
// Joint rotations rest at identity in root space, exactly like CharacterFigure, so FigureAnimator's
// convention holds: limbs hang along -Y, a negative X rotation swings them forward.
// Species hook: ISpeciesTemplate (body plan rules + trait distributions); only HumanTemplate exists.
public enum BoneKind { Pelvis, Lumbar, Chest, Neck, Head, UpperArm, Forearm, Hand, Thigh, Shin, Foot }
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
    public Vector3 girth;            // radius at start, middle, end
    public BodyPart.Location location;
}

[Serializable]
public class LimbSpec
{
    public int side;                 // -1 / 0 / +1
    public int girdle;               // 0 = shoulders / hips, 1+ = extra girdles
    public int root, mid, end, tip;  // bone indices: upper, lower, hand/foot (tip = end bone)
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
            float m = b.girth.y * b.girth.y * b.length;
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
        string salt = attempt == 0 ? "" : "#" + attempt;
        var plan = new BodyPlan { sheet = sheet, species = Name, spineSegments = 5, legType = LegType.Plantigrade };
        float H = t.height * (1f + rules.heightNoise * new PcgRandom(sheet.seed, "height" + salt).Signed());

        // Per-bone noise: a shared draw per bone kind plus a smaller per-side one (left and right differ
        // a little, never a lot).
        float Len(string key, float frac, int side = 0)
        {
            float n = new PcgRandom(sheet.seed, "len." + key + salt).Signed();
            if (side != 0) n += 0.3f * new PcgRandom(sheet.seed, "len." + key + side + salt).Signed();
            return frac * H * (1f + rules.lengthNoise * n);
        }
        Vector3 Girth(string key, Vector3 g, float scale, int side = 0)
        {
            float n = new PcgRandom(sheet.seed, "girth." + key + salt).Signed();
            if (side != 0) n += 0.3f * new PcgRandom(sheet.seed, "girth." + key + side + salt).Signed();
            return g * H * scale * (1f + rules.girthNoise * n);
        }
        float hg = rules.houseGirth, ex = rules.extremityScale;
        Vector3 Taper(Vector3 g) => new Vector3(g.x, g.y, g.y * rules.extremityTaper);

        // ---------- spine ----------
        float ankleH = Len("ankle", t.ankleHeight), shin = Len("shin", t.shin), thigh = Len("thigh", t.thigh);
        float hipY = ankleH + shin + thigh;
        float lumbar = Len("lumbar", t.lumbar), chest = Len("chest", t.chest), neck = Len("neck", t.neck), head = Len("head", t.head);
        int hips = plan.Add(new BoneSpec { name = "Hips", kind = BoneKind.Pelvis, localPos = new Vector3(0f, hipY, 0f), dir = Vector3.down,
                                           length = Len("pelvis", t.pelvis), girth = Girth("pelvis", t.pelvisGirth, hg), location = BodyPart.Location.Body });
        int spine = plan.Add(new BoneSpec { name = "Spine", kind = BoneKind.Lumbar, parent = hips, dir = Vector3.up, length = lumbar,
                                            girth = Girth("lumbar", t.lumbarGirth, hg), location = BodyPart.Location.Body });
        int chestB = plan.Add(new BoneSpec { name = "Chest", kind = BoneKind.Chest, parent = spine, localPos = new Vector3(0f, lumbar, 0f), dir = Vector3.up,
                                             length = chest, girth = Girth("chest", t.chestGirth, hg), location = BodyPart.Location.Body });
        int neckB = plan.Add(new BoneSpec { name = "Neck", kind = BoneKind.Neck, parent = chestB, localPos = new Vector3(0f, chest, t.neckForward * H), dir = Vector3.up,
                                            length = neck, girth = Girth("neck", t.neckGirth, hg), location = BodyPart.Location.Head });
        int headB = plan.Add(new BoneSpec { name = "Head", kind = BoneKind.Head, parent = neckB, localPos = new Vector3(0f, neck, 0f), dir = Vector3.up,
                                            length = head, girth = Girth("head", t.headGirth, 1f), location = BodyPart.Location.Head });

        // ---------- legs ----------
        float footLen = Len("foot", t.foot) * ex;
        foreach (int side in new[] { -1, 1 })
        {
            string S = side < 0 ? "L" : "R";
            var leg = new LimbSpec { side = side };
            leg.root = plan.Add(new BoneSpec { name = "Hip" + S, kind = BoneKind.Thigh, parent = hips, side = side, limb = plan.legs.Count,
                                               localPos = new Vector3(side * t.hipHalfWidth * H, 0f, 0f), dir = Vector3.down, length = thigh,
                                               girth = Girth("thigh", t.thighGirth, hg, side), location = BodyPart.Location.Leg });
            leg.mid = plan.Add(new BoneSpec { name = "Knee" + S, kind = BoneKind.Shin, parent = leg.root, side = side, limb = plan.legs.Count,
                                              localPos = new Vector3(0f, -thigh, 0f), dir = Vector3.down, length = shin,
                                              girth = Taper(Girth("shin", t.shinGirth, hg, side)), location = BodyPart.Location.Leg });
            // Foot: from the ankle forward and down to the toes on the ground (heel behind, via girth).
            Vector3 toe = new Vector3(0f, -ankleH, footLen * 0.72f);
            leg.end = leg.tip = plan.Add(new BoneSpec { name = "Ankle" + S, kind = BoneKind.Foot, parent = leg.mid, side = side, limb = plan.legs.Count,
                                                        localPos = new Vector3(0f, -shin, 0f), dir = toe.normalized, length = toe.magnitude,
                                                        girth = Girth("foot", t.footGirth, ex, side), location = BodyPart.Location.Foot });
            plan.legs.Add(leg);
        }

        // ---------- arms ----------
        float upper = Len("upperArm", t.upperArm), fore = Len("forearm", t.forearm), hand = Len("hand", t.hand) * ex;
        var slots = ArmSlots(armCount, sheet.seed, salt);
        foreach (var (side, girdle) in slots)
        {
            string S = (side < 0 ? "L" : side > 0 ? "R" : "C") + (girdle > 0 ? (girdle + 1).ToString() : "");
            float y = chest - t.shoulderDrop * H - girdle * rules.extraGirdleStep * chest;
            float x = side * t.shoulderHalfWidth * H * (girdle > 0 ? 0.9f : 1f);
            float z = side == 0 ? t.chestGirth.y * H * hg : 0f; // a centred arm sits on the chest's front
            var arm = new LimbSpec { side = side, girdle = girdle };
            int li = plan.arms.Count;
            arm.root = plan.Add(new BoneSpec { name = "Shoulder" + S, kind = BoneKind.UpperArm, parent = chestB, side = side, limb = li,
                                               localPos = new Vector3(x, y, z), dir = Vector3.down, length = upper,
                                               girth = Girth("upperArm", t.upperArmGirth, hg, side), location = BodyPart.Location.Arm });
            arm.mid = plan.Add(new BoneSpec { name = "Elbow" + S, kind = BoneKind.Forearm, parent = arm.root, side = side, limb = li,
                                              localPos = new Vector3(0f, -upper, 0f), dir = Vector3.down, length = fore,
                                              girth = Taper(Girth("forearm", t.forearmGirth, hg, side)), location = BodyPart.Location.Arm });
            arm.end = arm.tip = plan.Add(new BoneSpec { name = "Wrist" + S, kind = BoneKind.Hand, parent = arm.mid, side = side, limb = li,
                                                        localPos = new Vector3(0f, -fore, 0f), dir = Vector3.down, length = hand,
                                                        girth = Girth("hand", t.handGirth, ex, side), location = BodyPart.Location.Hand });
            plan.arms.Add(arm);
            plan.sockets.Add(new Socket { name = "hand." + S + ".grip", bone = arm.end, localPos = Vector3.down * hand * 0.5f });
        }

        plan.sockets.Add(new Socket { name = "head.eyes", bone = headB, localPos = new Vector3(0f, head * 0.45f, t.headGirth.y * H * 0.8f) });
        plan.sockets.Add(new Socket { name = "hip.R.holster", bone = hips, localPos = new Vector3(t.pelvisGirth.y * H * hg, -0.02f * H, 0f) });
        plan.sockets.Add(new Socket { name = "back", bone = chestB, localPos = new Vector3(0f, chest * 0.6f, -t.chestGirth.y * H * hg) });
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
