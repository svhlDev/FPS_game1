using UnityEngine;
using UnityEngine.InputSystem;

// On foot. Cars are moving platforms: land on a roof and magnet boots lock you on after a short
// slide (longer if your speed doesn't match the car's, or the car is accelerating hard). Once
// locked, the car carries you and you walk on its roof normally. Leaving a car keeps its velocity.
// Runs after the vehicles so it carries with this frame's car motion.
[DefaultExecutionOrder(100)]
[RequireComponent(typeof(CharacterController))]
public class FirstPersonController : MonoBehaviour
{
    public Camera playerCamera;
    [Tooltip("The 'Head' object the camera sits under. Defaults to the camera's parent.")]
    public Transform cameraRoot;
    public float walkSpeed = 5f;
    public float sprintSpeed = 9f;
    public float jumpHeight = 1.4f;
    public float gravity = -25f;
    public float lookSensitivity = 0.1f;
    public float interactRange = 5f;

    [Header("Air")]
    [Tooltip("Fraction of inputAcceleration available in the air.")]
    public float airControl = 0.3f;
    [Tooltip("On-foot input acceleration (m/s^2). Only used to scale air control.")]
    public float inputAcceleration = 40f;

    [Header("Magnet boots")]
    [Tooltip("How fast slide relative to the car dies out (1/s). 4 = locked on in about half a second.")]
    public float bootGrip = 4f;
    [Tooltip("Seconds after landing during which the car's acceleration can slide you.")]
    public float bootSettleTime = 0.6f;
    [Tooltip("How much of the car's acceleration turns into slide while settling.")]
    public float slipInertia = 1f;
    [Tooltip("Cap on the speed mismatch at touchdown (m/s). Slide distance is about this / bootGrip, " +
             "so 8 = about 2 m. Without a cap, landing on a 35 m/s car from a standstill slides ~9 m: off a 6 m car.")]
    public float maxLandingSlip = 8f;

    [Header("Falling")]
    [Tooltip("Respawn when this far below the lowest traffic layer.")]
    public float killDepthBelowLowestLayer = 40f;
    [Tooltip("Touching this collider respawns you (the ground far below the traffic). Leave empty to allow walking on it.")]
    public Collider fallRespawnGround;

    public float SpawnTime { get; private set; }

    CharacterController cc;
    float pitch;
    float verticalVelocity;
    int blockInteractFrame = -1;
    bool grounded;
    Collider groundCollider;          // what the last move landed on
    Vector3 airVel;                   // horizontal world velocity while airborne

    FlyingVehicle platform;           // car we're standing on
    Vector3 platformLocal;            // our position in the car's yaw-only frame
    float platformYaw;
    Vector3 lastCarLocalVel;
    Vector3 slipVel;                  // slide relative to the car, world space
    float landTime;

    Vector3 spawnPos;
    Quaternion spawnRot;
    float killHeight = float.NegativeInfinity;
    string flash; float flashUntil;

    void Awake()
    {
        cc = GetComponent<CharacterController>();
        cc.minMoveDistance = 0f; // small carry/slide moves must not be dropped
        if (cameraRoot == null) cameraRoot = playerCamera.transform.parent;
    }

    void Start()
    {
        spawnPos = transform.position;
        spawnRot = transform.rotation;
        SpawnTime = Time.time;
        killHeight = TrafficAuthority.LayerAltitude(LaneLayer.Lower) - killDepthBelowLowestLayer;
    }

    void OnEnable()
    {
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
        verticalVelocity = 0f;
        airVel = Vector3.zero;
        grounded = false;
        LeavePlatform();
    }

    void Update()
    {
        var kb = Keyboard.current;
        var mouse = Mouse.current;
        if (kb == null || mouse == null) return;
        float dt = Time.deltaTime;

        // Cars moved this frame; make their colliders match before we query against them.
        Physics.SyncTransforms();

        Vector2 look = mouse.delta.ReadValue() * lookSensitivity;
        transform.Rotate(0f, look.x, 0f);
        pitch = Mathf.Clamp(pitch - look.y, -85f, 85f);
        cameraRoot.localRotation = Quaternion.Euler(pitch, 0f, 0f);

        Vector3 carVel = platform != null ? Carry(dt) : Vector3.zero;

        float x = (kb.dKey.isPressed ? 1f : 0f) - (kb.aKey.isPressed ? 1f : 0f);
        float z = (kb.wKey.isPressed ? 1f : 0f) - (kb.sKey.isPressed ? 1f : 0f);
        float speed = kb.leftShiftKey.isPressed ? sprintSpeed : walkSpeed;
        Vector3 move = Vector3.ClampMagnitude(transform.right * x + transform.forward * z, 1f) * speed;

        // Horizontal motion: on the ground (or a roof, relative to it) input is direct;
        // in the air you keep your momentum with a little control.
        Vector3 horizontal;
        if (grounded)
        {
            horizontal = platform != null ? move + slipVel : move;
            if (verticalVelocity < 0f) verticalVelocity = -2f;
            if (kb.spaceKey.wasPressedThisFrame) verticalVelocity = Mathf.Sqrt(jumpHeight * -2f * gravity);
        }
        else
        {
            AirControl(move, dt);
            horizontal = airVel;
        }
        verticalVelocity += gravity * dt;

        groundCollider = null;
        var flags = cc.Move((horizontal + Vector3.up * verticalVelocity) * dt);
        bool wasGrounded = grounded;
        grounded = (flags & CollisionFlags.Below) != 0;

        if (grounded)
        {
            var car = groundCollider != null ? groundCollider.GetComponentInParent<FlyingVehicle>() : null;
            if (car != platform)
            {
                if (car != null) Land(car, wasGrounded ? horizontal + carVel : airVel, move);
                else LeavePlatform();
            }
            if (car == null) airVel = Vector3.zero; // landing on anything but a car stops you dead
            if (fallRespawnGround != null && groundCollider == fallRespawnGround) { Respawn(); return; }
        }
        else if (wasGrounded)
        {
            // Jumped, walked or slid off: launch with the car's velocity plus your own.
            airVel = horizontal + carVel;
            LeavePlatform();
        }

        if (platform != null)
            platformLocal = Quaternion.Inverse(platform.PlatformRotation) * (transform.position - platform.PlatformPosition);

        if (transform.position.y < killHeight) { Respawn(); return; }

        if (kb.eKey.wasPressedThisFrame && Time.frameCount != blockInteractFrame)
        {
            if (platform != null && !platform.IsOccupied)
            {
                var car = platform;
                LeavePlatform();
                car.Enter(this);
            }
            else TryHijack();
        }
    }

    void OnControllerColliderHit(ControllerColliderHit hit)
    {
        if (hit.normal.y > 0.5f) groundCollider = hit.collider;
    }

    // ---------- moving platforms ----------

    // Follow the car: move to where our stored local point is now, turn the view with it,
    // and run the magnet-boot slide. Returns the car's horizontal velocity.
    Vector3 Carry(float dt)
    {
        Quaternion rot = platform.PlatformRotation;
        float yaw = rot.eulerAngles.y;
        transform.Rotate(0f, Mathf.DeltaAngle(platformYaw, yaw), 0f);
        platformYaw = yaw;
        cc.Move(platform.PlatformPosition + rot * platformLocal - transform.position);

        // Acceleration measured in the car's own frame, so a steady cruise round a bend
        // reads as zero; braking and flooring it are what slide you.
        Vector3 carVel = Flat(platform.Velocity);
        Vector3 localVel = Quaternion.Inverse(rot) * carVel;
        Vector3 carAccel = dt > 0f ? rot * ((localVel - lastCarLocalVel) / dt) : Vector3.zero;
        lastCarLocalVel = localVel;

        slipVel *= Mathf.Exp(-bootGrip * dt);
        if (Time.time - landTime < bootSettleTime) slipVel -= carAccel * slipInertia * dt;
        return carVel;
    }

    void Land(FlyingVehicle car, Vector3 worldVel, Vector3 move)
    {
        SetPlatform(car);
        landTime = Time.time;
        // Whatever your own walking doesn't explain is slide.
        slipVel = Vector3.ClampMagnitude(Flat(worldVel) - Flat(car.Velocity) - move, maxLandingSlip);
        airVel = Vector3.zero;
    }

    // Placed on a roof by the car (hold-E exit): already locked on, no slide.
    public void MountPlatform(FlyingVehicle car)
    {
        SetPlatform(car);
        landTime = float.NegativeInfinity;
        slipVel = Vector3.zero;
        airVel = Vector3.zero;
        grounded = true;
        verticalVelocity = -2f;
    }

    void SetPlatform(FlyingVehicle car)
    {
        platform = car;
        Quaternion rot = car.PlatformRotation;
        platformYaw = rot.eulerAngles.y;
        platformLocal = Quaternion.Inverse(rot) * (transform.position - car.PlatformPosition);
        lastCarLocalVel = Quaternion.Inverse(rot) * Flat(car.Velocity);
    }

    void LeavePlatform()
    {
        platform = null;
        slipVel = Vector3.zero;
    }

    // Can't add speed beyond your input speed in the input direction, but can steer and brake.
    void AirControl(Vector3 move, float dt)
    {
        float wishSpeed = move.magnitude;
        if (wishSpeed < 1e-3f) return;
        Vector3 dir = move / wishSpeed;
        float add = wishSpeed - Vector3.Dot(airVel, dir);
        if (add > 0f) airVel += dir * Mathf.Min(add, airControl * inputAcceleration * dt);
    }

    static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }

    void Respawn()
    {
        cc.enabled = false;
        transform.SetPositionAndRotation(spawnPos, spawnRot);
        cc.enabled = true;
        LeavePlatform();
        airVel = Vector3.zero;
        verticalVelocity = 0f;
        grounded = false;
        pitch = 0f;
        cameraRoot.localRotation = Quaternion.identity;
        SpawnTime = Time.time;
        Flash("You fell");
    }

    // ---------- interaction ----------

    void TryHijack()
    {
        var ray = new Ray(playerCamera.transform.position, playerCamera.transform.forward);
        if (!Physics.Raycast(ray, out var hit, interactRange, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)) return;
        var vehicle = hit.collider.GetComponentInParent<FlyingVehicle>();
        if (vehicle != null && !vehicle.IsOccupied) vehicle.Enter(this);
    }

    public void AttachCamera(Camera cam)
    {
        cam.transform.SetParent(cameraRoot, false);
        cam.transform.localPosition = Vector3.zero;
        cam.transform.localRotation = Quaternion.identity;
        pitch = 0f;
        cameraRoot.localRotation = Quaternion.identity;
    }

    public void BlockInteractThisFrame() => blockInteractFrame = Time.frameCount;

    void Flash(string msg) { flash = msg; flashUntil = Time.time + 2f; }

    void OnGUI()
    {
        if (Time.time < flashUntil) GUI.Label(new Rect(Screen.width / 2f - 100, Screen.height / 2f - 60, 400, 25), flash);
        if (platform != null && grounded)
            GUI.Label(new Rect(20, Screen.height - 30, 600, 25), "E take car | Space jump");
    }
}
