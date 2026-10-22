using System.Collections.Generic;
using UnityEngine;

// Graphics Phase 1: the city's real (unshadowed) point and spot lights, under a budget. The builders
// place them (street lamps, blade signs) and list them here; cars register their head / tail lights at
// runtime. Every 0.2 s the nearest lights to the camera (by distance over their kind's reach) are
// switched on, up to maxActive, fading out over the last quarter of the reach so nothing pops; the rest
// are disabled (Forward+ then only sees the ones that matter). Day dims lamps and signs (TimeOfDay).
[DefaultExecutionOrder(50)]
public class CityLights : MonoBehaviour
{
    public enum Kind : byte { Lamp, Sign, Headlight, Taillight, Other }

    [Header("Placed by the builders")]
    public List<Light> lights = new List<Light>();
    public List<Kind> kinds = new List<Kind>();

    [Header("Budget")]
    public int maxActive = 128;
    public float lampReach = 160f, signReach = 260f, headReach = 120f, tailReach = 60f, otherReach = 150f;
    public float interval = 0.2f;

    public static CityLights Instance { get; private set; }
    public bool On { get; set; } = true;
    public int ActiveCount { get; private set; }
    public int TotalCount => entries.Count;

    struct Entry { public Light light; public Kind kind; public float baseIntensity; }
    readonly List<Entry> entries = new List<Entry>();
    readonly Dictionary<Light, int> index = new Dictionary<Light, int>();
    float[] score = new float[0];
    int[] order = new int[0];
    float next;

    void Awake()
    {
        Instance = this;
        for (int i = 0; i < lights.Count; i++) Add(lights[i], i < kinds.Count ? kinds[i] : Kind.Other);
    }

    void OnDestroy() { if (Instance == this) Instance = null; }

    // Runtime lights (cars). The light's current intensity is its full intensity.
    public static void Register(Light l, Kind kind) { if (Instance != null && l != null) Instance.Add(l, kind); }

    public static void Unregister(Light l)
    {
        if (Instance == null || l == null || !Instance.index.TryGetValue(l, out int i)) return;
        var e = Instance.entries;
        int last = e.Count - 1;
        e[i] = e[last]; Instance.index[e[i].light] = i;
        e.RemoveAt(last); Instance.index.Remove(l);
    }

    void Add(Light l, Kind kind)
    {
        if (l == null || index.ContainsKey(l)) return;
        index[l] = entries.Count;
        entries.Add(new Entry { light = l, kind = kind, baseIntensity = l.intensity });
        l.enabled = false;
    }

    float Reach(Kind k) => k switch
    {
        Kind.Lamp => lampReach, Kind.Sign => signReach, Kind.Headlight => headReach, Kind.Taillight => tailReach, _ => otherReach,
    };

    void LateUpdate()
    {
        using var _alloc = AllocProbe.Begin("CityLights.LateUpdate");
        if (Time.unscaledTime < next) return;
        next = Time.unscaledTime + interval;
        Refresh();
    }

    public void Refresh()
    {
        var cam = Camera.main;
        int n = entries.Count;
        if (score.Length < n) { score = new float[n * 2]; order = new int[n * 2]; }
        if (!On || cam == null)
        {
            for (int i = 0; i < n; i++) if (entries[i].light != null) entries[i].light.enabled = false;
            ActiveCount = 0;
            return;
        }
        Vector3 c = cam.transform.position;
        int m = 0;
        for (int i = 0; i < n; i++)
        {
            var e = entries[i];
            if (e.light == null || !e.light.gameObject.activeInHierarchy) { score[i] = float.MaxValue; continue; }
            float s = (e.light.transform.position - c).magnitude / Reach(e.kind);
            score[i] = s;
            if (s < 1f) order[m++] = i; else e.light.enabled = false;
        }
        if (m > maxActive) System.Array.Sort(order, 0, m, Comparer.Instance.With(score));
        float day = TimeOfDay.Instance != null ? TimeOfDay.Instance.DayAmount : 0f;
        int active = 0;
        for (int k = 0; k < m; k++)
        {
            var e = entries[order[k]];
            if (k >= maxActive) { e.light.enabled = false; continue; }
            float fade = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.75f, 1f, score[order[k]]));
            float dayMul = e.kind == Kind.Lamp ? 1f - day : e.kind == Kind.Sign ? Mathf.Lerp(1f, 0.35f, day) : 1f;
            float v = e.baseIntensity * fade * dayMul;
            e.light.enabled = v > 0.01f;
            e.light.intensity = v;
            if (v > 0.01f) active++;
        }
        ActiveCount = active;
    }

    sealed class Comparer : IComparer<int>
    {
        public static readonly Comparer Instance = new Comparer();
        float[] s;
        public Comparer With(float[] score) { s = score; return this; }
        public int Compare(int a, int b) => s[a].CompareTo(s[b]);
    }
}
