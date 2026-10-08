using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

// Bakes a LanePath's guiding lights at edit time: one combined, static mesh per colour (middle lane,
// lanes above, lanes below, no-switch sections) made of low-poly spheres (~48 triangles each).
// The meshes go in a sibling object, never under the path (a LanePath's children are its waypoints).
public static class LaneLightBaker
{
    const int Bands = 4, Segments = 6;
    static Vector3[] unitVerts;
    static int[] unitTris;

    public static void Bake(LanePath path)
    {
        var s = path.GetComponent<LaneLights>();
        float spacing = s != null ? Mathf.Max(1f, s.spacing) : 15f;
        float radius = (s != null ? s.size : 0.6f) * 0.5f;
        var colors = s != null
            ? new[] { s.middleColor, s.upperColor, s.lowerColor, s.noSwitchColor }
            : new[] { Color.cyan, Color.yellow, Color.magenta, Color.red };
        string[] names = { "Middle", "Above", "Below", "NoSwitch" };
        const int Mid = 0, Up = 1, Low = 2, Red = 3;

        path.Rebuild();
        var buckets = new List<Vector3>[4];
        for (int i = 0; i < 4; i++) buckets[i] = new List<Vector3>();

        for (float d = 0f; d < path.Length; d += spacing)
        {
            path.Sample(d, out var p, out _);
            buckets[path.IsNoSwitch(d) ? Red : Mid].Add(p);
        }
        foreach (int level in path.laneLevels)
        {
            if (level == 0) continue;
            for (float d = 0f; d < path.Length; d += spacing)
            {
                path.LaneWeight(level, d, out var off);
                path.Sample(d, out var p, out var f);
                buckets[path.IsNoSwitch(d) ? Red : level > 0 ? Up : Low].Add(path.ToWorld(p, f, off));
            }
        }
        foreach (var seg in path.sideLanes)
        {
            for (float d = seg.startDistance; d <= seg.endDistance; d += spacing)
            {
                float w = LanePath.SegmentWeight(seg, d);
                if (w < 0.05f) continue;
                path.Sample(d, out var p, out var f);
                buckets[path.IsNoSwitch(d) ? Red : path.LevelOf(seg) > 0 ? Up : Low].Add(path.ToWorld(p, f, path.LaneOffset(seg) * w));
            }
        }

        var root = new GameObject(path.name + "_Lights").transform;
        root.SetParent(path.transform.parent, false);
        root.position = Vector3.zero;
        root.rotation = Quaternion.identity;
        for (int i = 0; i < 4; i++)
        {
            if (buckets[i].Count == 0) continue;
            var go = new GameObject(names[i]);
            go.transform.SetParent(root, false);
            go.AddComponent<MeshFilter>().sharedMesh = Combine(buckets[i], radius, $"{path.name}_{names[i]}");
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = FlightGrayboxBuilder.GetUnlitMaterial("LaneLight" + names[i], colors[i], 1f);
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccludeeStatic);
        }
    }

    static Mesh Combine(List<Vector3> points, float radius, string name)
    {
        if (unitVerts == null) BuildUnitSphere();
        var verts = new List<Vector3>(points.Count * unitVerts.Length);
        var tris = new List<int>(points.Count * unitTris.Length);
        foreach (var p in points)
        {
            int b = verts.Count;
            foreach (var v in unitVerts) verts.Add(p + v * radius);
            foreach (int t in unitTris) tris.Add(b + t);
        }
        var mesh = new Mesh { name = name };
        if (verts.Count > 65000) mesh.indexFormat = IndexFormat.UInt32;
        mesh.SetVertices(verts);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    // Latitude/longitude sphere: (Bands+1) x (Segments+1) vertices, Bands x Segments x 2 triangles.
    static void BuildUnitSphere()
    {
        var v = new List<Vector3>();
        for (int b = 0; b <= Bands; b++)
        {
            float theta = Mathf.PI * b / Bands;
            for (int s = 0; s <= Segments; s++)
            {
                float phi = 2f * Mathf.PI * s / Segments;
                v.Add(new Vector3(Mathf.Sin(theta) * Mathf.Cos(phi), Mathf.Cos(theta), Mathf.Sin(theta) * Mathf.Sin(phi)));
            }
        }
        var t = new List<int>();
        for (int b = 0; b < Bands; b++)
        for (int s = 0; s < Segments; s++)
        {
            int a = b * (Segments + 1) + s, c = a + Segments + 1;
            t.Add(a); t.Add(a + 1); t.Add(c);
            t.Add(a + 1); t.Add(c + 1); t.Add(c);
        }
        unitVerts = v.ToArray();
        unitTris = t.ToArray();
    }
}
