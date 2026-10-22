using System;
using System.Collections.Generic;
using UnityEngine;

// Phase 8: the pool of generated bodies, filled in the background from scene load (BodyBuild: Burst
// jobs and worker tasks, a few in flight at once; the main thread only starts them and uploads the
// meshes). Characters take a body by seed:
//   Civilian(seed) : pedestrians (near-tier promotion), ejected civilian drivers
//   Officer(seed)  : officers, from a sub-pool rolled with STR / DEX above average
// A body not generated yet returns null: the caller uses the primitive figure meanwhile.
// Request() generates one-off bodies (the player's, when their stats change) through the same queue,
// ahead of the pool fill.
// -bodies off on the command line (or Enabled = false) keeps everyone on the primitive figure.
// Preset (BodyPreset): bodies baked in the editor fill their slots at startup (decoded on a worker, a
// few meshes uploaded per frame); only the slots it doesn't hold are generated (hybrid). -preset off
// skips it.
[DefaultExecutionOrder(-50)]
public class BodyPool : MonoBehaviour
{
    public int civilians = 120;
    public int officers = 30;
    public int maxInFlight = 2;
    public float officerStrBias = 3f, officerDexBias = 3f;
    [Tooltip("Load the baked preset (Resources/BodyPoolPreset) at startup.")]
    public bool usePreset = true;
    [Tooltip("Preset bodies turned into meshes per frame while loading.")]
    public int presetUploadsPerFrame = 6;
    public const float DefaultOfficerStrBias = 3f, DefaultOfficerDexBias = 3f;

    public static BodyPool Instance { get; private set; }
    public static bool Enabled = true;

    BodyAsset[] civ, off;
    readonly Queue<(int index, bool officer)> queue = new Queue<(int, bool)>();
    readonly Queue<(CharacterSheet sheet, Action<BodyAsset> done)> requests = new Queue<(CharacterSheet, Action<BodyAsset>)>();
    readonly List<(BodyBuild build, int index, bool officer, Action<BodyAsset> done)> inFlight = new List<(BodyBuild, int, bool, Action<BodyAsset>)>();

    // Stats
    public int Ready { get; private set; }
    public int Total => (civ?.Length ?? 0) + (off?.Length ?? 0);
    public int Failed { get; private set; }
    public float FillStarted { get; private set; } = -1f;
    public float FillSeconds { get; private set; } = -1f;
    public readonly List<float> WallMs = new List<float>(), MainMs = new List<float>();
    public readonly List<int> Triangles = new List<int>();
    public float StepMsThisFrame { get; private set; }
    public readonly float[] StageMaxMs = new float[9];
    public float WorstStepMs { get; private set; }
    public int PresetLoaded { get; private set; }
    public float PresetSeconds { get; private set; } = -1f;
    public string PresetProblem { get; private set; }
    System.Threading.Tasks.Task<List<BodyPreset.Entry>> presetTask;
    readonly Queue<BodyPreset.Entry> presetUploads = new Queue<BodyPreset.Entry>();
    float presetStart;

    public static BodyPool Ensure()
    {
        if (Instance != null) return Instance;
        var args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++) if (args[i] == "-bodies" && args[i + 1] == "off") Enabled = false;
        var go = new GameObject("BodyPool");
        DontDestroyOnLoad(go);
        return Instance = go.AddComponent<BodyPool>();
    }

    void Awake()
    {
        civ = new BodyAsset[civilians];
        off = new BodyAsset[officers];
        // Interleave so both kinds become available early.
        for (int i = 0; i < Mathf.Max(civilians, officers); i++)
        {
            if (i < civilians) queue.Enqueue((i, false));
            if (i < officers && i % 4 == 0) queue.Enqueue((i, true));
        }
        for (int i = 0; i < officers; i++) if (i % 4 != 0) queue.Enqueue((i, true));
        StartPreset();
    }

    void StartPreset()
    {
        foreach (var a in Environment.GetCommandLineArgs()) if (a == "-presetoff") usePreset = false;
        if (!usePreset || !Enabled) return;
        var ta = Resources.Load<TextAsset>(BodyPreset.ResourceName);
        if (ta == null) return;
        var bytes = ta.bytes;
        Resources.UnloadAsset(ta);
        BodyPreset.PrepareDecode();
        presetStart = Time.realtimeSinceStartup;
        presetTask = System.Threading.Tasks.Task.Run(() =>
        {
            var list = BodyPreset.Decode(bytes, out string problem);
            PresetProblem = problem;
            return list;
        });
    }

    // Preset bodies into their slots, a few per frame (mesh upload is main-thread work).
    void UpdatePreset()
    {
        if (presetTask != null && presetTask.IsCompleted)
        {
            if (presetTask.IsFaulted) { PresetProblem = "preset failed to load: " + presetTask.Exception?.InnerException?.Message; Debug.LogWarning(PresetProblem); }
            else
            {
                if (PresetProblem != null) Debug.LogWarning("BodyPool preset: " + PresetProblem);
                foreach (var e in presetTask.Result) presetUploads.Enqueue(e);
            }
            presetTask = null;
        }
        for (int k = 0; k < presetUploadsPerFrame && presetUploads.Count > 0; k++)
        {
            var e = presetUploads.Dequeue();
            var arr = e.officer ? off : civ;
            if (e.index < 0 || e.index >= arr.Length || arr[e.index] != null) continue;
            try { arr[e.index] = e.ToAsset(); } catch (Exception ex) { PresetProblem = ex.Message; continue; }
            Ready++; PresetLoaded++;
            if (presetUploads.Count == 0 && presetTask == null) PresetSeconds = Time.realtimeSinceStartup - presetStart;
        }
    }

    // Still waiting for the preset? (Generation holds off until it's in, then fills what it didn't hold.)
    bool PresetPending => presetTask != null || presetUploads.Count > 0;

    public static BodyAsset Civilian(int seed) => Get(seed, false);
    public static BodyAsset Officer(int seed) => Get(seed, true);

    static BodyAsset Get(int seed, bool officer)
    {
        if (!Enabled || Instance == null) return null;
        var arr = officer ? Instance.off : Instance.civ;
        if (arr == null || arr.Length == 0) return null;
        int i = (seed & int.MaxValue) % arr.Length;
        // Not generated yet: the nearest ready one after it (so early on a few bodies are shared).
        for (int k = 0; k < arr.Length; k++)
        {
            var a = arr[(i + k) % arr.Length];
            if (a != null) return a;
        }
        return null;
    }

    // A one-off body (the player's), ahead of the pool queue.
    public void Request(CharacterSheet sheet, Action<BodyAsset> done) => requests.Enqueue((sheet, done));

    CharacterSheet SheetFor(int index, bool officer) => SheetFor(index, officer, officerStrBias, officerDexBias);

    // A pool slot's sheet and outfit (the editor bake makes exactly what the pool would).
    public static CharacterSheet SheetFor(int index, bool officer, float strBias, float dexBias) =>
        officer ? CharacterSheet.Roll(50000 + index, strBias, 0f, dexBias) : CharacterSheet.Roll(1000 + index);
    public static Outfit OutfitFor(CharacterSheet sheet, bool officer) => Wardrobe.For(officer ? CharacterFigure.Role.Police : CharacterFigure.Role.Civilian, sheet);

    void Update()
    {
        using var _alloc = AllocProbe.Begin("BodyPool.Update");
        if (!Enabled && requests.Count == 0 && inFlight.Count == 0) return;
        if (FillStarted < 0f) FillStarted = Time.realtimeSinceStartup;
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        UpdatePreset();
        while (inFlight.Count < maxInFlight && requests.Count > 0)
        {
            var (sheet, done) = requests.Dequeue();
            inFlight.Add((new BodyBuild(sheet, null, Wardrobe.For(CharacterFigure.Role.Player, sheet)), -1, false, done));
        }
        int arg = CommandLineInFlight();
        if (arg > 0) maxInFlight = arg;
        while (Enabled && !PresetPending && inFlight.Count < maxInFlight && queue.Count > 0)
        {
            var (index, officer) = queue.Dequeue();
            if ((officer ? off : civ)[index] != null) continue;   // the preset had it
            var sheet = SheetFor(index, officer);
            inFlight.Add((new BodyBuild(sheet, null, OutfitFor(sheet, officer)), index, officer, null));
        }
        for (int i = inFlight.Count - 1; i >= 0; i--)
        {
            var f = inFlight[i];
            if (!f.build.Step()) continue;
            inFlight.RemoveAt(i);
            var a = f.build.Asset;
            if (a == null) { Failed++; continue; }
            WallMs.Add(a.wallMs); MainMs.Add(a.mainThreadMs); Triangles.Add(a.triangles);
            for (int s = 0; s < StageMaxMs.Length; s++) StageMaxMs[s] = Mathf.Max(StageMaxMs[s], f.build.StageMainMs[s]);
            if (f.done != null) { f.done(a); continue; }
            (f.officer ? off : civ)[f.index] = a;
            Ready++;
        }
        if (Ready + Failed >= Total && FillSeconds < 0f) FillSeconds = Time.realtimeSinceStartup - FillStarted;
        StepMsThisFrame = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
        WorstStepMs = Mathf.Max(WorstStepMs, StepMsThisFrame);
    }

    static int inFlightArg = -1;
    static int CommandLineInFlight()
    {
        if (inFlightArg >= 0) return inFlightArg;
        inFlightArg = 0;
        var args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++) if (args[i] == "-bodyjobs" && int.TryParse(args[i + 1], out int n)) inFlightArg = n;
        return inFlightArg;
    }

    public bool Filling => PresetPending || queue.Count > 0 || inFlight.Count > 0;
}
