using System;
using System.Collections.Generic;
using UnityEngine;

// Legacy lane names. Upper/Lower side-lane segments map onto levels +/- laneLayerOffset.
public enum LaneLayer { Lower = 0, Middle = 1, Upper = 2 }

[Serializable]
public class SideLaneSegment
{
    public LaneLayer layer = LaneLayer.Upper;
    public float startDistance = 50f;
    public float endDistance = 250f;
    [Tooltip("Sideways offset from the middle line (m). Height comes from the path's laneLayerOffset.")]
    public float sideOffset;
    [Tooltip("Distance over which the lane splits off and converges back.")]
    public float blendLength = 40f;
}

[Serializable]
public class NoSwitchZone
{
    public float startDistance = 100f;
    public float endDistance = 160f;
}

// Flyway. Middle lane = smooth curve through this object's child transforms (waypoints), flattened
// to RideHeight(baseLayer). Lanes are LEVELS: whole-layer offsets from baseLayer, stacked straight
// above/below the middle line. Two kinds:
//   laneLevels : full-loop lanes, e.g. {-2,-1,0,1,2} = five stacked lanes.
//   sideLanes  : legacy Upper/Lower segments at +/- laneLayerOffset that peel off and merge back.
// Level 0 (the middle line) always exists. All heights are car UNDERSIDE heights.
// No-switch zones: lane changes here are physically possible but illegal.
[ExecuteAlways]
public class LanePath : MonoBehaviour
{
    public bool closedLoop = true;
    [Tooltip("Grid layer of the middle lane (level 0).")]
    public int baseLayer = 8;
    [Tooltip("Full-loop lanes as layer offsets from baseLayer, e.g. -2..2. 0 is always present.")]
    public int[] laneLevels = new int[0];
    [Tooltip("Legacy Upper/Lower side lanes sit this many layers above/below the middle lane.")]
    public int laneLayerOffset = 4;
    [Range(4, 64)] public int samplesPerSegment = 16;
    public List<SideLaneSegment> sideLanes = new List<SideLaneSegment>();
    public List<NoSwitchZone> noSwitchZones = new List<NoSwitchZone>();

    readonly List<Vector3> points = new List<Vector3>();
    readonly List<float> cumulative = new List<float>();
    readonly List<int> levels = new List<int>();
    public float Length { get; private set; }

    // Cars currently attached to this lane (Lane mode). Maintained by FlyingVehicle via TrafficSystem.
    [NonSerialized] public readonly List<FlyingVehicle> Cars = new List<FlyingVehicle>();

    void OnEnable() => Rebuild();
    void OnValidate() => Rebuild();
    void Update() { if (!Application.isPlaying) Rebuild(); }

    public void Rebuild()
    {
        points.Clear(); cumulative.Clear(); Length = 0f;
        int n = transform.childCount;
        if (n < 2) return;

        var wp = new Vector3[n];
        float rideY = TrafficAuthority.RideHeight(baseLayer);
        for (int i = 0; i < n; i++) { wp[i] = transform.GetChild(i).position; wp[i].y = rideY; }

        int segs = closedLoop ? n : n - 1;
        for (int s = 0; s < segs; s++)
        {
            Vector3 p0, p1 = wp[s], p2, p3;
            if (closedLoop) { p0 = wp[Wrap(s - 1, n)]; p2 = wp[Wrap(s + 1, n)]; p3 = wp[Wrap(s + 2, n)]; }
            else { p0 = wp[Mathf.Max(s - 1, 0)]; p2 = wp[Mathf.Min(s + 1, n - 1)]; p3 = wp[Mathf.Min(s + 2, n - 1)]; }
            for (int k = 0; k < samplesPerSegment; k++)
                AddPoint(CatmullRom(p0, p1, p2, p3, k / (float)samplesPerSegment));
        }
        AddPoint(closedLoop ? wp[0] : wp[n - 1]);
    }

    void AddPoint(Vector3 p)
    {
        if (points.Count > 0) Length += Vector3.Distance(points[points.Count - 1], p);
        points.Add(p);
        cumulative.Add(Length);
    }

    public float WrapDistance(float d) => closedLoop && Length > 0f ? Mathf.Repeat(d, Length) : Mathf.Clamp(d, 0f, Length);

    public void Sample(float distance, out Vector3 position, out Vector3 forward)
    {
        if (points.Count < 2) { position = transform.position; forward = transform.forward; return; }
        distance = WrapDistance(distance);

        int lo = 0, hi = cumulative.Count - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (cumulative[mid] < distance) lo = mid; else hi = mid;
        }
        float segLen = cumulative[hi] - cumulative[lo];
        float t = segLen > 0.0001f ? (distance - cumulative[lo]) / segLen : 0f;

        position = Vector3.Lerp(points[lo], points[hi], t);
        forward = points[hi] - points[lo];
        forward = forward.sqrMagnitude > 1e-6f ? forward.normalized : transform.forward;
    }

    // ---------- levels ----------

    public int LevelOf(SideLaneSegment seg) => seg.layer == LaneLayer.Upper ? laneLayerOffset : -laneLayerOffset;
    public int LevelOf(LaneLayer lane) => ((int)lane - 1) * laneLayerOffset;
    public int GridLayerOf(int level) => baseLayer + level;

    bool HasFullLevel(int level)
    {
        foreach (int l in laneLevels) if (l == level) return true;
        return false;
    }

    // Every level this flyway has somewhere (0, full-loop levels, legacy side lanes). Not allocation-free
    // to build, so it's refreshed on demand into a cached list.
    public List<int> Levels
    {
        get
        {
            levels.Clear();
            levels.Add(0);
            foreach (int l in laneLevels) if (!levels.Contains(l)) levels.Add(l);
            foreach (var seg in sideLanes) { int l = LevelOf(seg); if (!levels.Contains(l)) levels.Add(l); }
            return levels;
        }
    }

    // 0 = lane doesn't exist here, 1 = fully split off. Level 0 and full-loop levels are always 1.
    // offset = (sideways m, up/down m) from the middle line.
    public float LaneWeight(int level, float distance, out Vector2 offset)
    {
        offset = new Vector2(0f, level * TrafficAuthority.Spacing);
        if (level == 0) { offset = Vector2.zero; return 1f; }
        if (HasFullLevel(level)) return 1f;
        distance = WrapDistance(distance);

        float best = 0f;
        foreach (var seg in sideLanes)
        {
            if (LevelOf(seg) != level) continue;
            float w = SegmentWeight(seg, distance);
            if (w > best) { best = w; offset = LaneOffset(seg); }
        }
        return best;
    }

    // Next level from `current` in direction dir (+1 up / -1 down) that exists at this distance.
    public bool NextLevel(int current, int dir, float distance, out int next)
    {
        next = current;
        bool found = false;
        foreach (int l in Levels)
        {
            if ((l - current) * dir <= 0) continue;
            if (LaneWeight(l, distance, out _) <= 0.9f) continue;
            if (!found || Mathf.Abs(l - current) < Mathf.Abs(next - current)) { next = l; found = true; }
        }
        return found;
    }

    // Offset of a legacy side lane from the middle line: sideways in metres, up/down by whole layers.
    public Vector2 LaneOffset(SideLaneSegment seg) => new Vector2(seg.sideOffset, LevelOf(seg) * TrafficAuthority.Spacing);

    public int LowestLayer
    {
        get
        {
            int min = 0;
            foreach (int l in Levels) min = Mathf.Min(min, l);
            return baseLayer + min;
        }
    }

    public bool IsNoSwitch(float distance)
    {
        distance = WrapDistance(distance);
        foreach (var z in noSwitchZones)
            if (distance >= z.startDistance && distance <= z.endDistance) return true;
        return false;
    }

    // Smooth tangent: direction between a point slightly behind and one slightly ahead.
    // Avoids the per-segment kinks of the sampled polyline.
    public Vector3 SmoothForward(float distance, float window = 5f)
    {
        Sample(distance - window, out var a, out _);
        Sample(distance + window, out var b, out var f);
        Vector3 d = b - a;
        return d.sqrMagnitude > 1e-6f ? d.normalized : f;
    }

    // Distance along the path of the point nearest to pos (measured in XZ), searched around a hint.
    // Result is kept continuous with the hint so it never jumps across the loop seam.
    public float Project(Vector3 pos, float hint, float window = 40f)
    {
        int segCount = points.Count - 1;
        if (segCount < 1) return hint;

        Vector2 p2 = new Vector2(pos.x, pos.z);
        float bestSq = float.MaxValue, best = hint, covered = 0f;
        int i = IndexAt(hint - window);
        for (int guard = 0; guard < segCount && covered < 2f * window; guard++)
        {
            int j = i + 1;
            Vector2 a = new Vector2(points[i].x, points[i].z), b = new Vector2(points[j].x, points[j].z);
            Vector2 ab = b - a;
            float len2 = ab.sqrMagnitude;
            float t = len2 > 1e-6f ? Mathf.Clamp01(Vector2.Dot(p2 - a, ab) / len2) : 0f;
            float sq = (a + ab * t - p2).sqrMagnitude;
            float segLen = cumulative[j] - cumulative[i];
            if (sq < bestSq) { bestSq = sq; best = cumulative[i] + segLen * t; }
            covered += segLen;
            i = j;
            if (i >= segCount) { if (closedLoop) i = 0; else break; }
        }

        if (closedLoop && Length > 0f)
            best = hint + (Mathf.Repeat(best - hint + Length * 0.5f, Length) - Length * 0.5f);
        return best;
    }

    int IndexAt(float distance)
    {
        distance = WrapDistance(distance);
        int lo = 0, hi = cumulative.Count - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (cumulative[mid] <= distance) lo = mid; else hi = mid;
        }
        return Mathf.Min(lo, cumulative.Count - 2);
    }

    public Vector3 ToWorld(Vector3 basePos, Vector3 forward, Vector2 offset)
    {
        Vector3 right = Vector3.Cross(Vector3.up, forward).normalized;
        return basePos + right * offset.x + Vector3.up * offset.y;
    }

    // Nearest point on this path's lane at the given level (side lanes only where fully split).
    public bool FindNearest(Vector3 pos, int level, out float distance, out float sqrDist)
    {
        distance = 0f; sqrDist = float.MaxValue;
        bool found = false;
        for (int i = 0; i < points.Count - 1; i++)
        {
            float d = cumulative[i];
            float w = LaneWeight(level, d, out Vector2 off);
            if (w < 0.9f) continue;
            off *= w;
            Vector3 fwd = points[i + 1] - points[i];
            if (fwd.sqrMagnitude < 1e-6f) continue;
            float sq = (ToWorld(points[i], fwd.normalized, off) - pos).sqrMagnitude;
            if (sq < sqrDist) { sqrDist = sq; distance = d; found = true; }
        }
        return found;
    }

    public static float SegmentWeight(SideLaneSegment seg, float d)
    {
        if (d < seg.startDistance || d > seg.endDistance) return 0f;
        float b = Mathf.Max(0.01f, seg.blendLength);
        float w = Mathf.Min(Mathf.Clamp01((d - seg.startDistance) / b), Mathf.Clamp01((seg.endDistance - d) / b));
        return Mathf.SmoothStep(0f, 1f, w);
    }

    static int Wrap(int i, int n) => ((i % n) + n) % n;

    static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
    {
        float t2 = t * t, t3 = t2 * t;
        return 0.5f * ((2f * p1) + (-p0 + p2) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
    }

    void OnDrawGizmos()
    {
        if (points.Count < 2) return;
        for (int i = 1; i < points.Count; i++)
        {
            Gizmos.color = IsNoSwitch(cumulative[i]) ? Color.red : Color.cyan;
            Gizmos.DrawLine(points[i - 1], points[i]);
        }
        foreach (int l in laneLevels)
        {
            if (l == 0) continue;
            Gizmos.color = l > 0 ? Color.yellow : Color.magenta;
            Vector3 up = Vector3.up * (l * TrafficAuthority.Spacing);
            for (int i = 1; i < points.Count; i++) Gizmos.DrawLine(points[i - 1] + up, points[i] + up);
        }
        foreach (var seg in sideLanes)
        {
            Gizmos.color = seg.layer == LaneLayer.Upper ? Color.yellow : Color.magenta;
            bool hasPrev = false; Vector3 prev = Vector3.zero;
            for (float d = seg.startDistance; d <= seg.endDistance; d += 4f)
            {
                Sample(d, out var p, out var f);
                Vector3 wpos = ToWorld(p, f, LaneOffset(seg) * SegmentWeight(seg, d));
                if (hasPrev) Gizmos.DrawLine(prev, wpos);
                prev = wpos; hasPrev = true;
            }
        }
    }
}
