using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using static FlightGrayboxBuilder;

// Tools > Build Sky Avenue: an 800 m canyon (along Z) between two rows of 300-600 m skyscrapers,
// on a 10 m altitude grid. Two traffic loops, one per direction, each a racetrack around one
// building row with its canyon straight inside the canyon:
//   east loop (around the east row) runs north through the canyon,
//   west loop (around the west row) runs south.
// Each loop has 2 concentric lanes, each with 5 stacked levels (layers 10-14, ride 100.5-140.5 m):
// 20 lanes in all. A 10 m median separates the two directions in the canyon. The start deck sticks
// out of a west-row tower at layer 16, 20 m above the top traffic level.
public static class SkyAvenueBuilder
{
    const string ScenePath = "Assets/Scenes/SkyAvenue.unity";

    // Grid
    const float LayerSpacing = 10f;
    const int BaseLayer = 12;                              // middle level rides at 120.5 m
    static readonly int[] LaneLevels = { -2, -1, 0, 1, 2 }; // ride heights 100.5 .. 140.5
    const int DeckLayer = 16;                              // deck top 160 m
    const int PoliceLayer = 15;

    // Canyon and building rows (canyon centred on x = 0)
    const float CanyonLength = 800f;
    const float CanyonWidth = 60f;
    const float RowDepth = 60f;
    const float FootprintMin = 40f, FootprintMax = 60f;
    const float AlleyWidth = 20f;
    const float HeightMin = 300f, HeightMax = 600f;

    // Traffic loops
    const float CarWidth = 3f, CarHalfHeight = 0.75f, CarLength = 6f;
    const float LaneSpacing = CarWidth + 4f;               // between the two concentric lanes of a loop
    const float MedianGap = 10f;                           // between car edges of the two directions
    const float EndClearance = 20f;                        // inner semicircle radius = row half-depth + this
    const float WaypointSpacing = 12f;
    const int CarsPerLane = 25;
    const float SpeedMin = 18f, SpeedMax = 20f;
    const float SpacingJitter = 0.1f;
    const float MinSpawnGap = 15f;
    const float LightSpacing = 30f;

    // Derived lane geometry (x of the canyon straights)
    const float MedianLaneX = MedianGap * 0.5f + CarWidth * 0.5f;   // 6.5: lane next to the median
    const float WallLaneX = MedianLaneX + LaneSpacing;              // 13.5: lane nearer the buildings
    const float InnerRadius = RowDepth * 0.5f + EndClearance;       // 50
    const float LoopCenterX = WallLaneX + InnerRadius;              // 63.5

    // Start deck
    const float DeckReach = 14f;                           // how far it sticks out into the canyon
    const float DeckWidth = 30f;
    const float DeckThickness = 1.5f;
    const int ParkedCars = 2;

    const int PoliceCount = 4;

    static float Snap(float h) => Mathf.Max(1, Mathf.RoundToInt(h / LayerSpacing)) * LayerSpacing;
    static float Ride(int layer) => layer * LayerSpacing + 0.5f;

    [MenuItem("Tools/Build Sky Avenue")]
    public static void Build()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

        var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
        var mainCam = Camera.main;
        mainCam.farClipPlane = 3000f;

        var fogColor = new Color(0.62f, 0.66f, 0.72f);
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.Linear;
        RenderSettings.fogColor = fogColor;
        RenderSettings.fogStartDistance = 250f;
        RenderSettings.fogEndDistance = 1300f;
        mainCam.clearFlags = CameraClearFlags.SolidColor;
        mainCam.backgroundColor = fogColor;

        // Authority first: lanes read the 10 m grid from it while being built.
        var ta = new GameObject("TrafficAuthority").AddComponent<TrafficAuthority>();
        ta.layerSpacing = LayerSpacing;
        ta.hoverHeight = 0.5f;
        ta.maxLayer = 70;

        var groundMat = GetMaterial("Ground", new Color(0.3f, 0.32f, 0.3f));
        var towerMats = new[]
        {
            GetMaterial("TowerA", new Color(0.62f, 0.62f, 0.65f)),
            GetMaterial("TowerB", new Color(0.5f, 0.52f, 0.56f)),
            GetMaterial("TowerC", new Color(0.7f, 0.68f, 0.64f)),
        };
        var platformMat = GetMaterial("Platform", new Color(0.42f, 0.44f, 0.48f));
        var propMat = GetMaterial("Prop", new Color(0.75f, 0.6f, 0.3f));
        var parkedMats = new[]
        {
            GetMaterial("ParkedCar", new Color(0.2f, 0.8f, 0.3f)),
            GetMaterial("ParkedCarRed", new Color(0.85f, 0.15f, 0.15f)),
        };
        var trafficMats = new[]
        {
            GetMaterial("TrafficCar", new Color(0.95f, 0.55f, 0.1f)),
            GetMaterial("TrafficCarYellow", new Color(0.95f, 0.85f, 0.2f)),
            GetMaterial("TrafficCarTeal", new Color(0.1f, 0.7f, 0.7f)),
            GetMaterial("TrafficCarPurple", new Color(0.55f, 0.3f, 0.8f)),
        };
        var rackMat = GetMaterial("Rack", new Color(0.15f, 0.15f, 0.17f));
        var policeMat = GetMaterial("Police", new Color(0.1f, 0.3f, 1f));
        var playerMat = GetMaterial("Player", new Color(0.9f, 0.9f, 0.9f));

        var rng = new System.Random(23);

        var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
        ground.name = "Ground";
        ground.transform.localScale = new Vector3(300f, 1f, 300f); // 3 km square
        SetMat(ground, groundMat);

        // Building rows. Each tower's canyon face is flush with the canyon edge.
        var cityRoot = new GameObject("City").transform;
        var westTowers = new List<Vector2>(); // (centre z, half length) for picking the deck tower
        float halfL = CanyonLength * 0.5f, halfW = CanyonWidth * 0.5f;
        for (int side = -1; side <= 1; side += 2)
        {
            var row = new GameObject(side < 0 ? "WestRow" : "EastRow").transform;
            row.SetParent(cityRoot, false);
            float z = -halfL;
            int i = 0;
            while (z < halfL)
            {
                float len = Mathf.Min(Mathf.Lerp(FootprintMin, FootprintMax, (float)rng.NextDouble()), halfL - z);
                if (len < AlleyWidth) break;
                float depth = Mathf.Lerp(FootprintMin, FootprintMax, (float)rng.NextDouble());
                float h = Snap(Mathf.Lerp(HeightMin, HeightMax, (float)rng.NextDouble()));
                float cx = side * (halfW + depth * 0.5f);
                Box(row, $"Tower_{i++:00}", new Vector3(cx, 0f, z + len * 0.5f), new Vector3(depth, h, len), Pick(towerMats, rng));
                if (side < 0) westTowers.Add(new Vector2(z + len * 0.5f, len * 0.5f));
                z += len + AlleyWidth;
            }
        }

        // Traffic loops: both clockwise from above, which makes the east loop's canyon side run north
        // and the west loop's run south.
        var lanesRoot = new GameObject("Lanes").transform;
        var lanes = new List<LanePath>();
        for (int side = -1; side <= 1; side += 2)
        {
            var center = new Vector3(side * LoopCenterX, 0f, 0f);
            for (int ring = 0; ring < 2; ring++)
            {
                float radius = InnerRadius + ring * LaneSpacing; // ring 1 is the lane next to the median
                lanes.Add(MakeLoop($"{(side < 0 ? "West" : "East")}Loop_{ring}", lanesRoot,
                                   RacetrackClockwise(center, radius, CanyonLength)));
            }
        }

        // Traffic: CarsPerLane on every level of every loop lane.
        var trafficRoot = new GameObject("Traffic").transform;
        int carIndex = 0;
        foreach (var path in lanes)
        {
            float L = path.Length;
            foreach (int level in LaneLevels)
            {
                int count = Mathf.Min(CarsPerLane, Mathf.FloorToInt(L / MinSpawnGap));
                float spacing = L / count;
                float maxJitter = Mathf.Min(SpacingJitter * spacing, (spacing - MinSpawnGap) * 0.5f);
                float phase = (float)rng.NextDouble() * spacing;
                for (int i = 0; i < count; i++)
                {
                    float jitter = ((float)rng.NextDouble() * 2f - 1f) * maxJitter;
                    float start = Mathf.Repeat(phase + i * spacing + jitter, L);
                    var v = CreateVehicle($"Traffic_{carIndex++:000}", Pick(trafficMats, rng));
                    if (carIndex % RackEvery == 0) AddRearRack(v, rackMat);
                    v.transform.SetParent(trafficRoot, false);
                    v.path = path;
                    v.startDistance = start;
                    v.startLevel = level;
                    v.aiCruiseSpeed = Mathf.Lerp(SpeedMin, SpeedMax, (float)rng.NextDouble());

                    path.Sample(start, out var p, out var f);
                    path.LaneWeight(level, start, out var off);
                    v.transform.SetPositionAndRotation(path.ToWorld(p, f, off) + Vector3.up * CarRootAboveUnderside,
                                                       Quaternion.LookRotation(f));
                }
            }
        }

        // Start deck on the west-row tower nearest mid-length, sticking out into the canyon.
        float deckZ = 0f, bestDz = float.MaxValue;
        foreach (var t in westTowers)
            if (Mathf.Abs(t.x) < bestDz) { bestDz = Mathf.Abs(t.x); deckZ = t.x; }
        float deckTop = DeckLayer * LayerSpacing;
        float face = -halfW;                         // west row's canyon face
        float deckInner = face - 1f, deckOuter = face + DeckReach;
        float deckMidX = (deckInner + deckOuter) * 0.5f, deckLen = deckOuter - deckInner;

        var deckRoot = new GameObject("StartDeck").transform;
        Slab(deckRoot, "Deck", new Vector3(deckMidX, deckTop - DeckThickness * 0.5f, deckZ), new Vector3(deckLen, DeckThickness, DeckWidth), platformMat);
        // Railings on three sides (north, south, and along the tower face with a doorway gap); open to the canyon.
        for (int s = -1; s <= 1; s += 2)
        {
            Slab(deckRoot, s < 0 ? "RailSouth" : "RailNorth",
                 new Vector3(deckMidX, deckTop + 0.6f, deckZ + s * (DeckWidth * 0.5f - 0.5f)), new Vector3(deckLen, 1.2f, 1f), platformMat);
            Slab(deckRoot, s < 0 ? "RailBackSouth" : "RailBackNorth",
                 new Vector3(face + 0.5f, deckTop + 0.6f, deckZ + s * (DeckWidth * 0.25f + 2f)), new Vector3(1f, 1.2f, DeckWidth * 0.5f - 4f), platformMat);
            Slab(deckRoot, s < 0 ? "BeamSouth" : "BeamNorth",
                 new Vector3(deckMidX, deckTop - DeckThickness - 2.5f, deckZ + s * (DeckWidth * 0.5f - 3f)), new Vector3(deckLen, 5f, 2f), platformMat);
        }
        Slab(deckRoot, "Doorway", new Vector3(face - 0.3f, deckTop + 5f, deckZ), new Vector3(1f, 10f, 6f), propMat);

        for (int i = 0; i < ParkedCars; i++)
        {
            float z = deckZ + (i - (ParkedCars - 1) * 0.5f) * 12f;
            var parked = CreateVehicle($"ParkedCar_{i}", parkedMats[i % parkedMats.Length]);
            parked.startParkedOnSurface = true;
            parked.transform.SetParent(deckRoot, true);
            parked.transform.SetPositionAndRotation(new Vector3(face + DeckReach - 5f, deckTop + CarHalfHeight, z),
                                                    Quaternion.LookRotation(Vector3.right));
        }

        // Player at the doorway, facing the canyon. Spawn = respawn point.
        var fpc = CreatePlayer(new Vector3(face + 3f, deckTop, deckZ), Quaternion.LookRotation(Vector3.right), mainCam, playerMat);
        fpc.fallRespawnGround = ground.GetComponent<Collider>();

        // Police hovering over the median.
        var policeRoot = new GameObject("Police").transform;
        for (int i = 0; i < PoliceCount; i++)
        {
            float z = Mathf.Lerp(-halfL * 0.75f, halfL * 0.75f, PoliceCount > 1 ? i / (float)(PoliceCount - 1) : 0.5f);
            var cop = Slab(policeRoot, $"Police_{i}", new Vector3(0f, Ride(PoliceLayer) + CarRootAboveUnderside, z),
                           new Vector3(CarWidth, CarHalfHeight * 2f, CarLength), policeMat);
            AddKinematicBody(cop.gameObject);
            cop.gameObject.AddComponent<PoliceUnit>();
        }

        Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
        EditorSceneManager.SaveScene(scene, ScenePath);
        Selection.activeGameObject = fpc.gameObject;
        Debug.Log($"Sky Avenue built: {lanes.Count} loop lanes x {LaneLevels.Length} levels, {carIndex} traffic cars.");
    }

    // Closed flyway through the waypoints with full-loop lanes at every level.
    static LanePath MakeLoop(string name, Transform parent, List<Vector3> wps)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        for (int i = 0; i < wps.Count; i++)
        {
            var wp = new GameObject($"WP_{i:000}").transform;
            wp.SetParent(go.transform, false);
            wp.position = wps[i];
        }
        var path = go.AddComponent<LanePath>();
        path.closedLoop = true;
        path.baseLayer = BaseLayer;
        path.laneLevels = (int[])LaneLevels.Clone();
        path.Rebuild();
        go.AddComponent<LaneLights>().spacing = LightSpacing;
        return path;
    }

    // Racetrack around c: straights along Z of length `run` at x = c.x +/- radius, semicircle ends.
    // Clockwise from above: +X side heads -Z, -X side heads +Z. Waypoints every ~WaypointSpacing m.
    static List<Vector3> RacetrackClockwise(Vector3 c, float radius, float run)
    {
        var pts = new List<Vector3>();
        float h = run * 0.5f, y = Ride(BaseLayer);
        int nStraight = Mathf.Max(1, Mathf.CeilToInt(run / WaypointSpacing));
        int nArc = Mathf.Max(4, Mathf.CeilToInt(Mathf.PI * radius / WaypointSpacing));

        for (int i = 0; i < nStraight; i++)
            pts.Add(new Vector3(c.x + radius, y, c.z + h - run * i / nStraight));
        for (int i = 0; i < nArc; i++)
        {
            float t = -Mathf.PI * i / nArc;
            pts.Add(new Vector3(c.x + radius * Mathf.Cos(t), y, c.z - h + radius * Mathf.Sin(t)));
        }
        for (int i = 0; i < nStraight; i++)
            pts.Add(new Vector3(c.x - radius, y, c.z - h + run * i / nStraight));
        for (int i = 0; i < nArc; i++)
        {
            float t = Mathf.PI - Mathf.PI * i / nArc;
            pts.Add(new Vector3(c.x + radius * Mathf.Cos(t), y, c.z + h + radius * Mathf.Sin(t)));
        }
        return pts;
    }
}
