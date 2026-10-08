using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

// Tools > Build Flight Graybox: a dense city-grid chase scene.
// 6x6 blocks of towers, lanes running down avenues (N-S) and streets (E-W) so they cross,
// each lane doubled into two parallel rings, ~80 traffic cars, and a large launch
// platform cantilevered off a tower at the city's south-west corner.
public static class FlightGrayboxBuilder
{
    const string ScenePath = "Assets/Scenes/FlightGraybox.unity";
    const string MaterialFolder = "Assets/Graybox/Materials";

    const int Blocks = 6;
    const float Pitch = 140f;            // distance between avenue centerlines
    const float Half = Blocks * Pitch * 0.5f;
    const int LaneBaseLayer = 8;         // flyway middle lanes ride at layer 8 (40.5 m); side lanes at 4 / 12
    const int LaneLayerOffset = 4;
    const int PoliceLayer = 10;
    internal const float CarRootAboveUnderside = 0.75f; // car body is 1.5 m tall, centred on the root
    const float ParallelGap = 8f;        // spacing between parallel lanes
    const float CornerRadius = 30f;
    const float LoopOverhang = 60f;      // loops turn around just outside the city
    const int CarsPerLane = 10;
    const float PlatformTop = 30f;
    internal const int RackEvery = 3;     // every Nth traffic car gets a rear rack to catch

    static float Avenue(int k) => -Half + k * Pitch;                // lane corridors
    static float BlockCenter(int i) => -Half + Pitch * 0.5f + i * Pitch;

    [MenuItem("Tools/Build Flight Graybox")]
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
        var parkedMats = new[]
        {
            GetMaterial("ParkedCar", new Color(0.2f, 0.8f, 0.3f)),
            GetMaterial("ParkedCarRed", new Color(0.85f, 0.15f, 0.15f)),
            GetMaterial("ParkedCarWhite", new Color(0.92f, 0.92f, 0.95f)),
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

        var rng = new System.Random(7);

        // Ground
        var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
        ground.name = "Ground";
        ground.transform.localScale = new Vector3(140f, 1f, 140f); // 1.4 km square
        SetMat(ground, groundMat);

        // City blocks. Each block is 90 m of buildable footprint between 50 m corridors.
        var cityRoot = new GameObject("City").transform;
        Transform startTower = null;
        for (int bx = 0; bx < Blocks; bx++)
        for (int bz = 0; bz < Blocks; bz++)
        {
            var c = new Vector2(BlockCenter(bx), BlockCenter(bz));
            var block = new GameObject($"Block_{bx}_{bz}").transform;
            block.SetParent(cityRoot, false);

            if (bx == 0 && bz == 0)
            {
                startTower = Box(block, "StartTower", new Vector3(c.x, 0f, c.y), new Vector3(60f, 170f, 60f), towerMats[0]);
                continue;
            }

            int kind = rng.Next(3);
            if (kind == 0)
            {
                float w = 55f + (float)rng.NextDouble() * 25f;
                Box(block, "Tower", new Vector3(c.x, 0f, c.y), new Vector3(w, RandomHeight(rng), w), Pick(towerMats, rng));
            }
            else if (kind == 1)
            {
                for (int s = -1; s <= 1; s += 2)
                    Box(block, "Tower", new Vector3(c.x + s * 22.5f, 0f, c.y), new Vector3(38f, RandomHeight(rng), 78f), Pick(towerMats, rng));
            }
            else
            {
                for (int sx = -1; sx <= 1; sx += 2)
                for (int sz = -1; sz <= 1; sz += 2)
                    Box(block, "Tower", new Vector3(c.x + sx * 22.5f, 0f, c.y + sz * 22.5f), new Vector3(36f, RandomHeight(rng), 36f), Pick(towerMats, rng));
            }
        }

        new GameObject("TrafficAuthority").AddComponent<TrafficAuthority>();

        // Lanes: loops up one corridor and back down the next. N-S loops cross E-W loops at every
        // corridor intersection. Each loop gets an inner and an outer ring = two parallel lanes.
        var lanesRoot = new GameObject("Lanes").transform;
        var lanes = new List<LanePath>();
        int[][] pairs = { new[] { 1, 2 }, new[] { 4, 5 } };
        float lo = -Half - LoopOverhang, hi = Half + LoopOverhang;
        foreach (var pr in pairs)
        {
            float a = Avenue(pr[0]), b = Avenue(pr[1]);
            for (int ring = 0; ring < 2; ring++)
            {
                float g = ring * ParallelGap;
                // North up corridor a, south down corridor b
                lanes.Add(MakeLoop($"Lane_NS_{pr[0]}{pr[1]}_{ring}", lanesRoot, new[]
                {
                    new Vector2(a - g, lo - g), new Vector2(a - g, hi + g),
                    new Vector2(b + g, hi + g), new Vector2(b + g, lo - g),
                }));
                // East along street a, west along street b
                lanes.Add(MakeLoop($"Lane_EW_{pr[0]}{pr[1]}_{ring}", lanesRoot, new[]
                {
                    new Vector2(lo - g, a - g), new Vector2(hi + g, a - g),
                    new Vector2(hi + g, b + g), new Vector2(lo - g, b + g),
                }));
            }
        }

        // Traffic
        var trafficRoot = new GameObject("Traffic").transform;
        int carIndex = 0;
        foreach (var path in lanes)
        {
            float L = path.Length;
            for (int i = 0; i < CarsPerLane; i++)
            {
                float start = (i + (float)rng.NextDouble() * 0.6f) / CarsPerLane * L;
                int level = path.LevelOf((LaneLayer)rng.Next(3));
                if (level != 0 && path.LaneWeight(level, start, out _) < 0.9f) level = 0;
                float speed = 33f + (float)rng.NextDouble() * 4f; // around the 35 limit

                var v = CreateVehicle($"Traffic_{carIndex++:000}", Pick(trafficMats, rng));
                if (carIndex % RackEvery == 0) AddRearRack(v, rackMat); // index-based so the RNG sequence is untouched
                v.transform.SetParent(trafficRoot, false);
                v.path = path;
                v.startDistance = start;
                v.startLevel = level;
                v.aiCruiseSpeed = speed;

                path.Sample(start, out var p, out var f);
                float w = path.LaneWeight(level, start, out var off);
                v.transform.SetPositionAndRotation(path.ToWorld(p, f, off * w) + Vector3.up * CarRootAboveUnderside,
                                                   Quaternion.LookRotation(f));
            }
        }

        // Launch platform cantilevered off the start tower's west face, over the empty edge corridor.
        var platformRoot = new GameObject("LaunchPlatform").transform;
        Vector3 tc = startTower.position;
        float faceX = tc.x - 30f;
        var pc = new Vector3(faceX - 30f, PlatformTop - 1.5f, tc.z); // 60 x 80 m deck
        Slab(platformRoot, "Deck", pc, new Vector3(60f, 3f, 80f), platformMat);
        Slab(platformRoot, "RailNorth", new Vector3(pc.x, PlatformTop + 0.6f, pc.z + 39.5f), new Vector3(60f, 1.2f, 1f), platformMat);
        Slab(platformRoot, "RailSouth", new Vector3(pc.x, PlatformTop + 0.6f, pc.z - 39.5f), new Vector3(60f, 1.2f, 1f), platformMat);
        Slab(platformRoot, "BeamA", new Vector3(pc.x + 5f, PlatformTop - 6f, pc.z + 25f), new Vector3(50f, 6f, 3f), platformMat);
        Slab(platformRoot, "BeamB", new Vector3(pc.x + 5f, PlatformTop - 6f, pc.z - 25f), new Vector3(50f, 6f, 3f), platformMat);
        Slab(platformRoot, "Doorway", new Vector3(faceX - 0.5f, PlatformTop + 5f, tc.z), new Vector3(1f, 10f, 14f), propMat);
        for (int i = 0; i < 5; i++)
        {
            float cx = faceX - 8f - (float)rng.NextDouble() * 12f;
            float cz = tc.z - 30f + (float)rng.NextDouble() * 60f;
            Slab(platformRoot, $"Crate_{i}", new Vector3(cx, PlatformTop + 2.5f, cz), new Vector3(2f, 5f, 2f), propMat); // top on the grid
        }

        // Player at the doorway, facing out over the edge
        var player = CreatePlayer(new Vector3(faceX - 4f, PlatformTop, tc.z), Quaternion.LookRotation(Vector3.left),
                                  mainCam, playerMat).gameObject;

        // Three parked cars near the platform edge, noses pointing out
        for (int i = 0; i < 3; i++)
        {
            var parked = CreateVehicle($"ParkedCar_{i}", parkedMats[i]);
            parked.startParkedOnSurface = true;
            parked.transform.SetParent(platformRoot, true);
            parked.transform.SetPositionAndRotation(new Vector3(pc.x - 12f, PlatformTop + 0.75f, tc.z - 20f + i * 20f),
                                                   Quaternion.LookRotation(Vector3.left));
        }

        // Police at a handful of corridor intersections
        var policeRoot = new GameObject("Police").transform;
        int[,] posts = { { 1, 1 }, { 2, 4 }, { 4, 2 }, { 5, 5 }, { 1, 5 }, { 5, 1 } };
        for (int i = 0; i < posts.GetLength(0); i++)
        {
            var cop = Slab(policeRoot, $"Police_{i}",
                new Vector3(Avenue(posts[i, 0]) + 15f, TrafficAuthority.RideHeight(PoliceLayer) + CarRootAboveUnderside, Avenue(posts[i, 1]) + 15f),
                new Vector3(3f, 1.5f, 5f), policeMat);
            AddKinematicBody(cop.gameObject);
            cop.gameObject.AddComponent<PoliceUnit>();
        }

        Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
        EditorSceneManager.SaveScene(scene, ScenePath);
        Selection.activeGameObject = player;
        Debug.Log($"Flight graybox built: {lanes.Count} lanes, {carIndex} traffic cars.");
    }

    // ---------- lanes ----------

    // Rounded-rectangle loop through 4 corners in driving order. First side = first long run.
    static LanePath MakeLoop(string name, Transform parent, Vector2[] c)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var wps = new List<Vector3>();
        for (int i = 0; i < 4; i++)
        {
            Vector2 p = c[i], q = c[(i + 1) % 4];
            Vector2 d = (q - p).normalized;
            wps.Add(To3(p + d * CornerRadius));
            wps.Add(To3(q - d * CornerRadius));
        }
        for (int i = 0; i < wps.Count; i++)
        {
            var wp = new GameObject($"WP_{i}").transform;
            wp.SetParent(go.transform, false);
            wp.position = wps[i];
        }

        var path = go.AddComponent<LanePath>();
        path.closedLoop = true;
        path.baseLayer = LaneBaseLayer;
        path.laneLayerOffset = LaneLayerOffset;
        path.Rebuild();

        // Distances where the two long sides start and end.
        float s1a = 0f, s1b = DistanceAt(path, wps[1]);
        float s3a = DistanceAt(path, wps[4]), s3b = DistanceAt(path, wps[5]);
        AddSide(path, LaneLayer.Upper, s1a, s1b, 0.10f, 0.55f);
        AddSide(path, LaneLayer.Lower, s1a, s1b, 0.45f, 0.90f);
        AddSide(path, LaneLayer.Lower, s3a, s3b, 0.10f, 0.55f);
        AddSide(path, LaneLayer.Upper, s3a, s3b, 0.45f, 0.90f);
        path.noSwitchZones.Add(new NoSwitchZone
        {
            startDistance = Mathf.Lerp(s3a, s3b, 0.30f),
            endDistance = Mathf.Lerp(s3a, s3b, 0.40f),
        });

        go.AddComponent<LaneLights>();
        return path;
    }

    static void AddSide(LanePath path, LaneLayer layer, float from, float to, float t0, float t1)
    {
        path.sideLanes.Add(new SideLaneSegment
        {
            layer = layer,
            startDistance = Mathf.Lerp(from, to, t0),
            endDistance = Mathf.Lerp(from, to, t1),
            blendLength = 40f,
        });
    }

    static float DistanceAt(LanePath path, Vector3 p)
    {
        path.FindNearest(p, 0, out float d, out _);
        return d;
    }

    static Vector3 To3(Vector2 v) => new Vector3(v.x, TrafficAuthority.RideHeight(LaneBaseLayer), v.y);

    // ---------- player ----------

    // CharacterController + FirstPersonController, Head at 1.6 m with the camera under it.
    // Visible body: 1.8 m capsule plus a visor block showing facing (hidden in first person).
    // On the Player layer so traffic and the camera ignore it.
    internal static FirstPersonController CreatePlayer(Vector3 pos, Quaternion rot, Camera cam, Material mat)
    {
        var player = new GameObject("Player");
        player.transform.SetPositionAndRotation(pos, rot);
        var cc = player.AddComponent<CharacterController>();
        cc.height = 2f; cc.radius = 0.5f; cc.center = new Vector3(0f, 1f, 0f);

        var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        body.name = "Body";
        Object.DestroyImmediate(body.GetComponent<Collider>());
        body.transform.SetParent(player.transform, false);
        body.transform.localPosition = new Vector3(0f, 0.9f, 0f);
        body.transform.localScale = new Vector3(1f, 0.9f, 1f); // 1.8 m tall
        body.GetComponent<Renderer>().shadowCastingMode = ShadowCastingMode.ShadowsOnly;
        SetMat(body, mat);

        var visor = GameObject.CreatePrimitive(PrimitiveType.Cube);
        visor.name = "Visor";
        Object.DestroyImmediate(visor.GetComponent<Collider>());
        visor.transform.SetParent(player.transform, false);
        visor.transform.localPosition = new Vector3(0f, 1.55f, 0.45f);
        visor.transform.localScale = new Vector3(0.6f, 0.15f, 0.15f);
        visor.GetComponent<Renderer>().shadowCastingMode = ShadowCastingMode.ShadowsOnly;
        SetMat(visor, GetMaterial("Visor", new Color(0.1f, 0.12f, 0.15f)));

        // Boot thrusters: small emissive blocks under the feet, shown only while thrusting.
        var thrusterMat = GetMaterial("Thruster", new Color(0.3f, 0.9f, 1f));
        thrusterMat.EnableKeyword("_EMISSION");
        thrusterMat.SetColor("_EmissionColor", new Color(0.3f, 0.9f, 1f) * 4f);
        var thrusters = new Renderer[2];
        for (int i = 0; i < 2; i++)
        {
            var t = GameObject.CreatePrimitive(PrimitiveType.Cube);
            t.name = i == 0 ? "ThrusterL" : "ThrusterR";
            Object.DestroyImmediate(t.GetComponent<Collider>());
            t.transform.SetParent(player.transform, false);
            t.transform.localPosition = new Vector3(i == 0 ? -0.18f : 0.18f, 0.04f, 0f);
            t.transform.localScale = new Vector3(0.16f, 0.06f, 0.26f);
            thrusters[i] = t.GetComponent<Renderer>();
            thrusters[i].shadowCastingMode = ShadowCastingMode.Off;
            thrusters[i].enabled = false;
            SetMat(t, thrusterMat);
        }

        var head = new GameObject("Head").transform;
        head.SetParent(player.transform, false);
        head.localPosition = new Vector3(0f, 1.6f, 0f);
        cam.transform.SetParent(head, false);
        cam.transform.localPosition = Vector3.zero;
        cam.transform.localRotation = Quaternion.identity;

        var fpc = player.AddComponent<FirstPersonController>();
        fpc.playerCamera = cam;
        fpc.cameraRoot = head;
        fpc.bodyRenderers = new[] { body.GetComponent<Renderer>(), visor.GetComponent<Renderer>() };
        fpc.thrusterRenderers = thrusters;
        fpc.blobShadowMaterial = GetBlobShadowMaterial();

        int layer = EnsureLayer("Player");
        if (layer >= 0)
        {
            player.layer = layer;
            body.layer = layer;
            visor.layer = layer;
            foreach (var t in thrusters) t.gameObject.layer = layer;
            head.gameObject.layer = layer;
        }
        return fpc;
    }

    // Soft round dark blob, transparent URP Unlit. Saved as assets so the build includes the right variant.
    internal static Material GetBlobShadowMaterial()
    {
        string texPath = MaterialFolder + "/BlobShadow_Tex.asset";
        var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(texPath);
        if (tex == null)
        {
            const int size = 64;
            tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, name = "BlobShadow" };
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float r = new Vector2(x + 0.5f - size * 0.5f, y + 0.5f - size * 0.5f).magnitude / (size * 0.5f);
                float a = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(1f - r));
                tex.SetPixel(x, y, new Color(0f, 0f, 0f, a));
            }
            tex.Apply();
            Directory.CreateDirectory(MaterialFolder);
            AssetDatabase.CreateAsset(tex, texPath);
        }

        string matPath = MaterialFolder + "/BlobShadow.mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
        if (mat == null)
        {
            Shader sh = Shader.Find("Universal Render Pipeline/Unlit");
            if (sh == null) sh = Shader.Find("Unlit/Transparent");
            mat = new Material(sh);
            AssetDatabase.CreateAsset(mat, matPath);
        }
        var tint = new Color(0f, 0f, 0f, 0.65f);
        if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", tex);
        if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", tex);
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", tint);
        if (mat.HasProperty("_Color")) mat.SetColor("_Color", tint);
        // URP transparent, alpha blended, no depth write.
        mat.SetFloat("_Surface", 1f);
        mat.SetFloat("_Blend", 0f);
        mat.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
        mat.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
        mat.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
        mat.SetFloat("_DstBlendAlpha", (float)BlendMode.OneMinusSrcAlpha);
        mat.SetFloat("_ZWrite", 0f);
        mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        mat.SetOverrideTag("RenderType", "Transparent");
        mat.renderQueue = (int)RenderQueue.Transparent;
        EditorUtility.SetDirty(mat);
        return mat;
    }

    // Returns the layer index, adding the layer to the first free user slot if it doesn't exist.
    internal static int EnsureLayer(string name)
    {
        int existing = LayerMask.NameToLayer(name);
        if (existing >= 0) return existing;

        var tagManager = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
        var layers = tagManager.FindProperty("layers");
        for (int i = 8; i < layers.arraySize; i++)
        {
            var slot = layers.GetArrayElementAtIndex(i);
            if (!string.IsNullOrEmpty(slot.stringValue)) continue;
            slot.stringValue = name;
            tagManager.ApplyModifiedProperties();
            return i;
        }
        Debug.LogError($"No free layer slot for '{name}'.");
        return -1;
    }

    // ---------- props ----------

    internal static float RandomHeight(System.Random rng)
    {
        double r = rng.NextDouble();
        float h = 15f + 245f * (float)(r * r); // mostly mid-rise, a few very tall
        return SnapToGrid(h);
    }

    // Round a height to a whole number of layers so roofs are layer floors.
    internal static float SnapToGrid(float h) => Mathf.Max(1, Mathf.RoundToInt(h / TrafficAuthority.Spacing)) * TrafficAuthority.Spacing;

    internal static T Pick<T>(T[] arr, System.Random rng) => arr[rng.Next(arr.Length)];

    // Box standing on the ground at base position.
    internal static Transform Box(Transform parent, string name, Vector3 basePos, Vector3 size, Material mat)
    {
        var t = Slab(parent, name, basePos + Vector3.up * size.y * 0.5f, size, mat);
        return t;
    }

    // Box centered at position.
    internal static Transform Slab(Transform parent, string name, Vector3 center, Vector3 size, Material mat)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.position = center;
        go.transform.localScale = size;
        SetMat(go, mat);
        return go.transform;
    }

    internal static FlyingVehicle CreateVehicle(string name, Material mat)
    {
        var root = new GameObject(name);
        var body = GameObject.CreatePrimitive(PrimitiveType.Cube);
        body.name = "Body";
        body.transform.SetParent(root.transform, false);
        body.transform.localScale = new Vector3(3f, 1.5f, 6f);
        SetMat(body, mat);

        var cockpit = new GameObject("Cockpit").transform;
        cockpit.SetParent(root.transform, false);
        cockpit.localPosition = new Vector3(0f, 1.2f, 0.5f);

        AddKinematicBody(root);
        var v = root.AddComponent<FlyingVehicle>();
        v.cockpitAnchor = cockpit;
        AddCarLights(root);
        return v;
    }

    // Two warm headlights (just over the bloom threshold) and two dim red tail lights (under it) at the
    // body's corners. Emissive boxes only: no Light components, no colliders. CarLights swaps the tails to
    // the brake material while decelerating and everything to "off" while parked.
    static Material headOnMat, tailOnMat, brakeMat, lightsOffMat;

    static void AddCarLights(GameObject root)
    {
        if (headOnMat == null) headOnMat = GetUnlitMaterial("CarHeadlight", new Color(1f, 0.92f, 0.78f), 3f);
        if (tailOnMat == null) tailOnMat = GetUnlitMaterial("CarTaillight", new Color(1f, 0.08f, 0.05f), 0.8f);
        if (brakeMat == null) brakeMat = GetUnlitMaterial("CarBrakeLight", new Color(1f, 0.08f, 0.05f), 2.5f);
        if (lightsOffMat == null) lightsOffMat = GetUnlitMaterial("CarLightOff", new Color(0.08f, 0.08f, 0.09f), 1f);

        // Body is 3 x 1.5 x 6, centred on the root.
        var heads = new Renderer[2];
        var tails = new Renderer[2];
        for (int i = 0; i < 2; i++)
        {
            float x = i == 0 ? -1.1f : 1.1f;
            // 0.4 m deep, half inside the body: sticks 0.2 m out of the face so its top reads from above.
            heads[i] = LightBox(root, "Headlight", new Vector3(x, 0.15f, 3f), new Vector3(0.45f, 0.22f, 0.4f), headOnMat);
            tails[i] = LightBox(root, "Taillight", new Vector3(x, 0.15f, -3f), new Vector3(0.45f, 0.22f, 0.4f), tailOnMat);
        }
        var lights = root.AddComponent<CarLights>();
        lights.headlights = heads;
        lights.taillights = tails;
        lights.headOn = headOnMat;
        lights.tailOn = tailOnMat;
        lights.brakeOn = brakeMat;
        lights.off = lightsOffMat;
    }

    static Renderer LightBox(GameObject root, string name, Vector3 localPos, Vector3 size, Material mat)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        Object.DestroyImmediate(go.GetComponent<Collider>());
        go.transform.SetParent(root.transform, false);
        go.transform.localPosition = localPos;
        go.transform.localScale = size;
        var r = go.GetComponent<Renderer>();
        r.sharedMaterial = mat;
        r.shadowCastingMode = ShadowCastingMode.Off;
        r.receiveShadows = false;
        return r;
    }

    // URP Unlit colour times intensity (> 1 blooms), GPU instancing on.
    internal static Material GetUnlitMaterial(string name, Color color, float intensity)
    {
        string assetPath = $"{MaterialFolder}/{name}.mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
        var shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (mat == null)
        {
            mat = new Material(shader);
            Directory.CreateDirectory(MaterialFolder);
            AssetDatabase.CreateAsset(mat, assetPath);
        }
        else if (shader != null) mat.shader = shader;
        mat.SetColor("_BaseColor", new Color(color.r * intensity, color.g * intensity, color.b * intensity, 1f));
        mat.enableInstancing = true;
        EditorUtility.SetDirty(mat);
        return mat;
    }

    // Moving colliders need a kinematic Rigidbody so physics doesn't treat them as static geometry.
    // Still moved by script through the transform, exactly as before.
    internal static Rigidbody AddKinematicBody(GameObject go)
    {
        var rb = go.AddComponent<Rigidbody>();
        rb.isKinematic = true;
        rb.useGravity = false;
        rb.interpolation = RigidbodyInterpolation.None;
        return rb;
    }

    // Visible rack across the back of a car with a VehicleGrabPoint on its rear edge. No collider:
    // it would overlap the body and the car's own collision would push against it.
    internal static void AddRearRack(FlyingVehicle v, Material mat)
    {
        var rack = Slab(v.transform, "RearRack", Vector3.zero, Vector3.one, mat);
        Object.DestroyImmediate(rack.GetComponent<Collider>());
        rack.localPosition = new Vector3(0f, 0.55f, -3.35f);
        rack.localRotation = Quaternion.identity;
        rack.localScale = new Vector3(1.6f, 0.3f, 0.7f); // narrower than the car so the tail lights show from above

        var grab = new GameObject("GrabPoint").transform;
        grab.SetParent(v.transform, false);
        grab.localPosition = new Vector3(0f, 0.7f, -3.7f);
        grab.gameObject.AddComponent<VehicleGrabPoint>();
    }

    internal static void SetMat(GameObject go, Material m) => go.GetComponent<Renderer>().sharedMaterial = m;
    internal static void SetMat(Transform t, Material m) => SetMat(t.gameObject, m);

    internal static Material GetMaterial(string name, Color color)
    {
        string assetPath = $"{MaterialFolder}/{name}.mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
        if (mat == null)
        {
            Shader sh = Shader.Find("Universal Render Pipeline/Lit");
            if (sh == null) sh = Shader.Find("Standard");
            mat = new Material(sh);
            Directory.CreateDirectory(MaterialFolder);
            AssetDatabase.CreateAsset(mat, assetPath);
        }
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
        if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
        EditorUtility.SetDirty(mat);
        return mat;
    }
}
