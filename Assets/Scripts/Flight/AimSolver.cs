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
