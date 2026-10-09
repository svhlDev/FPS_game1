using System.Collections.Generic;
using UnityEngine;

// The bone Transform hierarchy built from a BodyPlan. Joints carry CharacterFigure's names (Hips,
// Spine, Neck, Head, ShoulderL/R, ElbowL/R, WristL/R, HipL/R, KneeL/R, AnkleL/R) plus Chest between
// Spine and Neck, so the camera, punches, cuffing and FigureAnimator keep working on any human. Arms
// beyond the first pair are in ExtraArms (never instead of the named ones). Each bone keeps its spec:
// length, rest rotation, direction and girth profile.
// HandL/R are points at the middle of each hand (CharacterFigure's hand transforms).
public class BodySkeleton : MonoBehaviour
{
    public class Bone
    {
        public Transform joint;
        public BoneSpec spec;
        public Vector3 Start => joint.position;
        public Vector3 Dir => (joint.rotation * spec.dir).normalized;
        public Vector3 End => Start + Dir * spec.length * joint.lossyScale.y;
        public Vector3 At(float t) => Start + Dir * spec.length * t * joint.lossyScale.y;
    }

    public BodyPlan Plan { get; private set; }
    public readonly List<Bone> Bones = new List<Bone>();

    public Transform Hips, Spine, Chest, Neck, Head;
    public Transform ShoulderL, ShoulderR, ElbowL, ElbowR, WristL, WristR, HandL, HandR;
    public Transform HipL, HipR, KneeL, KneeR, AnkleL, AnkleR;
    public readonly List<(Transform shoulder, Transform elbow, Transform wrist)> ExtraArms = new List<(Transform, Transform, Transform)>();
    public float GenerationMs { get; private set; }

    public static BodySkeleton Build(Transform root, BodyPlan plan)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var sk = root.gameObject.AddComponent<BodySkeleton>();
        sk.Plan = plan;
        var joints = new Transform[plan.bones.Count];
        for (int i = 0; i < plan.bones.Count; i++)
        {
            var b = plan.bones[i];
            var t = new GameObject(b.name).transform;
            t.SetParent(b.parent >= 0 ? joints[b.parent] : root, false);
            t.localPosition = b.localPos;
            t.localRotation = b.restRotation;
            t.gameObject.layer = root.gameObject.layer;
            joints[i] = t;
            sk.Bones.Add(new Bone { joint = t, spec = b });
        }
        Transform J(string n) { int i = plan.Index(n); return i >= 0 ? joints[i] : null; }
        sk.Hips = J("Hips"); sk.Spine = J("Spine"); sk.Chest = J("Chest"); sk.Neck = J("Neck"); sk.Head = J("Head");
        sk.ShoulderL = J("ShoulderL"); sk.ShoulderR = J("ShoulderR"); sk.ElbowL = J("ElbowL"); sk.ElbowR = J("ElbowR");
        sk.WristL = J("WristL"); sk.WristR = J("WristR");
        sk.HipL = J("HipL"); sk.HipR = J("HipR"); sk.KneeL = J("KneeL"); sk.KneeR = J("KneeR"); sk.AnkleL = J("AnkleL"); sk.AnkleR = J("AnkleR");
        foreach (var arm in plan.arms)
        {
            if (arm.girdle == 0 && arm.side != 0) continue;
            sk.ExtraArms.Add((joints[arm.root], joints[arm.mid], joints[arm.end]));
        }
        foreach (var arm in plan.arms)
        {
            if (arm.girdle != 0 || arm.side == 0) continue;
            var h = new GameObject(arm.side < 0 ? "HandL" : "HandR").transform;
            h.SetParent(joints[arm.end], false);
            h.localPosition = plan.bones[arm.end].dir * plan.bones[arm.end].length * 0.5f;
            if (arm.side < 0) sk.HandL = h; else sk.HandR = h;
        }
        sw.Stop();
        sk.GenerationMs = (float)sw.Elapsed.TotalMilliseconds;
        return sk;
    }

    public Vector3 SocketPosition(string name)
    {
        foreach (var s in Plan.sockets)
            if (s.name == name) return Bones[s.bone].joint.TransformPoint(s.localPos);
        return transform.position;
    }
}
