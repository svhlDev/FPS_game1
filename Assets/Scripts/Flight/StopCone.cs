using System.Collections.Generic;
using UnityEngine;

// The red cone over a police stop (a tow): a translucent red cylinder from the surface below the
// stopped car up to `above` m over it, plus a ring on the surface. Pulses slowly, no collider, follows
// the car. Lane traffic avoids the part of the cylinder round the tow group (from `bandBelow` m under
// the car to the top), plus a margin: it shifts to a lane level outside that band, or stops and waits
// for the band to pass (FlyingVehicle avoidance). Levels further down stay open until it gets there.
public class StopCone : MonoBehaviour
{
    public float radius = 12f;
    public float above = 20f;
    public float pulseRate = 0.6f;
    [Tooltip("Traffic treats the cylinder from this far below the stopped car up to the top as blocked.")]
    public float bandBelow = 10f;

    static readonly List<StopCone> active = new List<StopCone>();
    public static IReadOnlyList<StopCone> Active => active;

    public Transform Target { get; private set; }
    public float Bottom { get; private set; }
    public float Top => Target != null ? Target.position.y + above : Bottom + above;
    public Vector3 Center => Target != null ? Target.position : transform.position;

    Transform tube, ring;
    Material mat;
    float baseIntensity = 1f;
    float nextProbe;
    static Mesh tubeMesh, ringMesh;
    static readonly RaycastHit[] hits = new RaycastHit[16];

    public static StopCone Spawn(Transform target, Material material)
    {
        var go = new GameObject("StopCone");
        var cone = go.AddComponent<StopCone>();
        cone.Target = target;
        cone.mat = material != null ? new Material(material) : DefaultMaterial();
        if (cone.mat != null && cone.mat.HasProperty("_Intensity")) cone.baseIntensity = cone.mat.GetFloat("_Intensity");
        cone.tube = cone.Part("Tube", Tube());
        cone.ring = cone.Part("Ring", Ring());
        cone.Probe();
        cone.Place();
        return cone;
    }

    // Scenes built before the police cars carried a cone material: make one from the hologram shader.
    static Material DefaultMaterial()
    {
        var shader = Shader.Find("FPS/Hologram");
        if (shader == null) return null;
        var m = new Material(shader);
        m.SetColor("_ColorA", new Color(1f, 0.08f, 0.05f));
        m.SetColor("_ColorB", new Color(1f, 0.25f, 0.1f));
        m.SetFloat("_Intensity", 0.35f);
        m.SetFloat("_Pattern", 0f);
        m.SetFloat("_ScrollSpeed", 0.05f);
        m.SetFloat("_ScanlineDensity", 4f);
        m.SetFloat("_ScanlineStrength", 0.3f);
        m.SetFloat("_FlickerRate", 0f);
        m.SetFloat("_EdgeFade", 0.15f);
        return m;
    }

    Transform Part(string name, Mesh mesh)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var r = go.AddComponent<MeshRenderer>();
        r.sharedMaterial = mat;
        r.enabled = mat != null; // never the magenta error shader
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;
        return go.transform;
    }

    void OnEnable() => active.Add(this);
    void OnDisable() => active.Remove(this);
    void OnDestroy() { if (mat != null) Destroy(mat); }

    // First static surface under the target (cars, people ignored).
    void Probe()
    {
        Vector3 from = Center + Vector3.down * 2f;
        int n = Physics.RaycastNonAlloc(from, Vector3.down, hits, 2000f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        float best = float.MaxValue;
        Bottom = 0f;
        for (int i = 0; i < n; i++)
        {
            var c = hits[i].collider;
            if (c.attachedRigidbody != null || c.GetComponentInParent<FlyingVehicle>() != null || c.GetComponentInParent<CharacterController>() != null) continue;
            if (hits[i].distance < best) { best = hits[i].distance; Bottom = hits[i].point.y; }
        }
    }

    void Place()
    {
        Vector3 c = Center;
        float h = Mathf.Max(1f, Top - Bottom);
        transform.position = new Vector3(c.x, Bottom, c.z);
        tube.localScale = new Vector3(radius, h, radius);
        ring.localPosition = Vector3.up * 0.05f;
        ring.localScale = new Vector3(radius, 1f, radius);
    }

    void LateUpdate()
    {
        if (Target == null) { Destroy(gameObject); return; }
        if (Time.time >= nextProbe) { nextProbe = Time.time + 0.5f; Probe(); }
        Place();
        if (mat != null && mat.HasProperty("_Intensity"))
            mat.SetFloat("_Intensity", baseIntensity * (0.7f + 0.3f * Mathf.Sin(Time.time * pulseRate * 2f * Mathf.PI)));
    }

    // Is a point inside the cylinder grown by `margin` (horizontally) and `vMargin` (vertically)?
    public bool Contains(Vector3 p, float margin, float vMargin)
    {
        Vector3 c = Center;
        float dx = p.x - c.x, dz = p.z - c.z, r = radius + margin;
        return dx * dx + dz * dz < r * r && p.y > Bottom - vMargin && p.y < Top + vMargin;
    }

    // The part traffic must keep out of: the band round the tow group (bandBelow under it to the top).
    public bool BlocksTraffic(Vector3 p, float margin, float vMargin)
    {
        Vector3 c = Center;
        float dx = p.x - c.x, dz = p.z - c.z, r = radius + margin;
        return dx * dx + dz * dz < r * r && p.y > Mathf.Max(Bottom, c.y - bandBelow) - vMargin && p.y < Top + vMargin;
    }

    // ---------- meshes (unit size: radius 1, height 1) ----------

    static Mesh Tube()
    {
        if (tubeMesh != null) return tubeMesh;
        const int seg = 40;
        var v = new Vector3[(seg + 1) * 2];
        var uv = new Vector2[v.Length];
        var tri = new int[seg * 6];
        for (int i = 0; i <= seg; i++)
        {
            float a = i * Mathf.PI * 2f / seg;
            var d = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
            v[i * 2] = d; v[i * 2 + 1] = d + Vector3.up;
            uv[i * 2] = new Vector2(i / (float)seg, 0f); uv[i * 2 + 1] = new Vector2(i / (float)seg, 1f);
            if (i == seg) break;
            int t = i * 6, b = i * 2;
            tri[t] = b; tri[t + 1] = b + 1; tri[t + 2] = b + 2;
            tri[t + 3] = b + 1; tri[t + 4] = b + 3; tri[t + 5] = b + 2;
        }
        tubeMesh = new Mesh { name = "StopConeTube", vertices = v, uv = uv, triangles = tri };
        tubeMesh.RecalculateBounds();
        return tubeMesh;
    }

    static Mesh Ring()
    {
        if (ringMesh != null) return ringMesh;
        const int seg = 40;
        const float inner = 0.9f;
        var v = new Vector3[(seg + 1) * 2];
        var uv = new Vector2[v.Length];
        var tri = new int[seg * 6];
        for (int i = 0; i <= seg; i++)
        {
            float a = i * Mathf.PI * 2f / seg;
            var d = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
            v[i * 2] = d * inner; v[i * 2 + 1] = d;
            uv[i * 2] = new Vector2(i / (float)seg, 0.3f); uv[i * 2 + 1] = new Vector2(i / (float)seg, 0.7f);
            if (i == seg) break;
            int t = i * 6, b = i * 2;
            tri[t] = b; tri[t + 1] = b + 2; tri[t + 2] = b + 1;
            tri[t + 3] = b + 1; tri[t + 4] = b + 2; tri[t + 5] = b + 3;
        }
        ringMesh = new Mesh { name = "StopConeRing", vertices = v, uv = uv, triangles = tri };
        ringMesh.RecalculateBounds();
        return ringMesh;
    }
}
