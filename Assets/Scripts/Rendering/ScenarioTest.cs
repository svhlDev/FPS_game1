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
        Log($"street intersection entries {entries}, on red {redEntries}; most waiting at lights {stoppedAtRedMax}; " +
            $"longest a street car stood still {longestStill:0} s; most sky/street cars off their lanes {offLaneMax}");
        Log(entries > 0 && redEntries == 0 ? "PASS no street car entered an intersection on red" : $"FAIL red-light entries {redEntries} of {entries}");
        Log(stoppedAtRedMax > 0 ? "PASS cars queue at red lights" : "WARN no car seen waiting at a light");
        Log(longestStill < 60f ? "PASS no gridlock (nobody stood still a minute)" : "FAIL a street car stood still over a minute");
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
