using System;
using System.Collections.Generic;

// Garbage per system: bytes allocated on the main thread inside a scope, summed per name. On only while
// PerfProbe measures (-perfprobe); otherwise a scope costs one bool test.
//   using var _ = AllocProbe.Begin("PedestrianSystem.Update");
public static class AllocProbe
{
    public static bool On;
    // (Unity's Mono reports no per-thread allocation counter: the managed heap's used size instead - grows with
    // every allocation until a collection, which a scope then reads as negative and is skipped. Development
    // players only.)
    static long Heap() => UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong();
    static readonly Dictionary<string, int> slots = new Dictionary<string, int>();
    static readonly List<string> names = new List<string>();
    static readonly List<long> bytes = new List<long>();
    static readonly List<long> calls = new List<long>();

    public readonly struct Scope : IDisposable
    {
        readonly int slot; readonly long start;
        public Scope(int slot, long start) { this.slot = slot; this.start = start; }
        public void Dispose()
        {
            if (slot < 0) return;
            long d = Heap() - start;
            if (d > 0) bytes[slot] += d;
            calls[slot]++;
        }
    }

    public static Scope Begin(string name)
    {
        if (!On) return new Scope(-1, 0);
        if (!slots.TryGetValue(name, out int s)) { s = names.Count; slots[name] = s; names.Add(name); bytes.Add(0); calls.Add(0); }
        return new Scope(s, Heap());
    }

    public static void Reset() { for (int i = 0; i < bytes.Count; i++) { bytes[i] = 0; calls[i] = 0; } }

    // Name, bytes per frame, calls per frame - the biggest first.
    public static List<(string name, double bytesPerFrame, double callsPerFrame)> Report(int frames)
    {
        var r = new List<(string, double, double)>();
        for (int i = 0; i < names.Count; i++) r.Add((names[i], bytes[i] / (double)Math.Max(1, frames), calls[i] / (double)Math.Max(1, frames)));
        r.Sort((a, b) => b.Item2.CompareTo(a.Item2));
        return r;
    }
}
