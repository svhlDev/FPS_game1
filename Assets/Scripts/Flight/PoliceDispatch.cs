using System.Collections.Generic;
using UnityEngine;

// Police brain, one per scene (created on demand). Police don't care about traffic rules; they care
// about reckless crashing, carjacking in front of them, roof-riding and car-jumping:
//   Trigger  : the player's car crashes at crashTrigger m/s or more while a police car within
//              triggerRange can see it. Wanted level 1, that car pursues.
//   Resist   : not stopping for ignoreStopTime s, ramming a police car at ramPoliceSpeed, hitting a
//              civilian car while wanted. Each raises the level (max 3).
//   Units    : up to 1 / 3 / 5 cars per level; missing ones are recruited from patrols or spawned on a
//              lane 300 to 500 m away, out of view. Chasing units claim distinct approach slots (two
//              beside the player on its layer, the rest behind/above on the layers next to it).
//   Contact  : pursuing cars ram the player's car. The meter fills while the last touch was under
//              contactGrace s ago and drains once it's longer. empContactTime s of it = EMP.
//   Disabled : the car is always towed down to the street (two docked units with the player inside,
//              one if it's empty); the others box it in. On the street: two officers come to the door
//              (3 s arrest, then the player is pulled out and dragged); an empty car is impounded.
//   On foot  : officers (OfficerAgent) ride in on scooters, dismount and run to the player.
//              Approach -> Cuffing (2.5 s; stepping 1.5 m away interrupts, the 2nd time brings out
//              stun guns) -> Dragging (to the nearest police car at 2 m/s; mash the mouse buttons to
//              break free) -> Loaded: back seat, hands tied, driven to the police station (PlayerRide).
//   Force    : None -> NonLethal (stun guns: 2 s stun, then cuffing) -> Lethal (car gunfire, sidearms
//              on foot). Punching an officer, or a carjacking a police officer sees, is Lethal at once.
//   Station  : arriving = Busted: fine, the stolen car is lost, released at the station door, wanted 0.
//   Roof     : riding a moving car's roof in sight of police: 5 s to get inside or off, then arrest
//              (a police car pulls alongside and an officer steps onto the roof). Seen jumping from
//              one car to another: arrest at once.
//   Losing   : decayTime s with no unit or officer seeing the player lowers the level one step.
[DefaultExecutionOrder(-20)]
public class PoliceDispatch : MonoBehaviour
{
    public static PoliceDispatch Instance { get; private set; }

    public enum Force { None, NonLethal, Lethal }
    enum Arrest { None, Cuffing, Dragging }
    enum Trip { Climb, Cruise, Descend }

    [Header("Trigger and wanted level")]
    public float crashTrigger = 25f;
    public float triggerRange = 150f;
    public float ignoreStopTime = 10f;
    [Tooltip("Above this speed the player counts as not stopping.")]
    public float stopSpeed = 5f;
    public float ramPoliceSpeed = 20f;
    [Tooltip("Impact speed that counts as hitting a civilian car while wanted.")]
    public float civilianHitSpeed = 10f;
    [Tooltip("One resist action can only raise the level once in this many seconds.")]
    public float resistCooldown = 1.5f;
    public int[] unitCap = { 0, 1, 3, 5 };
    public float spawnMinDistance = 300f;
    public float spawnMaxDistance = 500f;
    public float sightRange = 200f;
    public float decayTime = 20f;

    [Header("Contact / EMP")]
    public float contactFill = 1f;
    public float contactDrain = 1f;
    [Tooltip("The meter keeps filling while the last touch was less than this long ago.")]
    public float contactGrace = 2f;
    public float empContactTime = 5f;

    [Header("Box-in and tow")]
    public int maxTowUnits = 2;
    [Tooltip("Box-in units hold this far ahead / behind the car (centre to centre; a car is 4 m long).")]
    public float boxDistance = 5.5f;
    [Tooltip("Once the car is down, the docked units move out this far sideways to park alongside.")]
    public float parkAlongside = 3.5f;
    [Tooltip("Gap between the towed car and a docked unit (m), beyond their half widths.")]
    public float towGap = 0.6f;
    [Tooltip("A docker this close to its slot attaches (blending in over 0.4 s).")]
    public float attachRange = 2.5f;
    [Tooltip("Tow not started this long after the car went down: it descends anyway.")]
    public float tractorFallback = 5f;
    public float arrestTime = 3f;

    [Header("Apprehension")]
    public float cuffTime = 2.5f;
    public float cuffRange = 1.1f;
    [Tooltip("Moving this far from the cuffing officer interrupts the cuffing.")]
    public float resistDistance = 1.5f;
    public float dragSpeed = 2f;
    public float breakPerPress = 1f / 8f;
    public float breakDecay = 0.4f;

    [Header("Force")]
    public float stunRange = 12f;
    public float stunInterval = 2.5f;
    public float stunTime = 2f;
    public float sidearmRange = 30f;
    public int sidearmBurst = 3;
    public float sidearmBurstInterval = 1.5f;
    public float sidearmShotInterval = 0.15f;
    public float sidearmDamage = 10f;

    [Header("Witnesses and roof-riding")]
    public float yankSightRange = 80f;
    public float passengerSightRange = 25f;
    public float passengerSightAngle = 30f;
    public float roofSightRange = 60f;
    public float roofComplyTime = 5f;
    [Tooltip("Chance the transport driver notices untied hands at a mirror check.")]
    public float mirrorNoticeChance = 0.7f;

    [Header("Busted")]
    public int bustedFine = 500;

    public int WantedLevel { get; private set; }
    public Force ForceLevel { get; private set; }
    public float Contact { get; private set; }
    // Seconds of grace left before the contact meter starts draining.
    public float ContactGraceLeft => Mathf.Max(0f, contactGrace - (Time.time - lastTouchTime));
    public IReadOnlyList<PoliceDriver> Pursuing => pursuing;
    public static IReadOnlyList<PoliceDriver> Units => units;

    static readonly List<PoliceDriver> units = new List<PoliceDriver>();
    readonly List<PoliceDriver> pursuing = new List<PoliceDriver>();
    readonly List<PoliceDriver> dockers = new List<PoliceDriver>();
    readonly List<PoliceDriver> chasers = new List<PoliceDriver>();
    readonly List<OfficerAgent> officers = new List<OfficerAgent>();
    readonly Dictionary<PoliceDriver, int> chaseSlot = new Dictionary<PoliceDriver, int>();
    LanePath[] lanes;
    float lastTouchTime = -100f;
    float unseenTime, ignoreTime, lastResist = -100f, nextSpawn, nextOfficer;
    FlyingVehicle towCar;   // disabled car being towed (or impounded), player in it or not
    bool towing;
    StopCone cone;
    float downSince = -1f;
    float arrestTimer;
    bool pullingOut;
    FlyingVehicle lastPlayerCar;
    int preferredSide = 1;
    float lastTargetYaw; FlyingVehicle yawTarget;

    // Apprehension.
    Arrest arrest;
    OfficerAgent cuffer;
    float cuffTimer, dragBreak;
    int cuffInterrupts;
    PoliceDriver dragUnit;

    // Transport.
    bool transporting;
    PoliceDriver transportUnit;
    Trip trip;
    float cruiseY, transportHaltUntil;
    bool[] laneLayers;

    // Roof-riding.
    bool callout; float calloutUntil;
    FlyingVehicle roofCar;
    PoliceDriver roofUnit;
    float roofAlongside;
    int roofSide = 1;

    string message; float messageUntil;
    float stunFlashUntil;
    Transform spark; float nextSpark;

    static readonly RaycastHit[] losBuf = new RaycastHit[16];

    public static PoliceDispatch Ensure()
    {
        if (Instance == null) Instance = new GameObject("PoliceDispatch").AddComponent<PoliceDispatch>();
        return Instance;
    }

    public static void Register(PoliceDriver d) { if (!units.Contains(d)) units.Add(d); }
    public static void Unregister(PoliceDriver d) => units.Remove(d);

    void Awake() => Instance = this;

    void OnEnable()
    {
        PlayerFists.PunchHit += OnPunchHit;
        PlayerFists.PunchPressed += OnPunchPressed;
        PlayerWeapon.ShotHit += OnShotHit;
        PlayerWeapon.ShotFired += OnShotFired;
        FirstPersonController.CarJump += OnCarJump;
    }

    void OnDisable()
    {
        PlayerFists.PunchHit -= OnPunchHit;
        PlayerFists.PunchPressed -= OnPunchPressed;
        PlayerWeapon.ShotHit -= OnShotHit;
        PlayerWeapon.ShotFired -= OnShotFired;
        FirstPersonController.CarJump -= OnCarJump;
    }

    void OnDestroy() { if (Instance == this) Instance = null; }

    // Line of sight against static geometry only (cars and people don't block it).
    public static bool LineOfSight(Vector3 a, Vector3 b)
    {
        Vector3 d = b - a;
        float len = d.magnitude;
        if (len < 0.01f) return true;
        int n = Physics.RaycastNonAlloc(a, d / len, losBuf, len, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < n; i++)
        {
            var c = losBuf[i].collider;
            if (c.attachedRigidbody != null || c.GetComponentInParent<FlyingVehicle>() != null ||
                c.GetComponentInParent<CharacterController>() != null) continue;
            return false;
        }
        return true;
    }

    // A police car (any, patrolling or not) or officer that can see `p` within `range`; null if none.
    // `seenBy` is the car to put on the case (the officer's unit for an officer).
    bool PoliceSees(Vector3 p, float range, out PoliceDriver seenBy)
    {
        float r2 = range * range;
        foreach (var o in officers)
        {
            if (o == null) continue;
            Vector3 eye = o.transform.position + Vector3.up * 1.6f;
            if ((eye - p).sqrMagnitude <= r2 && LineOfSight(eye, p)) { seenBy = o.Home; return true; }
        }
        foreach (var u in units)
        {
            if (u == null || !u.isActiveAndEnabled) continue;
            Vector3 eye = u.transform.position + Vector3.up;
            if ((eye - p).sqrMagnitude <= r2 && LineOfSight(eye, p)) { seenBy = u; return true; }
        }
        seenBy = null;
        return false;
    }

    // ---------- combat events (officers killed, grenades, trunk theft) ----------

    // An officer died: off the pursuit (and off the player, if it had hands on them). Killed by the
    // player in sight of police (or with force already out), force stays / goes Lethal.
    public void OnOfficerDown(OfficerAgent o, DamageInfo d)
    {
        officers.Remove(o);
        if (cuffer == o)
        {
            var fpc = FirstPersonController.Instance;
            if (fpc != null && arrest != Arrest.None) ReleasePlayer(fpc);
            cuffer = null;
        }
        OfficersKilled++;
        if (!d.byPlayer) return;
        Vector3 p = o.transform.position + Vector3.up;
        if (PoliceSees(p, triggerRange, out var by) || ForceLevel != Force.None)
        {
            EnsureWanted(2, by != null ? by : o.Home);
            Escalate(Force.Lethal, "Officer down");
        }
    }
    public static int OfficersKilled;

    // A grenade thrown: landing near police (an officer or a police car within nearPolice m), or the
    // throw seen, is lethal force.
    public void ReportGrenadeThrown(Vector3 from, Vector3 landing, float nearPolice = 12f)
    {
        bool near = false;
        float r2 = nearPolice * nearPolice;
        foreach (var o in OfficerAgent.All) if (o != null && (o.transform.position - landing).sqrMagnitude < r2) { near = true; break; }
        if (!near) foreach (var u in units) if (u != null && u.isActiveAndEnabled && (u.transform.position - landing).sqrMagnitude < r2) { near = true; break; }
        if (!near && !PoliceSees(from + Vector3.up, triggerRange, out _)) return;
        EnsureWanted(2, null);
        Escalate(Force.Lethal, "Grenade!");
    }

    // An explosion the player caused, in sight of police: wanted 2 and lethal force.
    public void ReportExplosion(Vector3 at)
    {
        if (!PoliceSees(at + Vector3.up, triggerRange, out var by)) return;
        EnsureWanted(2, by);
        Escalate(Force.Lethal, "Explosion");
    }

    // Opened a police car's trunk: in sight of an officer, wanted 1 (theft).
    public void ReportTrunkTheft(Vector3 at)
    {
        if (!PoliceSees(at, triggerRange, out var by)) return;
        EnsureWanted(1, by);
        Show("POLICE: Theft from a patrol car");
    }

    public bool Sees(Vector3 p, float range) => PoliceSees(p, range, out _);

    // ---------- events from cars, the player and the ride ----------

    // Player's car hit something static.
    public void OnPlayerImpact(FlyingVehicle car, float impact)
    {
        if (impact >= crashTrigger) TryTrigger(car);
    }

    // `mover` touched `other` this frame (one of them is the player's car).
    public void OnCarContact(FlyingVehicle mover, FlyingVehicle other, float impact)
    {
        var player = FlyingVehicle.Driven;
        var cop = mover == player ? other.GetComponent<PoliceDriver>() : mover.GetComponent<PoliceDriver>();
        if (cop != null && cop.IsPursuing)
        {
            lastTouchTime = Time.time;
            cop.NotifyContact();
        }

        if (WantedLevel == 0)
        {
            if (impact >= crashTrigger) TryTrigger(player);
            return;
        }
        if (mover != player) return; // only what the player drives into counts as resisting
        if (cop != null && impact >= ramPoliceSpeed) Resist("Rammed police");
        else if (cop == null && impact >= civilianHitSpeed) Resist("Hit a civilian");
    }

    public void OnRestarted(FlyingVehicle car)
    {
        Contact = 0f;
        lastTouchTime = -100f;
        ReleaseTow();
        ReturnOfficers();
        arrestTimer = 0f;
    }

    // Leaving a disabled car doesn't stop the tow: it goes down to the street empty.
    public void OnPlayerExited(FlyingVehicle car)
    {
        if (pullingOut) return;
        arrestTimer = 0f;
        Contact = 0f;
    }

    // Somebody took the wheel of `car` from its driver (yank, passenger takeover, back-seat wrestle).
    // A police car stops being one.
    public void OnCarTakenOver(FlyingVehicle car)
    {
        var pd = car.GetComponent<PoliceDriver>();
        if (pd == null) return;
        pursuing.Remove(pd);
        if (dockers.Remove(pd)) UndockUnit(pd);
        chaseSlot.Remove(pd);
        if (pd == transportUnit) { transporting = false; transportUnit = null; }
        if (pd == dragUnit) dragUnit = null;
        if (pd == roofUnit) roofUnit = null;
        car.ReportContacts = false;
        Destroy(pd);
        Escalate(Force.Lethal, "Police car stolen");
    }

    // The player's ride ended: out of a door (tookWheel false) or into the driver's seat.
    public void OnRideEnded(FlyingVehicle car, bool tookWheel)
    {
        if (!transporting || transportUnit == null || car != transportUnit.Car) return;
        transporting = false;
        if (!tookWheel)
        {
            Show("Escaped custody!");
            Escalate(Force.NonLethal, null);
        }
    }

    // Transport driver's mirror check (called by PlayerRide while the player's hands are free).
    public bool OnMirrorCheck(FlyingVehicle car)
    {
        if (!transporting || transportUnit == null || car != transportUnit.Car) return false;
        if (Random.value > mirrorNoticeChance) return false;
        transportHaltUntil = Time.time + 2f; // pulls over and ties you up again
        Show("The officer noticed you!");
        return true;
    }

    public void OnDriverAssaulted(FlyingVehicle car) => Escalate(Force.Lethal, "Assaulted an officer");

    public void OnDriverDown(FlyingVehicle car) => Show("Driver down");

    // Carjacking: seen by an officer or police car? Driver side (a yank) is seen from yankSightRange
    // at any angle; a passenger-side takeover only close up and nearly broadside. A police car's own
    // driver always sees it.
    public void ReportTakeover(FlyingVehicle car, bool driverSide)
    {
        PoliceDriver witness = car.GetComponent<PoliceDriver>();
        bool seen = witness != null;
        Vector3 p = car.transform.position;
        Vector3 right = car.PlatformRotation * Vector3.right;
        float range = driverSide ? yankSightRange : passengerSightRange;
        float cosMax = Mathf.Cos(passengerSightAngle * Mathf.Deg2Rad);

        bool Sees(Vector3 eye)
        {
            Vector3 to = eye - p;
            if (to.sqrMagnitude > range * range || !LineOfSight(eye, p)) return false;
            if (driverSide) return true;
            to.y = 0f;
            return to.sqrMagnitude > 0.01f && Mathf.Abs(Vector3.Dot(to.normalized, right)) >= cosMax;
        }

        if (!seen)
            foreach (var o in officers)
                if (o != null && Sees(o.transform.position + Vector3.up * 1.6f)) { seen = true; witness = o.Home; break; }
        if (!seen)
            foreach (var u in units)
                if (u != null && u.isActiveAndEnabled && u.Car != car && Sees(u.transform.position + Vector3.up)) { seen = true; witness = u; break; }
        if (!seen) return;

        EnsureWanted(2, witness);
        Escalate(Force.Lethal, "Carjacking witnessed");
    }

    void OnPunchHit(Collider c)
    {
        var o = c.GetComponentInParent<OfficerAgent>();
        if (o == null) return;
        Vector3 away = o.transform.position - FirstPersonController.Instance.transform.position;
        away.y = 0f;
        o.Stagger(0.8f, away.normalized * 1f);
        EnsureWanted(2, o.Home);
        Escalate(Force.Lethal, "Assaulted an officer");
    }

    // T-gun hits. An officer or a police car in either mode: lethal force. A civilian in sight of
    // police: stunned -> wanted 1, shot -> wanted 2.
    void OnShotHit(Collider c, Vector3 point, float damage, Weapon.Mode mode)
    {
        var o = c.GetComponentInParent<OfficerAgent>();
        if (o != null)
        {
            if (mode == Weapon.Mode.Lethal)
            {
                Vector3 away = o.transform.position - FirstPersonController.Instance.transform.position;
                away.y = 0f;
                o.Stagger(1.2f, away.normalized * 1.5f);
            }
            EnsureWanted(2, o.Home);
            Escalate(Force.Lethal, mode == Weapon.Mode.Stun ? "Stunned an officer" : "Shot an officer");
            return;
        }
        var unit = c.GetComponentInParent<PoliceDriver>();
        if (unit != null) { EnsureWanted(2, unit); Escalate(Force.Lethal, "Shot at police"); return; }
        bool civilian = PedestrianSystem.IsPedestrian(c) || c.GetComponentInParent<NpcBody>() != null;
        if (civilian && PoliceSees(point + Vector3.up, triggerRange, out var by))
        {
            EnsureWanted(mode == Weapon.Mode.Stun ? 1 : 2, by);
            Show(mode == Weapon.Mode.Stun ? "POLICE: Assault on a civilian" : "POLICE: Civilian shot!");
        }
    }

    // Gunfire in sight of police: stun -> wanted 1 and stun guns; lethal -> wanted 2 and lethal force.
    void OnShotFired(Vector3 muzzle, Weapon.Mode mode)
    {
        if (!PoliceSees(muzzle, triggerRange, out var by)) return;
        EnsureWanted(mode == Weapon.Mode.Stun ? 1 : 2, by);
        if (mode == Weapon.Mode.Stun) Escalate(Force.NonLethal, "Stun shots fired", 1);
        else Escalate(Force.Lethal, "Shots fired");
    }

    // Brandishing shows the mode: a lethal-mode gun drawn in sight of police brings out the stun guns.
    void CheckBrandishing()
    {
        if (Time.time < nextBrandishCheck) return;
        nextBrandishCheck = Time.time + 0.25f;
        var fpc = FirstPersonController.Instance;
        if (fpc == null || !fpc.isActiveAndEnabled) return;
        var w = fpc.GetComponent<PlayerWeapon>();
        if (w == null || !w.Brandishing || w.Mode != Weapon.Mode.Lethal || ForceLevel >= Force.NonLethal) return;
        Vector3 p = fpc.transform.position + Vector3.up * 1.2f;
        bool seen = PoliceSees(p, triggerRange, out var by);
        if (!seen)
            foreach (var o in officers)
                if (o != null && (o.transform.position - p).sqrMagnitude < 40f * 40f && LineOfSight(o.transform.position + Vector3.up * 1.4f, p)) { seen = true; break; }
        if (!seen) return;
        EnsureWanted(1, by);
        Escalate(Force.NonLethal, "Armed suspect", 1);
    }
    float nextBrandishCheck;

    void OnPunchPressed(int hand)
    {
        if (arrest == Arrest.Dragging) dragBreak += breakPerPress;
    }

    // Left one car and landed on another in sight of police: arrest at once.
    void OnCarJump(FlyingVehicle from, FlyingVehicle to)
    {
        var fpc = FirstPersonController.Instance;
        if (fpc == null) return;
        if (!PoliceSees(fpc.transform.position + Vector3.up, roofSightRange, out var by) &&
            !(from != null && PoliceSees(from.transform.position + Vector3.up, roofSightRange, out by))) return;
        EnsureWanted(1, by);
        Show("POLICE: Car surfing! You're under arrest");
        if (to.Velocity.magnitude > 3f) StartRoofArrest(to);
    }

    // ---------- wanted level and force ----------

    void TryTrigger(FlyingVehicle car)
    {
        if (car == null || WantedLevel > 0) return;
        Vector3 p = car.transform.position;
        foreach (var u in units)
        {
            if (u == null) continue;
            Vector3 eye = u.transform.position + Vector3.up;
            if ((eye - p).sqrMagnitude > triggerRange * triggerRange || !LineOfSight(eye, p)) continue;
            EnsureWanted(1, u);
            Show("RECKLESS FLYING  -  POLICE PURSUIT");
            return;
        }
    }

    // Wanted at least `level`; starts the pursuit with `by` (or whoever is nearest) if there is none.
    void EnsureWanted(int level, PoliceDriver by)
    {
        if (WantedLevel == 0)
        {
            unseenTime = 0f; ignoreTime = 0f;
            if (by == null)
            {
                Vector3 p = PlayerPosition();
                float best = float.MaxValue;
                foreach (var u in units)
                    if (u != null && u.isActiveAndEnabled && (u.transform.position - p).sqrMagnitude < best)
                    { best = (u.transform.position - p).sqrMagnitude; by = u; }
            }
            if (by != null) StartPursuit(by);
        }
        WantedLevel = Mathf.Clamp(Mathf.Max(WantedLevel, level), 0, 3);
    }

    // Test hook (ScenarioTest): wanted at `level` with the nearest unit on the case.
    public void DebugStartPursuit(int level) => EnsureWanted(level, null);
    public void DebugForce(Force f) => Escalate(f, "test");
    public void DebugClear() => EndPursuit();

    void Resist(string why)
    {
        if (WantedLevel <= 0 || WantedLevel >= 3 || Time.time - lastResist < resistCooldown) return;
        lastResist = Time.time;
        WantedLevel++;
        Show($"{why}  -  WANTED {WantedLevel}");
    }

    // Raise the force level (never lowers). Force means wanted `level` or more (2 unless a stun-only
    // incident: stun shots seen, an armed suspect).
    void Escalate(Force f, string why, int level = 2)
    {
        EnsureWanted(level, null);
        if (f <= ForceLevel) return;
        ForceLevel = f;
        Show((why != null ? why + "  -  " : "") + (f == Force.Lethal ? "POLICE: LETHAL FORCE" : "POLICE: stun guns out"));
    }

    void StartPursuit(PoliceDriver u)
    {
        if (u == null || pursuing.Contains(u)) return;
        u.Pursue();
        pursuing.Add(u);
    }

    void EndPursuit()
    {
        WantedLevel = 0;
        ForceLevel = Force.None;
        foreach (var u in pursuing) if (u != null) u.Patrol();
        pursuing.Clear();
        chaseSlot.Clear();
        ReleaseTow();
        ReturnOfficers();
        Contact = 0f;
        arrestTimer = 0f;
        unseenTime = 0f; ignoreTime = 0f;
        cuffInterrupts = 0;
        arrest = Arrest.None;
        cuffer = null; dragUnit = null;
        transporting = false; transportUnit = null;
        roofCar = null; roofUnit = null; callout = false;
        var fpc = FirstPersonController.Instance;
        if (fpc != null) fpc.Restrained = false;
    }

    Vector3 PlayerPosition()
    {
        var car = FlyingVehicle.Driven;
        if (car != null) return car.transform.position;
        if (PlayerRide.Active) return PlayerRide.Instance.Car.transform.position;
        var fpc = FirstPersonController.Instance;
        return fpc != null ? fpc.transform.position + Vector3.up : Vector3.zero;
    }

    // ---------- main loop (before the police drivers, the cars and the officers move) ----------

    void Update()
    {
        float dt = Time.deltaTime;
        pursuing.RemoveAll(u => u == null || !u.isActiveAndEnabled);
        for (int i = dockers.Count - 1; i >= 0; i--)
            if (dockers[i] == null || !pursuing.Contains(dockers[i])) { UndockUnit(dockers[i]); dockers.RemoveAt(i); }
        officers.RemoveAll(o => o == null || o.State == OfficerAgent.Phase.Return);
        CheckBrandishing();
        foreach (var o in officers)
        {
            o.ActionPose = o != cuffer ? FigureAnimator.Pose.Normal
                         : arrest == Arrest.Cuffing ? FigureAnimator.Pose.Cuffing
                         : arrest == Arrest.Dragging ? FigureAnimator.Pose.Dragging : FigureAnimator.Pose.Normal;
            o.Armed = ForceLevel != Force.None;
        }

        var car = FlyingVehicle.Driven;
        var fpc = FirstPersonController.Instance;
        bool riding = PlayerRide.Active;
        bool onFoot = fpc != null && fpc.isActiveAndEnabled;
        if (car != null) lastPlayerCar = car;
        if (towCar == null ? dockers.Count > 0 : !towCar.Disabled) ReleaseTow(); // restarted, or gone

        UpdateSparks(car);
        UpdateRoofRiding(onFoot ? fpc : null);
        if (WantedLevel == 0) { Contact = 0f; return; }

        if (transporting) { UpdateTransport(); return; }
        if (car != null && car.Disabled) towCar = car;

        Vector3 playerPos = PlayerPosition();

        // An empty car being towed ties up its towing unit: staff one more.
        StaffUnits(playerPos, towCar != null && towCar != car ? 1 : 0);
        if (pursuing.Count == 0) { EndPursuit(); return; }

        // Seen? Otherwise the level decays.
        bool seen = arrest != Arrest.None || PoliceSees(playerPos, sightRange, out _);
        unseenTime = seen ? 0f : unseenTime + dt;
        if (unseenTime >= decayTime)
        {
            unseenTime = 0f;
            WantedLevel--;
            if (WantedLevel == 0) { EndPursuit(); Show("You lost them"); return; }
            Show($"Wanted level {WantedLevel}");
        }

        // The tow carries on wherever the player is.
        if (towCar != null) UpdateTow(towCar, towCar == car);
        chasers.Clear();
        foreach (var u in pursuing) if (!dockers.Contains(u)) chasers.Add(u);

        if (car != null && !car.Disabled)
        {
            // In a (new) car: officers go back to their units.
            ReturnOfficers();
            arrest = Arrest.None;

            ignoreTime = car.Velocity.magnitude > stopSpeed ? ignoreTime + dt : 0f;
            if (ignoreTime >= ignoreStopTime) { ignoreTime = 0f; Resist("Failed to stop"); }

            // Contact meter: fills while the last touch is within the grace time, drains after.
            bool inGrace = Time.time - lastTouchTime < contactGrace;
            Contact = Mathf.Max(0f, Contact + (inGrace ? contactFill : -contactDrain) * dt);
            if (Contact >= empContactTime)
            {
                Contact = 0f;
                lastTouchTime = -100f;
                car.Disable(false);
                towCar = car;
                Show("EMP!");
                return;
            }
            AssignChaseSlots(car);
            foreach (var u in chasers) u.Chase(car);
            foreach (var u in chasers) { if (ForceLevel == Force.Lethal) u.UpdateLaser(car); else u.StopLaser(); }
            return;
        }

        Contact = 0f;
        if (car != null) { UpdateBoxAndArrest(car, dt); return; }
        if (riding)
        {
            ReturnOfficers();
            foreach (var u in chasers) u.HoverNear(PlayerRide.Instance.Car.transform.position);
            return;
        }
        if (onFoot) UpdateOnFoot(fpc, dt);
    }

    // Keep the unit count at the level's cap: release extras, recruit patrols, spawn the rest.
    void StaffUnits(Vector3 playerPos, int extra)
    {
        int cap = unitCap[Mathf.Clamp(WantedLevel, 0, unitCap.Length - 1)] + extra;
        while (pursuing.Count > cap)
        {
            int far = -1;
            for (int i = 0; i < pursuing.Count; i++)
            {
                var u = pursuing[i];
                if (dockers.Contains(u) || u == dragUnit || u == roofUnit) continue;
                if (far < 0 || (u.transform.position - playerPos).sqrMagnitude > (pursuing[far].transform.position - playerPos).sqrMagnitude) far = i;
            }
            if (far < 0) break;
            chaseSlot.Remove(pursuing[far]);
            pursuing[far].Patrol();
            pursuing.RemoveAt(far);
        }
        if (pursuing.Count >= cap || Time.time < nextSpawn) return;
        nextSpawn = Time.time + 0.5f;

        PoliceDriver best = null;
        float bestSq = spawnMaxDistance * spawnMaxDistance;
        foreach (var u in units)
        {
            if (u == null || u.IsPursuing || !u.isActiveAndEnabled) continue;
            float sq = (u.transform.position - playerPos).sqrMagnitude;
            if (sq < bestSq) { bestSq = sq; best = u; }
        }
        if (best == null) best = SpawnUnit(playerPos);
        if (best != null) StartPursuit(best);
    }

    // A copy of any police car, on a lane point 300 to 500 m from the player that the camera can't see.
    PoliceDriver SpawnUnit(Vector3 playerPos)
    {
        PoliceDriver template = null;
        foreach (var u in units) if (u != null) { template = u; break; }
        if (template == null) return null;
        if (lanes == null) lanes = FindObjectsByType<LanePath>(FindObjectsSortMode.None);
        if (lanes.Length == 0) return null;
        var cam = Camera.main;

        for (int attempt = 0; attempt < 40; attempt++)
        {
            var lane = lanes[Random.Range(0, lanes.Length)];
            if (lane == null || lane.Length <= 0f) continue;
            float d = Random.Range(0f, lane.Length);
            lane.Sample(d, out Vector3 pos, out Vector3 fwd);
            float dist = Vector3.Distance(pos, playerPos);
            if (dist < spawnMinDistance || dist > spawnMaxDistance) continue;
            if (cam != null)
            {
                Vector3 v = cam.WorldToViewportPoint(pos);
                if (v.z > 0f && v.x > -0.1f && v.x < 1.1f && v.y > -0.1f && v.y < 1.1f) continue;
            }
            var t = template.Car;
            pos.y += t.BodyHalfExtents.y - t.BodyCenterLocal.y; // lane heights are underside heights
            fwd.y = 0f;
            var go = Instantiate(template.gameObject, pos, Quaternion.LookRotation(fwd.sqrMagnitude > 1e-4f ? fwd : Vector3.forward),
                                 template.transform.parent);
            go.name = template.name + " (dispatched)";
            var fv = go.GetComponent<FlyingVehicle>();
            fv.path = lane;
            fv.startDistance = d;
            fv.startLevel = 0;
            fv.hasDriver = true;
            fv.driverKind = FlyingVehicle.DriverKind.Officer;
            return go.GetComponent<PoliceDriver>();
        }
        return null;
    }

    // ---------- chase slots ----------

    // Distinct approach slots per chasing unit (in the target's yaw frame, side relative to the side
    // the target isn't turning toward). At most two units on the target's own layer; they're the ones
    // that ram. The rest hold the layers above / below.
    static readonly (float x, float z, int layer, bool ram)[] ChaseSlots =
    {
        (4f, -5f, 0, true),     // preferred side
        (-4f, -5f, 0, true),    // other side
        (0f, -7f, 1, false),    // behind, a layer up
        (0f, -7f, -1, false),   // behind, a layer down
        (4f, 3f, 1, false),     // above, alongside
    };

    void AssignChaseSlots(FlyingVehicle target)
    {
        // Which way is the target turning? Come in on the other side.
        float yaw = target.Yaw;
        if (yawTarget == target && Time.deltaTime > 0f)
        {
            float turn = Mathf.DeltaAngle(lastTargetYaw, yaw) / Time.deltaTime;
            if (Mathf.Abs(turn) > 10f) preferredSide = turn > 0f ? -1 : 1;
        }
        yawTarget = target; lastTargetYaw = yaw;

        var stale = new List<PoliceDriver>();
        foreach (var kv in chaseSlot) if (kv.Key == null || !chasers.Contains(kv.Key)) stale.Add(kv.Key);
        foreach (var k in stale) chaseSlot.Remove(k);

        bool nearGround = target.transform.position.y < TrafficAuthority.Spacing * 1.5f;
        foreach (var u in chasers)
        {
            if (!chaseSlot.TryGetValue(u, out int s))
            {
                s = 0;
                while (chaseSlot.ContainsValue(s)) s++;
                chaseSlot[u] = s;
            }
            var def = ChaseSlots[Mathf.Min(s, ChaseSlots.Length - 1)];
            int layer = def.layer < 0 && nearGround ? 2 : def.layer; // no layer below the street
            u.ApproachSlot = new Vector3(def.x * preferredSide, 0f, def.z - (s >= ChaseSlots.Length ? 5f * (s - ChaseSlots.Length + 1) : 0f));
            u.LayerOffset = layer;
            u.MayRam = def.ram && s < ChaseSlots.Length;
        }
    }

    // ---------- disabled car: tow, box-in, arrest at the door ----------

    // Docking: each docker flies to its side slot (half widths + towGap apart), ignoring collisions with
    // the car; within 1 m it attaches rigidly. The tow starts as soon as the first docker is within 3 m
    // of its slot (latched while the car is disabled), or after tractorFallback s regardless (dockers
    // then attach on the way down). A red StopCone marks the stop until shortly after touchdown.
    void UpdateTow(FlyingVehicle car, bool occupied)
    {
        int want = Mathf.Min(occupied ? maxTowUnits : 1, pursuing.Count);
        while (dockers.Count > want) { UndockUnit(dockers[dockers.Count - 1]); dockers.RemoveAt(dockers.Count - 1); }
        while (dockers.Count < want)
        {
            PoliceDriver best = null; float bestSq = float.MaxValue;
            foreach (var u in pursuing)
            {
                if (dockers.Contains(u)) continue;
                float sq = (u.transform.position - car.transform.position).sqrMagnitude;
                if (sq < bestSq) { bestSq = sq; best = u; }
            }
            dockers.Add(best);
        }

        bool down = car.OnSurface;
        if (!down) downSince = -1f;
        else if (downSince < 0f) downSince = Time.time;
        if (cone == null && !down) cone = StopCone.Spawn(car.transform, ConeMaterial());
        if (cone != null && down && Time.time - downSince > 3f) { Destroy(cone.gameObject); cone = null; }

        Quaternion rot = car.PlatformRotation;
        for (int i = 0; i < dockers.Count; i++)
        {
            var u = dockers[i];
            float side = i == 0 ? 1f : -1f;
            if (down)
            {
                // Down: let go and park alongside.
                u.Car.Detach();
                u.HoldSlot(car, new Vector3(side * parkAlongside, 0f, 0f), car.towSpeed + 2f);
                continue;
            }
            if (u.Car.IsAttached) continue;
            u.Car.IgnoreCar = car;
            var slot = new Vector3(side * (car.BodyHalfExtents.x + u.Car.BodyHalfExtents.x + towGap), 0f, 0f);
            u.HoldSlot(car, slot, car.towSpeed + 2f);
            float d = (u.transform.position - (car.transform.position + rot * slot)).magnitude;
            if (i == 0 && d < 3f) towing = true;
            if (d < attachRange) u.Car.AttachTo(car, slot);
        }
        if (!towing && Time.time - car.DisabledTime > tractorFallback) towing = true; // descend anyway
        car.Towed = towing && !down;
        if (down && !occupied && !car.Impounded)
        {
            car.Impounded = true;
            Show("Car impounded");
        }
    }

    void UndockUnit(PoliceDriver u)
    {
        if (u == null) return;
        u.Car.Detach();
        u.Car.IgnoreCar = null;
    }

    Material ConeMaterial()
    {
        foreach (var u in units) if (u != null && u.coneMaterial != null) return u.coneMaterial;
        return null;
    }

    // Player inside the disabled car: the non-towing units box it in (front, back, above); on the
    // street two officers come to the door, and after arrestTime the player is pulled out and dragged.
    void UpdateBoxAndArrest(FlyingVehicle car, float dt)
    {
        int slot = 0;
        foreach (var u in chasers)
        {
            Vector3 off = slot == 0 ? new Vector3(0f, 0f, boxDistance)
                        : slot == 1 ? new Vector3(0f, 0f, -boxDistance)
                        : new Vector3(0f, 6f, slot == 2 ? 0f : boxDistance * (slot - 2));
            u.HoldSlot(car, off, car.towSpeed + 2f);
            slot++;
        }

        if (!car.OnSurface) return;
        var from = chasers.Count > 0 ? Nearest(chasers, car.transform.position) : dockers.Count > 0 ? dockers[0] : null;
        while (officers.Count < 2 && from != null) officers.Add(OfficerAgent.SpawnFrom(from, officers.Count == 0 ? -1.5f : 1.5f));

        Vector3 door = car.transform.position + car.PlatformRotation * Vector3.right * (car.BodyHalfExtents.x + 1.2f);
        door.y = car.transform.position.y - 0.5f;
        bool arrived = false;
        for (int i = 0; i < officers.Count; i++)
        {
            var o = officers[i];
            o.Goal = door + car.PlatformRotation * Vector3.forward * (i == 0 ? 0f : 1.5f);
            o.StopDistance = 0.8f;
            o.LookAt = car.transform.position;
            o.SpeedOverride = null;
            if (i == 0 && o.State == OfficerAgent.Phase.Foot && o.AtGoal) arrived = true;
        }
        if (!arrived) return;
        arrestTimer += dt;
        if (arrestTimer < arrestTime) return;

        // Pulled out, cuffed, and dragged off.
        arrestTimer = 0f;
        var fpc = car.Driver;
        pullingOut = true;
        car.Exit(false);
        pullingOut = false;
        if (fpc == null) return;
        fpc.Restrained = true;
        cuffer = officers[0];
        StartDrag(fpc);
    }

    void ReleaseTow()
    {
        if (towCar != null)
        {
            towCar.Towed = false;
            towCar.Impounded = false;
        }
        towCar = null;
        foreach (var u in dockers) UndockUnit(u);
        dockers.Clear();
        towing = false;
        if (cone != null) { Destroy(cone.gameObject); cone = null; }
    }

    static PoliceDriver Nearest(List<PoliceDriver> list, Vector3 p)
    {
        PoliceDriver best = null; float bestSq = float.MaxValue;
        foreach (var u in list)
        {
            if (u == null) continue;
            float sq = (u.transform.position - p).sqrMagnitude;
            if (sq < bestSq) { bestSq = sq; best = u; }
        }
        return best;
    }

    // ---------- on foot: officers, apprehension, weapons ----------

    void UpdateOnFoot(FirstPersonController fpc, float dt)
    {
        Vector3 p = fpc.transform.position;

        // Roof arrest: one car alongside the carrying car, one officer onto the roof; the rest wait.
        if (roofCar != null && fpc.Platform != roofCar) { roofCar = null; roofUnit = null; }
        if (roofCar != null) UpdateRoofArrest(fpc, dt);
        else
        {
            // Officers: one, or two once force is used.
            int want = ForceLevel == Force.None ? 1 : 2;
            if (officers.Count < want && Time.time >= nextOfficer)
            {
                var from = Nearest(chasers.FindAll(u => u != dragUnit), p) ?? Nearest(chasers, p);
                if (from != null)
                {
                    officers.Add(OfficerAgent.SpawnFrom(from, officers.Count == 0 ? -1.5f : 1.5f));
                    nextOfficer = Time.time + 1f;
                }
            }
            Vector3 watch = towCar != null ? towCar.transform.position : p;
            foreach (var u in chasers) if (u != dragUnit) u.HoverNear(watch);
        }

        UpdateApprehension(fpc, dt);
        if (transporting) return;

        // Weapons, while nobody has hands on the player.
        if (arrest != Arrest.None) return;
        Vector3 aim = p + Vector3.up * 1.2f;
        foreach (var o in officers)
        {
            if (o == null) continue;
            if (ForceLevel != Force.None) o.AimAt(aim);
            if (ForceLevel == Force.NonLethal && !fpc.Stunned)
            {
                if (o.FireStun(aim, stunRange, stunInterval))
                {
                    fpc.Stun(stunTime);
                    stunFlashUntil = Time.time + 0.4f;
                    Show("Stunned!");
                }
            }
            else if (ForceLevel == Force.Lethal && o.FireSidearm(aim, sidearmRange, sidearmBurst, sidearmBurstInterval, sidearmShotInterval) > 0)
            {
                fpc.Damage(sidearmDamage);
                if (fpc.Health <= 0f) { Busted("Shot down"); return; }
            }
        }
    }

    // Approach -> Cuffing -> Dragging -> Loaded.
    void UpdateApprehension(FirstPersonController fpc, float dt)
    {
        Vector3 p = fpc.transform.position;
        switch (arrest)
        {
            case Arrest.None:
            {
                OfficerAgent nearest = null; float best = float.MaxValue;
                foreach (var o in officers)
                {
                    o.Goal = p; o.StopDistance = cuffRange; o.LookAt = p; o.SpeedOverride = null;
                    if (o.State != OfficerAgent.Phase.Foot || o.Staggered) continue;
                    float d = Flat(o.transform.position - p).magnitude;
                    if (d < best) { best = d; nearest = o; }
                }
                // Hands on: within cuffing range (a bit more while stunned: they rush in).
                if (nearest != null && best <= cuffRange + (fpc.Stunned ? 0.6f : 0.25f) && Mathf.Abs(nearest.transform.position.y - p.y) < 1.5f)
                {
                    arrest = Arrest.Cuffing;
                    cuffer = nearest;
                    cuffTimer = 0f;
                }
                break;
            }
            case Arrest.Cuffing:
            {
                foreach (var o in officers) { o.Goal = p; o.StopDistance = cuffRange; o.LookAt = p; o.SpeedOverride = null; }
                if (cuffer == null || cuffer.Staggered || Flat(cuffer.transform.position - p).magnitude > resistDistance)
                {
                    arrest = Arrest.None;
                    cuffer = null;
                    cuffInterrupts++;
                    if (cuffInterrupts >= 2) Escalate(Force.NonLethal, "Resisted arrest");
                    else Show("Resisted arrest");
                    break;
                }
                cuffTimer += dt;
                if (cuffTimer >= cuffTime)
                {
                    fpc.Restrained = true;
                    StartDrag(fpc);
                    Show("Cuffed");
                }
                break;
            }
            case Arrest.Dragging:
                UpdateDrag(fpc, dt);
                break;
        }
    }

    void StartDrag(FirstPersonController fpc)
    {
        arrest = Arrest.Dragging;
        dragBreak = 0f;
        dragUnit = roofUnit != null ? roofUnit : Nearest(pursuing, fpc.transform.position);
    }

    // The officer walks the player to the nearest police car at dragSpeed. The car comes down next to
    // them (on a roof arrest it's the car alongside). Reaching its door = loaded.
    void UpdateDrag(FirstPersonController fpc, float dt)
    {
        Vector3 p = fpc.transform.position;
        if (cuffer == null) { ReleasePlayer(fpc); return; }
        if (dragUnit == null) dragUnit = Nearest(pursuing, p);
        if (dragUnit == null) { ReleasePlayer(fpc); return; }

        // Mash to break free.
        dragBreak = Mathf.Max(0f, dragBreak - breakDecay * dt);
        if (dragBreak >= 1f)
        {
            Vector3 away = Flat(cuffer.transform.position - p);
            cuffer.Stagger(1.5f, (away.sqrMagnitude > 0.01f ? away.normalized : cuffer.transform.forward) * 1f);
            ReleasePlayer(fpc);
            Escalate(Force.NonLethal, "Broke free");
            return;
        }

        var car = dragUnit.Car;
        Quaternion rot = car.PlatformRotation;
        float feet = p.y;
        if (dragUnit != roofUnit)
        {
            // Pull up beside the player, just off the ground they stand on.
            Vector3 off = Flat(car.transform.position - p);
            off = off.sqrMagnitude > 0.01f ? off.normalized : Vector3.forward;
            Vector3 pickup = p + off * 6f;
            pickup.y = feet + (car.BodyHalfExtents.y - car.BodyCenterLocal.y) + 0.5f;
            dragUnit.FlyTo(pickup, 20f, dragUnit.policeVerticalSpeed);
        }
        float sideSign = Mathf.Sign((Quaternion.Inverse(rot) * (p - car.transform.position)).x);
        if (sideSign == 0f) sideSign = 1f;
        Vector3 door = car.transform.position + rot * Vector3.right * sideSign * (car.BodyHalfExtents.x + 0.9f);

        cuffer.Goal = door; cuffer.StopDistance = 0.2f; cuffer.LookAt = null; cuffer.SpeedOverride = dragSpeed;
        foreach (var o in officers) if (o != cuffer) { o.Goal = p; o.StopDistance = 2f; o.LookAt = p; o.SpeedOverride = null; }

        // The player is pulled along just behind the officer.
        Vector3 want = cuffer.transform.position - cuffer.transform.forward * 0.8f;
        Vector3 delta = Flat(want - p);
        fpc.Drag(Vector3.ClampMagnitude(delta, dragSpeed * 2f * dt));

        if (Flat(door - p).magnitude < 1.8f && Mathf.Abs(door.y - (p.y + 1f)) < 3f) Load(fpc, dragUnit);
    }

    void ReleasePlayer(FirstPersonController fpc)
    {
        fpc.Restrained = false;
        arrest = Arrest.None;
        cuffer = null;
        dragUnit = null;
    }

    // Into the back seat, hands tied, off to the station. Everyone else goes back to patrol.
    void Load(FirstPersonController fpc, PoliceDriver unit)
    {
        fpc.Restrained = false;
        arrest = Arrest.None;
        cuffer = null;
        dragUnit = null;
        roofCar = null; roofUnit = null;
        ReturnOfficers();
        foreach (var u in pursuing) if (u != unit) u.Patrol();
        pursuing.Clear();
        pursuing.Add(unit);
        chaseSlot.Clear();
        if (towCar != null)
        {
            towCar.Towed = false;
            foreach (var u in dockers) UndockUnit(u);
            dockers.Clear();
        }

        if (PoliceStation.Find() == null) { Busted("Busted"); return; }
        PlayerRide.Begin(fpc, unit.Car, PlayerRide.Seat.Back, true, true);
        transporting = true;
        transportUnit = unit;
        trip = Trip.Climb;
        cruiseY = CruiseHeight(unit.transform.position.y) + (unit.Car.BodyHalfExtents.y - unit.Car.BodyCenterLocal.y);
        Show("Loaded into the police car");
    }

    // ---------- transport to the station ----------

    // Nearest layer (at least 3) that no flyway uses.
    float CruiseHeight(float y)
    {
        if (laneLayers == null)
        {
            laneLayers = new bool[TrafficAuthority.MaxLayer + 1];
            foreach (var lane in FindObjectsByType<LanePath>(FindObjectsSortMode.None))
                foreach (int level in lane.Levels)
                {
                    int g = lane.GridLayerOf(level);
                    if (g >= 0 && g < laneLayers.Length) laneLayers[g] = true;
                }
        }
        int here = TrafficAuthority.NearestLayer(y);
        for (int d = 0; d <= laneLayers.Length; d++)
            foreach (int l in new[] { here + d, here - d })
                if (l >= 3 && l < laneLayers.Length && !laneLayers[l]) return TrafficAuthority.RideHeight(l);
        return TrafficAuthority.RideHeight(Mathf.Max(3, here));
    }

    // Climb to a free layer, fly to the station, land on the pad. Arrival = Busted.
    void UpdateTransport()
    {
        var u = transportUnit;
        var ride = PlayerRide.Instance;
        if (u == null || !PlayerRide.Active || ride.Car != u.Car) { transporting = false; return; }
        if (ride.DriverDown || Time.time < transportHaltUntil) { u.Halt(); return; }

        var station = PoliceStation.Find();
        if (station == null) { Busted("Busted"); return; }
        Vector3 pos = u.transform.position;
        Vector3 pad = station.pad.position;
        float padY = pad.y + (u.Car.BodyHalfExtents.y - u.Car.BodyCenterLocal.y) + 0.3f;
        float flat = Flat(pad - pos).magnitude;
        switch (trip)
        {
            case Trip.Climb:
                u.FlyTo(new Vector3(pos.x, cruiseY, pos.z), 10f, u.policeVerticalSpeed);
                if (Mathf.Abs(pos.y - cruiseY) < 2f) trip = Trip.Cruise;
                break;
            case Trip.Cruise:
                u.FlyTo(new Vector3(pad.x, cruiseY, pad.z), 45f, u.policeVerticalSpeed);
                if (flat < 12f) trip = Trip.Descend;
                break;
            default:
                u.FlyTo(new Vector3(pad.x, padY, pad.z), 12f, u.policeVerticalSpeed);
                if (flat < 3f && Mathf.Abs(pos.y - padY) < 1.5f) Busted("Booked at the station");
                break;
        }
    }

    // Fine, the stolen car is lost, released at the station door (or the start deck without a
    // station), wanted 0.
    void Busted(string why)
    {
        var ta = TrafficAuthority.Instance;
        if (ta != null) ta.Fine(bustedFine);
        foreach (var lost in new[] { towCar, lastPlayerCar })
            if (lost != null && !lost.IsOccupied && lost.GetComponent<PoliceDriver>() == null) Destroy(lost.gameObject);
        lastPlayerCar = null;

        var fpc = FirstPersonController.Instance;
        var station = PoliceStation.Find();
        string msg = $"BUSTED ({why})  -  fined {bustedFine} cr";
        if (PlayerRide.Active) PlayerRide.Instance.Release();
        EndPursuit();
        if (fpc != null)
        {
            if (station != null && station.door != null)
            {
                fpc.PlaceAt(station.door.position, station.door.rotation);
                fpc.Heal();
                fpc.Flash(msg);
            }
            else fpc.Respawn(msg);
        }
        Show(msg);
    }

    // ---------- roof-riding ----------

    // On a moving car's roof in sight of police: a callout, then arrest if still up there.
    void UpdateRoofRiding(FirstPersonController fpc)
    {
        var car = fpc != null ? fpc.Platform : null;
        bool moving = car != null && car.Velocity.magnitude > 3f;
        if (!moving || car == roofCar || arrest != Arrest.None || transporting) { callout = false; return; }
        if (!callout)
        {
            if (PoliceSees(fpc.transform.position + Vector3.up, roofSightRange, out var by))
            {
                callout = true;
                calloutUntil = Time.time + roofComplyTime;
                calloutBy = by;
                Show("POLICE: Get back inside the vehicle!");
            }
            return;
        }
        if (Time.time < calloutUntil) return;
        callout = false;
        EnsureWanted(1, calloutBy);
        StartRoofArrest(car);
    }
    PoliceDriver calloutBy;

    void StartRoofArrest(FlyingVehicle car)
    {
        roofCar = car;
        roofUnit = Nearest(pursuing, car.transform.position);
        roofAlongside = 0f;
        roofSide = 1;
        ReturnOfficers();
    }

    void UpdateRoofArrest(FirstPersonController fpc, float dt)
    {
        if (roofUnit == null) roofUnit = Nearest(pursuing, roofCar.transform.position);
        if (roofUnit == null) return;
        float x = roofCar.BodyHalfExtents.x + roofUnit.Car.BodyHalfExtents.x + 1f;
        roofUnit.HoldSlot(roofCar, new Vector3(roofSide * x, 0f, 0f), roofUnit.policeVerticalSpeed + 4f);
        foreach (var u in chasers) if (u != roofUnit) u.HoverNear(roofCar.transform.position);

        roofAlongside = roofUnit.AtSlot ? roofAlongside + dt : 0f;
        if (officers.Count == 0 && roofAlongside >= 0.5f)
        {
            Quaternion rot = roofCar.PlatformRotation;
            Vector3 step = roofCar.transform.position + rot * new Vector3(roofSide * (roofCar.BodyHalfExtents.x - 0.5f), 0f, 0f);
            step.y = roofCar.RoofY + 0.05f;
            officers.Add(OfficerAgent.SpawnOnRoof(roofUnit, roofCar, step));
            Show("An officer steps onto the roof");
        }
    }

    // ---------- officers ----------

    void ReturnOfficers()
    {
        foreach (var o in officers) if (o != null) o.ReturnToUnit();
        officers.Clear();
        if (arrest != Arrest.None)
        {
            var fpc = FirstPersonController.Instance;
            if (fpc != null) fpc.Restrained = false;
            arrest = Arrest.None;
            cuffer = null;
            dragUnit = null;
        }
    }

    static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }

    // ---------- effects / HUD ----------

    // Placeholder sparks flickering on a disabled car the player is in.
    void UpdateSparks(FlyingVehicle car)
    {
        bool on = car != null && car.Disabled;
        if (!on) { if (spark != null) spark.gameObject.SetActive(false); return; }
        if (spark == null)
        {
            Material m = null;
            foreach (var u in units) if (u != null && u.sparkMaterial != null) { m = u.sparkMaterial; break; }
            if (m == null) return;
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "DisabledSparks";
            Destroy(go.GetComponent<Collider>());
            var rend = go.GetComponent<Renderer>();
            rend.sharedMaterial = m;
            rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            spark = go.transform;
        }
        if (Time.time < nextSpark) return;
        nextSpark = Time.time + Random.Range(0.05f, 0.25f);
        bool show = Random.value < 0.6f;
        spark.gameObject.SetActive(show);
        if (!show) return;
        Vector3 h = car.BodyHalfExtents;
        spark.position = car.transform.TransformPoint(car.BodyCenterLocal +
                         new Vector3(Random.Range(-h.x, h.x), h.y * Random.Range(0.3f, 1f), Random.Range(-h.z, h.z)));
        spark.rotation = Random.rotation;
        spark.localScale = Vector3.one * Random.Range(0.1f, 0.3f);
    }

    void Show(string msg) { message = msg; messageUntil = Time.time + 3f; }

    static void Bar(Rect r, float fill, Color c, string label)
    {
        var prev = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.6f);
        GUI.DrawTexture(r, Texture2D.whiteTexture);
        GUI.color = c;
        GUI.DrawTexture(new Rect(r.x, r.y, r.width * Mathf.Clamp01(fill), r.height), Texture2D.whiteTexture);
        GUI.color = prev;
        if (!string.IsNullOrEmpty(label)) GUI.Label(new Rect(r.x, r.y - 20, r.width + 100, 20), label);
    }

    void OnGUI()
    {
        float right = Screen.width - 20;
        var car = FlyingVehicle.Driven;
        var fpc = FirstPersonController.Instance;
        var prev = GUI.color;

        // Stun flash.
        if (Time.time < stunFlashUntil)
        {
            GUI.color = new Color(0.8f, 0.9f, 1f, (stunFlashUntil - Time.time) / 0.4f);
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
            GUI.color = prev;
        }

        // Wanted stars and the force level, top right.
        if (WantedLevel > 0)
        {
            for (int i = 0; i < 3; i++)
            {
                GUI.color = i < WantedLevel ? new Color(1f, 0.85f, 0.2f) : new Color(1f, 1f, 1f, 0.25f);
                GUI.DrawTexture(new Rect(right - (3 - i) * 26, 20, 20, 20), Texture2D.whiteTexture);
            }
            GUI.color = ForceLevel == Force.Lethal ? new Color(1f, 0.3f, 0.25f) : ForceLevel == Force.NonLethal ? new Color(0.4f, 0.75f, 1f) : Color.white;
            GUI.Label(new Rect(right - 200, 44, 200, 20),
                      "WANTED   " + (ForceLevel == Force.Lethal ? "LETHAL" : ForceLevel == Force.NonLethal ? "STUN" : "no force"));
            GUI.color = prev;
        }

        float y = 90;
        if (Contact > 0f)
        {
            var r = new Rect(right - 200, y, 200, 8);
            Bar(r, Contact / empContactTime, new Color(0.3f, 0.6f, 1f), "EMP contact");
            // Grace left: an outline that fades as the 2 s run out.
            float g = ContactGraceLeft / Mathf.Max(contactGrace, 0.01f);
            if (g > 0f)
            {
                GUI.color = new Color(0.6f, 0.85f, 1f, g);
                GUI.DrawTexture(new Rect(r.x - 2, r.y - 2, r.width + 4, 2), Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(r.x - 2, r.yMax, r.width + 4, 2), Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(r.x - 2, r.y, 2, r.height), Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(r.xMax, r.y, 2, r.height), Texture2D.whiteTexture);
                GUI.color = prev;
            }
            y += 40;
        }
        // Car health has no HUD bar: read the car itself, or the dashboard gauge in first person.
        if (fpc != null && fpc.isActiveAndEnabled && fpc.Health < fpc.maxHealth)
        {
            Bar(new Rect(right - 200, y, 200, 8), fpc.Health / fpc.maxHealth, new Color(0.9f, 0.2f, 0.25f), "Health");
            y += 40;
        }

        float cx = Screen.width / 2f;
        if (arrestTimer > 0f) Bar(new Rect(cx - 120, Screen.height * 0.75f, 240, 10), arrestTimer / arrestTime, new Color(1f, 0.25f, 0.2f), "ARREST");
        if (arrest == Arrest.Cuffing) Bar(new Rect(cx - 120, Screen.height * 0.75f, 240, 10), cuffTimer / cuffTime, new Color(1f, 0.25f, 0.2f), "BEING CUFFED  -  move away!");
        if (arrest == Arrest.Dragging) Bar(new Rect(cx - 120, Screen.height * 0.75f, 240, 10), dragBreak, new Color(1f, 0.7f, 0.2f), "DRAGGED  -  mash the mouse buttons to break free");
        if (callout) GUI.Label(new Rect(cx - 160, Screen.height * 0.3f, 420, 25), $"POLICE: Get back inside the vehicle!  {calloutUntil - Time.time:0.0}");
        if (car != null && car.Disabled)
        {
            float hold = car.RoofHoldProgress;
            if (hold > 0f) Bar(new Rect(cx - 100, Screen.height * 0.8f, 200, 8), hold, new Color(0.3f, 0.9f, 1f), "Hold E: roof");
            else if (!RestartQTE.Active) GUI.Label(new Rect(cx - 150, Screen.height * 0.8f - 20, 400, 20), "DISABLED   R restart | E door | hold E roof");
        }
        if (Time.time < messageUntil) GUI.Label(new Rect(cx - 200, 60, 400, 25), message);
    }
}
