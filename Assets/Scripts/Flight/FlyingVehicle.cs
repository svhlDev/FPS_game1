using UnityEngine;
using UnityEngine.InputSystem;

// Three flight modes:
//   Lane  : snapped to a lane. W/S throttle, A/D drift, Space cycles layers (ping-pong).
//   Layer : free horizontal driving, altitude locked to the current layer. Mouse steers, WASD move/strafe.
//   Free  : full 3D, unregistered. Mouse steers yaw+pitch, WASD move/strafe. Police get interested.
// Ctrl (tap)     : Lane <-> Layer (release lane / snap to nearest lane in this layer)
// Ctrl + Space   : Free <-> Layer (unlock from all layers / lock to nearest layer)
public enum FlightMode { Lane, Layer, Free }

public class FlyingVehicle : MonoBehaviour
{
    [Header("Lane mode")]
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
    public float laneChangeSmoothTime = 0.6f;
    public float maxLateral = 4f;
    public float lateralSpeed = 6f;

    [Header("Layer / Free mode")]
    public float layerMaxSpeed = 45f;
    public float freeMaxSpeed = 55f;
    public float strafeFactor = 0.6f;
    public float steerSensitivity = 0.12f;
    public float altitudeSmoothTime = 0.8f;
    public float snapRadius = 25f;
    public float minAltitude = 1.5f;

    [Header("Camera")]
    public Transform cockpitAnchor;
    public float maxCameraDistance = 14f;
    public float zoomStep = 0.2f;
    public float lookSensitivity = 0.1f;

    public bool IsOccupied => driver != null;
    public FlightMode Mode { get; private set; } = FlightMode.Lane;

    LaneLayer currentLayer;
    int cycleDir = 1;
    float distance, speed, lateral, zoom, camYaw, camPitch;
    Vector2 currentOffset, offsetVel;
    Vector3 velocity;
    float yaw, pitch, altVel;
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
            case FlightMode.Lane: UpdateLane(kb); break;
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
        // Ctrl acts on release, so Ctrl+Space doesn't also trigger the lane toggle.
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
        if (Mode == FlightMode.Lane) velocity = transform.forward * speed;
        Mode = FlightMode.Free;
        yaw = transform.eulerAngles.y; pitch = 0f;
        Flash("FREE FLIGHT: unregistered");
    }

    void ToggleLaneLock()
    {
        switch (Mode)
        {
            case FlightMode.Lane:
                velocity = transform.forward * speed;
                yaw = transform.eulerAngles.y;
                Mode = FlightMode.Layer;
                Flash("Lane released");
                break;
            case FlightMode.Layer:
                if (!TrySnapToLane()) Flash("No lane in range");
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

    bool TrySnapToLane()
    {
        LanePath best = null; float bestD = 0f, bestSq = snapRadius * snapRadius;
        foreach (var lp in allLanes)
        {
            if (lp == null) continue;
            if (lp.FindNearest(transform.position, currentLayer, out float d, out float sq) && sq < bestSq)
            { best = lp; bestD = d; bestSq = sq; }
        }
        if (best == null) return false;

        path = best; distance = bestD; Mode = FlightMode.Lane;
        best.Sample(distance, out var basePos, out var fwd);
        float w = best.LaneWeight(currentLayer, distance, out var laneOff);
        Vector2 target = currentLayer == LaneLayer.Middle ? Vector2.zero : laneOff * w;

        // Start from where we actually are and glide onto the lane.
        Vector3 right = Vector3.Cross(Vector3.up, fwd).normalized;
        Vector3 residual = transform.position - best.ToWorld(basePos, fwd, target);
        currentOffset = target + new Vector2(Vector3.Dot(residual, right), residual.y);
        offsetVel = Vector2.zero; lateral = 0f;
        speed = Mathf.Max(0f, Vector3.Dot(velocity, fwd));
        Flash("Snapped to lane");
        return true;
    }

    // ---------- movement ----------

    Vector2 MoveInput(Keyboard kb)
    {
        if (!IsOccupied || kb == null) return Vector2.zero;
        return new Vector2((kb.dKey.isPressed ? 1f : 0f) - (kb.aKey.isPressed ? 1f : 0f),
                           (kb.wKey.isPressed ? 1f : 0f) - (kb.sKey.isPressed ? 1f : 0f));
    }

    Vector2 SteerInput(Mouse mouse) =>
        IsOccupied && mouse != null ? mouse.delta.ReadValue() * steerSensitivity : Vector2.zero;

    void UpdateLane(Keyboard kb)
    {
        if (path == null) { Mode = FlightMode.Layer; return; }
        float dt = Time.deltaTime;

        if (IsOccupied)
        {
            Vector2 input = MoveInput(kb);
            float max = currentLayer == LaneLayer.Middle ? middleLaneMaxSpeed : sideLaneMaxSpeed;
            if (input.y > 0f) speed = Mathf.MoveTowards(speed, max, acceleration * dt);
            if (input.y < 0f) speed = Mathf.MoveTowards(speed, 0f, braking * dt);
            if (speed > max) speed = Mathf.MoveTowards(speed, max, braking * dt);
            lateral = Mathf.Clamp(lateral + input.x * lateralSpeed * dt, -maxLateral, maxLateral);
        }
        else speed = Mathf.MoveTowards(speed, aiCruiseSpeed, acceleration * dt);

        distance += speed * dt;

        float w = path.LaneWeight(currentLayer, distance, out Vector2 laneOffset);
        if (currentLayer != LaneLayer.Middle && w <= 0.01f) currentLayer = LaneLayer.Middle; // lane converged
        Vector2 target = currentLayer == LaneLayer.Middle ? Vector2.zero : laneOffset * w;
        currentOffset = Vector2.SmoothDamp(currentOffset, target, ref offsetVel, laneChangeSmoothTime);

        path.Sample(distance, out Vector3 basePos, out Vector3 fwd);
        transform.position = path.ToWorld(basePos, fwd, currentOffset + new Vector2(lateral, 0f));
        float p = Mathf.Clamp(-offsetVel.y * 1.5f, -20f, 20f);
        transform.rotation = Quaternion.LookRotation(fwd, Vector3.up) * Quaternion.Euler(p, 0f, 0f);
        velocity = fwd * speed;
    }

    void UpdateLayer(Keyboard kb, Mouse mouse)
    {
        if (parked && !IsOccupied) return;
        float dt = Time.deltaTime;

        yaw += SteerInput(mouse).x;
        Quaternion heading = Quaternion.Euler(0f, yaw, 0f);
        Vector2 input = MoveInput(kb);
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
        Vector2 steer = SteerInput(mouse);
        yaw += steer.x;
        pitch = Mathf.Clamp(pitch - steer.y, -80f, 80f);
        Quaternion rot = Quaternion.Euler(pitch, yaw, 0f);

        Vector2 input = MoveInput(kb);
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
            if (Mode == FlightMode.Lane) // mouse orbits only when the lane does the steering
            {
                Vector2 d = mouse.delta.ReadValue() * lookSensitivity;
                camYaw += d.x;
                camPitch = Mathf.Clamp(camPitch - d.y, -60f, 70f);
            }
            float scroll = mouse.scroll.ReadValue().y;
            if (Mathf.Abs(scroll) > 0.01f) zoom = Mathf.Clamp01(zoom - Mathf.Sign(scroll) * zoomStep);
        }
        if (Mode != FlightMode.Lane)
        {
            camYaw = Mathf.Lerp(camYaw, 0f, 5f * Time.deltaTime);
            camPitch = Mathf.Lerp(camPitch, 0f, 5f * Time.deltaTime);
        }

        Transform anchor = cockpitAnchor != null ? cockpitAnchor : transform;
        Quaternion look = transform.rotation * Quaternion.Euler(camPitch, camYaw, 0f);
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
        zoom = 0f; camYaw = 0f; camPitch = 0f;
        yaw = transform.eulerAngles.y;
        TrafficAuthority.Instance?.SetPlayerVehicle(this);
    }

    public void Exit()
    {
        var who = driver;
        driver = null;
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
            GUI.Label(new Rect(20, 45, 500, 25),
                $"Upper open: {IsAvailable(LaneLayer.Upper)}   Lower open: {IsAvailable(LaneLayer.Lower)}   No-switch: {path.IsNoSwitch(distance)}");
        if (Time.time < flashUntil) GUI.Label(new Rect(20, 70, 500, 25), flash);
        GUI.Label(new Rect(20, Screen.height - 30, 900, 25),
            "Space: cycle layer   Ctrl: release/snap lane   Ctrl+Space: free flight/lock layer   Scroll: zoom   E: exit");
    }
}
