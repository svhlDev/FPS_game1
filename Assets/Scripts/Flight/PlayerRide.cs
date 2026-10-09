using UnityEngine;
using UnityEngine.InputSystem;

// The player riding in a car someone else drives: the passenger seat of a civilian car (got in on the
// passenger side) or the back seat of a police car (arrested, hands tied). The player object is off,
// like when driving; the camera sits at the seat with free mouse look. Mouse buttons are read here
// directly (the fists live on the player object, which is off).
//   Passenger : tap E = out of the right door; hold E 1.5 s = kick the driver out of the left door and
//               slide over to the wheel (2 s in all).
//   Back seat : tied - mash the mouse buttons to slip the ties (+1/10 a press, decays 0.4/s).
//               free - tap E = out of the rear door; hold E 2 s = wrestle the driver out and take the
//               wheel; punch the driver (aim at the driver's seat) 4 times = driver down, then E takes
//               the seat. In custody the driver checks the mirror every 4-8 s.
public class PlayerRide : MonoBehaviour
{
    public enum Seat { Back, Passenger }

    public static PlayerRide Instance { get; private set; }
    public static bool Active => Instance != null && Instance.car != null;

    public float lookSensitivity = 0.1f;
    public float passengerTakeoverHold = 1.5f;
    public float slideTime = 0.5f;
    public float wrestleHold = 2f;
    public float tiePerPress = 0.1f;
    public float tieDecay = 0.4f;
    public int punchesToDown = 4;

    // Eye positions in the car's frame (FlyingVehicle's seats; the driver's is the punch target).
    static Vector3 BackSeatOf(FlyingVehicle car) => car.SeatEyeLocal(FlyingVehicle.SeatId.BackRight);
    static Vector3 PassengerSeatOf(FlyingVehicle car) => car.SeatEyeLocal(FlyingVehicle.SeatId.Passenger);
    public static Vector3 DriverSeatOf(FlyingVehicle car) => car.SeatEyeLocal(FlyingVehicle.SeatId.Driver);

    public FlyingVehicle Car => car;
    public Seat CurrentSeat { get; private set; }
    public bool Tied { get; private set; }
    public float TieProgress { get; private set; }
    public bool Custody { get; private set; }
    public bool DriverDown { get; private set; }

    FlyingVehicle car;
    FirstPersonController fpc;
    Camera cam;
    float lookYaw, lookPitch;
    bool eArmed; float eDownTime;
    int startFrame;
    float nextMirror;
    int driverHits;
    float slideUntil = -1f;
    float shake;
    string flash; float flashUntil;

    public static PlayerRide Ensure()
    {
        if (Instance == null) Instance = new GameObject("PlayerRide").AddComponent<PlayerRide>();
        return Instance;
    }

    public static void Begin(FirstPersonController who, FlyingVehicle car, Seat seat, bool tied, bool custody)
    {
        var r = Ensure();
        r.car = car;
        r.fpc = who;
        r.CurrentSeat = seat;
        r.Tied = tied;
        r.TieProgress = 0f;
        r.Custody = custody;
        r.DriverDown = false;
        r.driverHits = 0;
        r.eArmed = false;
        r.slideUntil = -1f;
        r.lookYaw = 0f; r.lookPitch = 0f;
        r.startFrame = Time.frameCount;
        r.nextMirror = Time.time + Random.Range(4f, 8f);
        r.cam = who.playerCamera;
        r.cam.transform.SetParent(null);
        who.gameObject.SetActive(false);
        r.Flash(seat == Seat.Back ? (tied ? "In custody: hands tied" : "Back seat") : "Passenger seat");
    }

    void Flash(string m) { flash = m; flashUntil = Time.time + 2.5f; }

    // Out through a door (side -1 left / +1 right, rear = back door). Falls if the car is in the air.
    void ExitDoor(int side, bool rear)
    {
        var c = car;
        Quaternion rot = c.PlatformRotation;
        float z = c.SeatHipsLocal(rear ? FlyingVehicle.SeatId.BackRight : FlyingVehicle.SeatId.Passenger).z;
        Vector3 pos = c.transform.position + rot * new Vector3(side * (c.BodyHalfExtents.x + 0.6f), -0.6f, z);
        End();
        fpc.gameObject.SetActive(true);
        fpc.PlaceAt(pos, rot, c.Velocity);
        fpc.AttachCamera(cam);
        fpc.BlockInteractThisFrame();
        fpc.NoteLeftCar(c);
        PoliceDispatch.Instance?.OnRideEnded(c, false);
    }

    // Into the driver's seat (the driver is already out).
    void TakeWheel()
    {
        var c = car;
        End();
        PoliceDispatch.Instance?.OnCarTakenOver(c);
        c.Enter(fpc);
        PoliceDispatch.Instance?.OnRideEnded(c, true);
    }

    void End() => car = null;

    // Out of the car on foot, wherever it is (the caller places the player: Busted at the station).
    public void Release()
    {
        var c = car;
        if (c == null) return;
        End();
        fpc.gameObject.SetActive(true);
        fpc.AttachCamera(cam);
        fpc.PlaceAt(c.transform.position + c.PlatformRotation * Vector3.right * (c.BodyHalfExtents.x + 1f), c.PlatformRotation);
    }

    void Update()
    {
        if (car == null) return;
        if (!car.isActiveAndEnabled) { car = null; return; }
        var kb = Keyboard.current;
        var mouse = Mouse.current;
        if (kb == null || mouse == null) return;
        float dt = Time.deltaTime;

        if (Cursor.lockState == CursorLockMode.Locked)
        {
            Vector2 d = mouse.delta.ReadValue() * lookSensitivity;
            lookYaw = Mathf.Clamp(lookYaw + d.x, -160f, 160f);
            lookPitch = Mathf.Clamp(lookPitch - d.y, -70f, 70f);
        }

        // Sliding over to the wheel after kicking the driver out.
        if (slideUntil > 0f)
        {
            if (Time.time >= slideUntil) TakeWheel();
            return;
        }

        bool punched = mouse.leftButton.wasPressedThisFrame || mouse.rightButton.wasPressedThisFrame;
        if (Time.frameCount != startFrame && kb.eKey.wasPressedThisFrame) { eArmed = true; eDownTime = Time.time; }
        float held = eArmed && kb.eKey.isPressed ? Time.time - eDownTime : 0f;

        if (CurrentSeat == Seat.Passenger)
        {
            if (eArmed && kb.eKey.wasReleasedThisFrame) { eArmed = false; ExitDoor(1, false); return; }
            if (held >= passengerTakeoverHold)
            {
                eArmed = false;
                if (car.hasDriver)
                {
                    PoliceDispatch.Instance?.ReportTakeover(car, false);
                    car.EjectDriver(-1);
                }
                slideUntil = Time.time + slideTime;
                Flash("Taking the wheel");
            }
            return;
        }

        // Back seat.
        if (Tied)
        {
            if (punched) TieProgress += tiePerPress;
            TieProgress = Mathf.Max(0f, TieProgress - tieDecay * dt);
            if (TieProgress >= 1f) { Tied = false; TieProgress = 0f; Flash("Hands free"); }
            eArmed = false;
            return;
        }

        if (Custody && !DriverDown && Time.time >= nextMirror)
        {
            nextMirror = Time.time + Random.Range(4f, 8f);
            if (PoliceDispatch.Instance != null && PoliceDispatch.Instance.OnMirrorCheck(car))
            {
                Tied = true;
                eArmed = false;
                Flash("He saw you. Tied up again.");
                return;
            }
        }

        if (DriverDown)
        {
            if (eArmed && kb.eKey.wasReleasedThisFrame)
            {
                eArmed = false;
                car.EjectDriver(-1, true); // the slumped driver goes out of his door
                TakeWheel();
            }
            return;
        }

        if (eArmed && kb.eKey.wasReleasedThisFrame) { eArmed = false; ExitDoor(1, true); return; }
        if (held >= wrestleHold)
        {
            eArmed = false;
            PoliceDispatch.Instance?.OnDriverAssaulted(car);
            car.EjectDriver(-1);
            TakeWheel();
            return;
        }

        // Punch the driver: the aim has to pass close to the driver's seat.
        if (punched && car.hasDriver)
        {
            Vector3 seat = car.transform.TransformPoint(DriverSeatOf(car));
            Ray ray = new Ray(cam.transform.position, cam.transform.forward);
            Vector3 toSeat = seat - ray.origin;
            float along = Vector3.Dot(toSeat, ray.direction);
            if (along > 0f && along < 2.5f && (toSeat - ray.direction * along).magnitude < 0.5f)
            {
                driverHits++;
                shake = 0.15f;
                PoliceDispatch.Instance?.OnDriverAssaulted(car);
                if (driverHits >= punchesToDown)
                {
                    DriverDown = true;
                    PoliceDispatch.Instance?.OnDriverDown(car);
                    Flash("Driver down  -  E take the wheel");
                }
            }
        }
    }

    void LateUpdate()
    {
        if (car == null || cam == null) return;
        Vector3 seat = CurrentSeat == Seat.Back ? BackSeatOf(car) : PassengerSeatOf(car);
        Vector3 pos = car.transform.position + car.PlatformRotation * seat;
        if (shake > 0f)
        {
            pos += Random.insideUnitSphere * shake;
            shake = Mathf.MoveTowards(shake, 0f, Time.deltaTime);
        }
        cam.transform.SetPositionAndRotation(pos, car.PlatformRotation * Quaternion.Euler(lookPitch, lookYaw, 0f));
    }

    void OnGUI()
    {
        if (car == null) return;
        float cx = Screen.width / 2f, y = Screen.height - 60;
        if (Time.time < flashUntil) GUI.Label(new Rect(cx - 150, Screen.height * 0.35f, 400, 25), flash);
        string hint;
        if (slideUntil > 0f) hint = "Sliding over...";
        else if (CurrentSeat == Seat.Passenger) hint = "PASSENGER   E exit | hold E take over";
        else if (Tied) hint = "TIED   mash the mouse buttons to slip the ties";
        else if (DriverDown) hint = "E take the wheel";
        else hint = "BACK SEAT   E rear door | hold E wrestle the driver | punch the driver (aim at his seat)";
        GUI.Label(new Rect(20, y, 900, 25), hint);

        float bar = Tied ? TieProgress : 0f;
        if (eArmed && Keyboard.current != null && Keyboard.current.eKey.isPressed)
            bar = Mathf.Clamp01((Time.time - eDownTime) / (CurrentSeat == Seat.Passenger ? passengerTakeoverHold : wrestleHold));
        if (bar > 0f)
        {
            var prev = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.6f);
            GUI.DrawTexture(new Rect(cx - 100, y - 20, 200, 8), Texture2D.whiteTexture);
            GUI.color = Tied ? new Color(1f, 0.6f, 0.2f) : new Color(0.3f, 0.9f, 1f);
            GUI.DrawTexture(new Rect(cx - 100, y - 20, 200 * bar, 8), Texture2D.whiteTexture);
            GUI.color = prev;
        }
        if (!Tied && CurrentSeat == Seat.Back && !DriverDown)
            GUI.Label(new Rect(cx - 5, Screen.height / 2f - 10, 20, 20), "+");
    }
}
