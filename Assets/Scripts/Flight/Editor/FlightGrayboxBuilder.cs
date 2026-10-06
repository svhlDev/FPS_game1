using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

// Tools > Build Flight Graybox: generates a test scene for the lane/layer flight system.
public static class FlightGrayboxBuilder
{
    const string ScenePath = "Assets/Scenes/FlightGraybox.unity";
    const string MaterialFolder = "Assets/Graybox/Materials";

    const float LaneAltitude = 40f;
    const float RooftopHeight = 36f; // just under the Middle layer, so a hijacked car lifts into it

    [MenuItem("Tools/Build Flight Graybox")]
    public static void Build()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

        var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
        var mainCam = Camera.main;
        mainCam.farClipPlane = 2000f;

        var groundMat = GetMaterial("Ground", new Color(0.35f, 0.37f, 0.35f));
        var towerMat = GetMaterial("Tower", new Color(0.6f, 0.6f, 0.62f));
        var parkedMat = GetMaterial("ParkedCar", new Color(0.2f, 0.8f, 0.3f));
        var trafficMat = GetMaterial("TrafficCar", new Color(0.95f, 0.55f, 0.1f));
        var policeMat = GetMaterial("Police", new Color(0.1f, 0.3f, 1f));
        var playerMat = GetMaterial("Player", new Color(0.9f, 0.9f, 0.9f));

        // Ground
        var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
        ground.name = "Ground";
        ground.transform.localScale = new Vector3(60f, 1f, 60f); // 600 x 600 m
        SetMat(ground, groundMat);

        // Towers: 12 around the loop, alternating inside/outside so the lane weaves between them.
        var towersRoot = new GameObject("Towers").transform;
        var rng = new System.Random(42);
        Transform rooftopTower = null;
        for (int i = 0; i < 12; i++)
        {
            float angle = (i * 30f + 15f) * Mathf.Deg2Rad;
            float radius = i % 2 == 0 ? 105f : 200f;
            float height = i == 0 ? RooftopHeight : 50f + (float)rng.NextDouble() * 80f;
            float width = 16f + (float)rng.NextDouble() * 4f;

            var tower = GameObject.CreatePrimitive(PrimitiveType.Cube);
            tower.name = $"Tower_{i:00}";
            tower.transform.SetParent(towersRoot, false);
            tower.transform.position = new Vector3(Mathf.Cos(angle) * radius, height * 0.5f, Mathf.Sin(angle) * radius);
            tower.transform.localScale = new Vector3(width, height, width);
            SetMat(tower, towerMat);
            if (i == 0) rooftopTower = tower.transform;
        }

        // Traffic authority (default layer altitudes 20 / 40 / 60)
        new GameObject("TrafficAuthority").AddComponent<TrafficAuthority>();

        // Lane path: 8 waypoints on a weaving loop at 40 m
        var pathGo = new GameObject("LanePath");
        for (int i = 0; i < 8; i++)
        {
            float angle = i * 45f * Mathf.Deg2Rad;
            float radius = i % 2 == 0 ? 165f : 145f;
            var wp = new GameObject($"WP_{i}");
            wp.transform.SetParent(pathGo.transform, false);
            wp.transform.position = new Vector3(Mathf.Cos(angle) * radius, LaneAltitude, Mathf.Sin(angle) * radius);
        }
        var path = pathGo.AddComponent<LanePath>();
        path.closedLoop = true;
        path.Rebuild();
        float L = path.Length;

        path.sideLanes.Add(new SideLaneSegment
        {
            layer = LaneLayer.Upper, startDistance = 0.10f * L, endDistance = 0.40f * L,
            offset = new Vector2(0f, 20f), blendLength = 40f
        });
        path.sideLanes.Add(new SideLaneSegment
        {
            layer = LaneLayer.Lower, startDistance = 0.55f * L, endDistance = 0.85f * L,
            offset = new Vector2(0f, -20f), blendLength = 40f
        });
        path.noSwitchZones.Add(new NoSwitchZone { startDistance = 0.25f * L, endDistance = 0.32f * L });
        pathGo.AddComponent<LaneLights>();

        // Player on the rooftop of tower 0
        Vector3 roof = rooftopTower.position + Vector3.up * (RooftopHeight * 0.5f);
        Vector3 inward = -new Vector3(rooftopTower.position.x, 0f, rooftopTower.position.z).normalized;

        var player = new GameObject("Player");
        player.transform.position = roof + inward * 5f;
        player.transform.rotation = Quaternion.LookRotation(-inward);
        var cc = player.AddComponent<CharacterController>();
        cc.height = 2f; cc.radius = 0.5f; cc.center = new Vector3(0f, 1f, 0f);

        var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        body.name = "Body";
        Object.DestroyImmediate(body.GetComponent<Collider>());
        body.transform.SetParent(player.transform, false);
        body.transform.localPosition = new Vector3(0f, 1f, 0f);
        body.GetComponent<Renderer>().shadowCastingMode = ShadowCastingMode.ShadowsOnly;
        SetMat(body, playerMat);

        var head = new GameObject("Head").transform;
        head.SetParent(player.transform, false);
        head.localPosition = new Vector3(0f, 1.6f, 0f);

        mainCam.transform.SetParent(head, false);
        mainCam.transform.localPosition = Vector3.zero;
        mainCam.transform.localRotation = Quaternion.identity;

        var fpc = player.AddComponent<FirstPersonController>();
        fpc.playerCamera = mainCam;
        fpc.cameraRoot = head;

        // Parked car on the rooftop (no path)
        var parked = CreateVehicle("ParkedCar", parkedMat);
        parked.transform.position = roof - inward * 2f + Vector3.up * 0.75f;
        parked.transform.rotation = Quaternion.LookRotation(Vector3.Cross(Vector3.up, inward));

        // Traffic on the path
        var traffic = new[]
        {
            (start: 0.05f, layer: LaneLayer.Middle, speed: 40f),
            (start: 0.22f, layer: LaneLayer.Upper,  speed: 28f),
            (start: 0.50f, layer: LaneLayer.Middle, speed: 55f),
            (start: 0.70f, layer: LaneLayer.Lower,  speed: 32f),
        };
        var trafficRoot = new GameObject("Traffic").transform;
        for (int i = 0; i < traffic.Length; i++)
        {
            var t = traffic[i];
            var v = CreateVehicle($"Traffic_{i}", trafficMat);
            v.transform.SetParent(trafficRoot, false);
            v.path = path;
            v.startDistance = t.start * L;
            v.startLayer = t.layer;
            v.aiCruiseSpeed = t.speed;

            // Place it where it will start so the scene view matches play mode.
            path.Sample(v.startDistance, out var p, out var f);
            float w = path.LaneWeight(t.layer, v.startDistance, out var off);
            v.transform.SetPositionAndRotation(path.ToWorld(p, f, off * w), Quaternion.LookRotation(f));
        }

        // Police hovering beside the lane
        var policeRoot = new GameObject("Police").transform;
        float[] policeAt = { 0.15f, 0.65f };
        for (int i = 0; i < policeAt.Length; i++)
        {
            path.Sample(policeAt[i] * L, out var p, out var f);
            Vector3 right = Vector3.Cross(Vector3.up, f).normalized;

            var cop = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cop.name = $"Police_{i}";
            cop.transform.SetParent(policeRoot, false);
            cop.transform.SetPositionAndRotation(p + right * 12f + Vector3.up * 5f, Quaternion.LookRotation(f));
            cop.transform.localScale = new Vector3(3f, 1.5f, 5f);
            SetMat(cop, policeMat);
            cop.AddComponent<PoliceUnit>();
        }

        Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
        EditorSceneManager.SaveScene(scene, ScenePath);
        Selection.activeGameObject = player;
        Debug.Log($"Flight graybox built: {ScenePath} (lane length {L:0} m)");
    }

    static FlyingVehicle CreateVehicle(string name, Material mat)
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

        var v = root.AddComponent<FlyingVehicle>();
        v.cockpitAnchor = cockpit;
        return v;
    }

    static void SetMat(GameObject go, Material m) => go.GetComponent<Renderer>().sharedMaterial = m;

    static Material GetMaterial(string name, Color color)
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
