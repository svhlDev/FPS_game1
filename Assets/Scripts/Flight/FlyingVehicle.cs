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
public enum FlightMode { Lane, Layer, Free }

public class FlyingVehicle : MonoBehaviour
{
    [Header("Lane")]
    [Tooltip("Leave empty for a parked car that sits still until hijacked.")]
    public LanePath path;
    public float startDistance = 0f;
    public LaneLayer startLayer = LaneLayer.Middle;
    [Tooltip("Speed when nobody is driving (traffic).")]
    public float aiCruiseSpeed = 40f;
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

    [Header("Camera")]
    public Transform cockpitAnchor;
    public float maxCameraDistance = 14f;
    public float zoomStep = 0.2f;

    public bool IsOccupied => driver != null;
    public FlightMode Mode { get; private set; } = FlightMode.Lane;

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
    LanePath[] allLanes;
    string flash; float flashUntil;

    void Start()
    {
        allLanes = FindObjectsByType<LanePath>(FindObjectsSortMode.None);
        yaw = transform.eulerAngles.y;
        if (path == null) { Mode = FlightMode.Layer; parked = true; currentLayer = TrafficAuthority.NearestLayer(transform.position.y); }
        else { distance = startDistance; currentLayer = startLayer; speed = aiCruiseSpeed; }
    }

    void Update()
    {
        var kb = Keyboard.current;
        var mouse = Mouse.current;
        if (IsOccupied && kb != null) HandleModeInput(kb);

        switch (Mode)
        {
            case FlightMode.Lane:
                if (IsOccupied) UpdateLaneMagnetic(kb, mouse);
                else UpdateLaneTraffic();
                break;
            case FlightMode.Layer: UpdateLayer(kb, mouse); break;
            case FlightMode.Free: UpdateFree(kb, mouse); break;
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
        if (kb.eKey.wasPressedThisFrame && Time.frameCount != enterFrame) Exit();
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
        if (!IsOccupied || mouse == null) return;
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

    // Player-driven lane mode: free flight plus a magnetic pull toward the line.
    void UpdateLaneMagnetic(Keyboard kb, Mouse mouse)
    {
        if (path == null) { Mode = FlightMode.Layer; return; }
        float dt = Time.deltaTime;
        Vector2 input = MoveInput(kb);
        UpdateAim(mouse);

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
            Mode = FlightMode.Layer;
            magnetHold = 0f;
            Flash("Broke free of the lane");
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

        float max = currentLayer == LaneLayer.Middle ? middleLaneMaxSpeed : sideLaneMaxSpeed;
        if (input.y > 0f) fwdSpeed = Mathf.MoveTowards(fwdSpeed, max, acceleration * dt);
        else if (input.y < 0f) fwdSpeed = Mathf.MoveTowards(fwdSpeed, 0f, braking * dt);
        if (fwdSpeed > max) fwdSpeed = Mathf.MoveTowards(fwdSpeed, max, braking * dt);

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
        speed = fwdSpeed;

        float bank = Mathf.Clamp(-Vector3.Dot(hv, carRight) * 0.6f, -20f, 20f);
        float p = Mathf.Clamp(-altVel * 1.5f, -20f, 20f);
        transform.SetPositionAndRotation(pos, heading * Quaternion.Euler(p, 0f, bank));
    }

    // Unoccupied traffic: rides the line, smoothed.
    void UpdateLaneTraffic()
    {
        if (path == null) { Mode = FlightMode.Layer; return; }
        float dt = Time.deltaTime;
        speed = Mathf.MoveTowards(speed, aiCruiseSpeed, acceleration * dt);
        distance += speed * dt;

        Vector3 linePoint = UpdateLineOffset(out Vector3 laneFwd);
        transform.position = linePoint;
        transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(laneFwd, Vector3.up), 8f * dt);
        velocity = laneFwd * speed;
        yaw = transform.eulerAngles.y;
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
        transform.SetPositionAndRotation(pos, heading * Quaternion.Euler(p, 0f, bank));
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

        Vector3 pos = transform.position + velocity * dt;
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
        cam.transform.SetPositionAndRotation(pos, look);
    }

    // ---------- enter / exit / police ----------

    public void Enter(FirstPersonController who)
    {
        if (IsOccupied) return;
        driver = who;
        parked = false;
        enterFrame = Time.frameCount;
        cam = who.playerCamera;
        cam.transform.SetParent(null);
        who.gameObject.SetActive(false);
        zoom = 0f;
        yaw = transform.eulerAngles.y;
        aimYaw = yaw; aimPitch = 0f;
        TrafficAuthority.Instance?.SetPlayerVehicle(this);
    }

    public void Exit()
    {
        var who = driver;
        driver = null;

        // Hand a lane car back to traffic without a jump: start the line where the car actually is.
        if (Mode == FlightMode.Lane && path != null)
        {
            path.Sample(distance, out var basePos, out _);
            Vector3 fwd = path.SmoothForward(distance);
            Vector3 right = Vector3.Cross(Vector3.up, fwd).normalized;
            Vector3 rel = transform.position - basePos;
            currentOffset = new Vector2(Vector3.Dot(rel, right), rel.y);
            offsetVel = Vector2.zero;
            speed = Mathf.Max(0f, speed);
        }

        who.transform.SetPositionAndRotation(transform.position + transform.right * 3f,
                                             Quaternion.Euler(0f, transform.eulerAngles.y, 0f));
        who.gameObject.SetActive(true);
        who.AttachCamera(cam);
        who.BlockInteractThisFrame();
        cam = null;
        TrafficAuthority.Instance?.SetPlayerVehicle(null);
    }

    public void ForceStop()
    {
        velocity = Vector3.zero; speed = 0f;
        if (Mode == FlightMode.Free) LockToNearestLayer();
    }

    void Flash(string msg) { flash = msg; flashUntil = Time.time + 2f; }

    void OnGUI()
    {
        if (!IsOccupied) return;
        float shownSpeed = Mode == FlightMode.Lane ? speed : velocity.magnitude;
        string where = Mode == FlightMode.Free ? "none" : currentLayer.ToString();
        GUI.Label(new Rect(20, 20, 500, 25), $"Mode: {Mode}   Layer: {where}   Speed: {shownSpeed:0}");
        if (Mode == FlightMode.Lane && path != null)
            GUI.Label(new Rect(20, 45, 700, 25),
                $"Magnet: {magnetHold * 100f:0}%   Upper open: {IsAvailable(LaneLayer.Upper)}   Lower open: {IsAvailable(LaneLayer.Lower)}   No-switch: {path.IsNoSwitch(distance)}");
        if (Time.time < flashUntil) GUI.Label(new Rect(20, 70, 500, 25), flash);
        GUI.Label(new Rect(20, Screen.height - 30, 900, 25),
            "Space: cycle layer   Ctrl: magnet off/on   Ctrl+Space: free flight/lock layer   Scroll: zoom   E: exit");
    }
}
