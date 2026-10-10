using UnityEngine;
using UnityEngine.InputSystem;

// The player's grenades (carry up to 3; police trunks hold them).
//   Hold G: ready - the right arm draws back and an arc shows the predicted path and the landing point.
//   Release G: overhand throw at 14 m/s along the view pitched up 8 degrees, plus the player's own
//   velocity. A tap throws at once. Fuse: 3 s from release (Grenade).
// A gun in the hand is holstered first. Thrown at or near police: lethal force
// (PoliceDispatch.ReportGrenadeThrown).
[DefaultExecutionOrder(106)] // after PlayerWeapon: the arm pose is ours while readying
[RequireComponent(typeof(FirstPersonController))]
public class PlayerGrenades : MonoBehaviour
{
    public const int MaxGrenades = 3;
    public const float ThrowSpeed = 14f, ThrowPitch = 8f;

    public int Count { get; private set; }
    public bool Readying { get; private set; }
    public Vector3 PredictedLanding { get; private set; }
    public bool PredictedHit { get; private set; }

    FirstPersonController fpc;
    PlayerWeapon weapon;
    LineRenderer arc;
    Transform marker;
    Vector3 lastPos, velocity;
    bool hasLast;
    const int ArcPoints = 60;
    readonly Vector3[] points = new Vector3[ArcPoints];
    static readonly RaycastHit[] hits = new RaycastHit[8];
    static Material arcMat;

    public static int Thrown;

    void Awake()
    {
        fpc = GetComponent<FirstPersonController>();
        weapon = GetComponent<PlayerWeapon>();
    }

    // Picked one up. False when already carrying the maximum.
    public bool Add(int n = 1)
    {
        if (Count >= MaxGrenades) return false;
        Count = Mathf.Min(MaxGrenades, Count + n);
        return true;
    }

    void Update()
    {
        float dt = Time.deltaTime;
        if (dt > 0f)
        {
            Vector3 p = transform.position;
            if (hasLast) velocity = Vector3.Lerp(velocity, (p - lastPos) / dt, 0.3f);
            lastPos = p; hasLast = true;
        }
        var kb = Keyboard.current;
        bool can = fpc.isActiveAndEnabled && !fpc.Restrained && !fpc.Stunned && !fpc.IsHanging && !fpc.Frozen;
        if (!can) { Cancel(); return; }
        if (kb == null) return;
        if (kb.gKey.wasPressedThisFrame)
        {
            if (Count <= 0) fpc.Flash("No grenades");
            else
            {
                if (weapon != null && weapon.Drawn) weapon.Holster();
                Readying = true;
            }
        }
        if (Readying && fpc.Animator != null) fpc.Animator.ThrowReady = true;
        if (Readying && kb.gKey.wasReleasedThisFrame) Release();
    }

    void LateUpdate()
    {
        bool show = Readying;
        if (show)
        {
            Predict(LaunchPosition(), LaunchVelocity());
            EnsureArc();
        }
        if (arc != null) arc.enabled = show;
        if (marker != null) marker.gameObject.SetActive(show && PredictedHit);
    }

    void Cancel()
    {
        Readying = false;
        if (fpc != null && fpc.Animator != null) fpc.Animator.ThrowReady = false;
    }

    Transform Cam => fpc.playerCamera.transform;

    public Vector3 LaunchVelocity()
    {
        Vector3 dir = Quaternion.AngleAxis(-ThrowPitch, Cam.right) * Cam.forward;
        return dir * ThrowSpeed + velocity;
    }

    // In front of the eyes, a little right and below (clear of our own body).
    public Vector3 LaunchPosition()
    {
        var root = fpc.cameraRoot;
        return root.position + Cam.forward * 0.45f + Cam.right * 0.15f - Vector3.up * 0.05f;
    }

    void Release()
    {
        Readying = false;
        if (Count <= 0) { Cancel(); return; }
        Count--;
        Vector3 p = LaunchPosition(), v = LaunchVelocity();
        Predict(p, v);
        Grenade.Throw(p, v, GetComponent<CharacterController>(), true);
        if (fpc.Animator != null) fpc.Animator.Throw();
        Thrown++;
        PoliceDispatch.Instance?.ReportGrenadeThrown(transform.position, PredictedLanding);
    }

    // Test hooks: hold G (arc showing), a throw at the current view (as a release of G).
    public void DebugReady() { if (Count > 0) Readying = true; }
    public bool DebugThrow()
    {
        if (Count <= 0) return false;
        Release();
        return true;
    }

    // Ballistic path to the first thing it meets (not us, not triggers), up to 4 s.
    void Predict(Vector3 p, Vector3 v)
    {
        Vector3 g = Physics.gravity;
        const float step = 4f / ArcPoints;
        int n = 0;
        PredictedHit = false;
        points[n++] = p;
        for (int i = 1; i < ArcPoints; i++)
        {
            Vector3 next = p + v * step + 0.5f * g * step * step;
            v += g * step;
            Vector3 d = next - p;
            float len = d.magnitude;
            if (len > 1e-5f && First(p, d / len, len, out var h))
            {
                points[n++] = h.point;
                PredictedLanding = h.point;
                PredictedHit = true;
                if (marker != null) { marker.position = h.point + h.normal * 0.02f; marker.rotation = Quaternion.LookRotation(-h.normal); }
                break;
            }
            points[n++] = next;
            p = next;
        }
        if (!PredictedHit) PredictedLanding = points[n - 1];
        if (arc != null) { arc.positionCount = n; arc.SetPositions(points); }
    }

    bool First(Vector3 o, Vector3 dir, float len, out RaycastHit best)
    {
        int n = Physics.RaycastNonAlloc(o, dir, hits, len, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        best = default; float bd = float.MaxValue;
        for (int i = 0; i < n; i++)
        {
            if (hits[i].collider.transform.IsChildOf(transform)) continue;
            if (hits[i].distance < bd) { bd = hits[i].distance; best = hits[i]; }
        }
        return bd < float.MaxValue;
    }

    void EnsureArc()
    {
        if (arc != null) return;
        if (arcMat == null)
        {
            arcMat = new Material(Shader.Find("Universal Render Pipeline/Unlit")) { name = "Grenade arc" };
            arcMat.SetColor("_BaseColor", new Color(1f, 0.55f, 0.15f) * 1.6f);
        }
        arc = new GameObject("GrenadeArc").AddComponent<LineRenderer>();
        arc.sharedMaterial = arcMat;
        arc.widthMultiplier = 0.025f;
        arc.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        arc.receiveShadows = false;
        var m = GameObject.CreatePrimitive(PrimitiveType.Quad);
        Destroy(m.GetComponent<Collider>());
        m.name = "GrenadeLanding";
        m.GetComponent<Renderer>().sharedMaterial = arcMat;
        m.transform.localScale = Vector3.one * 0.5f;
        marker = m.transform;
        Predict(LaunchPosition(), LaunchVelocity());
    }

    void OnDestroy()
    {
        if (arc != null) Destroy(arc.gameObject);
        if (marker != null) Destroy(marker.gameObject);
    }

    void OnGUI()
    {
        if (Count <= 0 && !Readying) return;
        GUI.Label(new Rect(Screen.width - 160, Screen.height - 30, 150, 25), $"GRENADES  {Count}" + (Readying ? "   (release G)" : ""));
    }
}
