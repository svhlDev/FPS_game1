using System.Collections.Generic;
using UnityEngine;

// Builds the WalkGraph from the district's walk rectangles (sidewalks, skywalk decks, bridges,
// crosswalks): a chain of nodes down the middle of each rectangle, links where rectangles at the same
// height touch, building entrances where a chain runs along a tower face, and taxi pad nodes.
public static class DistrictWalkGraph
{
    const float NodeSpacing = 10f;
    const float EntranceEvery = 3;   // every third node along a facade is a door

    public static WalkGraph Build(Transform parent, List<CityDressing.WalkRect> rects, List<Bounds> towerMasses,
                                  List<Vector3> taxiPads, IReadOnlyList<TrafficSignal> signals)
    {
        var pos = new List<Vector3>();
        var width = new List<float>();
        var kind = new List<WalkGraph.Kind>();
        var sig = new List<TrafficSignal>();
        var axis = new List<byte>();
        var edges = new List<List<int>>();
        var chains = new List<List<int>>();

        int AddNode(Vector3 p, float w, WalkGraph.Kind k, TrafficSignal s = null, byte ax = 0)
        {
            pos.Add(p); width.Add(w); kind.Add(k); sig.Add(s); axis.Add(ax); edges.Add(new List<int>());
            return pos.Count - 1;
        }
        void Link(int a, int b)
        {
            if (a == b || edges[a].Contains(b)) return;
            edges[a].Add(b); edges[b].Add(a);
        }

        // Chains down each rectangle.
        foreach (var r in rects)
        {
            var c = r.area.center; var s = r.area.size;
            bool alongX = s.x >= s.z;
            float len = alongX ? s.x : s.z, w = alongX ? s.z : s.x;
            int n = Mathf.Max(1, Mathf.CeilToInt(len / NodeSpacing));
            var chain = new List<int>();
            TrafficSignal gate = null;
            byte ax = 0;
            if (r.crossing)
            {
                foreach (var t in signals)
                    if (gate == null || (t.transform.position - c).sqrMagnitude < (gate.transform.position - c).sqrMagnitude) gate = t;
                ax = (byte)(r.alongNorthSouth ? TrafficSignal.Axis.NorthSouth : TrafficSignal.Axis.EastWest);
            }
            for (int i = 0; i < n; i++)
            {
                float u = n == 1 ? 0f : Mathf.Lerp(-len * 0.5f + 2f, len * 0.5f - 2f, i / (float)(n - 1));
                Vector3 p = c + (alongX ? Vector3.right : Vector3.forward) * u;
                int id = AddNode(p, w, r.crossing ? WalkGraph.Kind.Crossing : WalkGraph.Kind.Walk, gate, ax);
                if (chain.Count > 0) Link(chain[chain.Count - 1], id);
                chain.Add(id);
            }
            chains.Add(chain);
        }

        // Links between rectangles that touch at the same height: each node inside (or within 1.5 m of)
        // the other rectangle joins that rectangle's nearest node.
        for (int a = 0; a < rects.Count; a++)
        {
            var ra = rects[a].area;
            for (int b = a + 1; b < rects.Count; b++)
            {
                var rb = rects[b].area;
                if (Mathf.Abs(ra.center.y - rb.center.y) > 0.4f) continue;
                if (ra.min.x > rb.max.x + 1.5f || rb.min.x > ra.max.x + 1.5f || ra.min.z > rb.max.z + 1.5f || rb.min.z > ra.max.z + 1.5f) continue;
                bool linked = false;
                foreach (int na in chains[a])
                    if (Near(rb, pos[na], 1.5f)) { Link(na, Nearest(chains[b], pos[na], pos)); linked = true; }
                foreach (int nb in chains[b])
                    if (Near(ra, pos[nb], 1.5f)) { Link(nb, Nearest(chains[a], pos[nb], pos)); linked = true; }
                if (!linked)
                {
                    // Edge-to-edge contact with no node inside: join the closest pair.
                    int bestA = -1, bestB = -1; float best = 14f;
                    foreach (int na in chains[a])
                        foreach (int nb in chains[b])
                        {
                            float d = Vector3.Distance(pos[na], pos[nb]);
                            if (d < best) { best = d; bestA = na; bestB = nb; }
                        }
                    if (bestA >= 0) Link(bestA, bestB);
                }
            }
        }

        // Entrances: a door wherever a chain runs along a tower face that's there at that height.
        for (int ci = 0; ci < chains.Count; ci++)
        {
            if (rects[ci].crossing) continue;
            int along = 0;
            foreach (int id in chains[ci].ToArray())
            {
                if (!FacadeNear(pos[id], width[id] * 0.5f + 1.5f, towerMasses, out Vector3 door)) continue;
                if (along++ % EntranceEvery != 0) continue;
                int e = AddNode(door, 2f, WalkGraph.Kind.Entrance);
                Link(e, id);
            }
        }

        // Taxi pads join the nearest node at their height.
        foreach (var p in taxiPads)
        {
            int best = -1; float bestD = 12f;
            for (int i = 0; i < pos.Count; i++)
            {
                if (kind[i] != WalkGraph.Kind.Walk || Mathf.Abs(pos[i].y - p.y) > 0.5f) continue;
                float d = Vector3.Distance(pos[i], p);
                if (d < bestD) { bestD = d; best = i; }
            }
            if (best < 0) continue;
            int t = AddNode(new Vector3(p.x, pos[best].y, p.z), 4f, WalkGraph.Kind.TaxiPad);
            Link(t, best);
        }

        var go = new GameObject("WalkGraph");
        go.transform.SetParent(parent, false);
        var g = go.AddComponent<WalkGraph>();
        g.positions = pos.ToArray();
        g.widths = width.ToArray();
        g.kinds = kind.ToArray();
        g.signals = sig.ToArray();
        g.crossAxis = axis.ToArray();
        var start = new int[pos.Count + 1];
        var adj = new List<int>();
        for (int i = 0; i < pos.Count; i++) { start[i] = adj.Count; adj.AddRange(edges[i]); }
        start[pos.Count] = adj.Count;
        g.adjStart = start;
        g.adj = adj.ToArray();
        return g;
    }

    static bool Near(Bounds r, Vector3 p, float m) =>
        p.x >= r.min.x - m && p.x <= r.max.x + m && p.z >= r.min.z - m && p.z <= r.max.z + m;

    static int Nearest(List<int> chain, Vector3 p, List<Vector3> pos)
    {
        int best = chain[0]; float bd = float.MaxValue;
        foreach (int i in chain)
        {
            float d = (pos[i] - p).sqrMagnitude;
            if (d < bd) { bd = d; best = i; }
        }
        return best;
    }

    // A tower mass whose face is within `reach` of p (horizontally) and spans p's height: the door point
    // just in front of that face.
    static bool FacadeNear(Vector3 p, float reach, List<Bounds> masses, out Vector3 door)
    {
        door = p;
        float best = reach;
        bool found = false;
        foreach (var b in masses)
        {
            if (b.min.y > p.y + 0.5f || b.max.y < p.y + 3f) continue;
            Vector3 q = new Vector3(Mathf.Clamp(p.x, b.min.x, b.max.x), p.y, Mathf.Clamp(p.z, b.min.z, b.max.z));
            float d = Vector3.Distance(new Vector3(p.x, p.y, p.z), q);
            if (d <= 0.01f || d >= best) continue;
            best = d; found = true;
            door = q + (p - q).normalized * 0.6f;
        }
        return found;
    }
}
