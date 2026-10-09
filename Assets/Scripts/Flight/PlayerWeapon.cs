using System;
using UnityEngine;
using UnityEngine.InputSystem;

// One-handed pistol on the player's real right arm.
//   1 draws (arm rises into view), H holsters (pistol back on the right hip, visible in third person).
//   Drawn: LMB fires, RMB steadies (pistol just under the eye line, less sway, slight zoom).
// Every LateUpdate (after the animator posed the body and the camera sits in the head):
//   1. Aim point: the camera's centre ray, first hit that isn't us (300 m).
//   2. Wrist target: from the shoulder toward the aim point at 97% of the arm's reach; pulled back when
//      the aim point is close so the muzzle stays short of it (the elbow bends). Followed through a
//      critically damped spring (12 Hz, 20 Hz steady), so the arm has weight.
//   3. Two-bone IK places the elbow (pole down and out).
//   4. The gun turns in the hand so the barrel ray passes through the aim point (2 iterations, the
//      muzzle moves as it turns), at most 35 degrees off the forearm. Shots travel the actual barrel.
//   5. If the barrel ray hits something more than 0.5 m before the aim point, a hollow marker shows
//      where the shot will really land.
// Drawn = brandishing (police and stealth read Brandishing).
[DefaultExecutionOrder(105)] // after FigureAnimator (50) and FirstPersonController (100) LateUpdates
[RequireComponent(typeof(FirstPersonController))]
public class PlayerWeapon : MonoBehaviour
{
    public float springHz = 12f, steadyHz = 20f;
    public float steadyDrop = 0.08f;       // hand below the eye line when steady
    public float maxWristAngle = 35f;
    public float bodyClearance = 0.25f;    // muzzle stays this far short of a close aim point
    public float steadyZoom = 0.85f;       // fov multiplier

    // A shot connected: collider, point, damage.
    public static event Action<Collider, Vector3, float> ShotHit;
    public static event Action<Vector3> ShotFired;

    public Weapon Gun { get; private set; }
    public bool Drawn { get; private set; }
    public bool Brandishing => Drawn;
    public bool Aiming { get; private set; }     // drawn and the arm is on the aim (not hanging etc.)
    public bool Steady { get; private set; }
    public Vector3 AimPoint { get; private set; }
    public bool Blocked { get; private set; }
    public Vector3 BarrelHit { get; private set; }

    // Test readouts / hooks.
    public static int ShotsFired;
    public static Collider LastShotCollider;
    public static Vector3 LastShotPoint;
    public bool DebugSteady { get; set; }

    FirstPersonController fpc;
    Vector3 handOff, handVel;
    Quaternion gunRot = Quaternion.identity;
    bool gunRotValid;
    float recoil;
    float nextFire;
    float baseFov = -1f, zoomBlend;
    LineRenderer tracer;
    float tracerUntil;
    static Material tracerMat;

    void Awake()
    {
        fpc = GetComponent<FirstPersonController>();
    }

    void Start()
    {
        Gun = Weapon.BuildPistol(transform, gameObject.layer);
        Holster();
    }

    public void Draw()
    {
        if (Drawn || Gun == null) return;
        Drawn = true;
        gunRotValid = false;
        var fig = fpc.Figure;
        // Start the hand where it hangs now, so the arm rises into the aim.
        handOff = fig.WristR.position - fig.ShoulderR.position;
        handVel = Vector3.zero;
        Gun.transform.SetParent(fig.WristR, false);
        Gun.transform.localPosition = new Vector3(0f, -0.04f * fig.Scale, 0f);
        Gun.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
    }

    public void Holster()
    {
        Drawn = false;
        Aiming = false;
        Blocked = false;
        if (Gun == null || fpc.Figure == null) return;
        float s = fpc.Figure.Scale;
        Gun.transform.SetParent(fpc.Figure.Hips, false);
        Gun.transform.localPosition = new Vector3(0.18f, -0.03f, 0.03f) * s;
        Gun.transform.localRotation = Quaternion.Euler(90f, 0f, 0f); // barrel down
    }

    bool CanAim => fpc.isActiveAndEnabled && !fpc.IsHanging && !fpc.Restrained && !fpc.Stunned;

    void Update()
    {
        var kb = Keyboard.current;
        var mouse = Mouse.current;
        if (fpc.Restrained || !fpc.isActiveAndEnabled) { if (Drawn) Holster(); return; }
        bool locked = Cursor.lockState == CursorLockMode.Locked;
        if (kb != null && kb.digit1Key.wasPressedThisFrame) Draw();
        if (kb != null && kb.hKey.wasPressedThisFrame) Holster();
        Aiming = Drawn && CanAim;
        Steady = Aiming && (DebugSteady || (mouse != null && locked && mouse.rightButton.isPressed));
        if (fpc.Animator != null && Aiming) fpc.Animator.ArmMode = FigureAnimator.Arms.Aim;
        if (Aiming && mouse != null && locked && mouse.leftButton.wasPressedThisFrame) Fire();
    }

    void LateUpdate()
    {
        UpdateZoom();
        if (tracer != null && tracer.enabled && Time.time > tracerUntil) tracer.enabled = false;
        if (!Aiming || fpc.Figure == null) { Blocked = false; return; }
        float dt = Time.deltaTime;
        var fig = fpc.Figure;
        var cam = fpc.playerCamera.transform;
        float s = fig.Scale;

        // 1. Aim point.
        AimPoint = AimSolver.AimPoint(cam.position, cam.forward, transform, Gun.range, out _, out _);

        // 2. Wrist target.
        Vector3 S = fig.ShoulderR.position;
        float upper = Vector3.Distance(fig.ShoulderR.position, fig.ElbowR.position);
        float fore = Vector3.Distance(fig.ElbowR.position, fig.WristR.position);
        float reach = upper + fore;
        float handAhead = 0.04f * s;                     // hand centre (the grip) ahead of the wrist
        float gunLen = Gun.Length + handAhead;
        Vector3 toAim = AimPoint - S;
        float d = toAim.magnitude;
        Vector3 dir = d > 1e-4f ? toAim / d : cam.forward;
        float ext = reach * 0.97f;
        Vector3 target;
        if (Steady)
        {
            // On the eye -> aim line, steadyDrop below it, as far out as the arm allows.
            Vector3 eye = cam.position, u = (AimPoint - eye).normalized;
            Vector3 o = eye - cam.up * steadyDrop * s - S;
            float b = Vector3.Dot(o, u), disc = b * b - (o.sqrMagnitude - ext * ext);
            float t = disc > 0f ? -b + Mathf.Sqrt(disc) : 0.4f * s;
            t = Mathf.Min(t, Vector3.Distance(eye, AimPoint) - gunLen - bodyClearance);
            target = eye + u * Mathf.Max(0.15f, t) - cam.up * steadyDrop * s;
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
            float free = Clearance(S, armDir, armLen + gunLen + 0.1f);
            float maxArm = free - gunLen - 0.1f;
            if (maxArm < armLen) target = S + armDir * Mathf.Max(0.2f * s, maxArm);
        }
        // Sway: a little always, more running, almost none steady.
        Vector3 mv = fpc.Animator != null ? fpc.Animator.Velocity : Vector3.zero;
        float run = Mathf.InverseLerp(2f, 6f, new Vector3(mv.x, 0f, mv.z).magnitude);
        float sway = (Steady ? 0.0015f : Mathf.Lerp(0.005f, 0.03f, run)) * s;
        float tt = Time.time * (Steady ? 0.6f : 1.1f + run * 2f);
        target += (cam.right * (Mathf.PerlinNoise(tt, 0.3f) - 0.5f) + cam.up * (Mathf.PerlinNoise(0.7f, tt) - 0.5f)) * 2f * sway;
        AimSolver.Spring(ref handOff, ref handVel, target - S, Steady ? steadyHz : springHz, dt);
        Vector3 wrist = S + handOff;

        // 3. Two-bone IK, pole down and out to the right of the body.
        Vector3 bodyRight = Quaternion.Euler(0f, fpc.Animator != null ? fpc.Animator.BodyYaw : transform.eulerAngles.y, 0f) * Vector3.right;
        Vector3 pole = S + (Vector3.down + bodyRight * 0.6f) * reach;
        Vector3 elbow = AimSolver.TwoBone(S, wrist, upper, fore, pole, out Vector3 reached);
        Vector3 axis = Vector3.Cross(reached - S, pole - S);
        if (axis.sqrMagnitude < 1e-8f) axis = bodyRight;
        axis.Normalize();
        AimSolver.PointBone(fig.ShoulderR, elbow, axis);
        AimSolver.PointBone(fig.ElbowR, reached, axis);

        // 4. Gun: barrel ray through the aim point, from wherever the muzzle ends up.
        Vector3 w = fig.WristR.position;
        Vector3 forearm = (w - fig.ElbowR.position).normalized;
        Vector3 up = cam.up;
        Vector3 m = Gun.muzzle.localPosition + Vector3.forward * handAhead; // muzzle in the gun frame, from the wrist
        Quaternion R = gunRotValid ? gunRot : Quaternion.LookRotation(forearm, up);
        for (int i = 0; i < 2; i++)
        {
            Vector3 muzzle = w + R * m;
            Vector3 f = AimPoint - muzzle;
            if (f.sqrMagnitude > 1e-6f) R = Quaternion.LookRotation(f, up);
        }
        Vector3 fwd = R * Vector3.forward;
        if (Vector3.Angle(forearm, fwd) > maxWristAngle)
            R = Quaternion.LookRotation(Vector3.RotateTowards(forearm, fwd, maxWristAngle * Mathf.Deg2Rad, 0f), up);
        // The barrel carries a little weight too (a fast flick fired early goes where it points).
        float k = 1f - Mathf.Exp(-2f * Mathf.PI * (Steady ? steadyHz : springHz) * 0.6f * dt);
        gunRot = gunRotValid ? Quaternion.Slerp(gunRot, R, k) : R;
        gunRotValid = true;
        recoil = Mathf.Lerp(recoil, 0f, 1f - Mathf.Exp(-14f * dt));
        Quaternion final = gunRot * Quaternion.Euler(-recoil, 0f, 0f);
        fig.WristR.rotation = final * Quaternion.Euler(-90f, 0f, 0f); // hand along the barrel; the gun is its child

        // 5. Where the barrel ray really lands.
        Vector3 mz = Gun.muzzle.position, bf = Gun.muzzle.forward;
        float toAimPoint = Vector3.Distance(mz, AimPoint);
        Blocked = AimSolver.First(mz, bf, toAimPoint + 1f, transform, out var bh) && bh.distance < toAimPoint - 0.5f;
        if (Blocked) BarrelHit = bh.point;
    }

    static readonly RaycastHit[] clearHits = new RaycastHit[16];

    // Free distance along a ray for a gun-thick sphere (solid world only, not us, not triggers).
    float Clearance(Vector3 from, Vector3 dir, float max)
    {
        int n = Physics.SphereCastNonAlloc(from, 0.04f, dir, clearHits, max, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        float best = max;
        for (int i = 0; i < n; i++)
        {
            var c = clearHits[i].collider;
            if (c == null || c.transform.IsChildOf(transform)) continue;
            if (clearHits[i].distance > 0f && clearHits[i].distance < best) best = clearHits[i].distance;
        }
        return best;
    }

    void UpdateZoom()
    {
        var c = fpc.playerCamera;
        if (c == null) return;
        float want = Steady ? 1f : 0f;
        if (want == 0f && zoomBlend == 0f) { baseFov = -1f; return; }
        if (baseFov < 0f) baseFov = c.fieldOfView;
        zoomBlend = Mathf.MoveTowards(zoomBlend, want, Time.deltaTime * 6f);
        c.fieldOfView = baseFov * Mathf.Lerp(1f, steadyZoom, zoomBlend);
        if (zoomBlend == 0f) { c.fieldOfView = baseFov; baseFov = -1f; }
    }

    // ---------- firing ----------

    public void DebugFire() { if (Aiming) Fire(); }

    void Fire()
    {
        if (Time.time < nextFire) return;
        nextFire = Time.time + Gun.fireInterval;
        Vector3 m = Gun.muzzle.position, dir = Gun.muzzle.forward;
        bool hit = AimSolver.First(m, dir, Gun.range, transform, out var h);
        Vector3 end = hit ? h.point : m + dir * Gun.range;
        ShotsFired++;
        LastShotCollider = hit ? h.collider : null;
        LastShotPoint = end;

        Effects.Flash(m + dir * 0.06f, 0.12f);
        ShowTracer(m, end);
        PedestrianSystem.ReportDanger(m, 40f);
        ShotFired?.Invoke(m);
        if (hit)
        {
            Effects.Flash(h.point + h.normal * 0.05f, 0.18f);
            for (int i = 0; i < 4; i++)
                Effects.Debris(h.point + h.normal * 0.05f, (h.normal + UnityEngine.Random.insideUnitSphere * 0.8f) * UnityEngine.Random.Range(2f, 5f), 0.03f);
            float dmg = Gun.damage;
            var part = h.collider.GetComponent<BodyPart>();
            if (part != null)
                dmg *= part.location == BodyPart.Location.Head ? 2f : part.location == BodyPart.Location.Body ? 1f : 0.6f;
            var car = h.collider.GetComponentInParent<FlyingVehicle>();
            if (car != null && car.Health != null) car.Health.Damage(dmg, h.point, h.normal);
            var rb = h.collider.attachedRigidbody;
            if (rb != null && !rb.isKinematic) rb.AddForceAtPosition(dir * 4f, h.point, ForceMode.Impulse);
            ShotHit?.Invoke(h.collider, h.point, dmg);
        }

        // Recoil: barrel up, hand back; the spring brings it home.
        recoil += Gun.recoilPitch;
        handOff -= dir * Gun.recoilBack;
    }

    void ShowTracer(Vector3 a, Vector3 b)
    {
        if (tracer == null)
        {
            if (tracerMat == null)
            {
                tracerMat = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
                tracerMat.SetColor("_BaseColor", new Color(1f, 0.85f, 0.45f) * 3f);
            }
            tracer = new GameObject("Tracer").AddComponent<LineRenderer>();
            tracer.sharedMaterial = tracerMat;
            tracer.widthMultiplier = 0.02f;
            tracer.positionCount = 2;
            tracer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            tracer.receiveShadows = false;
        }
        tracer.SetPosition(0, a);
        tracer.SetPosition(1, b);
        tracer.enabled = true;
        tracerUntil = Time.time + 0.06f;
    }

    // ---------- crosshair and blocked marker ----------

    void OnGUI()
    {
        if (!Aiming) return;
        var prev = GUI.color;
        float cx = Screen.width * 0.5f, cy = Screen.height * 0.5f;
        GUI.color = new Color(1f, 1f, 1f, 0.85f);
        GUI.DrawTexture(new Rect(cx - 2f, cy - 2f, 4f, 4f), Texture2D.whiteTexture);
        if (Blocked)
        {
            var c = fpc.playerCamera;
            Vector3 sp = c.WorldToScreenPoint(BarrelHit);
            if (sp.z > 0f)
            {
                float x = sp.x, y = Screen.height - sp.y, r = 7f, t = 2f;
                GUI.color = new Color(1f, 0.35f, 0.25f, 0.95f);
                GUI.DrawTexture(new Rect(x - r, y - r, 2f * r, t), Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(x - r, y + r - t, 2f * r, t), Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(x - r, y - r, t, 2f * r), Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(x + r - t, y - r, t, 2f * r), Texture2D.whiteTexture);
            }
        }
        GUI.color = prev;
    }
}
