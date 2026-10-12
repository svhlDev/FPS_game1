using System.Collections.Generic;
using UnityEngine;

// Clothing Part B / Phase 8c-d: the spring chains of a character's skirts (coat tails), simulated after
// the pose is set. Per joint (Verlet: position + previous position):
//   inertia (damped, frame-rate independent), gravity, air drag (wind minus the character's own motion:
//   running trails the tails, falling and gliding flare them up), a pull back toward the animated rest
//   (stiff at the ring, loose at the hem), segment lengths kept, the swing from rest limited (maxAngle),
//   neighbouring panels held together (not across the front opening), and pushed out of the legs and
//   pelvis (capsules from the skeleton, radius = girth + a margin).
// Within simDistance of the camera and on screen only; otherwise the chains ease back to rest. A jump of
// more than 2 m in a frame (teleport, pool reuse) resets them.
[DefaultExecutionOrder(500)]   // after FigureAnimator and anything else that poses the body
public class ClothDangle : MonoBehaviour
{
    public static Vector3 Wind = new Vector3(0.6f, 0f, 0.3f);
    public static int Simulated { get; private set; }   // chains simulated this frame (all characters)
    static int simFrame;

    class Set
    {
        public ClothChainSet def;
        public Transform[] bones;      // [panel * bones + b]
        public Vector3[] restLocal;    // joints in the pelvis' space: [panel * (bones + 1) + level]
        public Vector3[] x, xp;        // simulated joints (world)
        public float[] seg;            // rest segment lengths [panel * bones + b]
        public Vector3[] rest;         // this frame's rest joints (world), cached
        public Transform anchor;       // the joint the ring follows (pelvis, or the head for long hair)
    }
    // This frame's capsule ends (cached: transform reads are the expensive part).
    Vector3[] capA, capB; float[] capR;
    readonly List<Set> sets = new List<Set>();
    CharacterFigure fig;
    Transform pelvis;
    Renderer visCheck;
    Vector3 lastRoot; bool init;
    float simBlend;   // 1 simulating, 0 at rest

    // Leg / pelvis capsules (joint transforms + radius).
    readonly List<(Transform a, Transform b, float r)> capsules = new List<(Transform, Transform, float)>();

    public IReadOnlyList<Transform> BonesOf(int set) => sets[set].bones;

    // Creates the chain bones under the pelvis joint; returns them in renderer order (after the plan's).
    public static List<Transform> Attach(CharacterFigure f, BodyAsset a)
    {
        var list = new List<Transform>();
        if (a.chains.Count == 0) return list;
        var d = f.gameObject.AddComponent<ClothDangle>();
        d.fig = f;
        d.pelvis = f.Hips;
        foreach (var cs in a.chains)
        {
            bool head = cs.anchor == "Head" && f.HeadJoint != null;
            var anchor = head ? f.HeadJoint : f.Hips;
            Vector3 anchorRest = a.plan.JointPos(a.plan.Index(head ? "Head" : "Hips"));
            var s = new Set { def = cs, anchor = anchor, bones = new Transform[cs.BoneCount], restLocal = new Vector3[cs.joints.Length], x = new Vector3[cs.joints.Length], xp = new Vector3[cs.joints.Length], seg = new float[cs.BoneCount] };
            for (int i = 0; i < cs.joints.Length; i++) s.restLocal[i] = cs.joints[i] - anchorRest;
            for (int p = 0; p < cs.panels; p++)
                for (int b = 0; b < cs.bones; b++)
                {
                    var t = new GameObject($"{(head ? "Hair" : "Coat")}{p}_{b}").transform;
                    t.SetParent(anchor, false);
                    t.localPosition = s.restLocal[p * (cs.bones + 1) + b];
                    t.gameObject.layer = f.gameObject.layer;
                    s.bones[p * cs.bones + b] = t;
                    s.seg[p * cs.bones + b] = (cs.Joint(p, b + 1) - cs.Joint(p, b)).magnitude;
                    list.Add(t);
                }
            d.sets.Add(s);
        }
        float margin = 0.02f + (a.outfit != null && a.chains[0].garment < a.outfit.Count ? a.outfit.garments[a.chains[0].garment].thickness : 0.015f);
        float R(string bone) { int i = a.plan.Index(bone); var b = a.plan.bones[i]; return Mathf.Max(b.Outer.x, Mathf.Max(b.Outer.y, b.Outer.z)) + margin; }
        d.capsules.Add((f.HipL, f.KneeL, R("HipL"))); d.capsules.Add((f.HipR, f.KneeR, R("HipR")));
        d.capsules.Add((f.KneeL, f.AnkleL, R("KneeL"))); d.capsules.Add((f.KneeR, f.AnkleR, R("KneeR")));
        d.capsules.Add((f.Hips, f.Spine, R("Hips")));
        // Long hair (Face Fix Part 9): the head, the neck and the shoulders.
        bool hair = false; foreach (var cs in a.chains) if (cs.anchor == "Head") hair = true;
        if (hair && f.HeadJoint != null && f.Neck != null && f.Head != null)
        {
            float RB(string bone, float m) { int i = a.plan.Index(bone); var b = a.plan.bones[i]; return Mathf.Max(b.Outer.x, Mathf.Max(b.Outer.y, b.Outer.z)) + m; }
            d.capsules.Add((f.Neck, f.HeadJoint, RB("Neck", 0.008f)));
            d.capsules.Add((f.HeadJoint, f.Head, RB("Head", 0.004f)));
            if (f.ShoulderL != null && f.ElbowL != null) d.capsules.Add((f.ShoulderL, f.ElbowL, RB("ShoulderL", 0.01f)));
            if (f.ShoulderR != null && f.ElbowR != null) d.capsules.Add((f.ShoulderR, f.ElbowR, RB("ShoulderR", 0.01f)));
            if (f.Chest != null) d.capsules.Add((f.Chest, f.Neck, RB("Chest", 0.01f) * 0.8f));
        }
        return list;
    }

    public void ResetToRest() => init = false;

    Vector3 Rest(Set s, int i) => s.rest[i];

    void CacheFrame()
    {
        foreach (var s in sets)
        {
            var m = s.anchor.localToWorldMatrix;
            if (s.rest == null || s.rest.Length != s.restLocal.Length) s.rest = new Vector3[s.restLocal.Length];
            for (int i = 0; i < s.rest.Length; i++) s.rest[i] = m.MultiplyPoint3x4(s.restLocal[i]);
        }
        if (capA == null || capA.Length != capsules.Count) { capA = new Vector3[capsules.Count]; capB = new Vector3[capsules.Count]; capR = new float[capsules.Count]; }
        for (int k = 0; k < capsules.Count; k++) { capA[k] = capsules[k].a.position; capB[k] = capsules[k].b.position; capR[k] = capsules[k].r; }
    }

    void LateUpdate()
    {
        if (pelvis == null || sets.Count == 0) return;
        if (visCheck == null && fig != null && fig.Renderers.Count > 0) visCheck = fig.Renderers[0];
        float dt = Mathf.Min(Time.deltaTime, 1f / 30f);
        Vector3 root = transform.position;
        Vector3 vel = init && dt > 0f ? (root - lastRoot) / Mathf.Max(Time.deltaTime, 1e-4f) : Vector3.zero;
        if (!init || (root - lastRoot).sqrMagnitude > 4f) { ResetState(); init = true; }
        lastRoot = root;
        CacheFrame();

        // LOD: simulate near and on screen, else ease back to rest.
        var cam = Camera.main;
        float sim = sets[0].def.def.simDistance;
        bool active = cam != null && (cam.transform.position - root).sqrMagnitude < sim * sim && (visCheck == null || visCheck.isVisible);
        simBlend = Mathf.MoveTowards(simBlend, active ? 1f : 0f, dt * 2f);
        if (simFrame != Time.frameCount) { simFrame = Time.frameCount; Simulated = 0; }

        foreach (var s in sets)
        {
            if (active && dt > 0f) { Step(s, dt * 0.5f, vel); Step(s, dt * 0.5f, vel); Simulated++; }
            else if (simBlend <= 0f) for (int i = 0; i < s.x.Length; i++) { s.x[i] = Rest(s, i); s.xp[i] = s.x[i]; }   // resting: start from here when it wakes
            Apply(s);
        }
    }

    void ResetState()
    {
        CacheFrame();
        foreach (var s in sets)
            for (int i = 0; i < s.x.Length; i++) { s.x[i] = Rest(s, i); s.xp[i] = s.x[i]; }
        simBlend = 0f;
    }

    void Step(Set s, float dt, Vector3 charVel)
    {
        var c = s.def; var d = c.def;
        int L = c.bones + 1;
        float keep = Mathf.Pow(d.damping, dt * 60f);
        Vector3 g = Physics.gravity * d.gravityScale;
        float t = Time.time;
        for (int p = 0; p < c.panels; p++)
        {
            // The ring follows the pelvis.
            int r0 = p * L;
            s.x[r0] = Rest(s, r0); s.xp[r0] = s.x[r0];
            for (int l = 1; l < L; l++)
            {
                int i = r0 + l;
                Vector3 v = (s.x[i] - s.xp[i]) * keep;
                s.xp[i] = s.x[i];
                // Air: wind minus our motion, with a little per-panel flutter.
                Vector3 air = Wind - charVel + new Vector3(Mathf.Sin(t * 7.3f + p * 1.7f), Mathf.Sin(t * 5.1f + p * 2.3f) * 0.5f, Mathf.Cos(t * 6.1f + p)) * 0.6f;
                Vector3 acc = g + d.drag * (air - v / dt);
                s.x[i] += v + acc * dt * dt;
                float k = Mathf.Lerp(d.stiffnessRoot, d.stiffnessTip, (l - 1f) / Mathf.Max(1, L - 2));
                s.x[i] = Vector3.Lerp(s.x[i], Rest(s, i), 1f - Mathf.Exp(-k * dt));
            }
        }
        for (int it = 0; it < 3; it++)
        {
            // Neighbouring panels at the same level keep their rest spacing (the cloth doesn't tear apart);
            // not across the front opening.
            for (int p = 0; p < c.panels; p++)
            {
                int q = (p + 1) % c.panels;
                if (Crosses(c, c.angles[p], c.angles[q], 0f)) continue;
                for (int l = 1; l < L; l++)
                {
                    int i = p * L + l, j = q * L + l;
                    Vector3 restOff = Rest(s, j) - Rest(s, i);
                    Vector3 err = (s.x[j] - s.x[i]) - restOff;
                    s.x[i] += err * d.neighbour * 0.5f; s.x[j] -= err * d.neighbour * 0.5f;
                }
            }
            for (int p = 0; p < c.panels; p++)
                for (int l = 1; l < L; l++)
                {
                    int i = p * L + l;
                    // Length, and the swing from the rest direction limited.
                    Vector3 parent = s.x[i - 1];
                    Vector3 restDir = (Rest(s, i) - Rest(s, i - 1)).normalized;
                    Vector3 dir = s.x[i] - parent;
                    if (dir.sqrMagnitude < 1e-8f) dir = restDir;
                    dir.Normalize();
                    float ang = Vector3.Angle(restDir, dir);
                    if (ang > d.maxAngle) dir = Vector3.Slerp(restDir, dir, d.maxAngle / ang);
                    s.x[i] = parent + dir * s.seg[p * c.bones + l - 1];
                    // Out of the legs and pelvis (last: nothing after it may pull the joint back in).
                    s.x[i] = PushOutAll(s.x[i]);
                }
        }
    }

    static bool Crosses(ClothChainSet c, float a, float b, float gap)
    {
        float ab = Mathf.DeltaAngle(a, b), ag = Mathf.DeltaAngle(a, gap);
        return ab > 0f ? ag > 0f && ag < ab : ag < 0f && ag > ab;
    }

    // Out of every capsule: where they overlap (a bent knee) leaving one can mean entering the other,
    // so a few passes until none moves the point.
    Vector3 PushOutAll(Vector3 p)
    {
        for (int pass = 0; pass < 4; pass++)
        {
            bool moved = false;
            for (int k = 0; k < capA.Length; k++)
            {
                Vector3 q = PushOut(p, capA[k], capB[k], capR[k]);
                if ((q - p).sqrMagnitude > 1e-10f) { p = q; moved = true; }
            }
            if (!moved) break;
        }
        return p;
    }

    static Vector3 PushOut(Vector3 p, Vector3 a, Vector3 b, float r)
    {
        Vector3 ab = b - a;
        float h = Mathf.Clamp01(Vector3.Dot(p - a, ab) / Mathf.Max(1e-6f, ab.sqrMagnitude));
        Vector3 c = a + ab * h, d = p - c;
        float m = d.magnitude;
        return m < r ? c + (m > 1e-5f ? d / m : Vector3.forward) * r : p;
    }

    // Bones from the joints (blended with rest when not simulating).
    void Apply(Set s)
    {
        var c = s.def;
        int L = c.bones + 1;
        for (int p = 0; p < c.panels; p++)
            for (int b = 0; b < c.bones; b++)
            {
                int i = p * L + b;
                Vector3 r0 = Rest(s, i), r1 = Rest(s, i + 1);
                Vector3 a = Vector3.Lerp(r0, s.x[i], simBlend), e = Vector3.Lerp(r1, s.x[i + 1], simBlend);
                var t = s.bones[p * c.bones + b];
                t.position = a;
                t.rotation = Quaternion.FromToRotation(r1 - r0, e - a) * s.anchor.rotation;
            }
    }

    // Test hooks.
    public Vector3 DebugJoint(int set, int panel, int level) => sets[set].x[panel * (sets[set].def.bones + 1) + level];
    public Vector3 DebugRest(int set, int panel, int level) => Rest(sets[set], panel * (sets[set].def.bones + 1) + level);
    public int DebugInsideLegs(int set) => DebugInsideLegs(set, out _);
    public int DebugInsideLegs(int set, out string where)
    {
        int n = 0; var s = sets[set]; where = "";
        for (int i = 0; i < s.x.Length; i++)
            if (i % (s.def.bones + 1) != 0)
                for (int k = 0; k < capsules.Count; k++)
                {
                    var (a, b, r) = capsules[k];
                    if ((PushOut(s.x[i], a.position, b.position, r * 0.8f) - s.x[i]).sqrMagnitude > 1e-8f)
                    { n++; where += $"panel {i / (s.def.bones + 1)} level {i % (s.def.bones + 1)} in {a.name}-{b.name}; "; break; }
                }
        return n;
    }
}
