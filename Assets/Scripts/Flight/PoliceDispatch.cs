using System.Collections.Generic;
using UnityEngine;

// Police brain, one per scene (created on demand). Police don't care about traffic rules; they
// care about reckless crashing:
//   Trigger : the player's car crashes at crashTrigger m/s or more while a police car within
//             triggerRange can see it. Wanted level 1, that car pursues.
//   Resist  : not stopping for ignoreStopTime s, ramming a police car at ramPoliceSpeed, hitting a
//             civilian car while wanted, breaking free from an arrest. Each raises the level (max 3).
//   Units   : up to 1 / 3 / 5 cars per level; missing ones are recruited from patrols or spawned on
//             a lane 300 to 500 m away, out of view.
//   Contact : pursuing cars ram the player's car. The meter fills while the last touch was under
//             contactGrace s ago (a brief gap doesn't save you) and drains once it's longer.
//             empContactTime s of it = EMP.
//   Disabled: the car is always towed down to the street, with or without the player in it. Units
//             dock at its sides (two with the player inside, one if it's empty) and tow it; the towing
//             unit never deploys officers. The others box it in. On the street an occupied car gets two
//             officers from a non-towing unit and an arrest; an empty one is impounded (locked until
//             the pursuit ends).
//   On foot : the player left a disabled car: one officer on a scooter from the nearest non-towing
//             unit chases them; the other units watch the tow group. Taking another car sends the
//             officer back to its unit and the chase goes on against the new car.
//   Arrest  : arrestTime s at the door, then an escort with one break-free sequence. Fail = Busted.
//   Level 3 : units shoot at the player's car.
//   Losing  : decayTime s with no unit seeing the player lowers the level one step; at 0 they patrol.
[DefaultExecutionOrder(-20)]
public class PoliceDispatch : MonoBehaviour
{
    public static PoliceDispatch Instance { get; private set; }

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

    [Header("Box-in, tow and arrest")]
    public int maxTowUnits = 2;
    public float boxDistance = 8f;
    [Tooltip("Once the car is down, the docked units move out this far sideways to park alongside.")]
    public float parkAlongside = 7f;
    public float officerSpeed = 10f;
    public float arrestTime = 3f;
    public float escortTime = 3f;
    public int breakFreeLength = 3;
    public int bustedFine = 500;

    public int WantedLevel { get; private set; }
    public float Contact { get; private set; }
    // Seconds of grace left before the contact meter starts draining.
    public float ContactGraceLeft => Mathf.Max(0f, contactGrace - (Time.time - lastTouchTime));

    // Placeholder officer on a fold-out scooter, belonging to a unit.
    class Officer
    {
        public Transform t;
        public PoliceDriver home;
    }

    static readonly List<PoliceDriver> units = new List<PoliceDriver>();
    readonly List<PoliceDriver> pursuing = new List<PoliceDriver>();
    readonly List<PoliceDriver> dockers = new List<PoliceDriver>();
    readonly List<Officer> officers = new List<Officer>();
    readonly List<PoliceDriver> chasers = new List<PoliceDriver>();
    LanePath[] lanes;
    float lastTouchTime = -100f;
    FlyingVehicle towCar;   // disabled car being towed (or impounded), player in it or not
    float unseenTime, ignoreTime, lastResist = -100f, nextSpawn;
    bool towing;
    float arrestTimer;
    bool escorting; float escortEnd;
    FlyingVehicle arrestedCar;
    string message; float messageUntil;
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

    // ---------- events from the cars ----------

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
        ClearOfficers();
        arrestTimer = 0f;
    }

    // Leaving a disabled car doesn't stop the tow: it goes down to the street empty.
    public void OnPlayerExited(FlyingVehicle car)
    {
        if (escorting) return;
        arrestTimer = 0f;
        Contact = 0f;
        if (car.Disabled) ClearOfficers(); // any arresting officers give way to one scooter chaser
    }

    // ---------- wanted level ----------

    void TryTrigger(FlyingVehicle car)
    {
        if (car == null || WantedLevel > 0) return;
        Vector3 p = car.transform.position;
        foreach (var u in units)
        {
            if (u == null) continue;
            Vector3 eye = u.transform.position + Vector3.up;
            if ((eye - p).sqrMagnitude > triggerRange * triggerRange || !LineOfSight(eye, p)) continue;
            WantedLevel = 1;
            unseenTime = 0f; ignoreTime = 0f;
            StartPursuit(u);
            Show("RECKLESS FLYING  -  POLICE PURSUIT");
            return;
        }
    }

    void Resist(string why)
    {
        if (WantedLevel <= 0 || WantedLevel >= 3 || Time.time - lastResist < resistCooldown) return;
        lastResist = Time.time;
        WantedLevel++;
        Show($"{why}  -  WANTED {WantedLevel}");
    }

    void StartPursuit(PoliceDriver u)
    {
        if (pursuing.Contains(u)) return;
        u.Pursue();
        pursuing.Add(u);
    }

    void EndPursuit()
    {
        WantedLevel = 0;
        foreach (var u in pursuing) if (u != null) u.Patrol();
        pursuing.Clear();
        ReleaseTow();
        ClearOfficers();
        Contact = 0f;
        arrestTimer = 0f;
        unseenTime = 0f; ignoreTime = 0f;
        if (escorting) { escorting = false; RestartQTE.Cancel(); var fpc = FirstPersonController.Instance; if (fpc != null) fpc.Frozen = false; }
    }

    // ---------- main loop (before the police drivers and the cars move) ----------

    void Update()
    {
        float dt = Time.deltaTime;
        pursuing.RemoveAll(u => u == null || !u.isActiveAndEnabled);
        dockers.RemoveAll(u => u == null || !pursuing.Contains(u));
        officers.RemoveAll(o => o.t == null);

        var car = FlyingVehicle.Driven;
        var fpc = FirstPersonController.Instance;
        if (towCar == null ? dockers.Count > 0 : !towCar.Disabled) ReleaseTow(); // restarted, or gone

        UpdateSparks(car);
        if (WantedLevel == 0) { Contact = 0f; return; }
        if (car != null && car.Disabled) towCar = car;

        Vector3 playerPos = car != null ? car.transform.position : fpc != null ? fpc.transform.position + Vector3.up : Vector3.zero;

        // An empty car being towed ties up its towing unit: staff one more.
        StaffUnits(playerPos, towCar != null && towCar != car ? 1 : 0);
        if (pursuing.Count == 0) { EndPursuit(); return; }

        // Seen? Otherwise the level decays.
        bool seen = false;
        foreach (var u in pursuing)
        {
            Vector3 eye = u.transform.position + Vector3.up;
            if ((eye - playerPos).sqrMagnitude <= sightRange * sightRange && LineOfSight(eye, playerPos)) { seen = true; break; }
        }
        unseenTime = seen ? 0f : unseenTime + dt;
        if (unseenTime >= decayTime)
        {
            unseenTime = 0f;
            WantedLevel--;
            if (WantedLevel == 0) { EndPursuit(); Show("You lost them"); return; }
            Show($"Wanted level {WantedLevel}");
        }

        if (escorting) { UpdateEscort(fpc); return; }

        // The tow carries on wherever the player is.
        if (towCar != null) UpdateTow(towCar, towCar == car);
        chasers.Clear();
        foreach (var u in pursuing) if (!dockers.Contains(u)) chasers.Add(u);

        if (car != null && !car.Disabled)
        {
            // In a (new) car: scooter officers go back to their units.
            ReturnOfficers(dt);

            // Not stopping.
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
            foreach (var u in chasers) u.Chase(car);
            if (WantedLevel >= 3) foreach (var u in chasers) u.UpdateShooting(car);
            return;
        }

        Contact = 0f;
        if (car != null) { UpdateBoxAndArrest(car, dt); return; }

        // On foot.
        Vector3 p = fpc != null ? fpc.transform.position : playerPos;
        if (towCar != null)
        {
            // One officer on a scooter from the nearest non-towing unit; the rest watch the tow group.
            if (officers.Count == 0 && chasers.Count > 0) SpawnOfficers(Nearest(chasers, p), 1);
            foreach (var u in chasers) u.HoverNear(towCar.transform.position);
        }
        else foreach (var u in chasers) u.HoverNear(p);
        MoveOfficersTo(p, 1.5f, dt);
    }

    static PoliceDriver Nearest(List<PoliceDriver> list, Vector3 p)
    {
        PoliceDriver best = null; float bestSq = float.MaxValue;
        foreach (var u in list)
        {
            float sq = (u.transform.position - p).sqrMagnitude;
            if (sq < bestSq) { bestSq = sq; best = u; }
        }
        return best;
    }

    // Keep the unit count at the level's cap: release extras, recruit patrols, spawn the rest.
    void StaffUnits(Vector3 playerPos, int extra)
    {
        int cap = unitCap[Mathf.Clamp(WantedLevel, 0, unitCap.Length - 1)] + extra;
        while (pursuing.Count > cap)
        {
            int far = -1;
            for (int i = 0; i < pursuing.Count; i++)
                if (!dockers.Contains(pursuing[i]) && (far < 0 ||
                    (pursuing[i].transform.position - playerPos).sqrMagnitude > (pursuing[far].transform.position - playerPos).sqrMagnitude)) far = i;
            if (far < 0) break;
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
            return go.GetComponent<PoliceDriver>();
        }
        return null;
    }

    // ---------- disabled car: tow, box-in, arrest ----------

    // Dock and tow the disabled car down to the street: two units with the player inside, one if it's
    // empty (the second undocks). Docked units sit nose-in against its sides. Down on the street they
    // park alongside; an empty car is impounded.
    void UpdateTow(FlyingVehicle car, bool occupied)
    {
        int want = Mathf.Min(occupied ? maxTowUnits : 1, pursuing.Count);
        while (dockers.Count > want) dockers.RemoveAt(dockers.Count - 1);
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
        bool allDocked = dockers.Count > 0;
        for (int i = 0; i < dockers.Count; i++)
        {
            var u = dockers[i];
            float side = i == 0 ? 1f : -1f;
            float x = down ? parkAlongside : car.BodyHalfExtents.x + u.Car.BodyHalfExtents.z + 0.1f;
            u.HoldSlot(car, new Vector3(side * x, 0f, 0f), car.towSpeed + 2f);
            allDocked &= u.AtSlot;
        }
        if (allDocked) towing = true;
        car.Towed = towing && !down;
        if (down && !occupied && !car.Impounded)
        {
            car.Impounded = true;
            Show("Car impounded");
        }
    }

    // Player inside the disabled car: the non-towing units box it in (front, back, above); on the
    // street two officers from one of them come to the door.
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
        if (officers.Count == 0)
        {
            // The towing unit keeps its crew; only with nobody else does it send its own.
            var from = chasers.Count > 0 ? Nearest(chasers, car.transform.position) : dockers.Count > 0 ? dockers[0] : null;
            if (from != null) SpawnOfficers(from, 2);
        }
        Vector3 door = car.transform.position + car.PlatformRotation * Vector3.right * (car.BodyHalfExtents.x + 1.2f);
        bool arrived = MoveOfficersTo(door, 0.8f, dt);
        if (!arrived) return;
        arrestTimer += dt;
        if (arrestTimer >= arrestTime) BeginEscort(car);
    }

    void ReleaseTow()
    {
        if (towCar != null)
        {
            towCar.Towed = false;
            towCar.Impounded = false;
        }
        towCar = null;
        dockers.Clear();
        towing = false;
    }

    // ---------- officers (placeholder: capsule on a hover board) ----------

    void SpawnOfficers(PoliceDriver from, int count)
    {
        var bodyRend = from.GetComponentInChildren<MeshRenderer>(true);
        for (int i = 0; i < count; i++)
        {
            var root = new GameObject("Officer").transform;
            root.position = from.transform.position + from.Car.PlatformRotation * new Vector3(i == 0 ? -1.5f : 1.5f, 0.5f, 0f);
            var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            Destroy(body.GetComponent<Collider>());
            body.transform.SetParent(root, false);
            body.transform.localPosition = new Vector3(0f, 1.1f, 0f);
            body.transform.localScale = new Vector3(0.5f, 0.85f, 0.5f);
            var board = GameObject.CreatePrimitive(PrimitiveType.Cube); // the fold-out scooter
            Destroy(board.GetComponent<Collider>());
            board.transform.SetParent(root, false);
            board.transform.localPosition = new Vector3(0f, 0.15f, 0f);
            board.transform.localScale = new Vector3(0.7f, 0.08f, 1.3f);
            if (bodyRend != null) body.GetComponent<Renderer>().sharedMaterial = bodyRend.sharedMaterial;
            officers.Add(new Officer { t = root, home = from });
        }
    }

    // Back to their units; gone once aboard.
    void ReturnOfficers(float dt)
    {
        for (int i = officers.Count - 1; i >= 0; i--)
        {
            var o = officers[i];
            if (o.home == null) { Destroy(o.t.gameObject); officers.RemoveAt(i); continue; }
            Vector3 to = o.home.transform.position - o.t.position;
            float dist = to.magnitude;
            if (dist < 2f) { Destroy(o.t.gameObject); officers.RemoveAt(i); continue; }
            o.t.position += to / dist * Mathf.Min(dist, officerSpeed * 1.5f * dt);
        }
    }

    // Officers fly toward a point (side by side). True once the first is within `stop` m.
    bool MoveOfficersTo(Vector3 target, float stop, float dt)
    {
        bool arrived = false;
        for (int i = 0; i < officers.Count; i++)
        {
            var o = officers[i].t;
            Vector3 t = target + Vector3.right * (i == 0 ? 0f : 1.2f);
            Vector3 to = t - o.position;
            float dist = to.magnitude;
            if (dist > stop) o.position += to / dist * Mathf.Min(dist - stop, officerSpeed * dt);
            to.y = 0f;
            if (to.sqrMagnitude > 0.01f) o.rotation = Quaternion.LookRotation(to);
            if (i == 0 && dist <= stop + 0.3f) arrived = true;
        }
        return arrived;
    }

    void ClearOfficers()
    {
        foreach (var o in officers) if (o.t != null) Destroy(o.t.gameObject);
        officers.Clear();
    }

    // ---------- arrest ----------

    void BeginEscort(FlyingVehicle car)
    {
        var fpc = car.Driver;
        if (fpc == null) return;
        escorting = true;
        arrestedCar = car;
        escortEnd = Time.time + escortTime;
        arrestTimer = 0f;
        RestartQTE.Cancel();
        car.Exit(false);
        var cop = dockers.Count > 0 ? dockers[0] : pursuing[0];
        Quaternion r = cop.Car.PlatformRotation;
        fpc.PlaceAt(cop.transform.position + r * Vector3.right * (cop.Car.BodyHalfExtents.x + 1.5f), r);
        fpc.Frozen = true;
        RestartQTE.Begin("BREAK FREE  (Bypass " + PlayerSkills.BypassLevel + ")", breakFreeLength, PlayerSkills.PromptWindow, false,
                         BreakFree, Busted);
    }

    void UpdateEscort(FirstPersonController fpc)
    {
        if (fpc != null) MoveOfficersTo(fpc.transform.position + fpc.transform.right * 1.2f, 0.5f, Time.deltaTime);
        foreach (var u in pursuing) if (!dockers.Contains(u) && fpc != null) u.HoverNear(fpc.transform.position);
        if (Time.time >= escortEnd && !RestartQTE.Active) Busted();
    }

    void BreakFree()
    {
        if (!escorting) return;
        escorting = false;
        var fpc = FirstPersonController.Instance;
        if (fpc != null) fpc.Frozen = false;
        WantedLevel = 3;
        unseenTime = 0f;
        Show("BROKE FREE  -  WANTED 3");
    }

    void Busted()
    {
        if (!escorting) return;
        escorting = false;
        RestartQTE.Cancel();
        // The stolen car is gone.
        if (arrestedCar != null && !arrestedCar.IsOccupied)
        {
            if (arrestedCar == towCar) ReleaseTow();
            Destroy(arrestedCar.gameObject);
        }
        arrestedCar = null;
        var ta = TrafficAuthority.Instance;
        if (ta != null) ta.Fine(bustedFine);
        EndPursuit();
        var fpc = FirstPersonController.Instance;
        if (fpc != null) fpc.Respawn($"BUSTED  -  fined {bustedFine} cr");
        Show($"BUSTED  -  fined {bustedFine} cr");
    }

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

        // Wanted stars, top right.
        if (WantedLevel > 0)
        {
            var prev = GUI.color;
            for (int i = 0; i < 3; i++)
            {
                GUI.color = i < WantedLevel ? new Color(1f, 0.85f, 0.2f) : new Color(1f, 1f, 1f, 0.25f);
                GUI.DrawTexture(new Rect(right - (3 - i) * 26, 20, 20, 20), Texture2D.whiteTexture);
            }
            GUI.color = prev;
            GUI.Label(new Rect(right - 200, 44, 200, 20), "WANTED");
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
                var prev = GUI.color;
                GUI.color = new Color(0.6f, 0.85f, 1f, g);
                GUI.DrawTexture(new Rect(r.x - 2, r.y - 2, r.width + 4, 2), Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(r.x - 2, r.yMax, r.width + 4, 2), Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(r.x - 2, r.y, 2, r.height), Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(r.xMax, r.y, 2, r.height), Texture2D.whiteTexture);
                GUI.color = prev;
            }
            y += 40;
        }
        if (car != null && (WantedLevel >= 3 || car.Integrity < car.maxIntegrity))
        {
            Bar(new Rect(right - 200, y, 200, 8), car.Integrity / car.maxIntegrity, new Color(0.9f, 0.3f, 0.2f), "Integrity");
            y += 40;
        }

        float cx = Screen.width / 2f;
        if (arrestTimer > 0f) Bar(new Rect(cx - 120, Screen.height * 0.75f, 240, 10), arrestTimer / arrestTime, new Color(1f, 0.25f, 0.2f), "ARREST");
        if (escorting) GUI.Label(new Rect(cx - 120, Screen.height * 0.75f - 20, 300, 20), "ESCORTED  -  break free!");
        if (car != null && car.Disabled)
        {
            float hold = car.RoofHoldProgress;
            if (hold > 0f) Bar(new Rect(cx - 100, Screen.height * 0.8f, 200, 8), hold, new Color(0.3f, 0.9f, 1f), "Hold E: roof");
            else if (!RestartQTE.Active) GUI.Label(new Rect(cx - 150, Screen.height * 0.8f - 20, 400, 20), "DISABLED   R restart | E door | hold E roof");
        }
        if (Time.time < messageUntil) GUI.Label(new Rect(cx - 200, 60, 400, 25), message);
    }
}
