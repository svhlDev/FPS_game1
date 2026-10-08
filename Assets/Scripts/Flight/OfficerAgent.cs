using System.Collections.Generic;
using UnityEngine;

// A police officer as a character (placeholder: 1.8 m capsule + visor, a board for the scooter).
// PoliceDispatch decides WHAT it does (where to go, cuffing, dragging, weapons); this moves it like a
// person:
//   Scooter : rides its fold-out scooter (flies, no collisions) to within dismountRange of its goal,
//             then hops off (the scooter stays parked) and continues on foot.
//   Foot    : CharacterController with gravity. Runs to the goal, slows to a walk inside walkRange,
//             stops at the goal's stop distance facing its look target. Keeps apart from other officers
//             and steps round obstacles with a forward probe. Body bobs while walking.
//   Return  : back on the scooter to its unit; removed on arrival.
// Stands on moving cars like the player does (carried by the car it's standing on).
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
    Transform body, scooter;
    float verticalVelocity;
    float staggerUntil;
    Vector3 knock;
    float bobPhase;
    FlyingVehicle platform; Vector3 platformLocal; float platformYaw;
    Collider groundCollider;
    int probeMask;
    float nextFire;
    int burstLeft;
    LineRenderer tracer; float tracerUntil;

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

    static OfficerAgent Create(Vector3 pos, Quaternion rot, PoliceDriver home)
    {
        var go = new GameObject("Officer");
        go.transform.SetPositionAndRotation(pos, Quaternion.Euler(0f, rot.eulerAngles.y, 0f));
        int layer = LayerMask.NameToLayer("Player");
        if (layer >= 0) go.layer = layer;
        var cc = go.AddComponent<CharacterController>();
        cc.height = 1.8f; cc.radius = 0.35f; cc.center = new Vector3(0f, 0.9f, 0f);
        cc.minMoveDistance = 0f;

        Material mat = null;
        var homeRend = home != null ? home.GetComponentInChildren<MeshRenderer>(true) : null;
        if (homeRend != null) mat = homeRend.sharedMaterial;

        var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        body.name = "Body";
        body.transform.SetParent(go.transform, false);
        body.transform.localPosition = new Vector3(0f, 0.9f, 0f);
        body.transform.localScale = new Vector3(0.7f, 0.9f, 0.7f);
        // The CharacterController (a collider on the Player layer) is what the player's punches hit.
        Object.Destroy(body.GetComponent<Collider>());
        var visor = GameObject.CreatePrimitive(PrimitiveType.Cube);
        visor.name = "Visor";
        Object.Destroy(visor.GetComponent<Collider>());
        visor.transform.SetParent(body.transform, false);
        visor.transform.localPosition = new Vector3(0f, 0.6f, 0.45f);
        visor.transform.localScale = new Vector3(0.7f, 0.15f, 0.2f);
        var board = GameObject.CreatePrimitive(PrimitiveType.Cube);
        board.name = "Scooter";
        Object.Destroy(board.GetComponent<Collider>());
        board.transform.SetParent(go.transform, false);
        board.transform.localPosition = new Vector3(0f, -0.1f, 0f);
        board.transform.localScale = new Vector3(0.6f, 0.08f, 1.3f);
        if (mat != null) body.GetComponent<Renderer>().sharedMaterial = mat;
        if (layer >= 0) { body.layer = layer; visor.layer = layer; board.layer = layer; }

        var o = go.AddComponent<OfficerAgent>();
        o.body = body.transform;
        o.scooter = board.transform;
        o.Home = home;
        o.Goal = pos;
        return o;
    }

    void Awake()
    {
        cc = GetComponent<CharacterController>();
        int playerLayer = LayerMask.NameToLayer("Player");
        probeMask = Physics.DefaultRaycastLayers & ~(playerLayer >= 0 ? 1 << playerLayer : 0);
    }

    void OnEnable() => all.Add(this);
    void OnDisable() => all.Remove(this);

    void OnDestroy()
    {
        if (scooter != null && scooter.parent == null) Destroy(scooter.gameObject); // parked scooter goes too
        if (tracer != null) Destroy(tracer.gameObject);
    }

    // ---------- commands ----------

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
        if (tracer != null && tracer.enabled && Time.time > tracerUntil) tracer.enabled = false;
    }

    void UpdateScooter(float dt)
    {
        cc.enabled = false;
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
        if (Staggered)
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
                Face(dir, dt);
                bobPhase += dt * speed * 2.2f;
            }
            else
            {
                AtGoal = true;
                Vector3 look = (LookAt ?? Goal) - transform.position;
                look.y = 0f;
                if (look.sqrMagnitude > 0.01f) Face(look, dt);
            }
        }

        // Placeholder walk animation.
        float bob = move.sqrMagnitude > 0.1f ? Mathf.Abs(Mathf.Sin(bobPhase)) * 0.06f : 0f;
        body.localPosition = new Vector3(0f, 0.9f + bob, 0f);

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

    // Non-lethal: one stun shot every `interval` s within `range`; returns true on a hit.
    public bool FireStun(Vector3 target, float range, float interval, float hitChance, Material tracerMat)
    {
        if (State != Phase.Foot || Staggered || Time.time < nextFire) return false;
        Vector3 eye = transform.position + Vector3.up * 1.4f;
        if ((target - eye).sqrMagnitude > range * range || !PoliceDispatch.LineOfSight(eye, target)) return false;
        nextFire = Time.time + interval;
        bool hit = Random.value < hitChance;
        ShowTracer(eye, hit ? target : target + Random.insideUnitSphere * 1.5f, tracerMat, new Color(0.4f, 0.7f, 1f));
        return hit;
    }

    // Lethal: bursts of hitscan shots from the sidearm; returns the number of hits this frame (0 or 1).
    public int FireSidearm(Vector3 target, float range, int burst, float burstInterval, float shotInterval, Material tracerMat)
    {
        if (State != Phase.Foot || Staggered) return 0;
        Vector3 eye = transform.position + Vector3.up * 1.4f;
        float d = Vector3.Distance(eye, target);
        if (d > range || !PoliceDispatch.LineOfSight(eye, target)) return 0;
        if (burstLeft <= 0)
        {
            if (Time.time < nextFire) return 0;
            burstLeft = burst;
        }
        if (Time.time < nextFire) return 0;
        burstLeft--;
        nextFire = Time.time + (burstLeft > 0 ? shotInterval : burstInterval);
        bool hit = Random.value < Mathf.Lerp(0.6f, 0.15f, d / range);
        ShowTracer(eye, hit ? target : target + Random.insideUnitSphere * 2f, tracerMat, Color.white);
        return hit ? 1 : 0;
    }

    void ShowTracer(Vector3 a, Vector3 b, Material mat, Color tint)
    {
        PedestrianSystem.ReportDanger(a);
        if (mat == null) return;
        if (tracer == null)
        {
            var go = new GameObject("OfficerTracer");
            tracer = go.AddComponent<LineRenderer>();
            tracer.sharedMaterial = mat;
            tracer.positionCount = 2;
            tracer.startWidth = tracer.endWidth = 0.05f;
            tracer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            tracer.receiveShadows = false;
        }
        tracer.startColor = tracer.endColor = tint;
        tracer.SetPosition(0, a);
        tracer.SetPosition(1, b);
        tracer.enabled = true;
        tracerUntil = Time.time + 0.06f;
    }
}
