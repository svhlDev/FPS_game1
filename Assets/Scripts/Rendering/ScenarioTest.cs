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
public class ScenarioTest : MonoBehaviour
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
            return;
        }
    }

    IEnumerator Start()
    {
        Application.runInBackground = true; // keep going when the window loses focus
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = 60;
        t0 = Time.time;
        Log($"scenario {scenarioName}, scene {UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}");
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
            default: Log($"FAIL unknown scenario {scenarioName}"); break;
        }
        Finish();
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
        car.DebugPlace(car.transform.position, car.transform.rotation, Vector3.zero);
        for (int i = 0; i < 4; i++)
        {
            FireSystem.Spray(car.transform.position + Vector3.up * 5f, Vector3.zero, 3, 0.3f, 1.2f);
            yield return new WaitForSeconds(0.25f);
        }
        yield return new WaitForSeconds(2f);
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
        yield return null; yield return null;
        for (float t = 0f; t < 120f && lineup.MeshesBuilt < lineup.Entries.Count; t += Time.unscaledDeltaTime) yield return null;
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
        Vector3 spot = me + side * 8f;
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
