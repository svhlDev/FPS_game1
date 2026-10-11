using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// Graphics Phase 1: the city's reflection probes (placed by the builders: along streets, at
// intersections, up the canyons). Realtime probes rendered by script instead of baked, so they follow the
// day / night cycle and the city lights: at startup every probe is rendered once, nearest to the camera
// first, a few per frame; afterwards one probe is re-rendered every refreshFrames frames (round robin,
// nearest first), so a full refresh takes a while and costs little per frame. RenderMs is the CPU time of
// the last render call (the GPU cost shows up as frame time: compare with the probes off).
[DefaultExecutionOrder(60)]
public class CityProbes : MonoBehaviour
{
    public List<ReflectionProbe> probes = new List<ReflectionProbe>();
    public int startupPerFrame = 2;
    public int refreshFrames = 20;

    public static CityProbes Instance { get; private set; }
    public bool On { get; private set; } = true;
    public int Rendered { get; private set; }
    public float RenderMs { get; private set; }
    public bool StartupDone { get; private set; }

    readonly Queue<ReflectionProbe> queue = new Queue<ReflectionProbe>();
    bool startup;
    int frame;

    void Awake() => Instance = this;
    void OnDestroy() { if (Instance == this) Instance = null; }

    void Start()
    {
        probes.RemoveAll(p => p == null);
        foreach (var p in probes)
        {
            p.mode = ReflectionProbeMode.Realtime;
            p.refreshMode = ReflectionProbeRefreshMode.ViaScripting;
            p.timeSlicingMode = ReflectionProbeTimeSlicingMode.NoTimeSlicing;
        }
        EnqueueByDistance();
    }

    void EnqueueByDistance()
    {
        var cam = Camera.main;
        Vector3 c = cam != null ? cam.transform.position : Vector3.zero;
        var sorted = new List<ReflectionProbe>(probes);
        sorted.Sort((a, b) => (a.transform.position - c).sqrMagnitude.CompareTo((b.transform.position - c).sqrMagnitude));
        foreach (var p in sorted) queue.Enqueue(p);
    }

    public void SetOn(bool on)
    {
        On = on;
        foreach (var p in probes) if (p != null) p.enabled = on;
        if (on) { queue.Clear(); EnqueueByDistance(); startup = false; StartupDone = false; }
    }

    void Update()
    {
        if (!On || probes.Count == 0) return;
        int count;
        if (!startup)
        {
            count = startupPerFrame;
            if (queue.Count <= count) startup = true;
        }
        else
        {
            if (++frame % Mathf.Max(1, refreshFrames) != 0) return;
            if (queue.Count == 0) EnqueueByDistance();
            count = 1;
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < count && queue.Count > 0; i++)
        {
            var p = queue.Dequeue();
            if (p == null || !p.isActiveAndEnabled) continue;
            p.RenderProbe();
            Rendered++;
        }
        RenderMs = (float)sw.Elapsed.TotalMilliseconds;
        if (startup) StartupDone = true;  // the first pass has rendered every probe
    }
}
