using System.Collections.Generic;
using UnityEngine;

// A police officer as a character (placeholder: 1.8 m capsule + visor, a board for the scooter).
// PoliceDispatch decides WHAT it does (where to go, cuffing, dragging, weapons); this moves it like a
// person:
//   Scooter : rides its fold-out scooter (flies, no collisions) to within dismountRange of its goal,
//             then hops off (the scooter stays parked) and continues on foot.
//   Foot    : CharacterController with gravity. Runs to the goal, slows to a walk inside walkRange,
//             stops at the goal's stop distance facing its look target. Keeps apart from other officers
//             and steps round obstacles with a forward probe.
// Body: the shared CharacterFigure in police blue, posed by FigureAnimator (walk / run, riding the
// scooter, guard when force is authorised, reaching out to cuff, one arm on the player when dragging).
//   Return  : back on the scooter to its unit; removed on arrival.
// Stands on moving cars like the player does (carried by the car it's standing on).
// Weapon: a T-gun on the hip; while PoliceDispatch calls AimAt it is drawn and aimed with the shared
// arm IK (ArmAim) at the target plus an error that shrinks the longer it tracks, and shots leave the
// muzzle along the barrel (LaserWeapon), so cover stops them. Stun(s) drops it like the player.
// Lives on the Player layer: cars never push it; the player's fists hit it.
// Runs after the player (whose Update syncs the moved cars' colliders), so it stands on cars cleanly.
[DefaultExecutionOrder(110)]
[RequireComponent(typeof(CharacterController))]
public class OfficerAgent : MonoBehaviour
{
    public enum Phase { Scooter, Foot, Return }

    public float walkSpeed = 3f;
    public float runSpeed = 6.5f;
    public float turnRate = 360f;
    public float scooterSpeed = 14f;
    public float dismountRange = 6f;
    public float walkRange = 3f;
    public float separation = 1.5f;
    public float gravity = -25f;

    public Phase State { get; private set; }
    public PoliceDriver Home { get; private set; }
    public bool Grounded { get; private set; }
    public bool Staggered => Time.time < staggerUntil;
    public bool AtGoal { get; private set; }
    public FlyingVehicle Platform => platform;

    // Set every frame by PoliceDispatch.
    public Vector3 Goal { get; set; }
    public float StopDistance { get; set; } = 1.1f;
    public Vector3? LookAt { get; set; }
    public float? SpeedOverride { get; set; }   // e.g. dragging at 2 m/s

    static readonly List<OfficerAgent> all = new List<OfficerAgent>();
    public static IReadOnlyList<OfficerAgent> All => all;

    CharacterController cc;
    Transform scooter;
    FigureAnimator anim;
    Vector3 lastPos;
    // Set by PoliceDispatch: what the hands are doing, and whether force is authorised (guard up).
    public FigureAnimator.Pose ActionPose { get; set; }
    public bool Armed { get; set; }
    float verticalVelocity;
    float staggerUntil;
    Vector3 knock;
    FlyingVehicle platform; Vector3 platformLocal; float platformYaw;
    Collider groundCollider;
    int probeMask;
    float nextFire;
    int burstLeft;
    // T-gun in the right hand (holstered on the hip until force is used).
    public float errorStart = 1.2f, errorSettled = 0.12f, errorSettle = 3f;
    CharacterFigure fig;
    Weapon gun;
    readonly ArmAim arm = new ArmAim();
    bool gunDrawn;
    Vector3 aimTarget; int aimFrame = -10;
    float trackTime, seed;
    float stunUntil;
    public bool Stunned => Time.time < stunUntil;

    // ---------- spawning ----------

    // Hops off `home` onto its scooter.
    public static OfficerAgent SpawnFrom(PoliceDriver home, float sideOffset)
    {
        Vector3 pos = home.transform.position + home.Car.PlatformRotation * new Vector3(sideOffset, 0.3f, 0f);
        var o = Create(pos, home.Car.PlatformRotation, home);
        o.State = Phase.Scooter;
        return o;
    }

    // Steps straight onto a car's roof (roof arrests).
    public static OfficerAgent SpawnOnRoof(PoliceDriver home, FlyingVehicle car, Vector3 roofPoint)
    {
        var o = Create(roofPoint, car.PlatformRotation, home);
        o.State = Phase.Foot;
        o.DropScooter(false);
        o.SetPlatform(car);
        return o;
    }

    static int spawnSeed;

    static OfficerAgent Create(Vector3 pos, Quaternion rot, PoliceDriver home)
    {
        var go = new GameObject("Officer");
        go.transform.SetPositionAndRotation(pos, Quaternion.Euler(0f, rot.eulerAngles.y, 0f));
        int layer = LayerMask.NameToLayer("Player");
        if (layer >= 0) go.layer = layer;
        var cc = go.AddComponent<CharacterController>();
        float h = CharacterFigure.DefaultHeight;
        cc.height = h; cc.radius = 0.2f; cc.center = new Vector3(0f, h * 0.5f, 0f);
        cc.minMoveDistance = 0f;

        // The figure's hit colliders (BodyPart triggers on the Player layer) are what punches hit.
        // A pooled generated body (officers' sub-pool), or the primitive figure while it fills.
        var asset = BodyPool.Officer(++spawnSeed);
        var fig = asset != null ? CharacterFigure.Assemble(go.transform, asset, CharacterFigure.Role.Police, false, true, spawnSeed)
                                : CharacterFigure.Build(go.transform, CharacterFigure.Role.Police);
        cc.height = fig.Height; cc.center = new Vector3(0f, fig.Height * 0.5f, 0f);
        go.AddComponent<FigureAnimator>();
        Flammable.Add(go, Flammable.Kind.Character);
        var board = GameObject.CreatePrimitive(PrimitiveType.Cube);
        board.name = "Scooter";
        Object.Destroy(board.GetComponent<Collider>());
        board.transform.SetParent(go.transform, false);
        board.transform.localPosition = new Vector3(0f, -0.05f, 0f);
        board.transform.localScale = new Vector3(0.45f, 0.06f, 1.1f);
        board.GetComponent<Renderer>().sharedMaterial = CharacterFigure.Mat(new Color(0.08f, 0.09f, 0.12f));
        if (layer >= 0) board.layer = layer;

        var o = go.AddComponent<OfficerAgent>();
        o.anim = go.GetComponent<FigureAnimator>();
        o.scooter = board.transform;
        o.Home = home;
        o.Goal = pos;
        return o;
    }

    void Awake()
    {
        cc = GetComponent<CharacterController>();
        seed = Random.value * 100f;
        int playerLayer = LayerMask.NameToLayer("Player");
        probeMask = Physics.DefaultRaycastLayers & ~(playerLayer >= 0 ? 1 << playerLayer : 0);
    }

    void OnEnable() => all.Add(this);
    void OnDisable() => all.Remove(this);

    void OnDestroy()
    {
        if (scooter != null && scooter.parent == null) Destroy(scooter.gameObject); // parked scooter goes too
    }

    // ---------- commands ----------

    void Start()
    {
        fig = GetComponent<CharacterFigure>();
        if (fig != null) { gun = Weapon.BuildTGun(transform, gameObject.layer); HolsterGun(); }
    }

    // Stun shot: down (Stunned pose) for `seconds`, no moving or shooting.
    public void Stun(float seconds) => stunUntil = Mathf.Max(stunUntil, Time.time + seconds);

    public void Stagger(float seconds, Vector3 knockback)
    {
        staggerUntil = Time.time + seconds;
        knock = knockback;
    }

    public void ReturnToUnit()
    {
        if (State == Phase.Return) return;
        State = Phase.Return;
        LeavePlatform();
        // Remount: the scooter comes back under the officer (placeholder).
        if (scooter != null)
        {
            scooter.SetParent(transform, false);
            scooter.localPosition = new Vector3(0f, -0.1f, 0f);
            scooter.localRotation = Quaternion.identity;
        }
        cc.enabled = false;
    }

    void DropScooter(bool park)
    {
        if (scooter == null) return;
        if (park)
        {
            scooter.SetParent(null, true); // stays where it was left
        }
        else
        {
            Destroy(scooter.gameObject);
            scooter = null;
        }
    }

    // ---------- update ----------

    void Update()
    {
        float dt = Time.deltaTime;
        switch (State)
        {
            case Phase.Scooter: UpdateScooter(dt); break;
            case Phase.Foot: UpdateFoot(dt); break;
            default: UpdateReturn(dt); break;
        }
        Pose(dt);
    }

    // Body pose from what the officer is doing (velocity measured, so carried-on-a-car looks right
    // only in relative terms; fine for a placeholder).
    void Pose(float dt)
    {
        if (anim == null) return;
        Vector3 v = dt > 0f ? (transform.position - lastPos) / dt : Vector3.zero;
        lastPos = transform.position;
        if (platform != null) v -= platform.Velocity;
        anim.Velocity = v;
        anim.Grounded = State != Phase.Foot || Grounded;
        anim.LookYaw = transform.eulerAngles.y;
        anim.CurrentPose = State != Phase.Foot ? FigureAnimator.Pose.Scooter
                         : Stunned ? FigureAnimator.Pose.Stunned
                         : Staggered ? FigureAnimator.Pose.Staggered
                         : !Grounded ? FigureAnimator.Pose.Air
                         : ActionPose;
        anim.ArmMode = Aiming ? FigureAnimator.Arms.Aim
                     : Armed && ActionPose == FigureAnimator.Pose.Normal ? FigureAnimator.Arms.Guard : FigureAnimator.Arms.Lowered;
    }

    void UpdateScooter(float dt)
    {
        cc.enabled = false;
        if (Stunned) return; // hangs there on the hovering scooter
        Vector3 target = Goal + Vector3.up * 0.3f;
        Vector3 to = target - transform.position;
        Vector3 flat = new Vector3(to.x, 0f, to.z);
        if (flat.sqrMagnitude > 0.01f) Face(flat, dt);
        float d = to.magnitude;
        if (d > 0.01f) transform.position += to / d * Mathf.Min(d, scooterSpeed * dt);

        // Close enough: hop off and walk the rest.
        if (flat.magnitude < dismountRange && Mathf.Abs(to.y) < 3f)
        {
            DropScooter(true);
            State = Phase.Foot;
            cc.enabled = true;
            verticalVelocity = 0f;
        }
    }

    void UpdateReturn(float dt)
    {
        if (Home == null) { Destroy(gameObject); return; }
        Vector3 to = Home.transform.position - transform.position;
        float d = to.magnitude;
        if (d < 2f) { Destroy(gameObject); return; }
        Face(new Vector3(to.x, 0f, to.z), dt);
        transform.position += to / d * Mathf.Min(d, scooterSpeed * 1.5f * dt);
    }

    void UpdateFoot(float dt)
    {
        if (!cc.enabled) cc.enabled = true;

        // Carried by the car it stands on.
        if (platform != null)
        {
            Quaternion rot = platform.PlatformRotation;
            float yaw = rot.eulerAngles.y;
            transform.Rotate(0f, Mathf.DeltaAngle(platformYaw, yaw), 0f);
            platformYaw = yaw;
            cc.Move(platform.PlatformPosition + rot * platformLocal - transform.position);
        }

        Vector3 move = Vector3.zero;
        AtGoal = false;
        if (Stunned) { }
        else if (Staggered)
        {
            // Knocked back over the first fraction of the stagger.
            move = knock * 4f;
            knock = Vector3.MoveTowards(knock, Vector3.zero, 4f * dt);
        }
        else
        {
            Vector3 to = Goal - transform.position;
            to.y = 0f;
            float d = to.magnitude;
            if (d > StopDistance)
            {
                float speed = SpeedOverride ?? (d > walkRange ? runSpeed : walkSpeed);
                Vector3 dir = to / d;

                // Keep apart from other officers.
                foreach (var o in all)
                {
                    if (o == this || o.State != Phase.Foot) continue;
                    Vector3 away = transform.position - o.transform.position;
                    away.y = 0f;
                    float ad = away.magnitude;
                    if (ad < separation && ad > 0.01f) dir += away / ad * (1f - ad / separation) * 1.5f;
                }
                dir.y = 0f;
                if (dir.sqrMagnitude > 1e-4f) dir.Normalize();

                // Step round obstacles: probe ahead at knee height, turn toward the clearer side.
                if (Blocked(dir))
                {
                    Vector3 left = Quaternion.Euler(0f, -60f, 0f) * dir, right = Quaternion.Euler(0f, 60f, 0f) * dir;
                    dir = !Blocked(right) ? right : !Blocked(left) ? left : Quaternion.Euler(0f, 110f, 0f) * dir;
                }

                move = dir * Mathf.Min(speed, d / Mathf.Max(dt, 1e-4f));
                // Aiming: keeps facing the target while moving (strafes).
                if (!Aiming) Face(dir, dt);
                else { Vector3 look = aimTarget - transform.position; look.y = 0f; Face(look, dt); }
            }
            else
            {
                AtGoal = true;
                Vector3 look = (Aiming ? aimTarget : LookAt ?? Goal) - transform.position;
                look.y = 0f;
                if (look.sqrMagnitude > 0.01f) Face(look, dt);
            }
        }


        verticalVelocity = Grounded ? -2f : verticalVelocity + gravity * dt;
        groundCollider = null;
        var flags = cc.Move((move + Vector3.up * verticalVelocity) * dt);
        Grounded = (flags & CollisionFlags.Below) != 0;
        if (Grounded)
        {
            var car = groundCollider != null ? groundCollider.GetComponentInParent<FlyingVehicle>() : null;
            if (car != platform) { if (car != null) SetPlatform(car); else LeavePlatform(); }
        }
        else LeavePlatform();
        if (platform != null)
            platformLocal = Quaternion.Inverse(platform.PlatformRotation) * (transform.position - platform.PlatformPosition);
    }

    bool Blocked(Vector3 dir) =>
        Physics.Raycast(transform.position + Vector3.up * 0.5f, dir, 1.2f, probeMask, QueryTriggerInteraction.Ignore);

    void Face(Vector3 dir, float dt)
    {
        if (dir.sqrMagnitude < 1e-4f) return;
        transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(dir), turnRate * dt);
    }

    void SetPlatform(FlyingVehicle car)
    {
        platform = car;
        platformYaw = car.PlatformRotation.eulerAngles.y;
        platformLocal = Quaternion.Inverse(car.PlatformRotation) * (transform.position - car.PlatformPosition);
    }

    void LeavePlatform() => platform = null;

    void OnControllerColliderHit(ControllerColliderHit hit)
    {
        if (hit.normal.y > 0.5f) groundCollider = hit.collider;
    }

    // ---------- weapons (called by PoliceDispatch) ----------

    // Point the T-gun at `target` this frame (call every frame while it should aim). The arm aims at
    // the target plus an error that shrinks the longer it keeps tracking.
    public void AimAt(Vector3 target) { aimTarget = target; aimFrame = Time.frameCount; }

    public bool Aiming => Time.frameCount - aimFrame <= 1 && State == Phase.Foot && !Staggered && !Stunned
                          && ActionPose == FigureAnimator.Pose.Normal && gun != null;
    public Weapon Gun => gun;
    public Vector3 AimPoint { get; private set; }
    public float TrackTime => trackTime;

    // Non-lethal: one stun shot every `interval` s within `range`, along the barrel. True if it hit the
    // player.
    public bool FireStun(Vector3 target, float range, float interval)
    {
        if (!CanFire(target, range) || Time.time < nextFire) return false;
        nextFire = Time.time + interval;
        return Shoot(Weapon.Mode.Stun, range);
    }

    // Lethal: bursts of shots along the barrel; returns 1 if this frame's shot hit the player.
    public int FireSidearm(Vector3 target, float range, int burst, float burstInterval, float shotInterval)
    {
        if (!CanFire(target, range)) return 0;
        if (burstLeft <= 0)
        {
            if (Time.time < nextFire) return 0;
            burstLeft = burst;
        }
        if (Time.time < nextFire) return 0;
        burstLeft--;
        bool hit = Shoot(Weapon.Mode.Lethal, range);
        nextFire = Time.time + (burstLeft > 0 ? shotInterval : burstInterval);
        return hit ? 1 : 0;
    }

    // Drawn and on the target for a moment, the suspect in sight (eye line) and in range.
    bool CanFire(Vector3 target, float range)
    {
        if (!Aiming || !gunDrawn || trackTime < 0.4f) return false;
        Vector3 eye = transform.position + Vector3.up * 1.4f;
        return (target - eye).sqrMagnitude <= range * range && PoliceDispatch.LineOfSight(eye, target);
    }

    // One shot from the muzzle along the barrel; cover in the way takes it. True if the player was hit.
    bool Shoot(Weapon.Mode mode, float range)
    {
        if (gun.CurrentMode != mode) gun.SetMode(mode);
        Vector3 m = gun.muzzle.position, dir = gun.muzzle.forward;
        bool hit = LaserWeapon.Fire(m, dir, range * 1.5f, transform, mode, out var h);
        arm.Kick(dir, gun);
        Shots++;
        if (!hit) return false;
        var player = FirstPersonController.Instance;
        if (player != null && h.collider.transform.IsChildOf(player.transform)) { HitsOnPlayer++; return true; }
        // Stray shots: cars and bystanders take them like the player's would.
        var car = h.collider.GetComponentInParent<FlyingVehicle>();
        if (car != null)
        {
            if (mode == Weapon.Mode.Stun) car.Hiccup(gun.carHiccup);
            else if (car.Health != null) car.Health.Damage(gun.carDamage, h.point, h.normal);
        }
        PedestrianSystem.Shot(h.collider, mode == Weapon.Mode.Lethal, gun.stunTime, m);
        return false;
    }

    public static int Shots, HitsOnPlayer;

    void LateUpdate()
    {
        if (gun == null || fig == null) return;
        bool want = Aiming;
        if (want != gunDrawn)
        {
            gunDrawn = want;
            if (want) { arm.Begin(fig); ArmAim.Attach(gun, fig); trackTime = 0f; }
            else HolsterGun();
        }
        if (!want) { trackTime = 0f; return; }
        float dt = Time.deltaTime;
        trackTime += dt;
        // Aim error: wanders smoothly, shrinking from errorStart to errorSettled over errorSettle s.
        float err = Mathf.Lerp(errorStart, errorSettled, Mathf.Clamp01(trackTime / errorSettle));
        float t = Time.time * 0.9f + seed;
        Vector3 e = new Vector3(Mathf.PerlinNoise(t, 0.1f) - 0.5f, Mathf.PerlinNoise(0.3f, t) - 0.5f, Mathf.PerlinNoise(t, 0.7f) - 0.5f) * 2f * err;
        AimPoint = aimTarget + e;
        arm.Solve(fig, gun, AimPoint, transform, anim != null ? anim.BodyYaw : transform.eulerAngles.y, Vector3.up, null, Vector3.zero, dt);
    }

    void HolsterGun()
    {
        if (gun == null || fig == null) return;
        gun.transform.SetParent(fig.Hips, false);
        gun.transform.localPosition = new Vector3(0.18f, -0.03f, 0.03f) * fig.Scale;
        gun.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
    }
}
