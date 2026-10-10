using System;
using UnityEngine;
using UnityEngine.InputSystem;

// The player's fists are the body's real hands (CharacterFigure / FigureAnimator); no camera-glued cubes.
//   Arms are lowered by default (out of view when looking ahead). Any punch, or the guard key (Q),
//   raises them into a guard; they drop again lowerAfter seconds after the last combat action, or at
//   once on Q (holster). Raised arms are visible to everyone.
//   Left mouse = left hand, right mouse = right hand. A punch is a straight jab along the view
//   (FigureAnimator's IK); the hit check is a 0.12 m sphere at the posed fist the moment it reaches full
//   extension (hit colliders included: BodyPart triggers). 8 damage, 12 to the head (CharacterHealth).
//   Restrained: the hands are cuffed behind the back; presses still count (mashing free) and make the
//   arms strain, but nothing is hit.
// Hooks for the police logic:
//   PunchPressed(hand)   every button press, even when hitting is blocked (used to mash free).
//   PunchHit(collider)   a punch connected with something.
//   CanHit               false while restrained/dragged/stunned (set by FirstPersonController).
[RequireComponent(typeof(FirstPersonController))]
public class PlayerFists : MonoBehaviour
{
    public static event Action<int> PunchPressed;      // 0 = left, 1 = right
    public static event Action<Collider> PunchHit;

    [Header("Punch")]
    public float cooldown = 0.3f;        // per hand
    [Tooltip("Lowered arms come up and stay up this long after the last punch or guard.")]
    public float lowerAfter = 4f;
    [Tooltip("Radius of the hit check around the fully extended fist.")]
    public float hitRadius = 0.12f;
    public float hitForce = 6f;          // impulse on rigidbodies
    public LayerMask hitMask = ~0;

    [Tooltip("False while restrained or dragged: punches still animate (struggle) but don't hit.")]
    public bool CanHit = true;

    public bool ArmsRaised { get; private set; }

    FirstPersonController fpc;
    PlayerWeapon weapon;
    readonly float[] punchStart = { -10f, -10f };
    readonly bool[] hitDone = { true, true };
    float lastCombat = -100f, lastPress = -100f;
    static readonly Collider[] hits = new Collider[16];
    FigureAnimator subscribed;

    void Start()
    {
        fpc = GetComponent<FirstPersonController>();
        weapon = GetComponent<PlayerWeapon>();
    }

    // The animator reports each jab at full extension, where the fist really is.
    void Subscribe()
    {
        var anim = fpc != null ? fpc.Animator : null;
        if (anim == subscribed) return;
        if (subscribed != null) subscribed.PunchExtended -= OnExtended;
        subscribed = anim;
        if (anim != null) anim.PunchExtended += OnExtended;
    }

    void OnExtended(int hand, Vector3 fist)
    {
        if (hitDone[hand]) return;
        hitDone[hand] = true;
        if (CanHit) DoHit(hand, fist);
    }

    // Test hook: guard up as if a punch was just thrown.
    public void DebugRaise() { ArmsRaised = true; lastCombat = Time.time; }
    public void DebugPunch(int hand) { if (fpc != null && fpc.Animator != null) TryPunch(hand, fpc.Animator); }
    public static int HitsLanded;
    static void CountHit(Collider c) => HitsLanded++;
    void OnEnable() => PunchHit += CountHit;
    void OnDisable()
    {
        PunchHit -= CountHit;
        if (subscribed != null) { subscribed.PunchExtended -= OnExtended; subscribed = null; }
    }

    void Update()
    {
        var anim = fpc != null ? fpc.Animator : null;
        if (anim == null) return;
        Subscribe();
        var mouse = Mouse.current;
        var kb = Keyboard.current;
        bool usable = !fpc.IsHanging;
        // Pistol drawn: the mouse buttons fire / steady instead (PlayerWeapon), the fists stay down.
        if (weapon != null && weapon.Drawn)
        {
            ArmsRaised = false;
            anim.ArmMode = FigureAnimator.Arms.Lowered;
            anim.Straining = false;
            return;
        }

        if (mouse != null && Cursor.lockState == CursorLockMode.Locked && usable)
        {
            if (mouse.leftButton.wasPressedThisFrame) TryPunch(0, anim);
            if (mouse.rightButton.wasPressedThisFrame) TryPunch(1, anim);
        }
        if (kb != null && kb.qKey.wasPressedThisFrame && CanHit)
        {
            ArmsRaised = !ArmsRaised; // guard up / holster
            lastCombat = Time.time;
        }
        if (ArmsRaised && Time.time - lastCombat > lowerAfter) ArmsRaised = false;
        if (!CanHit) ArmsRaised = false;

        anim.ArmMode = ArmsRaised ? FigureAnimator.Arms.Guard : FigureAnimator.Arms.Lowered;
        anim.Straining = fpc.Restrained && Time.time - lastPress < 0.3f;
    }

    void TryPunch(int hand, FigureAnimator anim)
    {
        PunchPressed?.Invoke(hand); // always reported, so mashing works even mid-punch
        lastPress = Time.time;
        if (!CanHit) return;
        ArmsRaised = true;
        lastCombat = Time.time;
        if (Time.time - punchStart[hand] < cooldown) return;
        punchStart[hand] = Time.time;
        hitDone[hand] = false;
        anim.Punch(hand);
    }

    // A 0.12 m sphere at the fully extended fist: the nearest thing that isn't us (body-part triggers
    // count, other triggers don't).
    void DoHit(int hand, Vector3 fist)
    {
        Vector3 dir = fpc.playerCamera.transform.forward;
        int n = Physics.OverlapSphereNonAlloc(fist, hitRadius, hits, hitMask, QueryTriggerInteraction.Collide);
        Collider best = null;
        float bestDist = float.MaxValue;
        for (int i = 0; i < n; i++)
        {
            var c = hits[i];
            if (c == null || c.transform.IsChildOf(transform)) continue; // ourselves
            if (c.isTrigger && c.GetComponent<BodyPart>() == null) continue; // other triggers aren't things to hit
            Vector3 cp = Closest(c, fist);
            float d = (cp - fist).sqrMagnitude;
            if (d < bestDist) { bestDist = d; best = c; }
        }
        LastFist = fist;
        if (best == null) return;
        Vector3 point = Closest(best, fist);

        var rb = best.attachedRigidbody;
        if (rb != null && !rb.isKinematic)
            rb.AddForceAtPosition(dir * hitForce, point, ForceMode.Impulse);
        PunchHit?.Invoke(best);                                   // police react to the victim still standing
        CharacterHealth.PunchHit(best, point, dir, true);
    }

    public static Vector3 LastFist;

    // Collider.ClosestPoint handles primitives and convex meshes only.
    static Vector3 Closest(Collider c, Vector3 p) =>
        c is BoxCollider || c is SphereCollider || c is CapsuleCollider || (c is MeshCollider m && m.convex) ? c.ClosestPoint(p) : c.bounds.ClosestPoint(p);
}
