using System;
using UnityEngine;
using UnityEngine.InputSystem;

// Placeholder first-person fists: two cubes in front of the camera. Left mouse = left punch,
// right mouse = right punch. Each punch jabs forward and slightly inward, and at full extension
// sphere-casts for a hit.
//
// Lives on the player (same GameObject as FirstPersonController). The cubes are children of the
// camera root, so they vanish automatically when the player object is deactivated inside a car.
//
// Hooks for the police logic:
//   PunchPressed(hand)   every button press, even when hitting is blocked (used to mash free).
//   PunchHit(collider)   a punch connected with something.
//   CanHit               false while restrained/dragged: the fists still jab (struggle) but don't hit.
[RequireComponent(typeof(FirstPersonController))]
public class PlayerFists : MonoBehaviour
{
    public static event Action<int> PunchPressed;      // 0 = left, 1 = right
    public static event Action<Collider> PunchHit;

    [Header("Look")]
    public Vector3 restOffset = new Vector3(0.24f, -0.26f, 0.45f);   // right hand; left is mirrored
    public Vector3 fistSize = new Vector3(0.11f, 0.11f, 0.2f);
    public Color fistColor = new Color(0.16f, 0.15f, 0.14f);

    [Header("Punch")]
    public float reach = 0.38f;          // how far the fist travels forward
    public float inward = 0.12f;         // drift toward the centre line at full extension
    public float extendTime = 0.07f;
    public float holdTime = 0.04f;
    public float retractTime = 0.16f;
    public float cooldown = 0.3f;        // per hand

    [Header("Hit")]
    public float hitRange = 1.7f;        // from the camera root
    public float hitRadius = 0.3f;
    public float hitForce = 6f;          // impulse on rigidbodies
    public LayerMask hitMask = ~0;

    [Tooltip("False while restrained or dragged: punches still animate (struggle) but don't hit.")]
    public bool CanHit = true;

    FirstPersonController fpc;
    Transform root;
    readonly Transform[] fists = new Transform[2];
    readonly float[] punchStart = { -10f, -10f };
    readonly bool[] hitDone = { true, true };
    static readonly RaycastHit[] hits = new RaycastHit[8];

    float PunchLength => extendTime + holdTime + retractTime;

    void Start()
    {
        fpc = GetComponent<FirstPersonController>();
        root = fpc.cameraRoot != null ? fpc.cameraRoot : fpc.playerCamera.transform.parent;

        var mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        mat.SetColor("_BaseColor", fistColor);
        mat.SetFloat("_Smoothness", 0.2f);

        for (int i = 0; i < 2; i++)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = i == 0 ? "LeftFist" : "RightFist";
            Destroy(go.GetComponent<Collider>());
            go.layer = gameObject.layer;
            var r = go.GetComponent<Renderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            go.transform.SetParent(root, false);
            go.transform.localScale = fistSize;
            go.transform.localPosition = Rest(i);
            fists[i] = go.transform;
        }
    }

    Vector3 Rest(int hand) => new Vector3(hand == 0 ? -restOffset.x : restOffset.x, restOffset.y, restOffset.z);

    void Update()
    {
        if (root == null) return;

        // Only shown in first person; hidden while the camera is zoomed out.
        bool firstPerson = (fpc.playerCamera.transform.position - root.position).sqrMagnitude < 0.25f;
        bool usable = !fpc.IsHanging;
        for (int i = 0; i < 2; i++) fists[i].gameObject.SetActive(firstPerson && usable);
        if (!usable) return;

        var mouse = Mouse.current;
        if (mouse != null && Cursor.lockState == CursorLockMode.Locked)
        {
            if (mouse.leftButton.wasPressedThisFrame) TryPunch(0);
            if (mouse.rightButton.wasPressedThisFrame) TryPunch(1);
        }

        for (int i = 0; i < 2; i++) Animate(i);
    }

    void TryPunch(int hand)
    {
        PunchPressed?.Invoke(hand); // always reported, so mashing works even mid-punch
        if (Time.time - punchStart[hand] < cooldown) return;
        punchStart[hand] = Time.time;
        hitDone[hand] = false;
    }

    void Animate(int hand)
    {
        float t = Time.time - punchStart[hand];
        float k; // 0 = rest, 1 = full extension
        if (t < extendTime) k = EaseOut(t / extendTime);
        else if (t < extendTime + holdTime) k = 1f;
        else if (t < PunchLength) k = 1f - EaseInOut((t - extendTime - holdTime) / retractTime);
        else k = 0f;

        float side = hand == 0 ? 1f : -1f; // toward the centre
        Vector3 pos = Rest(hand) + new Vector3(side * inward * k, 0.04f * k, reach * k);
        fists[hand].localPosition = pos;
        fists[hand].localRotation = Quaternion.Euler(0f, -side * 8f * k, side * 20f * k);

        if (!hitDone[hand] && t >= extendTime)
        {
            hitDone[hand] = true;
            if (CanHit) DoHit();
        }
    }

    void DoHit()
    {
        Vector3 origin = root.position;
        Vector3 dir = root.forward;
        int n = Physics.SphereCastNonAlloc(origin, hitRadius, dir, hits, hitRange, hitMask, QueryTriggerInteraction.Ignore);

        Collider best = null;
        float bestDist = float.MaxValue;
        Vector3 bestPoint = default;
        for (int i = 0; i < n; i++)
        {
            var c = hits[i].collider;
            if (c == null || c.transform.IsChildOf(transform)) continue; // ignore ourselves
            if (hits[i].distance < bestDist) { bestDist = hits[i].distance; best = c; bestPoint = hits[i].point; }
        }
        if (best == null) return;

        var rb = best.attachedRigidbody;
        if (rb != null && !rb.isKinematic)
            rb.AddForceAtPosition(dir * hitForce, bestPoint == Vector3.zero ? best.bounds.center : bestPoint, ForceMode.Impulse);

        PunchHit?.Invoke(best);
    }

    static float EaseOut(float x) => 1f - (1f - x) * (1f - x);
    static float EaseInOut(float x) => x * x * (3f - 2f * x);
}
