using System.Collections.Generic;
using System.IO;
using System.Text;
using Unity.Profiling;
using UnityEngine;

// Frame-time benchmark. Starts itself when the player is launched with -perfprobe:
//   Bench.exe -perfprobe -perflabel before -screen-fullscreen 0 -screen-width 1920 -screen-height 1080
// VSync off, a warm-up, then it records frame times while turning the player a full circle (so both
// directions of the canyon are covered), writes perf_<label>.txt next to the executable and quits.
// CPU main-thread, render-thread and GPU times come from FrameTimingManager (the player needs
// Frame Timing Stats enabled; BuildScript.BuildBenchmark turns it on).
public class PerfProbe : MonoBehaviour
{
    public string label = "run";
    public float warmup = 5f;
    public float duration = 30f;
    public bool quitWhenDone = true;

    readonly List<float> frames = new List<float>(4096);
    readonly List<float> cpuMain = new List<float>(4096), cpuRender = new List<float>(4096), gpu = new List<float>(4096), presentWait = new List<float>(4096), cpuBusy = new List<float>(4096);
    readonly FrameTiming[] timing = new FrameTiming[1];

    // Per-marker breakdown (only the samplers available in this player; development builds have most).
    static readonly string[] Markers =
    {
        "PlayerLoop",
        "Update.ScriptRunBehaviourUpdate",
        "PreLateUpdate.ScriptRunBehaviourLateUpdate",
        "FixedUpdate.PhysicsFixedUpdate",
        "PostLateUpdate.FinishFrameRendering",
        "PostLateUpdate.UpdateAllRenderers",
        "FlyingVehicle.Update",
        "FlyingVehicle.AITargetSpeed",
        "FlyingVehicle.MoveAndCollide",
        "FPC.SyncTransforms",
        "TrafficSystem.Build",
        "TrafficSystem.Draw",
    };
    readonly List<ProfilerRecorder> recorders = new List<ProfilerRecorder>();
    readonly List<string> recorderNames = new List<string>();
    readonly List<double> recorderSums = new List<double>();
    int recordedFrames;
    float clock;
    int warmupFrames;
    bool measuring;
    Transform player;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        var args = System.Environment.GetCommandLineArgs();
        bool on = false;
        string label = "run";
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "-perfprobe") on = true;
            if (args[i] == "-perflabel" && i + 1 < args.Length) label = args[i + 1];
        }
        if (!on) return;
        // -noocclusion: occlusion culling off (comparison runs). TrafficSystem / pedestrians read it too.
        if (System.Array.Exists(args, a => a == "-noocclusion"))
            foreach (var c in Camera.allCameras) c.useOcclusionCulling = false;
        var probe = new GameObject("PerfProbe").AddComponent<PerfProbe>();
        probe.label = label;
    }

    void Start()
    {
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = -1;
        var fpc = FindAnyObjectByType<FirstPersonController>();
        if (fpc != null) player = fpc.transform;
        foreach (var name in Markers)
        {
            foreach (var cat in new[] { ProfilerCategory.Scripts, ProfilerCategory.Internal, ProfilerCategory.Render, ProfilerCategory.Physics })
            {
                var r = ProfilerRecorder.StartNew(cat, name);
                if (r.Valid) { recorders.Add(r); recorderNames.Add(name); recorderSums.Add(0); break; }
                r.Dispose();
            }
        }
    }

    void OnDestroy()
    {
        foreach (var r in recorders) r.Dispose();
    }

    void Update()
    {
        float dt = Time.unscaledDeltaTime;
        FrameTimingManager.CaptureFrameTimings();
        // Warm-up counts only ordinary frames (a multi-second scene-load hitch doesn't use it up)
        // and lasts at least 60 frames.
        if (!measuring)
        {
            clock += Mathf.Min(dt, 0.1f);
            warmupFrames++;
            if (clock < warmup || warmupFrames < 60) return;
            measuring = true;
            clock = warmup;
        }
        clock += dt;
        if (player != null) player.Rotate(0f, 360f / duration * dt, 0f);
        frames.Add(dt * 1000f);
        for (int i = 0; i < recorders.Count; i++) recorderSums[i] += recorders[i].LastValue * 1e-6; // ns -> ms
        recordedFrames++;
        if (FrameTimingManager.GetLatestTimings(1, timing) > 0)
        {
            if (timing[0].cpuMainThreadFrameTime > 0) cpuMain.Add((float)timing[0].cpuMainThreadFrameTime);
            if (timing[0].cpuRenderThreadFrameTime > 0) cpuRender.Add((float)timing[0].cpuRenderThreadFrameTime);
            if (timing[0].gpuFrameTime > 0) gpu.Add((float)timing[0].gpuFrameTime);
            // Time the main thread spends waiting on Present (VSync / a full GPU queue) isn't CPU work.
            presentWait.Add((float)timing[0].cpuMainThreadPresentWaitTime);
            if (timing[0].cpuMainThreadFrameTime > 0)
                cpuBusy.Add((float)(timing[0].cpuMainThreadFrameTime - timing[0].cpuMainThreadPresentWaitTime));
        }
        if (clock < warmup + duration) return;

        Write();
        enabled = false;
        if (quitWhenDone) Application.Quit();
    }

    // "avg / p95" of a timing series, or n/a when the platform doesn't report it.
    static string Stat(List<float> v)
    {
        if (v.Count == 0) return "n/a";
        var s = new List<float>(v);
        s.Sort();
        float sum = 0f;
        foreach (var x in v) sum += x;
        return $"avg {sum / v.Count:0.00}, median {s[s.Count / 2]:0.00}, p95 {s[Mathf.Min(s.Count - 1, Mathf.RoundToInt(0.95f * (s.Count - 1)))]:0.00}";
    }

    void Write()
    {
        var sorted = new List<float>(frames);
        sorted.Sort();
        float sum = 0f;
        foreach (var f in frames) sum += f;
        float P(float q) => sorted[Mathf.Clamp(Mathf.RoundToInt(q * (sorted.Count - 1)), 0, sorted.Count - 1)];

        var sb = new StringBuilder();
        sb.AppendLine($"label: {label}");
        sb.AppendLine($"scene: {UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}");
        sb.AppendLine($"frames: {frames.Count} over {duration:0} s");
        sb.AppendLine($"avg ms: {sum / frames.Count:0.00}  (fps {1000f * frames.Count / sum:0.0})");
        sb.AppendLine($"median ms: {P(0.5f):0.00}");
        sb.AppendLine($"p95 ms: {P(0.95f):0.00}");
        sb.AppendLine($"p99 ms: {P(0.99f):0.00}");
        sb.AppendLine($"max ms: {sorted[sorted.Count - 1]:0.00}");
        sb.AppendLine($"cpu main thread ms: {Stat(cpuMain)}");
        sb.AppendLine($"cpu main thread busy ms (minus present wait): {Stat(cpuBusy)}");
        sb.AppendLine($"present wait ms: {Stat(presentWait)}");
        sb.AppendLine($"cpu render thread ms: {Stat(cpuRender)}");
        sb.AppendLine($"gpu ms: {Stat(gpu)}");
        sb.AppendLine($"traffic cars: {FlyingVehicle.Active.Count}");
        sb.AppendLine($"development build: {Debug.isDebugBuild}");
        for (int i = 0; i < recorders.Count; i++)
            sb.AppendLine($"  {recorderNames[i]}: {recorderSums[i] / Mathf.Max(1, recordedFrames):0.00} ms/frame");
        sb.AppendLine($"screen: {Screen.width}x{Screen.height}");
        sb.AppendLine($"gpu: {SystemInfo.graphicsDeviceName}");
        sb.AppendLine($"cpu: {SystemInfo.processorType}");
        string path = Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", $"perf_{label}.txt");
        File.WriteAllText(path, sb.ToString());
        Debug.Log(sb.ToString());
    }
}
