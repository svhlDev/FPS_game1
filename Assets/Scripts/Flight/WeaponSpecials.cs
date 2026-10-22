using System;
using System.Collections.Generic;
using UnityEngine;

// The firing logic of the new weapons (PlayerWeapon calls these) and their world objects:
//   ArcCaster.Fire     : chain lightning (ArcBolts draws the jagged bolts).
//   Repulsor.Blast     : a cone shockwave (RepulsorWave draws the expanding rings).
//   LimpetCharge       : a sticky magnetic charge (launched, sticks, set off by the player).
//   SingularityOrb     : an orb that opens a micro black hole, pulls things in, then collapses.
// Each reports what it hit through `onHit` (collider, point, damage) so PlayerWeapon can tell the police,
// exactly like a T-gun hit. Everything here counts as caused by the player.
public static class WeaponFx
{
    static readonly Dictionary<Color, Material> unlit = new Dictionary<Color, Material>();

    public static Material Unlit(Color c)
    {
        if (unlit.TryGetValue(c, out var m) && m != null) return m;
        m = new Material(Shader.Find("Universal Render Pipeline/Unlit")) { name = "WeaponFx", enableInstancing = true };
        m.SetColor("_BaseColor", c);
        unlit[c] = m;
        return m;
    }

    public static Renderer Prim(PrimitiveType type, Transform parent, Vector3 pos, Vector3 scale, Quaternion rot, Material mat, bool shadows = false)
    {
        var go = GameObject.CreatePrimitive(type);
        UnityEngine.Object.Destroy(go.GetComponent<Collider>());
        go.transform.SetParent(parent, false);
        go.transform.localPosition = pos;
        go.transform.localRotation = rot;
        go.transform.localScale = scale;
        var r = go.GetComponent<Renderer>();
        r.sharedMaterial = mat;
        r.shadowCastingMode = shadows ? UnityEngine.Rendering.ShadowCastingMode.On : UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;
        return r;
    }

    // A character's chest (where bolts and pulls aim).
    public static Vector3 Chest(CharacterHealth ch)
    {
        var fig = ch.GetComponent<CharacterFigure>();
        float h = fig != null ? fig.Height : 1.8f;
        return ch.transform.position + Vector3.up * h * 0.62f;
    }

    public static Collider AnyCollider(Component c) => c != null ? c.GetComponentInChildren<Collider>() : null;

    public static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);
}

// ======================================================================================================
// Arc caster: a hitscan bolt along the barrel (35 m). Whatever it hits (or the wall it strikes) becomes
// the first node; from there the arc jumps to the nearest unzapped character or car within jumpRadius
// (8 m from a target, 6 m from a wall) with a clear line, up to maxJumps times, each jump at 80% of the
// last one's damage. People: damage (head/limb rules don't apply, it's the whole body), a jolt, officers
// stunned stunTime s. Cars: damage and a hiccup (thrust cut). Never arcs into the player.
// ======================================================================================================
public static class ArcCaster
{
    public const int MaxJumps = 4;
    public const float JumpRadius = 8f, WallJumpRadius = 6f, JumpFalloff = 0.8f;
    public static int Casts, LastChainLength;

    struct Node { public Vector3 p; public CharacterHealth ch; public FlyingVehicle car; public Collider col; }
    static readonly List<Node> chain = new List<Node>(8);
    static readonly HashSet<UnityEngine.Object> zapped = new HashSet<UnityEngine.Object>();

    public static void Fire(Vector3 muzzle, Vector3 dir, Weapon gun, Transform shooter, Action<Collider, Vector3, float> onHit)
    {
        dir.Normalize();
        Casts++;
        chain.Clear();
        zapped.Clear();
        bool hit = LaserWeapon.Trace(muzzle, dir, gun.Range, shooter, out var h);
        Vector3 end = hit ? h.point : muzzle + dir * gun.Range;
        ArcBolts.I.Bolt(muzzle, end, 0.16f, 0.035f);
        Effects.Glow(muzzle + dir * 0.03f, 0.05f, true, 0.08f);
        PedestrianSystem.ReportDanger(muzzle, 40f);
        if (!hit) { LastChainLength = 0; return; }

        float radius = WallJumpRadius;
        var first = NodeFor(h.collider, h.point);
        if (first.ch != null || first.car != null) { chain.Add(first); radius = JumpRadius; }
        Sparks(h.point, h.normal);
        BurnMarks.MarkOnly(h, Weapon.Mode.Stun);

        // Jump from the current point to the nearest new target.
        Vector3 from = hit ? h.point : end;
        for (int j = 0; j < MaxJumps; j++)
        {
            if (!Nearest(from, radius, out var next)) break;
            ArcBolts.I.Bolt(from, next.p, 0.16f, 0.025f);
            chain.Add(next);
            from = next.p;
            radius = JumpRadius;
        }

        // Apply, first node at full damage.
        float dmg = gun.damage, carDmg = gun.carDamage;
        Vector3 prev = muzzle;
        foreach (var n in chain)
        {
            Vector3 d = (n.p - prev).normalized;
            onHit?.Invoke(n.col, n.p, dmg);
            if (n.ch != null && !n.ch.Dead)
            {
                n.ch.React(BodyPart.Location.Body, null, n.p, d, 0.6f);
                n.ch.Damage(new DamageInfo { amount = dmg, kind = DamageKind.Laser, point = n.p, part = n.col, impulse = d * 2.5f, byPlayer = true });
                if (!n.ch.Dead)
                {
                    var o = n.ch.GetComponent<OfficerAgent>();
                    if (o != null) o.Stun(gun.stunTime);
                    else n.ch.Knock(WeaponFx.Flat(d) * 0.35f, 0.6f);
                }
                if (n.col != null) PedestrianSystem.Shot(n.col, true, 0f, muzzle);
            }
            else if (n.car != null)
            {
                n.car.Hiccup(gun.carHiccup);
                if (n.car.Health != null) n.car.Health.Damage(carDmg, n.p, -d);
            }
            Effects.Glow(n.p, 0.25f, true, 0.18f);
            Sparks(n.p, -d);
            dmg *= JumpFalloff; carDmg *= JumpFalloff;
            prev = n.p;
        }
        LastChainLength = chain.Count;
    }

    static Node NodeFor(Collider c, Vector3 p)
    {
        var n = new Node { p = p, col = c, ch = CharacterHealth.Of(c), car = c != null ? c.GetComponentInParent<FlyingVehicle>() : null };
        if (n.ch != null && n.ch.IsPlayer) n.ch = null;
        if (n.ch != null) { zapped.Add(n.ch); n.car = null; }
        else if (n.car != null) zapped.Add(n.car);
        return n;
    }

    static bool Nearest(Vector3 from, float radius, out Node best)
    {
        best = default;
        float bd = radius * radius;
        bool found = false;
        foreach (var ch in CharacterHealth.All)
        {
            if (ch == null || ch.Dead || ch.IsPlayer || zapped.Contains(ch)) continue;
            Vector3 p = WeaponFx.Chest(ch);
            float d = (p - from).sqrMagnitude;
            if (d >= bd || !PoliceDispatch.LineOfSight(from, p)) continue;
            bd = d; found = true;
            best = new Node { p = p, ch = ch, col = WeaponFx.AnyCollider(ch) };
        }
        foreach (var car in FlyingVehicle.Active)
        {
            if (car == null || zapped.Contains(car)) continue;
            Vector3 p = car.transform.position;
            float d = (p - from).sqrMagnitude;
            if (d >= bd || !PoliceDispatch.LineOfSight(from, p)) continue;
            bd = d; found = true;
            best = new Node { p = p, car = car, col = WeaponFx.AnyCollider(car) };
        }
        if (found) zapped.Add(best.ch != null ? (UnityEngine.Object)best.ch : best.car);
        return found;
    }

    static void Sparks(Vector3 p, Vector3 n)
    {
        for (int i = 0; i < 4; i++)
            Effects.Debris(p + n * 0.05f, (n + UnityEngine.Random.insideUnitSphere) * UnityEngine.Random.Range(2f, 6f), 0.02f);
    }
}

// Pool of jagged lightning bolts: 12-point lines whose inner points jump about every 35 ms while the
// bolt fades (thinner and thinner) over its life.
[DefaultExecutionOrder(210)]
public class ArcBolts : MonoBehaviour
{
    public static readonly Color BoltColor = new Color(0.55f, 0.85f, 1f);
    const int Points = 12;
    struct Live { public LineRenderer line; public Vector3 a, b; public float start, life, width, nextJitter; }
    static ArcBolts instance;
    readonly List<Live> live = new List<Live>();
    readonly Stack<LineRenderer> pool = new Stack<LineRenderer>();
    static Material mat;

    public static ArcBolts I => instance != null ? instance : instance = new GameObject("ArcBolts").AddComponent<ArcBolts>();

    public void Bolt(Vector3 a, Vector3 b, float life, float width)
    {
        if (mat == null) mat = WeaponFx.Unlit(BoltColor * 7f);
        LineRenderer l;
        if (pool.Count > 0) l = pool.Pop();
        else
        {
            l = new GameObject("Bolt").AddComponent<LineRenderer>();
            l.transform.SetParent(transform, false);
            l.positionCount = Points;
            l.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            l.receiveShadows = false;
            l.numCapVertices = 2;
        }
        l.sharedMaterial = mat;
        l.widthMultiplier = width;
        l.enabled = true;
        var b0 = new Live { line = l, a = a, b = b, start = Time.time, life = life, width = width };
        Jitter(ref b0);
        live.Add(b0);
    }

    static void Jitter(ref Live b)
    {
        Vector3 d = b.b - b.a;
        float len = d.magnitude;
        Vector3 f = len > 1e-4f ? d / len : Vector3.forward;
        Vector3 u = Vector3.Cross(f, Mathf.Abs(f.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
        Vector3 r = Vector3.Cross(f, u);
        float amp = Mathf.Clamp(len * 0.06f, 0.03f, 0.6f);
        for (int i = 0; i < Points; i++)
        {
            float t = i / (Points - 1f);
            Vector3 p = b.a + d * t;
            if (i > 0 && i < Points - 1)
            {
                float env = Mathf.Sin(t * Mathf.PI);
                p += (u * UnityEngine.Random.Range(-1f, 1f) + r * UnityEngine.Random.Range(-1f, 1f)) * amp * env;
            }
            b.line.SetPosition(i, p);
        }
        b.nextJitter = Time.time + 0.035f;
    }

    void LateUpdate()
    {
        for (int i = live.Count - 1; i >= 0; i--)
        {
            var b = live[i];
            float t = (Time.time - b.start) / b.life;
            if (t >= 1f || b.line == null)
            {
                if (b.line != null) { b.line.enabled = false; pool.Push(b.line); }
                live.RemoveAt(i);
                continue;
            }
            if (Time.time >= b.nextJitter) Jitter(ref b);
            b.line.widthMultiplier = b.width * (1f - t);
            live[i] = b;
        }
    }
}

// ======================================================================================================
// Repulsor: a cone shockwave from the muzzle. power 0.35 (tap) .. 1 (full 0.9 s charge) scales range
// (6 .. 16 m), the shove and the damage. Inside the 32-degree cone with a clear line:
//   cars       : shoved off their lane (impulse through the mass system, a little lift), hiccup, damage
//   people     : light damage (a blast; a killed body flies), shoved up to 4.5 m and staggered
//   rigidbodies: bodies, dropped guns, grenades, charges fly
// Returns how many things it moved.
// ======================================================================================================
public static class Repulsor
{
    public const float MinRange = 6f, MaxRange = 16f, HalfAngle = 32f;
    public const float CarImpulse = 30f, BodyPush = 18f, PeopleShove = 4.5f;
    public static int Blasts, LastMoved;
    static readonly Collider[] buf = new Collider[256];
    static readonly HashSet<Rigidbody> pushed = new HashSet<Rigidbody>();
    static readonly List<CharacterHealth> victims = new List<CharacterHealth>();

    public static int Blast(Vector3 origin, Vector3 dir, float power, Weapon gun, Transform shooter, Action<Collider, Vector3, float> onHit)
    {
        dir.Normalize();
        power = Mathf.Clamp01(power);
        float range = Mathf.Lerp(MinRange, MaxRange, power);
        float cos = Mathf.Cos(HalfAngle * Mathf.Deg2Rad);
        int moved = 0;
        Blasts++;

        bool InCone(Vector3 p, float slack, out float d, out Vector3 to)
        {
            to = p - origin;
            d = to.magnitude;
            if (d > range + slack) return false;
            if (d < 0.8f) return true; // right on top of the muzzle
            // Wide things (cars) count when any of them is in the cone: widen it by their angular size.
            float c = slack > 0f ? Mathf.Cos(Mathf.Min(89f, HalfAngle + Mathf.Atan2(slack, d) * Mathf.Rad2Deg) * Mathf.Deg2Rad) : cos;
            return Vector3.Dot(to / d, dir) >= c;
        }

        foreach (var car in FlyingVehicle.Active)
        {
            if (car == null) continue;
            float slack = car.BodyHalfExtents.z;
            if (!InCone(car.transform.position, slack, out float d, out Vector3 to)) continue;
            if (!PoliceDispatch.LineOfSight(origin, car.transform.position)) continue;
            float k = Mathf.Clamp01(1f - Mathf.Max(0f, d - slack) / range * 0.6f);
            Vector3 push = (to.normalized * 0.6f + dir * 0.4f).normalized;
            float imp = CarImpulse * power * k;
            onHit?.Invoke(WeaponFx.AnyCollider(car), car.transform.position, gun.carDamage * power * k);
            car.AddImpulse(push * imp + Vector3.up * imp * 0.18f);
            car.Hiccup(0.5f + power);
            if (car.Health != null) car.Health.Damage(gun.carDamage * power * k, car.transform.position - push, -push);
            moved++;
        }

        victims.Clear();
        foreach (var ch in CharacterHealth.All) if (ch != null && !ch.Dead && !ch.IsPlayer) victims.Add(ch);
        foreach (var ch in victims)
        {
            if (ch == null || ch.Dead) continue;
            Vector3 p = WeaponFx.Chest(ch);
            if (!InCone(p, 0.3f, out float d, out Vector3 to)) continue;
            if (!PoliceDispatch.LineOfSight(origin, p)) continue;
            float k = Mathf.Clamp01(1f - d / range * 0.6f);
            Vector3 push = WeaponFx.Flat(to).normalized;
            if (push.sqrMagnitude < 0.01f) push = WeaponFx.Flat(dir).normalized;
            float dmg = gun.damage * power * k;
            var col = WeaponFx.AnyCollider(ch);
            onHit?.Invoke(col, p, dmg);
            ch.Damage(new DamageInfo
            {
                amount = dmg, kind = DamageKind.Explosion, point = p, part = col, byPlayer = true,
                velocityChange = (push + Vector3.up * 0.45f).normalized * BodyPush * power * k,
            });
            if (ch != null && !ch.Dead)
            {
                var o = ch.GetComponent<OfficerAgent>();
                if (o != null) o.Stagger(1.4f, push * 3f * power * k);
                ch.Knock(push * PeopleShove * power * k, 1.0f);
                ch.React(BodyPart.Location.Body, null, p, push, 1f);
            }
            if (col != null) PedestrianSystem.Shot(col, true, 0f, origin);
            moved++;
        }

        // Loose physics: bodies, guns, grenades, charges in flight.
        pushed.Clear();
        int n = Physics.OverlapSphereNonAlloc(origin, range, buf, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < n; i++)
        {
            var rb = buf[i].attachedRigidbody;
            if (rb == null || rb.isKinematic || pushed.Contains(rb)) continue;
            if (shooter != null && rb.transform.IsChildOf(shooter)) continue;
            if (!InCone(rb.worldCenterOfMass, 0.3f, out float d, out Vector3 to)) continue;
            pushed.Add(rb);
            float k = Mathf.Clamp01(1f - d / range * 0.6f);
            rb.WakeUp();
            rb.AddForce((to.normalized + Vector3.up * 0.35f).normalized * BodyPush * power * k, ForceMode.VelocityChange);
            moved++;
        }

        // Show it: rings racing down the cone, dust, a flash at the bell.
        for (int i = 0; i < 3; i++) RepulsorWave.Spawn(origin, dir, range, HalfAngle, 0.28f + i * 0.06f, i * 0.05f);
        Effects.Glow(origin + dir * 0.05f, 0.12f, false, 0.1f);
        for (int i = 0; i < 8; i++)
        {
            Vector3 jitter = UnityEngine.Random.insideUnitSphere * 0.4f;
            Effects.Puff(origin + dir * (0.5f + i * range * 0.08f) + jitter, (dir + jitter) * (6f + 10f * power), 0.25f + 0.1f * i, 0.5f);
        }
        PedestrianSystem.ReportDanger(origin, 35f);
        LastMoved = moved;
        return moved;
    }
}

// An expanding violet ring travelling down the repulsor's cone.
public class RepulsorWave : MonoBehaviour
{
    const int Points = 32;
    LineRenderer line;
    Vector3 origin, dir;
    float range, tan, life, start;

    public static void Spawn(Vector3 origin, Vector3 dir, float range, float halfAngle, float life, float delay)
    {
        var go = new GameObject("RepulsorWave");
        var w = go.AddComponent<RepulsorWave>();
        w.origin = origin; w.dir = dir; w.range = range; w.life = life;
        w.tan = Mathf.Tan(halfAngle * Mathf.Deg2Rad);
        w.start = Time.time + delay;
        w.line = go.AddComponent<LineRenderer>();
        w.line.useWorldSpace = false;
        w.line.loop = true;
        w.line.positionCount = Points;
        w.line.sharedMaterial = Weapon.AccentMat(Weapon.Kind.Repulsor);
        w.line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        w.line.receiveShadows = false;
        w.line.enabled = false;
        go.transform.SetPositionAndRotation(origin, Quaternion.LookRotation(dir));
    }

    void LateUpdate()
    {
        float t = (Time.time - start) / life;
        if (t < 0f) return;
        if (t >= 1f) { Destroy(gameObject); return; }
        line.enabled = true;
        float e = 1f - (1f - t) * (1f - t);   // ease out
        float z = Mathf.Lerp(0.2f, range * 0.85f, e);
        float r = Mathf.Max(0.05f, z * tan);
        for (int i = 0; i < Points; i++)
        {
            float a = i / (float)Points * Mathf.PI * 2f;
            line.SetPosition(i, new Vector3(Mathf.Cos(a) * r, Mathf.Sin(a) * r, z));
        }
        line.widthMultiplier = Mathf.Lerp(0.09f, 0.01f, t);
    }
}

// ======================================================================================================
// Limpet charge: a small magnetic disc lobbed from the launcher (26 m/s along the barrel plus your own
// velocity, gravity). It sticks to the first thing it hits: a car (rides along with it), a person or
// body (rides the limb it hit), anything else (stays put). Its light blinks while armed. The player sets
// every live charge off with RMB (one after another, 70 ms apart, in the order they were fired); a
// charge also goes off by itself 60 s after sticking. Blast: radius 5, cars 140, people 110, a few fire
// chunks. Sticking one near police is lethal force, the blast in sight of police is wanted 2.
// At most MaxLive charges: firing more sets off the oldest first.
// ======================================================================================================
public class LimpetCharge : MonoBehaviour
{
    public const int MaxLive = 8;
    public const float Speed = 26f, Gravity = 9.81f, Radius = 5f, CarDamage = 140f, CharacterDamage = 110f, Impulse = 12f;
    public const float SelfTimer = 60f, FlightTime = 8f;

    static readonly List<LimpetCharge> live = new List<LimpetCharge>();
    public static IReadOnlyList<LimpetCharge> Live => live;
    public static int Exploded, Stuck;

    Vector3 velocity;
    Transform shooter;
    Vector3 shotFrom;
    float born, stuckAt = -1f, detonateAt = -1f;
    FlyingVehicle car;
    Renderer blink;
    public bool IsStuck => stuckAt >= 0f;
    public FlyingVehicle Car => car;

    static Material shell;

    public static LimpetCharge Launch(Vector3 pos, Vector3 vel, Transform shooter)
    {
        // Over the cap: the oldest goes off now.
        live.RemoveAll(l => l == null);
        if (live.Count >= MaxLive && live[0] != null) live[0].Detonate();

        if (shell == null) shell = MaterialLibrary.Get(SurfaceType.PaintedMetal, new Color(0.1f, 0.1f, 0.11f), MaterialLibrary.Variant.Object);
        var go = new GameObject("LimpetCharge");
        go.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(vel.sqrMagnitude > 1e-4f ? vel : Vector3.forward));
        var c = go.AddComponent<LimpetCharge>();
        c.velocity = vel;
        c.shooter = shooter;
        c.shotFrom = pos;
        c.born = Time.time;
        // Disc (normal along local -Z faces the surface), three claws, glowing eye.
        var root = go.transform;
        WeaponFx.Prim(PrimitiveType.Cylinder, root, Vector3.zero, new Vector3(0.09f, 0.012f, 0.09f), Quaternion.Euler(90f, 0f, 0f), shell, true);
        for (int i = 0; i < 3; i++)
        {
            float a = i * 120f * Mathf.Deg2Rad;
            WeaponFx.Prim(PrimitiveType.Cube, root, new Vector3(Mathf.Cos(a) * 0.045f, Mathf.Sin(a) * 0.045f, -0.012f),
                          new Vector3(0.02f, 0.02f, 0.02f), Quaternion.Euler(0f, 0f, i * 120f), shell, true);
        }
        c.blink = WeaponFx.Prim(PrimitiveType.Sphere, root, new Vector3(0f, 0f, 0.014f), Vector3.one * 0.03f, Quaternion.identity, Weapon.AccentMat(Weapon.Kind.Limpet));
        live.Add(c);
        return c;
    }

    // RMB: everything live, oldest first.
    public static int DetonateAll(float stagger = 0.07f)
    {
        live.RemoveAll(l => l == null);
        int n = 0;
        foreach (var c in live)
        {
            if (c == null || c.detonateAt >= 0f) continue;
            c.detonateAt = Time.time + n * stagger;
            n++;
        }
        return n;
    }

    public static int Armed { get { int n = 0; foreach (var c in live) if (c != null) n++; return n; } }

    void OnDestroy() => live.Remove(this);

    void Update()
    {
        float dt = Time.deltaTime;
        if (detonateAt >= 0f && Time.time >= detonateAt) { Detonate(); return; }
        if (IsStuck)
        {
            // Rides whatever it stuck to (a child of it). A wrecked car carrying it may vanish: so does it.
            if (Time.time - stuckAt > SelfTimer) { Detonate(); return; }
            float period = Time.time - stuckAt < SelfTimer - 5f ? 0.6f : 0.15f;
            if (blink != null) blink.enabled = Mathf.Repeat(Time.time - stuckAt, period) < period * 0.45f;
            return;
        }
        if (Time.time - born > FlightTime) { Destroy(gameObject); return; }
        Vector3 p = transform.position;
        velocity += Vector3.down * Gravity * dt;
        Vector3 step = velocity * dt;
        float dist = step.magnitude;
        if (dist > 1e-5f && AimSolver.First(p, step / dist, dist + 0.03f, shooter, out var h))
        {
            Stick(h);
            return;
        }
        transform.position = p + step;
        if (velocity.sqrMagnitude > 0.01f) transform.rotation = Quaternion.LookRotation(velocity);
    }

    void Stick(RaycastHit h)
    {
        stuckAt = Time.time;
        Stuck++;
        transform.position = h.point + h.normal * 0.012f;
        transform.rotation = Quaternion.LookRotation(h.normal);    // disc flat on the surface: claws (-Z) in, eye (+Z) out
        var col = h.collider;
        car = col.GetComponentInParent<FlyingVehicle>();
        var ch = CharacterHealth.Of(col);
        Transform host = null;
        if (car != null) host = car.transform;
        else if (col.attachedRigidbody != null) host = col.attachedRigidbody.transform;
        else if (ch != null) host = col.transform;
        if (host != null) transform.SetParent(host, true);
        Effects.Glow(transform.position, 0.06f, false, 0.1f);
        PoliceDispatch.Instance?.ReportGrenadeThrown(shotFrom, h.point, 8f);
    }

    public void Detonate()
    {
        if (this == null) return;
        Vector3 p = transform.position;
        Vector3 v = car != null ? car.Velocity : velocity * (IsStuck ? 0f : 1f);
        live.Remove(this);
        Destroy(gameObject);
        Exploded++;
        Explosion.At(p, v, null, Radius, CarDamage, Impulse, CharacterDamage, 3, 6, true);
        PoliceDispatch.Instance?.ReportExplosion(p);
    }
}

// ======================================================================================================
// Singularity orb: a bright violet orb (20 m/s, no gravity, a short trail). On hitting something, or
// 1.3 s into its flight, it opens a micro black hole 1 m off the surface for 3.5 s:
//   - everything within PullRadius (14 m) is dragged toward the core, harder the closer it is:
//     cars (thrust cut, pulled through the mass system, damped near the core so they pile up instead of
//     slingshotting), people (dragged off their feet a step at a time, staggered), loose rigidbodies
//     (bodies, guns, grenades). Parked cars come loose. The player is not pulled.
//   - people within 2 m of the core are crushed (35 per second).
// Then it collapses: a blast (radius 8) that throws the whole pile outward. Opening near police is
// lethal force, the collapse in sight of police is wanted 2.
// ======================================================================================================
public class SingularityOrb : MonoBehaviour
{
    public const float Speed = 20f, Fuse = 1.3f, OpenTime = 3.5f, PullRadius = 14f, CrushRadius = 2f, CrushDps = 35f;
    public const float CarPull = 26f, PeoplePull = 5f, BodyPull = 22f;
    public static int Opened, Collapsed;
    static readonly List<SingularityOrb> live = new List<SingularityOrb>();
    public static IReadOnlyList<SingularityOrb> Live => live;

    enum Phase { Flying, Open }
    Phase phase;
    Vector3 velocity, shotFrom;
    Transform shooter;
    float born, openedAt;
    Transform core, diskA, diskB, orb;
    static readonly Collider[] buf = new Collider[256];
    static readonly HashSet<Rigidbody> pulled = new HashSet<Rigidbody>();
    static readonly List<CharacterHealth> victims = new List<CharacterHealth>();

    public bool IsOpen => phase == Phase.Open;

    public static SingularityOrb Launch(Vector3 pos, Vector3 vel, Transform shooter)
    {
        var go = new GameObject("SingularityOrb");
        go.transform.position = pos;
        var s = go.AddComponent<SingularityOrb>();
        s.velocity = vel;
        s.shooter = shooter;
        s.shotFrom = pos;
        s.born = Time.time;
        var glow = Weapon.AccentMat(Weapon.Kind.Singularity);
        s.orb = WeaponFx.Prim(PrimitiveType.Sphere, go.transform, Vector3.zero, Vector3.one * 0.16f, Quaternion.identity, WeaponFx.Unlit(new Color(0.85f, 0.7f, 1f) * 4f)).transform;
        var trail = go.AddComponent<TrailRenderer>();
        trail.time = 0.25f;
        trail.widthMultiplier = 0.1f;
        trail.sharedMaterial = glow;
        trail.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        trail.receiveShadows = false;
        live.Add(s);
        return s;
    }

    void OnDestroy() => live.Remove(this);

    void Update()
    {
        if (phase == Phase.Flying) { Fly(); return; }
        float t = (Time.time - openedAt) / OpenTime;
        if (t >= 1f) { Collapse(); return; }
        Animate(t);
        Pull(Time.deltaTime, t);
    }

    void Fly()
    {
        float dt = Time.deltaTime;
        Vector3 p = transform.position;
        Vector3 step = velocity * dt;
        float dist = step.magnitude;
        if (dist > 1e-5f && AimSolver.First(p, step / dist, dist + 0.1f, shooter, out var h))
        {
            Open(h.point + h.normal * 1f);
            return;
        }
        transform.position = p + step;
        if (Time.time - born > Fuse) Open(transform.position);
    }

    void Open(Vector3 at)
    {
        phase = Phase.Open;
        openedAt = Time.time;
        Opened++;
        transform.position = at;
        var trail = GetComponent<TrailRenderer>();
        if (trail != null) trail.emitting = false;
        if (orb != null) orb.gameObject.SetActive(false);
        var glow = Weapon.AccentMat(Weapon.Kind.Singularity);
        core = WeaponFx.Prim(PrimitiveType.Sphere, transform, Vector3.zero, Vector3.one * 0.1f, Quaternion.identity, WeaponFx.Unlit(Color.black)).transform;
        diskA = WeaponFx.Prim(PrimitiveType.Cylinder, transform, Vector3.zero, new Vector3(0.1f, 0.004f, 0.1f), Quaternion.Euler(12f, 0f, 0f), glow).transform;
        diskB = WeaponFx.Prim(PrimitiveType.Cylinder, transform, Vector3.zero, new Vector3(0.1f, 0.004f, 0.1f), Quaternion.Euler(-60f, 30f, 0f), glow).transform;
        Effects.Flash(at, 1.2f);
        PedestrianSystem.ReportDanger(at, PullRadius * 2f);
        PoliceDispatch.Instance?.ReportGrenadeThrown(shotFrom, at, PullRadius);
    }

    // Grows in over 0.3 s, breathes, and the accretion disks spin.
    void Animate(float t)
    {
        float grow = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((Time.time - openedAt) / 0.3f));
        float shrink = t > 0.92f ? Mathf.Lerp(1f, 0.15f, (t - 0.92f) / 0.08f) : 1f;
        float s = grow * shrink * (1f + 0.06f * Mathf.Sin(Time.time * 18f));
        if (core != null) core.localScale = Vector3.one * 1.1f * s;
        if (diskA != null)
        {
            diskA.localScale = new Vector3(3.4f * s, 0.004f, 3.4f * s);
            diskA.Rotate(0f, 540f * Time.deltaTime, 0f, Space.Self);
        }
        if (diskB != null)
        {
            diskB.localScale = new Vector3(2.4f * s, 0.004f, 2.4f * s);
            diskB.Rotate(0f, -720f * Time.deltaTime, 0f, Space.Self);
        }
        // Dust spiralling in.
        Vector3 c = transform.position;
        for (int i = 0; i < 3; i++)
        {
            Vector3 off = UnityEngine.Random.onUnitSphere * PullRadius * UnityEngine.Random.Range(0.3f, 0.7f);
            Vector3 tangent = Vector3.Cross(off, Vector3.up).normalized;
            Effects.Puff(c + off, (-off.normalized * 9f + tangent * 6f), 0.2f, 0.7f);
        }
    }

    void Pull(float dt, float t)
    {
        Vector3 c = transform.position;
        float ramp = Mathf.Clamp01((Time.time - openedAt) / 0.4f);

        foreach (var car in FlyingVehicle.Active)
        {
            if (car == null) continue;
            Vector3 to = c - car.transform.position;
            float d = to.magnitude;
            if (d > PullRadius || d < 1e-3f) continue;
            float k = 1f - d / PullRadius;
            car.Hiccup(0.3f);
            car.AddImpulse(to / d * CarPull * (0.35f + k) * ramp * dt * car.EffectiveMass);
            if (d < 4f) car.AddImpulse(-car.Velocity * Mathf.Min(1f, 3f * dt) * car.EffectiveMass); // pile up, don't orbit
        }

        victims.Clear();
        foreach (var ch in CharacterHealth.All) if (ch != null && !ch.Dead && !ch.IsPlayer) victims.Add(ch);
        foreach (var ch in victims)
        {
            if (ch == null || ch.Dead) continue;
            Vector3 p = WeaponFx.Chest(ch);
            Vector3 to = c - p;
            float d = to.magnitude;
            if (d > PullRadius) continue;
            float k = 1f - d / PullRadius;
            Vector3 flat = WeaponFx.Flat(to);
            float step = Mathf.Min(flat.magnitude, PeoplePull * (0.3f + k) * ramp * dt);
            if (flat.sqrMagnitude > 1e-4f) ch.Knock(flat.normalized * step, 0.3f);
            if (d < CrushRadius)
                ch.Damage(new DamageInfo
                {
                    amount = CrushDps * dt, kind = DamageKind.Explosion, point = p, byPlayer = true,
                    velocityChange = to.normalized * 4f, part = WeaponFx.AnyCollider(ch),
                });
        }

        pulled.Clear();
        int n = Physics.OverlapSphereNonAlloc(c, PullRadius, buf, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < n; i++)
        {
            var rb = buf[i].attachedRigidbody;
            if (rb == null || rb.isKinematic || pulled.Contains(rb)) continue;
            if (shooter != null && rb.transform.IsChildOf(shooter)) continue;
            pulled.Add(rb);
            Vector3 to = c - rb.worldCenterOfMass;
            float d = to.magnitude;
            if (d < 1e-3f) continue;
            float k = 1f - d / PullRadius;
            rb.WakeUp();
            rb.AddForce(to / d * BodyPull * (0.3f + k) * ramp, ForceMode.Acceleration);
            if (d < 2f) rb.linearVelocity *= Mathf.Exp(-4f * dt);
        }
    }

    void Collapse()
    {
        Vector3 p = transform.position;
        Collapsed++;
        Destroy(gameObject);
        Explosion.At(p, Vector3.zero, null, 8f, 180f, 22f, 120f, 0, 2, true);
        PoliceDispatch.Instance?.ReportExplosion(p);
    }
}
