using System.Collections.Generic;
using UnityEngine;

// A dead figure as physics. FromFigure lifts the figure's skeleton and renderers off its owner onto a
// new "Corpse" object (the owner, with its CharacterController, FigureAnimator and AI, is then removed
// or pooled) and makes each part a rigidbody:
//   hips, body, head, upper arms, forearms, hands, thighs, shins, feet; mass by share of ~60 kg (scaled
//   by height), solid colliders (the existing BodyPart shapes, no longer triggers; generated ones when
//   the figure had none), CharacterJoints: neck 40 deg, shoulders 90, elbows 0..140 one way, hips 70,
//   knees 0..140 one way, ankles 30 (spine 30, wrists 40). Joints are set up in the rest pose, then the
//   current pose is put back, so the limits are anatomical whatever the figure was doing.
//   Every part keeps the figure's velocity; the killing hit pushes (DamageInfo.impulse on the hit part,
//   velocityChange on every part).
// Bodies sleep when still, collide with the world and cars (not with themselves), ride a car they lie
// on (CarCarry) and despawn once older than 5 minutes and out of view for 2 s. At most 30: a new one
// removes the oldest out of view (or the oldest).
public class Ragdoll : MonoBehaviour
{
    public const int MaxRagdolls = 30;
    public const float TotalMass = 60f;

    static readonly List<Ragdoll> all = new List<Ragdoll>();
    public static IReadOnlyList<Ragdoll> All => all;

    public CharacterFigure.Role Role { get; private set; }
    public readonly List<Rigidbody> Bodies = new List<Rigidbody>();
    public Rigidbody HipsBody { get; private set; }
    public Rigidbody HeadBody { get; private set; }
    public float Born { get; private set; }
    readonly List<Renderer> renderers = new List<Renderer>();
    readonly OffscreenTimer offscreen = new OffscreenTimer();
    readonly CarCarry carry = new CarCarry();

    enum Lim { Spine, Neck, Shoulder, Elbow, Wrist, Hip, Knee, Ankle }
    struct PartSpec { public Transform t; public int parent; public float mass; public Lim lim; public float radius; public Transform end; }

    public static Ragdoll FromFigure(CharacterFigure fig, Vector3 velocity, DamageInfo kill)
    {
        if (fig == null || fig.Hips == null) return null;
        var sk = fig.GetComponent<BodySkeleton>();
        float s = fig.Scale;

        var corpse = new GameObject("Corpse");
        corpse.layer = fig.gameObject.layer;
        corpse.transform.SetPositionAndRotation(fig.transform.position, fig.transform.rotation);
        var rd = corpse.AddComponent<Ragdoll>();
        rd.Role = fig.GetComponent<CharacterHealth>() != null ? fig.GetComponent<CharacterHealth>().Role : CharacterFigure.Role.Civilian;
        rd.Born = Time.time;

        Transform head = fig.HeadJoint != null ? fig.HeadJoint : fig.Neck;
        Transform hips = fig.Hips, headCentre = fig.Head;
        Vector3 fwd = fig.transform.forward;
        // Parts: transform, parent index, mass share, limits, limb radius (generated colliders), limb end.
        var parts = new List<PartSpec>
        {
            new PartSpec { t = fig.Hips, parent = -1, mass = 0.15f, lim = Lim.Spine, radius = 0.12f },
            new PartSpec { t = fig.Spine, parent = 0, mass = 0.26f, lim = Lim.Spine, radius = 0.14f, end = fig.Neck },
            new PartSpec { t = head, parent = 1, mass = 0.08f, lim = Lim.Neck, radius = 0.11f },
        };
        void Limb(Transform a, Transform b, Transform c, Transform end, bool arm)
        {
            if (a == null || b == null || c == null) return;
            int i = parts.Count;
            parts.Add(new PartSpec { t = a, parent = arm ? 1 : 0, mass = arm ? 0.04f : 0.11f, lim = arm ? Lim.Shoulder : Lim.Hip, radius = arm ? 0.045f : 0.065f, end = b });
            parts.Add(new PartSpec { t = b, parent = i, mass = arm ? 0.025f : 0.055f, lim = arm ? Lim.Elbow : Lim.Knee, radius = arm ? 0.04f : 0.05f, end = c });
            parts.Add(new PartSpec { t = c, parent = i + 1, mass = arm ? 0.01f : 0.016f, lim = arm ? Lim.Wrist : Lim.Ankle, radius = arm ? 0.045f : 0.05f, end = end });
        }
        Limb(fig.ShoulderL, fig.ElbowL, fig.WristL, fig.HandL, true);
        Limb(fig.ShoulderR, fig.ElbowR, fig.WristR, fig.HandR, true);
        Limb(fig.HipL, fig.KneeL, fig.AnkleL, null, false);
        Limb(fig.HipR, fig.KneeR, fig.AnkleR, null, false);

        // The rest pose (generated bones keep their rest rotation), the current pose to restore.
        var restOf = new Dictionary<Transform, Quaternion>();
        if (sk != null) foreach (var b in sk.Bones) restOf[b.joint] = b.spec.restRotation;
        var posed = new List<(Transform, Quaternion)>();
        foreach (Transform t in fig.Hips.GetComponentsInChildren<Transform>(true))
        {
            if (t == fig.Hips) continue;
            posed.Add((t, t.localRotation));
        }
        Quaternion Rest(Transform t) => restOf.TryGetValue(t, out var q) ? q : Quaternion.identity;
        var restJoints = new List<Transform> { fig.Spine, fig.Chest, fig.Neck, fig.HeadJoint, fig.ShoulderL, fig.ShoulderR, fig.ElbowL, fig.ElbowR,
                                               fig.WristL, fig.WristR, fig.HipL, fig.HipR, fig.KneeL, fig.KneeR, fig.AnkleL, fig.AnkleR };

        // Take the body: skeleton and the root-level (skinned) renderers.
        var rends = new List<Renderer>(fig.Renderers);
        rends.AddRange(fig.FaceRenderers);
        fig.Hips.SetParent(corpse.transform, true);
        foreach (var r in rends)
            if (r != null && r.transform.parent == fig.transform) r.transform.SetParent(corpse.transform, true);
        foreach (var r in rends) if (r != null && !rd.renderers.Contains(r)) rd.renderers.Add(r);
        fig.Release();

        foreach (var t in restJoints) if (t != null) t.localRotation = Rest(t);

        // Colliders: the figure's hit shapes made solid, or simple ones per part.
        var cols = new List<Collider>();
        foreach (var c in corpse.GetComponentsInChildren<Collider>(true))
        {
            if (c.GetComponent<BodyPart>() == null) { Object.Destroy(c); continue; }
            c.isTrigger = false;
            cols.Add(c);
        }
        if (cols.Count == 0) foreach (var p in parts) cols.Add(MakeCollider(p, hips, headCentre, fwd, s));
        else if (!HasOwnCollider(hips, parts)) cols.Add(MakeCollider(parts[0], hips, headCentre, fwd, s));

        // Bodies and joints.
        float mScale = TotalMass * Mathf.Pow(s, 2.2f);
        var bodies = new Rigidbody[parts.Count];
        for (int i = 0; i < parts.Count; i++)
        {
            var p = parts[i];
            var rb = p.t.GetComponent<Rigidbody>() ?? p.t.gameObject.AddComponent<Rigidbody>();
            rb.isKinematic = false;
            rb.useGravity = true;
            rb.mass = Mathf.Max(0.3f, p.mass * mScale);
            rb.linearDamping = 0.05f;
            rb.angularDamping = 0.6f;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            rb.solverIterations = 12;
            rb.solverVelocityIterations = 4;
            rb.maxDepenetrationVelocity = 3f;
            rb.sleepThreshold = 0.05f;
            bodies[i] = rb;
            rd.Bodies.Add(rb);
        }
        for (int i = 1; i < parts.Count; i++)
        {
            var p = parts[i];
            var j = p.t.gameObject.AddComponent<CharacterJoint>();
            j.connectedBody = bodies[p.parent];
            j.enablePreprocessing = false;
            j.enableProjection = true;
            j.projectionDistance = 0.05f;
            j.projectionAngle = 10f;
            Limits(j, p.lim);
        }
        rd.HipsBody = bodies[0];
        rd.HeadBody = bodies[2];
        for (int a = 0; a < cols.Count; a++)
            for (int b = a + 1; b < cols.Count; b++)
                if (cols[a] != null && cols[b] != null) Physics.IgnoreCollision(cols[a], cols[b]);

        // Back to the pose it died in, moving as it was; the killing hit pushes.
        foreach (var (t, q) in posed) if (t != null) t.localRotation = q;
        Physics.SyncTransforms();
        foreach (var rb in bodies) rb.linearVelocity = velocity + kill.velocityChange * 0.8f;
        if (kill.impulse.sqrMagnitude > 0f || kill.velocityChange.sqrMagnitude > 0f)
        {
            var hit = kill.part != null ? kill.part.attachedRigidbody : null;
            if (hit == null || !rd.Bodies.Contains(hit)) hit = Nearest(rd.Bodies, kill.point);
            if (hit != null)
            {
                if (kill.impulse.sqrMagnitude > 0f) hit.AddForceAtPosition(kill.impulse, kill.point, ForceMode.Impulse);
                if (kill.velocityChange.sqrMagnitude > 0f) hit.AddForce(kill.velocityChange * 0.25f, ForceMode.VelocityChange);
            }
        }

        // Cap: the oldest out of view goes (or the oldest).
        if (all.Count >= MaxRagdolls)
        {
            Ragdoll victim = null;
            foreach (var r in all) if (r != null && !r.offscreen.VisibleNow(r.renderers) && (victim == null || r.Born < victim.Born)) victim = r;
            if (victim == null) foreach (var r in all) if (r != null && (victim == null || r.Born < victim.Born)) victim = r;
            if (victim != null) victim.Remove();
        }
        all.Add(rd);
        Created++;
        return rd;
    }

    public static int Created;

    static bool HasOwnCollider(Transform hips, List<PartSpec> parts)
    {
        foreach (var c in hips.GetComponentsInChildren<Collider>(true))
        {
            // A collider belongs to the nearest part above it; the hips own the ones not under another part.
            Transform t = c.transform;
            while (t != null && t != hips)
            {
                bool other = false;
                foreach (var p in parts) if (p.t == t) { other = true; break; }
                if (other) break;
                t = t.parent;
            }
            if (t == hips) return true;
        }
        return false;
    }

    static Collider MakeCollider(PartSpec p, Transform hips, Transform headCentre, Vector3 fwd, float s)
    {
        var go = new GameObject("RagCol");
        go.layer = p.t.gameObject.layer;
        go.transform.SetParent(p.t, false);
        if (p.t == hips)
        {
            var b = go.AddComponent<BoxCollider>();
            b.size = new Vector3(0.28f, 0.16f, 0.2f) * s;
            return b;
        }
        if (p.lim == Lim.Neck)
        {
            var sc = go.AddComponent<SphereCollider>();
            sc.center = p.t.InverseTransformPoint(headCentre != null ? headCentre.position : p.t.position + Vector3.up * 0.1f * s);
            sc.radius = 0.11f * s;
            return sc;
        }
        Vector3 end = p.end != null ? p.end.position
                    : p.lim == Lim.Ankle ? p.t.position + fwd * 0.14f * s - Vector3.up * 0.04f * s
                    : p.t.position - p.t.up * 0.08f * s;
        Vector3 localEnd = p.t.InverseTransformPoint(end);
        if (p.lim == Lim.Wrist) localEnd *= 2f; // HandL/R mark the middle of the hand
        if (p.lim == Lim.Spine || p.lim == Lim.Ankle || p.lim == Lim.Wrist)
        {
            var b = go.AddComponent<BoxCollider>();
            float len = Mathf.Max(0.06f * s, localEnd.magnitude);
            go.transform.localRotation = Quaternion.FromToRotation(Vector3.up, localEnd.sqrMagnitude > 1e-6f ? localEnd.normalized : Vector3.down);
            b.center = Vector3.up * len * 0.5f;
            b.size = p.lim == Lim.Spine ? new Vector3(0.3f * s, len, 0.2f * s) : new Vector3(p.radius * 2f, len, p.radius * 1.5f);
            return b;
        }
        var cap = go.AddComponent<CapsuleCollider>();
        float l = localEnd.magnitude;
        go.transform.localRotation = Quaternion.FromToRotation(Vector3.up, l > 1e-4f ? localEnd / l : Vector3.down);
        cap.direction = 1;
        cap.radius = p.radius * s;
        cap.height = Mathf.Max(l + cap.radius, cap.radius * 2f);
        cap.center = Vector3.up * l * 0.5f;
        return cap;
    }

    // Limits for a joint whose limb hangs along local -Y and bends about local X (FigureAnimator's
    // convention: negative X swings an arm or a thigh forward, elbows bend negative, knees positive).
    static void Limits(CharacterJoint j, Lim lim)
    {
        float lo, hi, s1, s2;
        Vector3 axis = Vector3.right, swing = Vector3.up;
        switch (lim)
        {
            case Lim.Neck: axis = Vector3.up; swing = Vector3.right; lo = -40f; hi = 40f; s1 = 40f; s2 = 40f; break;
            case Lim.Spine: axis = Vector3.up; swing = Vector3.right; lo = -20f; hi = 20f; s1 = 30f; s2 = 20f; break;
            case Lim.Shoulder: axis = Vector3.down; swing = Vector3.right; lo = -40f; hi = 40f; s1 = 90f; s2 = 90f; break;
            case Lim.Hip: axis = Vector3.down; swing = Vector3.right; lo = -25f; hi = 25f; s1 = 70f; s2 = 40f; break;
            case Lim.Elbow: lo = -140f; hi = 0f; s1 = 5f; s2 = 5f; break;   // one way: bends forward (negative X)
            case Lim.Knee: lo = 0f; hi = 140f; s1 = 5f; s2 = 5f; break;     // one way: bends backward (positive X)
            case Lim.Wrist: lo = -40f; hi = 40f; s1 = 30f; s2 = 20f; break;
            default: lo = -30f; hi = 30f; s1 = 20f; s2 = 15f; break;        // ankle
        }
        j.axis = axis;
        j.swingAxis = swing;
        j.lowTwistLimit = new SoftJointLimit { limit = lo };
        j.highTwistLimit = new SoftJointLimit { limit = hi };
        j.swing1Limit = new SoftJointLimit { limit = s1 };
        j.swing2Limit = new SoftJointLimit { limit = s2 };
        var spring = new SoftJointLimitSpring { spring = 0f, damper = 0f };
        j.twistLimitSpring = spring; j.swingLimitSpring = spring;
    }

    static Rigidbody Nearest(List<Rigidbody> bodies, Vector3 p)
    {
        Rigidbody best = null; float bd = float.MaxValue;
        foreach (var b in bodies) { float d = (b.worldCenterOfMass - p).sqrMagnitude; if (d < bd) { bd = d; best = b; } }
        return best;
    }

    // A blast (Explosion) or other push on the whole body.
    public void Push(Vector3 velocityChange)
    {
        foreach (var b in Bodies) if (b != null) { b.WakeUp(); b.AddForce(velocityChange, ForceMode.VelocityChange); }
    }

    public bool Sleeping
    {
        get { foreach (var b in Bodies) if (b != null && !b.IsSleeping()) return false; return true; }
    }

    void FixedUpdate()
    {
        if (HipsBody != null) carry.Step(Bodies, HipsBody.position, 0.45f);
    }

    void Update()
    {
        offscreen.Tick(renderers);
        if (Time.time - Born > OffscreenTimer.Lifetime && offscreen.HiddenFor >= OffscreenTimer.HiddenTime) Remove();
    }

    void OnDestroy() => all.Remove(this);

    public void Remove() { all.Remove(this); Destroy(gameObject); }

    // Test hook.
    public static void ClearAll() { foreach (var r in new List<Ragdoll>(all)) if (r != null) r.Remove(); }
}

// The despawn rule for things left in the world (burn marks, bodies, dropped guns): older than
// Lifetime and not seen by the camera for the last HiddenTime seconds.
public class OffscreenTimer
{
    public const float Lifetime = 300f, HiddenTime = 2f;
    float lastSeen = -1f;
    public float HiddenFor => lastSeen < 0f ? float.MaxValue : Time.time - lastSeen;

    public bool VisibleNow(List<Renderer> rs)
    {
        foreach (var r in rs) if (r != null && r.isVisible) return true;
        return false;
    }

    public void Tick(List<Renderer> rs) { if (lastSeen < 0f || VisibleNow(rs)) lastSeen = Time.time; }
    public void Seen() => lastSeen = Time.time;
}

// Rigidbodies lying on a car ride along with it: the car is teleported each frame (no contact
// velocity), so every fixed step the bodies resting on its roof get the car's own motion since the
// last step (translation plus yaw about the car's pivot).
public class CarCarry
{
    FlyingVehicle car;
    Vector3 lastPos; float lastYaw;
    static readonly RaycastHit[] hits = new RaycastHit[8];
    static int mask = -2;

    public FlyingVehicle Car => car;

    public void Step(IReadOnlyList<Rigidbody> bodies, Vector3 probe, float probeDown)
    {
        if (mask == -2)
        {
            int traffic = LayerMask.NameToLayer("Traffic");
            mask = traffic >= 0 ? 1 << traffic : Physics.DefaultRaycastLayers;
        }
        FlyingVehicle under = null;
        int n = Physics.RaycastNonAlloc(probe + Vector3.up * 0.1f, Vector3.down, hits, probeDown + 0.1f, mask, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < n && under == null; i++) under = hits[i].collider.GetComponentInParent<FlyingVehicle>();
        if (under != car)
        {
            car = under;
            if (car != null) { lastPos = car.PlatformPosition; lastYaw = car.PlatformRotation.eulerAngles.y; }
            return;
        }
        if (car == null) return;
        Vector3 pos = car.PlatformPosition;
        float yaw = car.PlatformRotation.eulerAngles.y;
        Quaternion turn = Quaternion.Euler(0f, Mathf.DeltaAngle(lastYaw, yaw), 0f);
        Vector3 move = pos - lastPos;
        if (move.sqrMagnitude > 1e-8f || Mathf.Abs(Mathf.DeltaAngle(lastYaw, yaw)) > 1e-3f)
        {
            foreach (var b in bodies)
            {
                if (b == null || b.isKinematic) continue;
                b.position = pos + turn * (b.position - lastPos);
                b.rotation = turn * b.rotation;
                b.WakeUp();
            }
        }
        lastPos = pos; lastYaw = yaw;
    }
}
