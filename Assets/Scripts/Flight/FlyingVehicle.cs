using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

// Three flight modes:
//   Lane  : attached to a MAGNETIC lane. You fly freely; the lane pulls you toward it and
//           turns you along it. Pull is strongest at the line and fades with distance.
//           Steer far enough away and you break free.
//   Layer : free horizontal flight, altitude locked to the current layer.
//   Free  : full 3D, unregistered. Police get interested.
// Mouse always aims the camera. The car turns toward the aim only while W/S is held (Halo style).
// Space        : cycle layers (ping-pong)
// Ctrl (tap)   : Lane <-> Layer (magnet off / magnet on to nearest lane in range)
// Ctrl + Space : Free <-> Layer
// E tap / hold : exit through the door / onto the roof
public enum FlightMode { Lane, Layer, Free }

// Moves before the player so a rider standing on the roof is carried with this frame's motion.
[DefaultExecutionOrder(-10)]
public class FlyingVehicle : MonoBehaviour
{
    [Header("Lane")]
    [Tooltip("Leave empty for a parked car that sits still until hijacked.")]
    public LanePath path;
    public float startDistance = 0f;
    public LaneLayer startLayer = LaneLayer.Middle;
    [Tooltip("Traffic speed limit. Around 35 cars hold every bend on the magnet alone.")]
    public float aiCruiseSpeed = 20f;
    public float middleLaneMaxSpeed = 70f;
    public float sideLaneMaxSpeed = 35f;
    public float acceleration = 25f;
    public float braking = 40f;
    [Tooltip("How quickly the magnetic line itself glides up/down when you change layer.")]
    public float laneChangeSmoothTime = 0.6f;

    [Header("Lane magnet")]
    [Tooltip("Sideways range of the magnet. Beyond it you break free. Also the range for Ctrl to attach.")]
    public float captureRadius = 15f;
    [Tooltip("Pull toward the line at full strength (m/s^2).")]
    public float magnetStrength = 20f;
    [Tooltip("How fast the magnet turns the car along the lane at full strength (deg/s). Keep below headingTurnRate so hard steering can break free.")]
    public float magnetAlignRate = 70f;
    [Tooltip("Damps sideways wobble around the line, scaled by magnet strength.")]
    public float magnetDamping = 3f;
    [Tooltip("Higher = the pull fades faster with distance.")]
    public float magnetFalloff = 2f;
    [Tooltip("Within this distance the pull eases off so the car settles on the line instead of jittering.")]
    public float magnetSoftZone = 1.5f;
    public float strafeAcceleration = 18f;
    [Tooltip("How quickly sideways slide relative to the car bleeds off.")]
    public float lateralGrip = 2.5f;

    [Header("Layer / Free mode")]
    public float layerMaxSpeed = 45f;
    public float freeMaxSpeed = 55f;
    public float strafeFactor = 0.6f;
    public float steerSensitivity = 0.12f;
    public float altitudeSmoothTime = 0.8f;
    public float minAltitude = 1.5f;
    [Tooltip("Deg/sec the car turns toward where you're looking, only while W or S is held. Lower = heavier.")]
    public float headingTurnRate = 90f;
    public float pitchTurnRate = 60f;

    [Header("Collisions")]
    [Tooltip("0 = dead stop on impact, 1 = full rebound.")]
    public float bounciness = 0.25f;
    [Tooltip("Speed lost scraping along walls and other cars.")]
    public float scrapeFriction = 0.2f;
    public float impactShake = 0.35f;

    [Header("Traffic AI")]
    [Tooltip("Gap kept to the car in front, in metres (car is 6 m long, so 9 = 1.5 cars).")]
    public float followGap = 9f;
    [Tooltip("Below this gap the car brakes to a stop.")]
    public float minGap = 3f;
    [Tooltip("How hard it closes or opens the gap toward followGap.")]
    public float followGain = 0.8f;
    [Tooltip("Speed while finding its way back to its lane after being knocked off.")]
    public float recoverSpeed = 12f;
    [Tooltip("Aims this far ahead on the lane while returning, so it merges instead of hitting it square.")]
    public float recoverLead = 15f;
    public float yieldLookRange = 80f;
    [Tooltip("Cars closer than this at closest approach count as a conflict.")]
    public float yieldClearance = 7f;
    [Tooltip("Seconds ahead to look for conflicts.")]
    public float yieldHorizon = 3f;

    [Header("Camera")]
    public Transform cockpitAnchor;
    public float maxCameraDistance = 14f;
    public float zoomStep = 0.2f;

    [Header("Exit")]
    [Tooltip("Hold E this long to exit onto the roof instead of through the door.")]
    public float roofExitHoldTime = 0.35f;

    public bool IsOccupied => driver != null;
    public Vector3 Velocity => velocity;
    // Frame a rider stands in: yaw only, so banking and pitch never fling them.
    public Vector3 PlatformPosition => transform.position;
    public Quaternion PlatformRotation => Quaternion.Euler(0f, yaw, 0f);
    // Top of the body collider in world space.
    public float RoofY => transform.position.y + colCenter.y + colHalf.y;
    // Body collider half size and centre, in the car's own frame.
    public Vector3 BodyHalfExtents => colHalf;
    public Vector3 BodyCenterLocal => colCenter;
    public static IReadOnlyList<FlyingVehicle> Active => All;
    // The car the player is driving, if any.
    public static FlyingVehicle Driven { get; private set; }
    // Lane this car is registered on for the traffic AI (only while in Lane mode).
    public LanePath RegisteredPath => registeredPath;
    public float LaneDistance => distance;
    public LaneLayer CurrentLayer => currentLayer;
    // Slot in TrafficSystem.States for the current frame.
    public int TrafficIndex { get; set; } = -1;

    // HUD readouts (drawn by VehicleHUD).
    public float HudSpeed => Mode == FlightMode.Lane ? speed : velocity.magnitude;
    public float MagnetHold => magnetHold;
    public bool IsLayerOpen(LaneLayer layer) => IsAvailable(layer);
    public bool InNoSwitchZone => path != null && path.IsNoSwitch(distance);
    public string FlashMessage => Time.time < flashUntil ? flash : null;

    // A traffic agent: nobody driving and it knows which lane it belongs to.
    bool IsAI => !IsOccupied && path != null;
    static readonly List<FlyingVehicle> All = new List<FlyingVehicle>();
    // Changing mode keeps the lane's car list in sync (registered only while in Lane mode).
    public FlightMode Mode { get => mode; private set { mode = value; SyncLaneRegistration(); } }
    FlightMode mode = FlightMode.Lane;
    LanePath registeredPath;
    bool started;

    LaneLayer currentLayer;
    int cycleDir = 1;
    float distance, speed, zoom;
    Vector2 currentOffset, offsetVel;   // offset of the magnetic line from the base path
    Vector3 velocity;
    float yaw, pitch, altVel;
    float aimYaw, aimPitch;
    float magnetHold;                   // 0..1, for the HUD
    bool parked, ctrlComboUsed;
    FirstPersonController driver;
    Camera cam;
    int enterFrame = -1;
    bool eArmed; float eDownTime;
    LanePath[] allLanes;
    string flash; float flashUntil;
    BoxCollider bodyCol;
    Vector3 colHalf, colCenter;
    float shake;
    readonly RaycastHit[] hitBuf = new RaycastHit[16];
    readonly Collider[] overlapBuf = new Collider[16];
    static int collisionMask;   // everything except the on-foot player

    void OnEnable()
    {
        All.Add(this);
        SyncLaneRegistration();
    }

    void OnDisable()
    {
        All.Remove(this);
        SyncLaneRegistration();
        if (Driven == this) Driven = null;
    }

    void SyncLaneRegistration()
    {
        LanePath want = started && isActiveAndEnabled && mode == FlightMode.Lane ? path : null;
        if (want == registeredPath) return;
        TrafficSystem.Unregister(this, registeredPath);
        registeredPath = want;
        if (want != null) TrafficSystem.Register(this, want);
    }

    void Start()
    {
        started = true;
        allLanes = FindObjectsByType<LanePath>(FindObjectsSortMode.None);
        yaw = transform.eulerAngles.y;
        int playerLayer = LayerMask.NameToLayer("Player");
        collisionMask = Physics.DefaultRaycastLayers & ~(playerLayer >= 0 ? 1 << playerLayer : 0);
        bodyCol = GetComponentInChildren<BoxCollider>();
        if (bodyCol != null)
        {
            colHalf = Vector3.Scale(bodyCol.size, bodyCol.transform.lossyScale) * 0.5f;
            colCenter = transform.InverseTransformPoint(bodyCol.transform.TransformPoint(bodyCol.center));
        }
        if (path == null) { Mode = FlightMode.Layer; parked = true; currentLayer = TrafficAuthority.NearestLayer(transform.position.y); }
        else
        {
            distance = startDistance; currentLayer = startLayer; speed = aiCruiseSpeed;
            float w0 = path.LaneWeight(startLayer, startDistance, out var o0);
            currentOffset = startLayer == LaneLayer.Middle ? Vector2.zero : o0 * w0;
            velocity = path.SmoothForward(startDistance) * speed;
        }
        SyncLaneRegistration();
    }

    void Update()
    {
        var kb = Keyboard.current;
        var mouse = Mouse.current;
        if (IsOccupied && kb != null) HandleModeInput(kb);

        switch (Mode)
        {
            case FlightMode.Lane: UpdateLaneMagnetic(kb, mouse); break;
            case FlightMode.Layer:
                if (IsAI) UpdateRecover(); else UpdateLayer(kb, mouse);
                break;
            case FlightMode.Free:
                if (IsAI) UpdateRecover(); else UpdateFree(kb, mouse);
                break;
        }
    }

    // ---------- input / mode switching ----------

    void HandleModeInput(Keyboard kb)
    {
        bool ctrl = kb.leftCtrlKey.isPressed || kb.rightCtrlKey.isPressed;
        if (kb.spaceKey.wasPressedThisFrame)
        {
            if (ctrl) { ctrlComboUsed = true; ToggleFree(); }
            else OnSpace();
        }
        if (kb.leftCtrlKey.wasReleasedThisFrame || kb.rightCtrlKey.wasReleasedThisFrame)
        {
            if (!ctrlComboUsed) ToggleLaneLock();
            ctrlComboUsed = false;
        }
        // Tap E = door, hold E = roof. Only presses made after entering count.
        if (kb.eKey.wasPressedThisFrame && Time.frameCount != enterFrame) { eArmed = true; eDownTime = Time.time; }
        if (eArmed)
        {
            if (kb.eKey.wasReleasedThisFrame) Exit(false);
            else if (kb.eKey.isPressed && Time.time - eDownTime >= roofExitHoldTime) Exit(true);
        }
    }

    void OnSpace()
    {
        if (Mode == FlightMode.Lane) CycleLaneLayer();
        else if (Mode == FlightMode.Layer)
        {
            currentLayer = (LaneLayer)NextLayer((int)currentLayer);
            Flash($"Layer: {currentLayer} (off-lane change)");
            TrafficAuthority.Instance?.ReportViolation(this, "Off-lane layer change");
        }
    }

    void ToggleFree()
    {
        if (Mode == FlightMode.Free) { LockToNearestLayer(); return; }
        Mode = FlightMode.Free;
        pitch = 0f;
        Flash("FREE FLIGHT: unregistered");
    }

    void ToggleLaneLock()
    {
        switch (Mode)
        {
            case FlightMode.Lane:
                Mode = FlightMode.Layer;
                Flash("Magnet off");
                break;
            case FlightMode.Layer:
                if (!TryAttachToLane()) Flash("No lane in range");
                break;
            case FlightMode.Free:
                Flash("Lock to a layer first (Ctrl+Space)");
                break;
        }
    }

    int NextLayer(int cur)
    {
        int target = cur + cycleDir;
        if (target < 0 || target > 2) { cycleDir = -cycleDir; target = cur + cycleDir; }
        return target;
    }

    void CycleLaneLayer()
    {
        int cur = (int)currentLayer;
        int savedDir = cycleDir;
        int target = NextLayer(cur);

        if (target != 1 && !IsAvailable((LaneLayer)target))
        {
            int other = 2 - target;
            if (!IsAvailable((LaneLayer)other)) { cycleDir = savedDir; return; }
            target = other;
            cycleDir = other > cur ? 1 : -1;
        }
        currentLayer = (LaneLayer)target;
        if (path.IsNoSwitch(distance))
        {
            Flash("Illegal lane switch!");
            TrafficAuthority.Instance?.ReportViolation(this, "Illegal lane switch");
        }
    }

    bool IsAvailable(LaneLayer layer) => path != null && path.LaneWeight(layer, distance, out _) > 0.9f;

    void LockToNearestLayer()
    {
        currentLayer = TrafficAuthority.NearestLayer(transform.position.y);
        Mode = FlightMode.Layer;
        pitch = 0f; altVel = 0f;
        Flash($"Locked to {currentLayer} layer");
    }

    bool TryAttachToLane()
    {
        LanePath best = null; float bestD = 0f, bestSq = captureRadius * captureRadius;
        foreach (var lp in allLanes)
        {
            if (lp == null) continue;
            if (lp.FindNearest(transform.position, currentLayer, out float d, out float sq) && sq < bestSq)
            { best = lp; bestD = d; bestSq = sq; }
        }
        if (best == null) return false;

        path = best;
        distance = best.Project(transform.position, bestD);
        float w = best.LaneWeight(currentLayer, distance, out var laneOff);
        currentOffset = currentLayer == LaneLayer.Middle ? Vector2.zero : laneOff * w;
        offsetVel = Vector2.zero;
        Mode = FlightMode.Lane;
        Flash("Magnet on");
        return true;
    }

    // ---------- helpers ----------

    Vector2 MoveInput(Keyboard kb)
    {
        if (!IsOccupied || kb == null) return Vector2.zero;
        return new Vector2((kb.dKey.isPressed ? 1f : 0f) - (kb.aKey.isPressed ? 1f : 0f),
                           (kb.wKey.isPressed ? 1f : 0f) - (kb.sKey.isPressed ? 1f : 0f));
    }

    void UpdateAim(Mouse mouse)
    {
        if (!IsOccupied || mouse == null || Cursor.lockState != CursorLockMode.Locked) return;
        Vector2 s = mouse.delta.ReadValue() * steerSensitivity;
        aimYaw += s.x;
        aimPitch = Mathf.Clamp(aimPitch - s.y, -80f, 80f);
    }

    static Vector3 Flat(Vector3 v) { v.y = 0f; return v.sqrMagnitude > 1e-6f ? v.normalized : Vector3.forward; }

    // Updates the magnetic line's own layer offset (glides between layers, merges when a lane ends).
    Vector3 UpdateLineOffset(out Vector3 laneFwd)
    {
        float w = path.LaneWeight(currentLayer, distance, out Vector2 laneOffset);
        if (currentLayer != LaneLayer.Middle && w <= 0.01f) currentLayer = LaneLayer.Middle; // lane converged
        Vector2 target = currentLayer == LaneLayer.Middle ? Vector2.zero : laneOffset * w;
        currentOffset = Vector2.SmoothDamp(currentOffset, target, ref offsetVel, laneChangeSmoothTime);

        path.Sample(distance, out Vector3 basePos, out _);
        laneFwd = path.SmoothForward(distance);
        return path.ToWorld(basePos, laneFwd, currentOffset);
    }

    // ---------- movement ----------

    // Lane mode for player AND traffic: free flight plus a magnetic pull toward the line.
    // The player supplies throttle/steer/strafe; traffic only manages its speed.
    void UpdateLaneMagnetic(Keyboard kb, Mouse mouse)
    {
        if (path == null) { Mode = FlightMode.Layer; return; }
        float dt = Time.deltaTime;
        Vector2 input = MoveInput(kb);
        UpdateAim(mouse);

        // Traffic drifts back to its preferred side lane whenever it legally can.
        if (IsAI && currentLayer == LaneLayer.Middle && startLayer != LaneLayer.Middle &&
            IsAvailable(startLayer) && !path.IsNoSwitch(distance))
            currentLayer = startLayer;

        distance = path.Project(transform.position, distance);
        Vector3 linePoint = UpdateLineOffset(out Vector3 laneFwd);
        Vector3 laneFlat = Flat(laneFwd);

        // Sideways distance to the line (horizontal only, ignoring progress along it).
        Vector3 toLine = linePoint - transform.position;
        toLine.y = 0f;
        toLine -= laneFlat * Vector3.Dot(toLine, laneFlat);
        float d = toLine.magnitude;

        if (d > captureRadius)
        {
            Mode = FlightMode.Layer; // traffic switches to recovery from here
            magnetHold = 0f;
            if (IsOccupied) Flash("Broke free of the lane");
            return;
        }

        float w = Mathf.Pow(1f - d / captureRadius, magnetFalloff); // 1 at the line, 0 at the edge
        magnetHold = w;

        // Magnet turns the car along the lane and carries the camera aim with it,
        // so the view flows through bends without you touching the mouse.
        float laneYaw = Quaternion.LookRotation(laneFlat).eulerAngles.y;
        float aligned = Mathf.MoveTowardsAngle(yaw, laneYaw, magnetAlignRate * w * dt);
        float carried = Mathf.DeltaAngle(yaw, aligned);
        yaw += carried;
        aimYaw += carried;

        // Your steering fights the magnet: it wins near the edge, struggles near the line.
        if (input.y != 0f) yaw = Mathf.MoveTowardsAngle(yaw, aimYaw, headingTurnRate * dt);

        Quaternion heading = Quaternion.Euler(0f, yaw, 0f);
        Vector3 carFwd = heading * Vector3.forward, carRight = heading * Vector3.right;

        Vector3 hv = new Vector3(velocity.x, 0f, velocity.z);
        float fwdSpeed = Vector3.Dot(hv, carFwd);
        Vector3 side = hv - carFwd * fwdSpeed;

        if (IsOccupied)
        {
            float max = currentLayer == LaneLayer.Middle ? middleLaneMaxSpeed : sideLaneMaxSpeed;
            if (input.y > 0f) fwdSpeed = Mathf.MoveTowards(fwdSpeed, max, acceleration * dt);
            else if (input.y < 0f) fwdSpeed = Mathf.MoveTowards(fwdSpeed, 0f, braking * dt);
            if (fwdSpeed > max) fwdSpeed = Mathf.MoveTowards(fwdSpeed, max, braking * dt);
        }
        else
        {
            float target = AITargetSpeed(carFwd);
            fwdSpeed = Mathf.MoveTowards(fwdSpeed, target, (target < fwdSpeed ? braking : acceleration) * dt);
        }

        side += carRight * input.x * strafeAcceleration * dt;
        side *= Mathf.Exp(-lateralGrip * dt);
        hv = carFwd * fwdSpeed + side;

        // The pull: strongest at the line, eased inside the soft zone so it settles instead of jittering.
        if (d > 1e-4f)
        {
            float ease = Mathf.Clamp01(d / magnetSoftZone);
            hv += (toLine / d) * magnetStrength * w * ease * dt;
        }
        Vector3 vPerp = hv - laneFlat * Vector3.Dot(hv, laneFlat);
        hv -= vPerp * Mathf.Clamp01(magnetDamping * w * dt);

        Vector3 pos = transform.position + hv * dt;
        pos.y = Mathf.SmoothDamp(pos.y, linePoint.y, ref altVel, laneChangeSmoothTime);
        velocity = new Vector3(hv.x, altVel, hv.z);

        float bank = Mathf.Clamp(-Vector3.Dot(hv, carRight) * 0.6f, -20f, 20f);
        float p = Mathf.Clamp(-altVel * 1.5f, -20f, 20f);
        Quaternion rot = heading * Quaternion.Euler(p, 0f, bank);
        pos = MoveAndCollide(transform.position, pos - transform.position, rot);
        speed = Vector3.Dot(velocity, carFwd);
        transform.SetPositionAndRotation(pos, rot);
    }

    // Knocked off its lane: fly the straight 3D line back to the nearest point on it
    // (aiming a little ahead so it merges), slowly, then switch the magnet back on.
    void UpdateRecover()
    {
        float dt = Time.deltaTime;
        distance = path.Project(transform.position, distance, 120f);
        float w = path.LaneWeight(currentLayer, distance, out var laneOff);
        if (currentLayer != LaneLayer.Middle && w <= 0.01f) currentLayer = LaneLayer.Middle;
        currentOffset = currentLayer == LaneLayer.Middle ? Vector2.zero : laneOff * w;
        offsetVel = Vector2.zero;

        path.Sample(distance, out var nearBase, out _);
        Vector3 nearFwd = Flat(path.SmoothForward(distance));
        Vector3 nearest = path.ToWorld(nearBase, nearFwd, currentOffset);
        path.Sample(distance + recoverLead, out var aimBase, out _);
        Vector3 aimPoint = path.ToWorld(aimBase, path.SmoothForward(distance + recoverLead), currentOffset);

        Vector3 to = aimPoint - transform.position;
        Vector3 desired = to.sqrMagnitude > 1e-4f ? to.normalized * recoverSpeed : Vector3.zero;
        velocity = Vector3.MoveTowards(velocity, desired, acceleration * dt);

        Vector3 flatVel = new Vector3(velocity.x, 0f, velocity.z);
        if (flatVel.sqrMagnitude > 0.5f)
            yaw = Mathf.MoveTowardsAngle(yaw, Quaternion.LookRotation(flatVel).eulerAngles.y, headingTurnRate * dt);
        Quaternion rot = Quaternion.Euler(0f, yaw, 0f);
        Vector3 pos = MoveAndCollide(transform.position, velocity * dt, rot);
        transform.SetPositionAndRotation(pos, rot);

        Vector3 off = nearest - pos;
        float vertical = Mathf.Abs(off.y);
        off.y = 0f;
        off -= nearFwd * Vector3.Dot(off, nearFwd);
        if (off.magnitude < captureRadius * 0.3f && vertical < 3f)
        {
            Mode = FlightMode.Lane; // locked on again; it will accelerate back to the limit
            altVel = 0f;
            speed = Vector3.Dot(velocity, nearFwd);
        }
    }

    // Speed limit, then: follow the car ahead at followGap, and yield at crossings.
    // Uses the TrafficSystem snapshot: the leader is the next car on our lane and layer,
    // and only cars in the neighbouring grid cells are checked for conflicts.
    float AITargetSpeed(Vector3 fwd)
    {
        TrafficSystem.EnsureBuilt();
        float target = aiCruiseSpeed;
        int self = TrafficIndex;
        var states = TrafficSystem.States;
        if (self < 0 || self >= TrafficSystem.Count || states[self].car != this) return target;

        Vector3 me = transform.position;
        float range2 = yieldLookRange * yieldLookRange;
        float carLength = colHalf.z * 2f;
        var mine = states[self];

        // Same lane, same layer: follow the car ahead.
        if (mine.leader >= 0 && mine.leaderGap <= yieldLookRange)
        {
            float gap = mine.leaderGap - carLength;
            float follow = gap < minGap ? 0f : Vector3.Dot(states[mine.leader].vel, fwd) + followGain * (gap - followGap);
            target = Mathf.Min(target, Mathf.Max(0f, follow));
        }

        // Anything else on a collision course (crossings, cars cutting in, the player's car).
        TrafficSystem.CellOf(me, out int cx, out int cz);
        for (int dx = -1; dx <= 1; dx++)
        for (int dz = -1; dz <= 1; dz++)
        {
            if (!TrafficSystem.TryCell(cx + dx, cz + dz, out int start, out int count)) continue;
            for (int k = start; k < start + count; k++)
            {
                int j = TrafficSystem.CellCar(k);
                if (j == self) continue;
                ref var other = ref states[j];
                if (other.path != null && other.path == mine.path && other.layer == mine.layer) continue; // leader handles it

                Vector3 r = other.pos - me;
                if (r.sqrMagnitude > range2) continue;
                float ahead = Vector3.Dot(r, fwd);
                if (ahead <= 0f) continue;

                Vector3 u = other.vel - velocity;
                float uu = u.sqrMagnitude;
                float tca = uu > 1e-3f ? Mathf.Clamp(-Vector3.Dot(r, u) / uu, 0f, yieldHorizon) : 0f;
                if ((r + u * tca).sqrMagnitude > yieldClearance * yieldClearance) continue;

                float otherAhead = Vector3.Dot(-r, other.fwd);
                bool iYield = other.occupied || ahead > otherAhead + 0.01f ||
                              (Mathf.Abs(ahead - otherAhead) <= 0.01f && GetHashCode() > other.car.GetHashCode());
                if (iYield) target = Mathf.Min(target, Mathf.Max(0f, Vector3.Dot(other.vel, fwd) * 0.9f));
            }
        }
        return target;
    }

    // Sweep the car's box along its move, stop at the first hit, then push out of
    // anything still overlapping (e.g. traffic that drove into us).
    Vector3 MoveAndCollide(Vector3 from, Vector3 delta, Quaternion rot)
    {
        if (bodyCol == null) return from + delta;
        float dist = delta.magnitude;
        Vector3 moved = delta;
        const float skin = 0.05f;

        if (dist > 1e-5f)
        {
            Vector3 dir = delta / dist;
            int n = Physics.BoxCastNonAlloc(from + rot * colCenter, colHalf, dir, hitBuf, rot, dist + skin,
                                            collisionMask, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue; int bestIdx = -1;
            for (int i = 0; i < n; i++)
            {
                if (hitBuf[i].collider == bodyCol || hitBuf[i].distance <= 0f) continue;
                if (hitBuf[i].distance < best) { best = hitBuf[i].distance; bestIdx = i; }
            }
            if (bestIdx >= 0)
            {
                moved = dir * Mathf.Max(0f, best - skin);
                Bounce(hitBuf[bestIdx].normal, hitBuf[bestIdx].collider);
            }
        }

        Vector3 pos = from + moved;
        Vector3 bodyPos = pos + rot * bodyCol.transform.localPosition;
        Quaternion bodyRot = rot * bodyCol.transform.localRotation;
        int count = Physics.OverlapBoxNonAlloc(pos + rot * colCenter, colHalf, overlapBuf, rot,
                                               collisionMask, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < count; i++)
        {
            var c = overlapBuf[i];
            if (c == bodyCol) continue;
            if (Physics.ComputePenetration(bodyCol, bodyPos, bodyRot, c, c.transform.position, c.transform.rotation,
                                           out Vector3 sepDir, out float sepDist))
            {
                pos += sepDir * sepDist;
                bodyPos += sepDir * sepDist;
                Bounce(sepDir, c);
            }
        }
        return pos;
    }

    void Bounce(Vector3 normal, Collider other)
    {
        var otherCar = other.GetComponentInParent<FlyingVehicle>();
        Vector3 otherVel = otherCar != null ? otherCar.velocity : Vector3.zero;
        Vector3 rel = velocity - otherVel;
        float vn = Vector3.Dot(rel, normal);
        if (vn >= 0f) return;

        if (otherCar != null)
        {
            // Equal masses: split the impulse so the other car gets shoved too.
            float j = -(1f + bounciness) * vn * 0.5f;
            Vector3 tangential = rel - normal * vn;
            velocity += normal * j - tangential * scrapeFriction * 0.5f;
            otherCar.velocity -= normal * j - tangential * scrapeFriction * 0.5f;
            otherCar.AddImpact(-vn);
        }
        else
        {
            rel -= (1f + bounciness) * vn * normal;
            rel *= 1f - scrapeFriction;
            velocity = rel;
        }
        AddImpact(-vn);
    }

    void AddImpact(float impact)
    {
        if (!IsOccupied) return;
        if (impact > 2f) shake = Mathf.Min(1f, shake + impact / 25f);
        if (impact > 15f) Flash("CRASH");
    }

    void UpdateLayer(Keyboard kb, Mouse mouse)
    {
        if (parked && !IsOccupied) return;
        float dt = Time.deltaTime;

        Vector2 input = MoveInput(kb);
        UpdateAim(mouse);
        if (input.y != 0f) yaw = Mathf.MoveTowardsAngle(yaw, aimYaw, headingTurnRate * dt);

        Quaternion heading = Quaternion.Euler(0f, yaw, 0f);
        Vector3 wish = heading * new Vector3(input.x * strafeFactor, 0f, input.y) * layerMaxSpeed;

        Vector3 hv = new Vector3(velocity.x, 0f, velocity.z);
        float rate = wish.sqrMagnitude > hv.sqrMagnitude ? acceleration : braking;
        hv = Vector3.MoveTowards(hv, wish, rate * dt);

        Vector3 pos = transform.position + hv * dt;
        pos.y = Mathf.SmoothDamp(pos.y, TrafficAuthority.LayerAltitude(currentLayer), ref altVel, altitudeSmoothTime);
        velocity = new Vector3(hv.x, altVel, hv.z);

        float bank = Mathf.Clamp(-Vector3.Dot(hv, heading * Vector3.right) * 0.6f, -20f, 20f);
        float p = Mathf.Clamp(-altVel * 1.5f, -20f, 20f);
        Quaternion rot = heading * Quaternion.Euler(p, 0f, bank);
        pos = MoveAndCollide(transform.position, pos - transform.position, rot);
        transform.SetPositionAndRotation(pos, rot);
    }

    void UpdateFree(Keyboard kb, Mouse mouse)
    {
        float dt = Time.deltaTime;
        Vector2 input = MoveInput(kb);
        UpdateAim(mouse);
        if (input.y != 0f)
        {
            yaw = Mathf.MoveTowardsAngle(yaw, aimYaw, headingTurnRate * dt);
            pitch = Mathf.MoveTowards(pitch, aimPitch, pitchTurnRate * dt);
        }
        Quaternion rot = Quaternion.Euler(pitch, yaw, 0f);

        Vector3 wish = rot * new Vector3(input.x * strafeFactor, 0f, input.y) * freeMaxSpeed;
        float rate = wish.sqrMagnitude > velocity.sqrMagnitude ? acceleration : braking;
        velocity = Vector3.MoveTowards(velocity, wish, rate * dt);

        Vector3 pos = MoveAndCollide(transform.position, velocity * dt, rot);
        if (pos.y < minAltitude) { pos.y = minAltitude; velocity.y = Mathf.Max(0f, velocity.y); }
        transform.SetPositionAndRotation(pos, rot);
    }

    // ---------- camera ----------

    void LateUpdate()
    {
        if (!IsOccupied || cam == null) return;
        var mouse = Mouse.current;
        if (mouse != null)
        {
            float scroll = mouse.scroll.ReadValue().y;
            if (Mathf.Abs(scroll) > 0.01f) zoom = Mathf.Clamp01(zoom - Mathf.Sign(scroll) * zoomStep);
        }

        // Camera follows the aim, never the car's raw rotation, so it stays smooth in every mode.
        Transform anchor = cockpitAnchor != null ? cockpitAnchor : transform;
        Quaternion look = Quaternion.Euler(aimPitch, aimYaw, 0f);
        Vector3 pos = anchor.position - look * Vector3.forward * (zoom * maxCameraDistance) + Vector3.up * (zoom * 2f);
        if (shake > 0f)
        {
            pos += Random.insideUnitSphere * shake * impactShake;
            shake = Mathf.MoveTowards(shake, 0f, 2f * Time.deltaTime);
        }
        cam.transform.SetPositionAndRotation(pos, look);
    }

    // ---------- enter / exit / police ----------

    public void Enter(FirstPersonController who)
    {
        if (IsOccupied) return;
        driver = who;
        parked = false;
        enterFrame = Time.frameCount;
        eArmed = false;
        cam = who.playerCamera;
        cam.transform.SetParent(null);
        who.gameObject.SetActive(false);
        zoom = 0f;
        yaw = transform.eulerAngles.y;
        aimYaw = yaw; aimPitch = 0f;
        Driven = this;
        VehicleHUD.Ensure();
        TrafficAuthority.Instance?.SetPlayerVehicle(this);
    }

    // toRoof: stand on the roof centre facing the car's yaw, already locked on. The car keeps
    // its mode, so the AI takes over (in its lane it drives on; off it, it flies back).
    public void Exit(bool toRoof = false)
    {
        var who = driver;
        driver = null;
        eArmed = false;
        if (Driven == this) Driven = null;

        // From here the car is a traffic agent again: on its lane it just carries on,
        // off it, it finds its way back.
        Mode = Mode == FlightMode.Free && path != null ? FlightMode.Layer : Mode;

        if (toRoof)
            who.transform.SetPositionAndRotation(new Vector3(transform.position.x, RoofY + 0.02f, transform.position.z),
                                                 PlatformRotation);
        else
            who.transform.SetPositionAndRotation(transform.position + transform.right * 3f,
                                                 Quaternion.Euler(0f, transform.eulerAngles.y, 0f));
        who.gameObject.SetActive(true);
        who.AttachCamera(cam);
        who.BlockInteractThisFrame();
        if (toRoof) who.MountPlatform(this);
        cam = null;
        TrafficAuthority.Instance?.SetPlayerVehicle(null);
    }

    public void ForceStop()
    {
        velocity = Vector3.zero; speed = 0f;
        if (Mode == FlightMode.Free) LockToNearestLayer();
    }

    void Flash(string msg) { flash = msg; flashUntil = Time.time + 2f; }
}
