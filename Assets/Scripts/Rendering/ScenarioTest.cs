using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

// Automated gameplay checks in a built player. Starts itself when launched with -scenario <name>:
//   Bench.exe -scenario tow -screen-fullscreen 0 -screen-width 1280 -screen-height 720
// Writes scenario_<name>.txt next to the executable (a timeline plus PASS / FAIL lines) and quits.
//   tow      : the player's car near the top traffic layer is EMP'd with police around; the tow must
//              reach a surface in time while traffic routes round the red cone.
//   push     : the player's car shoves a civilian off its line; it must be pulled back.
//   district : spawn sanity, traffic lights obeyed (no street car enters a box on red), no gridlock,
//              sky traffic stays on its lanes.
//   combat   : officer health and ragdolls, burn marks, ignition chains, guard and jab, gun pickup,
//              trunks, grenades (ScenarioCombat.cs).
public partial class ScenarioTest : MonoBehaviour
{
    readonly StringBuilder log = new StringBuilder();
    string scenarioName;
    float t0;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        var args = System.Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] != "-scenario") continue;
            var go = new GameObject("ScenarioTest");
            DontDestroyOnLoad(go);
            go.AddComponent<ScenarioTest>().scenarioName = args[i + 1];
            PlayerWeapon.ForceStartWithGun = args[i + 1] != "combat"; // the older scenarios expect a gun
            return;
        }
    }

    IEnumerator Start()
    {
        Application.runInBackground = true; // keep going when the window loses focus
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = 60;
        // -scene <name>: run in another scene of the build.
        var argv = System.Environment.GetCommandLineArgs();
        for (int i = 0; i < argv.Length - 1; i++)
            if (argv[i] == "-scene" && UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != argv[i + 1])
            {
                UnityEngine.SceneManagement.SceneManager.LoadScene(argv[i + 1]);
                yield return null; yield return null;
            }
        t0 = Time.time;
        for (int i = 0; i + 1 < argv.Length; i++) if (argv[i] == "-skinfeatures") BodySDF.SkinFeatures = argv[i + 1] != "off";
        Log($"scenario {scenarioName}, scene {UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}{(BodySDF.SkinFeatures ? "" : ", skin features OFF")}");
        yield return new WaitForSeconds(3f); // traffic settles
        switch (scenarioName)
        {
            case "tow": yield return Tow(); break;
            case "push": yield return Push(); break;
            case "district": yield return District(); break;
            case "shots": yield return Shots(); break;
            case "peds": yield return Peds(); break;
            case "figures": yield return Figures(); break;
            case "damage": yield return DamageTest(); break;
            case "fx": yield return FxTest(); break;
            case "aim": yield return AimTest(); break;
            case "tgun": yield return TGunTest(); break;
            case "bodies": yield return BodiesTest(); break;
            case "skin": yield return SkinTest(); break;
            case "anim": yield return AnimTest(); break;
            case "face": yield return FaceTest(); break;
            case "pool": yield return PoolTest(); break;
            case "feet": yield return FeetTest(); break;
            case "hands": yield return HandsTest(); break;
            case "seats": yield return SeatsTest(); break;
            case "daynight": yield return DayNightTest(); break;
            case "garments": yield return GarmentsTest(); break;
            case "dressed": yield return DressedTest(); break;
            case "coat": yield return CoatTest(); break;
            case "lab": yield return LabTest(); break;
            case "heads": yield return HeadsTest(); break;
            case "headsfat": yield return HeadsTest(LowDexRows(), false); break;
            case "facemetrics": yield return FaceMetricsRun(); break;
            case "facesanity": { bool ok = FaceSanity.Run(out string rep); foreach (var l in rep.Split((char)10)) if (l.Length > 0) Log(l.TrimEnd()); yield return null; break; }
            case "lighting": yield return LightingTest(); break;
            case "combat": yield return CombatTest(); break;
            default: Log($"FAIL unknown scenario {scenarioName}"); break;
        }
        Finish();
    }

    // Face Metrics Fix Step 0: the measurements (FaceMetrics) for every lineup head and fill level; the table
    // goes to the log, the JSON next to it (face_metrics.json). -metricsonly <label part>, -metricseeds <n>.
    IEnumerator FaceMetricsRun()
    {
        var args = System.Environment.GetCommandLineArgs();
        string only = null; int seeds = 20;
        for (int i = 0; i + 1 < args.Length; i++)
        {
            if (args[i] == "-metricsonly") only = args[i + 1].Replace('_', ' ');   // (underscores for spaces)
            if (args[i] == "-metricseeds") int.TryParse(args[i + 1], out seeds);
            // (Resolution trials: cell sizes in mm; triangle budgets scale with the cell's area.)
            var mr = BodyRules.Default.mesh;
            if (args[i] == "-headcell" && float.TryParse(args[i + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float hc))
            { float r = mr.headCell / (hc * 0.001f); mr.headTriangles = Mathf.RoundToInt(mr.headTriangles * r * r); mr.headCell = hc * 0.001f; }
            if (args[i] == "-bodycell" && float.TryParse(args[i + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float bc))
            { float r = mr.cell / (bc * 0.001f); mr.targetTriangles = Mathf.RoundToInt(mr.targetTriangles * r * r); mr.cell = bc * 0.001f; }
        }
        Log($"mesh: body cell {BodyRules.Default.mesh.cell * 1000f:0.0} mm ({BodyRules.Default.mesh.targetTriangles} triangles), head cell {BodyRules.Default.mesh.headCell * 1000f:0.0} mm ({BodyRules.Default.mesh.headTriangles})");
        yield return null;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        FaceMetrics.Run(out string table, out string json, seeds, only, m => Debug.Log("FACEMETRICS " + m));
        foreach (var l in table.Split((char)10)) if (l.Length > 0) Log(l.TrimEnd());
        Log($"face metrics took {sw.Elapsed.TotalSeconds:0} s");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(Application.dataPath), "face_metrics.json"), json);
    }

    void Log(string line)
    {
        line = $"[{Time.time - t0,6:0.0}] {line}";
        log.AppendLine(line);
        Debug.Log("SCENARIO " + line);
    }

    void Finish()
    {
        string dir = Path.GetDirectoryName(Application.dataPath);
        File.WriteAllText(Path.Combine(dir, $"scenario_{scenarioName}.txt"), log.ToString());
        Application.Quit();
    }

    // Highest lane car (civilian, with a driver) that's in Lane mode.
    static FlyingVehicle PickTopCar()
    {
        FlyingVehicle best = null;
        foreach (var v in FlyingVehicle.Active)
        {
            if (v.path == null || v.Mode != FlightMode.Lane || !v.hasDriver || v.GetComponent<PoliceDriver>() != null) continue;
            if (best == null || v.GridLayer > best.GridLayer) best = v;
        }
        return best;
    }

    // ---------- tow ----------

    IEnumerator Tow()
    {
        var fpc = FirstPersonController.Instance;
        var dispatch = PoliceDispatch.Ensure();
        var car = PickTopCar();
        if (fpc == null || car == null) { Log("FAIL no player or no lane car"); yield break; }
        car.EjectDriver(-1);
        car.Enter(fpc);
        Log($"player in {car.name}, layer {car.GridLayer}, y {car.transform.position.y:0}");

        dispatch.DebugStartPursuit(2);
        // Let units arrive (spawned 300-500 m out).
        float wait = 0f;
        while (wait < 25f && NearestPolice(car) > 60f) { wait += 0.5f; yield return new WaitForSeconds(0.5f); }
        Log($"nearest police {NearestPolice(car):0} m after {wait:0.0} s, pursuing {dispatch.Pursuing.Count}");

        car.Disable(false);
        float start = Time.time, towStart = -1f, landed = -1f;
        float startY = car.transform.position.y;
        int maxStuck = 0, shifted = 0;
        var shiftedCars = new HashSet<FlyingVehicle>();
        while (Time.time - start < 90f)
        {
            yield return new WaitForSeconds(1f);
            if (car == null) { Log("FAIL car destroyed"); yield break; }
            if (car.Towed && towStart < 0f) towStart = Time.time - start;
            int attached = 0;
            foreach (var u in dispatch.Pursuing) if (u != null && u.Car.AttachHost == car) attached++;
            int stuck = 0;
            var cone = StopCone.Active.Count > 0 ? StopCone.Active[0] : null;
            foreach (var v in FlyingVehicle.Active)
            {
                if (v == car || v.GetComponent<PoliceDriver>() != null || v.path == null) continue;
                if (v.LaneLevel != v.startLevel && shiftedCars.Add(v)) shifted++;
                if (cone != null && cone.BlocksTraffic(v.transform.position, 8f, 5f) && v.Velocity.magnitude < 1f) stuck++;
            }
            // Dockers: assigned ones ignore the car until attached.
            var dock = new StringBuilder();
            foreach (var u in dispatch.Pursuing)
                if (u != null && (u.Car.IgnoreCar == car || u.Car.AttachHost == car))
                    dock.Append($" [{Vector3.Distance(u.transform.position, car.transform.position):0.0} m, v {u.Car.Velocity.magnitude:0.0}, {(u.Car.AttachHost == car ? "attached" : "docking")}]");
            maxStuck = Mathf.Max(maxStuck, stuck);
            Log($"y {car.transform.position.y,7:0.0}  towed {car.Towed}  attached {attached}  onSurface {car.OnSurface}  " +
                $"cone {(cone != null)}  stuckInBand {stuck}  shiftedSoFar {shifted}  dockers{dock}");
            if (car.OnSurface) { landed = Time.time - start; break; }
        }
        Log($"descended {startY - car.transform.position.y:0} m; tow started at {towStart:0.0} s; landed at {landed:0.0} s; " +
            $"max stopped civilians near cone {maxStuck}; civilians that shifted level {shifted}");
        Log(landed > 0f ? "PASS tow reached a surface" : "FAIL tow did not reach a surface in 90 s");
        Log(shifted > 0 ? "PASS traffic shifted level round the stop" : "WARN no traffic level shifts seen");
    }

    float NearestPolice(FlyingVehicle car)
    {
        float best = float.MaxValue;
        var d = PoliceDispatch.Instance;
        if (d == null) return best;
        foreach (var u in d.Pursuing) if (u != null) best = Mathf.Min(best, Vector3.Distance(u.transform.position, car.transform.position));
        return best;
    }

    // ---------- district ----------

    IEnumerator District()
    {
        var fpc = FirstPersonController.Instance;
        if (fpc == null) { Log("FAIL no player"); yield break; }
        Log($"player at y {fpc.transform.position.y:0.0} (start plaza 180)");
        Log(fpc.transform.position.y > 175f && fpc.transform.position.y < 185f ? "PASS player standing on the start plaza" : "FAIL player not on the plaza");
        Log($"signals {TrafficSignal.All.Count}, cars {FlyingVehicle.Active.Count}");

        var wasInside = new Dictionary<FlyingVehicle, bool>();
        var stillSince = new Dictionary<FlyingVehicle, float>();
        int redEntries = 0, entries = 0, offLaneMax = 0, stoppedAtRedMax = 0;
        float longestStill = 0f;
        for (float t = 0f; t < 90f; t += 0.25f)
        {
            yield return new WaitForSeconds(0.25f);
            int offLane = 0, stoppedAtRed = 0;
            foreach (var v in FlyingVehicle.Active)
            {
                if (v.path == null || v.IsOccupied || v.GetComponent<PoliceDriver>() != null) continue;
                if (v.Mode != FlightMode.Lane) offLane++;
                if (v.GridLayer != 0) continue;
                // Street car: entering an intersection on red?
                Vector3 p = v.transform.position, f = v.transform.forward;
                bool inside = false;
                foreach (var sgl in TrafficSignal.All)
                {
                    if (!sgl.Contains(p)) continue;
                    inside = true;
                    if (!wasInside.TryGetValue(v, out bool was) || !was)
                    {
                        entries++;
                        var axis = Mathf.Abs(f.z) >= Mathf.Abs(f.x) ? TrafficSignal.Axis.NorthSouth : TrafficSignal.Axis.EastWest;
                        if (sgl.State(axis) == TrafficSignal.Light.Red) redEntries++;
                    }
                }
                wasInside[v] = inside;
                float speed = v.Velocity.magnitude;
                if (speed < 0.5f)
                {
                    if (!stillSince.ContainsKey(v)) stillSince[v] = Time.time;
                    longestStill = Mathf.Max(longestStill, Time.time - stillSince[v]);
                    foreach (var sgl in TrafficSignal.All)
                        if (sgl.Contains(p, 25f) && !sgl.Contains(p)) { stoppedAtRed++; break; }
                }
                else stillSince.Remove(v);
            }
            offLaneMax = Mathf.Max(offLaneMax, offLane);
            stoppedAtRedMax = Mathf.Max(stoppedAtRedMax, stoppedAtRed);
            if (Mathf.Repeat(t, 15f) < 0.01f)
                Log($"t {t:0}: street entries {entries} (on red {redEntries}), waiting at lights now {stoppedAtRed}, off-lane cars {offLane}, longest still {longestStill:0} s");
        }
        // Where the long-standing street cars and the off-lane cars are.
        foreach (var kv in stillSince)
            if (kv.Key != null && Time.time - kv.Value > 30f)
                Log($"  still {Time.time - kv.Value:0} s: {kv.Key.name} at {kv.Key.transform.position:F0} heading {kv.Key.transform.forward:F1} mode {kv.Key.Mode}");
        int listed = 0;
        foreach (var v in FlyingVehicle.Active)
            if (v.path != null && !v.IsOccupied && v.Mode != FlightMode.Lane && v.GetComponent<PoliceDriver>() == null && listed++ < 12)
                Log($"  off lane: {v.name} ({v.path.name}) at {v.transform.position:F0} mode {v.Mode} speed {v.Velocity.magnitude:0}");
        Log($"street intersection entries {entries}, on red {redEntries}; most waiting at lights {stoppedAtRedMax}; " +
            $"longest a street car stood still {longestStill:0} s; most sky/street cars off their lanes {offLaneMax}");
        Log(entries > 0 && redEntries == 0 ? "PASS no street car entered an intersection on red" : $"FAIL red-light entries {redEntries} of {entries}");
        Log(stoppedAtRedMax > 0 ? "PASS cars queue at red lights" : "WARN no car seen waiting at a light");
        Log(longestStill < 60f ? "PASS no gridlock (nobody stood still a minute)" : "FAIL a street car stood still over a minute");
    }

    // ---------- pedestrians ----------

    // On the sidewalk by the avenue / south cross street corner: crowd counts, near-tier movement, waits
    // at crosswalks, nobody falling through the world, a flee reaction, frame time.
    IEnumerator Peds()
    {
        var fpc = FirstPersonController.Instance;
        var ps = PedestrianSystem.Instance;
        var graph = FindAnyObjectByType<WalkGraph>();
        if (fpc == null || ps == null || graph == null) { Log($"FAIL setup (player {fpc != null}, peds {ps != null}, graph {graph != null})"); yield break; }
        Log($"walk graph {graph.Count} nodes");
        fpc.PlaceAt(new Vector3(27f, 1f, -92f), Quaternion.LookRotation(Vector3.back));
        yield return new WaitForSeconds(5f);

        int frames = 0; float time = 0f, worst = 0f;
        int maxNear = 0, waitingSeen = 0, fallen = 0;
        var moved = new Dictionary<PedestrianSystem.Ped, Vector3>();
        for (float t = 0f; t < 30f; t += Time.deltaTime)
        {
            yield return null;
            frames++; time += Time.unscaledDeltaTime; worst = Mathf.Max(worst, Time.unscaledDeltaTime);
            maxNear = Mathf.Max(maxNear, ps.NearAgents.Count);
            if (frames % 30 != 0) continue;
            foreach (var p in ps.All)
            {
                if (p.state == PedestrianSystem.State.Waiting) waitingSeen++;
                if (p.pos.y < -2f) fallen++;
            }
            if (frames == 30) foreach (var p in ps.NearAgents) moved[p] = p.pos;
        }
        int active = 0, near = ps.NearAgents.Count;
        foreach (var p in ps.All) if (p.state != PedestrianSystem.State.Gone && p.goal >= 0) active++;
        int movedCount = 0;
        foreach (var kv in moved) if (kv.Key.state != PedestrianSystem.State.Gone && (kv.Key.pos - kv.Value).magnitude > 5f) movedCount++;
        Log($"active {active} of {ps.All.Count}, near now {near} (max {maxNear}); near agents that walked > 5 m in 30 s: {movedCount} of {moved.Count}; " +
            $"waiting-at-crosswalk samples {waitingSeen}; fallen samples {fallen}; frame avg {1000f * time / frames:0.0} ms, worst {1000f * worst:0} ms");

        // Danger: everyone near a spot should run.
        PedestrianSystem.Ped target = null;
        foreach (var p in ps.NearAgents) { target = p; break; }
        int fled = 0, around = 0;
        if (target != null)
        {
            Vector3 at = target.pos;
            PedestrianSystem.ReportDanger(at);
            yield return null; yield return null;
            foreach (var p in ps.NearAgents)
            {
                if ((p.pos - at).sqrMagnitude > 25f * 25f) continue;
                around++;
                if (p.state == PedestrianSystem.State.Fleeing) fled++;
            }
        }
        Log($"danger: {fled} of {around} near pedestrians within 25 m fled");
        Log(active > ps.budget * 0.8f ? "PASS crowd populated" : "FAIL crowd thin");
        Log(maxNear > 10 && movedCount > moved.Count / 2 ? "PASS near pedestrians walk" : "FAIL near pedestrians not walking");
        Log(waitingSeen > 0 ? "PASS pedestrians wait at crosswalks" : "WARN no crosswalk waits seen");
        Log(fallen == 0 ? "PASS nobody fell through" : "FAIL pedestrians fell");
        Log(around > 0 && fled == around ? "PASS pedestrians flee danger" : "FAIL flee reaction");
    }

    // ---------- car damage, fire, explosions, laser ----------

    IEnumerator DamageTest()
    {
        var fpc = FirstPersonController.Instance;
        string dir = Path.GetDirectoryName(Application.dataPath);
        var cam = Camera.main;
        IEnumerator ShotAt(string name, Vector3 target, Vector3 from)
        {
            FlyingVehicle.CameraOverride = true;
            cam.transform.SetParent(null);
            cam.transform.SetPositionAndRotation(from, Quaternion.LookRotation(target - from));
            yield return null;
            cam.transform.SetPositionAndRotation(from, Quaternion.LookRotation(target - from));
            yield return null;
            ScreenCapture.CaptureScreenshot(Path.Combine(dir, "dmg_" + name + ".png"));
            yield return null;
            yield return null;
            FlyingVehicle.CameraOverride = false;
        }
        var pink = FlyingVehicle.PinkCar;
        Log(pink != null ? $"pink car present: {pink.name}, top speed {pink.middleLaneMaxSpeed:0} m/s, turn {pink.headingTurnRate:0} deg/s"
                         : "no pink car this run (1 in 300 per traffic car)");
        var body = FlyingVehicle.Active[0].GetComponentInChildren<BoxCollider>();
        Log($"car body size {Vector3.Scale(body.size, body.transform.lossyScale)}");

        // 1. A real crash: the player's car into a tower face at 40 m/s.
        var car = PickTopCar();
        car.EjectDriver(-1);
        car.Enter(fpc);
        float h0 = car.Health.Health;
        Vector3 wallDir = Vector3.zero; float wallDist = 0f;
        foreach (var d in new[] { Vector3.right, Vector3.left, Vector3.forward, Vector3.back })
            if (Physics.Raycast(car.transform.position, d, out var wh, 60f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)
                && wh.collider.GetComponentInParent<FlyingVehicle>() == null) { wallDir = d; wallDist = wh.distance; break; }
        if (wallDir != Vector3.zero)
        {
            car.DebugPlace(car.transform.position, Quaternion.LookRotation(wallDir), wallDir * 40f);
            for (int i = 0; i < 40; i++) { car.DebugThrottle = 1f; car.DebugAimAt(car.transform.position + wallDir * 10f); yield return new WaitForSeconds(0.05f); }
            car.DebugThrottle = 0f;
            Log($"crash into a wall {wallDist:0} m away at 40 m/s: health {h0:0} -> {car.Health.Health:0} ({car.Health.Fraction:P0})");
            Log(car.Health.Health < h0 ? "PASS crashing damages the car" : "FAIL no crash damage");
        }
        else Log("WARN no wall found for the crash test");
        yield return ShotAt("crashed", car.transform.position, car.transform.position + new Vector3(8f, 4f, 8f));
        car.Health.Init(car.Health.maxHealth); // repaired, so it doesn't go critical and explode with us inside

        // 2. Damage stages on a traffic car.
        var stage = PickNearLaneCar(car.transform.position, 250f);
        if (stage != null)
        {
            stage.Health.Damage(stage.Health.maxHealth * 0.45f, stage.transform.position + stage.transform.right * 2f, stage.transform.right);
            yield return new WaitForSeconds(0.3f);
            yield return ShotAt("55pct", stage.transform.position, stage.transform.position + stage.transform.right * 10f + Vector3.up * 3f);
            stage.Health.Damage(stage.Health.maxHealth * 0.38f, stage.transform.position + stage.transform.forward * 4f, stage.transform.forward);
            float y0 = stage.transform.position.y;
            yield return new WaitForSeconds(1.5f);
            Log($"critical: health {stage.Health.Fraction:P0}, state {stage.Health.Current}, mode {stage.Mode}, fell {y0 - stage.transform.position.y:0.0} m in 1.5 s");
            Log(stage.Health.Critical || stage.Health.Wrecked ? "PASS critical: nosedive" : "FAIL not critical");
            yield return ShotAt("critical", stage.transform.position, stage.transform.position + stage.transform.right * 12f + Vector3.up * 4f);
        }

        // 3. Chain reaction: blow up a car in the middle of traffic.
        var bomb = PickNearLaneCar(car.transform.position, 400f, 3);
        if (bomb != null)
        {
            var hp = new Dictionary<FlyingVehicle, float>();
            foreach (var v in FlyingVehicle.Active) if (v.Health != null) hp[v] = v.Health.Health;
            Vector3 at = bomb.transform.position;
            int frames = 0; float worst = 0f, total = 0f;
            bomb.Health.Damage(10000f, at, Vector3.up);
            yield return ShotAt("explosion", at, at + new Vector3(25f, 10f, 25f));
            for (float t = 0f; t < 10f; t += Time.unscaledDeltaTime)
            {
                yield return null;
                frames++; total += Time.unscaledDeltaTime; worst = Mathf.Max(worst, Time.unscaledDeltaTime);
            }
            int damaged = 0, wrecks = 0, burning = 0;
            foreach (var v in FlyingVehicle.Active)
            {
                if (v == bomb || v.Health == null) continue;
                if (hp.TryGetValue(v, out float before) && v.Health.Health < before - 1f) damaged++;
                if (v.Health.Wrecked) wrecks++;
                if (v.Health.Burning) burning++;
            }
            Log($"chain: {damaged} other cars damaged, {wrecks} wrecks, {burning} burning; fire patches {FireSystem.PatchCount}, chunks {FireSystem.ChunkCount}; " +
                $"frames avg {1000f * total / Mathf.Max(1, frames):0.0} ms, worst {1000f * worst:0} ms");
            Log(damaged > 0 ? "PASS explosion damages neighbours" : "WARN explosion hit nothing (sparse spot)");
            Log(FireSystem.PatchCount > 0 ? "PASS fire chunks landed as patches" : "FAIL no fire patches");
            yield return ShotAt("aftermath", at + Vector3.down * 20f, at + new Vector3(30f, 15f, 30f));
        }

        // 4. Fire pooling on a car ignites it: pour onto the player's (stopped, repaired) car.
        car.Health.Init(car.Health.maxHealth);
        // Held still (the AI would drive off with it: the chunks take ~0.6 s to fall onto a 4 m roof).
        Vector3 holdPos = car.transform.position; Quaternion holdRot = car.transform.rotation;
        for (float t = 0f, next = 0f; t < 3f; t += Time.deltaTime)
        {
            car.DebugPlace(holdPos, holdRot, Vector3.zero);
            if (t >= next && next < 1f) { FireSystem.Spray(holdPos + Vector3.up * (car.BodyHalfExtents.y + 1.5f), Vector3.zero, 3, 0.1f, 0.4f); next += 0.25f; }
            yield return null;
        }
        Log($"poured fire on the player's car: burning {car.Health.Burning}, health {car.Health.Fraction:P0}, player still driving {FlyingVehicle.Driven == car}");
        Log(car.Health.Burning ? "PASS fire pooled on a car ignites it" : car.Health.Fraction < 0.99f ? "WARN fire hurt the car but didn't ignite it" : "FAIL fire did nothing");
        yield return ShotAt("burning", car.transform.position, car.transform.position + new Vector3(10f, 5f, 10f));

        // 5. Laser: lethal force on the player's car (repaired, not burning); wait for police to close in.
        // A fresh car for it (the burning one may be lost), next to a police car (45 m off its side,
        // same height), so the test doesn't depend on chases.
        PoliceDriver near = null;
        foreach (var pd in FindObjectsByType<PoliceDriver>(FindObjectsSortMode.None))
            if (pd.Car.Health == null || pd.Car.Health.Current == VehicleHealth.State.Ok) { near = pd; break; }
        if (near == null) { Log("WARN no police car left for the laser test"); yield break; }
        if (FlyingVehicle.Driven != null) FlyingVehicle.Driven.Exit(false);
        car = PickNearLaneCar(near.transform.position, 2000f, 2);
        if (car == null) { Log("WARN no car for the laser test"); yield break; }
        car.EjectDriver(-1);
        car.Enter(FirstPersonController.Instance);
        // A spot 45 m away with a clear line to the police car (not inside or behind a building).
        Vector3 spot = near.transform.position + near.transform.right * 45f;
        foreach (var off in new[] { near.transform.right, -near.transform.right, near.transform.forward, -near.transform.forward,
                                    near.transform.right + Vector3.up * 0.4f, -near.transform.right + Vector3.up * 0.4f })
        {
            Vector3 c = near.transform.position + off.normalized * 45f;
            if (PoliceDispatch.LineOfSight(near.transform.position + Vector3.up * 2f, c) &&
                !Physics.CheckBox(c, car.BodyHalfExtents + Vector3.one, Quaternion.identity, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            { spot = c; break; }
        }
        car.DebugPlace(spot, near.transform.rotation, Vector3.zero);
        // Drive this police car's laser at the player's car directly for 4 s (no chase AI involved).
        float hl = car.Health.Health;
        for (float t = 0f; t < 4f; t += Time.deltaTime)
        {
            near.UpdateLaser(car);
            yield return null;
        }
        near.StopLaser();
        Log($"laser: player car health {hl:0} -> {car.Health.Health:0}, state {car.Health.Current}; beam frames {PoliceDriver.LaserFireFrames}, " +
            $"on target {PoliceDriver.LaserHitFrames}; last beam hit {PoliceDriver.LastLaserHit}");
        Log(PoliceDriver.LaserHitFrames > 0 && car.Health.Health < hl ? "PASS police laser damages the car" : "FAIL laser never hit");
        var cop = near;
        if (cop != null) yield return ShotAt("laser", car.transform.position, (car.transform.position + cop.transform.position) * 0.5f + Vector3.up * 15f + Vector3.Cross(cop.transform.position - car.transform.position, Vector3.up).normalized * 25f);
    }

    PoliceDriver NearestPoliceCar(FlyingVehicle car)
    {
        PoliceDriver best = null; float bd = float.MaxValue;
        var d = PoliceDispatch.Instance;
        if (d == null) return null;
        foreach (var u in d.Pursuing) if (u != null && Vector3.Distance(u.transform.position, car.transform.position) < bd) { bd = Vector3.Distance(u.transform.position, car.transform.position); best = u; }
        return best;
    }

    // Every effect in front of the camera, one screenshot each.
    IEnumerator FxTest()
    {
        var cam = Camera.main;
        cam.transform.SetParent(null);
        var fpc = FirstPersonController.Instance;
        if (fpc != null) fpc.gameObject.SetActive(false);
        Vector3 at = cam.transform.position + cam.transform.forward * 8f;
        string dir = Path.GetDirectoryName(Application.dataPath);
        for (int i = 0; i < 20; i++) Effects.Puff(at + Random.insideUnitSphere, Vector3.zero, 0.8f, 3f);
        for (float t = 0f; t < 0.5f; t += Time.deltaTime) { Effects.Flame(at + Vector3.right * 2f, 1.5f); Effects.Scorch(at + Vector3.left * 2f + Vector3.down, 1f); yield return null; }
        Effects.Flame(at + Vector3.right * 2f, 1.5f);
        ScreenCapture.CaptureScreenshot(Path.Combine(dir, "fx_puff_flame.png"));
        yield return null;
        Effects.Flash(at, 3f); Effects.Ring(at, 6f);
        yield return new WaitForSeconds(0.12f);
        ScreenCapture.CaptureScreenshot(Path.Combine(dir, "fx_flash.png"));
        yield return null;
        Log($"hologram shader found: {Shader.Find("FPS/Hologram") != null}, lit found: {Shader.Find("Universal Render Pipeline/Lit") != null}");
    }

    static FlyingVehicle PickNearLaneCar(Vector3 near, float within, int skip = 0)
    {
        foreach (var v in FlyingVehicle.Active)
        {
            if (v.path == null || v.Mode != FlightMode.Lane || v.IsOccupied || v.GetComponent<PoliceDriver>() != null) continue;
            if (v.Health == null || v.Health.Current != VehicleHealth.State.Ok) continue;
            if ((v.transform.position - near).sqrMagnitude > within * within) continue;
            if (skip-- > 0) continue;
            return v;
        }
        return null;
    }

    // ---------- sausage hands ----------

    IEnumerator HandsTest()
    {
        string dir = Path.GetDirectoryName(Application.dataPath);
        var origin = new Vector3(0f, 1500f, 0f);
        var specs = new (string, CharacterSheet)[]
        {
            ("average man", new CharacterSheet(Sex.Male, 10, 10, 10, 1000)), ("STR 20", new CharacterSheet(Sex.Male, 20, 10, 10, 1001)),
            ("heavy woman (DEX 1)", new CharacterSheet(Sex.Female, 10, 10, 1, 1002)), ("ripped (DEX 20)", new CharacterSheet(Sex.Male, 10, 10, 20, 1003)),
        };
        var figs = new List<(string name, CharacterFigure f, FigureAnimator a)>();
        bool apart = true, closed = true; int worstHand = 0;
        for (int i = 0; i < specs.Length; i++)
        {
            var (name, sh) = specs[i];
            var go = new GameObject(name);
            go.transform.position = origin + new Vector3(i * 1.0f, 0f, 0f);
            go.transform.rotation = Quaternion.Euler(0f, 180f, 0f);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var f = CharacterFigure.BuildGenerated(go.transform, sh, CharacterFigure.Role.Civilian, true, false, null, null, Outfit.None()); // (naked: the hole count needs the whole skin)
            float ms = (float)sw.Elapsed.TotalMilliseconds;
            var an = go.AddComponent<FigureAnimator>(); an.Grounded = true; an.LookYaw = 180f;
            figs.Add((name, f, an));
            var plan = f.Plan;
            // Fingers apart: the SDF between neighbouring fingers (half way along) is outside the skin.
            using (var sdf = new BodySDF(plan))
            {
                var arm = plan.arms[0];
                float gapMin = float.MaxValue;
                for (int d = 1; d < 4; d++)
                {
                    int d0 = arm.digits[d], d1 = arm.digits[d + 1];
                    Vector3 m = ((sdf.start[d0] + sdf.end[d0]) * 0.5f + (sdf.start[d1] + sdf.end[d1]) * 0.5f) * 0.5f; m += sdf.bind[d0].MultiplyVector(plan.bones[d0].shapeOffset);
                    gapMin = Mathf.Min(gapMin, sdf.Kernel.Skin(m));
                }
                if (gapMin <= 0f) apart = false;
                var hb = plan.bones[arm.end];
                float midLen = 0f; foreach (int db in arm.digitBones) if (plan.bones[db].name.StartsWith("Middle")) midLen += plan.bones[db].length;
                var mid = plan.bones[arm.digits[2]];
                // Hand triangles (both hands) and holes in the whole mesh.
                // (Body and head welded by position: the split shares the vertices on the seam.)
                var tris = new List<int>(); var weld = new Dictionary<Vector3, int>();
                int handTris = 0;
                foreach (var mesh in new[] { f.Asset.body, f.Asset.head })
                {
                    var uv1 = new List<Vector2>(); mesh.GetUVs(1, uv1);
                    var vs = mesh.vertices;
                    for (int sm = 0; sm < mesh.subMeshCount; sm++)
                    {
                        var t = mesh.GetTriangles(sm);
                        foreach (int v in t)
                        {
                            if (!weld.TryGetValue(vs[v], out int id)) { id = weld.Count; weld[vs[v]] = id; }
                            tris.Add(id);
                        }
                        for (int k = 0; k < t.Length; k += 3) if ((BoneKind)(int)uv1[t[k]].x == BoneKind.Hand) handTris++;
                    }
                }
                BodyMesher.CountEdges(tris, out int holes, out int nonMan);
                if (holes > 0) closed = false;
                worstHand = Mathf.Max(worstHand, handTris);
                Log($"{name}: {plan.bones.Count} bones ({plan.arms.Count * 5} digits, {arm.digitBones.Count * plan.arms.Count} segments), hand {hb.length * 100f:0.0} cm long, palm {hb.Outer.y * BodyRules.Default.hands.palmWidth * 100f:0.0} cm wide, " +
                    $"middle finger {midLen * 100f:0.0} x {mid.girth.x * 200f:0.0} cm, narrowest finger gap {gapMin * 1000f:0.0} mm; " +
                    $"{f.Asset.triangles} tris ({handTris} hands), holes {holes}, non-manifold {nonMan}; generated in {ms:0} ms");
            }
            yield return null;
        }
        Log(apart ? "PASS fingers are separate sausages" : "FAIL fingers fused");
        Log(closed ? "PASS body + hand meshes closed (no holes at the wrist cut)" : "FAIL holes in the mesh");

        // Open (lowered: relaxed) vs fist (guard): fingertip distance from the wrist.
        float TipReach(CharacterFigure f)
        {
            float sum = 0f; int n = 0;
            foreach (var (joint, hand, _) in f.Digits)
            {
                if (hand != 0) continue;
                var spec = f.Plan.bones[f.Plan.Index(joint.name)];
                sum += (joint.TransformPoint(spec.dir * spec.length) - f.WristL.position).magnitude; n++;
            }
            return sum / Mathf.Max(1, n);
        }
        var f0 = figs[0].f; var a0 = figs[0].a;
        a0.ArmMode = FigureAnimator.Arms.Lowered;
        yield return new WaitForSeconds(0.5f);
        float relaxed = TipReach(f0);
        foreach (var (j, _, _) in f0.Digits) j.localRotation = Quaternion.identity;
        float open = TipReach(f0);
        a0.ArmMode = FigureAnimator.Arms.Guard;
        yield return new WaitForSeconds(0.5f);
        float fist = TipReach(f0);
        Log($"fingertips from the wrist: open {open * 100f:0.0} cm, relaxed {relaxed * 100f:0.0} cm, fist {fist * 100f:0.0} cm");
        Log(fist < open * 0.75f && relaxed < open && relaxed > fist ? "PASS hands curl: open > relaxed > fist" : "FAIL hand curl");

        // Screenshots: lit close-ups of open, relaxed and fist hands, and the lineup.
        var lights = new List<GameObject>();
        var lg = new GameObject("HandKey"); var l = lg.AddComponent<Light>(); l.type = LightType.Directional; l.intensity = 1.2f;
        lg.transform.rotation = Quaternion.LookRotation(new Vector3(0.3f, -0.5f, 1f)); lights.Add(lg);
        var cam = Camera.main;
        cam.transform.SetParent(null);
        FlyingVehicle.CameraOverride = true;
        float fov = cam.fieldOfView;
        IEnumerator Shot(string name, Vector3 at, Vector3 from, float fv)
        {
            cam.fieldOfView = fv;
            cam.transform.SetPositionAndRotation(from, Quaternion.LookRotation(at - from));
            yield return new WaitForEndOfFrame(); yield return new WaitForEndOfFrame();
            ScreenCapture.CaptureScreenshot(Path.Combine(dir, $"hands_{name}.png"));
            yield return null; yield return null;
        }
        foreach (var x in figs) x.a.ArmMode = FigureAnimator.Arms.Lowered;
        yield return new WaitForSeconds(0.6f);
        // Relaxed, then open (animator off, digits straight): the right hand from its outer front.
        Vector3 View(CharacterFigure f) => f.transform.forward * 0.26f + f.transform.right * 0.16f + Vector3.up * 0.04f;
        for (int i = 0; i < figs.Count; i++) { var f = figs[i].f; yield return Shot("relaxed" + i, f.HandR.position, f.HandR.position + View(f), 30f); }
        foreach (var x in figs) { x.a.enabled = false; foreach (var (j, _, _) in x.f.Digits) j.localRotation = Quaternion.identity; }
        yield return null;
        for (int i = 0; i < figs.Count; i++) { var f = figs[i].f; yield return Shot("open" + i, f.HandR.position, f.HandR.position + View(f), 30f); }
        { var f = figs[0].f; yield return Shot("open_out", f.HandR.position, f.HandR.position + f.transform.right * 0.3f, 30f); }
        foreach (var x in figs) { x.a.enabled = true; x.a.ArmMode = FigureAnimator.Arms.Guard; }
        yield return new WaitForSeconds(0.6f);
        for (int i = 0; i < figs.Count; i++)
        {
            var f = figs[i].f; Vector3 h = f.HandR.position;
            yield return Shot("fist" + i, h, h + f.transform.forward * 0.28f + f.transform.right * 0.12f + Vector3.up * 0.06f, 30f);
        }
        { var f = figs[0].f; Vector3 h = f.HandR.position; yield return Shot("fist_out", h, h + f.transform.right * 0.28f + Vector3.up * 0.05f, 30f); }
        Vector3 row = origin + new Vector3(1.5f, 1.0f, 0f);
        yield return Shot("lineup", row, row + new Vector3(0f, 0.1f, -3.4f), 50f);
        cam.fieldOfView = fov;
        foreach (var g in lights) Destroy(g);
        FlyingVehicle.CameraOverride = false;
    }

    // ---------- graphics phase 1: the lighting stack ----------

    // District: light and probe counts, probe startup, then before (SSAO, city lights, probes, grain off)
    // and after shots at street level, mid level and from the air, and the frame time either way.
    IEnumerator LightingTest()
    {
        string dir = Path.GetDirectoryName(Application.dataPath);
        yield return null;
        GraphicsFeatures.RegisterLightingStack();
        var lights = CityLights.Instance; var probes = CityProbes.Instance;
        if (lights == null || probes == null) { Log("FAIL no CityLights / CityProbes in the scene"); yield break; }
        Log($"features: {string.Join(", ", System.Linq.Enumerable.Select(GraphicsFeatures.All, f => f.name))}");
        var ssaoOk = GraphicsFeatures.Get(GraphicsFeatures.Ssao);
        Log(ssaoOk ? "PASS SSAO renderer feature installed and on" : "FAIL no SSAO");
        float t0 = Time.realtimeSinceStartup;
        while (!probes.StartupDone && Time.realtimeSinceStartup - t0 < 30f) yield return null;
        Log($"probes: {probes.probes.Count}, first pass {(Time.realtimeSinceStartup - t0):0.0} s ({probes.startupPerFrame} a frame), last render call {probes.RenderMs:0.00} ms CPU");
        Log(probes.probes.Count > 10 && probes.StartupDone ? "PASS reflection probes placed and rendered" : "FAIL probes");

        var fpc = FirstPersonController.Instance;
        var cam = Camera.main;
        cam.transform.SetParent(null); FlyingVehicle.CameraOverride = true;
        if (fpc != null) fpc.enabled = false;
        var tod = TimeOfDay.Instance; if (tod != null) { tod.freeze = true; tod.SetHour(22f); }
        Vector3 mid = fpc != null ? fpc.transform.position : new Vector3(0f, 180f, 0f);
        var sig = FindAnyObjectByType<TrafficSignal>();
        Vector3 street = sig != null ? new Vector3(sig.transform.position.x, 0f, sig.transform.position.z) : Vector3.zero;
        Light lamp = null; float best = float.MaxValue;
        foreach (var l in FindObjectsByType<Light>(FindObjectsInactive.Include))
            if (l.name == "LampLight") { float d = (l.transform.position - street).sqrMagnitude; if (d < best) { best = d; lamp = l; } }
        Vector3 lampFoot = lamp != null ? new Vector3(lamp.transform.position.x, 0f, lamp.transform.position.z) : street;
        Vector3 sign = mid;
        best = float.MaxValue;
        foreach (var l in FindObjectsByType<Light>(FindObjectsInactive.Include))
            if (l.name == "SignLight") { float d = (l.transform.position - mid).sqrMagnitude; if (d < best) { best = d; sign = l.transform.position; } }
        var views = new (string, Vector3, Vector3)[]
        {
            ("lamp", lampFoot + Vector3.up * 1f, lampFoot + new Vector3(10f, 4f, 10f)),
            ("sign", sign, sign + new Vector3(14f, 6f, 14f)),
            ("street", street + new Vector3(4f, 3f, 35f), street + new Vector3(-3f, 1.7f, -22f)),
            ("mid", mid + new Vector3(0f, -4f, 40f), mid + new Vector3(0f, 1.7f, 0f)),
            ("air", mid + new Vector3(0f, -60f, 200f), mid + new Vector3(-40f, 90f, -60f)),
        };
        void Phase1(bool on)
        {
            GraphicsFeatures.Set(GraphicsFeatures.Ssao, on);
            GraphicsFeatures.Set(GraphicsFeatures.CityLightsName, on);
            GraphicsFeatures.Set(GraphicsFeatures.CityProbesName, on);
            GraphicsFeatures.Set(GraphicsFeatures.Grain, on);
        }
        IEnumerator FrameMs(System.Action<float> result)
        {
            yield return new WaitForSeconds(0.5f);
            float sum = 0f; int n = 0; float end = Time.realtimeSinceStartup + 2f;
            while (Time.realtimeSinceStartup < end) { yield return null; sum += Time.unscaledDeltaTime; n++; }
            result(sum / Mathf.Max(1, n) * 1000f);
        }
        foreach (var (name, at, from) in views)
        {
            cam.transform.SetPositionAndRotation(from, Quaternion.LookRotation(at - from));
            Phase1(true); lights.Refresh();
            // Let the nearest probes catch up with this view.
            for (int i = 0; i < 30; i++) yield return null;
            int active = lights.ActiveCount, visible = 0;
            foreach (var l in FindObjectsByType<Light>()) if (l.enabled && l.type != LightType.Directional && l.isActiveAndEnabled) visible++;
            float onMs = 0f, offMs = 0f;
            yield return FrameMs(v => onMs = v);
            yield return new WaitForEndOfFrame();
            ScreenCapture.CaptureScreenshot(Path.Combine(dir, $"lighting_{name}_after.png"));
            yield return null; yield return null;
            Phase1(false);
            yield return FrameMs(v => offMs = v);
            yield return new WaitForEndOfFrame();
            ScreenCapture.CaptureScreenshot(Path.Combine(dir, $"lighting_{name}_before.png"));
            yield return null; yield return null;
            Log($"{name}: {active} city lights on of {lights.TotalCount} ({visible} point / spot lights enabled), frame {onMs:0.00} ms with Phase 1, {offMs:0.00} ms without");
        }
        Phase1(true);
        Log(lights.TotalCount > 50 ? "PASS city lights placed and budgeted" : "FAIL too few city lights");

        // Decals (Phase 5): a close look at a poster, a graffiti and an oil stain, from their meshes.
        int decalQuads = 0;
        foreach (var n in new[] { "Poster", "Graffiti", "Grime", "Leak", "Oil", "Skid" })
        {
            var go = GameObject.Find("Decals_" + n);
            var mf = go != null ? go.GetComponent<MeshFilter>() : null;
            if (mf == null || mf.sharedMesh == null) continue;
            var mesh = mf.sharedMesh;
            decalQuads += mesh.vertexCount / 4;
            if (n != "Poster" && n != "Graffiti" && n != "Oil") continue;
            var vs = mesh.vertices; var ns = mesh.normals;
            int q = (vs.Length / 4 / 3) * 4;                                        // a quad a third of the way in
            Vector3 c = go.transform.TransformPoint((vs[q] + vs[q + 1] + vs[q + 2] + vs[q + 3]) * 0.25f);
            Vector3 nn = go.transform.TransformDirection(ns[q]);
            Vector3 from = n == "Oil" ? c + Vector3.up * 2.2f + Vector3.forward * 1.5f : c + nn * 3f + Vector3.up * 0.3f;
            cam.transform.SetPositionAndRotation(from, Quaternion.LookRotation(c - from));
            lights.Refresh();
            for (int i = 0; i < 10; i++) yield return null;
            yield return new WaitForEndOfFrame();
            ScreenCapture.CaptureScreenshot(Path.Combine(dir, $"lighting_decal_{n}.png"));
            yield return null; yield return null;
        }
        Log($"decals: {decalQuads} quads");
        Log(decalQuads > 100 ? "PASS decals scattered" : "FAIL no decals");
        if (fpc != null) fpc.enabled = true;
        FlyingVehicle.CameraOverride = false;
        if (tod != null) tod.freeze = false;
    }

    // ---------- head / face lineup (Head spec Parts C-E) ----------

    // Rows of heads, each row shot from the front and three-quarter: both sexes at DEX 1 / 10 / 20 (fat
    // softens the face), INT 1 / 20 (egg elongation), and (Part D) one part exaggerated per head through
    // the per-character face overrides. Neutral key + fill light, no outfit, so the face reads.
    // Edges used by one triangle only (holes; the wig's brow strips are closed too).
    // Holes in the face and neck: the head and body meshes welded by position (they share the split's
    // vertices), then edges used by one triangle above the shoulders.
    static int FaceHoles(CharacterFigure f, out string where)
    {
        where = "";
        if (f.Asset == null || f.Asset.head == null || f.Asset.body == null) return 0;
        var plan = f.Plan; int hi = plan.Index("Head");
        var c = HeadCanon.From(plan, hi, plan.JointPos(hi), Vector3.right, Vector3.up, Vector3.forward);
        var ids = new Dictionary<Vector3Int, int>(); var pos = new List<Vector3>();
        var use = new Dictionary<long, int>();
        void Add(Mesh m, int sub)
        {
            var v = m.vertices; var t = m.GetTriangles(sub);
            var map = new int[v.Length];
            for (int i = 0; i < v.Length; i++)
            {
                var key = new Vector3Int(Mathf.RoundToInt(v[i].x * 5000f), Mathf.RoundToInt(v[i].y * 5000f), Mathf.RoundToInt(v[i].z * 5000f));
                if (!ids.TryGetValue(key, out int id)) { id = pos.Count; ids[key] = id; pos.Add(v[i]); }
                map[i] = id;
            }
            for (int i = 0; i < t.Length; i += 3)
                for (int e = 0; e < 3; e++)
                {
                    int a = map[t[i + e]], b = map[t[i + (e + 1) % 3]];
                    if (a == b) continue;
                    long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                    use.TryGetValue(key, out int n); use[key] = n + 1;
                }
        }
        Add(f.Asset.head, 0); Add(f.Asset.body, 0);
        bool dressed = f.Asset.outfit != null && f.Asset.outfit.Count > 0;   // (skin under a collar is dropped: open by design)
        int holes = 0; var sb = new System.Text.StringBuilder();
        foreach (var kv in use)
        {
            if (kv.Value != 1 || dressed) continue;
            var p = pos[(int)(kv.Key >> 32)] - c.o;
            if (p.y < c.chin - 0.05f) continue;    // (the shoulders and below: other checks)
            if (holes < 6) sb.Append($"({p.x * 100f:0.0},{p.y * 100f:0.0},{p.z * 100f:0.0}) ");
            holes++;
        }
        // Inverted triangles (facing against their vertices' normals): culled, they look like holes.
        var hm = f.Asset.head; var hv = hm.vertices; var hn = hm.normals; var ht = hm.triangles;
        int flipped = 0; var sf = new System.Text.StringBuilder();
        for (int i = 0; i < ht.Length; i += 3)
        {
            Vector3 a0 = hv[ht[i]], b0 = hv[ht[i + 1]], c0 = hv[ht[i + 2]];
            Vector3 fn = Vector3.Cross(b0 - a0, c0 - a0);
            if (fn.sqrMagnitude < 1e-12f) continue;
            if (Vector3.Dot(fn.normalized, (hn[ht[i]] + hn[ht[i + 1]] + hn[ht[i + 2]]).normalized) < -0.2f)
            {
                var q = (a0 + b0 + c0) / 3f - c.o;
                if (flipped < 6) sf.Append($"({q.x * 100f:0.0},{q.y * 100f:0.0},{q.z * 100f:0.0}) ");
                flipped++;
            }
        }
        if (flipped > 0) { where += $"; {flipped} inverted triangles at {sf}"; holes += flipped; }
        where = sb.ToString() + "cm from the eye line's centre" + where;
        return holes;
    }

    static string lastOpen = "";
    static int OpenEdges(Mesh m)
    {
        if (m == null) return 0;
        var t = m.triangles; var use = new Dictionary<long, int>();
        for (int i = 0; i < t.Length; i += 3)
            for (int e = 0; e < 3; e++)
            {
                int a = t[i + e], b = t[i + (e + 1) % 3];
                long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                use.TryGetValue(key, out int c); use[key] = c + 1;
            }
        int open = 0; var v = m.vertices; var sb = new System.Text.StringBuilder();
        foreach (var kv in use)
            if (kv.Value == 1)
            {
                if (open < 6) { var p = v[(int)(kv.Key >> 32)]; sb.Append($"({p.x * 100f:0.0},{p.y * 100f:0.0},{p.z * 100f:0.0}) "); }
                open++;
            }
        if (open > 0) Debug.Log($"open wig edges at (cm, head joint space): {sb}");
        lastOpen = sb.ToString();
        return open;
    }

    // Face Fix Part 1: a head's canon and how its mesh (rest pose) measures against it: crown, chin and
    // width at the eye line (cm, mesh - canon), the eyes' height and spacing.
    static string CanonReport(CharacterFigure f)
    {
        if (f == null || !f.Generated || f.Asset == null || f.Asset.head == null) return "no head";
        var plan = f.Plan; int hi = plan.Index("Head");
        var c = HeadCanon.From(plan, hi, plan.JointPos(hi), Vector3.right, Vector3.up, Vector3.forward);
        var v = f.Asset.head.vertices;
        float top = float.MinValue, chin = float.MaxValue, wid = 0f;
        foreach (var p in v)
        {
            Vector3 d = p - c.o;
            top = Mathf.Max(top, d.y);
            if (Mathf.Abs(d.y) < 0.005f) wid = Mathf.Max(wid, Mathf.Abs(d.x));
            if (d.z > c.halfD * 0.3f && Mathf.Abs(d.x) < c.halfW * 0.3f) chin = Mathf.Min(chin, d.y);
        }
        Vector3 em = (f.Asset.eyeL + f.Asset.eyeR) * 0.5f - c.o;
        return $"{c}; mesh vs canon: crown {(top - c.crown) * 100f:+0.0;-0.0} cm, chin {(chin - c.chin) * 100f:+0.0;-0.0} cm, half width at eyes {(wid - c.halfW) * 100f:+0.0;-0.0} cm; " +
               $"eyes {em.y * 100f:+0.0;-0.0} cm off the eye line, {(f.Asset.eyeR - f.Asset.eyeL).magnitude * 0.5f / c.eyeWidth:0.00} eye-widths from the midline; body {plan.height:0.00} m";
    }

    public static List<(string label, CharacterSheet sheet)>[] HeadRows()
    {
        var rows = new List<List<(string, CharacterSheet)>>
        {
            new List<(string, CharacterSheet)> { ("M DEX1", new CharacterSheet(Sex.Male, 10, 10, 1, 3101)), ("M DEX10", new CharacterSheet(Sex.Male, 10, 10, 10, 3102)), ("M DEX20", new CharacterSheet(Sex.Male, 10, 10, 20, 3103)) },
            new List<(string, CharacterSheet)> { ("F DEX1", new CharacterSheet(Sex.Female, 10, 10, 1, 3201)), ("F DEX10", new CharacterSheet(Sex.Female, 10, 10, 10, 3202)), ("F DEX20", new CharacterSheet(Sex.Female, 10, 10, 20, 3203)) },
            new List<(string, CharacterSheet)> { ("M STR1", new CharacterSheet(Sex.Male, 1, 10, 10, 3311)), ("M STR20", new CharacterSheet(Sex.Male, 20, 10, 10, 3312)), ("F STR1", new CharacterSheet(Sex.Female, 1, 10, 10, 3313)), ("F STR20", new CharacterSheet(Sex.Female, 20, 10, 10, 3314)) },
            // (STR alone at DEX 10, one seed per sex: the lower face's volumes growing.)
            new List<(string, CharacterSheet)> { ("M sSTR1", new CharacterSheet(Sex.Male, 1, 10, 10, 3321)), ("M sSTR10", new CharacterSheet(Sex.Male, 10, 10, 10, 3321)), ("M sSTR20", new CharacterSheet(Sex.Male, 20, 10, 10, 3321)),
                                                 ("F sSTR1", new CharacterSheet(Sex.Female, 1, 10, 10, 3322)), ("F sSTR10", new CharacterSheet(Sex.Female, 10, 10, 10, 3322)), ("F sSTR20", new CharacterSheet(Sex.Female, 20, 10, 10, 3322)) },
            new List<(string, CharacterSheet)> { ("M INT1", new CharacterSheet(Sex.Male, 10, 1, 10, 3301)), ("M INT10", new CharacterSheet(Sex.Male, 10, 10, 10, 3305)), ("M INT15", new CharacterSheet(Sex.Male, 10, 15, 10, 3306)), ("M INT20", new CharacterSheet(Sex.Male, 10, 20, 10, 3302)), ("F INT1", new CharacterSheet(Sex.Female, 10, 1, 10, 3303)), ("F INT20", new CharacterSheet(Sex.Female, 10, 20, 10, 3304)) },
        };
        // Part D: one part exaggerated per head through the per-character overrides (move, rotate, scale).
        CharacterSheet Ov(params FacePartOverride[] o) { var sh = new CharacterSheet(Sex.Male, 10, 10, 14, 3401); sh.face = o; return sh; }
        FacePartOverride O(string part, Vector3 move = default, Vector3 rotate = default, Vector3 scale = default) => new FacePartOverride { part = part, move = move, rotate = rotate, scale = scale };
        rows.Add(new List<(string, CharacterSheet)>
        {
            ("base", Ov()),
            ("brow up+big", Ov(O("Brow", new Vector3(0f, 0.06f, 0f), new Vector3(0f, 0f, 15f), new Vector3(1.6f, 1.3f, 1f)))),
            ("nose big", Ov(O("Nose", scale: new Vector3(1.4f, 1.4f, 1.4f)), O("NoseUnder", scale: new Vector3(1.5f, 1.3f, 1f)), O("Nostril", scale: new Vector3(1.6f, 1.4f, 1f)))),
            ("lips full", Ov(O("LipUpper", scale: new Vector3(1.9f, 1.2f, 1f)), O("LipLower", scale: new Vector3(1.9f, 1.2f, 1f)))),
            ("cheeks", Ov(O("Cheekbone", scale: new Vector3(1.3f, 1.3f, 1.6f)), O("CheekboneFront", scale: new Vector3(1.3f, 1.3f, 1.6f)))),
            ("chin out", Ov(O("Chin", new Vector3(0f, -0.05f, 0.15f), scale: new Vector3(1.4f, 1f, 1f)), O("JawAngle", scale: new Vector3(1.3f, 1.3f, 1.3f)))),
        });
        // Every wig style (forced through WigBuilder.Force), then the dressed row last.
        rows.Add(new List<(string, CharacterSheet)>
        {
            ("wig Buzz", new CharacterSheet(Sex.Male, 10, 10, 12, 3601)), ("wig Crop", new CharacterSheet(Sex.Male, 10, 10, 12, 3602)),
            ("wig Bob", new CharacterSheet(Sex.Female, 10, 10, 12, 3603)), ("wig Long", new CharacterSheet(Sex.Female, 10, 10, 12, 3604)),
            ("wig Ponytail", new CharacterSheet(Sex.Female, 10, 10, 12, 3605)), ("wig Bun", new CharacterSheet(Sex.Female, 10, 10, 12, 3606)),
        });
        // Face Polish Part 5: stubble and the beard styles (forced through FaceHair.ForceBeard).
        rows.Add(new List<(string, CharacterSheet)>
        {
            ("beard Stubble", new CharacterSheet(Sex.Male, 10, 10, 12, 3701)), ("beard ShortFull", new CharacterSheet(Sex.Male, 10, 10, 12, 3702)),
            ("beard Goatee", new CharacterSheet(Sex.Male, 10, 10, 12, 3703)), ("beard Moustache", new CharacterSheet(Sex.Male, 10, 10, 12, 3704)),
            ("beard Long", new CharacterSheet(Sex.Male, 10, 10, 6, 3705)),
        });
        // Dressed (a coat over a casual outfit), last row.
        rows.Add(new List<(string, CharacterSheet)> { ("dressed F coat", new CharacterSheet(Sex.Female, 10, 10, 12, 3501)), ("dressed M", new CharacterSheet(Sex.Male, 12, 10, 12, 3502)) });
        return rows.ConvertAll(r => r).ToArray();
    }

    // Face Fix Part 0: the face sanity test's low-DEX heads (thick face fat: where the "tentacles" grew), rows of 4.
    static List<(string label, CharacterSheet sheet)>[] LowDexRows()
    {
        var rows = new List<List<(string, CharacterSheet)>>();
        foreach (var cs in FaceSanity.Cases())
        {
            if (cs.sheet.DEX > 7) continue;
            var sh = cs.sheet;
            string lbl = $"{(sh.sex == Sex.Male ? "M" : "F")} STR{sh.STR} INT{sh.INT} DEX{sh.DEX} s{sh.seed}";
            if (rows.Count == 0 || rows[rows.Count - 1].Count == 4) rows.Add(new List<(string, CharacterSheet)>());
            rows[rows.Count - 1].Add((lbl, sh));
        }
        return rows.ToArray();
    }

    // Clay (-clay): grey, no hair or brows, no skin detail - the shapes only.
    static bool clay;
    static Material clayMat;
    static void Clay(CharacterFigure f)
    {
        if (clayMat == null)
        {
            var sh = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("FPS/BodySkin");
            clayMat = new Material(sh);
            if (clayMat.HasProperty("_PoreStrength")) clayMat.SetFloat("_PoreStrength", 0f);
            if (clayMat.HasProperty("_Redness")) clayMat.SetFloat("_Redness", 0f);
            clayMat.SetColor("_BaseColor", new Color(0.62f, 0.62f, 0.6f));
            clayMat.SetFloat("_Smoothness", 0.25f);
        }
        if (f.Wig != null) f.Wig.gameObject.SetActive(false);
        if (f.HairCards != null) f.HairCards.gameObject.SetActive(false);
        foreach (var hl in f.GetComponentsInChildren<HairLOD>()) hl.enabled = false;
        foreach (var r in f.GetComponentsInChildren<SkinnedMeshRenderer>())
        {
            if (r == f.HairCards || r == f.Wig) continue;
            var m = r.sharedMaterials;
            for (int i = 0; i < m.Length; i++) if (m[i] != null && m[i].shader != null && m[i].shader.name.Contains("BodySkin")) m[i] = clayMat;
            r.sharedMaterials = m;
        }
    }

    IEnumerator HeadsTest(List<(string label, CharacterSheet sheet)>[] customRows = null, bool dressLast = true)
    {
        foreach (var a0 in System.Environment.GetCommandLineArgs()) if (a0 == "-clay") clay = true;
        string dir = Path.GetDirectoryName(Application.dataPath);
        var origin = new Vector3(0f, 1500f, 0f);
        var cam = Camera.main;
        cam.transform.SetParent(null); FlyingVehicle.CameraOverride = true;
        var fpc = FirstPersonController.Instance; if (fpc != null) fpc.enabled = false;
        var key = new GameObject("HeadKey").AddComponent<Light>(); key.type = LightType.Directional; key.intensity = 1.1f;
        key.transform.rotation = Quaternion.LookRotation(new Vector3(0.4f, -0.35f, 1f));
        var fill = new GameObject("HeadFill").AddComponent<Light>(); fill.type = LightType.Directional; fill.intensity = 0.35f;
        fill.transform.rotation = Quaternion.LookRotation(new Vector3(-0.6f, -0.1f, 1f));
        var tod = TimeOfDay.Instance; if (tod != null) { tod.freeze = true; tod.SetHour(12f); }
        var rows = customRows ?? HeadRows();
        BodyBuild.FillLevels = true;   // (Anatomy Rework R8: the layer views below)
        int wigsChecked = 0, wigsOpen = 0; int faceHoles = 0;
        float totalMs = 0f; int built = 0, headTris = 0;
        for (int r = 0; r < rows.Length; r++)
        {
            var row = rows[r];
            var figs = new List<CharacterFigure>();
            for (int i = 0; i < row.Count; i++)
            {
                var go = new GameObject(row[i].label);
                go.transform.position = origin + new Vector3(i * 1.2f, 0f, r * 4f);
                go.transform.rotation = Quaternion.Euler(0f, 180f, 0f);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                // No outfit (the face and the wig read); the last row is dressed (a coat over a casual outfit).
                var outfit = Outfit.None();
                if (dressLast && r == rows.Length - 1)
                {
                    outfit = Outfit.Casual(row[i].sheet.sex == Sex.Female);
                    if (row[i].sheet.sex == Sex.Female) outfit.garments.Add(GarmentDef.Coat());
                }
                string lbl = row[i].label;
                WigBuilder.Force = lbl.StartsWith("wig ") ? (WigStyle?)System.Enum.Parse(typeof(WigStyle), lbl.Substring(4)) : null;
                FaceHair.ForceBeard = lbl.StartsWith("beard ") ? (BeardStyle?)System.Enum.Parse(typeof(BeardStyle), lbl.Substring(6)) : null;
                var f = CharacterFigure.BuildGenerated(go.transform, row[i].sheet, CharacterFigure.Role.Civilian, true, false, null, null, outfit);
                WigBuilder.Force = null; FaceHair.ForceBeard = null;
                totalMs += (float)sw.Elapsed.TotalMilliseconds; built++;
                var a = go.AddComponent<FigureAnimator>(); a.Grounded = true; a.LookYaw = 180f;
                foreach (var mf in go.GetComponentsInChildren<SkinnedMeshRenderer>()) if (mf.name.StartsWith("HeadMesh")) headTris += mf.sharedMesh.triangles.Length / 3;
                HeadCanonOverlay.Attach(f);
                if (lbl == "F DEX10" || lbl == "M DEX10")
                    using (var dsdf = new BodySDF(f.Plan))
                    {
                        var sbp = new System.Text.StringBuilder();
                        for (int q = 0; q < dsdf.FacePrims.Count; q++)
                        {
                            var fp = dsdf.FacePrims[q]; var dd = fp.o - dsdf.Canon.o;
                            sbp.Append($"{dsdf.FacePartNames[q]}({dd.x * 100f:0.0},{dd.y * 100f:0.0},{dd.z * 100f:0.0} r {fp.r.x * 100f:0.0}/{fp.r.y * 100f:0.0}/{fp.r.z * 100f:0.0}) ");
                        }
                        Log($"  {lbl} parts (cm from the eye line's centre): {sbp}");
                    }
                { int holes = FaceHoles(f, out string where); faceHoles += holes > 0 ? 1 : 0; if (holes > 0) Log($"  {lbl}: {holes} open edges in the face at {where}"); }
                Log($"  {lbl}: {FaceMetrics.Profile(f.Plan)}");
                if (clay) Clay(f);
                Log($"  {lbl}: {CanonReport(f)}; wig {f.Asset.wigStyle}: {OpenEdges(f.Asset.wig)} open edges; hair cards {(f.Asset.hairCards != null ? f.Asset.hairCards.triangles.Length / 3 : 0)} triangles; {f.Asset.neckWeld}");
                if (f.Asset.wig != null) { wigsChecked++; if (OpenEdges(f.Asset.wig) > 0) { wigsOpen++; Log($"    open edges at {lastOpen}"); } }
                figs.Add(f);
                yield return null;
            }
            yield return new WaitForSeconds(0.4f);
            // One close-up per head, front and three-quarter.
            foreach (var f in figs)
            {
                Transform hc = null;
                foreach (var t in f.GetComponentsInChildren<Transform>()) if (t.name == "HeadCentre") { hc = t; break; }
                Vector3 c = hc != null ? hc.position : f.transform.position + Vector3.up * 1.6f;
                Vector3 fwd = f.transform.forward, right = f.transform.right;
                var ov = f.HeadJoint != null ? f.HeadJoint.GetComponent<HeadCanonOverlay>() : null;
                // Only this figure shows (neighbours would overlap the side and back views).
                var hidden = new List<Renderer>();
                foreach (var other in Object.FindObjectsByType<CharacterFigure>())
                    if (other != f) foreach (var rr in other.GetComponentsInChildren<Renderer>()) if (rr.enabled) { rr.enabled = false; hidden.Add(rr); }
                // (Their hair LODs would turn their hair / face-hair cards back on.)
                var hiddenLods = new List<HairLOD>();
                foreach (var other in Object.FindObjectsByType<CharacterFigure>())
                    if (other != f) foreach (var hl in other.GetComponentsInChildren<HairLOD>()) if (hl.enabled) { hl.enabled = false; hiddenLods.Add(hl); }
                // front / side with the canon (orthographic, so the lines and the head share one scale), the
                // three-quarter, back and body views without.
                foreach (var (view, from) in new[] { ("front", c + fwd * 0.62f + Vector3.up * 0.02f), ("canon", c + fwd * 0.45f), ("side", c + right * 0.45f),
                                                     ("34", c + fwd * 0.36f + right * 0.3f + Vector3.up * 0.03f),
                                                     ("eye", c + fwd * 0.2f + right * 0.05f + Vector3.up * 0.01f),
                                                     ("mouth", c + fwd * 0.24f + right * 0.08f - Vector3.up * 0.045f),
                                                     ("ear", c + right * 0.24f + fwd * 0.04f - Vector3.up * 0.02f),
                                                     ("back", c - fwd * 0.5f + right * 0.2f - Vector3.up * 0.06f),
                                                     ("body", c - Vector3.up * 0.75f + fwd * 2.4f + right * 0.6f) })
                {
                    Vector3 at = view == "body" ? c - Vector3.up * 0.75f : view == "mouth" ? c - Vector3.up * 0.055f : view == "ear" ? c + right * 0.06f - Vector3.up * 0.03f : c;
                    bool ortho = view == "canon" || view == "side";
                    cam.orthographic = ortho; cam.orthographicSize = 0.17f;
                    if (ov != null) ov.Show(view == "canon" ? HeadCanonOverlay.View.Front : view == "side" ? HeadCanonOverlay.View.Side : HeadCanonOverlay.View.Off);
                    cam.transform.SetPositionAndRotation(from, Quaternion.LookRotation(at - from));
                    yield return new WaitForEndOfFrame(); yield return new WaitForEndOfFrame();
                    ScreenCapture.CaptureScreenshot(Path.Combine(dir, $"heads_r{r}_{f.name.Replace(' ', '_')}_{view}.png"));
                    yield return null; yield return null;
                }
                cam.orthographic = false;
                if (ov != null) ov.Show(HeadCanonOverlay.View.Off);
                foreach (var rr in hidden) if (rr != null) rr.enabled = true;
                foreach (var hl in hiddenLods) if (hl != null) hl.enabled = true;
            }
        }
        // Anatomy Rework R8: the layer view - a few heads at bone only, + muscle, + fat, and finished.
        foreach (var name in new[] { "M DEX10", "F DEX1", "M DEX20", "F INT20" })
        {
            var go = GameObject.Find(name);
            var fg = go != null ? go.GetComponent<CharacterFigure>() : null;
            if (fg == null) continue;
            var fl = go.GetComponent<FillLevelDebug>() ?? go.AddComponent<FillLevelDebug>();
            Transform hc = null;
            foreach (var t in go.GetComponentsInChildren<Transform>()) if (t.name == "HeadCentre") { hc = t; break; }
            if (hc == null) continue;
            var hidden = new List<Renderer>();
            foreach (var other in Object.FindObjectsByType<CharacterFigure>())
                if (other != fg) foreach (var rr in other.GetComponentsInChildren<Renderer>()) if (rr.enabled) { rr.enabled = false; hidden.Add(rr); }
            if (fg.Wig != null) fg.Wig.enabled = false;
            if (fg.HairCards != null) fg.HairCards.enabled = false;
            var lods = Object.FindObjectsByType<HairLOD>(); foreach (var hl in lods) hl.enabled = false;
            for (int lvl = 0; lvl < 4; lvl++)
            {
                fl.Show(lvl);
                foreach (var (view, from) in new[] { ("front", hc.position + go.transform.forward * 0.45f), ("side", hc.position + go.transform.right * 0.45f) })
                {
                    cam.orthographic = true; cam.orthographicSize = 0.17f;
                    cam.transform.SetPositionAndRotation(from, Quaternion.LookRotation(hc.position - from));
                    yield return new WaitForEndOfFrame(); yield return new WaitForEndOfFrame();
                    ScreenCapture.CaptureScreenshot(Path.Combine(dir, $"heads_layers_{name.Replace(' ', '_')}_{lvl}_{view}.png"));
                    yield return null; yield return null;
                }
            }
            fl.Show(3); cam.orthographic = false;
            if (fg.Wig != null) fg.Wig.enabled = true;
            if (fg.HairCards != null) fg.HairCards.enabled = true;
            foreach (var hl in lods) if (hl != null) hl.enabled = true;
            foreach (var rr in hidden) if (rr != null) rr.enabled = true;
            var hm0 = fg.HeadRenderer is SkinnedMeshRenderer hs0 ? hs0.sharedMesh : null;
            Log($"layers {name}: {(hm0 != null ? hm0.blendShapeCount : 0)} fill-level blend shapes on the head");
        }
        BodyBuild.FillLevels = false;
        // Long hair cards swing on their chains: turn the long-haired head's figure sharply, the tips lag.
        {
            var lh = GameObject.Find("wig Long");
            var dg = lh != null ? lh.GetComponent<ClothDangle>() : null;
            var lf = lh != null ? lh.GetComponent<CharacterFigure>() : null;
            if (dg != null && lf != null && lf.Asset.chains.Count > 0)
            {
                int set = lf.Asset.chains.Count - 1, mid = lf.Asset.chains[set].panels / 2, tip = lf.Asset.chains[set].bones;
                cam.transform.SetPositionAndRotation(lh.transform.position + Vector3.up * 1.4f - lh.transform.forward * 1.5f, Quaternion.LookRotation(lh.transform.forward));
                yield return new WaitForSeconds(0.3f);
                lh.transform.Rotate(0f, 70f, 0f);
                float lag = 0f;
                for (int q = 0; q < 6; q++) { yield return null; lag = Mathf.Max(lag, (dg.DebugJoint(set, mid, tip) - dg.DebugRest(set, mid, tip)).magnitude); }
                yield return new WaitForSeconds(1.5f);
                float settle = (dg.DebugJoint(set, mid, tip) - dg.DebugRest(set, mid, tip)).magnitude;
                lh.transform.Rotate(0f, -70f, 0f);
                Log($"long hair: {lf.Asset.chains[set].panels} strand groups x {tip} bones, tip lag after a 70 deg turn {lag * 100f:0.0} cm, {settle * 100f:0.0} cm after 1.5 s");
                Log(lag > 0.01f && settle < lag ? "PASS long hair swings on its chains and settles" : "FAIL long hair chains");
            }
            else Log("FAIL no long-hair chains on 'wig Long'");
        }
        // The face rig: wigs, eyelids, the jaw. Close the eyes and open every mouth, shoot the first row again.
        int wigs = 0, rigs = 0;
        var anims = new List<FaceAnimator>(Object.FindObjectsByType<FaceAnimator>());
        foreach (var fa in anims) { if (fa.Jaw != null && fa.UpperLids.Count == 2) rigs++; fa.MouthOverride = 1f; fa.BlinkOverride = 1f; }
        foreach (var fg in Object.FindObjectsByType<CharacterFigure>()) if (fg.Wig != null) wigs++;
        yield return new WaitForSeconds(0.3f);
        foreach (var name in new[] { "M DEX10", "F DEX10", "base" })
        {
            var go = GameObject.Find(name);
            if (go == null) continue;
            Transform hc = null;
            foreach (var t in go.GetComponentsInChildren<Transform>()) if (t.name == "HeadCentre") { hc = t; break; }
            if (hc == null) continue;
            // Straight at the mouth, from its height.
            var fgo = go.GetComponent<CharacterFigure>();
            Vector3 mouthW = fgo != null && fgo.HeadJoint != null ? fgo.HeadJoint.TransformPoint(fgo.Asset.mouthCentre - fgo.Plan.JointPos(fgo.Plan.Index("Head"))) : hc.position - Vector3.up * 0.06f;
            Vector3 from = mouthW + go.transform.forward * 0.3f + Vector3.up * 0.02f;
            cam.transform.SetPositionAndRotation(from, Quaternion.LookRotation(mouthW + Vector3.up * 0.01f - from));
            yield return new WaitForEndOfFrame(); yield return new WaitForEndOfFrame();
            ScreenCapture.CaptureScreenshot(Path.Combine(dir, $"heads_open_{name.Replace(' ', '_')}.png"));
            if (name == "M DEX10" && fgo != null && fgo.HeadRenderer is SkinnedMeshRenderer hsm)
            {
                // Posed (mouth open): the triangles that stretched most, their marks and jaw weights.
                var baked = new Mesh(); hsm.BakeMesh(baked, true);
                var bv = baked.vertices; var rv = hsm.sharedMesh.vertices; var tri = hsm.sharedMesh.triangles;
                var mk4 = new List<Vector3>(); hsm.sharedMesh.GetUVs(4, mk4); var bw4 = hsm.sharedMesh.boneWeights;
                var grow = new List<(float, int)>();
                for (int q = 0; q < tri.Length; q += 3)
                {
                    float ra = Vector3.Cross(rv[tri[q + 1]] - rv[tri[q]], rv[tri[q + 2]] - rv[tri[q]]).magnitude;
                    float ba = Vector3.Cross(bv[tri[q + 1]] - bv[tri[q]], bv[tri[q + 2]] - bv[tri[q]]).magnitude;
                    grow.Add((ba - ra, q));
                }
                float JW(BoneWeight bw) => (bw.boneIndex0 == fgo.Asset.jawBone ? bw.weight0 : 0f) + (bw.boneIndex1 == fgo.Asset.jawBone ? bw.weight1 : 0f) + (bw.boneIndex2 == fgo.Asset.jawBone ? bw.weight2 : 0f);
                // What the camera sees where the dark triangles are: a ray at each one's centre (posed).
                var mcGo = new GameObject("MouthProbe"); mcGo.transform.SetPositionAndRotation(hsm.transform.position, hsm.transform.rotation);
                var bakedW = new Mesh(); hsm.BakeMesh(bakedW, true);
                var col = mcGo.AddComponent<MeshCollider>(); col.sharedMesh = bakedW;
                Physics.SyncTransforms();
                var bvv = bakedW.vertices; var sbr = new System.Text.StringBuilder(); int shown = 0, hidden = 0;
                for (int q = 0; q < tri.Length && shown + hidden < 12; q += 3)
                {
                    if (!(mk4[tri[q]].z > 0.5f && mk4[tri[q + 1]].z > 0.5f && mk4[tri[q + 2]].z > 0.5f)) continue;
                    Vector3 cen = mcGo.transform.TransformPoint((bvv[tri[q]] + bvv[tri[q + 1]] + bvv[tri[q + 2]]) / 3f);
                    Vector3 eyeP = cam.transform.position;
                    if (Physics.Raycast(eyeP, (cen - eyeP).normalized, out var hit, 2f) && hit.collider == col)
                    {
                        int ht = hit.triangleIndex * 3;
                        bool same = ht == q;
                        if (same) shown++; else { hidden++; sbr.Append($"[hit tri inside {mk4[tri[ht]].z:0}/{mk4[tri[ht + 1]].z:0}/{mk4[tri[ht + 2]].z:0} jaw {JW(bw4[tri[ht]]):0.0}/{JW(bw4[tri[ht + 1]]):0.0}/{JW(bw4[tri[ht + 2]]):0.0} {(hit.point - cen).magnitude * 1000f:0} mm before, normal.cam {Vector3.Dot(hit.normal, -(cen - eyeP).normalized):0.00}] "); }
                    }
                }
                Log($"  dark triangles seen {shown}, hidden {hidden}: {sbr}");
                Object.Destroy(mcGo);
            }
            // ... and in profile.
            {
                Vector3 side = mouthW + go.transform.right * 0.3f + go.transform.forward * 0.05f;
                cam.transform.SetPositionAndRotation(side, Quaternion.LookRotation(mouthW + go.transform.forward * 0.02f - side));
                yield return new WaitForEndOfFrame(); yield return new WaitForEndOfFrame();
                ScreenCapture.CaptureScreenshot(Path.Combine(dir, $"heads_openside_{name.Replace(' ', '_')}.png"));
                yield return null; yield return null;
            }
            var fan = go.GetComponent<FaceAnimator>();
            if (fan != null) Log($"  {name} rig: jaw {(fan.Jaw != null ? fan.Jaw.localEulerAngles.x : -1f):0} deg, upper lid {(fan.UpperLids.Count > 0 ? fan.UpperLids[0].localEulerAngles.x : -1f):0} deg (closed {fan.lidClosed:0}), enabled {fan.isActiveAndEnabled}, blink {fan.BlinkOverride}");
            yield return null; yield return null;
        }
        foreach (var fa in anims) { fa.MouthOverride = -1f; fa.BlinkOverride = -1f; }
        Log($"face rigs: {rigs} of {anims.Count} with a jaw and both lids, {wigs} wigs");
        {
            var fg0 = GameObject.Find("M DEX10")?.GetComponent<CharacterFigure>();
            var hm = fg0 != null && fg0.HeadRenderer is SkinnedMeshRenderer smr ? smr.sharedMesh : null;
            if (hm != null)
            {
                var mk = new List<Vector3>(); hm.GetUVs(4, mk);
                int brow = 0, lip = 0, inside = 0, jaw = 0;
                foreach (var m in mk) { if (m.x > 0.5f) brow++; if (m.y > 0.5f) lip++; if (m.z > 0.5f) inside++; }
                foreach (var bw in hm.boneWeights) if (bw.boneIndex0 == fg0.Asset.jawBone || bw.boneIndex1 == fg0.Asset.jawBone) jaw++;
                Log($"face masks on M DEX10: {mk.Count} verts, brow {brow}, lip {lip}, mouth inside {inside}; {jaw} verts on the jaw (bone {fg0.Asset.jawBone})");
                // The mouth's bridging triangles (dark inside) and where they sit round the mouth centre (rest, mm).
                var tv = hm.vertices; var tt = hm.triangles; int bridgeTris = 0; float bxMin = 1f, bxMax = -1f, byMin = 1f, byMax = -1f;
                Vector3 mc = fg0.Asset.mouthCentre;
                for (int q = 0; q < tt.Length; q += 3)
                    if (mk[tt[q]].z > 0.5f && mk[tt[q + 1]].z > 0.5f && mk[tt[q + 2]].z > 0.5f)
                    {
                        bridgeTris++;
                        for (int e = 0; e < 3; e++) { var d = tv[tt[q + e]] - mc; bxMin = Mathf.Min(bxMin, d.x); bxMax = Mathf.Max(bxMax, d.x); byMin = Mathf.Min(byMin, d.y); byMax = Mathf.Max(byMax, d.y); }
                    }
                // Jaw weight of the head's vertices within 1 cm above / below the mouth centre, inside the mouth's width.
                int[] hist = new int[5]; var bws = hm.boneWeights;
                for (int q = 0; q < tv.Length; q++)
                {
                    var d = tv[q] - mc;
                    if (Mathf.Abs(d.x) > 0.02f || Mathf.Abs(d.y) > 0.01f || d.z < -0.02f) continue;
                    var bw = bws[q]; float w = (bw.boneIndex0 == fg0.Asset.jawBone ? bw.weight0 : 0f) + (bw.boneIndex1 == fg0.Asset.jawBone ? bw.weight1 : 0f) + (bw.boneIndex2 == fg0.Asset.jawBone ? bw.weight2 : 0f);
                    hist[Mathf.Clamp((int)(w * 4.999f), 0, 4)]++;
                }
                {
                    // Which mesh carries the face: body-mesh vertices in front of the face, and the face's top bones.
                    var plan0 = fg0.Plan; int hiB = plan0.Index("Head");
                    var cn = HeadCanon.From(plan0, hiB, plan0.JointPos(hiB), Vector3.right, Vector3.up, Vector3.forward);
                    var bodyM = fg0.Renderers[0] is SkinnedMeshRenderer bsm ? bsm.sharedMesh : null;
                    int bodyFace = 0;
                    if (bodyM != null) foreach (var bvx in bodyM.vertices) { var d = bvx - cn.o; if (d.z > cn.halfD * 0.5f && d.y > cn.chin && d.y < cn.brow && Mathf.Abs(d.x) < cn.halfW * 0.6f) bodyFace++; }
                    var tops = new Dictionary<string, int>();
                    for (int q = 0; q < tv.Length; q++)
                    {
                        var d = tv[q] - cn.o;
                        if (d.z < cn.halfD * 0.5f || d.y < cn.chin || d.y > cn.brow) continue;
                        int bi = hm.boneWeights[q].boneIndex0;
                        string bn = bi == fg0.Asset.jawBone ? "Jaw" : bi < plan0.bones.Count ? plan0.bones[bi].name : "chain" + bi;
                        tops.TryGetValue(bn, out int c0); tops[bn] = c0 + 1;
                    }
                    var tsb = new System.Text.StringBuilder(); foreach (var kv in tops) tsb.Append($"{kv.Key} {kv.Value} ");
                    Log($"face front: {bodyFace} body-mesh vertices in front of the face; head-mesh face vertices by top bone: {tsb}");
                }
                Log($"mouth: {bridgeTris} bridging triangles, x {bxMin * 1000f:0}..{bxMax * 1000f:0} mm, y {byMin * 1000f:0}..{byMax * 1000f:0} mm round the mouth centre; jaw weights near it (0-.2/../.8-1): {string.Join(" ", hist)}");
            }
        }
        Log(rigs == anims.Count && rigs > 0 ? "PASS every face has a jaw and eyelids" : "FAIL face rig");
        Log(faceHoles == 0 ? "PASS no holes in the faces" : $"FAIL {faceHoles} heads have holes in the face");
                Log(wigsOpen == 0 ? $"PASS every wig mesh is closed ({wigsChecked} wigs)" : $"FAIL {wigsOpen} of {wigsChecked} wig meshes have holes (open edges)");
        Log($"heads: {built} built, {totalMs / Mathf.Max(1, built):0} ms each (whole body, sync), head mesh {headTris / Mathf.Max(1, built)} triangles avg");
        Log(built > 0 ? "PASS heads built" : "FAIL no heads");
        if (fpc != null) fpc.enabled = true;
        FlyingVehicle.CameraOverride = false;
    }

    // ---------- graphics: the Material Lab ----------

    IEnumerator LabTest()
    {
        string dir = Path.GetDirectoryName(Application.dataPath);
        var lab = MaterialLab.Instance;
        if (lab == null) { Log("FAIL not in the Material Lab scene"); yield break; }
        yield return new WaitForSeconds(1.5f);
        GraphicsFeatures.RegisterLightingStack();
        int chars = lab.characterRow != null ? lab.characterRow.childCount : 0;
        Log($"lab: {MaterialLabPresetCount()} material rows, {chars} characters, {lab.neon.Count} orbiting neon lights, {GraphicsFeatures.All.Count} feature toggles: {string.Join(", ", System.Linq.Enumerable.Select(GraphicsFeatures.All, f => f.name))}");
        Log(chars >= 4 && lab.neon.Count > 0 && GraphicsFeatures.All.Count > 0 ? "PASS the lab has its exhibits, lights and toggles" : "FAIL lab incomplete");
        var cam = Camera.main;
        IEnumerator Shot(string n)
        {
            yield return new WaitForEndOfFrame(); yield return new WaitForEndOfFrame();
            ScreenCapture.CaptureScreenshot(Path.Combine(dir, $"lab_{n}.png"));
            yield return null; yield return null;
        }
        void View(Vector3 at, Vector3 from) => cam.transform.SetPositionAndRotation(from, Quaternion.LookRotation(at - from));
        Vector3 rows = new Vector3(0f, 0.6f, 2f), people = lab.characterRow.position + new Vector3(-2.6f, 1f, 0f);
        foreach (MaterialLab.Preset pr in System.Enum.GetValues(typeof(MaterialLab.Preset)))
        {
            lab.SetPreset(pr);
            yield return new WaitForSeconds(0.3f);
            View(rows, rows + new Vector3(0f, 6f, -12f)); yield return Shot(pr + "_materials");
            View(people, people + new Vector3(0.5f, 0.6f, -5f)); yield return Shot(pr + "_people");
        }
        // Close-ups under the overcast preset (neutral light): the detail of each type.
        lab.SetPreset(MaterialLab.Preset.Overcast);
        foreach (var n in new[] { "Concrete", "Asphalt", "PaintedMetal", "BrushedMetal", "CarPaint", "Cotton", "Denim", "Wool", "Leather" })
        {
            var mats = GameObject.Find("Materials");
            var row = mats != null ? mats.transform.Find(n) : null;
            if (row == null) continue;
            Vector3 c = row.transform.position + new Vector3(0f, 0.6f, -0.8f);
            View(c, c + new Vector3(0.6f, 0.9f, -2.2f)); yield return Shot("close_" + n);
        }
        // The cars up close (object-space patterns must stay in metres on scaled primitives), and the
        // road and floor from standing height.
        foreach (var carName in new[] { "LabCar", "LabPoliceCar" })
        {
            var car = GameObject.Find(carName);
            if (car == null) continue;
            Vector3 c = car.transform.position + Vector3.up * 0.5f;
            View(c, c + new Vector3(-1.2f, 0.8f, -1.6f)); yield return Shot("close_" + carName);
        }
        { Vector3 r = new Vector3(0f, 0f, 24f); View(r + new Vector3(0f, 0f, 10f), r + new Vector3(-6f, 1.7f, -5f)); yield return Shot("close_road"); }
        // Characters up close (Phase 3): face, torso with hems and seams, hand, and the coat.
        for (int ci = 0; ci < lab.characterRow.childCount && ci < 3; ci++)
        {
            var ch = lab.characterRow.GetChild(ci);
            Vector3 fwd = ch.forward, right = ch.right, o = ch.position;
            Transform headC = null;
            foreach (var t in ch.GetComponentsInChildren<Transform>()) if (t.name == "HeadCentre") { headC = t; break; }
            Vector3 head = headC != null ? headC.position : o + Vector3.up * 1.6f;
            float hgt = (head.y - o.y) / 0.9f;
            View(head, head + fwd * 0.5f + right * 0.12f + Vector3.up * 0.03f); yield return Shot($"char{ci}_face");
            View(o + Vector3.up * hgt * 0.55f, o + Vector3.up * hgt * 0.6f + fwd * 1.1f + right * 0.3f); yield return Shot($"char{ci}_torso");
            View(o + Vector3.up * hgt * 0.45f + right * 0.22f, o + Vector3.up * hgt * 0.5f + fwd * 0.5f + right * 0.45f); yield return Shot($"char{ci}_hand");
        }
        lab.SetPreset(MaterialLab.Preset.NightNeon);
        lab.SetWet(true); View(rows, rows + new Vector3(0f, 6f, -12f)); yield return Shot("wet"); lab.SetWet(false);
        // Phase 1 before / after: SSAO and film grain off, Neutral tonemapping.
        GraphicsFeatures.Set(GraphicsFeatures.Ssao, false); GraphicsFeatures.Set(GraphicsFeatures.Grain, false);
        yield return Shot("phase1_off");
        GraphicsFeatures.Set(GraphicsFeatures.Aces, false); yield return Shot("neutral_tonemap"); GraphicsFeatures.Set(GraphicsFeatures.Aces, true);
        GraphicsFeatures.Set(GraphicsFeatures.Ssao, true); GraphicsFeatures.Set(GraphicsFeatures.Grain, true);
        View(people, people + new Vector3(0.5f, 0.6f, -5f)); yield return Shot("people_ssao_on");
        GraphicsFeatures.Set(GraphicsFeatures.Ssao, false); yield return Shot("people_ssao_off"); GraphicsFeatures.Set(GraphicsFeatures.Ssao, true);
        GraphicsFeatures.Set("Post-processing", false); yield return Shot("post_off"); GraphicsFeatures.Set("Post-processing", true);
        View(new Vector3(0f, 5f, 38f), new Vector3(-18f, 8f, 10f)); yield return Shot("facade_road");
        lab.focus.position = rows; View(rows, rows + new Vector3(0f, 2f, -12f)); lab.SetDistance(120f); yield return Shot("far_120m");
        Log("lab screenshots: presets x (materials, people), wet, post off, facade + road, 120 m");
    }

    static int MaterialLabPresetCount() { var m = GameObject.Find("Materials"); return m != null ? m.transform.childCount : 0; }

    // ---------- clothing part B: the trench coat ----------

    IEnumerator CoatTest()
    {
        string dir = Path.GetDirectoryName(Application.dataPath);
        var origin = new Vector3(0f, 1500f, 0f);
        TimeOfDay.Instance?.SetHour(12f);
        var outfit = Outfit.Casual(); outfit.garments.Add(GarmentDef.Coat());
        var specs = new (string, CharacterSheet)[]
        {
            ("man", new CharacterSheet(Sex.Male, 10, 10, 10, 1000)), ("woman", new CharacterSheet(Sex.Female, 10, 10, 10, 1001)),
            ("heavy man", new CharacterSheet(Sex.Male, 14, 10, 2, 1002)),
        };
        var figs = new List<(CharacterFigure f, FigureAnimator a, ClothDangle d)>();
        for (int i = 0; i < specs.Length; i++)
        {
            var (name, sh) = specs[i];
            var go = new GameObject(name);
            go.transform.position = origin + new Vector3(i * 1.4f, 0f, 0f);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var f = CharacterFigure.BuildGenerated(go.transform, sh, CharacterFigure.Role.Civilian, true, false, null, null, outfit);
            float ms = (float)sw.Elapsed.TotalMilliseconds;
            var an = go.AddComponent<FigureAnimator>(); an.Grounded = true;
            var d = go.GetComponent<ClothDangle>();
            figs.Add((f, an, d));
            var asset = f.Asset;
            var sb = new StringBuilder();
            for (int g = 0; g < asset.outfit.Count; g++) sb.Append($"{asset.outfit.garments[g].name} {asset.garmentTriangles[g]}, ");
            Log($"{name}: {asset.triangles} tris (skin {asset.skinTriangles}, {sb}), chains {asset.chains.Count} x {(asset.chains.Count > 0 ? asset.chains[0].panels + " panels x " + asset.chains[0].bones + " bones" : "-")}; generated in {ms:0} ms");
            yield return null;
        }
        bool built = figs.TrueForAll(x => x.d != null && x.f.Asset.chains.Count == 1);
        Log(built ? "PASS the coat builds with its spring chains (6 panels x 3 bones)" : "FAIL no coat chains");
        if (!built) yield break;

        var (f0, a0, d0) = figs[0];
        var cs = f0.Asset.chains[0];
        int hem = cs.bones;
        float Avg(System.Func<int, float> v) { float sum = 0f; for (int p = 0; p < cs.panels; p++) sum += v(p); return sum / cs.panels; }
        // Hem joints relative to their rest: backward (-forward) and up.
        float Back() => Avg(p => -Vector3.Dot(d0.DebugJoint(0, p, hem) - d0.DebugRest(0, p, hem), f0.transform.forward));
        float Up() => Avg(p => d0.DebugJoint(0, p, hem).y - d0.DebugRest(0, p, hem).y);
        float Dev() => Avg(p => (d0.DebugJoint(0, p, hem) - d0.DebugRest(0, p, hem)).magnitude);
        int iFL = System.Array.FindIndex(cs.angles, x => Mathf.Abs(x + 30f) < 1f), iFR = System.Array.FindIndex(cs.angles, x => Mathf.Abs(x - 30f) < 1f);
        float Opening() => (d0.DebugJoint(0, iFL, hem) - d0.DebugJoint(0, iFR, hem)).magnitude;
        int inside = 0; string insideAt = "";
        IEnumerator Run(float seconds, Vector3 v, float yaw = 0f)
        {
            foreach (var (f, an, d) in figs) an.Velocity = v;
            for (float t = 0f; t < seconds; t += Time.deltaTime)
            {
                foreach (var (f, an, d) in figs)
                {
                    f.transform.position += v * Time.deltaTime;
                    if (yaw != 0f) f.transform.Rotate(0f, yaw * Time.deltaTime / seconds, 0f);
                }
                yield return new WaitForEndOfFrame();   // (after the pose and the chains this frame)
                foreach (var (f, an, d) in figs)
                {
                    int n = d.DebugInsideLegs(0, out string w);
                    if (n > inside) { inside = n; insideAt = $"{f.name}, {an.CurrentPose}, v {an.Velocity}: {w}"; }
                }
            }
        }
        var cam = Camera.main; cam.transform.SetParent(null); FlyingVehicle.CameraOverride = true;
        var lg = new GameObject("CoatKey"); var l = lg.AddComponent<Light>(); l.type = LightType.Directional; l.intensity = 1.2f;
        lg.transform.rotation = Quaternion.LookRotation(new Vector3(-0.4f, -0.5f, 0.8f));
        Coroutine follow = null;
        IEnumerator Shot(string n, Vector3 at, Vector3 from)
        {
            if (follow != null) StopCoroutine(follow);
            cam.transform.SetPositionAndRotation(from, Quaternion.LookRotation(at - from));
            yield return new WaitForEndOfFrame(); yield return new WaitForEndOfFrame();
            ScreenCapture.CaptureScreenshot(Path.Combine(dir, $"coat_{n}.png"));
            yield return null;
            follow = StartCoroutine(Follow());
        }
        Vector3 Mid() => (figs[0].f.transform.position + figs[figs.Count - 1].f.transform.position) * 0.5f + Vector3.up * 0.85f;
        // The camera stays on the group (chains only simulate near the camera and on screen).
        IEnumerator Follow() { while (true) { cam.transform.SetPositionAndRotation(Mid() + new Vector3(-3.5f, 0.6f, 3.5f), Quaternion.LookRotation(new Vector3(3.5f, -0.6f, -3.5f))); yield return null; } }
        follow = StartCoroutine(Follow());

        yield return Run(1.5f, Vector3.zero);
        float standDev = Dev(), standOpen = Opening();
        Log($"standing: hem {standDev * 100f:0.0} cm off rest, front opening {standOpen * 100f:0.0} cm");
        yield return Shot("front", Mid(), Mid() + new Vector3(0f, 0.2f, 4.6f));
        yield return Shot("back", Mid(), Mid() + new Vector3(0.4f, 0.3f, -4.6f));
        yield return Shot("side", figs[0].f.transform.position + Vector3.up * 0.85f, figs[0].f.transform.position + new Vector3(-2.6f, 0.4f, 0.4f));

        // Run (forward = +z): the tails trail and the front opens.
        yield return Run(0.8f, Vector3.forward * 5f);
        float runBack = 0f, runOpen = 0f;
        for (int k = 0; k < 20; k++) { yield return Run(0.05f, Vector3.forward * 5f); runBack = Mathf.Max(runBack, Back()); runOpen = Mathf.Max(runOpen, Opening()); }
        yield return Shot("run", figs[0].f.transform.position + Vector3.up * 0.8f, figs[0].f.transform.position + new Vector3(-2.4f, 0.5f, 0.6f));
        Log($"running 5 m/s: tails {runBack * 100f:0.0} cm behind rest, front opening up to {runOpen * 100f:0.0} cm (standing {standOpen * 100f:0.0})");
        Log(runBack > 0.04f && runOpen > standOpen + 0.02f ? "PASS running: the tails trail and the front opens" : "FAIL coat doesn't react to running");

        // Sudden stop: swings, then settles.
        yield return Run(0.3f, Vector3.zero);
        float swing = Dev();
        yield return Run(1.8f, Vector3.zero);
        float settled = Dev();
        Log($"stop: hem {swing * 100f:0.0} cm off rest right after, {settled * 100f:0.0} cm after 2 s (standing {standDev * 100f:0.0})");
        Log(settled < swing && settled < standDev + 0.03f ? "PASS the coat swings on a stop and settles" : "FAIL coat doesn't settle");

        // Turn on the spot.
        float turnMax = 0f;
        for (int k = 0; k < 12; k++) { yield return Run(0.05f, Vector3.zero, 180f / 12f / 0.05f * 0.05f); turnMax = Mathf.Max(turnMax, Dev()); }
        yield return Run(1.5f, Vector3.zero);
        Log($"turn 180 deg in 0.6 s: hem swings {turnMax * 100f:0.0} cm off rest");

        // Fall (the air rushes up): the skirt flares; then glide.
        foreach (var (f, an, d) in figs) { an.Grounded = false; an.CurrentPose = FigureAnimator.Pose.Air; }
        float flare = 0f;
        for (float t = 0f; t < 1.2f; t += 0.05f) { yield return Run(0.05f, Vector3.down * Mathf.Min(25f, 9.8f * (t + 0.05f))); flare = Mathf.Max(flare, Up()); }
        yield return Shot("fall", figs[0].f.transform.position + Vector3.up * 0.6f, figs[0].f.transform.position + new Vector3(-2.6f, 0.2f, 1.2f));
        Log($"falling: hem rises up to {flare * 100f:0.0} cm above rest");
        Log(flare > 0.05f ? "PASS falling flares the skirt up" : "FAIL no flare when falling");
        foreach (var (f, an, d) in figs) an.CurrentPose = FigureAnimator.Pose.Glide;
        yield return Run(1.2f, new Vector3(0f, -2f, 10f));
        yield return Shot("glide", figs[0].f.transform.position + Vector3.up * 0.6f, figs[0].f.transform.position + new Vector3(-2.6f, 0.6f, 0.4f));
        foreach (var (f, an, d) in figs) { an.Grounded = true; an.CurrentPose = FigureAnimator.Pose.Normal; }
        Log($"legs: at most {inside} chain joints inside a leg (80% of its radius) on any frame {insideAt}");
        Log(inside == 0 ? "PASS the legs never pass through the coat panels" : "FAIL chain joints inside the legs");
        Log($"simulated chain sets last frame: {ClothDangle.Simulated}");
        if (follow != null) StopCoroutine(follow);
        Destroy(lg);
        FlyingVehicle.CameraOverride = false;
    }

    // ---------- clothing part A: real garments in the game pipeline ----------

    IEnumerator DressedTest()
    {
        string dir = Path.GetDirectoryName(Application.dataPath);
        var origin = new Vector3(0f, 1500f, 0f);
        TimeOfDay.Instance?.SetHour(12f);
        var specs = new (string, CharacterSheet, CharacterFigure.Role)[]
        {
            ("civilian M", new CharacterSheet(Sex.Male, 10, 10, 10, 1000), CharacterFigure.Role.Civilian),
            ("civilian F heavy", new CharacterSheet(Sex.Female, 12, 10, 2, 1001), CharacterFigure.Role.Civilian),
            ("civilian M STR 20", new CharacterSheet(Sex.Male, 20, 10, 14, 1006), CharacterFigure.Role.Civilian),
            ("police", new CharacterSheet(Sex.Male, 14, 10, 13, 1003), CharacterFigure.Role.Police),
            ("player", new CharacterSheet(Sex.Female, 10, 10, 10, 1004), CharacterFigure.Role.Player),
        };
        var figs = new List<(CharacterFigure f, FigureAnimator a)>();
        bool allDressed = true;
        for (int i = 0; i < specs.Length; i++)
        {
            var (name, sh, role) = specs[i];
            var go = new GameObject(name);
            go.transform.position = origin + new Vector3(i * 1.2f, 0f, 0f);
            go.transform.rotation = Quaternion.Euler(0f, 180f, 0f);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var f = CharacterFigure.BuildGenerated(go.transform, sh, role, true, false);
            float ms = (float)sw.Elapsed.TotalMilliseconds;
            var an = go.AddComponent<FigureAnimator>(); an.Grounded = true; an.LookYaw = 180f;
            figs.Add((f, an));
            if (i == 0)
            {
                // Stage timing, one body sequentially (as the pool runs it) and blocking.
                var bb = new BodyBuild(sh, null, Wardrobe.For(role, sh));
                while (!bb.Step()) yield return null;
                Log($"  pool-style build: {bb.Asset.wallMs:0} ms wall: " + string.Join(", ", System.Linq.Enumerable.Select(System.Linq.Enumerable.Range(0, 7), s => $"{(BodyBuild.Stage)s} {bb.StageWallMs[s]:0}")));
                var nb = new BodyBuild(sh, null, Outfit.None());
                while (!nb.Step()) yield return null;
                Log($"  same body, no clothes: {nb.Asset.wallMs:0} ms wall: " + string.Join(", ", System.Linq.Enumerable.Select(System.Linq.Enumerable.Range(0, 7), s => $"{(BodyBuild.Stage)s} {nb.StageWallMs[s]:0}")));
            }
            var a = f.Asset;
            var sb = new StringBuilder();
            for (int g = 0; g < a.outfit.Count; g++) sb.Append($"{a.outfit.garments[g].name} {a.garmentTriangles[g]}, ");
            Log($"{name}: {a.triangles} tris = skin {a.skinTriangles} (of {a.skinTrianglesBeforeRemoval} before covered skin was dropped) + head + {sb}generated in {ms:0} ms");
            if (a.outfit.Count == 0 || a.body.subMeshCount != 1 + a.outfit.Count) allDressed = false;
            for (int g = 0; g < a.outfit.Count; g++) if (a.garmentTriangles[g] < 50) allDressed = false;
            yield return null;
        }
        Log(allDressed ? "PASS every role wears real garment meshes (a submesh each)" : "FAIL missing garments");

        // Poses (pictures): walk, run, seated, glide, hang, cuffed, guard + punch.
        var cam = Camera.main; cam.transform.SetParent(null); FlyingVehicle.CameraOverride = true;
        var lg = new GameObject("DressKey"); var l = lg.AddComponent<Light>(); l.type = LightType.Directional; l.intensity = 1.2f;
        lg.transform.rotation = Quaternion.LookRotation(new Vector3(-0.4f, -0.5f, 0.8f));
        IEnumerator Shot(string n, Vector3 at, Vector3 from)
        {
            cam.transform.SetPositionAndRotation(from, Quaternion.LookRotation(at - from));
            yield return new WaitForEndOfFrame(); yield return new WaitForEndOfFrame();
            ScreenCapture.CaptureScreenshot(Path.Combine(dir, $"dressed_{n}.png"));
            yield return null; yield return null;
        }
        Vector3 row = origin + new Vector3(2.4f, 0.9f, 0f);
        yield return Shot("lineup", row, row + new Vector3(0f, 0.2f, -4.2f));
        yield return Shot("lineup_back", row, row + new Vector3(0.5f, 0.3f, 4.2f));
        var poses = new (string, FigureAnimator.Pose, FigureAnimator.Arms, float)[]
        {
            ("walk", FigureAnimator.Pose.Normal, FigureAnimator.Arms.Lowered, 1.4f), ("run", FigureAnimator.Pose.Normal, FigureAnimator.Arms.Lowered, 5f),
            ("seated", FigureAnimator.Pose.Seated, FigureAnimator.Arms.Lowered, 0f), ("glide", FigureAnimator.Pose.Glide, FigureAnimator.Arms.Lowered, 0f),
            ("hang", FigureAnimator.Pose.Hang, FigureAnimator.Arms.Lowered, 0f), ("cuffed", FigureAnimator.Pose.Cuffed, FigureAnimator.Arms.Lowered, 0f),
            ("guard", FigureAnimator.Pose.Normal, FigureAnimator.Arms.Guard, 0f),
        };
        foreach (var (n, pose, arms, speed) in poses)
        {
            foreach (var (f, an) in figs) { an.CurrentPose = pose; an.ArmMode = arms; an.Velocity = Vector3.back * speed; an.Grounded = pose != FigureAnimator.Pose.Glide && pose != FigureAnimator.Pose.Hang; }
            if (arms == FigureAnimator.Arms.Guard) foreach (var (f, an) in figs) an.Punch(1);
            yield return new WaitForSeconds(speed > 0f ? 0.37f : 0.6f);
            yield return Shot(n, row, row + new Vector3(-0.6f, 0.4f, -3.6f));
            if (n == "walk" || n == "seated") yield return Shot(n + "_side", origin + new Vector3(1.2f, 0.9f, 0f), origin + new Vector3(4.2f, 1.2f, -1.2f));
        }
        foreach (var (f, an) in figs) { an.CurrentPose = FigureAnimator.Pose.Normal; an.ArmMode = FigureAnimator.Arms.Lowered; an.Velocity = Vector3.zero; an.Grounded = true; }
        Destroy(lg);
        FlyingVehicle.CameraOverride = false;

        // The city: near pedestrians wear the pool's dressed bodies.
        var peds = FindAnyObjectByType<PedestrianSystem>();
        var pool = BodyPool.Instance;
        float waited = 0f;
        for (; waited < 400f && pool != null && pool.Filling; waited += 1f) yield return new WaitForSeconds(1f);
        Log($"pool: {(pool != null ? pool.Ready : 0)} bodies ready after {waited:0} s more");
        if (peds != null)
        {
            peds.DebugRedress();
            yield return new WaitForSeconds(1f);
            int near = 0, dressed = 0;
            var mine = new HashSet<CharacterFigure>(); foreach (var (f, _) in figs) mine.Add(f);
            foreach (var cf in FindObjectsByType<CharacterFigure>())
            {
                if (cf.Asset == null || mine.Contains(cf)) continue;
                near++;
                if (cf.Asset.outfit != null && cf.Asset.outfit.Count > 0 && cf.Renderers[0] is SkinnedMeshRenderer smr && smr.sharedMesh.subMeshCount > 1) dressed++;
            }
            Log($"city: {near} generated characters near, {dressed} in garment meshes; pool filled: {(pool != null ? (!pool.Filling).ToString() : "-")}");
            Log(near > 0 && dressed == near ? "PASS near characters in the city wear garments" : "FAIL city characters undressed");
        }

        // An ejected driver (pool body) and the player (their own body: first person looking down, third person).
        FlyingVehicle withDriver = null;
        foreach (var c in FlyingVehicle.Active) if (c.hasDriver && !c.IsOccupied && !c.Disabled) { withDriver = c; break; }
        if (withDriver != null)
        {
            var npc = withDriver.EjectDriver(1);
            yield return null;
            var nf = npc != null ? npc.GetComponent<CharacterFigure>() : null;
            bool ok = nf != null && nf.Generated && nf.Asset.outfit.Count > 0;
            Log($"ejected driver: {(nf == null ? "none" : nf.Generated ? string.Join(", ", nf.Asset.outfit.garments.ConvertAll(g => g.name)) : "primitive figure")}");
            Log(ok ? "PASS an ejected driver wears garments" : "FAIL ejected driver undressed");
        }
        var fpc = FirstPersonController.Instance;
        if (fpc != null)
        {
            if (fpc.Figure == null || !fpc.Figure.Generated)
            {
                fpc.SetSheet(new CharacterSheet(Sex.Male, 10, 10, 10, 4242));
                for (float t = 0f; t < 20f && fpc.BodyPending; t += Time.deltaTime) yield return null;
            }
            var pf = fpc.Figure;
            bool pOk = pf != null && pf.Generated && pf.Asset.outfit.Count > 0;
            Log($"player: {(pf != null && pf.Generated ? string.Join(", ", pf.Asset.outfit.garments.ConvertAll(g => g.name)) : "no generated body")}");
            Log(pOk ? "PASS the player wears garments" : "FAIL player undressed");
            var pcam = Camera.main;
            fpc.AttachCamera(pcam);
            FlyingVehicle.CameraOverride = false;
            fpc.zoom.Snap(0f);
            foreach (float pitch in new[] { 45f, 70f })
            {
                fpc.DebugLook(pitch, fpc.transform.eulerAngles.y);
                for (int k = 0; k < 10; k++) yield return null;
                yield return new WaitForEndOfFrame();
                ScreenCapture.CaptureScreenshot(Path.Combine(dir, $"dressed_player_fp_{pitch:0}.png"));
                yield return null;
                // Where the eye is: a marker, seen from the side by a second camera.
                var eyeAt = pcam.transform.position;
                var marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                Destroy(marker.GetComponent<Collider>());
                marker.transform.position = eyeAt; marker.transform.localScale = Vector3.one * 0.03f;
                marker.GetComponent<Renderer>().sharedMaterial = CharacterFigure.Mat(Color.red);
                var side = new GameObject("SideCam").AddComponent<Camera>();
                Vector3 r = fpc.transform.right;
                side.transform.SetPositionAndRotation(fpc.transform.position + Vector3.up * 1.3f + r * 1.4f, Quaternion.LookRotation(-r));
                side.fieldOfView = 40f; side.depth = pcam.depth + 1;
                var prevMode = fpc.Figure.Renderers[0] is SkinnedMeshRenderer sm0 ? sm0.sharedMesh : null;
                yield return new WaitForEndOfFrame();
                ScreenCapture.CaptureScreenshot(Path.Combine(dir, $"dressed_player_fp_{pitch:0}_side.png"));
                yield return null;
                Log($"first person at {pitch:0} deg pitch: eye {fpc.transform.InverseTransformPoint(eyeAt)} (player space), camera fwd {fpc.transform.InverseTransformDirection(pcam.transform.forward)}");
                Destroy(side.gameObject); Destroy(marker);
            }
            fpc.zoom.Snap(1f);
            fpc.DebugLook(15f, fpc.transform.eulerAngles.y);
            for (int k = 0; k < 10; k++) yield return null;
            yield return new WaitForEndOfFrame();
            ScreenCapture.CaptureScreenshot(Path.Combine(dir, "dressed_player_third.png"));
            yield return null;
        }
    }

    // ---------- clothing phase 1: garment fields ----------

    IEnumerator GarmentsTest()
    {
        string dir = Path.GetDirectoryName(Application.dataPath);
        var origin = new Vector3(0f, 1500f, 0f);
        var specs = new (string, CharacterSheet)[]
        {
            ("M S20 D1", new CharacterSheet(Sex.Male, 20, 10, 1, 1000)), ("F S20 D1", new CharacterSheet(Sex.Female, 20, 10, 1, 1000)),
            ("M S10 D20", new CharacterSheet(Sex.Male, 10, 10, 20, 1000)), ("F S10 D10", new CharacterSheet(Sex.Female, 10, 10, 10, 1000)),
        };
        var lineup = new GameObject("GarmentLineup").AddComponent<BodyLineupDebug>();
        lineup.enabled = false;
        var shirt = GarmentDef.TShirt(); var pants = GarmentDef.Pants(); var coat = GarmentDef.Coat();
        bool hollowsOk = true, tentOk = true, legsOk = true;
        for (int i = 0; i < specs.Length; i++)
        {
            var (name, sh) = specs[i];
            var plan = BodyPlanner.Generate(sh);
            using var sdf = new BodySDF(plan);
            var k = sdf.Kernel;
            using var fs = new GarmentField(sdf, shirt);
            using var fp = new GarmentField(sdf, pants);
            var ks = fs.Kernel; var kp = fp.Kernel;

            // Standoff: from inside the body along dir, where the skin ends and where the garment's outer
            // surface ends (cm between them).
            float Exit(System.Func<Vector3, float> f, Vector3 from, Vector3 dirn) { for (float t = 0f; t < 0.5f; t += 0.0005f) if (f(from + dirn * t) >= 0f) return t; return 0.5f; }
            float Standoff(GarmentKernel g, Vector3 from, Vector3 dirn) => (Exit(g.OuterSolid, from, dirn) - Exit(k.Skin, from, dirn)) * 100f;
            Vector3 J(string b) => sdf.start[plan.Index(b)];
            Vector3 Ax(string b, float t) { int bi = plan.Index(b); return Vector3.Lerp(sdf.start[bi], sdf.end[bi], t); }

            // Front of the lumbar (navel height) against the sides; the spine groove at the chest.
            Vector3 lum = Ax("Spine", 0.45f), chestMid = Ax("Chest", 0.5f);
            float navel = Standoff(ks, lum, Vector3.forward), navelSide = Standoff(ks, lum, (Vector3.forward + Vector3.right).normalized);
            float spine = Standoff(ks, chestMid, Vector3.back);
            // Under the chest / breasts (front, low on the chest) and below the belly.
            float underBust = Standoff(ks, Ax("Chest", 0.12f), Vector3.forward), bust = Standoff(ks, Ax("Chest", 0.45f), Vector3.forward);
            float belly = Standoff(ks, Ax("Spine", 0.05f) + Vector3.down * 0.03f, Vector3.forward);
            // Pants: between the thighs, half way down: outside both leg tubes?
            Vector3 thighL = Ax("HipL", 0.6f), thighR = Ax("HipR", 0.6f), between = (thighL + thighR) * 0.5f;
            float crotchGap = kp.OuterSolid(between) * 100f, bodyGap = k.Skin(between) * 100f;
            float legL = Standoff(kp, thighL, Vector3.left), legR = Standoff(kp, thighR, Vector3.right);
            Log($"{name}: shirt {fs.buildMs:0} ms, pants {fp.buildMs:0} ms | standoff (cm): navel {navel:0.0}, navel side {navelSide:0.0}, spine {spine:0.0}, " +
                $"under bust {underBust:0.0} vs bust {bust:0.0}, below belly {belly:0.0} | pants: legs {legL:0.0} / {legR:0.0}, between the thighs (body {bodyGap:0.0} cm apart) {crotchGap:0.0} cm outside");
            float min = (shirt.thickness + shirt.gap) * 100f;
            if (navel < min - 0.05f || spine < min - 0.05f) hollowsOk = false;
            if (sh.sex == Sex.Female && sh.DEX >= 10 && underBust < bust + 0.5f) tentOk = false;   // (a heavy belly fills the space under the bust)
            if (bodyGap > 1f && crotchGap <= 0f) legsOk = false;                                  // (heavy thighs touch: nothing to keep apart)

            // Pictures: body + shirt + pants (+ a coat on the first) as debug meshes.
            var root = new GameObject(name).transform;
            root.position = origin + new Vector3(i * 1.1f, 0f, 0f);
            var (bm, _) = lineup.BuildMesh(plan);
            Mesh(root, bm, lineup.skinColor);
            foreach (var g in (i == 0 ? new[] { coat } : new[] { shirt, pants }))
            {
                using var f = new GarmentField(sdf, g);
                Mesh(root, f.BuildDebugMesh(0.006f), g.color);
            }
            yield return null;
        }
        Log(hollowsOk ? "PASS shirts bridge the navel and spine groove (never closer than thickness + gap)" : "FAIL garment hugs a hollow");
        Log(tentOk ? "PASS shirts tent below the bust (more standoff under it than on it)" : "FAIL no tent below the bust");
        Log(legsOk ? "PASS pants: separate leg tubes wherever the thighs are apart" : "FAIL pants fill the gap between the legs");

        var cam = Camera.main; cam.transform.SetParent(null); FlyingVehicle.CameraOverride = true;
        TimeOfDay.Instance?.SetHour(12f);
        var lg = new GameObject("GarmentKey"); var l = lg.AddComponent<Light>(); l.type = LightType.Directional; l.intensity = 1.3f;
        lg.transform.rotation = Quaternion.LookRotation(new Vector3(-0.4f, -0.5f, -0.8f));
        IEnumerator Shot(string n, Vector3 at, Vector3 from)
        {
            cam.transform.SetPositionAndRotation(from, Quaternion.LookRotation(at - from));
            yield return new WaitForEndOfFrame(); yield return new WaitForEndOfFrame();
            ScreenCapture.CaptureScreenshot(Path.Combine(dir, $"garments_{n}.png"));
            yield return null; yield return null;
        }
        Vector3 mid = origin + new Vector3(1.65f, 0.95f, 0f);
        yield return Shot("front", mid, mid + new Vector3(0f, 0.15f, 3.6f));
        yield return Shot("side", mid, mid + new Vector3(4.2f, 0.3f, 1.2f));
        for (int i = 0; i < specs.Length; i++)
        {
            Vector3 c = origin + new Vector3(i * 1.1f, 1.05f, 0f);
            yield return Shot("close" + i, c, c + new Vector3(0.9f, 0.1f, 1.3f));
        }
        Destroy(lg);
        FlyingVehicle.CameraOverride = false;

        static void Mesh(Transform parent, Mesh m, Color c)
        {
            var go = new GameObject(m.name);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = m;
            go.AddComponent<MeshRenderer>().sharedMaterial = CharacterFigure.Mat(c);
        }
    }

    // ---------- day / night ----------

    IEnumerator DayNightTest()
    {
        string dir = Path.GetDirectoryName(Application.dataPath);
        yield return null;
        var tod = TimeOfDay.Instance;
        if (tod == null) { Log("FAIL no TimeOfDay in the scene"); yield break; }
        Log($"clock: starts {tod.startHour:0.0} h, {tod.dayMinutes} min day; now {tod.Hour:0.00} h, day amount {tod.DayAmount:0.00}");
        Log(Mathf.Abs(tod.Hour - tod.startHour) < 0.2f && tod.DayAmount == 0f ? "PASS starts at night (22:00)" : "FAIL start time");

        // Smooth: the blend never jumps (per game minute) and is flat outside dawn / dusk.
        float worst = 0f, prev = tod.DayAt(0f);
        for (float h = 1f / 60f; h <= 24f; h += 1f / 60f) { float v = tod.DayAt(h); worst = Mathf.Max(worst, Mathf.Abs(v - prev)); prev = v; }
        bool flat = tod.DayAt(3f) == 0f && tod.DayAt(23f) == 0f && tod.DayAt(12f) == 1f && tod.DayAt(7.5f) == 1f && tod.DayAt(17.5f) == 1f;
        Log($"blend: worst step {worst:0.0000} per game minute (a 2 h ramp: ~0.0125 at its steepest)");
        Log(worst < 0.02f && flat ? "PASS smooth dawn (05-07) and dusk (18-20), steady night and day" : "FAIL blend");

        // The clock runs at 1 game hour per real minute.
        float h0 = tod.Hour; yield return new WaitForSeconds(3f); float rate = Mathf.Repeat(tod.Hour - h0, 24f) / 3f * 60f;
        Log($"clock rate: {rate:0.00} game hours per real minute");
        Log(Mathf.Abs(rate - 1f) < 0.05f ? "PASS 1 real minute = 1 hour" : "FAIL clock rate");

        // Looks at four times: values and pictures (street level and above the city).
        var fpc = FirstPersonController.Instance;
        var cam = Camera.main;
        var sun = RenderSettings.sun;
        Light dirLight = null; foreach (var l in FindObjectsByType<Light>()) if (l.type == LightType.Directional) dirLight = l;
        Vector3 street = fpc != null ? fpc.transform.position : Vector3.zero;
        cam.transform.SetParent(null); FlyingVehicle.CameraOverride = true;
        if (fpc != null) fpc.enabled = false;
        IEnumerator Shot(string name, Vector3 at, Vector3 from)
        {
            cam.transform.SetPositionAndRotation(from, Quaternion.LookRotation(at - from));
            yield return new WaitForEndOfFrame(); yield return new WaitForEndOfFrame();
            ScreenCapture.CaptureScreenshot(Path.Combine(dir, $"daynight_{name}.png"));
            yield return null; yield return null;
        }
        foreach (var (name, hour) in new[] { ("night_22", 22f), ("dawn_06", 6f), ("day_12", 12f), ("dusk_19", 19f) })
        {
            tod.SetHour(hour);
            yield return null;
            var hf = HeightFogSettings.Active;
            Log($"{hour:00}:00 day {tod.DayAmount:0.00}: light {(dirLight != null ? dirLight.intensity : 0f):0.00}, ambient {RenderSettings.ambientLight}, fog {RenderSettings.fogColor}, smog low {(hf != null ? hf.lowColor.ToString() : "-")} high {(hf != null ? hf.highColor.ToString() : "-")}");
            yield return Shot(name + "_street", street + new Vector3(0f, 1.6f, 0f) + Vector3.forward * 40f, street + new Vector3(0f, 1.7f, 0f));
            yield return Shot(name + "_above", street + new Vector3(0f, 20f, 200f), street + new Vector3(-40f, 90f, -60f));
        }
        tod.SetHour(22f);
        bool nightOk = Mathf.Abs(RenderSettings.ambientLight.g - 0.04f) < 0.005f && dirLight != null && Mathf.Abs(dirLight.intensity - 0.12f) < 0.01f;
        tod.SetHour(12f);
        bool dayOk = Mathf.Abs(RenderSettings.ambientLight.g - 0.36f) < 0.005f && dirLight != null && Mathf.Abs(dirLight.intensity - 0.9f) < 0.01f;
        Log(nightOk && dayOk ? "PASS night and day hit the spec's light, ambient and fog values" : "FAIL look values");
        tod.SetHour(22f);
        if (fpc != null) fpc.enabled = true;
        FlyingVehicle.CameraOverride = false;
    }

    // ---------- car size and seats ----------

    IEnumerator SeatsTest()
    {
        string dir = Path.GetDirectoryName(Application.dataPath);
        yield return null;
        FlyingVehicle car = null;
        foreach (var c in FlyingVehicle.Active) if (!c.IsOccupied) { car = c; break; }
        if (car == null) { Log("FAIL no car in the scene"); yield break; }
        // A test car of our own, far from traffic: the scene's car, copied and parked in the sky.
        var origin = new Vector3(0f, 1500f, 0f);
        var copy = Instantiate(car.gameObject);
        foreach (var mb in copy.GetComponentsInChildren<MonoBehaviour>()) if (!(mb is CarLights)) mb.enabled = false;
        var v = copy.GetComponent<FlyingVehicle>();
        copy.transform.SetPositionAndRotation(origin, Quaternion.identity);
        foreach (var r in copy.GetComponentsInChildren<Renderer>(true)) r.enabled = true; // (traffic is drawn instanced)
        var col = copy.GetComponentInChildren<BoxCollider>();
        Vector3 half = Vector3.Scale(col.size, col.transform.lossyScale) * 0.5f, centre = copy.transform.InverseTransformPoint(col.transform.TransformPoint(col.center));
        Vector3 size = half * 2f;
        float roof = origin.y + centre.y + half.y, under = origin.y + centre.y - half.y;
        Log($"car body {size.x:0.00} x {size.y:0.00} x {size.z:0.00} m");

        // Standing average man beside it: car length in characters, roof against the shoulders.
        var sgo = new GameObject("Standing");
        sgo.transform.position = new Vector3(origin.x + 2f, under, origin.z);
        var sf = CharacterFigure.BuildGenerated(sgo.transform, new CharacterSheet(Sex.Male, 10, 10, 10, 1000), CharacterFigure.Role.Civilian, true, false);
        var sa = sgo.AddComponent<FigureAnimator>(); sa.Grounded = true;
        yield return new WaitForSeconds(0.3f);
        float shoulder = (sf.ShoulderL.position.y + sf.ShoulderR.position.y) * 0.5f - under;
        float lengthInChars = size.z / sf.Height;
        Log($"standing average man {sf.Height:0.00} m, shoulders {shoulder:0.00} m: car is {lengthInChars:0.00} characters long, roof {size.y:0.00} m");
        Log(lengthInChars > 2.5f && lengthInChars < 2.9f && size.y < shoulder ? "PASS about 2.7 characters long, roof below shoulder height" : "FAIL car proportions");

        // Five seated figures (a spread of sheets): hips at the seat, head top under the roof, inside the sides.
        var sheets = new[]
        {
            new CharacterSheet(Sex.Male, 10, 10, 10, 1001), new CharacterSheet(Sex.Female, 10, 10, 10, 1002),
            new CharacterSheet(Sex.Male, 20, 10, 10, 1003), new CharacterSheet(Sex.Female, 12, 10, 4, 1004), new CharacterSheet(Sex.Male, 6, 10, 15, 1005),
        };
        var seated = new List<(CharacterFigure f, FlyingVehicle.SeatId seat)>();
        for (int i = 0; i < FlyingVehicle.SeatCount; i++)
        {
            var go = new GameObject("Seat" + i);
            go.transform.SetPositionAndRotation(origin, Quaternion.identity);
            var f = CharacterFigure.BuildGenerated(go.transform, sheets[i], CharacterFigure.Role.Civilian, true, false);
            var a = go.AddComponent<FigureAnimator>(); a.Grounded = true; a.CurrentPose = FigureAnimator.Pose.Seated;
            seated.Add((f, (FlyingVehicle.SeatId)i));
            yield return null;
        }
        yield return new WaitForSeconds(0.6f);
        bool ok = true;
        var sb = new StringBuilder("seated: ");
        foreach (var (f, seat) in seated)
        {
            Vector3 hips = copy.transform.TransformPoint(FlyingVehicle.SeatHipsFor(half, centre, seat));
            // Hip joints (the thighs' joints) onto the seat.
            Vector3 hipJoints = (f.HipL.position + f.HipR.position) * 0.5f;
            f.transform.position += hips - hipJoints;
            yield return null;
            var sk = f.GetComponent<BodySkeleton>();
            float headTop = sk.Bones[f.Plan.Index("Head")].End.y;
            float hipsAbove = (f.HipL.position.y + f.HipR.position.y) * 0.5f - under;
            float clear = roof - headTop;
            float halfW = 0f;
            foreach (var r in f.Renderers) { var b = r.bounds; halfW = Mathf.Max(halfW, Mathf.Max(Mathf.Abs(b.max.x - origin.x), Mathf.Abs(b.min.x - origin.x))); }
            sb.Append($"{seat} ({f.Plan.sheet.sex.ToString()[0]} S{f.Plan.sheet.STR} D{f.Plan.sheet.DEX}) hips {hipsAbove:0.00} m, head top {headTop - under:0.00} m ({clear * 100f:0} cm under the roof); ");
            if (clear < 0f || Mathf.Abs(hipsAbove - FlyingVehicle.SeatHipHeight) > 0.02f) ok = false;
        }
        Log(sb.ToString());
        Log(ok ? "PASS five seats: hips 0.3 m above the underside, heads under the roof" : "FAIL seated figures don't fit");

        // Pictures: the car with its body hidden (who sits where), and the car beside the standing man.
        var cam = Camera.main; cam.transform.SetParent(null); FlyingVehicle.CameraOverride = true;
        var lg = new GameObject("SeatKey"); var l = lg.AddComponent<Light>(); l.type = LightType.Directional; l.intensity = 1.2f;
        lg.transform.rotation = Quaternion.LookRotation(new Vector3(0.3f, -0.6f, 0.8f));
        IEnumerator Shot(string name, Vector3 at, Vector3 from)
        {
            cam.transform.SetPositionAndRotation(from, Quaternion.LookRotation(at - from));
            yield return new WaitForEndOfFrame(); yield return new WaitForEndOfFrame();
            ScreenCapture.CaptureScreenshot(Path.Combine(dir, $"seats_{name}.png"));
            yield return null; yield return null;
        }
        Vector3 mid = origin + Vector3.up * 0.1f;
        yield return Shot("car_and_man", mid, mid + new Vector3(-6f, 1.2f, -2.5f));
        var bodyR = copy.transform.Find("Body").GetComponent<Renderer>();
        bodyR.enabled = false;
        yield return Shot("seated_side", mid, mid + new Vector3(-5f, 0.8f, 0f));
        yield return Shot("seated_top", mid, mid + new Vector3(-1.5f, 4.5f, -1.5f));
        bodyR.enabled = true;
        Destroy(lg);
        FlyingVehicle.CameraOverride = false;
    }

    // ---------- foot planting: does the stance foot slide? ----------

    IEnumerator FeetTest()
    {
        var origin = new Vector3(0f, 1500f, 0f);
        var bodies = new List<(string name, CharacterFigure f, FigureAnimator a)>();
        void Add(string name, CharacterFigure f) { var a = f.gameObject.AddComponent<FigureAnimator>(); a.Grounded = true; bodies.Add((name, f, a)); }
        var specs = new (string, CharacterSheet)[]
        {
            ("average man", new CharacterSheet(Sex.Male, 10, 10, 10, 1000)), ("STR 20", new CharacterSheet(Sex.Male, 20, 10, 10, 1000)),
            ("STR 1", new CharacterSheet(Sex.Male, 1, 10, 10, 1000)), ("heavy (DEX 1)", new CharacterSheet(Sex.Male, 10, 10, 1, 1000)),
            ("woman", new CharacterSheet(Sex.Female, 10, 10, 10, 1000)),
        };
        int col = 0;
        foreach (var (name, sh) in specs)
        {
            var go = new GameObject(name); go.transform.position = origin + new Vector3(col++ * 2f, 0f, 0f);
            Add(name, CharacterFigure.BuildGenerated(go.transform, sh, CharacterFigure.Role.Civilian, false, false));
            yield return null;
        }
        { var go = new GameObject("primitive"); go.transform.position = origin + new Vector3(col++ * 2f, 0f, 0f); Add("primitive", CharacterFigure.Build(go.transform, CharacterFigure.Role.Civilian)); }

        float worst = 0f;
        foreach (float speed in new[] { 1.4f, 5f })
        {
            foreach (var b in bodies) { b.a.Velocity = Vector3.forward * speed; b.f.transform.position = new Vector3(b.f.transform.position.x, origin.y, origin.z); }
            var slide = new float[bodies.Count]; var planted = new int[bodies.Count]; var samples = new int[bodies.Count];
            var prev = new Vector3[bodies.Count]; var prevFoot = new int[bodies.Count];
            for (float t = 0f; t < 4f; t += Time.deltaTime)
            {
                float dt = Mathf.Max(Time.deltaTime, 1e-4f);
                foreach (var b in bodies) b.f.transform.position += Vector3.forward * speed * dt; // the root moves like a walking character's
                yield return null; // the animator poses in LateUpdate
                if (t < 1f) { for (int i = 0; i < bodies.Count; i++) prevFoot[i] = -1; continue; }
                for (int i = 0; i < bodies.Count; i++)
                {
                    var f = bodies[i].f;
                    // The stance foot is the lower ankle; its horizontal speed is how fast it slides.
                    int foot = f.AnkleL.position.y < f.AnkleR.position.y ? 0 : 1;
                    Vector3 p = foot == 0 ? f.AnkleL.position : f.AnkleR.position;
                    if (prevFoot[i] == foot)
                    {
                        Vector3 v = (p - prev[i]) / dt; v.y = 0f;
                        slide[i] += v.magnitude; samples[i]++;
                        if (v.magnitude < 0.25f * speed) planted[i]++;
                    }
                    prev[i] = p; prevFoot[i] = foot;
                }
            }
            var sb = new StringBuilder($"{(speed < 2f ? "walk" : "run")} {speed} m/s, stance foot slide / body speed: ");
            for (int i = 0; i < bodies.Count; i++)
            {
                float r = slide[i] / Mathf.Max(1, samples[i]) / speed;
                worst = Mathf.Max(worst, r);
                sb.Append($"{bodies[i].name} {r * 100f:0}% (planted {100f * planted[i] / Mathf.Max(1, samples[i]):0}%), ");
            }
            Log(sb.ToString());
        }
        Log(worst < 0.2f ? $"PASS feet plant (worst slide {worst * 100f:0}% of body speed)" : $"FAIL feet slide (worst {worst * 100f:0}% of body speed)");
    }

    // ---------- generated bodies: phase 8 (pool, async, LOD, perf) ----------

    IEnumerator PoolTest()
    {
        string dir = Path.GetDirectoryName(Application.dataPath);
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = -1;
        var pool = BodyPool.Ensure();
        var fpc = FirstPersonController.Instance;
        var ps = PedestrianSystem.Instance;

        // 1. The pool fills in the background: progress and frame times while it does.
        var fillFrames = new List<float>();
        float t = 0f, nextLog = 0f, presetIn = -1f;
        while (pool.Filling && t < 240f)
        {
            yield return null;
            t += Time.unscaledDeltaTime;
            fillFrames.Add(Time.unscaledDeltaTime * 1000f);
            if (presetIn < 0f && pool.PresetSeconds >= 0f) presetIn = t;
            if (t >= nextLog) { Log($"  pool {pool.Ready}/{pool.Total} after {t:0} s"); nextLog += 10f; }
        }
        Log($"preset: {pool.PresetLoaded} baked bodies loaded in {pool.PresetSeconds:0.00} s{(pool.PresetProblem != null ? " (" + pool.PresetProblem + ")" : "")}; the rest generated");
        Log(pool.PresetLoaded == 0 ? "WARN no preset loaded (bake one: Tools > Characters > Bake Body Pool)"
            : pool.PresetSeconds < 5f ? "PASS the baked preset fills its slots at startup" : $"WARN the preset loads slowly ({pool.PresetSeconds:0.0} s for {pool.PresetLoaded} bodies)");
        fillFrames.Sort();
        float Avg(List<float> l) { float x = 0f; foreach (var v in l) x += v; return l.Count > 0 ? x / l.Count : 0f; }
        float Pct(List<float> l, float q) => l.Count > 0 ? l[Mathf.Clamp((int)(l.Count * q), 0, l.Count - 1)] : 0f;
        var wall = new List<float>(pool.WallMs); var main = new List<float>(pool.MainMs); wall.Sort(); main.Sort();
        float tri = 0f; foreach (var x in pool.Triangles) tri += x; tri /= Mathf.Max(1, pool.Triangles.Count);
        Log($"pool filled: {pool.Ready}/{pool.Total} bodies in {pool.FillSeconds:0.0} s ({pool.Failed} failed), {pool.maxInFlight} in flight; " +
            $"per body {Avg(wall):0} ms start-to-finish (median {Pct(wall, 0.5f):0}), main thread {Avg(main):0.0} ms (max {Pct(main, 1f):0.0}), {tri:0} triangles");
        Log($"frames while filling: avg {Avg(fillFrames):0.0} ms, p95 {Pct(fillFrames, 0.95f):0.0} ms, worst {Pct(fillFrames, 1f):0.0} ms; pool's own main-thread step worst {pool.WorstStepMs:0.0} ms");
        Log("  main-thread ms per stage (worst body): " + string.Join(", ", System.Linq.Enumerable.Select(System.Linq.Enumerable.Range(0, 8), i => $"{(BodyBuild.Stage)i} {pool.StageMaxMs[i]:0.0}")));
        Log(pool.Ready == pool.Total ? "PASS the pool fills in the background" : "FAIL pool incomplete");
        Log(Pct(main, 1f) < 25f ? "PASS generation stays off the main thread (only start and upload run there)" : "WARN a body spent long on the main thread");

        // 2. The crowd: the player in the street, the near tier re-dressed from the pool.
        fpc.PlaceAt(new Vector3(27f, 1f, -92f), Quaternion.Euler(0f, 180f, 0f));
        fpc.DebugLook(5f, 180f);
        yield return new WaitForSeconds(1f);
        ps.DebugRedress();
        yield return new WaitForSeconds(3f);
        int near = ps.NearAgents.Count, gen = 0;
        foreach (var p in ps.NearAgents) if (p.near != null && p.near.GetComponent<CharacterFigure>().Generated) gen++;
        IEnumerator Measure(List<float> into, float seconds)
        {
            for (float m = 0f; m < seconds; m += Time.unscaledDeltaTime) { yield return null; into.Add(Time.unscaledDeltaTime * 1000f); }
            into.Sort();
        }
        var genFrames = new List<float>();
        yield return Measure(genFrames, 15f);
        Log($"crowd with generated bodies: {near} near pedestrians ({gen} generated, ~{gen * tri / 1000f:0}k skinned triangles), {ps.FarDrawn} far; " +
            $"frame avg {Avg(genFrames):0.0} ms, p95 {Pct(genFrames, 0.95f):0.0}, worst {Pct(genFrames, 1f):0.0}");
        Log(gen >= near * 0.9f && near > 0 ? "PASS near pedestrians wear pooled generated bodies" : "FAIL near tier not generated");
        fpc.zoom.Snap(0.7f);
        fpc.DebugLook(12f, 180f);
        yield return new WaitForSeconds(0.6f);
        ScreenCapture.CaptureScreenshot(Path.Combine(dir, "pool_crowd.png"));
        yield return null; yield return null;
        fpc.zoom.Snap(0f);
        fpc.DebugLook(5f, 180f);

        // Far tier scale: a far pedestrian's instance matches its body.
        foreach (var p in ps.All)
        {
            if (p.near != null || p.state == PedestrianSystem.State.Gone) continue;
            var sc = ps.DebugFarScale(p);
            var a = BodyPool.Civilian(p.seed);
            if (a == null) continue;
            Log($"far tier: instance scale {sc}, far mesh height {1.5f * sc.y:0.00} m vs the body's {a.height:0.00} m, width {0.30f * sc.x:0.00} vs {a.width:0.00} m");
            Log(Mathf.Abs(1.5f * sc.y - a.height) < 0.01f ? "PASS far tier sized from the body's DNA (no pop on promotion)" : "FAIL far scale");
            break;
        }

        // Same crowd on the primitive figures, for comparison.
        BodyPool.Enabled = false;
        ps.DebugRedress();
        yield return new WaitForSeconds(3f);
        var primFrames = new List<float>();
        yield return Measure(primFrames, 15f);
        Log($"same crowd on primitive figures: {ps.NearAgents.Count} near; frame avg {Avg(primFrames):0.0} ms, p95 {Pct(primFrames, 0.95f):0.0}, worst {Pct(primFrames, 1f):0.0}");
        BodyPool.Enabled = true;
        ps.DebugRedress();
        yield return new WaitForSeconds(1f);

        // 3. Officers from the officers' sub-pool.
        PoliceDriver unit = null;
        foreach (var u in PoliceDispatch.Units) if (u != null && u.isActiveAndEnabled) { unit = u; break; }
        if (unit != null)
        {
            var o = OfficerAgent.SpawnFrom(unit, 0f);
            var of = o.GetComponent<CharacterFigure>();
            Log($"officer: generated {of.Generated}, sheet {of.Asset?.sheet}, uniform '{of.Renderers[0].sharedMaterials[0].name}'");
            Log(of.Generated && of.Asset.sheet.seed >= 50000 ? "PASS officers draw from the officer sub-pool" : "FAIL officer body");
            Destroy(o.gameObject);
        }

        // 4. The player's stats change: a new body, generated in the background, swapped in.
        float h0 = fpc.Figure.Height;
        var swapFrames = new List<float>();
        fpc.SetSheet(new CharacterSheet(Sex.Male, 20, 12, 3, 77));
        float waited = 0f;
        while (fpc.BodyPending && waited < 20f) { yield return null; waited += Time.unscaledDeltaTime; swapFrames.Add(Time.unscaledDeltaTime * 1000f); }
        for (int k = 0; k < 5; k++) { yield return null; swapFrames.Add(Time.unscaledDeltaTime * 1000f); }
        swapFrames.Sort();
        Log($"player stats changed: body {h0:0.00} m -> {fpc.Figure.Height:0.00} m in {waited:0.00} s, generated {fpc.Figure.Generated}; worst frame meanwhile {Pct(swapFrames, 1f):0.0} ms");
        Log(!fpc.BodyPending && Mathf.Abs(fpc.Figure.Height - h0) > 0.05f ? "PASS the player's body regenerates async and swaps in" : "FAIL player swap");
        var w = fpc.GetComponent<PlayerWeapon>();
        w.Draw();
        yield return new WaitForSeconds(0.6f);
        Log($"  after the swap the gun is on {w.Gun.transform.parent.name}, aiming {w.Aiming}");
        w.Holster();
        fpc.zoom.Snap(0.6f);
        fpc.DebugLook(15f, 20f);
        yield return new WaitForSeconds(0.6f);
        ScreenCapture.CaptureScreenshot(Path.Combine(dir, "pool_player_swapped.png"));
        yield return null; yield return null;
        fpc.zoom.Snap(0f);
    }

    // ---------- generated bodies: phase 7 (eyes, INT glow, skin) ----------

    IEnumerator FaceTest()
    {
        string dir = Path.GetDirectoryName(Application.dataPath);
        var origin = new Vector3(0f, 1500f, 0f);
        var sh = BodyMaterials.SkinShader;
        Log($"skin shader: {(sh != null ? sh.name + (sh.isSupported ? " (supported)" : " (NOT supported)") : "missing")}");
        Log(sh != null && sh.isSupported ? "PASS skin shader loads in the player build" : "FAIL skin shader missing");

        var sheets = new (string name, CharacterSheet sh)[]
        {
            ("INT 10", new CharacterSheet(Sex.Male, 10, 10, 10, 1000)), ("INT 16", new CharacterSheet(Sex.Male, 10, 16, 10, 1003)),
            ("INT 20", new CharacterSheet(Sex.Male, 10, 20, 10, 1004)), ("woman INT 20", new CharacterSheet(Sex.Female, 10, 20, 14, 1005)),
            ("dark tone", new CharacterSheet(Sex.Female, 12, 10, 10, 1011)),
        };
        var figs = new List<CharacterFigure>();
        for (int i = 0; i < sheets.Length; i++)
        {
            var go = new GameObject(sheets[i].name);
            go.transform.position = origin + new Vector3(i * 0.9f, 0f, 0f);
            go.transform.rotation = Quaternion.Euler(0f, 180f, 0f); // facing -z, toward the camera
            var f = CharacterFigure.BuildGenerated(go.transform, sheets[i].sh, CharacterFigure.Role.Civilian, true, false);
            var a = go.AddComponent<FigureAnimator>(); a.Grounded = true; a.LookYaw = 180f;
            figs.Add(f);
            yield return null;
        }
        var sb = new StringBuilder("eyes: ");
        foreach (var f in figs)
        {
            var iris = f.EyeL.Find("Eyeball").GetComponent<Renderer>().sharedMaterial;
            sb.Append($"{f.name} glow {f.Plan.dna.eyeGlow:0.00} iris '{iris.name}' ({iris.shader.name}); ");
        }
        Log(sb.ToString());
        Log($"skin: {figs[0].Renderers[0].sharedMaterial.name} on {figs[0].Renderers[0].sharedMaterial.shader.name}, tones {string.Join(", ", figs.ConvertAll(f => f.Renderers[0].sharedMaterial.name))}");
        var glowMat = figs[2].EyeL.Find("Eyeball").GetComponent<Renderer>().sharedMaterial;
        bool glowOk = figs[0].Plan.dna.eyeGlow == 0f && figs[2].Plan.dna.eyeGlow == 1f && glowMat.HasProperty("_Glow") && glowMat.GetColor("_Glow").maxColorComponent > 1f;
        Log(glowOk ? "PASS INT 20 eyes glow (HDR), INT 10 don't" : "FAIL eye glow");

        // Eyes follow a target to the side (within reach).
        var target = new GameObject("LookTarget").transform;
        foreach (var f in figs) f.LookTarget = null;
        yield return new WaitForSeconds(0.3f);
        var f0 = figs[0];
        target.position = f0.EyeL.position + f0.transform.forward * 2f + f0.transform.right * 0.6f + Vector3.up * 0.3f;
        f0.LookTarget = target.position;
        yield return new WaitForSeconds(0.4f);
        float errL = Vector3.Angle(f0.EyeL.forward, target.position - f0.EyeL.position), errR = Vector3.Angle(f0.EyeR.forward, target.position - f0.EyeR.position);
        Log($"eyes on a target 18 deg aside: off by {errL:0.0} / {errR:0.0} deg");
        Log(errL < 3f && errR < 3f ? "PASS eyeballs look at their target" : "FAIL eye look");
        foreach (var f in figs) f.LookTarget = null;

        // Neon: a magenta and a cyan point light (additional lights), a dim key light.
        var lights = new List<GameObject>();
        GameObject L(LightType t, Color c, float intensity, Vector3 pos, Quaternion rot, float range = 4f)
        {
            var g = new GameObject("FaceLight"); var l = g.AddComponent<Light>();
            l.type = t; l.color = c; l.intensity = intensity; l.range = range;
            g.transform.SetPositionAndRotation(pos, rot); lights.Add(g); return g;
        }
        Vector3 faces = origin + new Vector3(1.8f, 1.4f, 0f);
        L(LightType.Directional, new Color(1f, 0.95f, 0.9f), 0.6f, Vector3.zero, Quaternion.LookRotation(new Vector3(0.3f, -0.4f, 1f)));
        L(LightType.Point, new Color(1f, 0.2f, 0.85f), 6f, faces + new Vector3(-2.4f, 0.2f, -0.8f), Quaternion.identity, 4f);
        L(LightType.Point, new Color(0.2f, 0.85f, 1f), 6f, faces + new Vector3(2.4f, 0.1f, -0.8f), Quaternion.identity, 4f);

        var cam = Camera.main;
        cam.transform.SetParent(null);
        FlyingVehicle.CameraOverride = true;
        IEnumerator Shot(string name, Vector3 at, Vector3 from)
        {
            cam.transform.SetPositionAndRotation(from, Quaternion.LookRotation(at - from));
            yield return new WaitForEndOfFrame(); yield return new WaitForEndOfFrame();
            ScreenCapture.CaptureScreenshot(Path.Combine(dir, $"face_{name}.png"));
            yield return null; yield return null;
        }
        yield return Shot("row", faces + new Vector3(0f, -0.1f, 0f), faces + new Vector3(0f, 0.05f, -2.6f));
        for (int i = 0; i < 4; i++)
        {
            var f = figs[i];
            Vector3 h = f.Head.position;
            yield return Shot("close" + i, h, h - f.transform.forward * -0.55f + new Vector3(0.12f, 0.03f, 0f));
        }
        Vector3 body = figs[3].transform.position + Vector3.up * 1.0f;
        yield return Shot("body_neon", body, body + new Vector3(0.6f, 0.2f, -1.9f));
        foreach (var g in lights) Destroy(g);
        FlyingVehicle.CameraOverride = false;
    }

    // ---------- generated bodies: phase 6 (animation) ----------

    IEnumerator AnimTest()
    {
        string dir = Path.GetDirectoryName(Application.dataPath);
        var origin = new Vector3(0f, 1500f, 0f);
        CharacterFigure Make(string name, CharacterSheet sh, Vector3 at, bool animator, ISpeciesTemplate t = null)
        {
            var go = new GameObject(name);
            go.transform.position = origin + at;
            CharacterFigure f;
            if (t == null) f = CharacterFigure.BuildGenerated(go.transform, sh, CharacterFigure.Role.Civilian, true, false);
            else
            {
                // Custom body plans go through the same path with a template.
                f = CharacterFigure.BuildGenerated(go.transform, sh, CharacterFigure.Role.Civilian, true, false, null, t);
            }
            if (animator) { var a = go.AddComponent<FigureAnimator>(); a.Grounded = true; }
            return f;
        }

        // 1. Avatars for a spread of bodies.
        var sheets = new (string name, CharacterSheet sh)[]
        {
            ("average man", new CharacterSheet(Sex.Male, 10, 10, 10, 1000)), ("heavy woman", new CharacterSheet(Sex.Female, 20, 10, 1, 1000)),
            ("strong man", new CharacterSheet(Sex.Male, 20, 10, 15, 1000)), ("small soft", new CharacterSheet(Sex.Male, 1, 10, 1, 1000)),
            ("lean woman", new CharacterSheet(Sex.Female, 10, 20, 20, 1000)),
        };
        var figs = new List<CharacterFigure>();
        int valid = 0;
        var sb = new StringBuilder("avatars: ");
        for (int i = 0; i < sheets.Length; i++)
        {
            var f = Make(sheets[i].name, sheets[i].sh, new Vector3(i * 1.4f, 0f, 0f), i == 0);
            figs.Add(f);
            var av = BodyAvatar.Build(f);
            bool ok = av != null && av.isValid && av.isHuman;
            if (ok) valid++;
            sb.Append($"{sheets[i].name} valid {av?.isValid} human {av?.isHuman}, ");
            yield return null;
        }
        Log(sb.ToString());
        Log(valid == sheets.Length ? "PASS generated bodies pass humanoid Avatar validation" : $"FAIL {sheets.Length - valid} avatars invalid");

        // 2. Retarget: the average man walks (FigureAnimator); the others copy his muscle-space pose.
        var src = figs[0];
        var srcAnim = src.GetComponent<FigureAnimator>();
        srcAnim.Velocity = Vector3.forward * 1.6f;
        var handlers = new List<HumanPoseHandler>();
        foreach (var f in figs) handlers.Add(new HumanPoseHandler(BodyAvatar.Build(f), f.transform));
        var pose = new HumanPose();
        // Muscles and body rotation come from the source; bodyPosition (normalized per body) is not
        // copied: each target's hips go back to its own height plus the source's bob, scaled.
        void Retarget(int i) => BodyAvatar.CopyPose(handlers[0], src, handlers[i], figs[i], ref pose);
        float err = 0f; int samples = 0;
        Vector3 Dir(CharacterFigure f, Transform a, Transform b) => f.transform.InverseTransformDirection(b.position - a.position).normalized;
        for (float t = 0f; t < 2.5f; t += Time.deltaTime)
        {
            yield return new WaitForEndOfFrame();
            for (int i = 1; i < figs.Count; i++)
            {
                Retarget(i);
                if (t > 0.5f)
                {
                    var f = figs[i];
                    err += Vector3.Angle(Dir(src, src.HipL, src.KneeL), Dir(f, f.HipL, f.KneeL));
                    err += Vector3.Angle(Dir(src, src.KneeR, src.AnkleR), Dir(f, f.KneeR, f.AnkleR));
                    err += Vector3.Angle(Dir(src, src.ShoulderL, src.ElbowL), Dir(f, f.ShoulderL, f.ElbowL));
                    err += Vector3.Angle(Dir(src, src.ElbowR, src.WristR), Dir(f, f.ElbowR, f.WristR));
                    samples += 4;
                }
            }
        }
        float meanErr = err / Mathf.Max(1, samples);
        Log($"retargeting the walk onto {figs.Count - 1} other bodies: limb directions within {meanErr:0.0} deg of the source on average");
        Log(meanErr < 10f ? "PASS humanoid retargeting drives any generated proportions" : "FAIL retargeted limbs drift from the source");

        // Shots: the row (light from the front).
        var lightGo = new GameObject("AnimLight");
        var lt = lightGo.AddComponent<Light>();
        lt.type = LightType.Directional; lt.intensity = 1.6f; lt.color = new Color(1f, 0.96f, 0.9f);
        lightGo.transform.rotation = Quaternion.LookRotation(new Vector3(0.2f, -0.5f, -1f));
        var cam = Camera.main;
        cam.transform.SetParent(null);
        FlyingVehicle.CameraOverride = true;
        Vector3 rowC = origin + new Vector3(2.8f, 0.9f, 0f);
        cam.transform.SetPositionAndRotation(rowC + new Vector3(-2.5f, 0.5f, 5.2f), Quaternion.LookRotation(-new Vector3(-2.5f, 0.5f, 5.2f)));
        for (int k = 0; k < 6; k++)
        {
            yield return new WaitForEndOfFrame();
            for (int i = 1; i < figs.Count; i++) Retarget(i);
        }
        ScreenCapture.CaptureScreenshot(Path.Combine(dir, "anim_retarget.png"));
        yield return null; yield return null;

        // 3. Procedural layer: extra arms, jitter, sway, stride.
        var four = Make("four arms", new CharacterSheet(Sex.Male, 12, 10, 10, 1001), new Vector3(0f, -2.6f, 0f), true, new HumanTemplate { armCount = 4 });
        var twitchy = Make("twitchy", new CharacterSheet(Sex.Male, 10, 10, 20, 1000), new Vector3(1.4f, -2.6f, 0f), true);
        var calm = Make("calm", new CharacterSheet(Sex.Male, 10, 10, 10, 1000), new Vector3(2.8f, -2.6f, 0f), true);
        var heavy = Make("heavy", new CharacterSheet(Sex.Male, 10, 10, 1, 1000), new Vector3(4.2f, -2.6f, 0f), true);
        var strong = Make("strong", new CharacterSheet(Sex.Male, 20, 10, 10, 1000), new Vector3(5.6f, -2.6f, 0f), true);
        var weak = Make("weak", new CharacterSheet(Sex.Male, 1, 10, 10, 1000), new Vector3(7.0f, -2.6f, 0f), true);
        foreach (var f in new[] { four, heavy, strong, weak, calm }) f.GetComponent<FigureAnimator>().Velocity = Vector3.forward * 1.6f;
        // twitchy and a second calm one stand still for the jitter measure
        var calmStill = Make("calm still", new CharacterSheet(Sex.Male, 10, 10, 10, 1002), new Vector3(8.4f, -2.6f, 0f), true);
        yield return new WaitForSeconds(0.6f);

        float extraMin = 999f, extraMax = -999f, mainMin = 999f, mainMax = -999f;
        float twitchSpeed = 0f, calmSpeed = 0f; int glances = 0; bool inGlance = false;
        float rollHeavy = 0f, rollCalm = 0f, spreadHeavy = 0f, spreadCalm = 0f;
        float swingStrong = 0f, swingWeak = 0f; int n = 0;
        Quaternion pT = twitchy.HeadJoint.localRotation, pC = calmStill.HeadJoint.localRotation;
        Transform extraS = four.ExtraArms.Count > 0 ? four.ExtraArms[0].shoulder : null;
        float A(float a) => Mathf.DeltaAngle(0f, a);
        for (float t = 0f; t < 4f; t += Time.deltaTime)
        {
            yield return null;
            float dt = Mathf.Max(Time.deltaTime, 1e-4f);
            twitchSpeed += Quaternion.Angle(pT, twitchy.HeadJoint.localRotation) / dt; pT = twitchy.HeadJoint.localRotation;
            calmSpeed += Quaternion.Angle(pC, calmStill.HeadJoint.localRotation) / dt; pC = calmStill.HeadJoint.localRotation;
            float yaw = Mathf.Abs(A(twitchy.HeadJoint.localEulerAngles.y));
            if (yaw > 12f && !inGlance) { glances++; inGlance = true; } else if (yaw < 6f) inGlance = false;
            if (extraS != null)
            {
                float e = A(extraS.localEulerAngles.x), m = A(four.ShoulderL.localEulerAngles.x);
                extraMin = Mathf.Min(extraMin, e); extraMax = Mathf.Max(extraMax, e); mainMin = Mathf.Min(mainMin, m); mainMax = Mathf.Max(mainMax, m);
            }
            rollHeavy = Mathf.Max(rollHeavy, Mathf.Abs(A(heavy.Spine.localEulerAngles.z)));
            rollCalm = Mathf.Max(rollCalm, Mathf.Abs(A(calm.Spine.localEulerAngles.z)));
            spreadHeavy = Mathf.Max(spreadHeavy, Mathf.Abs(heavy.AnkleL.position.x - heavy.AnkleR.position.x));
            spreadCalm = Mathf.Max(spreadCalm, Mathf.Abs(calm.AnkleL.position.x - calm.AnkleR.position.x));
            swingStrong = Mathf.Max(swingStrong, Mathf.Abs(A(strong.HipL.localEulerAngles.x)));
            swingWeak = Mathf.Max(swingWeak, Mathf.Abs(A(weak.HipL.localEulerAngles.x)));
            n++;
        }
        twitchSpeed /= n; calmSpeed /= n;
        float extraAmp = extraMax - extraMin, mainAmp = mainMax - mainMin;
        Log($"extra arms: {four.ExtraArms.Count}, extra shoulder swings {extraAmp:0} deg against the main arm's {mainAmp:0} deg");
        Log(four.ExtraArms.Count == 2 && extraAmp > 5f && extraAmp < mainAmp ? "PASS extra arms follow the main pair, smaller" : "FAIL extra arms");
        Log($"jitter: DEX 20 head moves {twitchSpeed:0} deg/s ({glances} glances in 4 s), DEX 10 {calmSpeed:0} deg/s; jitter values {twitchy.GetComponent<FigureAnimator>().Jitter:0.00} / {calmStill.GetComponent<FigureAnimator>().Jitter:0.00}");
        Log(twitchSpeed > calmSpeed * 3f + 5f && glances > 0 ? "PASS DEX 20 is twitchy (head and glances), DEX 10 calm" : "FAIL jitter");
        Log($"walk: heavy sways {rollHeavy:0.0} deg and spreads its feet {spreadHeavy * 100f:0} cm, average {rollCalm:0.0} deg / {spreadCalm * 100f:0} cm; hip swing STR 20 {swingStrong:0} deg, STR 1 {swingWeak:0} deg");
        Log(rollHeavy > rollCalm + 2f && spreadHeavy > spreadCalm + 0.02f ? "PASS heavier bodies sway more and stride wider" : "FAIL heavy gait");
        Log(swingStrong > swingWeak * 1.15f ? "PASS STR lengthens the stride" : "FAIL STR stride");

        Vector3 rowP = origin + new Vector3(4f, -1.7f, 0f);
        Vector3 pv = new Vector3(-1.5f, 0.6f, 6.4f);
        cam.transform.SetPositionAndRotation(rowP + pv, Quaternion.LookRotation(-pv));
        yield return new WaitForEndOfFrame(); yield return new WaitForEndOfFrame();
        ScreenCapture.CaptureScreenshot(Path.Combine(dir, "anim_procedural.png"));
        yield return null; yield return null;
        Vector3 fa = four.transform.position + Vector3.up * 1f;
        cam.transform.SetPositionAndRotation(fa + new Vector3(1.6f, 0.4f, 2.2f), Quaternion.LookRotation(-new Vector3(1.6f, 0.4f, 2.2f)));
        yield return new WaitForEndOfFrame(); yield return new WaitForEndOfFrame();
        ScreenCapture.CaptureScreenshot(Path.Combine(dir, "anim_four_arms.png"));
        yield return null; yield return null;
        FlyingVehicle.CameraOverride = false;
        Destroy(lightGo);
    }

    // ---------- generated bodies: phase 5 (skinning) ----------

    static float SkinnedVolume(CharacterFigure f)
    {
        float vol = 0f;
        foreach (var r in f.Renderers)
        {
            var smr = r as SkinnedMeshRenderer;
            if (smr == null) continue;
            var m = new Mesh();
            smr.BakeMesh(m, true);
            var v = m.vertices; var t = m.triangles;
            // Baked vertices are in the renderer's space; the renderer sits at the body root.
            for (int k = 0; k < t.Length; k += 3) vol += Vector3.Dot(v[t[k]], Vector3.Cross(v[t[k + 1]], v[t[k + 2]])) / 6f;
            Destroy(m);
        }
        return vol;
    }

    IEnumerator SkinTest()
    {
        string dir = Path.GetDirectoryName(Application.dataPath);
        var origin = new Vector3(0f, 1500f, 0f);
        var avgSheet = new CharacterSheet(Sex.Male, 10, 10, 10, 1000);

        // 1. Bind fidelity: joints set to the A-pose reproduce the generated surface.
        {
            var go = new GameObject("BindCheck");
            go.transform.position = origin + new Vector3(0f, 0f, -10f);
            var st = new CharacterFigure.GenerationStats();
            var f = CharacterFigure.BuildGenerated(go.transform, avgSheet, CharacterFigure.Role.Civilian, true, false, st);
            Log($"generated body: {st.triangles} tris, mesh {st.meshMs:0} ms, skin weights + split {st.skinMs:0} ms, total {st.totalMs:0} ms; " +
                $"height {f.Height:0.00}, hips {f.HipHeight:0.00}, eye height {f.EyeHeight:0.00}, eye forward {f.EyeForward:0.000}, arm {f.ArmLength:0.00}");
            var sk = go.GetComponent<BodySkeleton>();
            var pose = BodySDF.APose(f.Plan);
            for (int i = 0; i < sk.Bones.Count; i++) sk.Bones[i].joint.localRotation = pose[i];
            yield return null;
            using var sdf = new BodySDF(f.Plan);
            // (The hands' and body's caps at the wrist cut, and their slightly shrunk overlap, lie inside the
            // surface by design: hidden vertices are counted, not measured.)
            float worst = 0f, sum = 0f; int n = 0, hidden = 0;
            float hide = BodyRules.Default.hands.cutShrink + 0.0005f;
            foreach (var r in f.Renderers)
            {
                var m = new Mesh();
                ((SkinnedMeshRenderer)r).BakeMesh(m, true);
                // Skin only (submesh 0 and the head): garments stand off the body by design.
                var skinIdx = new HashSet<int>(m.GetTriangles(0));
                var bakedV = m.vertices;
                var skinV = new List<Vector3>(); foreach (int vi in skinIdx) skinV.Add(bakedV[vi]);
                foreach (var v in skinV)
                {
                    float d = sdf.Eval(v);
                    if (d < -hide) { hidden++; continue; }
                    d = Mathf.Abs(d); worst = Mathf.Max(worst, d); sum += d; n++;
                }
                Destroy(m);
            }
            Log($"bind check (A-pose): skinned vertices {sum / n * 1000f:0.00} mm from the SDF surface on average, worst {worst * 1000f:0.0} mm ({hidden} hidden inside: wrist caps)");
            Log(worst < 0.004f ? "PASS bind poses reproduce the generated surface" : "FAIL skinned mesh doesn't match the bind pose");
            Destroy(go);
        }

        // 2. Every pose on the same body, in a row; volume against standing.
        var poses = new (string name, FigureAnimator.Pose pose, FigureAnimator.Arms arms, float speed, bool punch)[]
        {
            ("stand", FigureAnimator.Pose.Normal, FigureAnimator.Arms.Lowered, 0f, false),
            ("walk", FigureAnimator.Pose.Normal, FigureAnimator.Arms.Lowered, 1.4f, false),
            ("run", FigureAnimator.Pose.Normal, FigureAnimator.Arms.Lowered, 6f, false),
            ("guard punch", FigureAnimator.Pose.Normal, FigureAnimator.Arms.Guard, 0f, true),
            ("aim", FigureAnimator.Pose.Normal, FigureAnimator.Arms.Aim, 0f, false),
            ("air", FigureAnimator.Pose.Air, FigureAnimator.Arms.Lowered, 0f, false),
            ("glide", FigureAnimator.Pose.Glide, FigureAnimator.Arms.Lowered, 0f, false),
            ("hang", FigureAnimator.Pose.Hang, FigureAnimator.Arms.Lowered, 0f, false),
            ("seated", FigureAnimator.Pose.Seated, FigureAnimator.Arms.Lowered, 0f, false),
            ("cuffed", FigureAnimator.Pose.Cuffed, FigureAnimator.Arms.Behind, 0f, false),
            ("cuffing", FigureAnimator.Pose.Cuffing, FigureAnimator.Arms.Lowered, 0f, false),
            ("stunned", FigureAnimator.Pose.Stunned, FigureAnimator.Arms.Lowered, 0f, false),
            ("landing", FigureAnimator.Pose.Landing, FigureAnimator.Arms.Lowered, 0f, false),
            ("scooter", FigureAnimator.Pose.Scooter, FigureAnimator.Arms.Lowered, 0f, false),
        };
        var figs = new List<(string name, CharacterFigure f, FigureAnimator a, bool punch)>();
        for (int i = 0; i < poses.Length; i++)
        {
            var go = new GameObject("Pose " + poses[i].name);
            go.transform.position = origin + new Vector3(i * 1.1f, 0f, 0f);
            var f = CharacterFigure.BuildGenerated(go.transform, avgSheet, CharacterFigure.Role.Civilian, true, false, null, null, Outfit.None()); // naked: the volume is the body's
            var a = go.AddComponent<FigureAnimator>();
            a.CurrentPose = poses[i].pose; a.ArmMode = poses[i].arms;
            a.Velocity = Vector3.forward * poses[i].speed; a.Grounded = poses[i].pose != FigureAnimator.Pose.Air && poses[i].pose != FigureAnimator.Pose.Glide;
            a.LookYaw = 0f; a.LookPitch = 0f;
            figs.Add((poses[i].name, f, a, poses[i].punch));
            yield return null;
        }
        // Different bodies walking: heavy, ripped, women, five arms.
        var bodies = new (string name, CharacterSheet sheet)[]
        {
            ("heavy man", new CharacterSheet(Sex.Male, 20, 10, 1, 1000)), ("ripped man", new CharacterSheet(Sex.Male, 20, 10, 20, 1000)),
            ("heavy woman", new CharacterSheet(Sex.Female, 20, 10, 1, 1000)), ("woman", new CharacterSheet(Sex.Female, 10, 10, 10, 1000)),
            ("lean woman", new CharacterSheet(Sex.Female, 10, 10, 20, 1000)), ("small soft", new CharacterSheet(Sex.Male, 1, 10, 1, 1000)),
        };
        for (int i = 0; i < bodies.Length; i++)
        {
            var go = new GameObject("Body " + bodies[i].name);
            go.transform.position = origin + new Vector3(i * 1.2f, -2.4f, 0f);
            var f = CharacterFigure.BuildGenerated(go.transform, bodies[i].sheet, CharacterFigure.Role.Civilian, true, false, null, null, Outfit.None());
            var a = go.AddComponent<FigureAnimator>();
            a.Velocity = Vector3.forward * 1.6f; a.Grounded = true;
            figs.Add((bodies[i].name, f, a, false));
            yield return null;
        }
        // Let the poses blend in; punches thrown just before the shot.
        for (float t = 0f; t < 1.6f; t += Time.deltaTime)
        {
            foreach (var x in figs) if (x.punch && Time.frameCount % 20 == 0) x.a.Punch(1);
            yield return null;
        }
        float standVol = SkinnedVolume(figs[0].f);
        var sb = new System.Text.StringBuilder("volume vs standing: ");
        float worstChange = 0f; string worstPose = "";
        for (int i = 1; i < poses.Length; i++)
        {
            float v = SkinnedVolume(figs[i].f), ch = v / standVol - 1f;
            sb.Append($"{figs[i].name} {ch * 100f:+0.0;-0.0}%, ");
            if (Mathf.Abs(ch) > Mathf.Abs(worstChange)) { worstChange = ch; worstPose = figs[i].name; }
        }
        Log(sb.ToString());
        Log(Mathf.Abs(worstChange) < 0.05f ? $"PASS poses keep their volume (worst {worstPose} {worstChange * 100f:+0.0;-0.0}%)" : $"WARN {worstPose} changes volume by {worstChange * 100f:0.0}%");

        // Shots: a light, the pose row in two halves, the body row, a close-up.
        var lightGo = new GameObject("SkinLight");
        var lt = lightGo.AddComponent<Light>();
        lt.type = LightType.Directional; lt.intensity = 1.6f; lt.color = new Color(1f, 0.96f, 0.9f);
        lightGo.transform.rotation = Quaternion.LookRotation(new Vector3(-0.3f, -0.5f, 1f));
        var cam = Camera.main;
        cam.transform.SetParent(null);
        FlyingVehicle.CameraOverride = true;
        IEnumerator Shot(string name, Vector3 at, Vector3 from)
        {
            cam.transform.SetPositionAndRotation(from, Quaternion.LookRotation(at - from));
            foreach (var x in figs) if (x.punch) x.a.Punch(1);
            yield return new WaitForSeconds(0.08f);
            ScreenCapture.CaptureScreenshot(Path.Combine(dir, $"skin_{name}.png"));
            yield return null; yield return null;
        }
        Vector3 row = origin + new Vector3(0f, 0.9f, 0f);
        yield return Shot("poses_a", row + new Vector3(3.3f, 0f, 0f), row + new Vector3(3.3f, 0.6f, -5.2f));
        yield return Shot("poses_b", row + new Vector3(10.4f, 0f, 0f), row + new Vector3(10.4f, 0.6f, -5.2f));
        yield return Shot("poses_side", row + new Vector3(6f, 0f, 0f), row + new Vector3(-2.5f, 1.2f, -3.5f));
        Vector3 brow = origin + new Vector3(3f, -1.5f, 0f);
        yield return Shot("bodies", brow, brow + new Vector3(0f, 0.6f, -5.4f));
        yield return Shot("bodies_front", brow, brow + new Vector3(0f, 0.6f, 5.4f));
        yield return Shot("poses_front", row + new Vector3(6.5f, 0f, 0f), row + new Vector3(6.5f, 0.8f, 9.5f));
        Vector3 c = origin + new Vector3(3 * 1.1f, 1.1f, 0f);
        yield return Shot("close_punch", c, c + new Vector3(0.8f, 0.3f, -1.6f));
        Vector3 gl = origin + new Vector3(6 * 1.1f, 1.1f, 0f);
        yield return Shot("close_glide", gl, gl + new Vector3(0.4f, 0.4f, -1.9f));
        FlyingVehicle.CameraOverride = false;

        // 3. The player: first person, looking down at the generated body.
        var fpc = FirstPersonController.Instance;
        var cr = fpc.playerCamera.transform;
        fpc.AttachCamera(fpc.playerCamera);
        Log($"player: generated {fpc.Figure.Generated}, height {fpc.Figure.Height:0.00}, capsule {fpc.GetComponent<CharacterController>().height:0.00}, eye {cr.position.y - fpc.transform.position.y:0.00} above the feet");
        lightGo.transform.rotation = Quaternion.LookRotation(Vector3.ProjectOnPlane(fpc.transform.forward, Vector3.up) * -1f + Vector3.down * 0.8f);
        fpc.DebugLook(70f, fpc.transform.eulerAngles.y);
        yield return new WaitForSeconds(0.8f);
        {
            var pf = fpc.Figure;
            Vector3 loc(Transform t) => fpc.transform.InverseTransformPoint(t.position);
            Log($"fp: camera {loc(cr)}, head {loc(pf.Head)}, chest {loc(pf.Chest)}, hips {loc(pf.Hips)}, toes {loc(pf.AnkleL)}; cam fwd {fpc.transform.InverseTransformDirection(cr.forward)}; " +
                $"renderers: {string.Join(", ", pf.Renderers.ConvertAll(r => r.name + (r.enabled ? "" : " OFF") + " " + r.shadowCastingMode + " visible " + r.isVisible))}");
        }
        ScreenCapture.CaptureScreenshot(Path.Combine(dir, "skin_fp_down.png"));
        yield return null; yield return null;
        fpc.zoom.Snap(0.6f);
        fpc.DebugLook(15f, fpc.transform.eulerAngles.y + 160f);
        yield return new WaitForSeconds(0.8f);
        ScreenCapture.CaptureScreenshot(Path.Combine(dir, "skin_tp_player.png"));
        yield return null; yield return null;
        fpc.zoom.Snap(0f);
        Destroy(lightGo);
    }

    // ---------- generated bodies: phase 1 (plan + skeleton) ----------

    IEnumerator BodiesTest()
    {
        string dir = Path.GetDirectoryName(Application.dataPath);
        Log($"pcg check: Hash(1,2,3) = {Pcg.Hash(1, 2, 3)}, Hash(0,0,0) = {Pcg.Hash(0, 0, 0)}, U01(Hash(7,8,9)) = {Pcg.U01(Pcg.Hash(7, 8, 9)):0.000000}");

        // Determinism: same sheet twice -> identical plan; different seed -> different plan.
        var sheet = new CharacterSheet(Sex.Female, 12, 9, 14, 4242);
        var a = BodyPlanner.Generate(sheet); var b = BodyPlanner.Generate(sheet);
        var c = BodyPlanner.Generate(new CharacterSheet(Sex.Female, 12, 9, 14, 4243));
        bool same = a.bones.Count == b.bones.Count, differs = false;
        for (int i = 0; i < a.bones.Count && same; i++)
            same = a.bones[i].length == b.bones[i].length && a.bones[i].girth == b.bones[i].girth && a.bones[i].localPos == b.bones[i].localPos;
        for (int i = 0; i < a.bones.Count; i++) differs |= a.bones[i].length != c.bones[i].length;
        Log($"determinism: same sheet identical {same}, next seed differs {differs}");
        Log(same && differs ? "PASS same seed + sheet = same body" : "FAIL determinism");

        // Timing and validation over many rolled sheets.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int n = 500, rerolled = 0, nudged = 0;
        float minH = 9f, maxH = 0f;
        for (int i = 0; i < n; i++)
        {
            var plan = BodyPlanner.Generate(CharacterSheet.Roll(i));
            if (plan.rerolls > 0) rerolled++;
            if (!string.IsNullOrEmpty(plan.nudged)) nudged++;
            minH = Mathf.Min(minH, plan.height); maxH = Mathf.Max(maxH, plan.height);
        }
        sw.Stop();
        Log($"{n} plans in {sw.Elapsed.TotalMilliseconds:0} ms ({sw.Elapsed.TotalMilliseconds / n:0.000} ms each), re-rolled {rerolled}, nudged {nudged}, heights {minH:0.00}-{maxH:0.00}");

        // Phase 2: the sheet is readable from the body.
        BodyPlan P(Sex sx, int str, int intel, int dex) => BodyPlanner.Generate(new CharacterSheet(sx, str, intel, dex, 1000));
        string Read(BodyPlan p)
        {
            var w = p.bones[p.Index("Spine")]; var ua = p.bones[p.Index("ShoulderL")]; var th = p.bones[p.Index("HipL")];
            var hd = p.bones[p.Index("Head")]; var hand = p.bones[p.Index("WristL")];
            return $"h {p.height:0.00}, waist {w.Outer.y * 100f:0.0} cm (fat {w.fat.y * 1000f:0} mm), upper arm muscle {ua.muscle.y * 1000f:0} mm fat {ua.fat.y * 1000f:0} mm, " +
                   $"thigh {th.Outer.y * 100f:0.0} cm, head {hd.length * 100f:0.0}x{hd.Outer.y * 200f:0.0} cm, hand {hand.length * 100f:0.0} cm / arm {(ua.length + p.bones[p.Index("ElbowL")].length) * 100f:0} cm";
        }
        foreach (var sx in new[] { Sex.Male, Sex.Female })
            foreach (var (str, dex, name) in new[] { (20, 1, "strongman"), (20, 20, "bodybuilder"), (1, 1, "small soft"), (1, 20, "small wiry"), (10, 10, "average") })
            {
                var pl = P(sx, str, 10, dex);
                Log($"  {sx} STR {str} DEX {dex} ({name}): {Read(pl)}");
                Log($"     dna: {pl.dna}");
            }
        foreach (int iv in new[] { 1, 10, 16, 20 })
        {
            var pl = P(Sex.Male, 10, iv, 10);
            Log($"  INT {iv}: head x{pl.dna.headScale:0.00}, elongation {pl.dna.headElongation:0.00}, eye glow {pl.dna.eyeGlow:0.00}, head bone {pl.bones[pl.Index("Head")].length * 100f:0.0} cm");
        }
        {
            var big = P(Sex.Male, 20, 10, 10); var small = P(Sex.Male, 1, 10, 10);
            var fat = P(Sex.Male, 10, 10, 1); var lean = P(Sex.Male, 10, 10, 20);
            var smart = P(Sex.Male, 10, 20, 10); var dull = P(Sex.Male, 10, 1, 10);
            float Muscle(BodyPlan q) => q.bones[q.Index("ShoulderL")].muscle.y;
            float Belly(BodyPlan q) => q.bones[q.Index("Spine")].fat.y;
            float HandRatio(BodyPlan q) => q.bones[q.Index("WristL")].length / (q.bones[q.Index("ShoulderL")].length + q.bones[q.Index("ElbowL")].length);
            bool strOk = big.height > small.height * 1.1f && Muscle(big) > Muscle(small) * 1.6f && HandRatio(big) < HandRatio(small);
            bool dexOk = Belly(fat) > Belly(lean) * 10f;
            bool intOk = smart.bones[smart.Index("Head")].length > dull.bones[dull.Index("Head")].length * 1.2f && smart.dna.eyeGlow == 1f && dull.dna.eyeGlow == 0f;
            var fem = P(Sex.Female, 10, 10, 10); var mal = P(Sex.Male, 10, 10, 10);
            bool sexOk = fem.bones[fem.Index("Hips")].Outer.y > mal.bones[mal.Index("Hips")].Outer.y && fem.dna.breastSize > 0f && mal.dna.breastSize == 0f
                         && Muscle(mal) > Muscle(fem);
            Log($"STR: height {small.height:0.00} -> {big.height:0.00}, arm muscle {Muscle(small) * 1000f:0} -> {Muscle(big) * 1000f:0} mm, hand/arm {HandRatio(small):0.000} -> {HandRatio(big):0.000}; " +
                $"DEX: belly fat {Belly(fat) * 1000f:0} -> {Belly(lean) * 1000f:0} mm");
            Log(strOk ? "PASS STR makes bigger, more muscled bodies (hands grow less)" : "FAIL STR");
            Log(dexOk ? "PASS DEX strips the fat (low DEX heavy, high DEX lean)" : "FAIL DEX");
            Log(intOk ? "PASS INT grows and elongates the head, eyes glow from 16" : "FAIL INT");
            Log(sexOk ? "PASS sex template: wider female pelvis and breasts, more male arm muscle" : "FAIL sex template");
        }

        // Phase 3: muscle definition shows through thin fat. Front surface of the abs, top to bottom:
        // bumpiness (mean |second difference| of the surface depth) for lean vs heavy.
        float Bumpiness(BodyPlan pl, out float meanZ)
        {
            var sdf = new BodySDF(pl);
            var lum = pl.bones[pl.Index("Spine")];
            int li = pl.Index("Spine");
            Vector3 a = sdf.start[li], b = sdf.end[li];
            float x = a.x + lum.girth.y * 0.19f;
            var zs = new List<float>();
            for (float y = a.y + 0.01f; y < b.y - 0.01f; y += 0.003f)
            {
                // Scan in from the front in 2 mm steps to the first inside sample, then bisect.
                float z = 0.45f;
                while (z > -0.2f && sdf.Eval(new Vector3(x, y, z)) > 0f) z -= 0.002f;
                float lo = z, hi = z + 0.002f;
                for (int k = 0; k < 12; k++) { float mid = (lo + hi) * 0.5f; if (sdf.Eval(new Vector3(x, y, mid)) > 0f) hi = mid; else lo = mid; }
                zs.Add((lo + hi) * 0.5f);
            }
            // RMS residual from a local quadratic fit over +-2.4 cm: the shape of the belly drops out,
            // the bumps of the muscle under thin fat stay.
            meanZ = 0f;
            foreach (var z in zs) meanZ += z;
            meanZ /= Mathf.Max(1, zs.Count);
            int w = 8; float sum = 0f; int cnt = 0;
            for (int k = w; k < zs.Count - w; k++)
            {
                double s0 = 0, s1 = 0, s2 = 0, s3 = 0, s4 = 0, t0 = 0, t1 = 0, t2 = 0;
                for (int j = -w; j <= w; j++) { double u = j, zz = zs[k + j]; s0 += 1; s1 += u; s2 += u * u; s3 += u * u * u; s4 += u * u * u * u; t0 += zz; t1 += zz * u; t2 += zz * u * u; }
                // Symmetric window: s1 = s3 = 0, so the fit's value at 0 is c0 = (t0 s4 - t2 s2) / (s0 s4 - s2^2).
                double c0 = (t0 * s4 - t2 * s2) / (s0 * s4 - s2 * s2);
                double r = zs[k] - c0;
                sum += (float)(r * r); cnt++;
            }
            return Mathf.Sqrt(sum / Mathf.Max(1, cnt)) * 1000f;
        }
        {
            float lean = Bumpiness(P(Sex.Male, 15, 10, 20), out float zLean), heavy = Bumpiness(P(Sex.Male, 15, 10, 1), out float zHeavy);
            float avg = Bumpiness(P(Sex.Male, 15, 10, 10), out float zAvg);
            Log($"abs definition (RMS bumps on the belly's front, mm): DEX 20 {lean:0.000}, DEX 10 {avg:0.000}, DEX 1 {heavy:0.000}; belly front at z {zLean * 100f:0.0} / {zAvg * 100f:0.0} / {zHeavy * 100f:0.0} cm");
            foreach (int dex in new[] { 20, 10, 1 }) Log($"  F DEX {dex}: " + new BodySDF(P(Sex.Female, 10, 10, dex)).BreastReport());
            Log("  M avg: " + new BodySDF(P(Sex.Male, 10, 10, 10)).BreastReport());
            foreach (var sx in new[] { Sex.Male, Sex.Female })
                foreach (int dex in new[] { 20, 10, 1 }) Log($"  {sx} DEX {dex}: " + new BodySDF(P(sx, 10, 10, dex)).ButtReport());
            Log(lean > heavy * 2f && zHeavy > zLean + 0.03f ? "PASS muscle definition shows at high DEX and is smoothed over at low DEX" : "FAIL definition doesn't follow DEX");
        }

        // Odd arms and extra girdles validate.
        foreach (int arms in new[] { 0, 1, 3, 4, 5 })
        {
            var p = BodyPlanner.Generate(new CharacterSheet(Sex.Male, 10, 10, 10, 77), null, new HumanTemplate { armCount = arms });
            Log($"  {arms} arms: {p.arms.Count} arms on girdles [{string.Join(",", p.arms.ConvertAll(x => x.girdle + (x.side < 0 ? "L" : x.side > 0 ? "R" : "C")))}], {p.bones.Count} bones, valid {BodyPlanner.Validate(p, BodyRules.Default, out var why)} {why}");
        }

        // The lineup, built as real hierarchies, in the sky.
        var go = new GameObject("Body Lineup");
        go.transform.position = new Vector3(0f, 1500f, 0f);
        var lineup = go.AddComponent<BodyLineupDebug>();
        lineup.collectMeshStats = true;
        yield return null; yield return null;
        for (float t = 0f; t < 400f && lineup.MeshesBuilt < lineup.Entries.Count; t += Time.unscaledDeltaTime) yield return null;
        {
            float mms = 0f, sdfMs = 0f, maxMs = 0f; int tris = 0, maxTris = 0, raw = 0, holes = 0, nonMan = 0; float vol = 0f;
            foreach (var (label, r) in lineup.MeshStats)
            {
                mms += r.totalMs; sdfMs += r.sampleMs; tris += r.triangles; raw += r.rawTriangles; maxTris = Mathf.Max(maxTris, r.triangles);
                holes += r.boundaryEdges; nonMan += r.nonManifoldEdges; maxMs = Mathf.Max(maxMs, r.totalMs);
            }
            Log("  first body: " + lineup.MeshStats[0].result);
            Log("  heaviest (F S20 D1): " + lineup.MeshStats.Find(x => x.label == "F S20 D1").result);
            Log("  5 arms: " + lineup.MeshStats.Find(x => x.label == "5 arms").result);
            var bad = new System.Text.StringBuilder("  non-manifold by body: ");
            foreach (var (label, r) in lineup.MeshStats) if (r.nonManifoldEdges > 0) bad.Append($"{label} {r.nonManifoldEdges}, ");
            Log(bad.ToString());
            Log("  first body non-manifold at: " + string.Join(" ", lineup.MeshStats[0].result.nonManifoldAt));
            Log("  heaviest non-manifold at: " + string.Join(" ", lineup.MeshStats.Find(x => x.label == "F S20 D1").result.nonManifoldAt));
            // Signed volume of the first mesh: positive = triangles wound outward.
            var m0 = lineup.MeshStats[0].result.mesh;
            var vv = m0.vertices; var tt = m0.triangles;
            for (int k = 0; k < tt.Length; k += 3) vol += Vector3.Dot(vv[tt[k]], Vector3.Cross(vv[tt[k + 1]], vv[tt[k + 2]])) / 6f;
            int nm = lineup.MeshStats.Count;
            Log($"body meshes: {nm} bodies at {lineup.meshCell * 100f:0.0} cm cells, {mms / nm:0} ms each (max {maxMs:0}; sampling {sdfMs / nm:0} ms), " +
                $"{raw / nm} raw -> {tris / nm} triangles avg ({maxTris} max), holes {holes}, non-manifold edges {nonMan}; first mesh volume {vol * 1000f:0.0} L");
            Log(vol > 0f ? "PASS body meshes wound outward" : "FAIL mesh inside out");
            Log(holes == 0 && nonMan == 0 ? "PASS no holes, no non-manifold edges" : $"WARN {holes} boundary / {nonMan} non-manifold edges over {nm} bodies");
            Log(maxTris <= lineup.meshTriangles * 1.02f ? $"PASS triangle budget ({lineup.meshTriangles})" : "FAIL over the triangle budget");
        }
        var sk = go.GetComponentsInChildren<BodySkeleton>();
        float ms = 0f; foreach (var s in sk) ms += s.GenerationMs;
        var first = sk[0];
        bool contract = first.Hips && first.Spine && first.Chest && first.Neck && first.Head && first.ShoulderL && first.ShoulderR && first.ElbowL && first.ElbowR
                        && first.WristL && first.WristR && first.HandL && first.HandR && first.HipL && first.HipR && first.KneeL && first.KneeR && first.AnkleL && first.AnkleR;
        Vector3 toe = first.Bones[first.Plan.Index("AnkleL")].End;
        Log($"lineup: {sk.Length} skeletons, hierarchy build {ms / sk.Length:0.000} ms each; named joints present {contract}; " +
            $"male #0: height {first.Plan.height:0.000}, head top {first.Bones[first.Plan.Index("Head")].End.y - go.transform.position.y:0.000}, toe at y {toe.y - go.transform.position.y:0.000}, hand {first.Plan.bones[first.Plan.Index("WristL")].length:0.000} m");
        Log(contract && Mathf.Abs(toe.y - go.transform.position.y) < 0.01f ? "PASS skeletons keep the rig contract, feet on the ground" : "FAIL skeleton");

        // A light for the shots (night scene).
        var lightGo = new GameObject("LineupLight");
        var lt = lightGo.AddComponent<Light>();
        lt.type = LightType.Directional; lt.intensity = 1.6f; lt.color = new Color(1f, 0.96f, 0.9f);
        lightGo.transform.rotation = Quaternion.LookRotation(new Vector3(-0.4f, -0.5f, -1f));
        // Camera: straight on, the whole grid.
        Time.timeScale = 0f;
        var cam = Camera.main;
        cam.transform.SetParent(null);
        FlyingVehicle.CameraOverride = true;
        Vector3 centre = go.transform.position + new Vector3(-5f, -4.6f, 0f);
        cam.transform.SetPositionAndRotation(centre + new Vector3(0f, 0f, 15f), Quaternion.LookRotation(Vector3.back));
        cam.fieldOfView = 55f;
        yield return new WaitForEndOfFrame(); yield return new WaitForEndOfFrame();
        ScreenCapture.CaptureScreenshot(Path.Combine(dir, "body_lineup.png"));
        yield return new WaitForEndOfFrame(); yield return new WaitForEndOfFrame();
        // Close-up of the STR x DEX grid (male), straight on.
        Vector3 gm = go.transform.position + new Vector3(-1f, -3.7f, 0f), gf = go.transform.position + new Vector3(-5f, -3.7f, 0f);
        Vector3 view = new Vector3(0f, 4.6f, 6.4f);
        cam.transform.SetPositionAndRotation(gm + view, Quaternion.LookRotation(-view));
        cam.fieldOfView = 50f;
        yield return new WaitForEndOfFrame(); yield return new WaitForEndOfFrame();
        ScreenCapture.CaptureScreenshot(Path.Combine(dir, "body_grid_male.png"));
        yield return new WaitForEndOfFrame(); yield return new WaitForEndOfFrame();
        cam.transform.SetPositionAndRotation(gf + view, Quaternion.LookRotation(-view));
        yield return new WaitForEndOfFrame(); yield return new WaitForEndOfFrame();
        ScreenCapture.CaptureScreenshot(Path.Combine(dir, "body_grid_female.png"));
        yield return new WaitForEndOfFrame(); yield return new WaitForEndOfFrame();
        // Close-up: STR 20 row, male DEX 1 / 10 / 20, three-quarter.
        Vector3 row3 = go.transform.position + new Vector3(-1f, -3f * lineup.rowHeight + 0.9f, 0f);
        Vector3 v3 = new Vector3(1.0f, 0.4f, 3.0f);
        cam.transform.SetPositionAndRotation(row3 + v3, Quaternion.LookRotation(-v3));
        yield return new WaitForEndOfFrame(); yield return new WaitForEndOfFrame();
        ScreenCapture.CaptureScreenshot(Path.Combine(dir, "body_str20_male.png"));
        yield return new WaitForEndOfFrame(); yield return new WaitForEndOfFrame();
        Vector3 row3f = row3 + new Vector3(-4f, 0f, 0f);
        cam.transform.SetPositionAndRotation(row3f + v3, Quaternion.LookRotation(-v3));
        yield return new WaitForEndOfFrame(); yield return new WaitForEndOfFrame();
        ScreenCapture.CaptureScreenshot(Path.Combine(dir, "body_str20_female.png"));
        yield return new WaitForEndOfFrame(); yield return new WaitForEndOfFrame();
        // Heads: the INT row, male, close.
        // One body up close, front and side: average male, then STR 20 DEX 20 and STR 10 DEX 1.
        foreach (var (label, col, row) in new[] { ("avg", 1, 2), ("ripped", 2, 3), ("heavy", 0, 2) })
        {
            Vector3 body = go.transform.position + new Vector3(-col * lineup.spacing, -row * lineup.rowHeight + 1.0f, 0f);
            Vector3 vf = new Vector3(0.35f, 0.15f, 1.9f);
            cam.transform.SetPositionAndRotation(body + vf, Quaternion.LookRotation(-vf));
            yield return new WaitForEndOfFrame(); yield return new WaitForEndOfFrame();
            ScreenCapture.CaptureScreenshot(Path.Combine(dir, $"body_close_{label}.png"));
            yield return new WaitForEndOfFrame(); yield return new WaitForEndOfFrame();
        }
        foreach (var (label, vv) in new[] { ("front", new Vector3(0f, 0.05f, 1.1f)), ("side", new Vector3(1.1f, 0.05f, 0.05f)) })
        {
            Vector3 body = go.transform.position + new Vector3(-1f * lineup.spacing, -2f * lineup.rowHeight + 1.15f, 0f);
            cam.transform.SetPositionAndRotation(body + vv, Quaternion.LookRotation(-vv));
            yield return new WaitForEndOfFrame(); yield return new WaitForEndOfFrame();
            ScreenCapture.CaptureScreenshot(Path.Combine(dir, $"body_torso_{label}.png"));
            yield return new WaitForEndOfFrame(); yield return new WaitForEndOfFrame();
        }
        // Side view of the STR 10 row (women, DEX 1 / 10 / 20): breasts and buttocks in profile.
        {
            Vector3 rowF = go.transform.position + new Vector3(-5f, -2f * lineup.rowHeight + 0.9f, 0f);
            Vector3 vs = new Vector3(2.6f, 0.2f, -1.4f);
            cam.transform.SetPositionAndRotation(rowF + vs, Quaternion.LookRotation(-vs));
            yield return new WaitForEndOfFrame(); yield return new WaitForEndOfFrame();
            ScreenCapture.CaptureScreenshot(Path.Combine(dir, "body_profile_female.png"));
            yield return new WaitForEndOfFrame(); yield return new WaitForEndOfFrame();
            Vector3 rowM = go.transform.position + new Vector3(-1f, -2f * lineup.rowHeight + 0.9f, 0f);
            cam.transform.SetPositionAndRotation(rowM + vs, Quaternion.LookRotation(-vs));
            yield return new WaitForEndOfFrame(); yield return new WaitForEndOfFrame();
            ScreenCapture.CaptureScreenshot(Path.Combine(dir, "body_profile_male.png"));
            yield return new WaitForEndOfFrame(); yield return new WaitForEndOfFrame();
        }
        Vector3 heads = go.transform.position + new Vector3(-2f, -4f * lineup.rowHeight + 1.45f, 0f);
        Vector3 vh = new Vector3(0.9f, 0.1f, 2.2f);
        cam.transform.SetPositionAndRotation(heads + vh, Quaternion.LookRotation(-vh));
        yield return new WaitForEndOfFrame(); yield return new WaitForEndOfFrame();
        ScreenCapture.CaptureScreenshot(Path.Combine(dir, "body_int_heads.png"));
        yield return new WaitForEndOfFrame(); yield return new WaitForEndOfFrame();
        Time.timeScale = 1f;
    }

    // ---------- T-gun: modes, police rules, officers' guns ----------

    IEnumerator TGunTest()
    {
        var fpc = FirstPersonController.Instance;
        var w = fpc.GetComponent<PlayerWeapon>();
        var disp = PoliceDispatch.Ensure();
        var cam = fpc.playerCamera.transform;
        string dir = Path.GetDirectoryName(Application.dataPath);
        IEnumerator Shot(string name)
        {
            yield return null;
            ScreenCapture.CaptureScreenshot(Path.Combine(dir, $"tgun_{name}.png"));
            yield return null; yield return null;
        }
        void LookAt(Vector3 p)
        {
            Vector3 d = p - cam.position;
            fpc.DebugLook(Mathf.Clamp(-Mathf.Atan2(d.y, new Vector2(d.x, d.z).magnitude) * Mathf.Rad2Deg, -80f, 80f), Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg);
        }
        yield return new WaitForSeconds(1f);

        // 1. Model and modes.
        var gun = w.Gun;
        Log($"gun {gun.name}: {gun.GetComponentsInChildren<Renderer>().Length} parts, muzzle z {gun.muzzle.localPosition.z:0.000}, mode {gun.CurrentMode}");
        w.Draw();
        w.DebugSteady = true;
        fpc.DebugLook(8f, fpc.transform.eulerAngles.y);
        yield return new WaitForSeconds(1f);
        var strip = gun.transform.GetChild(gun.transform.childCount - 2).GetComponent<Renderer>();
        bool blue = strip.sharedMaterial == LaserWeapon.ModeMat(Weapon.Mode.Stun);
        yield return Shot("stun_mode");
        w.ToggleMode();
        bool red = strip.sharedMaterial == LaserWeapon.ModeMat(Weapon.Mode.Lethal);
        bool blockedFire = !w.DebugFire();
        yield return new WaitForSeconds(0.35f);
        bool firesAfter = w.DebugFire();
        yield return Shot("lethal_mode");
        Log($"modes: stun strips blue {blue}, B -> lethal strips red {red}, no fire right after switching {blockedFire}, fires after 0.35 s {firesAfter}");
        Log(blue && red && blockedFire && firesAfter ? "PASS B flips the mode (strips, 0.3 s lockout)" : "FAIL mode switch");
        w.DebugSteady = false;
        fpc.zoom.Snap(0.35f);
        fpc.DebugLook(20f, fpc.transform.eulerAngles.y + 160f);
        yield return new WaitForSeconds(0.8f);
        yield return Shot("tp_hand");
        fpc.zoom.Snap(0f);
        fpc.DebugLook(8f, fpc.transform.eulerAngles.y - 160f);

        // 2. Police rules, with a patrol car held in view.
        PoliceDriver cop = null;
        foreach (var u in PoliceDispatch.Units) if (u != null && u.isActiveAndEnabled) { cop = u; break; }
        if (cop == null) { Log("WARN no police unit"); }
        else
        {
            Vector3 copPos = fpc.transform.position + Vector3.ProjectOnPlane(cam.forward, Vector3.up).normalized * 30f + Vector3.up * 6f;
            IEnumerator Hold(float seconds)
            {
                for (float t = 0f; t < seconds; t += Time.deltaTime)
                {
                    cop.Car.DebugPlace(copPos, Quaternion.LookRotation(fpc.transform.position - copPos), Vector3.zero);
                    yield return null;
                }
            }
            disp.DebugClear();
            if (w.Mode != Weapon.Mode.Stun) w.ToggleMode();
            yield return Hold(0.6f);
            fpc.DebugLook(-40f, fpc.transform.eulerAngles.y + 90f); // fire into the sky
            yield return Hold(0.4f);
            w.DebugFire();
            yield return Hold(0.3f);
            Log($"stun shot seen by police: wanted {disp.WantedLevel}, force {disp.ForceLevel}");
            Log(disp.WantedLevel == 1 && disp.ForceLevel == PoliceDispatch.Force.NonLethal ? "PASS a stun shot brings stun guns, not lethal force" : "FAIL stun-shot rule");

            disp.DebugClear();
            w.ToggleMode();
            yield return Hold(0.8f);
            Log($"lethal T-gun drawn in sight: wanted {disp.WantedLevel}, force {disp.ForceLevel}");
            Log(disp.WantedLevel >= 1 && disp.ForceLevel == PoliceDispatch.Force.NonLethal ? "PASS brandishing a lethal gun brings out the stun guns" : "FAIL brandishing rule");
            w.DebugFire();
            yield return Hold(0.3f);
            Log($"lethal shot seen: wanted {disp.WantedLevel}, force {disp.ForceLevel}");
            Log(disp.WantedLevel >= 2 && disp.ForceLevel == PoliceDispatch.Force.Lethal ? "PASS a lethal shot brings lethal force" : "FAIL lethal-shot rule");
            disp.DebugClear();
            cop.Car.DebugPlace(copPos + Vector3.up * 400f, Quaternion.identity, Vector3.zero); // out of the way
        }

        // 3. Cars: stun hiccups, lethal damages.
        var car = PickNearLaneCar(fpc.transform.position, 600f);
        if (car != null)
        {
            Vector3 carPos = cam.position + Vector3.ProjectOnPlane(cam.forward, Vector3.up).normalized * 14f + Vector3.up * 1f;
            if (w.Mode != Weapon.Mode.Stun) w.ToggleMode();
            float hp0 = car.Health.Health;
            for (float t = 0f; t < 0.9f; t += Time.deltaTime)
            {
                car.DebugPlace(carPos, Quaternion.LookRotation(cam.right), Vector3.zero);
                LookAt(car.transform.position);
                yield return null;
            }
            w.DebugFire();
            bool hiccup = car.Hiccuping;
            float hpStun = car.Health.Health;
            Log($"stun shot on {car.name}: hit {(PlayerWeapon.LastShotCollider != null ? PlayerWeapon.LastShotCollider.name : "nothing")}, hiccup {hiccup}, health {hp0:0} -> {hpStun:0}");
            yield return Shot("car_hiccup");
            yield return new WaitForSeconds(0.6f);
            Log($"  after 0.6 s: hiccuping {car.Hiccuping}");
            Log(hiccup && hpStun == hp0 && !car.Hiccuping ? "PASS stun hiccups a car, no damage" : "FAIL car stun");
            w.ToggleMode();
            for (float t = 0f; t < 0.5f; t += Time.deltaTime)
            {
                car.DebugPlace(carPos, Quaternion.LookRotation(cam.right), Vector3.zero);
                LookAt(car.transform.position);
                yield return null;
            }
            w.DebugFire();
            Log($"lethal shot on the car: health {hpStun:0} -> {car.Health.Health:0}");
            Log(car.Health.Health <= hpStun - 14f ? "PASS lethal shot damages a car (15)" : "FAIL lethal car damage");
            disp.DebugClear();
        }

        // 4. Down in the street: stun a pedestrian.
        fpc.PlaceAt(new Vector3(27f, 1f, -92f), Quaternion.Euler(0f, 180f, 0f));
        yield return new WaitForSeconds(3f);
        if (w.Mode != Weapon.Mode.Stun) w.ToggleMode();
        var ps = PedestrianSystem.Instance;
        PedestrianSystem.Ped ped = null;
        for (float t = 0f; t < 10f && ped == null && ps != null; t += 0.25f)
        {
            foreach (var p in ps.NearAgents)
            {
                float d = Vector3.Distance(p.pos, fpc.transform.position);
                if (d > 3f && d < 40f && p.state != PedestrianSystem.State.Down && PoliceDispatch.LineOfSight(cam.position, p.pos + Vector3.up)) { ped = p; break; }
            }
            if (ped == null) yield return new WaitForSeconds(0.25f);
        }
        if (ped == null) Log($"no pedestrian: {(ps != null ? ps.NearAgents.Count : -1)} near agents");
        if (ped == null) Log("WARN no pedestrian in sight");
        else
        {
            for (float t = 0f; t < 0.8f; t += Time.deltaTime) { LookAt(ped.pos + Vector3.up * 0.95f); yield return null; }
            w.DebugFire();
            var hitPed = PedestrianSystem.Find(PlayerWeapon.LastShotCollider);
            if (hitPed != null && hitPed != ped) { Log("  (another pedestrian stepped into the shot)"); ped = hitPed; }
            yield return null;
            var s0 = ped.state;
            fpc.zoom.Snap(0.5f);
            yield return Shot("ped_down");
            fpc.zoom.Snap(0f);
            yield return new WaitForSeconds(2.4f);
            Log($"stunned pedestrian: hit {(PlayerWeapon.LastShotCollider != null ? PlayerWeapon.LastShotCollider.name : "nothing")}, state {s0}, after 2.4 s {ped.state}");
            Log(s0 == PedestrianSystem.State.Down && ped.state != PedestrianSystem.State.Down ? "PASS stun drops a pedestrian for 2 s, then it flees" : "FAIL pedestrian stun");
        }
        disp.DebugClear();

        // 5. An officer's T-gun: shots leave its muzzle, cover stops them.
        PoliceDriver home = null;
        foreach (var u in PoliceDispatch.Units) if (u != null && u.isActiveAndEnabled) { home = u; break; }
        if (home == null) { Log("WARN no unit for the officer test"); yield break; }
        Vector3 me = fpc.transform.position;
        Vector3 side = Vector3.ProjectOnPlane(cam.right, Vector3.up).normalized;
        // The side with a clear line and room round the spot (the start plaza is next to podiums).
        Vector3 spot = me + side * 8f;
        bool Clear(Vector3 at)
        {
            foreach (float h in new[] { 0.6f, 1.2f, 1.7f })
                if (Physics.Linecast(me + Vector3.up * h, at + Vector3.up * h, ~0, QueryTriggerInteraction.Ignore)) return false;
            if (Physics.Linecast(cam.position, at + Vector3.up * 1f, ~0, QueryTriggerInteraction.Ignore)) return false; // the shot's own line
            return !Physics.CheckSphere(at + Vector3.up * 1.2f, 0.8f, ~0, QueryTriggerInteraction.Ignore);
        }
        for (int k = 1; k < 8 && !Clear(spot); k++)
        {
            Vector3 d = Quaternion.Euler(0f, k * 45f, 0f) * side;
            if (Clear(me + d * 8f)) { spot = me + d * 8f; side = d; }
        }
        home.Car.DebugPlace(spot + Vector3.up * 4f, Quaternion.identity, Vector3.zero);
        var off = OfficerAgent.SpawnFrom(home, 0f);
        for (float t = 0f; t < 15f && !(off.State == OfficerAgent.Phase.Foot && off.AtGoal); t += Time.deltaTime)
        {
            off.Goal = spot;
            off.StopDistance = 0.5f;
            yield return null;
        }
        Log($"test officer: {off.State}, {Vector3.Distance(off.transform.position, me):0.0} m away");
        off.Armed = true;
        Vector3 chest = fpc.transform.position + Vector3.up * 1.2f;
        int hits = 0, shots = 0; float worstOrigin = 0f;
        for (float t = 0f; t < 5f; t += Time.deltaTime)
        {
            off.Goal = spot; off.AimAt(chest);
            Vector3 muzzle = off.Gun.muzzle.position;
            int before = LaserWeapon.Shots;
            if (off.FireStun(chest, 30f, 0.5f)) hits++;
            if (LaserWeapon.Shots > before) { shots++; worstOrigin = Mathf.Max(worstOrigin, Vector3.Distance(LaserWeapon.LastOrigin, muzzle)); }
            yield return null;
        }
        Log($"officer stun shots: {shots} fired, {hits} hit the player (aim error shrinks with tracking), shot origin within {worstOrigin * 100f:0.0} cm of its muzzle");
        Log(shots > 0 && worstOrigin < 0.01f && hits > 0 ? "PASS officer fires along its barrel and hits once settled" : "FAIL officer shots");
        fpc.zoom.Snap(1f);
        LookAt(off.transform.position + Vector3.up * 1.2f);
        fpc.DebugLook(25f, fpc.transform.eulerAngles.y + 35f);
        for (int i = 0; i < 3; i++) { off.Goal = spot; off.AimAt(chest); yield return null; }
        yield return Shot("officer_aim");
        fpc.zoom.Snap(0f);

        // Cover: a slab 1.2 m in front of the officer (where its eye and barrel lines are furthest apart),
        // under the eye line but over the barrel line.
        Vector3 fromOff = chest - (off.transform.position + Vector3.up * 1.2f);
        Vector3 flat = Vector3.ProjectOnPlane(fromOff, Vector3.up).normalized;
        var slab = GameObject.CreatePrimitive(PrimitiveType.Cube);
        slab.name = "TestCover";
        float ground = off.transform.position.y;
        slab.transform.position = off.transform.position + flat * 1.2f + Vector3.up * 0.67f;
        slab.transform.rotation = Quaternion.LookRotation(flat);
        slab.transform.localScale = new Vector3(3f, 1.34f, 0.15f);
        Physics.SyncTransforms();
        bool eyeClear = PoliceDispatch.LineOfSight(off.transform.position + Vector3.up * 1.4f, chest);
        int coverHits = 0, coverShots = 0, onSlab = 0;
        for (float t = 0f; t < 3f; t += Time.deltaTime)
        {
            off.Goal = spot; off.AimAt(chest);
            int before = LaserWeapon.Shots;
            if (off.FireStun(chest, 30f, 0.5f)) coverHits++;
            if (LaserWeapon.Shots > before) { coverShots++; if (LaserWeapon.LastHit != null && LaserWeapon.LastHit.gameObject == slab) onSlab++; }
            yield return null;
        }
        Log($"behind cover (officer still sees: {eyeClear}): {coverShots} shots, {onSlab} hit the cover, {coverHits} hit the player");
        Log(coverShots > 0 && coverHits == 0 && onSlab > 0 ? "PASS cover stops officers' shots" : eyeClear ? "FAIL cover didn't stop the shots" : "WARN slab also blocked the eye line (no shots)");
        Destroy(slab);

        // 6. The player stuns the officer.
        w.Draw();
        for (float t = 0f; t < 0.8f; t += Time.deltaTime) { off.Goal = spot; LookAt(off.transform.position + Vector3.up * 1f); yield return null; }
        w.DebugFire();
        yield return null;
        bool down = off.Stunned;
        Log($"player stuns the officer: hit {(PlayerWeapon.LastShotCollider != null ? PlayerWeapon.LastShotCollider.name : "nothing")}, stunned {down}, force now {disp.ForceLevel}");
        yield return Shot("officer_stunned");
        yield return new WaitForSeconds(2.2f);
        Log(down && !off.Stunned && disp.ForceLevel == PoliceDispatch.Force.Lethal ? "PASS stun drops an officer for 2 s (and brings lethal force)" : "FAIL officer stun");
    }

    // ---------- one-handed aiming ----------

    static float BarrelMiss(PlayerWeapon w)
    {
        Vector3 m = w.Gun.muzzle.position, f = w.Gun.muzzle.forward;
        Vector3 toA = w.AimPoint - m;
        return Vector3.Cross(f, toA).magnitude; // distance of the aim point from the barrel ray
    }

    IEnumerator AimTest()
    {
        var fpc = FirstPersonController.Instance;
        var w = fpc.GetComponent<PlayerWeapon>();
        var fig = fpc.Figure;
        string dir = Path.GetDirectoryName(Application.dataPath);
        IEnumerator Shot(string name)
        {
            yield return null;
            ScreenCapture.CaptureScreenshot(Path.Combine(dir, $"aim_{name}.png"));
            yield return null; yield return null;
        }
        float ElbowBend() => Vector3.Angle(fig.ElbowR.position - fig.ShoulderR.position, fig.WristR.position - fig.ElbowR.position);
        float reach = Vector3.Distance(fig.ShoulderR.position, fig.ElbowR.position) + Vector3.Distance(fig.ElbowR.position, fig.WristR.position);
        yield return new WaitForSeconds(1f);

        // 1. Draw: the arm rises into view, nearly straight.
        Log($"holstered: gun on {w.Gun.transform.parent.name}");
        w.Draw();
        fpc.DebugLook(5f, fpc.transform.eulerAngles.y);
        yield return new WaitForSeconds(1f);
        float ext = Vector3.Distance(fig.ShoulderR.position, fig.WristR.position) / reach;
        Vector3 vp = fpc.playerCamera.WorldToViewportPoint(w.Gun.muzzle.position);
        Log($"drawn: wrist at {ext:P0} of reach, elbow bend {ElbowBend():0} deg, muzzle on screen {vp.x:0.00},{vp.y:0.00} z {vp.z:0.00}, brandishing {w.Brandishing}");
        Log(ext > 0.9f && vp.z > 0f && vp.x > 0f && vp.x < 1f && vp.y > 0f && vp.y < 1f ? "PASS draw raises the arm into view" : "FAIL arm not up / gun not in view");
        yield return Shot("drawn");

        // 2. Convergence at many distances: barrel ray through the aim point.
        float worst = 0f; string worstAt = "";
        foreach (var (pitch, yaw) in new[] { (5f, 0f), (20f, 30f), (45f, -40f), (70f, 10f), (-10f, 90f), (0f, 180f), (30f, -120f) })
        {
            fpc.DebugLook(pitch, fpc.transform.eulerAngles.y + yaw);
            yield return new WaitForSeconds(0.7f);
            float miss = BarrelMiss(w);
            float dist = Vector3.Distance(fpc.playerCamera.transform.position, w.AimPoint);
            Log($"  look pitch {pitch} yaw +{yaw}: aim {dist:0.0} m away, barrel misses by {miss * 100f:0.0} cm, blocked {w.Blocked}");
            if (miss > worst) { worst = miss; worstAt = $"{dist:0.0} m"; }
        }
        Log(worst < 0.03f ? $"PASS barrel ray meets the centre ray (worst {worst * 100f:0.0} cm at {worstAt})" : $"FAIL barrel misses the aim point by {worst * 100f:0.0} cm at {worstAt}");

        // 3. Flick: the arm trails, then settles.
        fpc.DebugLook(10f, fpc.transform.eulerAngles.y);
        yield return new WaitForSeconds(0.8f);
        fpc.DebugLook(10f, fpc.transform.eulerAngles.y + 50f);
        yield return null; yield return null;
        float lagNow = Vector3.Angle(w.Gun.muzzle.forward, w.AimPoint - w.Gun.muzzle.position);
        yield return new WaitForSeconds(0.5f);
        float lagLater = Vector3.Angle(w.Gun.muzzle.forward, w.AimPoint - w.Gun.muzzle.position);
        Log($"flick 50 deg: barrel off the aim {lagNow:0.0} deg after 2 frames, {lagLater:0.00} deg after 0.5 s");
        Log(lagNow > 1f && lagLater < 0.5f ? "PASS arm trails a flick and settles" : "WARN flick lag not as expected");

        // 4. Body turn: aim 100 deg to the side, the body follows.
        float body0 = fpc.Animator.BodyYaw;
        fpc.DebugLook(5f, fpc.transform.eulerAngles.y + 100f);
        yield return new WaitForSeconds(0.8f);
        float bodyOff = Mathf.Abs(Mathf.DeltaAngle(fpc.Animator.BodyYaw, fpc.transform.eulerAngles.y));
        Log($"aim 100 deg aside: body turned {Mathf.DeltaAngle(body0, fpc.Animator.BodyYaw):0} deg, now {bodyOff:0} deg off the aim");
        Log(bodyOff < 10f ? "PASS body turns to follow a wide aim" : "FAIL body didn't turn");

        // 5. Steady aim: hand just under the eye line; recoil kicks and recovers.
        w.DebugSteady = true;
        yield return new WaitForSeconds(0.8f);
        var cam = fpc.playerCamera.transform;
        Vector3 eyeToWrist = fig.WristR.position - cam.position;
        float below = -Vector3.Dot(eyeToWrist, cam.up), side = Vector3.Dot(eyeToWrist, cam.right);
        Log($"steady: wrist {below:0.000} m below the eye line, {side:0.000} m to the side, fov {fpc.playerCamera.fieldOfView:0.0}");
        Log(below > 0.04f && below < 0.14f ? "PASS steady aim holds the pistol under the eye line" : "FAIL steady pose off");
        yield return Shot("steady");
        Quaternion g0 = w.Gun.muzzle.rotation;
        w.DebugFire();
        yield return null; yield return null;
        float kick = Quaternion.Angle(g0, w.Gun.muzzle.rotation);
        yield return Shot("recoil");
        yield return new WaitForSeconds(0.6f);
        float back = Quaternion.Angle(g0, w.Gun.muzzle.rotation);
        Log($"recoil: barrel kicked {kick:0.0} deg, {back:0.00} deg off after 0.6 s; shots {PlayerWeapon.ShotsFired}");
        Log(kick > 2f && back < 0.6f ? "PASS recoil kicks and recovers" : "FAIL recoil");
        w.DebugSteady = false;

        // 6. Cover: a slab just under the eye line blocks the barrel but not the view.
        fpc.DebugLook(0f, fpc.transform.eulerAngles.y);
        yield return new WaitForSeconds(0.6f);
        Vector3 flat = Vector3.ProjectOnPlane(cam.forward, Vector3.up).normalized;
        var slab = GameObject.CreatePrimitive(PrimitiveType.Cube);
        slab.name = "TestCover";
        slab.transform.position = cam.position + flat * 1.6f + Vector3.down * (0.08f + 0.3f);
        slab.transform.rotation = Quaternion.LookRotation(flat);
        slab.transform.localScale = new Vector3(2f, 0.6f, 0.3f);
        yield return new WaitForSeconds(0.6f);
        bool blocked = w.Blocked;
        w.DebugFire();
        yield return null;
        Log($"cover slab: aim {Vector3.Distance(cam.position, w.AimPoint):0.0} m, blocked marker {blocked}, shot hit {(PlayerWeapon.LastShotCollider != null ? PlayerWeapon.LastShotCollider.name : "nothing")}");
        Log(blocked && PlayerWeapon.LastShotCollider != null && PlayerWeapon.LastShotCollider.gameObject == slab ? "PASS shot past cover hits the cover, marker shown" : "FAIL cover not detected");
        yield return Shot("blocked");
        Destroy(slab);

        // 7. Wall at arm's length: the elbow bends and the gun stays out of the wall.
        Vector3 start = fpc.transform.position;
        RaycastHit wall = default; bool found = false;
        for (int i = 0; i < 16 && !found; i++)
        {
            Vector3 d = Quaternion.Euler(0f, i * 22.5f, 0f) * Vector3.forward;
            if (Physics.Raycast(start + Vector3.up * 1.2f, d, out var h, 40f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)
                && Mathf.Abs(h.normal.y) < 0.2f && h.collider.GetComponentInParent<FlyingVehicle>() == null
                && Physics.Raycast(start + Vector3.up * 1.8f, d, out var h2, 40f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)
                && Mathf.Abs(h2.distance - h.distance) < 0.1f)
            { wall = h; found = true; }
        }
        if (!found) { Log("WARN no wall near the start"); yield break; }
        Vector3 n = new Vector3(wall.normal.x, 0f, wall.normal.z).normalized;
        fpc.PlaceAt(new Vector3(wall.point.x, start.y, wall.point.z) + n * 0.55f, Quaternion.LookRotation(-n));
        fpc.DebugLook(5f, Quaternion.LookRotation(-n).eulerAngles.y);
        yield return new WaitForSeconds(1f);
        Vector3 mz = w.Gun.muzzle.position;
        float clear = Vector3.Dot(mz - wall.point, n);
        float bend = ElbowBend();
        Log($"wall {wall.collider.name} at {Vector3.Dot(cam.position - wall.point, n):0.00} m from the eyes: elbow bend {bend:0} deg, muzzle {clear:0.00} m off the wall, barrel misses {BarrelMiss(w) * 100f:0.0} cm");
        Log(bend > 25f && clear > 0.05f ? "PASS close wall bends the elbow, no clipping" : "FAIL arm clips / doesn't bend");
        yield return Shot("wall");
        fpc.zoom.Snap(1f);
        fpc.DebugLook(10f, fpc.transform.eulerAngles.y + 150f);
        yield return new WaitForSeconds(0.8f);
        yield return Shot("tp_aim");
        fpc.zoom.Snap(0f);

        // 8. Holster.
        w.Holster();
        yield return new WaitForSeconds(0.5f);
        Log($"holster: gun on {w.Gun.transform.parent.name}, brandishing {w.Brandishing}");
        Log(w.Gun.transform.parent == fig.Hips && !w.Brandishing ? "PASS holster puts the pistol on the hip" : "FAIL holster");
    }

    // ---------- character figures ----------

    // Screenshots of the bodies: first person looking down, guard up, third person, the crowd, officers.
    IEnumerator Figures()
    {
        var fpc = FirstPersonController.Instance;
        if (fpc == null || fpc.Figure == null) { Log("FAIL no player figure"); yield break; }
        string dir = Path.GetDirectoryName(Application.dataPath);
        IEnumerator Shot(string name)
        {
            yield return new WaitForSeconds(0.6f);
            ScreenCapture.CaptureScreenshot(Path.Combine(dir, $"fig_{name}.png"));
            yield return null; yield return null;
            Log($"shot {name}");
        }
        var fists = fpc.GetComponent<PlayerFists>();
        Log($"player figure height {fpc.Figure.Height}, eye {fpc.Figure.EyeHeight:0.00}, capsule {fpc.GetComponent<CharacterController>().height}");

        fpc.DebugLook(80f, 90f);
        yield return Shot("fp_down");
        fpc.DebugLook(0f, 90f);
        yield return Shot("fp_ahead");
        fists.DebugRaise();
        yield return Shot("fp_guard");
        fpc.Animator.Punch(1);
        yield return new WaitForSeconds(0.08f);
        ScreenCapture.CaptureScreenshot(Path.Combine(dir, "fig_fp_punch.png"));
        yield return null;
        fpc.zoom.Snap(1f);
        fpc.DebugLook(15f, 120f);
        fists.DebugRaise();
        yield return Shot("tp_guard");

        // Street: the crowd up close.
        fpc.PlaceAt(new Vector3(27f, 1f, -92f), Quaternion.Euler(0f, 180f, 0f));
        fpc.DebugLook(10f, 180f);
        yield return new WaitForSeconds(4f);
        yield return Shot("tp_street");
        var ps = PedestrianSystem.Instance;
        if (ps != null && ps.NearAgents.Count > 0)
        {
            var p = ps.NearAgents[0];
            fpc.PlaceAt(p.pos + new Vector3(3f, 0.2f, 3f), Quaternion.identity);
            Vector3 to = p.pos - fpc.transform.position;
            fpc.DebugLook(12f, Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg);
            yield return Shot("tp_pedestrian");
        }

        // Officers: wanted, wait for one on foot nearby.
        fpc.PlaceAt(new Vector3(27f, 1f, -92f), Quaternion.Euler(0f, 180f, 0f));
        PoliceDispatch.Ensure().DebugStartPursuit(1);
        float wait = 0f;
        OfficerAgent near = null;
        while (wait < 40f && near == null)
        {
            yield return new WaitForSeconds(0.25f); wait += 0.25f;
            foreach (var o in OfficerAgent.All)
                if (o.State == OfficerAgent.Phase.Foot && Vector3.Distance(o.transform.position, fpc.transform.position) < 2f) near = o;
        }
        if (near != null)
        {
            Vector3 to = near.transform.position - fpc.transform.position;
            fpc.DebugLook(10f, Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg);
            yield return Shot("tp_officer");
            // Punch the officer (aim at its chest): the hit must land on its body-part colliders.
            Vector3 chest = near.transform.position + Vector3.up * 1f - fpc.playerCamera.transform.position;
            fpc.DebugLook(Mathf.Clamp(-Mathf.Atan2(chest.y, new Vector2(chest.x, chest.z).magnitude) * Mathf.Rad2Deg, -80f, 80f),
                          Mathf.Atan2(chest.x, chest.z) * Mathf.Rad2Deg);
            int before = PlayerFists.HitsLanded;
            yield return null;
            fists.DebugPunch(1);
            yield return new WaitForSeconds(0.4f);
            Log($"punch at {Vector3.Distance(near.transform.position, fpc.transform.position):0.0} m: hits {PlayerFists.HitsLanded - before}, force {PoliceDispatch.Instance.ForceLevel}");
            Log(PlayerFists.HitsLanded > before && PoliceDispatch.Instance.ForceLevel == PoliceDispatch.Force.Lethal
                ? "PASS punch landed on the officer (lethal force)" : "FAIL punch did not land");
            fpc.zoom.Snap(0f);
            yield return new WaitForSeconds(2.5f);
            yield return Shot("fp_cuffed");
        }
        else Log("WARN no officer reached the player in 40 s");
    }

    // ---------- screenshots ----------

    // Saves shot_<n>.png next to the executable from a few fixed viewpoints (the camera is taken off
    // the player for it). Viewpoints: -shotview x,y,z,yaw,pitch;x,y,z,yaw,pitch;... or the defaults.
    IEnumerator Shots()
    {
        var cam = Camera.main;
        if (cam == null) { Log("FAIL no camera"); yield break; }
        cam.transform.SetParent(null);
        string spec = null;
        var args = System.Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++) if (args[i] == "-shotview") spec = args[i + 1];
        spec ??= "-28,181.6,0,90,0;0,650,-560,0,45;0,1.7,-160,0,-5;-40,8,-126,90,8;60,121.7,0,90,0;20,62,-60,40,10;230,300,-230,-45,25";
        string dir = Path.GetDirectoryName(Application.dataPath);
        int n = 0;
        // "ped": look at a pedestrian (near tier first, then far), 6 m away.
        var ps = PedestrianSystem.Instance;
        if (ps != null)
        {
            Log($"pedestrians {ps.All.Count}, near {ps.NearAgents.Count}");
            foreach (var who in new[] { ps.NearAgents.Count > 0 ? ps.NearAgents[0] : null, Far(ps) })
            {
                if (who == null) continue;
                // Straight down from 7 m above (never inside a building).
                cam.transform.SetPositionAndRotation(who.pos + Vector3.up * 7f, Quaternion.Euler(89f, 0f, 0f));
                yield return null;
                cam.transform.SetPositionAndRotation(who.pos + Vector3.up * 7f, Quaternion.Euler(89f, 0f, 0f));
                yield return null;
                ScreenCapture.CaptureScreenshot(Path.Combine(dir, $"shot_ped{(who.IsNear ? "near" : "far")}.png"));
                yield return null; yield return null;
                Log($"ped shot {(who.IsNear ? "near" : "far")} at {who.pos:F1} state {who.state}; far drawn {ps.FarDrawn}, draw callbacks {ps.DrawCalls}, " +
                    $"material {(ps.bodyMaterial != null ? ps.bodyMaterial.name + " instancing " + ps.bodyMaterial.enableInstancing : "null")}");
            }
        }
        foreach (var view in spec.Split(';'))
        {
            var v = view.Split(',');
            if (v.Length < 5) continue;
            float F(int k) => float.Parse(v[k], System.Globalization.CultureInfo.InvariantCulture);
            cam.transform.SetPositionAndRotation(new Vector3(F(0), F(1), F(2)), Quaternion.Euler(F(4), F(3), 0f));
            yield return new WaitForSeconds(1.5f);
            string file = Path.Combine(dir, $"shot_{n}.png");
            ScreenCapture.CaptureScreenshot(file);
            yield return null; yield return null;
            Log($"shot {n}: {view}");
            n++;
        }
        yield return new WaitForSeconds(1f);
    }

    static PedestrianSystem.Ped Far(PedestrianSystem ps)
    {
        foreach (var p in ps.All) if (!p.IsNear && p.state != PedestrianSystem.State.Gone && p.goal >= 0) return p;
        return null;
    }

    // ---------- push ----------

    // The player's car drives into a civilian from behind and holds throttle: the civilian must be
    // shoved along (not a dead stop), knocked off its line, and pulled back afterwards.
    IEnumerator Push()
    {
        var fpc = FirstPersonController.Instance;
        FlyingVehicle victim = null;
        foreach (var v in FlyingVehicle.Active)
            if (v.path != null && v.Mode == FlightMode.Lane && v.hasDriver && v.GetComponent<PoliceDriver>() == null &&
                (fpc == null || Vector3.Distance(v.transform.position, fpc.transform.position) < 400f)) { victim = v; break; }
        FlyingVehicle mine = null;
        foreach (var v in FlyingVehicle.Active)
            if (v != victim && v.path != null && v.Mode == FlightMode.Lane && v.GetComponent<PoliceDriver>() == null) { mine = v; break; }
        if (fpc == null || victim == null || mine == null) { Log("FAIL setup"); yield break; }
        mine.EjectDriver(-1);
        mine.Enter(fpc);
        // Side ram: from 6 m to its right and a bit behind, heading diagonally into it at full throttle
        // (Layer mode brakes whatever isn't along the heading, so the heading must keep up with it).
        Vector3 right = victim.transform.right, fwd = victim.transform.forward;
        Vector3 dir = (fwd * 2f - right).normalized;
        mine.DebugPlace(victim.transform.position + right * 6f - fwd * 3f, Quaternion.LookRotation(dir), victim.Velocity - right * 6f);
        float maxOff = 0f, minDist = float.MaxValue;
        for (int i = 0; i < 15; i++)
        {
            mine.DebugThrottle = 1f;
            mine.DebugAimAt(victim.transform.position + victim.Velocity * 0.2f);
            yield return new WaitForSeconds(0.1f);
            minDist = Mathf.Min(minDist, Vector3.Distance(victim.transform.position, mine.transform.position));
            Vector3 rel = victim.transform.InverseTransformPoint(mine.transform.position);
            Log($"  rel {rel.x,5:0.0} {rel.y,5:0.0} {rel.z,5:0.0}  mine {mine.Mode} v {mine.Velocity.magnitude:0.0}  victim v {victim.Velocity.magnitude:0.0} {victim.Mode}");
            maxOff = Mathf.Max(maxOff, victim.DebugLaneOffset() < 50f ? victim.DebugLaneOffset() : 0f);
        }
        mine.DebugThrottle = -1f; // back off
        for (int i = 0; i < 20; i++)
        {
            yield return new WaitForSeconds(0.1f);
            maxOff = Mathf.Max(maxOff, victim.DebugLaneOffset() < 50f ? victim.DebugLaneOffset() : 0f);
        }
        mine.DebugThrottle = 0f;
        Log($"closest {minDist:0.0} m (centres), victim max off its line {maxOff:0.0} m, mode {victim.Mode}");
        yield return new WaitForSeconds(8f);
        float back = victim.DebugLaneOffset();
        Log($"8 s later: off line {back:0.0} m, mode {victim.Mode}");
        Log(back < 1.5f || victim.Mode != FlightMode.Lane ? "PASS pulled back to its line (or recovering)" : "FAIL not pulled back");
        Log(maxOff > 1.5f ? "PASS civilian shoved off its line" : "FAIL civilian not shoved");
    }
}
