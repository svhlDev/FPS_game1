using System;
using System.Collections.Generic;
using UnityEngine;

public enum LaneLayer { Lower = 0, Middle = 1, Upper = 2 }

[Serializable]
public class SideLaneSegment
{
    public LaneLayer layer = LaneLayer.Upper;
    public float startDistance = 50f;
    public float endDistance = 250f;
    [Tooltip("Offset from the middle lane. X = sideways, Y = up/down. Match Y to the TrafficAuthority layer altitudes.")]
    public Vector2 offset = new Vector2(0f, 20f);
    [Tooltip("Distance over which the lane splits off and converges back.")]
    public float blendLength = 40f;
}

[Serializable]
public class NoSwitchZone
{
    public float startDistance = 100f;
    public float endDistance = 160f;
}

// Middle lane = smooth curve through this object's child transforms (waypoints).
// Upper/Lower lanes peel off the middle lane and merge back into it.
// No-switch zones: lane changes here are physically possible but illegal.
[ExecuteAlways]
public class LanePath : MonoBehaviour
{
    public bool closedLoop = true;
    [Range(4, 64)] public int samplesPerSegment = 16;
    public List<SideLaneSegment> sideLanes = new List<SideLaneSegment>();
    public List<NoSwitchZone> noSwitchZones = new List<NoSwitchZone>();

    readonly List<Vector3> points = new List<Vector3>();
    readonly List<float> cumulative = new List<float>();
    public float Length { get; private set; }

    void OnEnable() => Rebuild();
    void OnValidate() => Rebuild();
    void Update() { if (!Application.isPlaying) Rebuild(); }

    public void Rebuild()
    {
        points.Clear(); cumulative.Clear(); Length = 0f;
        int n = transform.childCount;
        if (n < 2) return;

        var wp = new Vector3[n];
        for (int i = 0; i < n; i++) wp[i] = transform.GetChild(i).position;

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

    float WrapDistance(float d) => closedLoop && Length > 0f ? Mathf.Repeat(d, Length) : Mathf.Clamp(d, 0f, Length);

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

    // 0 = lane doesn't exist here, 1 = fully split off. Middle is always 1.
    public float LaneWeight(LaneLayer layer, float distance, out Vector2 offset)
    {
        offset = Vector2.zero;
        if (layer == LaneLayer.Middle) return 1f;
        distance = WrapDistance(distance);

        float best = 0f;
        foreach (var seg in sideLanes)
        {
            if (seg.layer != layer) continue;
            float w = SegmentWeight(seg, distance);
            if (w > best) { best = w; offset = seg.offset; }
        }
        return best;
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

    // Nearest point on this path's lane in the given layer (side lanes only where fully split).
    public bool FindNearest(Vector3 pos, LaneLayer layer, out float distance, out float sqrDist)
    {
        distance = 0f; sqrDist = float.MaxValue;
        bool found = false;
        for (int i = 0; i < points.Count - 1; i++)
        {
            float d = cumulative[i];
            Vector2 off = Vector2.zero;
            if (layer != LaneLayer.Middle)
            {
                float w = LaneWeight(layer, d, out off);
                if (w < 0.9f) continue;
                off *= w;
            }
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
        foreach (var seg in sideLanes)
        {
            Gizmos.color = seg.layer == LaneLayer.Upper ? Color.yellow : Color.magenta;
            bool hasPrev = false; Vector3 prev = Vector3.zero;
            for (float d = seg.startDistance; d <= seg.endDistance; d += 4f)
            {
                Sample(d, out var p, out var f);
                Vector3 wpos = ToWorld(p, f, seg.offset * SegmentWeight(seg, d));
                if (hasPrev) Gizmos.DrawLine(prev, wpos);
                prev = wpos; hasPrev = true;
            }
        }
    }
}
