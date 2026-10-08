using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using static FlightGrayboxBuilder;

// Tools > Build Twin Towers: traffic platforming slice.
// Two very tall towers, each wrapped in concentric racetrack rings of traffic on all three layers.
// Tower A runs clockwise, Tower B counter-clockwise (from above), so along the straights where
// they face each other the outermost lanes run side by side in the same direction.
// Start on a deck on Tower A, work outward over the car roofs, cross to B, reach B's far deck.
public static class TwinTowersBuilder
{
    const string ScenePath = "Assets/Scenes/TwinTowers.unity";

    // Altitude grid (layer n floor = 5n m, cars ride with their underside 0.5 m above it)
    const int RingBaseLayer = 24;           // rings ride at layers 20 / 24 / 28
    const int RingLayerOffset = 4;
    const int DeckLayer = 29;               // deck tops at 145 m: traffic in layer 28 passes 5 m below
    const int PoliceLayer = 26;

    // Towers
    const float TowerRadius = 30f;
    const float TowerHeight = 300f;

    // Rings
    const int RingsPerTower = 3;
    const float InnerRingRadius = 70f;      // hands-free speed ~ sqrt(magnetStrength * radius): 35 m/s needs ~61 m
    const float CarWidth = 3f;
    const float CarLength = 6f;
    const float CarHalfHeight = 0.75f;
    const float RingGap = 4f;               // gap between car edges in neighbouring rings: a sprint jump
    const float RingSpacing = CarWidth + RingGap;
    const float ParallelRunLength = 100f;   // straights between the semicircles; 0 = true circles
    const float WaypointSpacing = 12f;
    const float SideLaneBlend = 40f;

    // Placement: outermost straights of A and B face each other one RingSpacing apart.
    const float OuterRingRadius = InnerRingRadius + (RingsPerTower - 1) * RingSpacing;
    const float TowerDistance = 2f * OuterRingRadius + RingSpacing;

    // Traffic: rush hour. 18 cars x 9 lanes x 2 towers = 324.
    const int CarsPerLane = 18;
    const float SpeedMin = 18f, SpeedMax = 20f;
    const float SpacingJitter = 0.1f;       // fraction of the even spacing
    const float MinSpawnGap = 15f;          // followGap (9) + car length (6), centre to centre

    // Decks
    const float DeckRingGap = 6f;           // deck stops this short of the innermost ring's centreline
    const float DeckWidth = 24f;
    const float DeckThickness = 1.5f;
    const int ParkedCarsOnStart = 2;

    // Scenery
    const int BackgroundTowers = 40;
    const float BackgroundMinDist = 450f, BackgroundMaxDist = 1200f;
    const int PoliceCount = 4;


    // Decks sit at the top layer, so a jump that misses an upper car can still land on the
    // middle or lower layer below. Spawn (and so fall respawn) moves with the start deck.
    static float DeckTop => TrafficAuthority.FloorHeight(DeckLayer);

    [MenuItem("Tools/Build Twin Towers")]
    public static void Build()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

        var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
        var mainCam = Camera.main;
        mainCam.farClipPlane = 2500f;

        var fogColor = new Color(0.62f, 0.66f, 0.72f);
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.Linear;
        RenderSettings.fogColor = fogColor;
        RenderSettings.fogStartDistance = 250f;
        RenderSettings.fogEndDistance = 1300f;
        mainCam.clearFlags = CameraClearFlags.SolidColor;
        mainCam.backgroundColor = fogColor;

        var groundMat = GetMaterial("Ground", new Color(0.3f, 0.32f, 0.3f));
        var towerMats = new[]
        {
            GetMaterial("TowerA", new Color(0.62f, 0.62f, 0.65f)),
            GetMaterial("TowerB", new Color(0.5f, 0.52f, 0.56f)),
            GetMaterial("TowerC", new Color(0.7f, 0.68f, 0.64f)),
        };
        var platformMat = GetMaterial("Platform", new Color(0.42f, 0.44f, 0.48f));
        var propMat = GetMaterial("Prop", new Color(0.75f, 0.6f, 0.3f));
        var goalMat = GetMaterial("Goal", new Color(0.2f, 0.75f, 0.35f));
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
        var policeMat = GetMaterial("Police", new Color(0.1f, 0.3f, 1f));
        var rackMat = GetMaterial("Rack", new Color(0.15f, 0.15f, 0.17f));
        var playerMat = GetMaterial("Player", new Color(0.9f, 0.9f, 0.9f));

        var rng = new System.Random(11);
        var centerA = Vector3.zero;
        var centerB = new Vector3(TowerDistance, 0f, 0f);

        // Ground, far below the traffic. Touching it respawns you.
        var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
        ground.name = "Ground";
        ground.transform.position = new Vector3(TowerDistance * 0.5f, 0f, 0f);
        ground.transform.localScale = new Vector3(300f, 1f, 300f); // 3 km square
        SetMat(ground, groundMat);

        new GameObject("TrafficAuthority").AddComponent<TrafficAuthority>(); // default grid: 5 m layers

        // Towers
        var towersRoot = new GameObject("Towers").transform;
        Tower(towersRoot, "TowerA", centerA, towerMats[0]);
        Tower(towersRoot, "TowerB", centerB, towerMats[1]);

        Vector3 mid = (centerA + centerB) * 0.5f;
        for (int i = 0; i < BackgroundTowers; i++)
        {
            float ang = (float)rng.NextDouble() * Mathf.PI * 2f;
            float dist = Mathf.Lerp(BackgroundMinDist, BackgroundMaxDist, (float)rng.NextDouble());
            float w = 40f + (float)rng.NextDouble() * 50f;
            float h = SnapToGrid(100f + (float)rng.NextDouble() * 300f);
            Box(towersRoot, $"Background_{i:00}", mid + new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang)) * dist,
                new Vector3(w, h, w), Pick(towerMats, rng));
        }

        // Rings: A clockwise, B counter-clockwise.
        var lanesRoot = new GameObject("Lanes").transform;
        var lanes = new List<LanePath>();
        for (int r = 0; r < RingsPerTower; r++)
        {
            float radius = InnerRingRadius + r * RingSpacing;
            var cw = RacetrackClockwise(centerA, radius);
            lanes.Add(MakeRing($"A_Ring{r}", lanesRoot, cw));
            var ccw = RacetrackClockwise(centerB, radius);
            ccw.Reverse();
            lanes.Add(MakeRing($"B_Ring{r}", lanesRoot, ccw));
        }

        // Traffic: CarsPerLane on each layer of each ring, evenly spaced with small jitter,
        // never closer than MinSpawnGap (fewer cars on a lane too short to fit them all).
        var trafficRoot = new GameObject("Traffic").transform;
        int carIndex = 0;
        foreach (var path in lanes)
        {
            float L = path.Length;
            for (int layer = 0; layer < 3; layer++)
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
                    v.startLevel = path.LevelOf((LaneLayer)layer);
                    v.aiCruiseSpeed = Mathf.Lerp(SpeedMin, SpeedMax, (float)rng.NextDouble());

                    path.Sample(start, out var p, out var f);
                    float w = path.LaneWeight(v.startLevel, start, out var off);
                    v.transform.SetPositionAndRotation(path.ToWorld(p, f, off * w) + Vector3.up * CarRootAboveUnderside,
                                                       Quaternion.LookRotation(f));
                }
            }
        }

        // Start deck on A, facing away from B (-X). Goal deck on B's far side (+X).
        var startRoot = Deck("StartDeck", centerA, -1f, platformMat, propMat);
        var goalRoot = Deck("GoalDeck", centerB, 1f, platformMat, propMat);

        float deckOuter = InnerRingRadius - DeckRingGap;
        for (int i = 0; i < ParkedCarsOnStart; i++)
        {
            float z = (i - (ParkedCarsOnStart - 1) * 0.5f) * 12f;
            var parked = CreateVehicle($"ParkedCar_{i}", parkedMats[i % parkedMats.Length]);
            parked.startParkedOnSurface = true;
            parked.transform.SetParent(startRoot, true);
            parked.transform.SetPositionAndRotation(
                new Vector3(centerA.x - (deckOuter - 10f), DeckTop + CarHalfHeight, centerA.z + z),
                Quaternion.LookRotation(Vector3.left));
        }

        // Goal volume over B's deck
        float deckInner = TowerRadius - 3f;
        float deckLen = deckOuter - deckInner;
        var goal = Slab(goalRoot, "GoalZone",
            new Vector3(centerB.x + deckInner + deckLen * 0.5f, DeckTop + 3f, centerB.z),
            new Vector3(deckLen, 6f, DeckWidth), goalMat);
        Object.DestroyImmediate(goal.GetComponent<MeshRenderer>());
        Object.DestroyImmediate(goal.GetComponent<MeshFilter>());
        goal.gameObject.layer = 2; // Ignore Raycast
        goal.GetComponent<BoxCollider>().isTrigger = true;
        goal.gameObject.AddComponent<GoalZone>();
        Slab(goalRoot, "GoalMarker",
            new Vector3(centerB.x + deckOuter - 4f, DeckTop + 0.05f, centerB.z), new Vector3(4f, 0.1f, DeckWidth - 4f), goalMat);

        // Player at Tower A's doorway, facing out
        var fpc = CreatePlayer(new Vector3(centerA.x - (TowerRadius + 3f), DeckTop, centerA.z),
                               Quaternion.LookRotation(Vector3.left), mainCam, playerMat);
        fpc.fallRespawnGround = ground.GetComponent<Collider>();

        // Police hovering off the far ends of the rings
        var policeRoot = new GameObject("Police").transform;
        float endZ = ParallelRunLength * 0.5f + OuterRingRadius + 15f;
        Vector3[] posts =
        {
            centerA + new Vector3(0f, 0f, endZ), centerA + new Vector3(0f, 0f, -endZ),
            centerB + new Vector3(0f, 0f, endZ), centerB + new Vector3(0f, 0f, -endZ),
        };
        for (int i = 0; i < Mathf.Min(PoliceCount, posts.Length); i++)
        {
            var cop = Slab(policeRoot, $"Police_{i}", posts[i] + Vector3.up * (TrafficAuthority.RideHeight(PoliceLayer) + CarRootAboveUnderside),
                           new Vector3(CarWidth, CarHalfHeight * 2f, CarLength), policeMat);
            AddKinematicBody(cop.gameObject);
            cop.gameObject.AddComponent<PoliceUnit>();
        }

        Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
        EditorSceneManager.SaveScene(scene, ScenePath);
        Selection.activeGameObject = fpc.gameObject;
        Debug.Log($"Twin towers built: {lanes.Count} rings x 3 layers, {carIndex} traffic cars, " +
                  $"tower distance {TowerDistance:0} m, ring spacing {RingSpacing:0} m.");
    }

    // ---------- towers / decks ----------

    static void Tower(Transform parent, string name, Vector3 center, Material mat)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.position = center + Vector3.up * TowerHeight * 0.5f;
        go.transform.localScale = new Vector3(TowerRadius * 2f, TowerHeight * 0.5f, TowerRadius * 2f);
        Object.DestroyImmediate(go.GetComponent<Collider>());
        go.AddComponent<MeshCollider>();
        SetMat(go, mat);
    }

    // Deck cantilevered from the tower along X (dir = -1 or +1), stopping DeckRingGap short of the
    // innermost ring. Railings on the sides only; open toward the traffic.
    static Transform Deck(string name, Vector3 c, float dir, Material mat, Material propMat)
    {
        var root = new GameObject(name).transform;
        float inner = TowerRadius - 3f, outer = InnerRingRadius - DeckRingGap;
        float len = outer - inner;
        float midX = c.x + dir * (inner + outer) * 0.5f;

        Slab(root, "Deck", new Vector3(midX, DeckTop - DeckThickness * 0.5f, c.z), new Vector3(len, DeckThickness, DeckWidth), mat);
        for (int s = -1; s <= 1; s += 2)
        {
            Slab(root, s < 0 ? "RailSouth" : "RailNorth",
                 new Vector3(midX, DeckTop + 0.6f, c.z + s * (DeckWidth * 0.5f - 0.5f)), new Vector3(len, 1.2f, 1f), mat);
            Slab(root, s < 0 ? "BeamSouth" : "BeamNorth",
                 new Vector3(midX - dir * 4f, DeckTop - DeckThickness - 2.5f, c.z + s * (DeckWidth * 0.5f - 3f)),
                 new Vector3(len - 8f, 5f, 2f), mat);
        }
        Slab(root, "Doorway", new Vector3(c.x + dir * (TowerRadius - 0.3f), DeckTop + 5f, c.z), new Vector3(1f, 10f, 8f), propMat);
        return root;
    }

    // ---------- rings ----------

    // Racetrack around center: semicircles of the given radius at z = +/- ParallelRunLength/2,
    // joined by straights along Z. Clockwise seen from above. Waypoints every ~WaypointSpacing m.
    static List<Vector3> RacetrackClockwise(Vector3 c, float radius)
    {
        var pts = new List<Vector3>();
        float run = ParallelRunLength, h = run * 0.5f, y = TrafficAuthority.RideHeight(RingBaseLayer);
        int nStraight = run > 0.01f ? Mathf.Max(1, Mathf.CeilToInt(run / WaypointSpacing)) : 0;
        int nArc = Mathf.Max(4, Mathf.CeilToInt(Mathf.PI * radius / WaypointSpacing));

        for (int i = 0; i < nStraight; i++)          // +X side, heading -Z
            pts.Add(new Vector3(c.x + radius, y, c.z + h - run * i / nStraight));
        for (int i = 0; i < nArc; i++)               // south end, east -> west
        {
            float t = -Mathf.PI * i / nArc;
            pts.Add(new Vector3(c.x + radius * Mathf.Cos(t), y, c.z - h + radius * Mathf.Sin(t)));
        }
        for (int i = 0; i < nStraight; i++)          // -X side, heading +Z
            pts.Add(new Vector3(c.x - radius, y, c.z - h + run * i / nStraight));
        for (int i = 0; i < nArc; i++)               // north end, west -> east
        {
            float t = Mathf.PI - Mathf.PI * i / nArc;
            pts.Add(new Vector3(c.x + radius * Mathf.Cos(t), y, c.z + h + radius * Mathf.Sin(t)));
        }
        return pts;
    }

    // Closed lane through the waypoints, with full-loop Upper and Lower side lanes so the ring
    // exists on all three layers. Side offsets come from the layer altitudes.
    static LanePath MakeRing(string name, Transform parent, List<Vector3> wps)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        for (int i = 0; i < wps.Count; i++)
        {
            var wp = new GameObject($"WP_{i:00}").transform;
            wp.SetParent(go.transform, false);
            wp.position = wps[i];
        }

        var path = go.AddComponent<LanePath>();
        path.closedLoop = true;
        path.baseLayer = RingBaseLayer;
        path.laneLayerOffset = RingLayerOffset;
        path.Rebuild();

        // Start/end pushed past the seam by the blend length so the weight is 1 all the way round.
        float L = path.Length;
        AddFullLoopSide(path, LaneLayer.Upper, L);
        AddFullLoopSide(path, LaneLayer.Lower, L);

        go.AddComponent<LaneLights>();
        LaneLightBaker.Bake(path);
        return path;
    }

    static void AddFullLoopSide(LanePath path, LaneLayer layer, float length)
    {
        path.sideLanes.Add(new SideLaneSegment
        {
            layer = layer,
            startDistance = -SideLaneBlend,
            endDistance = length + SideLaneBlend,
            blendLength = SideLaneBlend,
        });
    }
}
