using System.Collections.Generic;
using UnityEngine;

// Traffic lights at a street-level intersection (built by DistrictBuilder). Two phases on one cycle:
//   north-south green (greenTime) -> amber (amberTime) -> all red (allRedTime) ->
//   east-west green -> amber -> all red -> ...
// Street traffic (grid layer 0) stops at the stop line (the box edge) unless its axis is green, or
// amber and too close to stop. Pedestrians walk across a street while the traffic running alongside
// their crossing has green (WalkAllowed). Lamp renderers swap between lit and off materials on phase
// changes only.
public class TrafficSignal : MonoBehaviour
{
    public enum Axis { NorthSouth, EastWest }
    public enum Light { Green, Amber, Red }

    public float greenTime = 20f;
    public float amberTime = 3f;
    public float allRedTime = 1f;
    [Tooltip("Intersection box half extents (x, z); the stop lines are its edges.")]
    public Vector2 halfExtents = new Vector2(30f, 20f);
    [Tooltip("Stop lines sit this far back from the box edge (behind the crosswalk).")]
    public float stopBack = 5f;
    [Tooltip("Cycle offset (s), so neighbouring signals aren't in lockstep.")]
    public float offset;

    [Header("Lamps (index 0 green, 1 amber, 2 red), one set per axis")]
    public Renderer[] northSouthLamps;
    public Renderer[] eastWestLamps;
    public Material greenOn, amberOn, redOn, lampOff;

    static readonly List<TrafficSignal> all = new List<TrafficSignal>();
    public static IReadOnlyList<TrafficSignal> All => all;

    float Cycle => 2f * (greenTime + amberTime + allRedTime);
    int shownNs = -1, shownEw = -1;

    void OnEnable() => all.Add(this);
    void OnDisable() => all.Remove(this);

    public Light State(Axis axis)
    {
        float t = Mathf.Repeat(Time.time + offset, Cycle);
        float half = greenTime + amberTime + allRedTime;
        if (axis == Axis.EastWest) t = Mathf.Repeat(t - half, Cycle);
        if (t < greenTime) return Light.Green;
        if (t < greenTime + amberTime) return Light.Amber;
        return Light.Red;
    }

    // Seconds left of this axis's green (0 when not green).
    public float GreenLeft(Axis axis)
    {
        float t = Mathf.Repeat(Time.time + offset, Cycle);
        if (axis == Axis.EastWest) t = Mathf.Repeat(t - (greenTime + amberTime + allRedTime), Cycle);
        return t < greenTime ? greenTime - t : 0f;
    }

    // People crossing the street that runs along `crossingAlong` may walk while that traffic is green.
    public bool WalkAllowed(Axis crossingAlong) => State(crossingAlong) == Light.Green && GreenLeft(crossingAlong) > 4f;

    public bool Contains(Vector3 p, float margin = 0f)
    {
        Vector3 c = transform.position;
        return Mathf.Abs(p.x - c.x) < halfExtents.x + margin && Mathf.Abs(p.z - c.z) < halfExtents.y + margin;
    }

    // Distance from a street car to this intersection's stop line along its heading, or -1 if the
    // intersection isn't ahead of it in its lane (or it's already inside).
    public float StopLineDistance(Vector3 pos, Vector3 fwd, out Axis axis)
    {
        Vector3 c = transform.position;
        axis = Mathf.Abs(fwd.z) >= Mathf.Abs(fwd.x) ? Axis.NorthSouth : Axis.EastWest;
        if (Contains(pos, stopBack)) return -1f; // past the stop line: carry on
        if (axis == Axis.NorthSouth)
        {
            if (Mathf.Abs(pos.x - c.x) > halfExtents.x) return -1f; // not on a road through this box
            float edge = fwd.z > 0f ? c.z - halfExtents.y - stopBack : c.z + halfExtents.y + stopBack;
            float d = (edge - pos.z) * Mathf.Sign(fwd.z);
            return d >= 0f ? d : -1f;
        }
        else
        {
            if (Mathf.Abs(pos.z - c.z) > halfExtents.y) return -1f;
            float edge = fwd.x > 0f ? c.x - halfExtents.x - stopBack : c.x + halfExtents.x + stopBack;
            float d = (edge - pos.x) * Mathf.Sign(fwd.x);
            return d >= 0f ? d : -1f;
        }
    }

    // Speed limit for a street car approaching from `pos` heading `fwd` at `speed`: stop at the line on
    // red, and on amber unless it's too close to stop (braking m/s^2). Infinity when free to go.
    public static float StreetLimit(Vector3 pos, Vector3 fwd, float speed, float braking, float halfLength, float gain, float look = 60f)
    {
        float limit = float.PositiveInfinity;
        for (int i = 0; i < all.Count; i++)
        {
            var s = all[i];
            float d = s.StopLineDistance(pos, fwd, out var axis);
            if (d < 0f || d > look) continue;
            var light = s.State(axis);
            if (light == Light.Green) continue;
            float gap = d - halfLength - 1f;
            if (light == Light.Amber && gap < speed * speed / (2f * Mathf.Max(braking, 0.1f))) continue; // can't stop: go
            limit = Mathf.Min(limit, gap <= 0.5f ? 0f : Mathf.Max(0f, gain * gap));
        }
        return limit;
    }

    void Update()
    {
        int ns = (int)State(Axis.NorthSouth), ew = (int)State(Axis.EastWest);
        if (ns != shownNs) { Show(northSouthLamps, ns); shownNs = ns; }
        if (ew != shownEw) { Show(eastWestLamps, ew); shownEw = ew; }
    }

    // Lamps come in triples (green, amber, red) per signal head.
    void Show(Renderer[] lamps, int state)
    {
        if (lamps == null) return;
        for (int i = 0; i < lamps.Length; i++)
        {
            if (lamps[i] == null) continue;
            int kind = i % 3;
            bool on = kind == state;
            lamps[i].sharedMaterial = on ? (kind == 0 ? greenOn : kind == 1 ? amberOn : redOn) : lampOff;
        }
    }
}
