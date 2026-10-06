using UnityEngine;

// Spawns small guiding lights along a LanePath at play time.
// Middle lane = cyan, upper = yellow, lower = magenta, no-switch sections = red.
[RequireComponent(typeof(LanePath))]
public class LaneLights : MonoBehaviour
{
    public float spacing = 15f;
    public float size = 0.6f;
    public Color middleColor = Color.cyan;
    public Color upperColor = Color.yellow;
    public Color lowerColor = Color.magenta;
    public Color noSwitchColor = Color.red;

    void Start()
    {
        var path = GetComponent<LanePath>();
        path.Rebuild();
        var root = new GameObject("LaneLights").transform;
        root.SetParent(transform, false);

        Material mid = MakeMat(middleColor), up = MakeMat(upperColor), low = MakeMat(lowerColor), red = MakeMat(noSwitchColor);

        for (float d = 0f; d < path.Length; d += spacing)
        {
            path.Sample(d, out var p, out _);
            Spawn(root, p, path.IsNoSwitch(d) ? red : mid);
        }

        foreach (var seg in path.sideLanes)
        {
            for (float d = seg.startDistance; d <= seg.endDistance; d += spacing)
            {
                float w = LanePath.SegmentWeight(seg, d);
                if (w < 0.05f) continue;
                path.Sample(d, out var p, out var f);
                bool noSwitch = path.IsNoSwitch(d);
                Spawn(root, path.ToWorld(p, f, seg.offset * w),
                      noSwitch ? red : seg.layer == LaneLayer.Upper ? up : low);
            }
        }
    }

    void Spawn(Transform parent, Vector3 pos, Material mat)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        Destroy(go.GetComponent<Collider>());
        go.transform.SetParent(parent, true);
        go.transform.position = pos;
        go.transform.localScale = Vector3.one * size;
        go.GetComponent<Renderer>().sharedMaterial = mat;
    }

    static Material MakeMat(Color c)
    {
        Shader sh = Shader.Find("Universal Render Pipeline/Unlit");
        if (sh == null) sh = Shader.Find("Unlit/Color");
        var m = new Material(sh);
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        if (m.HasProperty("_Color")) m.SetColor("_Color", c);
        return m;
    }
}
