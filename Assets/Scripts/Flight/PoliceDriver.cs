using UnityEngine;

// Drives a police FlyingVehicle. Patrol: it is ordinary lane traffic (FlyingVehicle's own AI).
// Everything else uses the car's autopilot (same physics and collision sweep as any car):
//   Chase     : fly to an intercept point at pursuitSpeed; inside ramRange push straight into the target.
//   HoldSlot  : keep a slot around the target (docked at its side to tow it, or boxing it in).
//   HoverNear : placeholder foot pursuit, hover near the player.
// At wanted level 3 PoliceDispatch also calls UpdateShooting. Runs before the cars move.
[DefaultExecutionOrder(-15)]
[RequireComponent(typeof(FlyingVehicle))]
public class PoliceDriver : MonoBehaviour
{
    public float pursuitSpeed = 60f;
    [Tooltip("Cap on how far ahead (s) the intercept point leads the target.")]
    public float maxLeadTime = 1.5f;
    [Tooltip("Inside this distance, aim straight at the target to push into contact.")]
    public float ramRange = 12f;

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

    float sightTime, nextBurst, nextShot;
    int shotsLeft;
    LineRenderer tracer;
    float tracerUntil;
    Transform spark;
    float sparkUntil;

    void Awake() => Car = GetComponent<FlyingVehicle>();
    void OnEnable() => PoliceDispatch.Register(this);
    void OnDisable() => PoliceDispatch.Unregister(this);

    public void Pursue()
    {
        IsPursuing = true;
        Car.ReportContacts = true;
    }

    public void Patrol()
    {
        IsPursuing = false;
        AtSlot = false;
        sightTime = 0f;
        Car.ReportContacts = false;
        Car.ClearAutopilot(); // the lane AI flies it back to its lane
    }

    public void Chase(FlyingVehicle target)
    {
        AtSlot = false;
        Vector3 me = transform.position, tp = target.transform.position, tv = target.Velocity;
        Vector3 rel = tp - me;
        float dist = rel.magnitude;
        if (dist < ramRange)
        {
            Car.SetAutopilot(tp, tv, pursuitSpeed); // push into the target, matching its velocity
            return;
        }
        float closing = Mathf.Max(5f, Vector3.Dot(Car.Velocity - tv, rel / Mathf.Max(dist, 0.01f)));
        float lead = Mathf.Min(maxLeadTime, dist / closing);
        Car.SetAutopilot(tp + tv * lead, Vector3.zero, pursuitSpeed);
    }

    // Keep a slot in the target's yaw frame, moving with it. Approaches from outside the slot (aims a
    // few metres further out until close), so it doesn't plough into the target on the way.
    public void HoldSlot(FlyingVehicle target, Vector3 localOffset)
    {
        Vector3 slot = target.transform.position + target.PlatformRotation * localOffset;
        Vector3 outward = slot - target.transform.position;
        outward.y = 0f;
        Vector3 aim = slot;
        if ((transform.position - slot).sqrMagnitude > 4f * 4f && outward.sqrMagnitude > 0.01f) aim += outward.normalized * 3f;
        Car.SetAutopilot(aim, target.Velocity, pursuitSpeed);
        AtSlot = (transform.position - slot).sqrMagnitude < 1.5f * 1.5f;
    }

    public void HoverNear(Vector3 point)
    {
        AtSlot = false;
        Vector3 away = transform.position - point;
        away.y = 0f;
        away = away.sqrMagnitude > 0.01f ? away.normalized : Vector3.forward;
        Car.SetAutopilot(point + away * 10f + Vector3.up * 5f, Vector3.zero, 25f);
    }

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
