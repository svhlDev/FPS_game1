using System.Collections.Generic;
using UnityEngine;

// Pedestrian walk network, built by the district builder (DistrictWalkGraph): nodes on sidewalks,
// skywalks, bridges, crosswalks, at building entrances and taxi pads; undirected edges with widths.
// Crosswalk nodes carry the traffic signal that gates them. PedestrianSystem paths over it with A*.
public class WalkGraph : MonoBehaviour
{
    public enum Kind : byte { Walk, Entrance, TaxiPad, Crossing }

    public Vector3[] positions = new Vector3[0];
    public float[] widths = new float[0];          // walkable width at the node (lane spread)
    public Kind[] kinds = new Kind[0];
    public TrafficSignal[] signals = new TrafficSignal[0];   // per node: the signal gating a crossing (or null)
    public byte[] crossAxis = new byte[0];         // TrafficSignal.Axis the crossing walks along
    // CSR adjacency: neighbours of node i are adj[adjStart[i] .. adjStart[i + 1]).
    public int[] adjStart = new int[1];
    public int[] adj = new int[0];

    public int Count => positions.Length;
    public static WalkGraph Instance { get; private set; }

    readonly List<int> entrances = new List<int>();
    public IReadOnlyList<int> Entrances => entrances;
    readonly List<int> goals = new List<int>();     // entrances + taxi pads
    public IReadOnlyList<int> Goals => goals;

    void Awake()
    {
        Instance = this;
        for (int i = 0; i < kinds.Length; i++)
        {
            if (kinds[i] == Kind.Entrance) { entrances.Add(i); goals.Add(i); }
            else if (kinds[i] == Kind.TaxiPad) goals.Add(i);
        }
    }

    void OnDestroy() { if (Instance == this) Instance = null; }

    // A* over the graph (straight-line heuristic). Fills `path` with node indices from start to goal.
    float[] g; int[] came; byte[] state; readonly List<int> open = new List<int>();
    public bool FindPath(int start, int goal, List<int> path)
    {
        int n = Count;
        if (g == null || g.Length != n) { g = new float[n]; came = new int[n]; state = new byte[n]; }
        System.Array.Clear(state, 0, n);
        open.Clear();
        g[start] = 0f; came[start] = -1; state[start] = 1; open.Add(start);
        Vector3 gp = positions[goal];
        while (open.Count > 0)
        {
            int bi = 0; float bf = float.MaxValue;
            for (int k = 0; k < open.Count; k++)
            {
                int o = open[k];
                float f = g[o] + Vector3.Distance(positions[o], gp);
                if (f < bf) { bf = f; bi = k; }
            }
            int cur = open[bi];
            open[bi] = open[open.Count - 1];
            open.RemoveAt(open.Count - 1);
            if (cur == goal)
            {
                path.Clear();
                for (int c = goal; c != -1; c = came[c]) path.Add(c);
                path.Reverse();
                return true;
            }
            state[cur] = 2;
            for (int e = adjStart[cur]; e < adjStart[cur + 1]; e++)
            {
                int nb = adj[e];
                if (state[nb] == 2) continue;
                float ng = g[cur] + Vector3.Distance(positions[cur], positions[nb]);
                if (state[nb] == 1 && ng >= g[nb]) continue;
                g[nb] = ng; came[nb] = cur;
                if (state[nb] != 1) { state[nb] = 1; open.Add(nb); }
            }
        }
        return false;
    }

    void OnDrawGizmosSelected()
    {
        for (int i = 0; i < Count; i++)
        {
            Gizmos.color = kinds[i] == Kind.Entrance ? Color.green : kinds[i] == Kind.TaxiPad ? Color.yellow : kinds[i] == Kind.Crossing ? Color.red : Color.cyan;
            Gizmos.DrawSphere(positions[i], 0.6f);
            Gizmos.color = new Color(0f, 1f, 1f, 0.4f);
            for (int e = adjStart[i]; e < adjStart[i + 1]; e++) if (adj[e] > i) Gizmos.DrawLine(positions[i], positions[adj[e]]);
        }
    }
}
