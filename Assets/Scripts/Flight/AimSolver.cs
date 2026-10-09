using UnityEngine;

// Shared aiming maths (player now, NPCs later with their target as the aim point).
//   AimPoint   : first thing along a ray that isn't the shooter (other people's hit triggers count),
//                or the ray's end.
//   TwoBone    : places an elbow by the law of cosines with a pole hint and returns it.
//   PointBone  : rotates a joint so its limb (hanging along local -Y) points at a position.
//   Spring     : exact critically damped spring step (stable at any frame rate).
public static class AimSolver
{
    public const float MaxRange = 300f;
    static readonly RaycastHit[] hits = new RaycastHit[32];

    public static Vector3 AimPoint(Vector3 origin, Vector3 dir, Transform ignore, float range, out RaycastHit hit, out bool didHit)
    {
        didHit = First(origin, dir, range, ignore, out hit);
        return didHit ? hit.point : origin + dir * range;
    }

    // Nearest hit along the ray, skipping `ignore` (and its children) and non-body triggers.
    public static bool First(Vector3 origin, Vector3 dir, float range, Transform ignore, out RaycastHit best)
    {
        int n = Physics.RaycastNonAlloc(origin, dir, hits, range, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Collide);
        best = default;
        float bd = float.MaxValue;
        for (int i = 0; i < n; i++)
        {
            var c = hits[i].collider;
            if (c == null || (ignore != null && c.transform.IsChildOf(ignore))) continue;
            if (c.isTrigger && c.GetComponent<BodyPart>() == null) continue;
            if (hits[i].distance < bd) { bd = hits[i].distance; best = hits[i]; }
        }
        return bd < float.MaxValue;
    }

    // Two-bone IK: elbow position for shoulder s reaching for target t with bone lengths a, b,
    // bending toward pole. The target is clamped to what the chain can reach.
    public static Vector3 TwoBone(Vector3 s, Vector3 t, float a, float b, Vector3 pole, out Vector3 reached)
    {
        Vector3 d = t - s;
        float dist = Mathf.Clamp(d.magnitude, Mathf.Abs(a - b) + 1e-3f, a + b - 1e-4f);
        Vector3 dir = d.sqrMagnitude > 1e-8f ? d.normalized : Vector3.forward;
        reached = s + dir * dist;
        float cosA = Mathf.Clamp((a * a + dist * dist - b * b) / (2f * a * dist), -1f, 1f);
        Vector3 bend = Vector3.ProjectOnPlane(pole - s, dir);
        if (bend.sqrMagnitude < 1e-8f) bend = Vector3.ProjectOnPlane(Vector3.down, dir);
        bend.Normalize();
        return s + dir * (cosA * a) + bend * (Mathf.Sqrt(1f - cosA * cosA) * a);
    }

    // Joint whose limb hangs along local -Y: point it from its position at `to`, rolled so local X
    // is the bend axis `axis`.
    public static void PointBone(Transform joint, Vector3 to, Vector3 axis)
    {
        Vector3 y = joint.position - to;
        if (y.sqrMagnitude < 1e-8f) return;
        y.Normalize();
        Vector3 z = Vector3.Cross(axis, y);
        if (z.sqrMagnitude < 1e-8f) z = Vector3.Cross(Vector3.right, y);
        joint.rotation = Quaternion.LookRotation(z.normalized, y);
    }

    public static void Spring(ref Vector3 x, ref Vector3 v, Vector3 target, float hz, float dt)
    {
        float w = 2f * Mathf.PI * hz;
        Vector3 delta = x - target;
        float e = Mathf.Exp(-w * dt);
        Vector3 tmp = (v + w * delta) * dt;
        v = (v - w * tmp) * e;
        x = target + (delta + tmp) * e;
    }
}

// One arm holding a one-handed gun on a CharacterFigure (right arm), aimed at a world point. Shared by
// the player (aim point = camera centre ray) and officers (aim point = the suspect's chest + error).
//   Wrist target: from the shoulder toward the aim point at 97% reach; pulled back when the aim point or
//   anything along the arm is close, so the muzzle stays short of it (the elbow bends). Steady: on the
//   eye -> aim line, steadyDrop below it. Followed through a critically damped spring.
//   Two-bone IK for the elbow (pole down and out), then the gun turned in the hand so the barrel ray
//   passes through the aim point (2 iterations), at most maxWristAngle off the forearm, with a little
//   lag of its own. Recoil kicks the barrel up and the hand back; the springs bring it home.
// Call Solve in LateUpdate after the FigureAnimator has posed the body.
public class ArmAim
{
    public float springHz = 12f, steadyHz = 20f;
    public float steadyDrop = 0.08f;
    public float maxWristAngle = 35f;
    public float bodyClearance = 0.25f;

    Vector3 handOff, handVel;
    Quaternion gunRot = Quaternion.identity;
    bool gunRotValid;
    float recoil;
    static readonly RaycastHit[] clearHits = new RaycastHit[16];

    // Arm starts where it hangs now (it rises into the aim).
    public void Begin(CharacterFigure fig)
    {
        handOff = fig.WristR.position - fig.ShoulderR.position;
        handVel = Vector3.zero;
        gunRotValid = false;
        recoil = 0f;
    }

    public void Kick(Vector3 shotDir, Weapon gun)
    {
        recoil += gun.recoilPitch;
        handOff -= shotDir * gun.recoilBack;
    }

    // Gun parented to the right wrist in hand pose (see Attach).
    public static void Attach(Weapon gun, CharacterFigure fig)
    {
        gun.transform.SetParent(fig.WristR, false);
        gun.transform.localPosition = new Vector3(0f, -0.04f * fig.Scale, 0f);
        gun.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
    }

    // steadyEye: eye position for the steady pose (null = arm's length). up: the view's up (gun upright).
    public void Solve(CharacterFigure fig, Weapon gun, Vector3 aimPoint, Transform self, float bodyYaw, Vector3 up,
                      Vector3? steadyEye, Vector3 sway, float dt)
    {
        float s = fig.Scale;
        Vector3 S = fig.ShoulderR.position;
        float upper = Vector3.Distance(S, fig.ElbowR.position);
        float fore = Vector3.Distance(fig.ElbowR.position, fig.WristR.position);
        float reach = upper + fore;
        float handAhead = 0.04f * s;                     // hand centre (the grip) ahead of the wrist
        float gunLen = gun.Length + handAhead;
        Vector3 toAim = aimPoint - S;
        float d = toAim.magnitude;
        Vector3 dir = d > 1e-4f ? toAim / d : Quaternion.Euler(0f, bodyYaw, 0f) * Vector3.forward;
        float ext = reach * 0.97f;
        Vector3 target;
        bool steady = steadyEye.HasValue;
        if (steady)
        {
            Vector3 eye = steadyEye.Value, u = (aimPoint - eye).normalized;
            Vector3 o = eye - up * steadyDrop * s - S;
            float b = Vector3.Dot(o, u), disc = b * b - (o.sqrMagnitude - ext * ext);
            float t = disc > 0f ? -b + Mathf.Sqrt(disc) : 0.4f * s;
            t = Mathf.Min(t, Vector3.Distance(eye, aimPoint) - gunLen - bodyClearance);
            target = eye + u * Mathf.Max(0.15f, t) - up * steadyDrop * s;
        }
        else
        {
            float lim = d - gunLen - bodyClearance;
            target = S + dir * (lim < ext ? Mathf.Max(0.25f * s, lim) : ext);
        }
        // Anything between the shoulder and where the muzzle would end up (a railing, a door frame the
        // aim passes over or beside) also pulls the hand back, so the gun never pushes into it.
        Vector3 armDir = target - S;
        float armLen = armDir.magnitude;
        if (armLen > 1e-4f)
        {
            armDir /= armLen;
            float free = Clearance(S, armDir, armLen + gunLen + 0.1f, self);
            float maxArm = free - gunLen - 0.1f;
            if (maxArm < armLen) target = S + armDir * Mathf.Max(0.2f * s, maxArm);
        }
        target += sway;
        float hz = steady ? steadyHz : springHz;
        AimSolver.Spring(ref handOff, ref handVel, target - S, hz, dt);
        Vector3 wrist = S + handOff;

        // Two-bone IK, pole down and out to the right of the body.
        Vector3 bodyRight = Quaternion.Euler(0f, bodyYaw, 0f) * Vector3.right;
        Vector3 pole = S + (Vector3.down + bodyRight * 0.6f) * reach;
        Vector3 elbow = AimSolver.TwoBone(S, wrist, upper, fore, pole, out Vector3 reached);
        Vector3 axis = Vector3.Cross(reached - S, pole - S);
        if (axis.sqrMagnitude < 1e-8f) axis = bodyRight;
        axis.Normalize();
        AimSolver.PointBone(fig.ShoulderR, elbow, axis);
        AimSolver.PointBone(fig.ElbowR, reached, axis);

        // Gun: barrel ray through the aim point, from wherever the muzzle ends up.
        Vector3 w = fig.WristR.position;
        Vector3 forearm = (w - fig.ElbowR.position).normalized;
        Vector3 m = gun.muzzle.localPosition + Vector3.forward * handAhead; // muzzle in the gun frame, from the wrist
        Quaternion R = gunRotValid ? gunRot : Quaternion.LookRotation(forearm, up);
        for (int i = 0; i < 2; i++)
        {
            Vector3 f = aimPoint - (w + R * m);
            if (f.sqrMagnitude > 1e-6f) R = Quaternion.LookRotation(f, up);
        }
        Vector3 fwd = R * Vector3.forward;
        if (Vector3.Angle(forearm, fwd) > maxWristAngle)
            R = Quaternion.LookRotation(Vector3.RotateTowards(forearm, fwd, maxWristAngle * Mathf.Deg2Rad, 0f), up);
        // The barrel carries a little weight too (a fast flick fired early goes where it points).
        float k = 1f - Mathf.Exp(-2f * Mathf.PI * hz * 0.6f * dt);
        gunRot = gunRotValid ? Quaternion.Slerp(gunRot, R, k) : R;
        gunRotValid = true;
        recoil = Mathf.Lerp(recoil, 0f, 1f - Mathf.Exp(-14f * dt));
        Quaternion final = gunRot * Quaternion.Euler(-recoil, 0f, 0f);
        fig.WristR.rotation = final * Quaternion.Euler(-90f, 0f, 0f); // hand along the barrel; the gun is its child
    }

    // Free distance along a ray for a gun-thick sphere (solid world only, not us, not triggers).
    static float Clearance(Vector3 from, Vector3 dir, float max, Transform self)
    {
        int n = Physics.SphereCastNonAlloc(from, 0.04f, dir, clearHits, max, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        float best = max;
        for (int i = 0; i < n; i++)
        {
            var c = clearHits[i].collider;
            if (c == null || (self != null && c.transform.IsChildOf(self))) continue;
            if (clearHits[i].distance > 0f && clearHits[i].distance < best) best = clearHits[i].distance;
        }
        return best;
    }
}
