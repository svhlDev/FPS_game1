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
