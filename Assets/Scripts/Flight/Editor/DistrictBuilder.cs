using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using static FlightGrayboxBuilder;

// Tools > Build District: one dense, designed city block, 600 x 600 m on the 10 m grid (the main test
// map; Sky Avenue and the others stay as they are).
//   Streets (street level, real intersections with traffic lights):
//     the avenue (north-south through the middle, 4 lanes), two cross streets (east-west, 2 lanes each
//     way), three side streets (1 lane each way). 6 m sidewalks, paved alleys, lamps, crosswalks.
//   Sky traffic: the avenue's two streams (layers 10-14 and 19-23) on two loops (one per direction),
//     and cross-street loops at layers 7-9 and 16-18, so every crossing in the sky is over or under.
//     Sparse street traffic on every street. 350 cars in all.
//   Skywalks at layers 6, 12, 18: rings round the towers (15 m on the tall ones, 8 m otherwise), alleys
//     decked over at some levels, bridges across the side streets, avenue and cross streets.
//   Quadrants: offices (NW, curtain wall / ribbon), apartments (NE, residential, balconies), school
//     (SW, low and wide, courtyard skywalk), market underworld (SE, industrial, street stalls).
//   The hollow tower (open atrium, plaza at layer 12, landing pad, balconies and ledges inside, bridge
//     east), the police station at the avenue / south cross street corner, and the start plaza on a
//     layer 18 skywalk over the avenue with two parked cars.
public static class DistrictBuilder
{
    const string ScenePath = "Assets/Scenes/District.unity";
    const int Seed = 41;
    const float LayerSpacing = 10f;
    const float MapHalf = 300f, BuildHalf = 270f;

    // Sky lanes
    const int AvenueBase = 12;
    static readonly int[] AvenueLevels = { -2, -1, 0, 1, 2, 7, 8, 9, 10, 11 };   // layers 10-14, 19-23
    const int CrossBase = 8;
    static readonly int[] CrossLevels = { -1, 0, 1, 8, 9, 10 };                  // layers 7-9, 16-18
    const float CornerRadius = 20f;   // keeps the inner loops' corners clear of the block corners
    const float WaypointSpacing = 12f;
    const float LightSpacing = 30f;

    // Traffic
    const int TrafficBudget = 350;
    const float StreetCarSpacing = 120f, StreetSpeed = 15f;
    const float SpeedMin = 18f, SpeedMax = 20f;
    const int PoliceCount = 4;

    // Towers
    static readonly int[] SkywalkLayers = { 6, 12, 18 };
    const float CellTarget = 75f;
    const float AlleyMin = 14f, AlleyMax = 20f;
    const float HologramChance = 0.25f;
    const int StartLayer = 18;
    static readonly Color[] WallTints =
    {
        new Color(0.2f, 0.21f, 0.23f), new Color(0.17f, 0.2f, 0.25f), new Color(0.24f, 0.23f, 0.22f),
    };

    enum Quarter { Office, Apartments, School, Market }

    class Cell
    {
        public Rect rect;
        public Rect block;
        public Quarter quarter;
        public CityDressing.Tower tower;
        public bool hollow;
    }

    [MenuItem("Tools/Build District")]
    public static void Build()
    {
        if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
        var mainCam = Camera.main;
        mainCam.farClipPlane = 3000f;

        var ta = new GameObject("TrafficAuthority").AddComponent<TrafficAuthority>();
        ta.layerSpacing = LayerSpacing;
        ta.hoverHeight = 0.5f;
        ta.maxLayer = 60;

        var rng = new System.Random(Seed);
        var decoRng = new System.Random(Seed * 7919 + 1);
        var kit = CityDressing.CreateKit(LayerSpacing, 7, 23);
        var sk = CityDressing.CreateSkywalkKit(kit);
        var walk = new List<CityDressing.WalkRect>();

        var groundMat = GetMaterial("UnderworldGround", new Color(0.05f, 0.05f, 0.06f));
        var platformMat = GetMaterial("Platform", new Color(0.42f, 0.44f, 0.48f));
        var parkedMats = new[] { GetMaterial("ParkedCar", new Color(0.2f, 0.8f, 0.3f)), GetMaterial("ParkedCarRed", new Color(0.85f, 0.15f, 0.15f)) };
        var trafficMats = new[]
        {
            GetMaterial("TrafficCar", new Color(0.95f, 0.55f, 0.1f)),
            GetMaterial("TrafficCarYellow", new Color(0.95f, 0.85f, 0.2f)),
            GetMaterial("TrafficCarTeal", new Color(0.1f, 0.7f, 0.7f)),
            GetMaterial("TrafficCarPurple", new Color(0.55f, 0.3f, 0.8f)),
        };
        var rackMat = GetMaterial("Rack", new Color(0.15f, 0.15f, 0.17f));
        var playerMat = GetMaterial("Player", new Color(0.9f, 0.9f, 0.9f));

        var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
        ground.name = "Ground";
        ground.transform.localScale = new Vector3(600f, 1f, 600f);
        SetMat(ground, groundMat);
        GameObjectUtility.SetStaticEditorFlags(ground, StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccludeeStatic);

        // ---------- street grid ----------
        var streets = new List<CityDressing.StreetDef>
        {
            St("Avenue", true, 0f, 30f, 2),
            St("SideWest", true, -170f, 10f, 1),
            St("SideEast", true, 170f, 10f, 1),
            St("CrossSouth", false, -120f, 20f, 2),
            St("CrossNorth", false, 120f, 20f, 2),
            St("SideNorth", false, 210f, 10f, 1),
        };
        var blocks = Blocks(streets);

        // ---------- cells (tower plots) ----------
        var cells = new List<Cell>();
        var hollowBlock = new Rect(30f, -100f, 130f, 200f);
        Rect hollowFp = Rect.MinMaxRect(40f, -45f, 150f, 45f), atrium = Rect.MinMaxRect(75f, -20f, 115f, 20f);
        foreach (var b in blocks)
        {
            var q = QuarterOf(b);
            if (Approximately(b, hollowBlock))
            {
                cells.Add(new Cell { rect = hollowFp, block = b, quarter = q, hollow = true });
                foreach (var r in Split(Rect.MinMaxRect(b.xMin, b.yMin, b.xMax, -61f), rng)) cells.Add(new Cell { rect = r, block = b, quarter = q });
                foreach (var r in Split(Rect.MinMaxRect(b.xMin, 61f, b.xMax, b.yMax), rng)) cells.Add(new Cell { rect = r, block = b, quarter = q });
                continue;
            }
            foreach (var r in Split(b, rng)) cells.Add(new Cell { rect = r, block = b, quarter = q });
        }

        // The start tower: west of the avenue, the cell on the avenue nearest z = 0.
        Cell startCell = null;
        foreach (var c in cells)
            if (Mathf.Approximately(c.rect.xMax, -30f) && (startCell == null || Mathf.Abs(c.rect.center.y) < Mathf.Abs(startCell.rect.center.y)))
                startCell = c;
        // The station: east of the avenue, south of the south cross street, the cell on the avenue nearest the corner.
        Cell stationCell = null;
        foreach (var c in cells)
            if (Mathf.Approximately(c.rect.xMin, 30f) && c.rect.yMax <= -140f && (stationCell == null || c.rect.yMax > stationCell.rect.yMax))
                stationCell = c;

        // ---------- lanes ----------
        var lanesRoot = new GameObject("Lanes").transform;
        var skyLanes = new List<LanePath>();
        var avenueLanes = new List<LanePath>();
        // Avenue loops: east one runs north up the avenue and back down the east side street; the west
        // one mirrors it. Two concentric rings each (avenue x 6.5 / 13.5, side street 166.5 / 173.5).
        for (int ring = 0; ring < 2; ring++)
        {
            // Ring 0 is the inner rectangle (avenue wall lane, inner side-street lane, nearer the map).
            // Lane x from the car width: the median lane clears the 10 m median, rings are a car + 4 m apart.
            float w = CarSize.x, median = 5f + w * 0.5f, spacing = w + 4f;
            float inner = ring == 0 ? median + spacing : median;
            float side = ring == 0 ? 170f - spacing * 0.5f : 170f + spacing * 0.5f;
            float end = ring == 0 ? 278f : 278f + spacing;
            var east = MakeLoop($"AvenueEast_{ring}", lanesRoot, Rounded(new[]
            {
                new Vector2(inner, -end), new Vector2(inner, end), new Vector2(side, end), new Vector2(side, -end),
            }, CornerRadius, Ride(AvenueBase)), AvenueBase, AvenueLevels, true);
            var west = MakeLoop($"AvenueWest_{ring}", lanesRoot, Rounded(new[]
            {
                new Vector2(-inner, end), new Vector2(-inner, -end), new Vector2(-side, -end), new Vector2(-side, end),
            }, CornerRadius, Ride(AvenueBase)), AvenueBase, AvenueLevels, true);
            skyLanes.Add(east); skyLanes.Add(west);
            avenueLanes.Add(east); avenueLanes.Add(west);
        }
        // Cross-street loops: the north one runs west along the north cross street and back east along
        // the north edge; the south one east along the south cross street and back along the south edge.
        for (int ring = 0; ring < 2; ring++)
        {
            float cs = CarSize.x + 4f; // ring spacing, both rings in the street's north / south half
            float z = ring == 0 ? 120f + 4f + cs : 120f + 4f, edge = ring == 0 ? 285f : 285f + cs, xe = edge;
            skyLanes.Add(MakeLoop($"CrossNorth_{ring}", lanesRoot, Rounded(new[]
            {
                new Vector2(-xe, edge), new Vector2(xe, edge), new Vector2(xe, z), new Vector2(-xe, z),
            }, CornerRadius, Ride(CrossBase)), CrossBase, CrossLevels, true));
            skyLanes.Add(MakeLoop($"CrossSouth_{ring}", lanesRoot, Rounded(new[]
            {
                new Vector2(-xe, -z), new Vector2(xe, -z), new Vector2(xe, -edge), new Vector2(-xe, -edge),
            }, CornerRadius, Ride(CrossBase)), CrossBase, CrossLevels, true));
        }
        // Street loops: one per street, a lane each way, joined by teardrop turns past the map edge.
        var streetLanes = new List<LanePath>();
        foreach (var st in streets)
        {
            // Outer lane on 2-lane streets; 1-lane streets keep opposing cars (CarSize.x wide) clear of each other.
            float off = st.lanesPerDir >= 2 ? CityDressing.StreetLaneWidth * 1.5f : Mathf.Max(CityDressing.StreetLaneWidth * 0.5f, CarSize.x * 0.5f + 0.5f);
            streetLanes.Add(MakeLoop($"Street_{st.name}", lanesRoot, StreetLoop(st, off), 0, new[] { 0 }, false));
        }
        var allLanes = new List<LanePath>(skyLanes);
        allLanes.AddRange(streetLanes);

        // ---------- traffic ----------
        var trafficRoot = new GameObject("Traffic").transform;
        int carIndex = 0, streetCars = 0;
        var streetRng = new System.Random(Seed * 31 + 7);
        foreach (var path in streetLanes)
        {
            int count = Mathf.Max(1, Mathf.RoundToInt(path.Length / StreetCarSpacing));
            float spacing = path.Length / count, phase = (float)streetRng.NextDouble() * spacing;
            for (int i = 0; i < count; i++)
            {
                float start = Mathf.Repeat(phase + i * spacing, path.Length);
                var v = CreateVehicle($"StreetCar_{streetCars++:000}", Pick(trafficMats, streetRng));
                PlaceOnLane(v, trafficRoot, path, start, 0, StreetSpeed);
            }
        }
        int skyBudget = Mathf.Max(0, TrafficBudget - streetCars - PoliceCount);
        float total = 0f;
        foreach (var path in skyLanes) total += path.Length * path.laneLevels.Length;
        foreach (var path in skyLanes)
        {
            foreach (int level in path.laneLevels)
            {
                int count = Mathf.Clamp(Mathf.RoundToInt(skyBudget * path.Length / total), 1, Mathf.Max(1, Mathf.FloorToInt(path.Length / MinSpawnGap)));
                float spacing = path.Length / count, phase = (float)rng.NextDouble() * spacing;
                for (int i = 0; i < count; i++)
                {
                    float start = Mathf.Repeat(phase + i * spacing + ((float)rng.NextDouble() - 0.5f) * spacing * 0.2f, path.Length);
                    var v = CreateVehicle($"Traffic_{carIndex++:000}", Pick(trafficMats, rng));
                    if (carIndex % RackEvery == 0) AddRearRack(v, rackMat);
                    PlaceOnLane(v, trafficRoot, path, start, level, Mathf.Lerp(SpeedMin, SpeedMax, (float)rng.NextDouble()));
                }
            }
        }

        // ---------- towers ----------
        var cityRoot = new GameObject("City").transform;
        var quarterRoots = new Dictionary<Quarter, Transform>();
        foreach (Quarter q in System.Enum.GetValues(typeof(Quarter)))
        {
            var r = new GameObject(q.ToString()).transform;
            r.SetParent(cityRoot, false);
            quarterRoots[q] = r;
        }
        int landmarks = 3;
        Vector3 plazaCenter = Vector3.zero, hollowPad = Vector3.zero;
        for (int i = 0; i < cells.Count; i++)
        {
            var c = cells[i];
            var parent = quarterRoots[c.quarter];
            var tint = Pick(WallTints, rng);
            if (c.hollow)
            {
                c.tower = CityDressing.BuildHollowTower(parent, "HollowTower", c.rect, atrium, 12, 16, 32, Vector3.left, tint,
                                                         CityDressing.StyleGrid, decoRng, kit, out plazaCenter, out hollowPad);
                continue;
            }
            int layers; int style; CityDressing.Archetype? arch = null;
            switch (c.quarter)
            {
                case Quarter.School:
                    layers = 6 + rng.Next(5); style = CityDressing.StyleGrid; arch = CityDressing.Archetype.Slab; break;
                case Quarter.Market:
                    layers = 15 + rng.Next(11); style = CityDressing.StyleIndustrial; break;
                case Quarter.Apartments:
                    layers = 15 + rng.Next(16); style = CityDressing.StyleResidential; break;
                default:
                    layers = 20 + rng.Next(21); style = rng.NextDouble() < 0.55 ? CityDressing.StyleCurtain : CityDressing.StyleRibbon; break;
            }
            if (c.quarter != Quarter.School && landmarks > 0 && rng.NextDouble() < 0.12) { layers = 45 + rng.Next(6); landmarks--; }
            if (c == startCell) { layers = Mathf.Max(layers, 32); arch = CityDressing.Archetype.Slab; }
            if (c == stationCell) layers = Mathf.Max(layers, 15);

            Vector3 normal = FaceNormal(c, streets);
            bool alongX = Mathf.Abs(normal.x) > 0.5f;
            var footprint = alongX ? new Vector2(c.rect.width, c.rect.height) : new Vector2(c.rect.height, c.rect.width);
            c.tower = CityDressing.BuildTower(parent, $"Tower_{i:00}", new Vector3(c.rect.center.x, 0f, c.rect.center.y), footprint, layers,
                                              normal, tint, rng, kit, arch, allowRecess: c != startCell, style: style);
            c.tower.tall = layers >= 45;
        }

        // ---------- streets, signals, station ----------
        kit.clearance = new CityDressing.Clearance(allLanes);
        var crossings = CityDressing.BuildDistrictStreets(cityRoot, streets, blocks, decoRng, kit, walk);
        var signalsRoot = new GameObject("TrafficSignals").transform;
        signalsRoot.SetParent(cityRoot, false);
        var signals = new List<TrafficSignal>();
        foreach (var x in crossings) signals.Add(CityDressing.BuildSignal(signalsRoot, x, (float)decoRng.NextDouble() * 48f, kit));
        if (stationCell != null)
        {
            float z = Mathf.Clamp(stationCell.rect.yMax - 20f, stationCell.rect.yMin + 6f, stationCell.rect.yMax - 6f);
            CityDressing.BuildPoliceStationAt(cityRoot, new Vector3(30f, 0f, z), Vector3.left, new Vector3(16f, 0f, z), kit);
        }

        // ---------- start plaza (layer 18, over the avenue in front of the start tower) ----------
        float startY = StartLayer * LayerSpacing;
        float sz = startCell != null ? startCell.rect.center.y : 0f;
        var startRoot = new GameObject("StartPlaza").transform;
        var plazaRect = Rect.MinMaxRect(-30f, sz - 20f, -15.5f, sz + 20f);
        Slab(startRoot, "StartPlazaDeck", new Vector3(plazaRect.center.x, startY - 0.6f, plazaRect.center.y), new Vector3(plazaRect.width, 1.2f, plazaRect.height), platformMat);
        sk.decks.Add(new Bounds(new Vector3(plazaRect.center.x, startY - 0.6f, plazaRect.center.y), new Vector3(plazaRect.width, 1.2f, plazaRect.height)));
        walk.Add(new CityDressing.WalkRect { area = new Bounds(new Vector3(plazaRect.center.x, startY, plazaRect.center.y), new Vector3(plazaRect.width, 0f, plazaRect.height)), skywalk = true });
        for (int s = -1; s <= 1; s += 2)
            Slab(startRoot, "StartPlazaRail", new Vector3(plazaRect.center.x, startY + 0.55f, sz + s * 19.9f), new Vector3(plazaRect.width, 1.1f, 0.2f), platformMat);
        Slab(startRoot, "StartPlazaRailEdge", new Vector3(-15.6f, startY + 0.55f, sz), new Vector3(0.2f, 1.1f, 40f), platformMat);
        Slab(startRoot, "Doorway", new Vector3(-30.3f, startY + 4f, sz), new Vector3(1f, 8f, 6f), GetMaterial("Prop", new Color(0.75f, 0.6f, 0.3f)));
        kit.keepOut.Add(new Bounds(new Vector3(plazaRect.center.x, startY, sz), new Vector3(plazaRect.width + 6f, 30f, 46f)));
        for (int i = 0; i < 2; i++)
        {
            var parked = CreateVehicle($"ParkedCar_{i}", parkedMats[i]);
            parked.startParkedOnSurface = true;
            parked.hasDriver = false;
            parked.transform.SetParent(startRoot, true);
            parked.transform.SetPositionAndRotation(new Vector3(-21f, startY + CarRootAboveUnderside, sz + (i == 0 ? -9f : 9f)),
                                                    Quaternion.LookRotation(Vector3.forward));
        }
        var fpc = CreatePlayer(new Vector3(-28f, startY, sz), Quaternion.LookRotation(Vector3.right), mainCam, playerMat);

        // ---------- look pass ----------
        var towers = new List<CityDressing.Tower>();
        foreach (var c in cells) towers.Add(c.tower);
        foreach (var t in towers)
        {
            if (t.root.name != "HollowTower")
            {
                CityDressing.AddBays(t, decoRng, kit);
                CityDressing.AddBalconies(t, decoRng, kit);
            }
            CityDressing.AddTwinBridges(t, decoRng, kit);
        }
        foreach (var c in cells)
            if (c.quarter == Quarter.Apartments && !c.hollow) CityDressing.AddBalconies(c.tower, decoRng, kit); // denser balconies
        foreach (var t in towers)
        {
            foreach (var b in t.masses) { sk.solids.Add(b); sk.masses.Add(b); }
            foreach (var b in t.features) sk.solids.Add(b);
        }

        // Skywalk rings (alleys decked over at some levels) and bridges.
        var skyRoot = new GameObject("Skywalks").transform;
        skyRoot.SetParent(cityRoot, false);
        var alleyCover = new Dictionary<(Cell, Cell, int), bool>();
        foreach (var c in cells)
        {
            foreach (int layer in SkywalkLayers)
            {
                if (c.tower.heightLayers < layer + 2) continue;
                float main = c.tower.heightLayers >= 30 || c.quarter == Quarter.School ? 15f : 8f;
                var widths = new float[4];
                for (int side = 0; side < 4; side++)
                {
                    var n = Neighbour(c, cells, side, out float gap);
                    if (n == null) { widths[side] = main; continue; }
                    // An alley: deck it over (both towers meet halfway) or leave it open.
                    var key = Key(c, n, layer);
                    if (!alleyCover.TryGetValue(key, out bool cover))
                    {
                        cover = c.quarter == Quarter.School || decoRng.NextDouble() < 0.45;
                        alleyCover[key] = cover;
                    }
                    widths[side] = cover && n.tower.heightLayers >= layer + 2 ? gap * 0.5f : 0f;
                }
                CityDressing.AddSkywalkRing(c.tower, skyRoot, layer, widths, kit, sk, walk, decoRng);
            }
        }
        int bridges = AddStreetBridges(skyRoot, cells, streets, kit, sk, walk, decoRng);
        // The hollow tower's bridge east over the side street, from its plaza level.
        CityDressing.AddSkywalkBridge(skyRoot, Rect.MinMaxRect(150f, -10f, 180f, 10f), 12 * LayerSpacing, true, kit, sk, walk);

        // Decoration, ledges, holograms; the market's stalls.
        foreach (var t in towers)
        {
            CityDressing.DressTower(t, decoRng, kit);
            CityDressing.AddLedges(t, decoRng, kit);
            if (decoRng.NextDouble() < HologramChance) CityDressing.AddHologram(t, decoRng, kit);
        }
        foreach (var b in blocks)
            if (QuarterOf(b) == Quarter.Market) CityDressing.AddMarketStalls(cityRoot, b, decoRng, kit);

        // ---------- pedestrians ----------
        var graph = DistrictWalkGraph.Build(cityRoot, walk, sk.masses, sk.taxiPads, signals);
        var peds = new GameObject("Pedestrians").AddComponent<PedestrianSystem>();
        peds.bodyMaterial = GetMaterial("PedestrianBody", new Color(0.32f, 0.3f, 0.34f));
        peds.visorMaterial = GetUnlitMaterial("PedestrianVisor", new Color(0.4f, 0.8f, 1f), 1.6f);
        int entrances = 0, pads = 0;
        foreach (var k in graph.kinds) { if (k == WalkGraph.Kind.Entrance) entrances++; else if (k == WalkGraph.Kind.TaxiPad) pads++; }

        CityDressing.AddUnderworldHaze(cityRoot, Vector3.zero, 2400f, new[] { 15f, 35f }, kit);
        CityDressing.AddCloudDeck(cityRoot, Vector3.zero, 6000f, new[] { 330f, 360f }, Vector3.forward);
        CityDressing.AddHeightFog();
        CityDressing.SetupAtmosphere(mainCam, "District_Post");

        // ---------- police ----------
        var policeRoot = new GameObject("Police").transform;
        for (int i = 0; i < PoliceCount; i++)
        {
            var lane = skyLanes[(i * 3) % skyLanes.Count];
            CreatePoliceCar($"Police_{i}", policeRoot, lane, lane.Length * (0.15f + 0.2f * i), 0, SpeedMax);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
        EditorSceneManager.SaveScene(scene, ScenePath);

        // Occlusion culling bake (tower masses, skywalks and bridges are occluders; decoration, streets
        // and cars only occludees). Re-baked on every rebuild.
        StaticOcclusionCulling.smallestOccluder = 5f;
        StaticOcclusionCulling.smallestHole = 0.5f;
        StaticOcclusionCulling.backfaceThreshold = 100f;
        var bakeStart = System.DateTime.Now;
        bool baked = StaticOcclusionCulling.Compute();
        Debug.Log($"District occlusion bake {(baked ? "done" : "FAILED")} in {(System.DateTime.Now - bakeStart).TotalSeconds:0} s");
        EditorSceneManager.SaveScene(scene, ScenePath);
        Selection.activeGameObject = fpc.gameObject;
        Debug.Log($"District built: {cells.Count} towers, {skyLanes.Count} sky loops, {streetLanes.Count} street loops, " +
                  $"{carIndex} sky cars + {streetCars} street cars + {PoliceCount} police, {crossings.Count} signals, " +
                  $"{sk.decks.Count} skywalk decks ({bridges} street bridges), {walk.Count} walk areas, " +
                  $"walk graph {graph.Count} nodes ({entrances} entrances, {pads} taxi pads).");
    }

    // ---------- layout helpers ----------

    static CityDressing.StreetDef St(string name, bool ns, float center, float halfWidth, int lanes) =>
        new CityDressing.StreetDef { name = name, northSouth = ns, center = center, halfWidth = halfWidth, min = -MapHalf, max = MapHalf, lanesPerDir = lanes };

    // Blocks: the gaps between the streets, inside BuildHalf.
    static List<Rect> Blocks(List<CityDressing.StreetDef> streets)
    {
        List<Vector2> Gaps(bool ns)
        {
            var spans = new List<Vector2>();
            foreach (var s in streets) if (s.northSouth == ns) spans.Add(new Vector2(s.center - s.halfWidth, s.center + s.halfWidth));
            spans.Sort((a, b) => a.x.CompareTo(b.x));
            var gaps = new List<Vector2>();
            float from = -BuildHalf;
            foreach (var sp in spans) { if (sp.x - from > 20f) gaps.Add(new Vector2(from, sp.x)); from = sp.y; }
            if (BuildHalf - from > 20f) gaps.Add(new Vector2(from, BuildHalf));
            return gaps;
        }
        var blocks = new List<Rect>();
        foreach (var gx in Gaps(true))
            foreach (var gz in Gaps(false))
                blocks.Add(Rect.MinMaxRect(gx.x, gz.x, gx.y, gz.y));
        return blocks;
    }

    static bool Approximately(Rect a, Rect b) => (a.min - b.min).sqrMagnitude < 1f && (a.max - b.max).sqrMagnitude < 1f;

    static Quarter QuarterOf(Rect b)
    {
        bool east = b.center.x > 0f, south = b.center.y < -50f;
        if (south) return east ? Quarter.Market : Quarter.School;
        return east ? Quarter.Apartments : Quarter.Office;
    }

    // Split a block into tower plots about CellTarget m across, with alleys between.
    static List<Rect> Split(Rect b, System.Random rng)
    {
        var xs = Cuts(b.xMin, b.xMax, rng);
        var zs = Cuts(b.yMin, b.yMax, rng);
        var cells = new List<Rect>();
        foreach (var x in xs) foreach (var z in zs) cells.Add(Rect.MinMaxRect(x.x, z.x, x.y, z.y));
        return cells;
    }

    static List<Vector2> Cuts(float a, float b, System.Random rng)
    {
        float len = b - a;
        int n = Mathf.Max(1, Mathf.RoundToInt(len / CellTarget));
        var parts = new List<Vector2>();
        if (n == 1) { parts.Add(new Vector2(a, b)); return parts; }
        float alley = Mathf.Lerp(AlleyMin, AlleyMax, (float)rng.NextDouble());
        float each = (len - alley * (n - 1)) / n;
        float p = a;
        for (int i = 0; i < n; i++)
        {
            float w = i == n - 1 ? b - p : Mathf.Round(each * Mathf.Lerp(0.85f, 1.15f, (float)rng.NextDouble()));
            parts.Add(new Vector2(p, p + w));
            p += w + alley;
        }
        return parts;
    }

    // The face toward the widest street the plot touches (the "canyon" face of the archetypes).
    static Vector3 FaceNormal(Cell c, List<CityDressing.StreetDef> streets)
    {
        Vector3 best = Vector3.left; float bestW = -1f;
        void Try(bool touches, Vector3 n, float edge, bool ns)
        {
            if (!touches) return;
            foreach (var s in streets)
            {
                if (s.northSouth != ns) continue;
                if (Mathf.Abs(Mathf.Abs(s.center - edge) - s.halfWidth) > 1f) continue;
                if (s.halfWidth > bestW) { bestW = s.halfWidth; best = n; }
            }
        }
        Try(Mathf.Approximately(c.rect.xMin, c.block.xMin), Vector3.left, c.rect.xMin, true);
        Try(Mathf.Approximately(c.rect.xMax, c.block.xMax), Vector3.right, c.rect.xMax, true);
        Try(Mathf.Approximately(c.rect.yMin, c.block.yMin), Vector3.back, c.rect.yMin, false);
        Try(Mathf.Approximately(c.rect.yMax, c.block.yMax), Vector3.forward, c.rect.yMax, false);
        return best;
    }

    // The plot across an alley on `side` (0 south, 1 north, 2 west, 3 east), within the same block.
    static Cell Neighbour(Cell c, List<Cell> cells, int side, out float gap)
    {
        gap = 0f;
        Cell best = null; float bestGap = 30f;
        foreach (var o in cells)
        {
            if (o == c || o.block != c.block) continue;
            float g;
            bool overlap;
            switch (side)
            {
                case 0: g = c.rect.yMin - o.rect.yMax; overlap = o.rect.xMax > c.rect.xMin + 5f && o.rect.xMin < c.rect.xMax - 5f; break;
                case 1: g = o.rect.yMin - c.rect.yMax; overlap = o.rect.xMax > c.rect.xMin + 5f && o.rect.xMin < c.rect.xMax - 5f; break;
                case 2: g = c.rect.xMin - o.rect.xMax; overlap = o.rect.yMax > c.rect.yMin + 5f && o.rect.yMin < c.rect.yMax - 5f; break;
                default: g = o.rect.xMin - c.rect.xMax; overlap = o.rect.yMax > c.rect.yMin + 5f && o.rect.yMin < c.rect.yMax - 5f; break;
            }
            if (!overlap || g < 0f || g >= bestGap) continue;
            bestGap = g; best = o;
        }
        gap = bestGap;
        return best;
    }

    static (Cell, Cell, int) Key(Cell a, Cell b, int layer) =>
        a.GetHashCode() < b.GetHashCode() ? (a, b, layer) : (b, a, layer);

    // Bridges 15-25 m wide across streets between towers facing each other: side streets at any skywalk
    // layer, the avenue and cross streets at layer 6 (under the sky lanes). Clearance checks reject any
    // that would cut a lane.
    static int AddStreetBridges(Transform parent, List<Cell> cells, List<CityDressing.StreetDef> streets, CityDressing.Kit kit,
                                CityDressing.SkywalkKit sk, List<CityDressing.WalkRect> walk, System.Random rng)
    {
        int n = 0;
        foreach (var st in streets)
        {
            int[] layers = st.lanesPerDir == 1 ? SkywalkLayers : new[] { 6 };
            float a = st.center - st.halfWidth, b = st.center + st.halfWidth;
            foreach (var c in cells)
            {
                // c on the low side of the street, facing it.
                if (st.northSouth ? !Mathf.Approximately(c.rect.xMax, a) : !Mathf.Approximately(c.rect.yMax, a)) continue;
                foreach (var o in cells)
                {
                    if (st.northSouth ? !Mathf.Approximately(o.rect.xMin, b) : !Mathf.Approximately(o.rect.yMin, b)) continue;
                    float lo = st.northSouth ? Mathf.Max(c.rect.yMin, o.rect.yMin) : Mathf.Max(c.rect.xMin, o.rect.xMin);
                    float hi = st.northSouth ? Mathf.Min(c.rect.yMax, o.rect.yMax) : Mathf.Min(c.rect.xMax, o.rect.xMax);
                    if (hi - lo < 20f) continue;
                    foreach (int layer in layers)
                    {
                        if (c.tower.heightLayers < layer + 2 || o.tower.heightLayers < layer + 2) continue;
                        if (rng.NextDouble() > (st.lanesPerDir == 1 ? 0.35 : 0.5)) continue;
                        float w = Mathf.Min(hi - lo - 4f, Mathf.Lerp(15f, 25f, (float)rng.NextDouble()));
                        float mid = Mathf.Lerp(lo + w * 0.5f + 2f, hi - w * 0.5f - 2f, (float)rng.NextDouble());
                        var r = st.northSouth ? Rect.MinMaxRect(a, mid - w * 0.5f, b, mid + w * 0.5f) : Rect.MinMaxRect(mid - w * 0.5f, a, mid + w * 0.5f, b);
                        if (CityDressing.AddSkywalkBridge(parent, r, layer * LayerSpacing, st.northSouth, kit, sk, walk)) n++;
                    }
                }
            }
        }
        return n;
    }

    // ---------- lanes ----------

    static float Ride(int layer) => layer * LayerSpacing + 0.5f;

    static LanePath MakeLoop(string name, Transform parent, List<Vector3> wps, int baseLayer, int[] levels, bool laneLights)
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
        path.laneLevels = (int[])levels.Clone();
        path.Rebuild();
        if (laneLights)
        {
            go.AddComponent<LaneLights>().spacing = LightSpacing;
            LaneLightBaker.Bake(path);
        }
        return path;
    }

    // Closed polygon with every corner filleted (quadratic curve, radius capped by half the edges),
    // waypoints about every WaypointSpacing m.
    static List<Vector3> Rounded(Vector2[] corners, float radius, float y)
    {
        var pts = new List<Vector3>();
        int n = corners.Length;
        for (int i = 0; i < n; i++)
        {
            Vector2 prev = corners[(i + n - 1) % n], c = corners[i], next = corners[(i + 1) % n];
            Vector2 dIn = (c - prev).normalized, dOut = (next - c).normalized;
            float r = Mathf.Min(radius, (c - prev).magnitude * 0.45f, (next - c).magnitude * 0.45f);
            Vector2 a = c - dIn * r, b = c + dOut * r;
            int steps = Mathf.Max(3, Mathf.CeilToInt(r * 1.6f / WaypointSpacing) + 2);
            for (int k = 0; k <= steps; k++)
            {
                float t = k / (float)steps;
                Vector2 p = (1 - t) * (1 - t) * a + 2 * (1 - t) * t * c + t * t * b;
                pts.Add(new Vector3(p.x, y, p.y));
            }
            // Straight run to the next corner's curve.
            Vector2 nn = corners[(i + 2) % n];
            float rNext = Mathf.Min(radius, (next - c).magnitude * 0.45f, (nn - next).magnitude * 0.45f);
            Vector2 s0 = b, s1 = next - dOut * rNext;
            float len = (s1 - s0).magnitude;
            int segs = Mathf.FloorToInt(len / WaypointSpacing);
            for (int k = 1; k < segs; k++)
            {
                Vector2 p = Vector2.Lerp(s0, s1, k / (float)segs);
                pts.Add(new Vector3(p.x, y, p.y));
            }
        }
        return pts;
    }

    // A lane each way along the street (right-hand traffic: northbound / eastbound on the east / south
    // side), joined past the map edge by teardrop turns wide enough to take at street speed.
    static List<Vector3> StreetLoop(CityDressing.StreetDef st, float off)
    {
        float e = MapHalf, c = st.center;
        float y = Ride(0);
        Vector2[] poly;
        if (st.northSouth)
            poly = new[]
            {
                new Vector2(c + off, -e), new Vector2(c + off, e), new Vector2(c + 30f, e + 40f), new Vector2(c - 30f, e + 40f),
                new Vector2(c - off, e), new Vector2(c - off, -e), new Vector2(c - 30f, -e - 40f), new Vector2(c + 30f, -e - 40f),
            };
        else
            poly = new[]
            {
                new Vector2(-e, c - off), new Vector2(e, c - off), new Vector2(e + 40f, c - 30f), new Vector2(e + 40f, c + 30f),
                new Vector2(e, c + off), new Vector2(-e, c + off), new Vector2(-e - 40f, c + 30f), new Vector2(-e - 40f, c - 30f),
            };
        return Rounded(poly, 20f, y);
    }

    static void PlaceOnLane(FlyingVehicle v, Transform parent, LanePath path, float start, int level, float speed)
    {
        v.transform.SetParent(parent, false);
        v.path = path;
        v.startDistance = start;
        v.startLevel = level;
        v.aiCruiseSpeed = speed;
        path.Sample(start, out var p, out var f);
        float w = path.LaneWeight(level, start, out var off);
        v.transform.SetPositionAndRotation(path.ToWorld(p, f, off * w) + Vector3.up * CarRootAboveUnderside, Quaternion.LookRotation(f));
    }
}
