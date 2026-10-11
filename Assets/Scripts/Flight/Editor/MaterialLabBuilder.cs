using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using static FlightGrayboxBuilder;

// Tools > Build Material Lab: Assets/Scenes/MaterialLab.unity, the Graphics Upgrade test scene (see
// MaterialLab.cs). Layout, all on a dark ground under the city's night lighting:
//   - a row of test shapes per material preset (sphere, rounded cube, bevelled box, cylinder), labelled;
//   - characters in each outfit (spawned at play: generation needs the runtime), the T-gun on a stand;
//   - a car and a police car, a road section with lane markings, a tower facade block;
//   - orbiting neon point and spot lights, a shaft-beam light (preset), a reflection probe.
// Placeholder materials (URP Lit with each preset's colour, metallic, smoothness) until the surface
// library replaces them (Phase 2).
public static class MaterialLabBuilder
{
    const string ScenePath = "Assets/Scenes/MaterialLab.unity";
    const string Folder = "Assets/Graybox/Lab";

    // Material presets (Graphics spec Phase 2 table): name, colour, metallic, smoothness.
    public static readonly (string name, Color color, float metallic, float smooth)[] Presets =
    {
        ("Concrete", new Color(0.5f, 0.5f, 0.48f), 0f, 0.15f), ("Asphalt", new Color(0.12f, 0.12f, 0.13f), 0f, 0.2f),
        ("PaintedMetal", new Color(0.55f, 0.12f, 0.1f), 0.3f, 0.45f), ("BrushedMetal", new Color(0.7f, 0.7f, 0.72f), 1f, 0.55f),
        ("Chrome", new Color(0.9f, 0.9f, 0.92f), 1f, 0.95f), ("Glass", new Color(0.05f, 0.07f, 0.08f), 0f, 0.95f),
        ("Rubber", new Color(0.05f, 0.05f, 0.05f), 0f, 0.25f), ("CarPaint", new Color(0.1f, 0.25f, 0.6f), 0.5f, 0.8f),
        ("Cotton", new Color(0.75f, 0.72f, 0.68f), 0f, 0.2f), ("Denim", new Color(0.18f, 0.24f, 0.38f), 0f, 0.25f),
        ("Wool", new Color(0.4f, 0.3f, 0.25f), 0f, 0.1f), ("Leather", new Color(0.25f, 0.15f, 0.08f), 0f, 0.45f),
        ("Vinyl", new Color(0.08f, 0.08f, 0.09f), 0f, 0.85f),
    };

    [MenuItem("Tools/Build Material Lab")]
    public static void Build()
    {
        if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
        Directory.CreateDirectory(Folder);
        var cam = Camera.main;
        var root = new GameObject("Lab").transform;

        // Ground and the city's night.
        var ground = Slab(root, "Ground", new Vector3(0f, -0.05f, 10f), new Vector3(120f, 0.1f, 90f), GetMaterial("LabGround", new Color(0.09f, 0.09f, 0.1f)));
        ground.gameObject.isStatic = true;
        var fog = CityDressing.AddHeightFog();
        fog.smogTop = 30f; fog.smogTopVariation = 0f; fog.smogDensity = 0.004f;
        CityDressing.SetupAtmosphere(cam, "MaterialLab_Post");

        // Material rows: z = 0..12, one row per preset, shapes along x.
        var meshes = new[] { Sphere(), RoundedCube(0.25f, 6, "LabRoundedCube"), RoundedCube(0.06f, 1, "LabBevelBox"), Cylinder() };
        var shapes = new GameObject("Materials").transform; shapes.SetParent(root, false);
        var exhibits = new List<Transform>();
        for (int r = 0; r < Presets.Length; r++)
        {
            var (name, color, metal, smooth) = Presets[r];
            var mat = LabMaterial(name, color, metal, smooth);
            var row = new GameObject(name).transform; row.SetParent(shapes, false);
            row.localPosition = new Vector3(-12f + (r % 7) * 4f, 0f, r < 7 ? 0f : 4.5f);
            for (int s = 0; s < meshes.Length; s++)
            {
                var go = new GameObject(meshes[s].name);
                go.transform.SetParent(row, false);
                go.transform.localPosition = new Vector3((s % 2) * 1.6f - 0.8f, 0.6f, (s / 2) * 1.6f - 0.8f);
                go.transform.localScale = Vector3.one * 1.1f;
                go.AddComponent<MeshFilter>().sharedMesh = meshes[s];
                go.AddComponent<MeshRenderer>().sharedMaterial = mat;
                go.isStatic = true;
            }
            Label(row, name, new Vector3(0f, 0.12f, -1.9f));
            exhibits.Add(row);
        }

        // Characters (spawned at play), the gun stand.
        var chars = new GameObject("Characters").transform; chars.SetParent(root, false);
        chars.localPosition = new Vector3(-3f, 0f, 12f);
        chars.localRotation = Quaternion.Euler(0f, 180f, 0f);
        var stand = Slab(root, "GunStand", new Vector3(5f, 0.5f, 12f), new Vector3(0.6f, 1f, 0.6f), LabMaterial("Concrete", Presets[0].color, 0f, 0.15f));
        var gunAt = new GameObject("Gun").transform; gunAt.SetParent(stand, false); gunAt.localPosition = new Vector3(0f, 0.65f, 0f);

        // Cars.
        var car = CreateVehicle("LabCar", GetMaterial("TrafficCar", new Color(0.95f, 0.55f, 0.1f)));
        car.transform.SetParent(root, false); car.transform.position = new Vector3(9f, CarRootAboveUnderside + 0.2f, 12f);
        car.enabled = false;
        var police = CreateVehicle("LabPoliceCar", GetMaterial("PoliceBody", new Color(0.04f, 0.07f, 0.2f)));
        police.transform.SetParent(root, false); police.transform.position = new Vector3(12f, CarRootAboveUnderside + 0.2f, 12f);
        police.enabled = false;
        exhibits.Add(car.transform);

        // Road section with lane markings, and a facade block.
        var road = Slab(root, "Road", new Vector3(0f, 0.01f, 24f), new Vector3(40f, 0.02f, 12f), LabMaterial("Asphalt", Presets[1].color, 0f, 0.2f));
        var paint = GetUnlitMaterial("LabLanePaint", new Color(0.8f, 0.8f, 0.75f), 0.6f);
        for (int i = -4; i <= 4; i++)
        {
            var dash = Slab(root, "LaneDash", new Vector3(i * 4f, 0.025f, 24f), new Vector3(2f, 0.01f, 0.15f), paint);
            Object.DestroyImmediate(dash.GetComponent<Collider>());
        }
        exhibits.Add(road);
        var kit = CityDressing.CreateKit(10f, 9, 16);
        var t = CityDressing.BuildTower(root, "FacadeBlock", new Vector3(0f, 0f, 38f), new Vector2(16f, 30f), 8, Vector3.back,
                                        new Color(0.55f, 0.56f, 0.6f), new System.Random(7), kit);
        exhibits.Add(t.root);

        // Lights: orbiting neon (points and spots), the shaft beam (off until its preset), a reflection probe.
        var lab = new GameObject("MaterialLab").AddComponent<MaterialLab>();
        var focus = new GameObject("Focus").transform; focus.SetParent(root, false); focus.position = new Vector3(0f, 1f, 6f);
        lab.focus = focus; lab.characterRow = chars; lab.gunStand = gunAt; lab.exhibits = exhibits;
        var colors = new[] { new Color(1f, 0.2f, 0.75f), new Color(0.2f, 0.9f, 1f), new Color(1f, 0.6f, 0.15f), new Color(0.35f, 1f, 0.45f), new Color(0.6f, 0.3f, 1f) };
        for (int i = 0; i < colors.Length; i++)
        {
            var l = new GameObject("Neon" + i).AddComponent<Light>();
            l.transform.SetParent(root, false);
            float a = i * 72f * Mathf.Deg2Rad;
            l.transform.position = focus.position + new Vector3(Mathf.Sin(a) * 14f, i % 2 == 0 ? 3f : 6f, Mathf.Cos(a) * 14f);
            l.type = i < 3 ? LightType.Point : LightType.Spot;
            l.color = colors[i]; l.intensity = i < 3 ? 25f : 60f; l.range = 22f; l.spotAngle = 50f;
            l.shadows = LightShadows.None;
            lab.neon.Add(l);
        }
        var beam = new GameObject("ShaftBeam").AddComponent<Light>();
        beam.transform.SetParent(root, false);
        beam.transform.position = focus.position + new Vector3(-8f, 60f, -5f);
        beam.transform.rotation = Quaternion.LookRotation(focus.position - beam.transform.position);
        beam.type = LightType.Spot; beam.spotAngle = 24f; beam.range = 120f; beam.intensity = 20000f;
        beam.color = new Color(1f, 0.95f, 0.85f); beam.shadows = LightShadows.Soft; beam.enabled = false;
        lab.beam = beam;
        // A street lamp in front of the characters (the facade windows behind them don't light them).
        var lamp = new GameObject("StreetLamp").AddComponent<Light>();
        lamp.transform.SetParent(root, false); lamp.transform.position = chars.position + new Vector3(-2.6f, 4f, -4f);
        lamp.type = LightType.Point; lamp.color = new Color(1f, 0.85f, 0.65f); lamp.intensity = 18f; lamp.range = 14f;
        lamp.shadows = LightShadows.Soft;
        var probe = new GameObject("ReflectionProbe").AddComponent<ReflectionProbe>();
        probe.transform.SetParent(root, false); probe.transform.position = new Vector3(0f, 3f, 10f);
        probe.size = new Vector3(60f, 20f, 50f); probe.boxProjection = true;
        probe.mode = ReflectionProbeMode.Realtime; probe.refreshMode = ReflectionProbeRefreshMode.ViaScripting;
        lab.probe = probe;

        cam.transform.position = new Vector3(0f, 4f, -8f);
        cam.transform.rotation = Quaternion.LookRotation(new Vector3(0f, -0.25f, 1f));
        EditorSceneManager.SaveScene(scene, ScenePath);
        AssetDatabase.SaveAssets();
        Debug.Log("MATERIAL LAB BUILT: " + ScenePath);
    }

    public static void BuildBatch()
    {
        bool ok = true;
        try { Build(); } catch (System.Exception e) { Debug.LogException(e); ok = false; }
        EditorApplication.Exit(ok ? 0 : 1);
    }

    // Placeholder until the surface library (Phase 2): URP Lit with the preset's look.
    static Material LabMaterial(string name, Color c, float metallic, float smooth)
    {
        var m = GetMaterial("Lab_" + name, c);
        m.SetFloat("_Metallic", metallic);
        m.SetFloat("_Smoothness", smooth);
        EditorUtility.SetDirty(m);
        return m;
    }

    static void Label(Transform parent, string text, Vector3 local)
    {
        var go = new GameObject("Label");
        go.transform.SetParent(parent, false);
        go.transform.localPosition = local;
        go.transform.localRotation = Quaternion.Euler(55f, 0f, 0f);
        var tm = go.AddComponent<TextMesh>();
        tm.text = text; tm.characterSize = 0.05f; tm.fontSize = 48; tm.anchor = TextAnchor.MiddleCenter; tm.color = new Color(0.8f, 0.85f, 0.9f);
    }

    // ---------- shapes (saved as assets: the scene references them) ----------

    static Mesh Save(Mesh m)
    {
        string path = $"{Folder}/{m.name}.asset";
        AssetDatabase.DeleteAsset(path);
        AssetDatabase.CreateAsset(m, path);
        return m;
    }

    static Mesh Primitive(PrimitiveType t, string name)
    {
        var go = GameObject.CreatePrimitive(t);
        var m = Object.Instantiate(go.GetComponent<MeshFilter>().sharedMesh);
        Object.DestroyImmediate(go);
        m.name = name;
        return Save(m);
    }
    static Mesh Sphere() => Primitive(PrimitiveType.Sphere, "LabSphere");
    static Mesh Cylinder()
    {
        var m = Primitive(PrimitiveType.Cylinder, "LabCylinder");
        // Unity's cylinder is 2 tall: halve it into the shapes' 1 m box.
        var v = m.vertices; for (int i = 0; i < v.Length; i++) v[i].y *= 0.5f;
        m.vertices = v; m.RecalculateBounds(); EditorUtility.SetDirty(m);
        return m;
    }

    // A 1 m cube with rounded edges (radius r, `seg` segments round each edge; 1 = a bevel).
    static Mesh RoundedCube(float r, int seg, string name)
    {
        int n = seg * 2 + 1;                 // grid points per face edge beyond the corners
        var verts = new List<Vector3>(); var norms = new List<Vector3>(); var tris = new List<int>();
        float inner = 0.5f - r;
        Vector3[] axes = { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };
        foreach (var nrm in axes)
        {
            Vector3 u = Mathf.Abs(nrm.y) > 0.5f ? Vector3.right : Vector3.up;
            Vector3 w = Vector3.Cross(nrm, u);
            int res = n + 1, start = verts.Count;
            for (int j = 0; j <= res; j++)
                for (int i = 0; i <= res; i++)
                {
                    // Points crowd toward the edges so the rounded strips get the segments.
                    float a = Grid(i, res, r), b = Grid(j, res, r);
                    Vector3 p = nrm * 0.5f + u * a + w * b;
                    Vector3 c = new Vector3(Mathf.Clamp(p.x, -inner, inner), Mathf.Clamp(p.y, -inner, inner), Mathf.Clamp(p.z, -inner, inner));
                    Vector3 d = (p - c).normalized;
                    verts.Add(c + d * r); norms.Add(d);
                }
            for (int j = 0; j < res; j++)
                for (int i = 0; i < res; i++)
                {
                    int q = start + j * (res + 1) + i;
                    tris.Add(q); tris.Add(q + res + 1); tris.Add(q + 1);
                    tris.Add(q + 1); tris.Add(q + res + 1); tris.Add(q + res + 2);
                }
        }
        var m = new Mesh { name = name };
        m.SetVertices(verts); m.SetNormals(norms); m.SetTriangles(tris, 0);
        // Winding: make every triangle face outward.
        var v2 = m.vertices; var t2 = m.triangles;
        for (int i = 0; i < t2.Length; i += 3)
            if (Vector3.Dot(Vector3.Cross(v2[t2[i + 1]] - v2[t2[i]], v2[t2[i + 2]] - v2[t2[i]]), v2[t2[i]] + v2[t2[i + 1]] + v2[t2[i + 2]]) < 0f)
            { int k = t2[i + 1]; t2[i + 1] = t2[i + 2]; t2[i + 2] = k; }
        m.triangles = t2;
        m.RecalculateBounds();
        return Save(m);
    }

    // i in 0..res -> position along a face edge (-0.5..0.5): the outer `seg` steps either side span the
    // rounded strip (r), the middle step the flat face.
    static float Grid(int i, int res, float r)
    {
        int seg = (res - 1) / 2;
        if (i <= seg) return -0.5f + r * i / Mathf.Max(1, seg);
        if (i >= res - seg) return 0.5f - r * (res - i) / Mathf.Max(1, seg);
        return Mathf.Lerp(-0.5f + r, 0.5f - r, (i - seg) / (float)Mathf.Max(1, res - 2 * seg));
    }
}
