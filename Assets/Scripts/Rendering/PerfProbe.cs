using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

// Frame-time benchmark. Starts itself when the player is launched with -perfprobe:
//   Bench.exe -perfprobe -perflabel before -screen-fullscreen 0 -screen-width 1920 -screen-height 1080
// VSync off, a warm-up, then it records frame times while turning the player a full circle (so both
// directions of the canyon are covered), writes perf_<label>.txt next to the executable and quits.
public class PerfProbe : MonoBehaviour
{
    public string label = "run";
    public float warmup = 5f;
    public float duration = 30f;
    public bool quitWhenDone = true;

    readonly List<float> frames = new List<float>(4096);
    float clock;
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
        var probe = new GameObject("PerfProbe").AddComponent<PerfProbe>();
        probe.label = label;
    }

    void Start()
    {
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = -1;
        var fpc = FindAnyObjectByType<FirstPersonController>();
        if (fpc != null) player = fpc.transform;
    }

    void Update()
    {
        float dt = Time.unscaledDeltaTime;
        clock += dt;
        if (clock < warmup) return;
        if (player != null) player.Rotate(0f, 360f / duration * dt, 0f);
        frames.Add(dt * 1000f);
        if (clock < warmup + duration) return;

        Write();
        enabled = false;
        if (quitWhenDone) Application.Quit();
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
        sb.AppendLine($"screen: {Screen.width}x{Screen.height}");
        sb.AppendLine($"gpu: {SystemInfo.graphicsDeviceName}");
        sb.AppendLine($"cpu: {SystemInfo.processorType}");
        string path = Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", $"perf_{label}.txt");
        File.WriteAllText(path, sb.ToString());
        Debug.Log(sb.ToString());
    }
}
