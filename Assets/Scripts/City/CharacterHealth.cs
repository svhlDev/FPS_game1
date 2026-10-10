using System;
using System.Collections.Generic;
using UnityEngine;

public enum DamageKind { Laser, Punch, Explosion, Fire, Car, Fall, Other }

// One hit on a character: how much, what kind, where, which part, the push it gives a body killed by it
// (Impulse: N·s at Point on the hit part; VelocityChange: m/s on every part) and whether the player
// caused it.
public struct DamageInfo
{
    public float amount;
    public DamageKind kind;
    public Vector3 point;
    public Collider part;
    public Vector3 impulse;
    public Vector3 velocityChange;
    public bool byPlayer;
}

// Health for every figure: officers 100, civilians 60, the player 100.
//   Lethal laser 25 (head x2, body x1, limbs x0.6), punches 8 (head 12), explosions (Explosion falloff),
//   fire 12/s while burning, cars (relative speed above 6 m/s: (v - 6) x 6, CombatSystem), falls (owners
//   track their own: 10 m or more is fatal).
// The player's health stays on FirstPersonController (this forwards to it; dying there respawns).
// Everyone else dies at 0: the owner's Died handler runs (officers drop the gun, pedestrians leave the
// crowd), then the figure becomes a ragdoll pushed by the killing hit (Ragdoll.FromFigure) unless the
// handler did it already.
public class CharacterHealth : MonoBehaviour
{
    public const float OfficerHealth = 100f, CivilianHealth = 60f, PlayerHealth = 100f;
    public const float LaserImpulse = 3f;
    public const float FireDamagePerSecond = 12f;

    public float maxHealth = CivilianHealth;
    public float Health { get; private set; }
    public bool Dead { get; private set; }
    public bool IsPlayer { get; private set; }
    public CharacterFigure.Role Role { get; private set; }
    // Measured each frame by CombatSystem (car strikes) and handed to the ragdoll.
    public Vector3 Velocity { get; internal set; }
    internal Vector3 lastPos; internal bool hasLastPos;
    internal readonly Dictionary<FlyingVehicle, float> lastCarHit = new Dictionary<FlyingVehicle, float>();

    // Runs once at 0 (before the ragdoll). Return true if it made the body / removed the figure itself.
    public Func<CharacterHealth, DamageInfo, bool> Died;
    public static event Action<CharacterHealth, DamageInfo> AnyDied;

    static readonly List<CharacterHealth> all = new List<CharacterHealth>();
    public static IReadOnlyList<CharacterHealth> All => all;

    FirstPersonController fpc;
    Flammable flammable;
    internal CharacterController cc;

    public static CharacterHealth Add(GameObject go, CharacterFigure.Role role)
    {
        var h = go.GetComponent<CharacterHealth>() ?? go.AddComponent<CharacterHealth>();
        h.Role = role;
        h.IsPlayer = role == CharacterFigure.Role.Player;
        h.maxHealth = role == CharacterFigure.Role.Police ? OfficerHealth : role == CharacterFigure.Role.Player ? PlayerHealth : CivilianHealth;
        h.ResetHealth();
        return h;
    }

    public static CharacterHealth Of(Collider c) => c != null ? c.GetComponentInParent<CharacterHealth>() : null;

    public void ResetHealth()
    {
        Health = maxHealth;
        Dead = false;
        hasLastPos = false;
        lastCarHit.Clear();
    }

    void Awake()
    {
        fpc = GetComponent<FirstPersonController>();
        flammable = GetComponent<Flammable>();
        cc = GetComponent<CharacterController>();
        if (Health <= 0f && !Dead) Health = maxHealth;
    }

    void OnEnable() { all.Add(this); CombatSystem.Ensure(); }
    void OnDisable() => all.Remove(this);

    public float Current => IsPlayer && fpc != null ? fpc.Health : Health;

    public void Damage(DamageInfo d)
    {
        if (Dead || d.amount <= 0f || !isActiveAndEnabled) return;
        if (IsPlayer)
        {
            // In a car the car takes the hits (VehicleHealth).
            if (fpc != null && fpc.isActiveAndEnabled) fpc.Damage(d.amount, Cause(d.kind));
            return;
        }
        Health = Mathf.Max(0f, Health - d.amount);
        if (Health <= 0f) Die(d);
    }

    public void Kill(DamageInfo d) { d.amount = Mathf.Max(d.amount, Current + 1f); Damage(d); }

    static string Cause(DamageKind k) => k switch
    {
        DamageKind.Laser => "Shot", DamageKind.Explosion => "Blown up", DamageKind.Fire => "Burned to death",
        DamageKind.Car => "Run over", DamageKind.Fall => "Fell to your death", _ => "Killed",
    };

    void Die(DamageInfo d)
    {
        Dead = true;
        var fig = GetComponent<CharacterFigure>();
        bool handled = Died != null && Died(this, d);
        AnyDied?.Invoke(this, d);
        if (handled) return;
        if (fig != null && fig.Hips != null) Ragdoll.FromFigure(fig, Velocity, d);
        Destroy(gameObject);
    }

    // Burning (Flammable): 12 damage a second (the player burns on FirstPersonController).
    void Update()
    {
        if (IsPlayer || Dead) return;
        if (flammable == null) flammable = GetComponent<Flammable>();
        if (flammable != null && flammable.Burning)
            Damage(new DamageInfo { amount = FireDamagePerSecond * Time.deltaTime, kind = DamageKind.Fire, point = transform.position + Vector3.up });
    }

    // ---------- shared hit rules ----------

    // Location multiplier for a lethal laser: head x2, body x1, limbs x0.6 (no BodyPart: body).
    public static float LaserMultiplier(Collider c)
    {
        var part = c != null ? c.GetComponent<BodyPart>() : null;
        if (part == null) return 1f;
        return part.location == BodyPart.Location.Head ? 2f : part.location == BodyPart.Location.Body ? 1f : 0.6f;
    }

    // A lethal laser hit on whatever `h` is (no-op if it isn't a character): damage by location, and a
    // killing hit pushes the hit part 3 N·s along the beam.
    public static bool LaserHit(RaycastHit h, Vector3 dir, float baseDamage, bool byPlayer)
    {
        var ch = Of(h.collider);
        if (ch == null) return false;
        ch.Damage(new DamageInfo
        {
            amount = baseDamage * LaserMultiplier(h.collider), kind = DamageKind.Laser, point = h.point, part = h.collider,
            impulse = dir.normalized * LaserImpulse, byPlayer = byPlayer,
        });
        return true;
    }

    // A punch: 8 (head 12).
    public static bool PunchHit(Collider c, Vector3 point, Vector3 dir, bool byPlayer)
    {
        var ch = Of(c);
        if (ch == null) return false;
        var part = c.GetComponent<BodyPart>();
        bool head = part != null && part.location == BodyPart.Location.Head;
        ch.Damage(new DamageInfo { amount = head ? 12f : 8f, kind = DamageKind.Punch, point = point, part = c, impulse = dir.normalized * 6f, byPlayer = byPlayer });
        return true;
    }
}

// Per-frame checks that span every character: cars striking people. Each character's capsule is
// tested against car bodies; a car whose speed relative to the character is above carHitSpeed deals
// (v - carHitSpeed) x carHitDamage (once a second per car), knocks the survivor (officers stagger,
// pedestrians go down, the player is thrown) and gives a killed body the car's velocity.
[DefaultExecutionOrder(130)] // after the cars, the player and the NPCs moved
public class CombatSystem : MonoBehaviour
{
    public float carHitSpeed = 6f;
    public float carHitDamage = 6f;

    static CombatSystem instance;
    static readonly Collider[] overlap = new Collider[8];
    int trafficMask;

    public static CombatSystem Ensure()
    {
        if (instance == null && Application.isPlaying) instance = new GameObject("CombatSystem").AddComponent<CombatSystem>();
        return instance;
    }

    void Awake()
    {
        int traffic = LayerMask.NameToLayer("Traffic");
        trafficMask = traffic >= 0 ? 1 << traffic : Physics.DefaultRaycastLayers;
    }

    void Update()
    {
        float dt = Time.deltaTime;
        if (dt <= 0f) return;
        var list = CharacterHealth.All;
        for (int i = list.Count - 1; i >= 0; i--)
        {
            var ch = list[i];
            if (ch == null || ch.Dead) continue;
            Vector3 pos = ch.transform.position;
            ch.Velocity = ch.hasLastPos ? Vector3.Lerp(ch.Velocity, (pos - ch.lastPos) / dt, 0.5f) : Vector3.zero;
            ch.lastPos = pos; ch.hasLastPos = true;

            var cc = ch.cc;
            if (cc == null || !cc.enabled) continue;
            float h = cc.height, r = cc.radius;
            Vector3 a = pos + Vector3.up * (r + 0.25f), b = pos + Vector3.up * Mathf.Max(r + 0.25f, h - r);
            int n = Physics.OverlapCapsuleNonAlloc(a, b, r, overlap, trafficMask, QueryTriggerInteraction.Ignore);
            for (int k = 0; k < n; k++)
            {
                var car = overlap[k].GetComponentInParent<FlyingVehicle>();
                if (car == null) continue;
                Vector3 rel = car.Velocity - ch.Velocity;
                float v = rel.magnitude;
                if (v <= carHitSpeed) continue;
                if (ch.lastCarHit.TryGetValue(car, out float last) && Time.time - last < 1f) continue;
                ch.lastCarHit[car] = Time.time;
                CarHit(ch, car, v);
                if (ch == null || ch.Dead) break;
            }
        }
    }

    void CarHit(CharacterHealth ch, FlyingVehicle car, float v)
    {
        bool byPlayer = car == FlyingVehicle.Driven;
        Vector3 push = car.Velocity;
        var d = new DamageInfo
        {
            amount = (v - carHitSpeed) * carHitDamage, kind = DamageKind.Car, point = ch.transform.position + Vector3.up,
            velocityChange = push, byPlayer = byPlayer,
        };
        Hits++;
        ch.Damage(d);
        if (ch == null || ch.Dead) return;
        // Survived: knocked about.
        Vector3 flat = new Vector3(push.x, 0f, push.z);
        var o = ch.GetComponent<OfficerAgent>();
        if (o != null) o.Stagger(1.2f, flat.normalized * 1.5f);
        var fpc = ch.GetComponent<FirstPersonController>();
        if (fpc != null) { fpc.Push(push * 0.5f); fpc.Shake(0.6f); }
        if (PedestrianSystem.Find(ch.GetComponent<Collider>()) != null)
            PedestrianSystem.Shot(ch.GetComponent<Collider>(), false, 1.5f, car.transform.position);
        PedestrianSystem.ReportDanger(ch.transform.position, 15f);
    }

    public static int Hits;
}
