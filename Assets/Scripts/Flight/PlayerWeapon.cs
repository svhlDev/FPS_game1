using System;
using UnityEngine;
using UnityEngine.InputSystem;

// The player's T-gun on the real right arm.
//   1 draws (arm rises into view), H holsters (gun back on the right hip, visible in third person).
//   Drawn: LMB fires, RMB steadies (gun just under the eye line, less sway, slight zoom), B toggles
//   Stun / Lethal (0.3 s before it fires again, a short tone; the strips and emitter change colour).
// Every LateUpdate (after the animator posed the body and the camera sits in the head):
//   1. Aim point: the camera's centre ray, first hit that isn't us (the mode's range).
//   2. ArmAim: arm IK toward it, gun turned so the barrel ray passes through it (AimSolver.cs).
//   3. If the barrel ray hits something more than 0.5 m before the aim point, a hollow marker shows
//      where the shot will really land.
// Shots travel the actual barrel (LaserWeapon.Fire). Stun: people down stunTime s, cars hiccup (thrust
// cut, lights flicker), no damage. Lethal: damage (head x2, body x1, limbs x0.6), cars through
// VehicleHealth.
// Drawn = brandishing (police see the mode: PoliceDispatch).
[DefaultExecutionOrder(105)] // after FigureAnimator (50) and FirstPersonController (100) LateUpdates
[RequireComponent(typeof(FirstPersonController))]
public class PlayerWeapon : MonoBehaviour
{
    public float steadyZoom = 0.85f;       // fov multiplier
    public float switchDelay = 0.3f;

    // A shot connected: collider, point, damage (0 for stun), mode.
    public static event Action<Collider, Vector3, float, Weapon.Mode> ShotHit;
    public static event Action<Vector3, Weapon.Mode> ShotFired;

    public Weapon Gun { get; private set; }
    public Weapon.Mode Mode => Gun != null ? Gun.CurrentMode : Weapon.Mode.Stun;
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
    readonly ArmAim arm = new ArmAim();
    float nextFire;
    float baseFov = -1f, zoomBlend;
    AudioSource audioSource;
    static AudioClip toneStun, toneLethal;

    void Awake()
    {
        fpc = GetComponent<FirstPersonController>();
    }

    void Start()
    {
        Gun = Weapon.BuildTGun(transform, gameObject.layer, Weapon.Mode.Stun);
        Holster();
    }

    public void Draw()
    {
        if (Drawn || Gun == null) return;
        Drawn = true;
        arm.Begin(fpc.Figure);
        ArmAim.Attach(Gun, fpc.Figure);
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

    // The body is about to be rebuilt (new stats): park the gun on the root, then put it back.
    public void DetachGun() { if (Gun != null) Gun.transform.SetParent(transform, true); }
    public void ReattachGun()
    {
        if (Gun == null) return;
        if (Drawn) { arm.Begin(fpc.Figure); ArmAim.Attach(Gun, fpc.Figure); }
        else Holster();
    }

    public void ToggleMode()
    {
        if (Gun == null) return;
        Gun.SetMode(Gun.CurrentMode == Weapon.Mode.Stun ? Weapon.Mode.Lethal : Weapon.Mode.Stun);
        nextFire = Mathf.Max(nextFire, Time.time + switchDelay);
        Tone(Gun.CurrentMode);
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
        if (kb != null && kb.bKey.wasPressedThisFrame && Drawn) ToggleMode();
        Aiming = Drawn && CanAim;
        Steady = Aiming && (DebugSteady || (mouse != null && locked && mouse.rightButton.isPressed));
        if (fpc.Animator != null && Aiming) fpc.Animator.ArmMode = FigureAnimator.Arms.Aim;
        if (Aiming && mouse != null && locked && mouse.leftButton.wasPressedThisFrame) Fire();
    }

    void LateUpdate()
    {
        UpdateZoom();
        if (!Aiming || fpc.Figure == null) { Blocked = false; return; }
        float dt = Time.deltaTime;
        var fig = fpc.Figure;
        var cam = fpc.playerCamera.transform;
        float s = fig.Scale;

        AimPoint = AimSolver.AimPoint(cam.position, cam.forward, transform, Gun.Range, out _, out _);

        // Sway: a little always, more running, almost none steady.
        Vector3 mv = fpc.Animator != null ? fpc.Animator.Velocity : Vector3.zero;
        float run = Mathf.InverseLerp(2f, 6f, new Vector3(mv.x, 0f, mv.z).magnitude);
        float amp = (Steady ? 0.0015f : Mathf.Lerp(0.005f, 0.03f, run)) * s;
        float tt = Time.time * (Steady ? 0.6f : 1.1f + run * 2f);
        Vector3 sway = (cam.right * (Mathf.PerlinNoise(tt, 0.3f) - 0.5f) + cam.up * (Mathf.PerlinNoise(0.7f, tt) - 0.5f)) * 2f * amp;

        float bodyYaw = fpc.Animator != null ? fpc.Animator.BodyYaw : transform.eulerAngles.y;
        arm.Solve(fig, Gun, AimPoint, transform, bodyYaw, cam.up, Steady ? cam.position : (Vector3?)null, sway, dt);

        // Where the barrel ray really lands.
        Vector3 mz = Gun.muzzle.position, bf = Gun.muzzle.forward;
        float toAimPoint = Vector3.Distance(mz, AimPoint);
        Blocked = AimSolver.First(mz, bf, toAimPoint + 1f, transform, out var bh) && bh.distance < toAimPoint - 0.5f;
        if (Blocked) BarrelHit = bh.point;
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

    public bool DebugFire() { if (!Aiming || Time.time < nextFire) return false; Fire(); return true; }

    void Fire()
    {
        if (Time.time < nextFire) return;
        nextFire = Time.time + Gun.FireInterval;
        var mode = Gun.CurrentMode;
        Vector3 m = Gun.muzzle.position, dir = Gun.muzzle.forward;
        bool hit = LaserWeapon.Fire(m, dir, Gun.Range, transform, mode, out var h);
        ShotsFired++;
        LastShotCollider = hit ? h.collider : null;
        LastShotPoint = hit ? h.point : m + dir * Gun.Range;
        ShotFired?.Invoke(m, mode);
        if (hit) Apply(h, dir, mode);
        arm.Kick(dir, Gun);
    }

    // What a shot does to what it hit.
    void Apply(RaycastHit h, Vector3 dir, Weapon.Mode mode)
    {
        var c = h.collider;
        float dmg = 0f;
        var car = c.GetComponentInParent<FlyingVehicle>();
        if (mode == Weapon.Mode.Stun)
        {
            if (car != null) car.Hiccup(Gun.carHiccup);
            var officer = c.GetComponentInParent<OfficerAgent>();
            if (officer != null) officer.Stun(Gun.stunTime);
            PedestrianSystem.Shot(c, false, Gun.stunTime, transform.position);
        }
        else
        {
            dmg = Gun.damage;
            var part = c.GetComponent<BodyPart>();
            if (part != null)
                dmg *= part.location == BodyPart.Location.Head ? 2f : part.location == BodyPart.Location.Body ? 1f : 0.6f;
            if (car != null && car.Health != null) { dmg = Gun.carDamage; car.Health.Damage(dmg, h.point, h.normal); }
            PedestrianSystem.Shot(c, true, 0f, transform.position);
            var rb = c.attachedRigidbody;
            if (rb != null && !rb.isKinematic) rb.AddForceAtPosition(dir * 4f, h.point, ForceMode.Impulse);
        }
        ShotHit?.Invoke(c, h.point, dmg, mode);
    }

    // Placeholder switch tone: a short beep, higher for stun.
    void Tone(Weapon.Mode mode)
    {
        if (audioSource == null)
        {
            audioSource = gameObject.AddComponent<AudioSource>();
            audioSource.playOnAwake = false;
            audioSource.spatialBlend = 0f;
            audioSource.volume = 0.25f;
        }
        ref AudioClip clip = ref (mode == Weapon.Mode.Stun ? ref toneStun : ref toneLethal);
        if (clip == null)
        {
            const int rate = 44100;
            int n = rate / 12;
            float f = mode == Weapon.Mode.Stun ? 1320f : 660f;
            var data = new float[n];
            for (int i = 0; i < n; i++) data[i] = Mathf.Sin(2f * Mathf.PI * f * i / rate) * Mathf.Clamp01((n - i) / (n * 0.3f));
            clip = AudioClip.Create("Tone" + mode, n, 1, rate, false);
            clip.SetData(data, 0);
        }
        audioSource.PlayOneShot(clip);
    }

    // ---------- crosshair, mode label and blocked marker ----------

    void OnGUI()
    {
        if (!Aiming) return;
        var prev = GUI.color;
        float cx = Screen.width * 0.5f, cy = Screen.height * 0.5f;
        GUI.color = new Color(1f, 1f, 1f, 0.85f);
        GUI.DrawTexture(new Rect(cx - 2f, cy - 2f, 4f, 4f), Texture2D.whiteTexture);
        Color mc = Color.Lerp(LaserWeapon.ColorOf(Mode), Color.white, 0.35f);
        GUI.color = new Color(mc.r, mc.g, mc.b, 0.95f);
        GUI.Label(new Rect(cx + 14f, cy + 6f, 80f, 20f), Mode == Weapon.Mode.Stun ? "STUN" : "LETHAL");
        if (Blocked)
        {
            var c = fpc.playerCamera;
            Vector3 sp = c.WorldToScreenPoint(BarrelHit);
            if (sp.z > 0f)
            {
                float x = sp.x, y = Screen.height - sp.y, r = 7f, t = 2f;
                GUI.color = new Color(1f, 0.85f, 0.3f, 0.95f);
                GUI.DrawTexture(new Rect(x - r, y - r, 2f * r, t), Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(x - r, y + r - t, 2f * r, t), Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(x - r, y - r, t, 2f * r), Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(x + r - t, y - r, t, 2f * r), Texture2D.whiteTexture);
            }
        }
        GUI.color = prev;
    }
}
