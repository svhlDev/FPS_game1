using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.InputSystem;

// Altitudes are a grid of 5 m layers (see TrafficAuthority). Three flight modes:
//   Lane  : attached to a MAGNETIC flyway lane. You fly freely; the lane pulls you toward it and
//           turns you along it. Pull is strongest at the line and fades with distance.
//           Steer far enough away and you break free.
//   Layer : free horizontal flight holding a grid layer's ride height. Over a rooftop or the
//           ground that's simply that layer; unoccupied over a surface, the car settles and parks.
//   Free  : full 3D.
// Police don't care about any of this; they care about crashes (see PoliceDispatch).
// Disabled (EMP or shot down): no propulsion, lights dead, the backup hover holds altitude (or the
// police tow it down to the street). R starts the restart sequence; E still exits.
// Mouse always aims the camera. The car turns toward the aim only while W/S is held (Halo style).
// Space / Shift+Space : one level up / down (off-lane: one layer; on a flyway: the next lane that exists here)
// Ctrl (tap)          : Lane <-> Layer (magnet off / magnet on to a lane at this layer)
// Ctrl + Space        : Free <-> Layer (locks to the nearest layer)
// E tap / hold        : exit through the door / onto the roof (longer hold while disabled)
// R (disabled)        : restart sequence
public enum FlightMode { Lane, Layer, Free }

// Moves before the player so a rider standing on the roof is carried with this frame's motion.
[DefaultExecutionOrder(-10)]
public class FlyingVehicle : MonoBehaviour
{
    // Who's at the wheel when the player isn't. Data only: no GameObject until thrown out (NpcBody).
    public enum DriverKind { Civilian, Officer }

    [Header("Driver")]
    [Tooltip("Traffic and police have a driver; parked cars are empty.")]
    public bool hasDriver = true;
    public DriverKind driverKind = DriverKind.Civilian;

    [Header("Lane")]
    [Tooltip("Leave empty for a car that isn't traffic (parked, or hovering where placed).")]
    public LanePath path;
    [Tooltip("No path: start parked on the surface below (e.g. a deck). Otherwise it hovers in Free mode.")]
    public bool startParkedOnSurface;
    public float startDistance = 0f;
    [Tooltip("Lane level (layer offset from the flyway's base) traffic starts in and returns to.")]
    public int startLevel;
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

    [Header("Parking")]
    [Tooltip("Unoccupied in Layer mode with a valid surface this close below the underside: sink and park.")]
    public float parkRange = 1f;
    [Tooltip("Seconds for an unoccupied car to settle onto the surface.")]
    public float sinkTime = 0.6f;
    [Tooltip("Seconds to lift back to ride height when someone gets in.")]
    public float liftTime = 0.4f;
    [Tooltip("Height smoothing while sinking / lifting.")]
    public float parkSmoothTime = 0.15f;

    [Header("Collisions")]
    [Tooltip("Car-vs-car pushing splits by inverse mass. Civilian / player 1, police 1.6; a tow group " +
             "(towed car + attached dockers) counts as one body with the summed mass.")]
    public float mass = 1f;
    [Tooltip("0 = dead stop on impact, 1 = full rebound.")]
    public float bounciness = 0.25f;
    [Tooltip("Below this relative speed (m/s) car-vs-car contact doesn't bounce, so a steady push carries the other car along.")]
    public float pushRestitutionSpeed = 3f;
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
    [Tooltip("Unoccupied cars farther than this from the camera skip collision and just follow their lane.")]
    public float physicsLodRadius = 300f;
    [Tooltip("Looks this far ahead for bends and slows so the magnet can hold the turn.")]
    public float curveLookahead = 25f;
    [Tooltip("Share of magnetStrength the AI will use as sideways acceleration in a bend.")]
    public float curveGrip = 0.6f;
    [Tooltip("Looks this far down its lane for stops (red cones, disabled cars) to route round.")]
    public float avoidLookahead = 80f;
    [Tooltip("Margin round a stop cone that counts as blocked.")]
    public float coneMargin = 4f;
    [Tooltip("A disabled car without a cone blocks this radius.")]
    public float disabledCarClearance = 15f;

    [Header("Camera")]
    public Transform cockpitAnchor;
    public float maxCameraDistance = 14f;
    [Tooltip("Same zoom behaviour as the on-foot camera.")]
    public CameraZoom zoom = new CameraZoom();

    [Header("Exit")]
    [Tooltip("Hold E this long to exit onto the roof instead of through the door.")]
    public float roofExitHoldTime = 0.35f;
    [Tooltip("Roof exit hold while the car is disabled (climbing out of a dead car takes longer).")]
    public float disabledRoofExitHoldTime = 1.5f;

    [Header("Disabled / damage")]
    [Tooltip("Seconds for a disabled car's velocity to die out.")]
    public float disableStopTime = 1f;
    [Tooltip("Descent speed while police tow a disabled car down to the street.")]
    public float towSpeed = 8f;
    [Tooltip("Descent speed of an abandoned disabled car (its backup hover gives out slowly).")]
    public float abandonedSinkSpeed = 3f;
    [Tooltip("Forward boost after a successful restart (m/s).")]
    public float restartBoost = 10f;
    public float maxIntegrity = 100f;
    [Tooltip("Integrity regained per second once regenDelay has passed without a hit.")]
    public float integrityRegen = 5f;
    public float regenDelay = 10f;

    public bool IsOccupied => driver != null;
    public FirstPersonController Driver => driver;

    // ---- police / damage state ----
    public bool Disabled { get; private set; }
    // Disabled by gunfire: the restart sequence is twice as long.
    public bool ShotDown { get; private set; }
    // Set by PoliceDispatch while units hold it at its sides: it descends at towSpeed.
    public bool Towed { get; set; }
    // A disabled car that has come to rest on a surface.
    public bool OnSurface { get; private set; }
    // Towed down and parked by the police: locked until the pursuit ends.
    public bool Impounded { get; set; }
    public float Integrity { get; private set; }
    public float LastHitTime { get; private set; } = -100f;
    // Report resting contact with other cars every frame (pursuing police).
    public bool ReportContacts { get; set; }
    public bool HasAutopilot => autopilot;
    // 0..1 progress of a held E (for the HUD while disabled).
    public float RoofHoldProgress => eArmed && IsOccupied && Keyboard.current != null && Keyboard.current.eKey.isPressed
        ? Mathf.Clamp01((Time.time - eDownTime) / RoofHoldTime) : 0f;
    float RoofHoldTime => Disabled ? disabledRoofExitHoldTime : roofExitHoldTime;

    // Resting on a surface until someone gets in (CarLights keeps its lights off).
    public bool IsParked => parked;

    // Lights follow the parked state (off while parked, on otherwise).
    void SetParked(bool value)
    {
        parked = value;
        if (lights != null) lights.SetOn(!value);
    }
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
    // Which lane of the flyway (Lane mode): layer offset from its base layer.
    public int LaneLevel => laneLevel;
    // Grid layer the car is in: its lane's layer on a flyway, otherwise the held layer.
    public int GridLayer => mode == FlightMode.Lane && path != null ? path.GridLayerOf(laneLevel) : gridLayer;
    float Underside => transform.position.y + colCenter.y - colHalf.y;
    float RootAboveUnderside => colHalf.y - colCenter.y;
    // Slot in TrafficSystem.States for the current frame.
    public int TrafficIndex { get; set; } = -1;

    // HUD readouts (drawn by VehicleHUD).
    public float HudSpeed => Mode == FlightMode.Lane ? speed : velocity.magnitude;
    public float MagnetHold => magnetHold;
    public bool CanStepLane(int dir) => mode == FlightMode.Lane && path != null && path.NextLevel(laneLevel, dir, distance, out _);
    public bool InNoSwitchZone => path != null && path.IsNoSwitch(distance);
    public string FlashMessage => Time.time < flashUntil ? flash : null;

    // A traffic agent: nobody driving and it knows which lane it belongs to.
    bool IsAI => !IsOccupied && path != null;
    static readonly List<FlyingVehicle> All = new List<FlyingVehicle>();
    // Changing mode keeps the lane's car list in sync (registered only while in Lane mode).
    // Entering Layer mode picks up the nearest grid layer.
    public FlightMode Mode
    {
        get => mode;
        private set
        {
            if (value == FlightMode.Layer && mode != FlightMode.Layer)
            {
                gridLayer = TrafficAuthority.NearestLayer(Underside);
                if (!parked) hoverBlend = 1f; // flying, not lifting off a surface (else it dives toward y = 0)
            }
            mode = value;
            SyncLaneRegistration();
        }
    }
    FlightMode mode = FlightMode.Lane;
    LanePath registeredPath;
    bool started;

    int laneLevel;
    int gridLayer;
    float distance, speed;
    Vector2 currentOffset, offsetVel;   // offset of the magnetic line from the base path
    Vector3 velocity;
    float yaw, pitch, altVel;
    float aimYaw, aimPitch;
    float magnetHold;                   // 0..1, for the HUD
    bool parked, ctrlComboUsed;
    CarLights lights;
    float hoverBlend;                   // 0 = resting on the surface, 1 = full hover height
    float surfaceY;                     // last known surface height under the car
    FirstPersonController driver;
    Camera cam;
    int enterFrame = -1;
    bool eArmed; float eDownTime;
    LanePath[] allLanes;
    string flash; float flashUntil;
    BoxCollider bodyCol;
    Vector3 colHalf, colCenter;
    float shake;
    bool autopilot; float apYaw, apThrottle, apTargetY, apVertical; DriveModel apModel;

    // Tow group: dockers attach rigidly to the towed car (fixed offset in its yaw frame).
    public FlyingVehicle AttachHost { get; private set; }
    public bool IsAttached => AttachHost != null;
    readonly List<FlyingVehicle> attached = new List<FlyingVehicle>();
    Vector3 attachLocal; float attachBlend; int followFrame = -1;
    // A car this one doesn't collide with (a docker on its way to the car it will tow).
    public FlyingVehicle IgnoreCar { get; set; }
    public bool InTowGroup => AttachHost != null || attached.Count > 0 || Towed;
    // Pairs involving the player, police or a tow group get full mass-based resolution.
    bool FullResolution => Driven == this || isPolice || InTowGroup || Disabled;
    bool isPolice;
    bool ghostCars;                 // tow watchdog: no collision with other cars until it lands
    float watchY, watchUntil;
    public float DisabledTime { get; private set; }
    float lastCrashFlash = -10f; int lastContactFrame = -10;

    // Lane avoidance of stops.
    static readonly List<FlyingVehicle> disabledCars = new List<FlyingVehicle>();
    float nextAvoid, avoidStopAt = -1f;
    bool returnClear = true;
    float disableDecel;
    bool hijacked;
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
        disabledCars.Remove(this);
        if (AttachHost != null) Detach();
        for (int i = attached.Count - 1; i >= 0; i--) if (attached[i] != null) attached[i].Detach();
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
        Integrity = maxIntegrity;
        isPolice = GetComponent<PoliceDriver>() != null;
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
        if (path == null)
        {
            if (startParkedOnSurface && ProbeSurface(transform.position, 0.05f, parkRange, out surfaceY))
            {
                Mode = FlightMode.Layer;
                gridLayer = TrafficAuthority.NearestLayer(surfaceY + TrafficAuthority.Hover);
                SetParked(true);
                hoverBlend = 0f;
            }
            else Mode = FlightMode.Free;
        }
        else
        {
            distance = startDistance; laneLevel = startLevel; speed = aiCruiseSpeed;
            float w0 = path.LaneWeight(startLevel, startDistance, out var o0);
            currentOffset = startLevel == 0 ? Vector2.zero : o0 * w0;
            velocity = path.SmoothForward(startDistance) * speed;
        }
        SyncLaneRegistration();
        lights = GetComponent<CarLights>();
        if (lights != null) lights.SetOn(!parked);
        TrafficSystem.AddRenderable(this); // drawn instanced from here on
    }

    void OnDestroy() => TrafficSystem.RemoveRenderable(this);

    static readonly ProfilerMarker UpdateMarker = new ProfilerMarker("FlyingVehicle.Update");
    static readonly ProfilerMarker AIMarker = new ProfilerMarker("FlyingVehicle.AITargetSpeed");
    static readonly ProfilerMarker CollideMarker = new ProfilerMarker("FlyingVehicle.MoveAndCollide");

    void Update()
    {
        using var _ = UpdateMarker.Auto();
        var kb = Keyboard.current;
        var mouse = Mouse.current;
        if (IsOccupied && kb != null) HandleModeInput(kb);
        if (Integrity < maxIntegrity && Time.time - LastHitTime > regenDelay)
            Integrity = Mathf.Min(maxIntegrity, Integrity + integrityRegen * Time.deltaTime);

        if (AttachHost != null) { FollowHost(); return; }
        if (Disabled) { UpdateDisabled(mouse); return; }
        if (autopilot && !IsOccupied) { UpdateAutopilot(); return; }

        switch (Mode)
        {
            case FlightMode.Lane: UpdateLaneMagnetic(kb, mouse); break;
            case FlightMode.Layer:
                // Traffic knocked off its lane flies back. Only a driverless car settles onto a surface
                // and parks (street traffic rides half a metre over the road, so it would park at once).
                if (IsAI && !parked && (hasDriver || !SurfaceBelow())) UpdateRecover(); else UpdateLayer(kb, mouse);
                break;
            case FlightMode.Free:
                if (IsAI) UpdateRecover(); else UpdateFree(kb, mouse);
                break;
        }
    }

    // ---------- input / mode switching ----------

    void HandleModeInput(Keyboard kb)
    {
        // Tap E = door, hold E = roof. Only presses made after entering count.
        // A disabled car takes longer to climb out of; starting to bail cancels the restart sequence.
        if (kb.eKey.wasPressedThisFrame && Time.frameCount != enterFrame)
        {
            eArmed = true; eDownTime = Time.time;
            if (RestartQTE.Active) RestartQTE.Cancel();
        }
        if (eArmed)
        {
            if (kb.eKey.wasReleasedThisFrame) { Exit(false); return; }
            if (kb.eKey.isPressed && Time.time - eDownTime >= RoofHoldTime) { Exit(true); return; }
        }

        if (Disabled)
        {
            // R: restart sequence (not while holding E for the roof).
            if (kb.rKey.wasPressedThisFrame && !eArmed && !RestartQTE.Active)
                RestartQTE.Begin("RESTART  (Bypass " + PlayerSkills.BypassLevel + ")",
                                 PlayerSkills.RestartLength(ShotDown), PlayerSkills.PromptWindow, true, Restart);
            return; // no propulsion: Space / Ctrl do nothing
        }

        bool ctrl = kb.leftCtrlKey.isPressed || kb.rightCtrlKey.isPressed;
        if (kb.spaceKey.wasPressedThisFrame)
        {
            bool shift = kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed;
            if (ctrl) { ctrlComboUsed = true; ToggleFree(); }
            else StepLevel(shift ? -1 : 1);
        }
        if (kb.leftCtrlKey.wasReleasedThisFrame || kb.rightCtrlKey.wasReleasedThisFrame)
        {
            if (!ctrlComboUsed) ToggleLaneLock();
            ctrlComboUsed = false;
        }
    }

    // Space / Shift+Space. Off-lane: exactly one layer. On a flyway: the next lane level that exists
    // at this point (nothing happens if there isn't one). Free flight: no levels.
    void StepLevel(int dir)
    {
        if (Mode == FlightMode.Lane) StepLane(dir);
        else if (Mode == FlightMode.Layer)
        {
            int next = Mathf.Clamp(gridLayer + dir, 0, TrafficAuthority.MaxLayer);
            if (next == gridLayer) return;
            gridLayer = next;
            Flash($"Layer {gridLayer}");
        }
    }

    void StepLane(int dir)
    {
        if (!path.NextLevel(laneLevel, dir, distance, out int next))
        {
            // Single-level lane (street traffic): magnet off and step a layer like off-lane flight.
            if (path.Levels.Count == 1)
            {
                Mode = FlightMode.Layer;
                StepLevel(dir);
            }
            return;
        }
        laneLevel = next;
    }

    void ToggleFree()
    {
        if (Mode == FlightMode.Free) { LockToNearestLayer(); return; }
        Mode = FlightMode.Free;
        pitch = 0f;
        Flash("Free flight");
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
                if (!TryAttachToLane()) Flash("No lane at this layer in range");
                break;
            case FlightMode.Free:
                Flash("Lock to a layer first (Ctrl+Space)");
                break;
        }
    }

    bool IsAvailable(int level) => path != null && path.LaneWeight(level, distance, out _) > 0.9f;

    void LockToNearestLayer()
    {
        Mode = FlightMode.Layer; // picks the nearest layer
        gridLayer = TrafficAuthority.NearestLayer(Underside);
        pitch = 0f; altVel = 0f;
        Flash($"Locked to layer {gridLayer}");
    }

    // ---------- parking ----------

    bool SurfaceBelow() => !IsOccupied && ProbeSurface(transform.position, 0.05f, parkRange, out surfaceY);

    // Four downward rays from the body's (slightly inset) corners, starting `above` over the
    // underside and reaching `below` under it. Valid hits: static world, normal.y > 0.9.
    // Returns the average height of the corners that hit.
    bool ProbeSurface(Vector3 rootPos, float above, float below, out float y)
    {
        Quaternion rot = PlatformRotation;
        float underside = rootPos.y + colCenter.y - colHalf.y;
        float hx = Mathf.Max(0f, colHalf.x - 0.1f), hz = Mathf.Max(0f, colHalf.z - 0.1f);
        int hits = 0; float sum = 0f;
        for (int i = 0; i < 4; i++)
        {
            float sx = (i & 1) == 0 ? -1f : 1f, sz = (i & 2) == 0 ? -1f : 1f;
            Vector3 origin = rootPos + rot * new Vector3(colCenter.x + sx * hx, 0f, colCenter.z + sz * hz);
            origin.y = underside + above;
            if (Physics.Raycast(origin, Vector3.down, out var h, above + below, collisionMask, QueryTriggerInteraction.Ignore)
                && IsValidSurface(h))
            {
                hits++;
                sum += h.point.y;
            }
        }
        y = hits > 0 ? sum / hits : 0f;
        return hits > 0;
    }

    static bool IsValidSurface(RaycastHit h) =>
        h.normal.y > 0.9f && h.collider.attachedRigidbody == null && h.collider.GetComponentInParent<FlyingVehicle>() == null;

    bool TryAttachToLane()
    {
        LanePath best = null; int bestLevel = 0;
        float bestD = 0f, bestSq = captureRadius * captureRadius;
        foreach (var lp in allLanes)
        {
            if (lp == null) continue;
            foreach (int level in lp.Levels)
            {
                if (lp.GridLayerOf(level) != gridLayer) continue;
                if (lp.FindNearest(transform.position, level, out float d, out float sq) && sq < bestSq)
                { best = lp; bestLevel = level; bestD = d; bestSq = sq; }
            }
        }
        if (best == null) return false;

        path = best;
        laneLevel = bestLevel;
        distance = best.Project(transform.position, bestD);
        float w = best.LaneWeight(laneLevel, distance, out var laneOff);
        currentOffset = laneLevel == 0 ? Vector2.zero : laneOff * w;
        offsetVel = Vector2.zero;
        Mode = FlightMode.Lane;
        Flash("Magnet on");
        return true;
    }

    // ---------- helpers ----------

    Vector2 MoveInput(Keyboard kb)
    {
        if (IsOccupied && DebugThrottle != 0f) return new Vector2(0f, DebugThrottle);
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
        float w = path.LaneWeight(laneLevel, distance, out Vector2 laneOffset);
        if (laneLevel != 0 && w <= 0.01f) laneLevel = 0; // lane converged
        Vector2 target = laneLevel == 0 ? Vector2.zero : laneOffset * w;
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

        // Traffic routes round stops (shifts level), and drifts back to its own level whenever it
        // legally can and that level is clear.
        if (IsAI) UpdateAvoidance();
        if (IsAI && laneLevel != startLevel && returnClear && IsAvailable(startLevel) && !path.IsNoSwitch(distance))
            laneLevel = startLevel;

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

        lineDist = d;
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
            float max = laneLevel == 0 ? middleLaneMaxSpeed : sideLaneMaxSpeed;
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
        pos.y = Mathf.SmoothDamp(pos.y, linePoint.y + RootAboveUnderside, ref altVel, laneChangeSmoothTime);
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
        float w = path.LaneWeight(laneLevel, distance, out var laneOff);
        if (laneLevel != 0 && w <= 0.01f) laneLevel = 0;
        currentOffset = laneLevel == 0 ? Vector2.zero : laneOff * w;
        offsetVel = Vector2.zero;

        path.Sample(distance, out var nearBase, out _);
        Vector3 nearFwd = Flat(path.SmoothForward(distance));
        Vector3 up = Vector3.up * RootAboveUnderside; // lane heights are underside heights
        Vector3 nearest = path.ToWorld(nearBase, nearFwd, currentOffset) + up;
        path.Sample(distance + recoverLead, out var aimBase, out _);
        Vector3 aimPoint = path.ToWorld(aimBase, path.SmoothForward(distance + recoverLead), currentOffset) + up;

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
        using var _ = AIMarker.Auto();
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

        // Slow for bends the magnet couldn't hold at speed (tight corners, street U-turns).
        if (path != null)
        {
            // Two windows: the full lookahead and half of it (catches short, sharp corners).
            Vector3 f0 = path.SmoothForward(distance);
            for (int k = 1; k <= 2; k++)
            {
                float look = curveLookahead * k * 0.5f;
                float turn = Vector3.Angle(f0, path.SmoothForward(distance + look)) * Mathf.Deg2Rad;
                if (turn > 0.15f) target = Mathf.Min(target, Mathf.Sqrt(magnetStrength * curveGrip * look / turn));
            }
        }

        // Street level: stop at red lights (and amber, when there's room to stop).
        if (mine.layer == 0 && TrafficSignal.All.Count > 0)
            target = Mathf.Min(target, TrafficSignal.StreetLimit(me, fwd, Vector3.Dot(velocity, fwd), braking, carLength * 0.5f, followGain));

        // A stop ahead that can't be passed on another level: stop short of it and wait.
        if (avoidStopAt >= 0f)
            target = Mathf.Min(target, Mathf.Max(0f, followGain * (avoidStopAt - carLength * 0.5f - followGap * 0.5f)));

        // The player on foot in our lane ahead: brake behind them like behind a car (cars never push
        // the player, they just stop).
        var walker = FirstPersonController.Instance;
        if (walker != null && walker.isActiveAndEnabled)
        {
            Vector3 r = walker.transform.position - me;
            float ahead = Vector3.Dot(r, fwd);
            if (ahead > 0f && ahead < yieldLookRange && Mathf.Abs(r.y) < 3f)
            {
                Vector3 side = r - fwd * ahead;
                side.y = 0f;
                if (side.sqrMagnitude < 2.5f * 2.5f)
                {
                    float gap = ahead - carLength * 0.5f - 0.5f;
                    float follow = gap < minGap ? 0f : followGain * (gap - followGap);
                    target = Mathf.Min(target, Mathf.Max(0f, follow));
                }
            }
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

    // Sweep the car's box along its move, stop at the first static hit (or civilian car, for
    // civilian-vs-civilian), then push out of anything still overlapping. Pairs involving the player,
    // police or a tow group don't stop at the sweep: the full step is taken and the overlap is split by
    // inverse mass (both cars move), with a mass-weighted impulse. Static geometry is infinitely heavy.
    Vector3 MoveAndCollide(Vector3 from, Vector3 delta, Quaternion rot)
    {
        using var _ = CollideMarker.Auto();
        if (bodyCol == null) return from + delta;
        // Physics LOD: far-away traffic just follows its lane, no sweeps or depenetration.
        if (!IsOccupied && !InTowGroup && TrafficSystem.IsBeyond(from, physicsLodRadius)) return from + delta;
        Vector3 half = colHalf, center = colCenter;
        float dist = delta.magnitude;
        Vector3 moved = delta;
        const float skin = 0.05f;
        bool full = FullResolution;

        if (dist > 1e-5f)
        {
            Vector3 dir = delta / dist;
            int n = Physics.BoxCastNonAlloc(from + rot * center, half, dir, hitBuf, rot, dist + skin,
                                            collisionMask, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue; int bestIdx = -1;
            for (int i = 0; i < n; i++)
            {
                var c = hitBuf[i].collider;
                if (c == bodyCol || hitBuf[i].distance <= 0f) continue;
                var car = c.GetComponentInParent<FlyingVehicle>();
                if (car != null && (Ignores(car) || full || car.FullResolution)) continue; // resolved by overlap below
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
        int count = Physics.OverlapBoxNonAlloc(pos + rot * center, half, overlapBuf, rot,
                                               collisionMask, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < count; i++)
        {
            var c = overlapBuf[i];
            if (c == bodyCol) continue;
            var car = c.GetComponentInParent<FlyingVehicle>();
            if (car == this || (car != null && Ignores(car))) continue;
            if (!Physics.ComputePenetration(bodyCol, bodyPos, bodyRot, c, c.transform.position, c.transform.rotation,
                                            out Vector3 sepDir, out float sepDist)) continue;
            if (car != null && (full || car.FullResolution))
            {
                // Split the overlap by inverse mass: each moves out by its share.
                float invA = 1f / EffectiveMass, invB = 1f / car.EffectiveMass;
                float shareA = invA / (invA + invB);
                pos += sepDir * sepDist * shareA;
                bodyPos += sepDir * sepDist * shareA;
                car.PushBy(-sepDir * sepDist * (1f - shareA));
                Collide(car, sepDir, invA, invB);
            }
            else
            {
                pos += sepDir * sepDist;
                bodyPos += sepDir * sepDist;
                Bounce(sepDir, c);
            }
        }
        return pos;
    }

    // Tow group members don't collide with each other; a docker ignores its future tow; the watchdog
    // turns all car collisions off for a stuck tow.
    bool Ignores(FlyingVehicle other)
    {
        if (ghostCars || other.ghostCars) return true;
        if (other == IgnoreCar || other.IgnoreCar == this) return true;
        var a = AttachHost != null ? AttachHost : this;
        var b = other.AttachHost != null ? other.AttachHost : other;
        return a == b;
    }

    // Mass of the body this car belongs to (a tow group counts as one).
    public float EffectiveMass
    {
        get
        {
            var root = AttachHost != null ? AttachHost : this;
            float m = root.mass;
            foreach (var a in root.attached) if (a != null) m += a.mass;
            return Mathf.Max(0.01f, m);
        }
    }

    // Shoved by another car's depenetration. Moves the whole tow group.
    void PushBy(Vector3 d)
    {
        var root = AttachHost != null ? AttachHost : this;
        if (root.parked) root.SetParked(false); // a parked car gets knocked loose and settles again
        root.transform.position += d;
        root.SyncAttached();
    }

    // Normal impulse split by mass (no bounce below pushRestitutionSpeed, so a steady push carries the
    // other car), plus scrape friction. `n` points from the other car to this one.
    void Collide(FlyingVehicle other, Vector3 n, float invA, float invB)
    {
        var ob = other.AttachHost != null ? other.AttachHost : other;
        var me = AttachHost != null ? AttachHost : this;
        Vector3 rel = me.velocity - ob.velocity;
        float vn = Vector3.Dot(rel, n);
        OnCarContact(other, Mathf.Max(0f, -vn));
        if (vn >= 0f) return;
        float e = -vn < pushRestitutionSpeed ? 0f : bounciness;
        float j = -(1f + e) * vn / (invA + invB);
        Vector3 tangential = rel - n * vn;
        float fA = invA / (invA + invB), fB = 1f - fA;
        me.velocity += n * (j * invA) - tangential * scrapeFriction * fA;
        ob.velocity -= n * (j * invB) - tangential * scrapeFriction * fB;
        me.AddImpact(j * invA);
        ob.AddImpact(j * invB);
        if (-vn > 15f) PedestrianSystem.ReportDanger(transform.position); // a crash: people near it flee
    }

    // Static geometry, or a cheap civilian-vs-civilian contact.
    void Bounce(Vector3 normal, Collider other)
    {
        var otherCar = other.GetComponentInParent<FlyingVehicle>();
        Vector3 otherVel = otherCar != null ? otherCar.velocity : Vector3.zero;
        Vector3 rel = velocity - otherVel;
        float vn = Vector3.Dot(rel, normal);
        if (otherCar != null) OnCarContact(otherCar, Mathf.Max(0f, -vn));
        else if (vn < 0f && Driven == this) PoliceDispatch.Instance?.OnPlayerImpact(this, -vn);
        if (vn >= 0f) return;
        if (-vn > 15f) PedestrianSystem.ReportDanger(transform.position);

        if (otherCar != null)
        {
            // Equal masses: split the impulse so the other car gets shoved too.
            float j = -(1f + bounciness) * vn * 0.5f;
            Vector3 tangential = rel - normal * vn;
            velocity += normal * j - tangential * scrapeFriction * 0.5f;
            otherCar.velocity -= normal * j - tangential * scrapeFriction * 0.5f;
            otherCar.AddImpact(j);
        }
        else
        {
            rel -= (1f + bounciness) * vn * normal;
            rel *= 1f - scrapeFriction;
            velocity = rel;
        }
        AddImpact(-vn);
    }

    // Called from the collision code every frame this car touches another one (resting contact
    // included, impactSpeed 0). Police care when the player's car is involved.
    public void OnCarContact(FlyingVehicle other, float impactSpeed)
    {
        if (Driven == this || Driven == other) PoliceDispatch.Instance?.OnCarContact(this, other, impactSpeed);
    }

    // Resting contact: the sweep only sees cars we move into, so check a slightly grown box.
    void ReportTouching(Vector3 pos, Quaternion rot)
    {
        if (bodyCol == null) return;
        int count = Physics.OverlapBoxNonAlloc(pos + rot * colCenter, colHalf + Vector3.one * 0.3f, overlapBuf, rot,
                                               collisionMask, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < count; i++)
        {
            if (overlapBuf[i] == bodyCol) continue;
            var other = overlapBuf[i].GetComponentInParent<FlyingVehicle>();
            if (other != null && other != this) OnCarContact(other, 0f);
        }
    }

    // Camera shake scales with the impulse (velocity change). Sustained pushing is a low rumble; CRASH
    // only flashes for a fresh hard hit, not every frame of a shove.
    void AddImpact(float impulse)
    {
        if (!IsOccupied) return;
        bool fresh = Time.frameCount - lastContactFrame > 10;
        lastContactFrame = Time.frameCount;
        shake = Mathf.Max(shake, Mathf.Clamp(impulse / 20f, 0.06f, 1f));
        if (impulse > 15f && fresh && Time.time - lastCrashFlash > 1.5f)
        {
            lastCrashFlash = Time.time;
            Flash("CRASH");
        }
    }

    // ---------- tow group (attach) ----------

    // Rigidly attach to `host` at a fixed offset in its yaw frame (blends in over 0.4 s). Autopilot and
    // collisions with the host are off until Detach.
    public void AttachTo(FlyingVehicle host, Vector3 localOffset)
    {
        if (AttachHost == host) return;
        if (AttachHost != null) Detach();
        AttachHost = host;
        attachLocal = localOffset;
        attachBlend = 0f;
        host.attached.Add(this);
    }

    public void Detach()
    {
        if (AttachHost != null) AttachHost.attached.Remove(this);
        AttachHost = null;
    }

    void SyncAttached()
    {
        for (int i = attached.Count - 1; i >= 0; i--)
        {
            if (attached[i] == null) { attached.RemoveAt(i); continue; }
            attached[i].FollowHost();
        }
    }

    // Once per frame: sit at the attach point (blending in from where it docked).
    void FollowHost()
    {
        var host = AttachHost;
        if (host == null || !host.isActiveAndEnabled) { Detach(); return; }
        if (followFrame == Time.frameCount) return;
        followFrame = Time.frameCount;
        Quaternion hr = host.PlatformRotation;
        Vector3 target = host.transform.position + hr * attachLocal;
        attachBlend = Mathf.MoveTowards(attachBlend, 1f, Time.deltaTime / 0.4f);
        float k = attachBlend >= 1f ? 1f : Mathf.Clamp01(attachBlend * 0.5f + 0.1f);
        yaw = Mathf.LerpAngle(yaw, hr.eulerAngles.y, k);
        transform.SetPositionAndRotation(Vector3.Lerp(transform.position, target, k), Quaternion.Euler(0f, yaw, 0f));
        velocity = host.velocity;
    }

    // ---------- routing round stops ----------

    // A stop at this point? Red cones (radius + coneMargin) and disabled cars not yet down.
    bool HitsObstacle(Vector3 p)
    {
        var cones = StopCone.Active;
        for (int i = 0; i < cones.Count; i++)
            if (cones[i].BlocksTraffic(p, coneMargin, 3f)) return true;
        float r2 = disabledCarClearance * disabledCarClearance;
        for (int i = 0; i < disabledCars.Count; i++)
        {
            var d = disabledCars[i];
            if (d == this || d.OnSurface) continue;
            Vector3 q = d.transform.position;
            float dx = p.x - q.x, dz = p.z - q.z;
            if (dx * dx + dz * dz < r2 && Mathf.Abs(p.y - q.y) < 4f) return true;
        }
        return false;
    }

    // Distance along the lane (at `level`) to the first blocked point within avoidLookahead, or -1.
    float BlockedDistance(int level)
    {
        for (float d = 0f; d <= avoidLookahead; d += 8f)
        {
            float dd = distance + d;
            float w = path.LaneWeight(level, dd, out Vector2 off);
            if (level != 0 && w < 0.5f) continue;
            path.Sample(dd, out Vector3 bp, out Vector3 f);
            Vector3 p = path.ToWorld(bp, f, level == 0 ? Vector2.zero : off * w) + Vector3.up * RootAboveUnderside;
            if (HitsObstacle(p)) return d;
        }
        return -1f;
    }

    // A few times a second: if the lane ahead runs into a stop, shift to the nearest clear level; with
    // none clear, stop short of it (AITargetSpeed). Also says whether the home level is clear again.
    void UpdateAvoidance()
    {
        if (StopCone.Active.Count == 0 && disabledCars.Count == 0) { avoidStopAt = -1f; returnClear = true; return; }
        if (Time.time < nextAvoid) return;
        nextAvoid = Time.time + 0.3f + Random.value * 0.1f;
        returnClear = laneLevel == startLevel || BlockedDistance(startLevel) < 0f;
        float blocked = BlockedDistance(laneLevel);
        avoidStopAt = -1f;
        if (blocked < 0f) return;
        for (int step = 1; step <= 3; step++)
            for (int dir = 1; dir >= -1; dir -= 2)
            {
                int lv = laneLevel;
                bool ok = true;
                for (int k = 0; k < step && ok; k++) ok = path.NextLevel(lv, dir, distance, out lv);
                if (!ok || BlockedDistance(lv) >= 0f) continue;
                laneLevel = lv;
                return;
            }
        avoidStopAt = blocked;
    }

    // Holds RideHeight(gridLayer). Rooftops and the ground snap to layer floors, so driving onto or
    // off them needs nothing special. Unoccupied over a surface within parkRange: sink onto it and
    // park; getting in lifts back to ride height.
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

        bool parking = SurfaceBelow();
        hoverBlend = Mathf.MoveTowards(hoverBlend, parking ? 0f : 1f, dt / (parking ? sinkTime : liftTime));
        float ride = TrafficAuthority.RideHeight(gridLayer);
        float targetY = (hoverBlend >= 1f ? ride : Mathf.Lerp(surfaceY, ride, hoverBlend)) + RootAboveUnderside;

        Vector3 pos = transform.position + hv * dt;
        pos.y = Mathf.SmoothDamp(pos.y, targetY, ref altVel, hoverBlend < 1f ? parkSmoothTime : altitudeSmoothTime);
        velocity = new Vector3(hv.x, altVel, hv.z);

        float bank = Mathf.Clamp(-Vector3.Dot(hv, heading * Vector3.right) * 0.6f, -20f, 20f);
        float p = Mathf.Clamp(-altVel * 1.5f, -20f, 20f);
        Quaternion rot = heading * Quaternion.Euler(p, 0f, bank);
        pos = MoveAndCollide(transform.position, pos - transform.position, rot);
        transform.SetPositionAndRotation(pos, rot);

        // Settled: rest exactly on the surface and stop updating until someone gets in.
        if (parking && hoverBlend <= 0f && Mathf.Abs(pos.y - targetY) < 0.02f && hv.sqrMagnitude < 0.01f)
        {
            transform.SetPositionAndRotation(new Vector3(pos.x, targetY, pos.z), heading);
            velocity = Vector3.zero; altVel = 0f;
            SetParked(true);
        }
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

    // ---------- autopilot (police pursuit) ----------

    // Handling of an AI-driven car (police): forward speed and heading are separate, like a real car.
    // Thrust and braking act along the heading only; the heading turns at a limited rate and sideways
    // velocity bleeds off slowly, so it slides wide in turns and overshoots what it aims at.
    [System.Serializable]
    public struct DriveModel
    {
        public float accel;         // m/s^2 along the heading at full throttle
        public float topSpeed;      // m/s
        public float brake;         // m/s^2 at full brake
        public float turnRate;      // deg/s
        public float lateralGrip;   // 1/s decay of sideways velocity
        public float verticalSpeed; // m/s max climb / descent (layer changes)
    }

    // The AI's only controls: a desired heading, throttle (> 0) or brake (< 0), and a target height.
    // It never sets the velocity. Leaves the lane; ClearAutopilot hands the car back to the lane AI.
    public void Drive(float desiredYaw, float throttle, float targetY, float maxVerticalSpeed, in DriveModel model)
    {
        if (!autopilot)
        {
            autopilot = true;
            SetParked(false);
            Mode = FlightMode.Free;
        }
        apYaw = desiredYaw;
        apThrottle = Mathf.Clamp(throttle, -1f, 1f);
        apTargetY = targetY;
        apVertical = maxVerticalSpeed;
        apModel = model;
    }

    public float Yaw => yaw;

    public void ClearAutopilot()
    {
        if (!autopilot) return;
        autopilot = false;
        Mode = FlightMode.Layer; // traffic flies back to its lane from here
    }

    void UpdateAutopilot()
    {
        float dt = Time.deltaTime;
        var m = apModel;
        yaw = Mathf.MoveTowardsAngle(yaw, apYaw, m.turnRate * dt);
        Quaternion heading = Quaternion.Euler(0f, yaw, 0f);
        Vector3 fwd = heading * Vector3.forward;

        // Velocity doesn't turn with the heading: what no longer lies along it becomes slide.
        Vector3 hv = new Vector3(velocity.x, 0f, velocity.z);
        float fwdSpeed = Vector3.Dot(hv, fwd);
        Vector3 side = hv - fwd * fwdSpeed;
        if (apThrottle > 0f) fwdSpeed = Mathf.MoveTowards(fwdSpeed, m.topSpeed, m.accel * apThrottle * dt);
        else if (apThrottle < 0f) fwdSpeed = Mathf.MoveTowards(fwdSpeed, 0f, m.brake * -apThrottle * dt);
        else fwdSpeed = Mathf.MoveTowards(fwdSpeed, 0f, 1f * dt); // coasting
        side *= Mathf.Exp(-m.lateralGrip * dt);

        float vy = Mathf.Clamp((apTargetY - transform.position.y) * 2f, -apVertical, apVertical);
        float vyNew = Mathf.MoveTowards(velocity.y, vy, 12f * dt);
        velocity = fwd * fwdSpeed + side + Vector3.up * vyNew;

        float bank = Mathf.Clamp(-Vector3.Dot(velocity, heading * Vector3.right) * 0.8f, -25f, 25f);
        Quaternion rot = heading * Quaternion.Euler(Mathf.Clamp(-velocity.y * 1.5f, -20f, 20f), 0f, bank);

        Vector3 pos = MoveAndCollide(transform.position, velocity * dt, rot);
        if (pos.y < minAltitude) { pos.y = minAltitude; velocity.y = Mathf.Max(0f, velocity.y); }
        transform.SetPositionAndRotation(pos, rot);
        if (ReportContacts) ReportTouching(pos, rot);
    }

    // ---------- disabled (EMP / shot down) ----------

    public void Disable(bool shotDown)
    {
        if (Disabled) return;
        Disabled = true;
        DisabledTime = Time.time;
        if (!disabledCars.Contains(this)) disabledCars.Add(this);
        watchUntil = Time.time + 2f; watchY = transform.position.y;
        ShotDown = shotDown;
        OnSurface = false;
        Towed = false;
        autopilot = false;
        if (Mode != FlightMode.Layer) Mode = FlightMode.Layer; // magnet off
        disableDecel = Mathf.Max(1f, velocity.magnitude) / Mathf.Max(0.05f, disableStopTime);
        altVel = 0f;
        if (lights != null) lights.SetOn(false);
        if (IsOccupied) Flash(shotDown ? "SHOT DOWN  -  R restart | E door | hold E roof" : "EMP  -  R restart | E door | hold E roof");
    }

    // Restart sequence succeeded: propulsion back, a kick forward.
    void Restart()
    {
        if (!Disabled) return;
        Disabled = false;
        disabledCars.Remove(this);
        ghostCars = false;
        ShotDown = false;
        Towed = false;
        OnSurface = false;
        SetParked(false);
        hoverBlend = 1f;
        Mode = FlightMode.Layer;
        gridLayer = Mathf.Max(gridLayer, TrafficAuthority.NearestLayer(Underside));
        if (Integrity <= 0f) Integrity = maxIntegrity * 0.25f;
        velocity += PlatformRotation * Vector3.forward * restartBoost;
        if (lights != null) lights.SetOn(true);
        Flash("RESTARTED");
        PoliceDispatch.Instance?.OnRestarted(this);
    }

    // Gunfire. Shake for the driver; at 0 integrity the car goes down like an EMP.
    public void Damage(float amount)
    {
        if (Disabled) return;
        Integrity = Mathf.Max(0f, Integrity - amount);
        LastHitTime = Time.time;
        if (IsOccupied) shake = Mathf.Min(1f, shake + 0.35f);
        if (Integrity <= 0f) Disable(true);
    }

    // No propulsion: the velocity dies out over disableStopTime. The backup hover holds altitude,
    // unless the police are towing it down (or nobody is in it: then it sinks slowly). It comes to
    // rest on the first surface below.
    void UpdateDisabled(Mouse mouse)
    {
        float dt = Time.deltaTime;
        UpdateAim(mouse);
        Vector3 hv = new Vector3(velocity.x, 0f, velocity.z);
        hv = Vector3.MoveTowards(hv, Vector3.zero, disableDecel * dt);

        float vy = 0f;
        if (!OnSurface)
        {
            float sink = Towed ? towSpeed : IsOccupied ? 0f : abandonedSinkSpeed;
            vy = -sink;
            // Touch down once the underside is within hover height of a surface.
            if (sink > 0f && ProbeSurface(transform.position, 0.05f, TrafficAuthority.Hover + sink * dt + 0.05f, out surfaceY))
            {
                OnSurface = true;
                vy = 0f;
            }
        }
        if (OnSurface)
        {
            // Settle onto the surface (sinks the last bit of hover).
            float restY = surfaceY + RootAboveUnderside;
            float y = Mathf.MoveTowards(transform.position.y, restY, TrafficAuthority.Hover / Mathf.Max(0.05f, sinkTime) * dt);
            vy = (y - transform.position.y) / Mathf.Max(dt, 1e-5f);
        }
        velocity = new Vector3(hv.x, vy, hv.z);

        // Watchdog: a tow that has descended less than 1 m in 2 s stops colliding with cars until it lands.
        if (Towed && !OnSurface)
        {
            if (Time.time >= watchUntil)
            {
                if (watchY - transform.position.y < 1f) ghostCars = true;
                watchY = transform.position.y;
                watchUntil = Time.time + 2f;
            }
        }
        else
        {
            watchY = transform.position.y;
            watchUntil = Time.time + 2f;
            if (OnSurface) ghostCars = false;
        }

        Quaternion rot = PlatformRotation;
        Vector3 pos = MoveAndCollide(transform.position, velocity * dt, rot);
        transform.SetPositionAndRotation(pos, rot);
        SyncAttached(); // dockers ride along
    }

    // ---------- camera ----------

    void LateUpdate()
    {
        if (!IsOccupied || cam == null) return;
        var mouse = Mouse.current;
        if (mouse != null)
        {
            zoom.HandleScroll(mouse);
        }

        // Camera follows the aim, never the car's raw rotation, so it stays smooth in every mode.
        Transform anchor = cockpitAnchor != null ? cockpitAnchor : transform;
        Quaternion look = Quaternion.Euler(aimPitch, aimYaw, 0f);
        float z = zoom.Tick(Time.deltaTime);
        Vector3 pos = anchor.position - look * Vector3.forward * (z * maxCameraDistance) + Vector3.up * (z * 2f);
        if (shake > 0f)
        {
            pos += Random.insideUnitSphere * shake * impactShake;
            shake = Mathf.MoveTowards(shake, 0f, 2f * Time.deltaTime);
        }
        cam.transform.SetPositionAndRotation(pos, look);
    }

    // ---------- enter / exit ----------

    public void Enter(FirstPersonController who)
    {
        if (IsOccupied) return;
        if (Impounded) { who.Flash("Impounded"); return; }
        driver = who;
        SetParked(false);
        enterFrame = Time.frameCount;
        eArmed = false;
        cam = who.playerCamera;
        cam.transform.SetParent(null);
        who.gameObject.SetActive(false);
        zoom.Snap(0f);
        yaw = transform.eulerAngles.y;
        aimYaw = yaw; aimPitch = 0f;
        Driven = this;
        VehicleHUD.Ensure();
        autopilot = false;
        if (!hijacked) { hijacked = true; PlayerSkills.AddHijack(); }
        if (Disabled)
        {
            if (lights != null) lights.SetOn(false);
            Flash("DISABLED  -  R restart | E door | hold E roof");
        }
    }

    // toRoof: stand on the roof centre facing the car's yaw, already locked on. The car keeps
    // its mode, so the AI takes over (in its lane it drives on; off it, it flies back).
    public void Exit(bool toRoof = false)
    {
        var who = driver;
        driver = null;
        eArmed = false;
        if (Driven == this) Driven = null;
        if (RestartQTE.Active) RestartQTE.Cancel();

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
        who.NoteLeftCar(this);
        if (toRoof) who.MountPlatform(this);
        cam = null;
        PoliceDispatch.Instance?.OnPlayerExited(this);
    }

    // Throws the driver out of the door on `side` (-1 left / driver, +1 right) as an NpcBody.
    public NpcBody EjectDriver(int side, bool unconscious = false)
    {
        if (!hasDriver) return null;
        hasDriver = false;
        return NpcBody.Eject(this, side, driverKind, unconscious);
    }

    // ---------- test hooks (ScenarioTest) ----------

    public float DebugThrottle { get; set; }
    float lineDist;

    public void DebugPlace(Vector3 pos, Quaternion rot, Vector3 vel)
    {
        transform.SetPositionAndRotation(pos, rot);
        yaw = rot.eulerAngles.y;
        aimYaw = yaw;
        velocity = vel;
    }

    // Point the player's aim at p (the car turns toward the aim while throttle is held).
    public void DebugAimAt(Vector3 p)
    {
        Vector3 d = p - transform.position;
        aimYaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
    }

    // Sideways distance from its magnetic line (Lane mode), 99 when off the lane.
    public float DebugLaneOffset() => Mode == FlightMode.Lane ? lineDist : 99f;

    public void ForceStop()
    {
        velocity = Vector3.zero; speed = 0f;
        if (Mode == FlightMode.Free) LockToNearestLayer();
    }

    void Flash(string msg) { flash = msg; flashUntil = Time.time + 2f; }
}
