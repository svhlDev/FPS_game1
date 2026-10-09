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
// Each loop has 2 concentric lanes, each with 10 stacked levels in two streams (layers 10-14 and 19-23):
// 40 lanes in all. A 10 m median separates the two directions in the canyon. The start deck sticks
// out of a west-row tower at layer 16, 20 m above the top traffic level.
// Towers 250-600 m (1 in 5 a giant through the cloud deck), smog layer and cloud deck above.
// Street level (layer 0) is playable: road, sidewalks, paved alleys, shops, lamps (CityStreet.cs), and
// two sparse street-traffic loops, one per direction, that stop for you when you're on foot in their lane.
// Look: night city from CityDressing (procedural facade windows, setbacks, neon, holograms, bridges,
// ledges, underworld haze, fog, bloom). Same CitySeed = same city.
public static class SkyAvenueBuilder
{
    const string ScenePath = "Assets/Scenes/SkyAvenue.unity";
    const int CitySeed = 23;

    // Altitude bands for the look: underworld below layer 9, traffic band 9-15, upper city 16+.
    const int TrafficBandMinLayer = 9, TrafficBandMaxLayer = 23;   // both streams get the bright treatment (90-240 m)
    const int TallTowerLayers = 45;                        // giants (450 m+) get a blinking aircraft light
    const float HologramChance = 0.25f;
    const float BridgeChance = 0.35f;        // per LedgeAlley
    const float ClimbAlleyChance = 0.5f;     // alleys kept perfectly flat for wall running / bouncing
    static readonly float[] HazeHeights = { 15f, 35f, 60f };
    static readonly Color[] WallTints =
    {
        new Color(0.2f, 0.21f, 0.23f),   // dark concrete
        new Color(0.17f, 0.2f, 0.25f),   // blue-grey
        new Color(0.24f, 0.23f, 0.22f),  // warm concrete
    };

    // Grid
    const float LayerSpacing = 10f;
    const int BaseLayer = 12;                              // middle level rides at 120.5 m
    // Two streams on the same loops: layers 10-14 (100.5-140.5 m) and 19-23 (190.5-230.5 m), with the deck
    // (layer 16) in the 50 m gap between them. Space from layer 14 goes straight to 19.
    static readonly int[] LaneLevels = { -2, -1, 0, 1, 2, 7, 8, 9, 10, 11 };
    const int DeckLayer = 25;                              // deck top 250 m, 20 m above the top lane (layer 23)
    const int DeckTowerMinLayers = 30;                     // the deck's tower is at least 300 m tall

    // Canyon and building rows (canyon centred on x = 0)
    const float CanyonLength = 600f;
    const float CanyonWidth = 60f;
    const float FootprintMin = 70f, FootprintMax = 120f;
    const float AlleyMin = 14f, AlleyMax = 20f;            // close walls for wall running
    const float HeightMin = 250f, HeightMax = 450f;
    const float GiantChance = 0.2f;                        // giants pierce the cloud deck
    const float GiantMin = 450f, GiantMax = 600f;

    // Sky
    static readonly float[] CloudHeights = { 330f, 360f };
    const float SkySize = 6000f;                           // ground, haze and cloud planes

    // Traffic loops
    static readonly float CarWidth = CarSize.x, CarHalfHeight = CarSize.y * 0.5f, CarLength = CarSize.z;
    static readonly float LaneSpacing = CarWidth + 4f;     // between the two concentric lanes of a loop
    const float MedianGap = 10f;                           // between car edges of the two directions
    const float EndClearance = 20f;                        // loops clear the back of the row by this much
    const float WaypointSpacing = 12f;
    const int MaxTrafficCars = 700;                        // total budget, split by lane length
    const int MinCarsPerLane = 4;
    const float SpeedMin = 18f, SpeedMax = 20f;
    const float SpacingJitter = 0.1f;
    const float LightSpacing = 30f;

    // Derived lane geometry (x of the canyon straights)
    static readonly float MedianLaneX = MedianGap * 0.5f + CarWidth * 0.5f;   // lane next to the median
    static readonly float WallLaneX = MedianLaneX + LaneSpacing;              // 13.5: lane nearer the buildings

    // Start deck
    const float DeckReach = 14f;                           // how far it sticks out into the canyon
    const float DeckWidth = 30f;
    const float DeckThickness = 1.5f;
    const int ParkedCars = 2;

    const int PoliceCount = 4;

    // Street traffic: one loop per direction at layer 0 on the outer road lanes, wrapping round the rows.
    const float StreetLaneX = CityDressing.StreetLaneWidth * 1.5f;  // 5.25: outer lane centre
    const float StreetCarSpacing = 120f;
    const float StreetSpeed = 15f;

    static float Snap(float h) => Mathf.Max(1, Mathf.RoundToInt(h / LayerSpacing)) * LayerSpacing;
    static float Ride(int layer) => layer * LayerSpacing + 0.5f;

    [MenuItem("Tools/Build Sky Avenue")]
    public static void Build()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

        var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
        var mainCam = Camera.main;
        mainCam.farClipPlane = 3000f;

        // Authority first: lanes read the 10 m grid from it while being built.
        var ta = new GameObject("TrafficAuthority").AddComponent<TrafficAuthority>();
        ta.layerSpacing = LayerSpacing;
        ta.hoverHeight = 0.5f;
        ta.maxLayer = 70;                                  // above the giants

        var groundMat = GetMaterial("UnderworldGround", new Color(0.05f, 0.05f, 0.06f));
        var kit = CityDressing.CreateKit(LayerSpacing, TrafficBandMinLayer, TrafficBandMaxLayer);
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
        var playerMat = GetMaterial("Player", new Color(0.9f, 0.9f, 0.9f));

        // Layout + traffic use `rng` (same sequence as before the look pass); setbacks and decoration
        // use their own stream, so both are deterministic per seed.
        var rng = new System.Random(CitySeed);
        var decoRng = new System.Random(CitySeed * 7919 + 1);

        var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
        ground.name = "Ground";
        ground.transform.localScale = new Vector3(SkySize / 10f, 1f, SkySize / 10f);
        SetMat(ground, groundMat);

        // Building rows. Each tower's canyon face is flush with the canyon edge.
        var cityRoot = new GameObject("City").transform;
        var westTowers = new List<Vector2>(); // (centre z, half length) for picking the deck tower
        var rows = new List<List<CityDressing.Tower>> { new List<CityDressing.Tower>(), new List<CityDressing.Tower>() };
        float halfL = CanyonLength * 0.5f, halfW = CanyonWidth * 0.5f;
        // Pass 1: footprints (same draws from `rng` as always, so layout and traffic don't change).
        var plots = new List<(int side, Vector3 center, Vector2 footprint, int layers, Color tint)>();
        for (int side = -1; side <= 1; side += 2)
        {
            float z = -halfL;
            while (z < halfL)
            {
                float len = Mathf.Min(Mathf.Lerp(FootprintMin, FootprintMax, (float)rng.NextDouble()), halfL - z);
                if (len < FootprintMin * 0.5f) break;
                float depth = Mathf.Lerp(FootprintMin, FootprintMax, (float)rng.NextDouble());
                bool giant = rng.NextDouble() < GiantChance;
                float h = Snap(giant ? Mathf.Lerp(GiantMin, GiantMax, (float)rng.NextDouble())
                                     : Mathf.Lerp(HeightMin, HeightMax, (float)rng.NextDouble()));
                float cx = side * (halfW + depth * 0.5f);
                var tint = Pick(WallTints, rng);
                plots.Add((side, new Vector3(cx, 0f, z + len * 0.5f), new Vector2(depth, len), Mathf.RoundToInt(h / LayerSpacing), tint));
                if (side < 0) westTowers.Add(new Vector2(z + len * 0.5f, len * 0.5f));
                z += len + Mathf.Lerp(AlleyMin, AlleyMax, (float)rng.NextDouble());
            }
        }
        // Loops follow the rows: the outer straight of each loop clears the deepest tower by EndClearance.
        float rowDepth = 0f;
        foreach (var plot in plots) rowDepth = Mathf.Max(rowDepth, plot.footprint.x);
        float innerRadius = (halfW + rowDepth + EndClearance - WallLaneX) * 0.5f;
        float loopCenterX = WallLaneX + innerRadius;
        // The deck hangs off the west tower nearest mid-length: keep that one a plain slab (no slot, no recess).
        int deckPlot = -1;
        for (int p = 0; p < plots.Count; p++)
            if (plots[p].side < 0 && (deckPlot < 0 || Mathf.Abs(plots[p].center.z) < Mathf.Abs(plots[deckPlot].center.z))) deckPlot = p;

        // Pass 2: towers from archetypes.
        var rowRoots = new[] { new GameObject("WestRow").transform, new GameObject("EastRow").transform };
        foreach (var r in rowRoots) r.SetParent(cityRoot, false);
        for (int p = 0; p < plots.Count; p++)
        {
            var plot = plots[p];
            int r = plot.side < 0 ? 0 : 1;
            bool deckTower = p == deckPlot;
            int layers = deckTower ? Mathf.Max(plot.layers, DeckTowerMinLayers) : plot.layers;
            var tower = CityDressing.BuildTower(rowRoots[r], $"Tower_{rows[r].Count:00}", plot.center, plot.footprint, layers,
                                                new Vector3(-plot.side, 0f, 0f), plot.tint, decoRng, kit,
                                                deckTower ? CityDressing.Archetype.Slab : (CityDressing.Archetype?)null, allowRecess: !deckTower);
            tower.tall = tower.heightLayers >= TallTowerLayers;
            rows[r].Add(tower);
        }

        // Traffic loops: both clockwise from above, which makes the east loop's canyon side run north
        // and the west loop's run south.
        var lanesRoot = new GameObject("Lanes").transform;
        var lanes = new List<LanePath>();
        for (int side = -1; side <= 1; side += 2)
        {
            var center = new Vector3(side * loopCenterX, 0f, 0f);
            for (int ring = 0; ring < 2; ring++)
            {
                float radius = innerRadius + ring * LaneSpacing; // ring 1 is the lane next to the median
                lanes.Add(MakeLoop($"{(side < 0 ? "West" : "East")}Loop_{ring}", lanesRoot,
                                   RacetrackClockwise(center, radius, CanyonLength, Ride(BaseLayer))));
            }
        }
        // Street loops: same shape at layer 0 on the outer road lanes (east runs north, west south).
        var streetLanes = new List<LanePath>();
        float streetRadius = (halfW + rowDepth + EndClearance - StreetLaneX) * 0.5f;
        for (int side = -1; side <= 1; side += 2)
        {
            var center = new Vector3(side * (StreetLaneX + streetRadius), 0f, 0f);
            streetLanes.Add(MakeLoop($"{(side < 0 ? "West" : "East")}Street", lanesRoot,
                                     RacetrackClockwise(center, streetRadius, CanyonLength, Ride(0)), 0, new[] { 0 }, false));
        }

        // Traffic: MaxTrafficCars split across every level of every loop lane in proportion to lane
        // length (at least MinCarsPerLane each, never closer than MinSpawnGap).
        var trafficRoot = new GameObject("Traffic").transform;
        int carIndex = 0;

        // Street traffic first (sparse, slow, its own random stream); it comes out of the same budget.
        var streetRng = new System.Random(CitySeed * 31 + 7);
        int streetCars = 0;
        foreach (var path in streetLanes)
        {
            int count = Mathf.Max(1, Mathf.RoundToInt(path.Length / StreetCarSpacing));
            float spacing = path.Length / count, phase = (float)streetRng.NextDouble() * spacing;
            for (int i = 0; i < count; i++)
            {
                float start = Mathf.Repeat(phase + i * spacing, path.Length);
                var v = CreateVehicle($"StreetCar_{streetCars++:000}", Pick(trafficMats, streetRng));
                v.transform.SetParent(trafficRoot, false);
                v.path = path;
                v.startDistance = start;
                v.startLevel = 0;
                v.aiCruiseSpeed = StreetSpeed;
                path.Sample(start, out var sp, out var sf);
                v.transform.SetPositionAndRotation(sp + Vector3.up * CarRootAboveUnderside, Quaternion.LookRotation(sf));
            }
        }
        int skyBudget = Mathf.Max(0, MaxTrafficCars - streetCars);

        float totalLength = 0f;
        foreach (var path in lanes) totalLength += path.Length * LaneLevels.Length;
        foreach (var path in lanes)
        {
            float L = path.Length;
            foreach (int level in LaneLevels)
            {
                int count = Mathf.Clamp(Mathf.RoundToInt(skyBudget * L / totalLength), MinCarsPerLane,
                                        Mathf.Max(1, Mathf.FloorToInt(L / MinSpawnGap)));
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
            parked.hasDriver = false;
            parked.transform.SetParent(deckRoot, true);
            parked.transform.SetPositionAndRotation(new Vector3(face + DeckReach - 5f, deckTop + CarHalfHeight, z),
                                                    Quaternion.LookRotation(Vector3.right));
        }

        // Player at the doorway, facing the canyon. Spawn = respawn point.
        var fpc = CreatePlayer(new Vector3(face + 3f, deckTop, deckZ), Quaternion.LookRotation(Vector3.right), mainCam, playerMat);

        // ---------- look pass: decoration, ledges, bridges, holograms, atmosphere ----------
        kit.clearance = new CityDressing.Clearance(lanes);

        // Street level: road, sidewalks, alleys, lamps, signs, vents.
        var streetPlots = new List<CityDressing.StreetPlot>();
        foreach (var plot in plots)
            streetPlots.Add(new CityDressing.StreetPlot { side = plot.side, center = plot.center, footprint = plot.footprint });
        CityDressing.BuildStreet(cityRoot, halfW, halfL, streetPlots, decoRng, kit);
        // Police station near the north end: door in an east-row facade, landing pad on the road in front.
        CityDressing.BuildPoliceStation(cityRoot, halfW, halfL - 35f, streetPlots, kit);
        kit.keepOut.Add(new Bounds(new Vector3(deckMidX, deckTop, deckZ), new Vector3(deckLen + 6f, 30f, DeckWidth + 6f)));
        for (int r = 0; r < rows.Count; r++)
        {
            var row = rows[r];

            // Tag each alley: ClimbAlley (both walls flat) or LedgeAlley (bays, balconies, bridges allowed).
            var climb = new bool[Mathf.Max(0, row.Count - 1)];
            for (int a = 0; a < climb.Length; a++)
            {
                climb[a] = decoRng.NextDouble() < ClimbAlleyChance;
                row[a].climbPlus = climb[a];
                row[a + 1].climbMinus = climb[a];
            }

            for (int i = 0; i < row.Count; i++)
            {
                CityDressing.AddBays(row[i], decoRng, kit);
                CityDressing.AddBalconies(row[i], decoRng, kit);
                CityDressing.AddTwinBridges(row[i], decoRng, kit);
                CityDressing.DressTower(row[i], decoRng, kit);
                CityDressing.AddLedges(row[i], decoRng, kit);
                if (decoRng.NextDouble() < HologramChance) CityDressing.AddHologram(row[i], decoRng, kit);
            }
            for (int a = 0; a < climb.Length; a++)
            {
                if (!climb[a] && decoRng.NextDouble() < BridgeChance)
                    CityDressing.AddBridge(row[a].root, row[a], row[a + 1], Vector3.forward, decoRng, kit);
                CityDressing.AddCables(row[a].root, row[a], row[a + 1], decoRng, kit);
            }
        }
        CityDressing.AddUnderworldHaze(cityRoot, Vector3.zero, SkySize, HazeHeights, kit);
        CityDressing.AddCloudDeck(cityRoot, Vector3.zero, SkySize, CloudHeights, Vector3.forward);
        CityDressing.AddHeightFog();
        CityDressing.SetupAtmosphere(mainCam, "SkyAvenue_Post");

        // Police patrolling the loops like traffic (more are dispatched during a pursuit).
        var policeRoot = new GameObject("Police").transform;
        for (int i = 0; i < PoliceCount; i++)
        {
            var lane = lanes[i % lanes.Count];
            CreatePoliceCar($"Police_{i}", policeRoot, lane, lane.Length * (0.1f + 0.2f * i), 0, SpeedMax);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
        EditorSceneManager.SaveScene(scene, ScenePath);
        Selection.activeGameObject = fpc.gameObject;
        Debug.Log($"Sky Avenue built: {lanes.Count} loop lanes x {LaneLevels.Length} levels, {carIndex} traffic cars, " +
                  $"{streetCars} street cars.");
    }

    // Closed flyway through the waypoints with full-loop lanes at every level (default: the sky streams).
    static LanePath MakeLoop(string name, Transform parent, List<Vector3> wps, int baseLayer = BaseLayer,
                             int[] levels = null, bool laneLights = true)
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
        path.baseLayer = baseLayer;
        path.laneLevels = (int[])(levels ?? LaneLevels).Clone();
        path.Rebuild();
        if (laneLights)
        {
            go.AddComponent<LaneLights>().spacing = LightSpacing;
            LaneLightBaker.Bake(path);
        }
        return path;
    }

    // Racetrack around c: straights along Z of length `run` at x = c.x +/- radius, semicircle ends.
    // Clockwise from above: +X side heads -Z, -X side heads +Z. Waypoints every ~WaypointSpacing m.
    static List<Vector3> RacetrackClockwise(Vector3 c, float radius, float run, float y)
    {
        var pts = new List<Vector3>();
        float h = run * 0.5f;
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
