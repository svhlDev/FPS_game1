using System;
using UnityEngine;
using UnityEngine.InputSystem;

// The player's weapons on the real right arm. The player starts unarmed (fists); T-guns come from dead
// officers (GunPickup, E), everything else from the police armory rack (WeaponRack, E). startWithGun
// gives a T-gun at the start, startWithArsenal every weapon (testing).
// Inventory: one slot per kind, the number keys pick them:
//   1 T-gun   2 Scatter laser   3 Arc caster   4 Limpet launcher   5 Repulsor   6 Singularity gun
//   A number key draws that weapon (the one in hand is stowed; the mouse wheel stays the camera zoom),
//   H holsters (the current weapon goes on the right hip, visible in third person).
// Drawn:
//   LMB fires (Repulsor: hold to charge, release to blast), RMB steadies (gun just under the eye line,
//   less sway, slight zoom) except on the Limpet launcher, where RMB sets off every stuck charge.
//   B toggles Stun / Lethal on the lasers (T-gun, Scatter): 0.3 s before it fires again, a short tone.
//   X sets off the limpet charges from anywhere, even from a car.
// Ammo: the T-gun is unlimited; the others carry cells (Weapon.maxAmmo), refilled at the armory rack.
// Every LateUpdate (after the animator posed the body and the camera sits in the head):
//   1. Aim point: the camera's centre ray, first hit that isn't us (the weapon's range).
//   2. ArmAim: arm IK toward it, gun turned so the barrel ray passes through it (AimSolver.cs).
//   3. If the barrel ray hits something more than 0.5 m before the aim point, a hollow marker shows
//      where the shot will really land.
// Shots travel the actual barrel. T-gun / Scatter: LaserWeapon.Fire per beam. Stun: people down
// stunTime s, cars hiccup, no damage. Lethal: damage (CharacterHealth: head x2, body x1, limbs x0.6), cars
// through VehicleHealth. The others: WeaponSpecials.cs. Every hit is reported (ShotHit) so the police
// react the same way to all of them; the non-laser weapons always count as lethal.
// Drawn = brandishing (police see the mode: PoliceDispatch).
[DefaultExecutionOrder(105)] // after FigureAnimator (50) and FirstPersonController (100) LateUpdates
[RequireComponent(typeof(FirstPersonController))]
public class PlayerWeapon : MonoBehaviour
{
    [Tooltip("Start with a T-gun (testing); otherwise fists until one is picked up.")]
    public bool startWithGun = false;
    [Tooltip("Start with every weapon, full ammo (testing).")]
    public bool startWithArsenal = false;
    public static bool ForceStartWithGun;  // ScenarioTest: the older scenarios expect a gun
    public float steadyZoom = 0.85f;       // fov multiplier
    public float switchDelay = 0.3f;
    [Tooltip("Seconds of LMB hold for a full Repulsor charge.")]
    public float repulsorChargeTime = 0.9f;
    [Tooltip("Upward launch speed of a full Repulsor blast fired at your feet.")]
    public float repulsorJump = 14f;
    public const int ScatterBeams = 9;
    public const float ScatterSpread = 5f;

    // A shot connected: collider, point, damage (0 for stun), mode.
    public static event Action<Collider, Vector3, float, Weapon.Mode> ShotHit;
    public static event Action<Vector3, Weapon.Mode> ShotFired;

    // The weapon in hand (drawn) or on the hip; null when unarmed.
    public Weapon Gun { get; private set; }
    public Weapon.Mode Mode => Gun != null ? Gun.CurrentMode : Weapon.Mode.Stun;
    public Weapon.Kind Current => Gun != null ? Gun.kind : Weapon.Kind.TGun;
    public bool Drawn { get; private set; }
    public bool Brandishing => Drawn;
    public bool Aiming { get; private set; }     // drawn and the arm is on the aim (not hanging etc.)
    public bool Steady { get; private set; }
    public Vector3 AimPoint { get; private set; }
    public bool Blocked { get; private set; }
    public Vector3 BarrelHit { get; private set; }
    public bool Charging { get; private set; }
    public float Charge => Charging ? Mathf.Clamp01((Time.time - chargeStart) / Mathf.Max(0.05f, repulsorChargeTime)) : 0f;

    // Test readouts / hooks.
    public static int ShotsFired;
    public static Collider LastShotCollider;
    public static Vector3 LastShotPoint;
    public bool DebugSteady { get; set; }

    FirstPersonController fpc;
    readonly ArmAim arm = new ArmAim();
    readonly Weapon[] slots = new Weapon[Weapon.KindCount];
    float nextFire, chargeStart, lastSwitch = -10f;
    float baseFov = -1f, zoomBlend;
    Vector3 lastPos, velocity;
    bool hasLastPos;
    AudioSource audioSource;
    static AudioClip toneStun, toneLethal, clickEmpty;
    static readonly AudioClip[] fireClips = new AudioClip[Weapon.KindCount];

    void Awake()
    {
        fpc = GetComponent<FirstPersonController>();
    }

    void Start()
    {
        if (startWithGun || ForceStartWithGun || startWithArsenal) Add(Weapon.BuildTGun(transform, gameObject.layer, Weapon.Mode.Stun));
        if (startWithArsenal)
            for (int k = 1; k < Weapon.KindCount; k++) Add(Weapon.Build((Weapon.Kind)k, transform, gameObject.layer));
        Holster();
    }

    // ---------- inventory ----------

    public bool Has(Weapon.Kind k) => slots[(int)k] != null;
    public Weapon Get(Weapon.Kind k) => slots[(int)k];
    // A T-gun of our own (dropped T-guns are only picked up without one).
    public bool HasGun => Has(Weapon.Kind.TGun);
    public bool Armed => Gun != null;
    public static int SlotKey(Weapon.Kind k) => (int)k + 1;

    // Into its slot (a second one of a kind just refills the first). The first weapon goes on the hip.
    void Add(Weapon w)
    {
        if (w == null) return;
        foreach (var t in w.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = gameObject.layer;
        int i = (int)w.kind;
        if (slots[i] != null && slots[i] != w)
        {
            slots[i].Refill();
            Destroy(w.gameObject);
            return;
        }
        slots[i] = w;
        w.ShowAmmo();
        if (Gun == null) { Gun = w; w.gameObject.SetActive(true); Holster(); }
        else if (Gun != w) Stow(w);
    }

    // A dropped T-gun picked up: on the hip if unarmed, 1 to draw. Ignored if we already have one.
    public bool Give(GunPickup pickup)
    {
        if (HasGun || pickup == null) return false;
        var gun = pickup.Take();
        if (gun == null) return false;
        Add(gun);
        fpc.Flash("Picked up a T-gun  -  1 to draw");
        return true;
    }

    // From the armory: a new weapon of that kind (true), or a refill of the one we have (false).
    public bool GiveKind(Weapon.Kind k)
    {
        var have = Get(k);
        if (have != null)
        {
            have.Refill();
            have.ShowAmmo();
            fpc.Flash($"{have.DisplayName} refilled");
            return false;
        }
        Add(Weapon.Build(k, transform, gameObject.layer));
        fpc.Flash($"Took the {Weapon.NameOf(k)}  -  {SlotKey(k)} to draw");
        lastSwitch = Time.time;
        return true;
    }

    public void RefillAll()
    {
        foreach (var w in slots) if (w != null) { w.Refill(); w.ShowAmmo(); }
    }

    // Out of sight on the player root (only the current weapon is shown on the hip or in the hand).
    void Stow(Weapon w)
    {
        if (w == null) return;
        w.transform.SetParent(transform, false);
        w.gameObject.SetActive(false);
    }

    // Draw that weapon (stowing the one in hand). False if we don't have it.
    public bool Select(Weapon.Kind k)
    {
        var w = Get(k);
        lastSwitch = Time.time;
        if (w == null)
        {
            fpc.Flash(k == Weapon.Kind.TGun ? "No T-gun  -  take one off an officer, or from the police armory" : $"No {Weapon.NameOf(k)}  -  the police armory has one");
            return false;
        }
        if (Gun == w) { Draw(); return true; }
        CancelCharge();
        bool wasDrawn = Drawn;
        if (Gun != null) Stow(Gun);
        Drawn = false;
        Gun = w;
        w.gameObject.SetActive(true);
        w.SetGlow(1f);
        w.ShowAmmo();
        if (wasDrawn) nextFire = Mathf.Max(nextFire, Time.time + switchDelay);
        Draw();
        return true;
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
        CancelCharge();
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
        if (Gun == null || !Gun.HasModes) return;
        Gun.SetMode(Gun.CurrentMode == Weapon.Mode.Stun ? Weapon.Mode.Lethal : Weapon.Mode.Stun);
        nextFire = Mathf.Max(nextFire, Time.time + switchDelay);
        Tone(Gun.CurrentMode);
    }

    bool CanAim => fpc.isActiveAndEnabled && !fpc.IsHanging && !fpc.Restrained && !fpc.Stunned;

    void Update()
    {
        var kb = Keyboard.current;
        var mouse = Mouse.current;
        TrackVelocity();
        // The limpet detonator works from anywhere (on foot, in a car, hanging off one).
        if (kb != null && kb.xKey.wasPressedThisFrame && !fpc.Restrained) Detonate();
        if (fpc.Restrained || !fpc.isActiveAndEnabled) { if (Drawn) Holster(); return; }
        bool locked = Cursor.lockState == CursorLockMode.Locked;
        if (kb != null)
        {
            if (kb.digit1Key.wasPressedThisFrame) Select(Weapon.Kind.TGun);
            if (kb.digit2Key.wasPressedThisFrame) Select(Weapon.Kind.Scatter);
            if (kb.digit3Key.wasPressedThisFrame) Select(Weapon.Kind.Arc);
            if (kb.digit4Key.wasPressedThisFrame) Select(Weapon.Kind.Limpet);
            if (kb.digit5Key.wasPressedThisFrame) Select(Weapon.Kind.Repulsor);
            if (kb.digit6Key.wasPressedThisFrame) Select(Weapon.Kind.Singularity);
            if (kb.hKey.wasPressedThisFrame) Holster();
            if (kb.bKey.wasPressedThisFrame && Drawn) ToggleMode();
        }
        Aiming = Drawn && CanAim && Gun != null;
        bool rmbSteadies = Gun != null && Gun.kind != Weapon.Kind.Limpet;
        Steady = Aiming && rmbSteadies && (DebugSteady || (mouse != null && locked && mouse.rightButton.isPressed));
        if (fpc.Animator != null && Aiming) fpc.Animator.ArmMode = FigureAnimator.Arms.Aim;
        if (!Aiming) { CancelCharge(); return; }
        if (mouse == null || !locked) { CancelCharge(); return; }

        if (Gun.kind == Weapon.Kind.Repulsor)
        {
            if (!Charging && mouse.leftButton.wasPressedThisFrame && Time.time >= nextFire)
            {
                if (!Gun.HasAmmo) Empty();
                else { Charging = true; chargeStart = Time.time; }
            }
            if (Charging)
            {
                Gun.SetGlow(1f + 0.5f * Charge + (Charge >= 1f ? 0.08f * Mathf.Sin(Time.time * 40f) : 0f));
                if (!mouse.leftButton.isPressed) Fire();
            }
        }
        else if (mouse.leftButton.wasPressedThisFrame) Fire();
        if (Gun.kind == Weapon.Kind.Limpet && mouse.rightButton.wasPressedThisFrame) Detonate();
    }

    void TrackVelocity()
    {
        float dt = Time.deltaTime;
        Vector3 p = transform.position;
        if (dt > 0f && hasLastPos) velocity = Vector3.Lerp(velocity, (p - lastPos) / dt, 0.3f);
        lastPos = p; hasLastPos = true;
    }

    void CancelCharge()
    {
        if (!Charging) return;
        Charging = false;
        if (Gun != null) Gun.SetGlow(1f);
    }

    public int Detonate()
    {
        int n = LimpetCharge.DetonateAll();
        if (n > 0) fpc.Flash(n == 1 ? "Charge detonated" : $"{n} charges detonated");
        return n;
    }

    void LateUpdate()
    {
        UpdateZoom();
        if (!Aiming || fpc.Figure == null || Gun == null) { Blocked = false; return; }
        float dt = Time.deltaTime;
        var fig = fpc.Figure;
        var cam = fpc.playerCamera.transform;
        float s = fig.Scale;

        AimPoint = AimSolver.AimPoint(cam.position, cam.forward, transform, Gun.Range, out _, out _);

        // Sway: a little always, more running, almost none steady; a charging Repulsor trembles.
        Vector3 mv = fpc.Animator != null ? fpc.Animator.Velocity : Vector3.zero;
        float run = Mathf.InverseLerp(2f, 6f, new Vector3(mv.x, 0f, mv.z).magnitude);
        float amp = (Steady ? 0.0015f : Mathf.Lerp(0.005f, 0.03f, run)) * s + Charge * 0.004f * s;
        float tt = Time.time * (Steady ? 0.6f : 1.1f + run * 2f + Charge * 6f);
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

    void Empty()
    {
        nextFire = Time.time + 0.35f;
        Play(ClickClip(), 0.4f);
        fpc.Flash($"{Gun.DisplayName} is empty  -  refill at the police armory");
    }

    void Fire()
    {
        float power = Mathf.Lerp(0.35f, 1f, Charge);
        Charging = false;
        Gun.SetGlow(1f);
        if (Time.time < nextFire) return;
        if (!Gun.HasAmmo) { Empty(); return; }
        nextFire = Time.time + Gun.FireInterval;
        Gun.Spend();
        Gun.ShowAmmo();
        var mode = Gun.CurrentMode;
        Vector3 m = Gun.muzzle.position, dir = Gun.muzzle.forward;
        ShotsFired++;

        switch (Gun.kind)
        {
            case Weapon.Kind.TGun:
            {
                bool hit = LaserWeapon.Fire(m, dir, Gun.Range, transform, mode, out var h);
                LastShotCollider = hit ? h.collider : null;
                LastShotPoint = hit ? h.point : m + dir * Gun.Range;
                ShotFired?.Invoke(m, mode);
                if (hit) Apply(h, dir, mode, Gun.damage, Gun.carDamage, 4f);
                break;
            }
            case Weapon.Kind.Scatter:
            {
                LastShotCollider = null;
                LastShotPoint = m + dir * Gun.Range;
                ShotFired?.Invoke(m, mode);
                for (int i = 0; i < ScatterBeams; i++)
                {
                    Vector3 d = Spread(dir, i == 0 ? 0f : ScatterSpread);
                    if (!LaserWeapon.Fire(m, d, Gun.Range, transform, mode, out var h)) continue;
                    if (LastShotCollider == null) { LastShotCollider = h.collider; LastShotPoint = h.point; }
                    Apply(h, d, mode, Gun.damage, Gun.carDamage, 6f);
                }
                fpc.Shake(0.25f);
                break;
            }
            case Weapon.Kind.Arc:
                LastShotCollider = null;
                ShotFired?.Invoke(m, Weapon.Mode.Lethal);
                ArcCaster.Fire(m, dir, Gun, transform, SpecialHit);
                break;
            case Weapon.Kind.Limpet:
                ShotFired?.Invoke(m, Weapon.Mode.Lethal);
                LimpetCharge.Launch(m + dir * 0.03f, dir * LimpetCharge.Speed + velocity, transform);
                Effects.Puff(m + dir * 0.1f, dir * 2f, 0.12f, 0.4f);
                break;
            case Weapon.Kind.Repulsor:
                LastShotCollider = null;
                ShotFired?.Invoke(m, Weapon.Mode.Lethal);
                Repulsor.Blast(m, dir, power, Gun, transform, SpecialHit);
                // Fired at your feet: a kinetic jump (the recoil is all yours).
                if (dir.y < -0.4f) fpc.Push(-dir * repulsorJump * power);
                fpc.Shake(0.2f + 0.3f * power);
                break;
            case Weapon.Kind.Singularity:
                ShotFired?.Invoke(m, Weapon.Mode.Lethal);
                SingularityOrb.Launch(m + dir * 0.06f, dir * SingularityOrb.Speed + velocity * 0.5f, transform);
                fpc.Shake(0.15f);
                break;
        }
        Play(FireClip(Gun.kind), Gun.kind == Weapon.Kind.TGun ? 0f : 0.35f);
        arm.Kick(dir, Gun);
    }

    // A random direction within `degrees` of dir (uniform over the cone's disc).
    static Vector3 Spread(Vector3 dir, float degrees)
    {
        if (degrees <= 0f) return dir;
        Vector2 r = UnityEngine.Random.insideUnitCircle * Mathf.Tan(degrees * Mathf.Deg2Rad);
        Vector3 u = Vector3.Cross(dir, Mathf.Abs(dir.y) < 0.95f ? Vector3.up : Vector3.right).normalized;
        Vector3 v = Vector3.Cross(dir, u);
        return (dir + u * r.x + v * r.y).normalized;
    }

    // The special weapons' hits: told to the police like any shot.
    void SpecialHit(Collider c, Vector3 point, float damage)
    {
        if (c == null) return;
        if (LastShotCollider == null) { LastShotCollider = c; LastShotPoint = point; }
        ShotHit?.Invoke(c, point, damage, Weapon.Mode.Lethal);
    }

    // What a laser beam does to what it hit.
    void Apply(RaycastHit h, Vector3 dir, Weapon.Mode mode, float baseDamage, float carDamage, float push)
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
            dmg = baseDamage * CharacterHealth.LaserMultiplier(c);
            if (car != null && car.Health != null) { dmg = carDamage; car.Health.Damage(dmg, h.point, h.normal); }
            // Police hear about it while the victim still stands (an officer shot dead is still an officer).
            ShotHit?.Invoke(c, h.point, dmg, mode);
            PedestrianSystem.Shot(c, true, 0f, transform.position);
            CharacterHealth.LaserHit(h, dir, baseDamage, true);
            var rb = c != null ? c.attachedRigidbody : null;
            if (rb != null && !rb.isKinematic) rb.AddForceAtPosition(dir * push, h.point, ForceMode.Impulse);
            return;
        }
        ShotHit?.Invoke(c, h.point, dmg, mode);
    }

    // ---------- placeholder sounds ----------

    void Play(AudioClip clip, float volume)
    {
        if (clip == null || volume <= 0f) return;
        if (audioSource == null)
        {
            audioSource = gameObject.AddComponent<AudioSource>();
            audioSource.playOnAwake = false;
            audioSource.spatialBlend = 0f;
            audioSource.volume = 0.25f;
        }
        audioSource.PlayOneShot(clip, volume);
    }

    // Placeholder switch tone: a short beep, higher for stun.
    void Tone(Weapon.Mode mode)
    {
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
        Play(clip, 1f);
    }

    static AudioClip ClickClip()
    {
        if (clickEmpty != null) return clickEmpty;
        const int rate = 44100;
        int n = rate / 40;
        var data = new float[n];
        for (int i = 0; i < n; i++) data[i] = (UnityEngine.Random.value * 2f - 1f) * Mathf.Exp(-i / (n * 0.12f));
        clickEmpty = AudioClip.Create("EmptyClick", n, 1, rate, false);
        clickEmpty.SetData(data, 0);
        return clickEmpty;
    }

    // Synthesised per kind: scatter = noise burst, arc = crackle, limpet = thump, repulsor = whump
    // (falling sweep), singularity = rising warble. The T-gun stays silent as before.
    static AudioClip FireClip(Weapon.Kind k)
    {
        if (k == Weapon.Kind.TGun) return null;
        ref AudioClip clip = ref fireClips[(int)k];
        if (clip != null) return clip;
        const int rate = 44100;
        float len = k switch { Weapon.Kind.Scatter => 0.18f, Weapon.Kind.Arc => 0.16f, Weapon.Kind.Limpet => 0.12f, Weapon.Kind.Repulsor => 0.3f, _ => 0.35f };
        int n = Mathf.RoundToInt(rate * len);
        var data = new float[n];
        var rng = new System.Random(17 + (int)k);
        float phase = 0f;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)rate, u = i / (float)n;
            float noise = (float)rng.NextDouble() * 2f - 1f;
            float s;
            switch (k)
            {
                case Weapon.Kind.Scatter: s = noise * Mathf.Exp(-u * 9f) + Mathf.Sin(2f * Mathf.PI * 180f * t) * 0.4f * Mathf.Exp(-u * 14f); break;
                case Weapon.Kind.Arc: s = noise * (rng.NextDouble() < 0.18 ? 1f : 0.15f) * (1f - u); break;
                case Weapon.Kind.Limpet: phase += 2f * Mathf.PI * Mathf.Lerp(140f, 60f, u) / rate; s = Mathf.Sin(phase) * Mathf.Exp(-u * 6f) + noise * 0.2f * Mathf.Exp(-u * 20f); break;
                case Weapon.Kind.Repulsor: phase += 2f * Mathf.PI * Mathf.Lerp(220f, 40f, u) / rate; s = Mathf.Sin(phase) * (1f - u) + noise * 0.35f * (1f - u) * (1f - u); break;
                default: phase += 2f * Mathf.PI * Mathf.Lerp(90f, 700f, u * u) / rate; s = Mathf.Sin(phase) * Mathf.Sin(u * Mathf.PI) * (0.7f + 0.3f * Mathf.Sin(t * 120f)); break;
            }
            data[i] = Mathf.Clamp(s, -1f, 1f) * 0.8f;
        }
        clip = AudioClip.Create("Fire" + k, n, 1, rate, false);
        clip.SetData(data, 0);
        return clip;
    }

    // ---------- crosshair, weapon / ammo readout, charge bar, slot strip ----------

    void OnGUI()
    {
        var prev = GUI.color;
        float cx = Screen.width * 0.5f, cy = Screen.height * 0.5f;
        if (Time.time - lastSwitch < 2f && fpc != null && fpc.isActiveAndEnabled) DrawSlots();
        if (!Aiming || Gun == null) { GUI.color = prev; return; }
        GUI.color = new Color(1f, 1f, 1f, 0.85f);
        GUI.DrawTexture(new Rect(cx - 2f, cy - 2f, 4f, 4f), Texture2D.whiteTexture);
        Color mc = Color.Lerp(Gun.Accent, Color.white, 0.35f);
        GUI.color = new Color(mc.r, mc.g, mc.b, 0.95f);
        string label = Gun.HasModes ? (Mode == Weapon.Mode.Stun ? "STUN" : "LETHAL") : Gun.DisplayName.ToUpperInvariant();
        GUI.Label(new Rect(cx + 14f, cy + 6f, 200f, 20f), label);
        string ammo = Gun.Unlimited ? "" : $"{Gun.ammo}/{Gun.maxAmmo}";
        if (Gun.kind == Weapon.Kind.Limpet) ammo += $"   armed {LimpetCharge.Armed}  (RMB / X detonate)";
        if (ammo.Length > 0)
        {
            GUI.color = Gun.HasAmmo ? new Color(1f, 1f, 1f, 0.8f) : new Color(1f, 0.35f, 0.3f, 0.95f);
            GUI.Label(new Rect(cx + 14f, cy + 22f, 320f, 20f), ammo);
        }
        if (Charging)
        {
            float w = 60f, k = Charge;
            GUI.color = new Color(0f, 0f, 0f, 0.5f);
            GUI.DrawTexture(new Rect(cx - w * 0.5f, cy + 16f, w, 4f), Texture2D.whiteTexture);
            GUI.color = new Color(mc.r, mc.g, mc.b, 0.95f);
            GUI.DrawTexture(new Rect(cx - w * 0.5f, cy + 16f, w * k, 4f), Texture2D.whiteTexture);
        }
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

    // The six slots along the bottom: owned ones bright, the current one in its colour.
    void DrawSlots()
    {
        const float w = 120f, h = 22f;
        float x0 = Screen.width * 0.5f - w * Weapon.KindCount * 0.5f, y = Screen.height - 90f;
        for (int i = 0; i < Weapon.KindCount; i++)
        {
            var k = (Weapon.Kind)i;
            var have = slots[i];
            bool current = have != null && have == Gun;
            GUI.color = new Color(0f, 0f, 0f, current ? 0.6f : 0.35f);
            GUI.DrawTexture(new Rect(x0 + i * w + 2f, y, w - 4f, h), Texture2D.whiteTexture);
            Color c = have == null ? new Color(1f, 1f, 1f, 0.3f) : current ? Color.Lerp(have.Accent, Color.white, 0.3f) : new Color(1f, 1f, 1f, 0.85f);
            GUI.color = c;
            string ammo = have != null && !have.Unlimited ? $" {have.ammo}" : "";
            GUI.Label(new Rect(x0 + i * w + 8f, y + 2f, w - 10f, h), $"{i + 1} {Weapon.NameOf(k)}{ammo}");
        }
    }
}
