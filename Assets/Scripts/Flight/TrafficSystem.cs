using System.Collections.Generic;
using UnityEngine;

// Per-frame snapshot of every vehicle for the traffic AI, built once before any car updates:
//  - position, velocity, heading etc. cached in a plain array (States)
//  - each lane's registered cars sorted by progress per layer, so a car's leader is simply
//    the next slot (wrapping on closed loops)
//  - a coarse XZ grid (cell = yieldLookRange) for the crossing / cut-in check
// Created automatically by the first car that registers with a lane.
[DefaultExecutionOrder(-20)]
public class TrafficSystem : MonoBehaviour
{
    public struct CarState
    {
        public FlyingVehicle car;
        public Vector3 pos, vel, fwd;
        public bool occupied;
        public LanePath path;       // lane it's registered on, or null
        public int layer;
        public float dist;          // progress on path, wrapped into [0, Length)
        public int leader;          // index in States of the next car ahead on the same path + layer, or -1
        public float leaderGap;     // centre-to-centre distance along the path to the leader
    }

    public static CarState[] States = new CarState[256];
    public static int Count;
    public static float CellSize = 80f;     // set each frame to the largest yieldLookRange

    static TrafficSystem instance;
    static int builtFrame = -1;
    static readonly List<LanePath> lanesWithCars = new List<LanePath>();

    // Lane sorting buffers
    static double[] sortKeys = new double[64];
    static int[] sortIdx = new int[64];

    // Grid: car indices sorted by cell key, and each cell's run in that array.
    static long[] cellKeys = new long[256];
    static int[] cellSorted = new int[256];
    static readonly Dictionary<long, Vector2Int> cells = new Dictionary<long, Vector2Int>(); // x = start, y = count

    // ---------- lane registration ----------

    public static void Register(FlyingVehicle car, LanePath path)
    {
        if (instance == null) instance = new GameObject("TrafficSystem").AddComponent<TrafficSystem>();
        if (path.Cars.Count == 0) lanesWithCars.Add(path);
        path.Cars.Add(car);
    }

    public static void Unregister(FlyingVehicle car, LanePath path)
    {
        if (ReferenceEquals(path, null)) return; // also runs for lanes being destroyed on scene unload
        path.Cars.Remove(car);
        if (path.Cars.Count == 0) lanesWithCars.Remove(path);
    }

    // ---------- per-frame build ----------

    void Update() => EnsureBuilt();

    public static void EnsureBuilt()
    {
        if (builtFrame == Time.frameCount) return;
        builtFrame = Time.frameCount;

        var all = FlyingVehicle.Active;
        Count = all.Count;
        if (States.Length < Count) States = new CarState[Mathf.NextPowerOfTwo(Count)];
        float maxLook = 1f;

        for (int i = 0; i < Count; i++)
        {
            var car = all[i];
            car.TrafficIndex = i;
            maxLook = Mathf.Max(maxLook, car.yieldLookRange);
            var path = car.RegisteredPath;
            var t = car.transform;
            States[i] = new CarState
            {
                car = car,
                pos = t.position,
                vel = car.Velocity,
                fwd = t.forward,
                occupied = car.IsOccupied,
                path = path,
                layer = car.GridLayer,
                dist = path != null ? path.WrapDistance(car.LaneDistance) : 0f,
                leader = -1,
            };
        }

        CellSize = maxLook; // one cell = look range, so the 3x3 neighbourhood covers it
        foreach (var lane in lanesWithCars) LinkLeaders(lane);
        BuildGrid();
    }

    // Sort the lane's cars by (layer, progress); each car's leader is the next one in its layer group.
    static void LinkLeaders(LanePath lane)
    {
        var cars = lane.Cars;
        int n = cars.Count;
        if (sortKeys.Length < n) { sortKeys = new double[Mathf.NextPowerOfTwo(n)]; sortIdx = new int[sortKeys.Length]; }

        int m = 0;
        for (int k = 0; k < n; k++)
        {
            int i = cars[k].TrafficIndex;
            if (i < 0 || i >= Count || States[i].car != cars[k]) continue;
            sortKeys[m] = States[i].layer * 1e7 + States[i].dist; // double: grid layers go up to ~60
            sortIdx[m] = i;
            m++;
        }
        System.Array.Sort(sortKeys, sortIdx, 0, m);

        float L = lane.Length;
        for (int g = 0; g < m;)
        {
            int layer = States[sortIdx[g]].layer, end = g;
            while (end < m && States[sortIdx[end]].layer == layer) end++;
            int groupCount = end - g;
            if (groupCount > 1)
            {
                for (int k = g; k < end; k++)
                {
                    bool last = k == end - 1;
                    if (last && !lane.closedLoop) continue;
                    int me = sortIdx[k], ahead = sortIdx[last ? g : k + 1];
                    float gap = States[ahead].dist - States[me].dist;
                    if (gap < 0f) gap += L;
                    States[me].leader = ahead;
                    States[me].leaderGap = gap;
                }
            }
            g = end;
        }
    }

    static void BuildGrid()
    {
        if (cellKeys.Length < Count) { cellKeys = new long[States.Length]; cellSorted = new int[States.Length]; }
        for (int i = 0; i < Count; i++)
        {
            CellOf(States[i].pos, out int cx, out int cz);
            cellKeys[i] = Key(cx, cz);
            cellSorted[i] = i;
        }
        System.Array.Sort(cellKeys, cellSorted, 0, Count);

        cells.Clear();
        for (int s = 0; s < Count;)
        {
            int e = s;
            while (e < Count && cellKeys[e] == cellKeys[s]) e++;
            cells[cellKeys[s]] = new Vector2Int(s, e - s);
            s = e;
        }
    }

    // ---------- queries ----------

    public static void CellOf(Vector3 p, out int cx, out int cz)
    {
        cx = Mathf.FloorToInt(p.x / CellSize);
        cz = Mathf.FloorToInt(p.z / CellSize);
    }

    static long Key(int cx, int cz) => ((long)cx << 32) ^ (uint)cz;

    // Run of entries in CellCars for one cell.
    public static bool TryCell(int cx, int cz, out int start, out int count)
    {
        if (cells.TryGetValue(Key(cx, cz), out var run)) { start = run.x; count = run.y; return true; }
        start = count = 0;
        return false;
    }

    public static int CellCar(int slot) => cellSorted[slot];
}
