using UnityEngine;

// Drives a police FlyingVehicle. Patrol: it is ordinary lane traffic (FlyingVehicle's own AI).
// Pursuit uses the car's autopilot with a deliberately clumsy handling model (DriveModel): more top
// speed than the player, but slow off the line, weak brakes, slow steering and little grip, so a
// police car wins on a long straight and loses on a sharp dodge or a brake check. The AI here only
// picks a heading, throttle/brake and a height; it never sets the car's velocity.
//   Chase     : Approach (fly to the slot PoliceDispatch gave it: beside/behind/above the target, on its
//               assigned layer) -> Ram (full throttle at a point 4 m THROUGH the target; only units on the
//               target's layer ram) -> Recover (brake and turn round after a hit or a miss) -> Approach.
//   Separation: while chasing or hovering it steers away from other pursuing police within 10 m, so
//               they spread round the player instead of stacking.
//   HoldSlot  : keep a slot around the target (docked to tow it, or boxing it in), arriving from outside.
//   HoverNear : watch a point (the player on foot, the tow group).
// At wanted level 3 PoliceDispatch also calls UpdateShooting. Runs before the cars move.
// Car gunfire only happens at PoliceDispatch's Lethal force level.
[DefaultExecutionOrder(-15)]
[RequireComponent(typeof(FlyingVehicle))]
public class PoliceDriver : MonoBehaviour
{
    [Header("Handling (player: ~25 m/s^2, 90 deg/s, grip 2.5)")]
    public float policeAccel = 9f;
    public float policeTopSpeed = 75f;
    public float policeBrake = 6f;
    public float policeTurnRate = 55f;
    [Tooltip("Sideways velocity decay (1/s). Low: slides wide in turns.")]
    public float policeLateralGrip = 1.5f;
    [Tooltip("Max climb / descent while chasing (layer changes).")]
    public float policeVerticalSpeed = 6f;

    [Header("Chase")]
    [Tooltip("Cap on how far ahead (s) the intercept point leads the target.")]
    public float maxLeadTime = 1.5f;
    [Tooltip("Inside this distance it commits to a ram.")]
    public float ramRange = 12f;
    [Tooltip("The ram aims this far beyond the target along the approach.")]
    public float ramThrough = 4f;
    [Tooltip("Steer away from other pursuing police within this range.")]
    public float separationRange = 10f;
    public float separationWeight = 1.5f;
    [Tooltip("Recover time after a miss (overshoot without contact).")]
    public float missRecoverTime = 2f;
    [Tooltip("Recover time after a hit.")]
    public float hitRecoverTime = 0.7f;

    [Header("Shooting (wanted level 3)")]
    public float shootRange = 120f;
    public int burstShots = 3;
    public float burstInterval = 2f;
    public float shotInterval = 0.12f;
    [Tooltip("Spread (degrees, cone radius) when the target was just sighted...")]
    public float spreadStart = 6f;
    [Tooltip("...shrinking to this after lockTime seconds in sight (about 40% hits at 60 m).")]
    public float spreadLocked = 1.8f;
    public float lockTime = 5f;
    public float damagePerHit = 6f;
    public Material tracerMaterial;
    public Material sparkMaterial;

    public FlyingVehicle Car { get; private set; }
    public bool IsPursuing { get; private set; }
    public bool AtSlot { get; private set; }
    // Set by PoliceDispatch: approach slot in the target's yaw frame, layer offset, and whether this
    // unit may ram (only those on the target's layer).
    public Vector3 ApproachSlot { get; set; } = new Vector3(8f, 0f, -8f);
    public int LayerOffset { get; set; }
    public bool MayRam { get; set; } = true;

    enum ChaseState { Approach, Ram, Recover }
    ChaseState state;
    float recoverUntil;
    bool contacted;
    Vector3 ramDir;

    float sightTime, nextBurst, nextShot;
    int shotsLeft;
    LineRenderer tracer;
    float tracerUntil;
    Transform spark;
    float sparkUntil;

    FlyingVehicle.DriveModel Model => new FlyingVehicle.DriveModel
    {
        accel = policeAccel, topSpeed = policeTopSpeed, brake = policeBrake,
        turnRate = policeTurnRate, lateralGrip = policeLateralGrip, verticalSpeed = policeVerticalSpeed,
    };

    void Awake() => Car = GetComponent<FlyingVehicle>();
    void OnEnable() => PoliceDispatch.Register(this);
    void OnDisable() => PoliceDispatch.Unregister(this);

    public void Pursue()
    {
        IsPursuing = true;
        Car.ReportContacts = true;
        state = ChaseState.Approach;
    }

    public void Patrol()
    {
        IsPursuing = false;
        AtSlot = false;
        sightTime = 0f;
        Car.ReportContacts = false;
        Car.ClearAutopilot(); // the lane AI flies it back to its lane
    }

    // Touched the player's car this frame (from PoliceDispatch).
    public void NotifyContact() => contacted = true;

    // ---------- low-level steering ----------

    static float YawOf(Vector3 v) => Mathf.Atan2(v.x, v.z) * Mathf.Rad2Deg;

    Vector3 Heading => Quaternion.Euler(0f, Car.Yaw, 0f) * Vector3.forward;

    // Away from other pursuing police within separationRange, weighted 1 - d/range (horizontal push).
    Vector3 Separation()
    {
        Vector3 sum = Vector3.zero;
        var d = PoliceDispatch.Instance;
        if (d == null) return sum;
        Vector3 me = transform.position;
        foreach (var u in d.Pursuing)
        {
            if (u == null || u == this) continue;
            Vector3 away = me - u.transform.position;
            float dist = away.magnitude;
            if (dist >= separationRange || dist < 0.01f) continue;
            away.y = 0f;
            if (away.sqrMagnitude < 1e-4f) away = transform.right;
            sum += away.normalized * (1f - dist / separationRange);
        }
        return sum;
    }

    // Desired heading plus the separation push.
    float Separate(float yaw, float weight)
    {
        if (weight <= 0f) return yaw;
        Vector3 dir = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward + Separation() * weight;
        return dir.sqrMagnitude > 1e-4f ? YawOf(dir) : yaw;
    }

    // Arrive at a point moving at pointVel: the approach speed is what the weak brakes can still stop
    // from. Throttle/brake hold the speed along the heading; the heading follows the wanted velocity
    // (or faces `face` once there).
    void SteerTo(Vector3 point, Vector3 pointVel, float maxSpeed, float verticalSpeed, Vector3? face = null, float separate = 0f)
    {
        Vector3 to = point - transform.position;
        to.y = 0f;
        float d = to.magnitude;
        float approach = Mathf.Min(maxSpeed, Mathf.Sqrt(2f * policeBrake * 0.6f * d));
        Vector3 pv = new Vector3(pointVel.x, 0f, pointVel.z);
        Vector3 desired = pv + (d > 0.05f ? to / d * approach : Vector3.zero);

        float yaw = Car.Yaw;
        if (desired.sqrMagnitude > 3f * 3f) yaw = YawOf(desired);
        else if (face.HasValue)
        {
            Vector3 f = face.Value - transform.position;
            f.y = 0f;
            if (f.sqrMagnitude > 0.25f) yaw = YawOf(f);
        }
        Vector3 fwd = Heading;
        float throttle = (Vector3.Dot(desired, fwd) - Vector3.Dot(Car.Velocity, fwd)) / 2f;
        Car.Drive(Separate(yaw, separate), throttle, point.y + pointVel.y * 0.3f, verticalSpeed, Model);
    }

    // Plain point-to-point flight (transport to the station).
    public void FlyTo(Vector3 point, float maxSpeed, float verticalSpeed) => SteerTo(point, Vector3.zero, maxSpeed, verticalSpeed);

    // Brake to a stop and hold this height (driver down, mirror check).
    public void Halt() => Car.Drive(Car.Yaw, -1f, transform.position.y, policeVerticalSpeed, Model);

    // ---------- chase ----------

    public void Chase(FlyingVehicle target)
    {
        AtSlot = false;
        Vector3 me = transform.position, tp = target.transform.position, tv = target.Velocity;
        Quaternion tr = target.PlatformRotation;

        Vector3 rel = tp - me;
        float dist = rel.magnitude;
        float closing = Mathf.Max(5f, Vector3.Dot(Car.Velocity - tv, rel / Mathf.Max(dist, 0.01f)));
        float lead = Mathf.Min(maxLeadTime, dist / closing);
        Vector3 flat = new Vector3(rel.x, 0f, rel.z);

        switch (state)
        {
            case ChaseState.Approach:
            {
                Vector3 slot = tp + tv * lead + tr * ApproachSlot + Vector3.up * (LayerOffset * TrafficAuthority.Spacing);
                SteerTo(slot, tv, policeTopSpeed, policeVerticalSpeed, null, separationWeight);
                if (MayRam && dist < ramRange)
                {
                    state = ChaseState.Ram;
                    contacted = false;
                    ramDir = flat.sqrMagnitude > 0.01f ? flat.normalized : Heading;
                }
                break;
            }
            case ChaseState.Ram:
            {
                // Full throttle at a point through the target: no velocity matching, so it can miss.
                Vector3 aim = tp + tv * lead + ramDir * ramThrough;
                Car.Drive(YawOf(aim - me), 1f, aim.y, policeVerticalSpeed, Model);
                bool passed = Vector3.Dot(flat, Heading) < 0f;
                if (contacted) { state = ChaseState.Recover; recoverUntil = Time.time + hitRecoverTime; }
                else if (passed || dist > ramRange * 2.5f) { state = ChaseState.Recover; recoverUntil = Time.time + missRecoverTime; }
                break;
            }
            default:
            {
                // Brake and swing round toward the target before trying again.
                bool facing = Vector3.Angle(Heading, flat) < 40f;
                Car.Drive(Separate(YawOf(flat), separationWeight), facing ? 0.3f : -1f,
                          tp.y + LayerOffset * TrafficAuthority.Spacing, policeVerticalSpeed, Model);
                if (Time.time >= recoverUntil) state = ChaseState.Approach;
                break;
            }
        }
        contacted = false;
    }

    // ---------- slots ----------

    // Keep a slot in the target's yaw frame, moving with it, facing the target. Arrives from outside
    // (via a point 10 m further out) so it doesn't plough into the target on the way.
    public void HoldSlot(FlyingVehicle target, Vector3 localOffset, float verticalSpeed)
    {
        state = ChaseState.Approach;
        Vector3 tp = target.transform.position;
        Vector3 slot = tp + target.PlatformRotation * localOffset;
        Vector3 me = transform.position;
        Vector3 outward = slot - tp;
        outward.y = 0f;
        Vector3 aim = slot;
        Vector3 toSlot = slot - me;
        toSlot.y = 0f;
        if (outward.sqrMagnitude > 0.01f && toSlot.magnitude > 3f)
        {
            Vector3 o = outward.normalized;
            Vector3 fromSlot = me - slot;
            fromSlot.y = 0f;
            float outside = Vector3.Dot(fromSlot, o);
            float lateral = (fromSlot - o * outside).magnitude;
            if (outside < 2f || lateral > outside) aim = slot + o * 10f;
        }
        SteerTo(aim, target.Velocity, policeTopSpeed, verticalSpeed, tp);
        AtSlot = (me - slot).sqrMagnitude < 2f * 2f;
    }

    public void HoverNear(Vector3 point)
    {
        AtSlot = false;
        state = ChaseState.Approach;
        Vector3 away = transform.position - point;
        away.y = 0f;
        away = away.sqrMagnitude > 0.01f ? away.normalized : Vector3.forward;
        SteerTo(point + away * 12f + Vector3.up * 5f, Vector3.zero, 25f, policeVerticalSpeed, point, separationWeight);
    }

    // ---------- shooting ----------

    // Bursts of hitscan shots at the target's car while it is in sight; the spread tightens the
    // longer it stays in sight.
    public void UpdateShooting(FlyingVehicle target)
    {
        float dt = Time.deltaTime;
        Vector3 origin = transform.position + Vector3.up * 1.2f;
        Vector3 to = target.transform.position - origin;
        float dist = to.magnitude;
        bool sight = dist <= shootRange && PoliceDispatch.LineOfSight(origin, target.transform.position);
        sightTime = sight ? sightTime + dt : 0f;
        if (!sight) return;

        if (Time.time >= nextBurst)
        {
            shotsLeft = burstShots;
            nextBurst = Time.time + burstInterval;
            nextShot = Time.time;
        }
        if (shotsLeft <= 0 || Time.time < nextShot) return;
        shotsLeft--;
        nextShot = Time.time + shotInterval;

        float spread = Mathf.Lerp(spreadStart, spreadLocked, Mathf.Clamp01(sightTime / lockTime));
        Vector2 r = Random.insideUnitCircle * spread;
        Vector3 dir = Quaternion.LookRotation(to) * Quaternion.Euler(r.y, r.x, 0f) * Vector3.forward;
        Vector3 end = origin + dir * shootRange * 1.5f;
        if (Physics.Raycast(origin, dir, out var hit, shootRange * 1.5f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
        {
            end = hit.point;
            if (hit.collider.GetComponentInParent<FlyingVehicle>() == target) target.Damage(damagePerHit);
            ShowSpark(hit.point);
        }
        ShowTracer(origin, end);
    }

    void Update()
    {
        if (tracer != null && tracer.enabled && Time.time > tracerUntil) tracer.enabled = false;
        if (spark != null && spark.gameObject.activeSelf && Time.time > sparkUntil) spark.gameObject.SetActive(false);
    }

    // ---------- placeholder effects ----------

    void ShowTracer(Vector3 a, Vector3 b)
    {
        if (tracerMaterial == null) return;
        if (tracer == null)
        {
            var go = new GameObject("Tracer");
            tracer = go.AddComponent<LineRenderer>();
            tracer.sharedMaterial = tracerMaterial;
            tracer.positionCount = 2;
            tracer.startWidth = tracer.endWidth = 0.08f;
            tracer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            tracer.receiveShadows = false;
        }
        tracer.SetPosition(0, a);
        tracer.SetPosition(1, b);
        tracer.enabled = true;
        tracerUntil = Time.time + 0.06f;
    }

    void ShowSpark(Vector3 p)
    {
        if (sparkMaterial == null) return;
        if (spark == null)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "Spark";
            Destroy(go.GetComponent<Collider>());
            go.transform.localScale = Vector3.one * 0.4f;
            var rend = go.GetComponent<Renderer>();
            rend.sharedMaterial = sparkMaterial;
            rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            spark = go.transform;
        }
        spark.position = p;
        spark.rotation = Random.rotation;
        spark.gameObject.SetActive(true);
        sparkUntil = Time.time + 0.1f;
    }

    void OnDestroy()
    {
        if (tracer != null) Destroy(tracer.gameObject);
        if (spark != null) Destroy(spark.gameObject);
    }
}
