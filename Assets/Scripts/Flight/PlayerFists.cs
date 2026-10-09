using System;
using UnityEngine;
using UnityEngine.InputSystem;

// The player's fists are the body's real hands (CharacterFigure / FigureAnimator); no camera-glued cubes.
//   Arms are lowered by default (out of view when looking ahead). Any punch, or the guard key (Q),
//   raises them into a guard; they drop again lowerAfter seconds after the last combat action, or at
//   once on Q (holster). Raised arms are visible to everyone.
//   Left mouse = left hand, right mouse = right hand. The punch travels the real arm, and the hit check
//   runs from the shoulder along the aim at full extension (hit colliders included: BodyPart triggers).
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
    [Tooltip("Reach past the hand (a punch lands a little beyond the fully extended fist).")]
    public float extraReach = 0.35f;
    public float hitRadius = 0.22f;
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
    static readonly RaycastHit[] hits = new RaycastHit[16];

    void Start() { fpc = GetComponent<FirstPersonController>(); weapon = GetComponent<PlayerWeapon>(); }

    // Test hook: guard up as if a punch was just thrown.
    public void DebugRaise() { ArmsRaised = true; lastCombat = Time.time; }
    public void DebugPunch(int hand) { if (fpc != null && fpc.Animator != null) TryPunch(hand, fpc.Animator); }
    public static int HitsLanded;
    static void CountHit(Collider c) => HitsLanded++;
    void OnEnable() => PunchHit += CountHit;
    void OnDisable() => PunchHit -= CountHit;

    void Update()
    {
        var anim = fpc != null ? fpc.Animator : null;
        if (anim == null) return;
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

        for (int h = 0; h < 2; h++)
            if (!hitDone[h] && anim.PunchAmount(h) >= 0.99f)
            {
                hitDone[h] = true;
                if (CanHit) DoHit(h);
            }
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

    // From the shoulder along the aim, as far as the arm reaches (plus a little): the nearest thing
    // that isn't us. Body-part triggers count.
    void DoHit(int hand)
    {
        var fig = fpc.Figure;
        Transform shoulder = hand == 0 ? fig.ShoulderL : fig.ShoulderR;
        Vector3 origin = shoulder.position;
        Vector3 dir = fpc.playerCamera.transform.forward;
        float reach = fig.ArmLength + extraReach;
        int n = Physics.SphereCastNonAlloc(origin, hitRadius, dir, hits, reach, hitMask, QueryTriggerInteraction.Collide);

        Collider best = null;
        float bestDist = float.MaxValue;
        Vector3 bestPoint = default;
        for (int i = 0; i < n; i++)
        {
            var c = hits[i].collider;
            if (c == null || c.transform.IsChildOf(transform)) continue; // ourselves
            if (c.isTrigger && c.GetComponent<BodyPart>() == null) continue; // other triggers aren't things to hit
            float d = hits[i].distance;
            if (d < bestDist) { bestDist = d; best = c; bestPoint = hits[i].point; }
        }
        if (best == null) return;

        var rb = best.attachedRigidbody;
        if (rb != null && !rb.isKinematic)
            rb.AddForceAtPosition(dir * hitForce, bestPoint == Vector3.zero ? best.bounds.center : bestPoint, ForceMode.Impulse);
        PunchHit?.Invoke(best);
    }
}
