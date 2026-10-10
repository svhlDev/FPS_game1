using System.Collections.Generic;
using UnityEngine;

// A car explosion or a grenade: damage and a push to everything nearby with a clear line to the centre
// (static geometry blocks it), a flash, a shockwave ring, debris, camera shake by distance, and fire
// chunks (14-20 for a car) thrown upward and along the source's velocity.
//   Cars    : damage x falloff, pushed through the mass system, may ignite close in.
//   People  : characterDamage x falloff (CharacterHealth; the player on foot is thrown); a body killed by
//             it, and every ragdoll in range, gets impulse x falloff as a velocity change away from it.
// Falloff: 1 - distance / radius.
public static class Explosion
{
    public const float Radius = 12f, CentreDamage = 250f, Impulse = 18f, CharacterDamage = 120f;
    static readonly List<CharacterHealth> victims = new List<CharacterHealth>();

    public static void At(Vector3 pos, Vector3 sourceVelocity, VehicleHealth source = null,
                          float radius = Radius, float damage = CentreDamage, float impulse = Impulse,
                          float characterDamage = CharacterDamage, int minChunks = 14, int maxChunks = 20, bool byPlayer = false)
    {
        // Cars: damage and push (through the mass system: lighter cars fly further).
        foreach (var car in FlyingVehicle.Active)
        {
            if (car == null) continue;
            var h = car.Health;
            if (h == source) continue;
            Vector3 to = car.transform.position - pos;
            float d = to.magnitude;
            if (d > radius) continue;
            if (!PoliceDispatch.LineOfSight(pos, car.transform.position)) continue;
            float k = 1f - d / radius;
            Vector3 dir = d > 0.01f ? to / d : Vector3.up;
            if (h != null) h.Damage(damage * k, car.transform.position - dir * car.BodyHalfExtents.x, -dir);
            car.AddImpulse(dir * impulse * k);
            if (k > 0.5f && h != null && Random.value < k) h.Ignite();
        }

        // The player on foot.
        var fpc = FirstPersonController.Instance;
        if (fpc != null && fpc.isActiveAndEnabled)
        {
            Vector3 p = fpc.transform.position + Vector3.up;
            float d = Vector3.Distance(p, pos);
            if (d < radius && PoliceDispatch.LineOfSight(pos, p))
            {
                fpc.Damage(characterDamage * (1f - d / radius), "Blown up");
                fpc.Push((p - pos).normalized * impulse * (1f - d / radius));
            }
            if (d < radius * 6f) fpc.Shake(Mathf.Clamp01(1.2f - d / (radius * 6f)));
        }
        // Everyone else on foot, then the bodies lying around.
        victims.Clear();
        foreach (var ch in CharacterHealth.All) if (ch != null && !ch.IsPlayer && !ch.Dead) victims.Add(ch);
        foreach (var ch in victims)
        {
            if (ch == null || ch.Dead) continue;
            Vector3 p = ch.transform.position + Vector3.up;
            Vector3 to = p - pos;
            float d = to.magnitude;
            if (d >= radius || !PoliceDispatch.LineOfSight(pos, p)) continue;
            float k = 1f - d / radius;
            Vector3 dir = d > 0.01f ? to / d : Vector3.up;
            ch.Damage(new DamageInfo { amount = characterDamage * k, kind = DamageKind.Explosion, point = p,
                                       velocityChange = (dir + Vector3.up * 0.5f).normalized * impulse * k, byPlayer = byPlayer });
            if (ch != null && !ch.Dead)
            {
                var o = ch.GetComponent<OfficerAgent>();
                if (o != null) o.Stagger(1.5f, new Vector3(dir.x, 0f, dir.z).normalized * 2f * k);
            }
        }
        foreach (var r in Ragdoll.All)
        {
            if (r == null || r.HipsBody == null || r.Born >= Time.time) continue; // killed by this blast: already pushed
            Vector3 to = r.HipsBody.position - pos;
            float d = to.magnitude;
            if (d >= radius) continue;
            float k = 1f - d / radius;
            r.Push((to.normalized + Vector3.up * 0.5f).normalized * impulse * k);
        }
        foreach (var g in GunPickup.All)
            if (g != null && g.Body != null && (g.Body.position - pos).sqrMagnitude < radius * radius)
                g.Body.AddForce((g.Body.position - pos).normalized * impulse * (1f - (g.Body.position - pos).magnitude / radius), ForceMode.VelocityChange);

        var driven = FlyingVehicle.Driven;
        if (driven != null) driven.Shake(Mathf.Clamp01(1.2f - Vector3.Distance(driven.transform.position, pos) / (radius * 6f)));
        PedestrianSystem.ReportDanger(pos, radius * 3f);

        // Show it.
        Effects.Flash(pos, radius * 0.45f);
        Effects.Ring(pos, radius * 1.6f);
        int n = Random.Range(10, 16);
        for (int i = 0; i < n; i++)
            Effects.Debris(pos + Random.insideUnitSphere, Random.onUnitSphere * Random.Range(6f, 16f) + Vector3.up * 6f + sourceVelocity * 0.5f, Random.Range(0.25f, 0.7f));
        for (int i = 0; i < 12; i++)
            Effects.Puff(pos + Random.insideUnitSphere * 2f, Random.insideUnitSphere * 3f, Random.Range(1f, 2f), Random.Range(2f, 4f));

        // Fire: chunks in a hemisphere biased upward and along the velocity.
        FireSystem.Spray(pos, sourceVelocity * 0.6f + Vector3.up * 4f, Random.Range(minChunks, maxChunks + 1), 6f, 14f);
        Count++;
    }

    public static int Count;
}
