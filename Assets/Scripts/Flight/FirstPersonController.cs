using Unity.Profiling;
using UnityEngine;
using UnityEngine.InputSystem;

// On foot. Cars are moving platforms: land on a roof and magnet boots lock you on after a short
// slide (longer if your speed doesn't match the car's, or the car is accelerating hard). Once
// locked, the car carries you and you walk on its roof normally. Leaving a car keeps its velocity.
// While falling, the boots nudge you toward the safe front of a roof you're about to land on.
// Cars with a rear rack (VehicleGrabPoint) can be caught from the air (swept test, timing is
// everything): you hang off the back and mash Space to climb up.
// Boot thrusters: one extra jump in the air, and holding Space while falling glides.
// Falling has no limit: you land on the street. A hard landing (no glide) staggers you for a moment.
// Scroll zooms from first person out to a third-person shoulder boom (same feel as the car).
// The player is a real body (CharacterFigure, posed by FigureAnimator): the camera sits in its head
// (the head itself hidden from that view), looking turns the head first and the body follows, and
// looking down shows your own body. A blob shadow marks where you'll land.
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

    [Header("Boot thrusters")]
    [Tooltip("Extra jumps while airborne. Reset on landing on anything.")]
    public int airJumps = 1;
    [Tooltip("Max fall speed while Space is held (m/s).")]
    public float glideFallSpeed = 3f;
    [Tooltip("Air control while gliding (replaces airControl).")]
    public float glideAirControl = 0.5f;
    public float fuelMax = 100f;
    public float fuel = 100f;
    [Tooltip("0 = infinite glide.")]
    public float glideFuelPerSecond = 0f;
    [Tooltip("0 = free double jumps.")]
    public float doubleJumpFuelCost = 0f;
    [Tooltip("Blocks under the feet that glow while thrusting.")]
    public Renderer[] thrusterRenderers;

    [Header("Magnet boots")]
    [Tooltip("How fast slide relative to the car dies out (1/s). 4 = locked on in about half a second.")]
    public float bootGrip = 4f;
    [Tooltip("Seconds after landing during which the car's acceleration can slide you.")]
    public float bootSettleTime = 0.6f;
    [Tooltip("How much of the car's acceleration turns into slide while settling.")]
    public float slipInertia = 1f;
    [Tooltip("Furthest a landing can slide you (m). Caps the touchdown speed mismatch at bootGrip * this. " +
             "Without a cap, landing on a 35 m/s car from a standstill slides ~9 m: off a 6 m car.")]
    public float maxSlideDistance = 1.0f;

    float MaxLandingSlip => bootGrip * maxSlideDistance;

    [Header("Landing assist")]
    [Tooltip("Cars within this many metres horizontally, and up to this far below your feet, are considered.")]
    public float assistRange = 8f;
    [Tooltip("Only assist when the predicted landing is within this distance of the safe zone.")]
    public float assistMargin = 1.5f;
    [Tooltip("Max assist acceleration (m/s^2). This clamp is what keeps bad jumps bad.")]
    public float assistAccel = 8f;
    [Tooltip("Safe zone inset from the roof's side edges (m). The zone is the front 2/3 of the roof.")]
    public float assistSideInset = 0.3f;

    [Header("Rear grab")]
    [Tooltip("Catch a grab point when your hands come within this distance of it.")]
    public float grabRadius = 1.5f;
    [Tooltip("Max body sway from the car's acceleration (m).")]
    public float swayAmount = 0.5f;
    [Tooltip("Sway per m/s^2 of car acceleration, before the swayAmount clamp.")]
    public float swayPerAccel = 0.05f;
    [Tooltip("Feet hang this far below the grab point; also where your hands are when checking for a catch.")]
    public float hangDrop = 1.8f;
    public float hangBehind = 0.4f;
    [Tooltip("After letting go, can't catch again for this long.")]
    public float regrabDelay = 0.5f;
    [Tooltip("Space presses to climb from a full hang.")]
    public int climbPresses = 6;
    [Tooltip("Climb progress lost per second (fraction of the full climb).")]
    public float climbDecay = 0.3f;
    [Tooltip("Relative speed at the catch that counts as fully violent (m/s).")]
    public float violentCatchSpeed = 25f;
    [Tooltip("Camera shake at a fully violent catch (m).")]
    public float catchShake = 0.3f;
    [Tooltip("Hang offset kick at a fully violent catch (m).")]
    public float catchSwing = 0.8f;
    [Tooltip("Seconds for the catch swing to settle.")]
    public float catchSwingSettle = 0.5f;

    [Header("Falling")]
    [Tooltip("Landing faster than this (m/s) staggers you: no input for staggerTime, camera dips.")]
    public float staggerFallSpeed = 25f;
    public float staggerTime = 1f;
    public float staggerDip = 0.5f;

    [Header("Body")]
    [Tooltip("Sole to top of head; the collision capsule matches it.")]
    public float figureHeight = CharacterFigure.DefaultHeight;
    [Tooltip("The player's character sheet: the body is generated from it (sex, STR, INT, DEX, seed).")]
    public CharacterSheet sheet = new CharacterSheet(Sex.Male, 10, 10, 10, 7);
    [Tooltip("Off: the old primitive figure.")]
    public bool generatedBody = true;

    [Header("Camera")]
    [Tooltip("Same zoom behaviour as the car camera; on-foot zoom is remembered separately.")]
    public CameraZoom zoom = new CameraZoom();
    [Tooltip("Third-person boom length at full zoom-out (m).")]
    public float maxBoomDistance = 6f;
    [Tooltip("Right-shoulder offset at full zoom-out (m); scales with zoom, so first person has none.")]
    public float shoulderOffset = 0.5f;
    [Tooltip("Radius of the camera's collision probe.")]
    public float cameraRadius = 0.2f;
    [Tooltip("Body is shown once the camera is at least this far from the head.")]
    public float showBodyDistance = 0.5f;
    [Tooltip("Body + visor. Hidden (shadow only) in first person. Defaults to all child renderers.")]
    public Renderer[] bodyRenderers;

    [Header("Blob shadow")]
    [Tooltip("Soft dark blob under the player on whatever is below. Leave empty for none.")]
    public Material blobShadowMaterial;
    public float blobSize = 0.9f;
    public float blobMaxDistance = 300f;

    public float SpawnTime { get; private set; }
    public bool IsHanging => hang != null;
    public CharacterFigure Figure { get; private set; }
    public FigureAnimator Animator { get; private set; }
    // The player (on foot or not). Set in Awake, so it exists while driving too.
    public static FirstPersonController Instance { get; private set; }
    // No movement or interaction input (yanking a driver out). Gravity still applies.
    public bool Frozen { get; set; }
    // Cuffed: no movement or interaction, can't hit; an officer drags you (Drag).
    public bool Restrained { get; set; }
    public bool Stunned => Time.time < stunUntil;
    public float Health { get; private set; } = 100f;
    public float maxHealth = 100f;
    // The car whose roof you're standing on, if any.
    public FlyingVehicle Platform => platform;
    // Jumped (or got out) of one car and landed on another: (from, to).
    public static event System.Action<FlyingVehicle, FlyingVehicle> CarJump;
    public Vector3 Velocity => (grounded ? Vector3.zero : airVel) + Vector3.up * verticalVelocity;

    CharacterController cc;
    float pitch;
    float verticalVelocity;
    int blockInteractFrame = -1;
    bool grounded;
    Collider groundCollider;          // what the last move landed on
    Vector3 airVel;                   // horizontal world velocity while airborne
    bool assistActive;

    FlyingVehicle platform;           // car we're standing on
    Vector3 platformLocal;            // our position in the car's yaw-only frame
    float platformYaw;
    Vector3 lastCarLocalVel;
    Vector3 slipVel;                  // slide relative to the car, world space
    float landTime;

    VehicleGrabPoint hang;            // grab point we're hanging from
    Vector3 hangLocal;                // grab point in the car's yaw-only frame
    float regrabAt;
    Vector3 sway, swayVel;
    Vector3 swing, swingVel;          // catch kick, car-local
    float climbProgress;
    Vector3 lastHand;                 // hand point last frame, for the swept catch
    bool lastHandValid;
    float camShake;

    int airJumpsLeft;
    bool gliding;
    float thrusterGlowUntil;
    bool thrustersOn = true;

    Vector3 spawnPos;
    Quaternion spawnRot;
    float staggerUntil = -1f;
    Vector3 animVel;
    float lastDragTime = -10f;
    float stunUntil = -1f, stunTime = 1f;
    float lastHurt = -100f;
    Vector3 pendingDrag;
    FlyingVehicle lastLeftCar; float leftCarTime;
    PlayerFists fists;
    string flash; float flashUntil;

    int playerLayerMask;
    readonly RaycastHit[] camHits = new RaycastHit[16];
    bool bodyShown = true;
    Transform blob;

    void Awake()
    {
        Instance = this;
        cc = GetComponent<CharacterController>();
        fists = GetComponent<PlayerFists>();
        cc.minMoveDistance = 0f; // small carry/slide moves must not be dropped
        if (cameraRoot == null) cameraRoot = playerCamera.transform.parent;
        // Older scenes carry a capsule + visor body: hide it, the figure replaces it.
        if (bodyRenderers != null) foreach (var r in bodyRenderers) if (r != null) r.enabled = false;
        BodyPool.Ensure(); // starts filling the crowd's bodies in the background
        Figure = generatedBody ? CharacterFigure.BuildGenerated(transform, sheet, CharacterFigure.Role.Player, shadows: true)
                               : CharacterFigure.Build(transform, CharacterFigure.Role.Player, null, figureHeight, shadows: true);
        float h = Figure.Height;
        cc.height = h;
        cc.radius = 0.2f * h / CharacterFigure.DefaultHeight;
        cc.center = new Vector3(0f, h * 0.5f, 0f);
        Animator = gameObject.AddComponent<FigureAnimator>();
        Flammable.Add(gameObject, Flammable.Kind.Character);
        Animator.FollowLook = true;
        if (GetComponent<PlayerWeapon>() == null) gameObject.AddComponent<PlayerWeapon>();
        // First person hides only the head (the camera is inside it); everything else stays visible.
        bodyRenderers = Figure.FaceRenderers.Count > 0 ? Figure.FaceRenderers.ToArray() : new[] { Figure.HeadRenderer };
        bodyShown = true;
        playerCamera.nearClipPlane = 0.05f;
        int playerLayer = LayerMask.NameToLayer("Player");
        playerLayerMask = playerLayer >= 0 ? 1 << playerLayer : 0;
        CreateBlob();
    }

    void Start()
    {
        spawnPos = transform.position;
        spawnRot = transform.rotation;
        SpawnTime = Time.time;
        VehicleHUD.Ensure();
        PoliceDispatch.Ensure();
    }

    void OnEnable()
    {
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
        verticalVelocity = 0f;
        airVel = Vector3.zero;
        grounded = false;
        assistActive = false;
        hang = null;
        gliding = false;
        lastHandValid = false;
        airJumpsLeft = airJumps;
        if (cc != null) cc.enabled = true;
        LeavePlatform();
    }

    float JumpVelocity => Mathf.Sqrt(jumpHeight * -2f * gravity);

    // Driving: the car owns the camera, and the blob goes away with us.
    void OnDisable()
    {
        if (blob != null) blob.gameObject.SetActive(false);
    }

    void OnDestroy()
    {
        if (blob != null) Destroy(blob.gameObject);
    }

    void Update()
    {
        var kb = Keyboard.current;
        var mouse = Mouse.current;
        if (kb == null || mouse == null) return;
        float dt = Time.deltaTime;

        // Cars moved this frame; make their colliders match before we query against them.
        using (SyncMarker.Auto()) Physics.SyncTransforms();

        // No mouse look while the cursor is released (Esc).
        Vector2 look = Cursor.lockState == CursorLockMode.Locked ? mouse.delta.ReadValue() * lookSensitivity : Vector2.zero;
        transform.Rotate(0f, look.x, 0f);
        pitch = Mathf.Clamp(pitch - look.y, -85f, 85f);
        cameraRoot.localRotation = Quaternion.Euler(pitch, 0f, 0f);

        if (hang != null) { animVel = Vector3.zero; UpdateHanging(kb, dt); return; }

        Vector3 carVel = platform != null ? Carry(dt) : Vector3.zero;

        bool noInput = Frozen || Restrained || Stunned || Time.time < staggerUntil;
        if (fists != null) fists.CanHit = !Restrained && !Stunned;
        if (Health < maxHealth && Time.time - lastHurt > 8f) Health = Mathf.Min(maxHealth, Health + 5f * dt);
        float x = noInput ? 0f : (kb.dKey.isPressed ? 1f : 0f) - (kb.aKey.isPressed ? 1f : 0f);
        float z = noInput ? 0f : (kb.wKey.isPressed ? 1f : 0f) - (kb.sKey.isPressed ? 1f : 0f);
        float speed = kb.leftShiftKey.isPressed ? sprintSpeed : walkSpeed;
        Vector3 move = Vector3.ClampMagnitude(transform.right * x + transform.forward * z, 1f) * speed;

        // Horizontal motion: on the ground (or a roof, relative to it) input is direct;
        // in the air you keep your momentum with a little control.
        Vector3 horizontal;
        if (grounded)
        {
            assistActive = false;
            gliding = false;
            airJumpsLeft = airJumps; // landing on anything, roofs included
            horizontal = platform != null ? move + slipVel : move;
            if (verticalVelocity < 0f) verticalVelocity = -2f;
            if (kb.spaceKey.wasPressedThisFrame && !noInput) verticalVelocity = JumpVelocity;
        }
        else
        {
            // A press in the air spends the double jump; keep holding and the glide takes over from the apex.
            if (kb.spaceKey.wasPressedThisFrame && !noInput && airJumpsLeft > 0 && fuel >= doubleJumpFuelCost)
            {
                airJumpsLeft--;
                fuel -= doubleJumpFuelCost;
                verticalVelocity = Mathf.Max(verticalVelocity, JumpVelocity);
                thrusterGlowUntil = Time.time + 0.25f;
            }
            gliding = !noInput && kb.spaceKey.isPressed && verticalVelocity < 0f && (glideFuelPerSecond <= 0f || fuel > 0f);
            AirControl(move, dt, gliding ? glideAirControl : airControl);
            assistActive = verticalVelocity < 0f && LandingAssist(dt);
            horizontal = airVel;
        }
        verticalVelocity += gravity * dt;
        if (gliding)
        {
            verticalVelocity = Mathf.Max(verticalVelocity, -glideFallSpeed);
            fuel = Mathf.Max(0f, fuel - glideFuelPerSecond * dt);
        }

        groundCollider = null;
        float fallSpeed = -verticalVelocity;
        animVel = (grounded ? move + (platform != null ? slipVel : Vector3.zero) : horizontal) + Vector3.up * verticalVelocity;
        if (pendingDrag.sqrMagnitude > 1e-6f) { lastDragTime = Time.time; animVel += pendingDrag / Mathf.Max(dt, 1e-4f); }
        UpdateBurning(dt, move);
        var flags = cc.Move((horizontal + Vector3.up * verticalVelocity) * dt + pendingDrag);
        pendingDrag = Vector3.zero;
        bool wasGrounded = grounded;
        grounded = (flags & CollisionFlags.Below) != 0;
        if (grounded && !wasGrounded && fallSpeed > staggerFallSpeed)
        {
            staggerUntil = Time.time + staggerTime;
            camShake = Mathf.Max(camShake, 0.5f);
        }

        if (grounded)
        {
            var car = groundCollider != null ? groundCollider.GetComponentInParent<FlyingVehicle>() : null;
            if (car != platform)
            {
                if (car != null) Land(car, wasGrounded ? horizontal + carVel : airVel, move);
                else LeavePlatform();
            }
            if (car == null) { airVel = Vector3.zero; lastLeftCar = null; } // landing on anything but a car stops you dead
        }
        else if (wasGrounded)
        {
            // Jumped, walked or slid off: launch with the car's velocity plus your own.
            airVel = horizontal + carVel;
            LeavePlatform();
        }

        if (grounded) lastHandValid = false;
        else if (TryGrab()) return;

        if (platform != null)
            platformLocal = Quaternion.Inverse(platform.PlatformRotation) * (transform.position - platform.PlatformPosition);

        if (kb.eKey.wasPressedThisFrame && !noInput && Time.frameCount != blockInteractFrame)
        {
            if (platform != null && !platform.IsOccupied)
            {
                var car = platform;
                LeavePlatform();
                TryEnter(car);
            }
            else TryHijack();
        }
    }

    static readonly ProfilerMarker SyncMarker = new ProfilerMarker("FPC.SyncTransforms");

    void OnControllerColliderHit(ControllerColliderHit hit)
    {
        if (hit.normal.y > 0.5f) groundCollider = hit.collider;
    }

    // ---------- camera / body / blob shadow ----------

    // Camera orbits the head (which carries the pitch) on a boom with a right-shoulder offset.
    // Collision pulls it in, but only against static world: moving cars and the player are ignored.
    void LateUpdate()
    {
        zoom.HandleScroll(Mouse.current);
        float z = zoom.Tick(Time.deltaTime);
        PoseBody();
        // The eyes: just in front of the head's centre, wherever the posed head is now.
        if (Figure != null)
            // The gait's hip drop is damped: the eyes keep 30% of it (a full stride's dip reads as bouncing).
            cameraRoot.position = Figure.EyePosition(transform.eulerAngles.y) + Vector3.up * (Animator != null ? Animator.GaitDrop * 0.7f : 0f);

        var cam = playerCamera.transform;
        if (cam.parent == cameraRoot)
        {
            Vector3 local = new Vector3(shoulderOffset * z, 0f, -maxBoomDistance * z);
            float dist = local.magnitude;
            if (dist > 1e-3f)
            {
                float allowed = CameraClearance(cameraRoot.position, cameraRoot.rotation * (local / dist), dist);
                local *= allowed / dist;
                dist = allowed;
            }
            if (camShake > 0f)
            {
                local += Random.insideUnitSphere * camShake * catchShake;
                camShake = Mathf.MoveTowards(camShake, 0f, 2f * Time.deltaTime);
            }
            // Hard landings and stuns move the head itself (the body crouches / falls); stunned also
            // tilts the view.
            float down = Stunned ? Mathf.Clamp01(Mathf.Min((stunUntil - Time.time) * 4f, (Time.time - (stunUntil - stunTime)) * 4f)) : 0f;
            cam.localPosition = local;
            cam.localRotation = Quaternion.Euler(0f, 0f, 70f * down);
            SetBodyVisible(dist > showBodyDistance);
        }
        UpdateBlob();
        SetThrusters(hang == null && (gliding || Time.time < thrusterGlowUntil));
    }

    // Inputs for the body's pose (the animator runs before this, so they apply from the next frame).
    void PoseBody()
    {
        if (Animator == null) return;
        Animator.LookYaw = transform.eulerAngles.y;
        Animator.LookPitch = pitch;
        Animator.Velocity = animVel;
        Animator.Grounded = grounded || hang != null;
        FigureAnimator.Pose pose;
        if (hang != null) pose = FigureAnimator.Pose.Hang;
        else if (Stunned) pose = FigureAnimator.Pose.Stunned;
        else if (Restrained) pose = Time.time - lastDragTime < 0.2f ? FigureAnimator.Pose.Dragged : FigureAnimator.Pose.Cuffed;
        else if (Time.time < staggerUntil) pose = FigureAnimator.Pose.Landing;
        else if (gliding) pose = FigureAnimator.Pose.Glide;
        else if (!grounded) pose = FigureAnimator.Pose.Air;
        else pose = FigureAnimator.Pose.Normal;
        Animator.CurrentPose = pose;
    }

    void SetThrusters(bool on)
    {
        if (on == thrustersOn || thrusterRenderers == null) return;
        thrustersOn = on;
        foreach (var r in thrusterRenderers)
            if (r != null) r.enabled = on;
    }

    float CameraClearance(Vector3 origin, Vector3 dir, float dist)
    {
        int mask = Physics.DefaultRaycastLayers & ~playerLayerMask;
        int n = Physics.SphereCastNonAlloc(origin, cameraRadius, dir, camHits, dist, mask, QueryTriggerInteraction.Ignore);
        float allowed = dist;
        for (int i = 0; i < n; i++)
        {
            var h = camHits[i];
            if (h.distance <= 0f || IsMoving(h.collider)) continue;
            allowed = Mathf.Min(allowed, h.distance);
        }
        return allowed;
    }

    // Cars have kinematic bodies; the component check covers scenes built before that.
    static bool IsMoving(Collider c) =>
        c.attachedRigidbody != null || c.GetComponentInParent<FlyingVehicle>() != null;

    void SetBodyVisible(bool show)
    {
        if (show == bodyShown) return;
        bodyShown = show;
        var mode = show ? UnityEngine.Rendering.ShadowCastingMode.On : UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly;
        foreach (var r in bodyRenderers)
            if (r != null) r.shadowCastingMode = mode;
    }

    void CreateBlob()
    {
        if (blobShadowMaterial == null) return;
        var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
        go.name = "PlayerBlobShadow";
        Destroy(go.GetComponent<Collider>());
        go.layer = 2; // Ignore Raycast
        var r = go.GetComponent<MeshRenderer>();
        r.sharedMaterial = blobShadowMaterial;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;
        blob = go.transform;
        blob.localScale = Vector3.one * blobSize;
        go.SetActive(false);
    }

    // Straight down from the player onto whatever is below (roofs included), flat on the surface.
    void UpdateBlob()
    {
        if (blob == null) return;
        int mask = Physics.DefaultRaycastLayers & ~playerLayerMask;
        bool hit = Physics.Raycast(transform.position + Vector3.up * 0.5f, Vector3.down, out var h,
                                   blobMaxDistance, mask, QueryTriggerInteraction.Ignore);
        blob.gameObject.SetActive(hit);
        if (!hit) return;
        // Quad's visible face points along -Z, so aim -Z along the surface normal.
        blob.SetPositionAndRotation(h.point + h.normal * 0.03f, Quaternion.LookRotation(-h.normal));
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
        Vector3 carAccel = rot * CarLocalAccel(rot, carVel, dt);

        slipVel *= Mathf.Exp(-bootGrip * dt);
        if (Time.time - landTime < bootSettleTime) slipVel -= carAccel * slipInertia * dt;
        return carVel;
    }

    // Car's horizontal acceleration in its yaw-only frame since last frame.
    Vector3 CarLocalAccel(Quaternion rot, Vector3 carVel, float dt)
    {
        Vector3 localVel = Quaternion.Inverse(rot) * carVel;
        Vector3 accel = dt > 0f ? (localVel - lastCarLocalVel) / dt : Vector3.zero;
        lastCarLocalVel = localVel;
        return accel;
    }

    void Land(FlyingVehicle car, Vector3 worldVel, Vector3 move)
    {
        if (lastLeftCar != null && lastLeftCar != car && Time.time - leftCarTime < 6f) CarJump?.Invoke(lastLeftCar, car);
        lastLeftCar = null;
        SetPlatform(car);
        landTime = Time.time;
        // Whatever your own walking doesn't explain is slide.
        slipVel = Vector3.ClampMagnitude(Flat(worldVel) - Flat(car.Velocity) - move, MaxLandingSlip);
        airVel = Vector3.zero;
    }

    // Placed on a roof by the car (hold-E exit) or by climbing up from a rack: already locked on, no slide.
    public void MountPlatform(FlyingVehicle car)
    {
        SetPlatform(car);
        landTime = float.NegativeInfinity;
        slipVel = Vector3.zero;
        airVel = Vector3.zero;
        grounded = true;
        verticalVelocity = -2f;
        airJumpsLeft = airJumps;
        gliding = false;
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
        if (platform != null) NoteLeftCar(platform);
        platform = null;
        slipVel = Vector3.zero;
    }

    // Can't add speed beyond your input speed in the input direction, but can steer and brake.
    void AirControl(Vector3 move, float dt, float control)
    {
        float wishSpeed = move.magnitude;
        if (wishSpeed < 1e-3f) return;
        Vector3 dir = move / wishSpeed;
        float add = wishSpeed - Vector3.Dot(airVel, dir);
        if (add > 0f) airVel += dir * Mathf.Min(add, control * inputAcceleration * dt);
    }

    // ---------- landing assist ----------

    // Predict where we'd touch down on each nearby roof (relative to the car) and, for the
    // closest near-miss, accelerate toward the safe zone: front 2/3 of the roof, inset from the
    // sides. Just enough to arrive (a = 2*miss/t^2), clamped so bad jumps stay bad.
    bool LandingAssist(float dt)
    {
        Vector3 feet = transform.position;
        float bestMiss = float.MaxValue, bestT = 0f;
        Vector3 bestDir = Vector3.zero;

        foreach (var car in FlyingVehicle.Active)
        {
            if (car.IsOccupied) continue;
            float dy = feet.y - car.RoofY;
            if (dy < 0f || dy > assistRange) continue;
            Vector3 toCar = car.PlatformPosition - feet;
            if (toCar.x * toCar.x + toCar.z * toCar.z > assistRange * assistRange) continue;

            // Time until feet reach the roof: dy + vRel*t + g*t^2/2 = 0 (g < 0).
            float vRel = verticalVelocity - car.Velocity.y;
            float t = gliding
                ? (vRel < -0.01f ? dy / -vRel : float.MaxValue)                // constant glide sink
                : (-vRel - Mathf.Sqrt(vRel * vRel - 2f * gravity * dy)) / gravity;
            if (t <= 1e-3f || t > 10f) continue;

            Quaternion rot = car.PlatformRotation;
            Vector3 relVel = airVel - Flat(car.Velocity);
            Vector3 land = Quaternion.Inverse(rot) * Flat(feet + relVel * t - car.PlatformPosition) - Flat(car.BodyCenterLocal);

            Vector3 half = car.BodyHalfExtents;
            float xMax = Mathf.Max(0f, half.x - assistSideInset);
            var nearest = new Vector3(Mathf.Clamp(land.x, -xMax, xMax), 0f, Mathf.Clamp(land.z, -half.z / 3f, half.z));
            Vector3 missVec = nearest - land;
            float miss = missVec.magnitude;
            if (miss > assistMargin || miss >= bestMiss) continue;

            bestMiss = miss;
            bestT = t;
            bestDir = miss > 1e-4f ? rot * (missVec / miss) : Vector3.zero;
        }

        if (bestMiss == float.MaxValue) return false;
        float a = Mathf.Min(2f * bestMiss / (bestT * bestT), assistAccel);
        airVel += bestDir * a * dt;
        return true;
    }

    // ---------- rear grab / hanging ----------

    // Swept catch: the hand point's path since last frame, measured relative to each grab point (so in
    // the car's moving frame), against a sphere of grabRadius. A 25 m/s free fall moves ~0.4 m a frame,
    // so a point test would miss. No assist and no speed limit: timing is the skill.
    bool TryGrab()
    {
        Vector3 hands = transform.position + Vector3.up * hangDrop;
        Vector3 prevHands = lastHandValid ? lastHand : hands;
        bool swept = lastHandValid;
        lastHand = hands;
        lastHandValid = true;
        if (verticalVelocity >= 0f || Time.time < regrabAt) return false;

        float r2 = grabRadius * grabRadius;
        VehicleGrabPoint best = null;
        float bestD2 = float.MaxValue;
        foreach (var g in VehicleGrabPoint.All)
        {
            if (g.Vehicle == null || g.Vehicle.IsOccupied) continue;
            Vector3 p1 = hands - g.transform.position;
            Vector3 p0 = swept ? prevHands - g.PreviousPosition : p1;
            Vector3 seg = p1 - p0;
            float reach = grabRadius + seg.magnitude;
            if (p1.sqrMagnitude > reach * reach) continue;

            float ss = seg.sqrMagnitude;
            float s = ss > 1e-8f ? Mathf.Clamp01(-Vector3.Dot(p0, seg) / ss) : 0f;
            float d2 = (p0 + seg * s).sqrMagnitude;
            if (d2 <= r2 && d2 < bestD2) { bestD2 = d2; best = g; }
        }
        if (best == null) return false;
        StartHang(best);
        return true;
    }

    void StartHang(VehicleGrabPoint g)
    {
        var car = g.Vehicle;
        Quaternion rot = car.PlatformRotation;

        // How hard the catch is: relative speed between you and the car.
        Vector3 rel = new Vector3(airVel.x, verticalVelocity, airVel.z) - car.Velocity;
        float relSpeed = rel.magnitude;
        float violence = Mathf.Clamp01(relSpeed / violentCatchSpeed);
        camShake = Mathf.Max(camShake, violence);
        swing = relSpeed > 0.01f ? -(Quaternion.Inverse(rot) * rel / relSpeed) * catchSwing * violence : Vector3.zero;
        swingVel = Vector3.zero;

        LeavePlatform();
        hang = g;
        hangLocal = Quaternion.Inverse(rot) * (g.transform.position - car.PlatformPosition);
        platformYaw = rot.eulerAngles.y;
        lastCarLocalVel = Quaternion.Inverse(rot) * Flat(car.Velocity);
        sway = swayVel = Vector3.zero;
        climbProgress = 0f;
        airVel = Vector3.zero;
        verticalVelocity = 0f;
        grounded = false;
        gliding = false;
        assistActive = false;
        lastHandValid = false;
        cc.enabled = false; // held by the rack, not by collision
        UpdateHangPosition(car, rot);
    }

    void UpdateHanging(Keyboard kb, float dt)
    {
        var car = hang.Vehicle;
        if (car == null || !hang.isActiveAndEnabled || car.IsOccupied) { LetGo(car != null ? Flat(car.Velocity) : Vector3.zero); return; }

        Quaternion rot = car.PlatformRotation;
        float yaw = rot.eulerAngles.y;
        transform.Rotate(0f, Mathf.DeltaAngle(platformYaw, yaw), 0f);
        platformYaw = yaw;

        // Body swings opposite to the car's acceleration; the catch kick settles out.
        Vector3 carVel = Flat(car.Velocity);
        Vector3 swayTarget = Vector3.ClampMagnitude(-CarLocalAccel(rot, carVel, dt) * swayPerAccel, swayAmount);
        sway = Vector3.SmoothDamp(sway, swayTarget, ref swayVel, 0.25f);
        swing = Vector3.SmoothDamp(swing, Vector3.zero, ref swingVel, catchSwingSettle * 0.3f);
        UpdateHangPosition(car, rot);

        // Climb: mash Space; progress drains if you stop.
        climbProgress = Mathf.Max(0f, climbProgress - climbDecay * dt);
        if (kb.spaceKey.wasPressedThisFrame) climbProgress += 1f / Mathf.Max(1, climbPresses);
        if (climbProgress >= 0.999f) { ClimbUp(car); return; }
        if (kb.leftCtrlKey.wasPressedThisFrame || kb.rightCtrlKey.wasPressedThisFrame) LetGo(carVel);
    }

    void UpdateHangPosition(FlyingVehicle car, Quaternion rot)
    {
        Vector3 local = hangLocal + new Vector3(0f, -hangDrop, -hangBehind) + sway + swing;
        transform.position = car.PlatformPosition + rot * local;
    }

    // Onto the rear centre of the roof, already locked on.
    void ClimbUp(FlyingVehicle car)
    {
        hang = null;
        Vector3 c = car.BodyCenterLocal, half = car.BodyHalfExtents;
        Vector3 p = car.PlatformPosition + car.PlatformRotation * new Vector3(c.x, 0f, c.z - half.z + 0.6f);
        p.y = car.RoofY + 0.02f;
        transform.position = p;
        cc.enabled = true;
        MountPlatform(car);
    }

    void LetGo(Vector3 carVel)
    {
        hang = null;
        lastHandValid = false;
        cc.enabled = true;
        airVel = carVel;
        verticalVelocity = 0f;
        grounded = false;
        regrabAt = Time.time + regrabDelay;
    }

    static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }

    // Back to the start deck (Busted).
    public void Respawn(string message)
    {
        Frozen = false;
        Restrained = false;
        stunUntil = -1f;
        Heal();
        staggerUntil = -1f;
        hang = null;
        lastHandValid = false;
        gliding = false;
        airJumpsLeft = airJumps;
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
        Flash(message);
    }

    // ---------- interaction ----------

    // Aim from the camera (so it works in third person), but range is measured from the head.
    void TryHijack()
    {
        var cam = playerCamera.transform;
        float back = Vector3.Distance(cam.position, cameraRoot.position);
        var ray = new Ray(cam.position, cam.forward);
        int mask = Physics.DefaultRaycastLayers & ~playerLayerMask;
        if (!Physics.Raycast(ray, out var hit, interactRange + back, mask, QueryTriggerInteraction.Ignore)) return;
        if (Vector3.Distance(hit.point, cameraRoot.position) > interactRange) return;
        var vehicle = hit.collider.GetComponentInParent<FlyingVehicle>();
        if (vehicle != null && !vehicle.IsOccupied) TryEnter(vehicle);
    }

    // Getting into a car. Empty: straight to the wheel. With a driver, by side (car-local X):
    //   left (driver side) : yank the driver out (0.6 s) and take the wheel. Fast, but loud.
    //   right (passenger)  : sit in the passenger seat; the driver drives on (PlayerRide).
    void TryEnter(FlyingVehicle car)
    {
        if (car.Impounded) { Flash("Impounded"); return; }
        if (!car.hasDriver) { car.Enter(this); return; }
        float side = (Quaternion.Inverse(car.PlatformRotation) * (transform.position - car.transform.position)).x;
        if (side < 0f) StartCoroutine(Yank(car));
        else PlayerRide.Begin(this, car, PlayerRide.Seat.Passenger, false, false);
    }

    System.Collections.IEnumerator Yank(FlyingVehicle car)
    {
        Frozen = true;
        Flash("Yanking the driver out!");
        yield return new WaitForSeconds(0.6f);
        Frozen = false;
        if (car == null || car.IsOccupied || !isActiveAndEnabled) yield break;
        if (Vector3.Distance(car.transform.position, transform.position) > interactRange + 4f) { Flash("Too slow"); yield break; }
        PoliceDispatch.Instance?.ReportTakeover(car, true);
        car.EjectDriver(-1);
        PoliceDispatch.Instance?.OnCarTakenOver(car);
        LeavePlatform();
        car.Enter(this);
    }

    // Left a car (roof or seat): landing on a different one soon after counts as a car jump.
    public void NoteLeftCar(FlyingVehicle car)
    {
        lastLeftCar = car;
        leftCarTime = Time.time;
    }

    // Stun gun hit: down for `seconds`, no input.
    public void Stun(float seconds)
    {
        stunTime = seconds;
        stunUntil = Time.time + seconds;
        camShake = Mathf.Max(camShake, 0.4f);
    }

    public void Damage(float amount)
    {
        Health = Mathf.Max(0f, Health - amount);
        lastHurt = Time.time;
        camShake = Mathf.Max(camShake, 0.3f);
    }

    public void Heal() => Health = maxHealth;

    // Killed (an exploding car with you in it): placeholder death screen, back at the police station
    // (or the start deck without one).
    public void Die(string message)
    {
        Restrained = false; burningUntil = -1f;
        var station = PoliceStation.Find();
        if (station != null && station.door != null) { PlaceAt(station.door.position, station.door.rotation); Heal(); Flash("WASTED  -  " + message); }
        else Respawn("WASTED  -  " + message);
    }

    // Thrown by a blast: airborne with this velocity.
    public void Push(Vector3 velocity)
    {
        airVel += new Vector3(velocity.x, 0f, velocity.z);
        verticalVelocity = Mathf.Max(verticalVelocity, velocity.y + 3f);
        grounded = false;
        LeavePlatform();
    }

    public void Shake(float amount) => camShake = Mathf.Max(camShake, amount * 0.6f);

    // On fire: 12 damage a second for 6 s, or until you stand still for 1.5 s (stop, drop).
    public void Ignite()
    {
        if (Time.time < burningUntil) return;
        burningUntil = Time.time + 6f;
        stillSince = -1f;
        Flash("ON FIRE  -  stand still to put it out");
    }
    public bool OnFire => Time.time < burningUntil;
    float burningUntil = -1f, stillSince = -1f;

    void UpdateBurning(float dt, Vector3 move)
    {
        if (!OnFire) return;
        Damage(12f * dt);
        Effects.Flame(transform.position + Vector3.up * 1f, 1.2f);
        if (move.sqrMagnitude < 0.01f && grounded)
        {
            if (stillSince < 0f) stillSince = Time.time;
            else if (Time.time - stillSince > 1.5f) { burningUntil = -1f; Flash("Fire's out"); }
        }
        else stillSince = -1f;
        if (Random.value < dt * 0.5f) FireSystem.Spray(transform.position + Vector3.up, Vector3.zero, 1, 1f, 3f);
    }

    // Test hooks (ScenarioTest): set the look pitch / yaw directly.
    public void DebugLook(float pitchDeg, float? yawDeg = null)
    {
        pitch = pitchDeg;
        cameraRoot.localRotation = Quaternion.Euler(pitch, 0f, 0f);
        if (yawDeg.HasValue) transform.rotation = Quaternion.Euler(0f, yawDeg.Value, 0f);
    }

    // Moved by someone else this frame (an officer dragging you).
    public void Drag(Vector3 delta) => pendingDrag += delta;

    public void AttachCamera(Camera cam)
    {
        cam.transform.SetParent(cameraRoot, false);
        cam.transform.localPosition = Vector3.zero;
        cam.transform.localRotation = Quaternion.identity;
        pitch = 0f;
        cameraRoot.localRotation = Quaternion.identity;
    }

    public void BlockInteractThisFrame() => blockInteractFrame = Time.frameCount;

    // New stats: the body is regenerated in the background (BodyPool) and swapped in when ready.
    public bool BodyPending { get; private set; }
    public void SetSheet(CharacterSheet newSheet)
    {
        sheet = newSheet;
        if (!generatedBody) return;
        BodyPending = true;
        BodyPool.Ensure().Request(newSheet, asset =>
        {
            BodyPending = false;
            if (this == null || asset == null || !sheet.Equals(newSheet)) return;
            SwapBody(asset);
        });
    }

    void SwapBody(BodyAsset asset)
    {
        var weapon = GetComponent<PlayerWeapon>();
        weapon?.DetachGun();
        CharacterFigure.Assemble(transform, asset, CharacterFigure.Role.Player, true, true, sheet.seed);
        weapon?.ReattachGun();
        float h = Figure.Height;
        cc.height = h;
        cc.radius = 0.2f * h / CharacterFigure.DefaultHeight;
        cc.center = new Vector3(0f, h * 0.5f, 0f);
        bodyRenderers = Figure.FaceRenderers.Count > 0 ? Figure.FaceRenderers.ToArray() : new[] { Figure.HeadRenderer };
        bool shown = bodyShown;
        bodyShown = !shown;
        SetBodyVisible(shown);
    }

    public void Flash(string msg) { flash = msg; flashUntil = Time.time + 2f; }

    // Teleport (out of a car door, released at the station): off any platform, falling, keeping
    // `velocity` (horizontal) as momentum.
    public void PlaceAt(Vector3 pos, Quaternion rot, Vector3 velocity = default)
    {
        hang = null;
        cc.enabled = false;
        transform.SetPositionAndRotation(pos, Quaternion.Euler(0f, rot.eulerAngles.y, 0f));
        cc.enabled = true;
        LeavePlatform();
        lastLeftCar = null;
        airVel = Flat(velocity);
        verticalVelocity = 0f;
        grounded = false;
    }

    void OnGUI()
    {
        float cx = Screen.width / 2f, cy = Screen.height / 2f;
        if (Time.time < flashUntil) GUI.Label(new Rect(cx - 100, cy - 60, 400, 25), flash);

        // Placeholder boot-glow: a tick under the crosshair while the assist is steering you.
        if (assistActive) GUI.Label(new Rect(cx - 40, cy + 20, 200, 25), "[ BOOTS LOCK ]");

        if (hang != null)
        {
            GUI.Label(new Rect(20, Screen.height - 30, 600, 25), "HANGING   mash Space to climb | Ctrl let go");
            if (climbProgress > 0f)
            {
                var bar = new Rect(cx - 100, Screen.height - 70, 200, 10);
                var prev = GUI.color;
                GUI.color = new Color(0f, 0f, 0f, 0.6f);
                GUI.DrawTexture(bar, Texture2D.whiteTexture);
                GUI.color = new Color(0.3f, 0.9f, 1f);
                GUI.DrawTexture(new Rect(bar.x, bar.y, bar.width * Mathf.Clamp01(climbProgress), bar.height), Texture2D.whiteTexture);
                GUI.color = prev;
            }
        }
        else if (platform != null && grounded)
            GUI.Label(new Rect(20, Screen.height - 30, 600, 25), "E take car | Space jump");
    }
}
