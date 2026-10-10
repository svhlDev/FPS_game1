using System.Collections.Generic;
using UnityEngine;

// A trunk at the rear of every car: an open box (floor, ceiling, two sides, a front wall) cut into the
// rear of the body, its open side facing backward. For the 1.6 x 1.15 x 4.0 body the opening is 1.2
// wide x 0.5 tall in the upper half of the rear face, 0.7 deep (scaled with the body). The body mesh is
// replaced by a box with that cavity (BodyMesh; the subdivided version is VehicleHealth's dent mesh),
// shared by every car of the same size, so traffic keeps batching.
// A hatch panel exactly the size of the opening covers it. Opening slides it straight down along the
// outside of the rear face, over the lower half, in 0.4 s; closing slides it back up. The tail lights
// sit beside the opening and a rear rack moves back a little, so the hatch passes clear of both.
// Opened by the player on foot (PlayerInteract: E within 1.5 m of the rear, looking at it, the car
// slower than 1 m/s). Contents: police cars hold 2 grenades; civilian trunks are empty for now. Items
// in an open trunk show on its floor and are picked up with E. Opening a police trunk in sight of
// police: wanted 1 (PoliceDispatch.ReportTrunkTheft).
public class CarTrunk : MonoBehaviour
{
    public const float SlideTime = 0.4f;
    public const float OpenRange = 1.5f, MaxSpeed = 1f;
    // Opening on the reference 1.6 x 1.15 x 4.0 body (scaled per axis for other sizes).
    static readonly Vector3 RefBody = new Vector3(1.6f, 1.15f, 4f);
    const float OpenW = 1.2f, OpenH = 0.5f, Depth = 0.7f, TopGap = 0.05f;

    public FlyingVehicle Car { get; private set; }
    public bool IsOpen { get; private set; }
    public float OpenAmount { get; private set; }   // 0 closed .. 1 open
    public int Grenades { get; set; }
    public bool Police { get; private set; }
    public Transform Hatch { get; private set; }

    Vector3 bodyCentre, half;       // car-local
    float openW, openH, depth, yc;  // metres; yc = opening centre height above the body centre
    float closedY, openY;
    readonly List<Transform> items = new List<Transform>();
    public IReadOnlyList<Transform> Items => items;

    static readonly Dictionary<(Vector3, bool), Mesh> meshes = new Dictionary<(Vector3, bool), Mesh>();

    // Fit the trunk to `car`'s body (before the car is handed to instanced drawing).
    public static CarTrunk Setup(FlyingVehicle car)
    {
        var body = car.GetComponentInChildren<BoxCollider>();
        if (body == null) return null;
        var mf = body.GetComponent<MeshFilter>();
        var rend = body.GetComponent<Renderer>();
        if (mf == null || rend == null) return null;
        var t = car.gameObject.AddComponent<CarTrunk>();
        t.Car = car;
        t.Police = car.kind == FlyingVehicle.VehicleKind.Police || car.GetComponent<PoliceDriver>() != null;
        t.Grenades = t.Police ? 2 : 0;
        Vector3 size = body.transform.localScale;
        t.half = size * 0.5f;
        t.bodyCentre = car.transform.InverseTransformPoint(body.transform.position);
        t.openW = OpenW * size.x / RefBody.x;
        t.openH = OpenH * size.y / RefBody.y;
        t.depth = Depth * size.z / RefBody.z;
        t.yc = t.half.y - TopGap * size.y / RefBody.y - t.openH * 0.5f;
        mf.sharedMesh = BodyMesh(size, false);

        // The hatch: a thin panel over the opening, just proud of the rear face.
        var hatch = GameObject.CreatePrimitive(PrimitiveType.Cube);
        hatch.name = "TrunkHatch";
        Destroy(hatch.GetComponent<Collider>());
        hatch.layer = car.gameObject.layer;
        hatch.transform.SetParent(car.transform, false);
        hatch.transform.localScale = new Vector3(t.openW, t.openH, 0.02f);
        t.closedY = t.bodyCentre.y + t.yc;
        t.openY = t.closedY - t.openH - 0.03f;
        hatch.transform.localPosition = new Vector3(t.bodyCentre.x, t.closedY, t.bodyCentre.z - t.half.z - 0.012f);
        var hr = hatch.GetComponent<Renderer>();
        hr.sharedMaterial = rend.sharedMaterial;
        hr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        t.Hatch = hatch.transform;
        if (car.Health != null) car.Health.TintAlso(hr);

        // Tail lights beside the opening; a rack a little further back.
        foreach (Transform c in car.transform)
        {
            if (c.name == "Taillight")
            {
                float side = Mathf.Sign(c.localPosition.x - t.bodyCentre.x);
                float room = t.half.x - t.openW * 0.5f;
                c.localScale = new Vector3(Mathf.Min(c.localScale.x, room * 0.8f), c.localScale.y, c.localScale.z);
                c.localPosition = new Vector3(t.bodyCentre.x + side * (t.openW * 0.5f + room * 0.5f), t.bodyCentre.y + t.yc, c.localPosition.z);
            }
            else if (c.name == "RearRack" || c.name == "GrabPoint")
                c.localPosition += Vector3.back * 0.04f;
        }
        t.enabled = false; // runs only while the hatch moves
        return t;
    }

    // ---------- geometry (car-local / world) ----------

    // Centre of the opening on the rear face.
    public Vector3 OpeningCentre => Car.transform.TransformPoint(bodyCentre + new Vector3(0f, yc, -half.z));
    public Vector3 RearNormal => -Car.transform.forward;
    // A point on the trunk floor, x across (-1..1), z into the trunk (0..1).
    public Vector3 FloorPoint(float x, float z) =>
        Car.transform.TransformPoint(bodyCentre + new Vector3(x * openW * 0.4f, yc - openH * 0.5f, -half.z + depth * Mathf.Lerp(0.2f, 0.8f, z)));

    public bool CanOpen => Car != null && Car.Velocity.magnitude < MaxSpeed && !(Car.Health != null && Car.Health.Wrecked);

    // ---------- open / close ----------

    public void Open()
    {
        if (IsOpen) return;
        IsOpen = true;
        enabled = true;
        ShowItems();
        Opened++;
    }

    public void Close()
    {
        if (!IsOpen) return;
        IsOpen = false;
        enabled = true;
    }

    public static int Opened;

    void Update()
    {
        OpenAmount = Mathf.MoveTowards(OpenAmount, IsOpen ? 1f : 0f, Time.deltaTime / SlideTime);
        var p = Hatch.localPosition;
        Hatch.localPosition = new Vector3(p.x, Mathf.Lerp(closedY, openY, OpenAmount), p.z);
        if (OpenAmount <= 0f) ClearItems();
        if (OpenAmount <= 0f || OpenAmount >= 1f) enabled = false;
    }

    void ShowItems()
    {
        ClearItems();
        for (int i = 0; i < Grenades; i++)
        {
            var g = Grenade.BuildModel(Car.transform, Car.gameObject.layer);
            g.position = FloorPoint(i == 0 ? -0.5f : 0.5f, 0.35f) + Car.transform.up * 0.045f;
            g.rotation = Car.transform.rotation * Quaternion.Euler(0f, 0f, 90f);
            items.Add(g);
        }
    }

    void ClearItems()
    {
        foreach (var i in items) if (i != null) Destroy(i.gameObject);
        items.Clear();
    }

    // An item taken by the player.
    public bool TakeItem(Transform item)
    {
        int i = items.IndexOf(item);
        if (i < 0) return false;
        items.RemoveAt(i);
        Destroy(item.gameObject);
        Grenades = Mathf.Max(0, Grenades - 1);
        return true;
    }

    // ---------- body mesh ----------

    // Unit box (-0.5..0.5) with the trunk cavity cut into its -Z face, for a body of `size` (m).
    // subdivided: the dent mesh (faces split so impacts can push them in).
    public static Mesh BodyMesh(Vector3 size, bool subdivided)
    {
        if (meshes.TryGetValue((size, subdivided), out var cached) && cached != null) return cached;
        float hx = OpenW / RefBody.x * 0.5f;                                      // opening half width (unit)
        float yt = 0.5f - TopGap / RefBody.y, yb = yt - OpenH / RefBody.y;        // opening top / bottom (unit)
        float zc = -0.5f + Depth / RefBody.z;                                     // cavity front wall (unit)

        var v = new List<Vector3>(); var tri = new List<int>(); var uv = new List<Vector2>();
        int Div(float len, int perUnit) => subdivided ? Mathf.Max(1, Mathf.RoundToInt(len * perUnit)) : 1;
        void Face(Vector3 origin, Vector3 a, Vector3 b, int na, int nb)
        {
            int start = v.Count;
            for (int j = 0; j <= nb; j++)
                for (int i = 0; i <= na; i++)
                {
                    v.Add(origin + a * (i / (float)na) + b * (j / (float)nb));
                    uv.Add(new Vector2(i / (float)na, j / (float)nb));
                }
            for (int j = 0; j < nb; j++)
                for (int i = 0; i < na; i++)
                {
                    int p = start + j * (na + 1) + i;
                    // Wound so cross(a, b) is the front face.
                    tri.Add(p); tri.Add(p + 1); tri.Add(p + na + 1);
                    tri.Add(p + 1); tri.Add(p + na + 2); tri.Add(p + na + 1);
                }
        }
        const int X = 6, Y = 3, Z = 10; // per unit, as the plain dent box
        Face(new Vector3(-0.5f, -0.5f, 0.5f), Vector3.right, Vector3.up, Div(1, X), Div(1, Y));       // +Z
        Face(new Vector3(0.5f, -0.5f, 0.5f), Vector3.back, Vector3.up, Div(1, Z), Div(1, Y));         // +X
        Face(new Vector3(-0.5f, -0.5f, -0.5f), Vector3.forward, Vector3.up, Div(1, Z), Div(1, Y));    // -X
        Face(new Vector3(-0.5f, 0.5f, 0.5f), Vector3.right, Vector3.back, Div(1, X), Div(1, Z));      // +Y
        Face(new Vector3(-0.5f, -0.5f, -0.5f), Vector3.right, Vector3.forward, Div(1, X), Div(1, Z)); // -Y
        // -Z: a frame round the opening.
        void Rear(float x0, float x1, float y0, float y1)
        {
            if (x1 - x0 <= 1e-4f || y1 - y0 <= 1e-4f) return;
            Face(new Vector3(x1, y0, -0.5f), Vector3.left * (x1 - x0), Vector3.up * (y1 - y0), Div(x1 - x0, X), Div(y1 - y0, Y));
        }
        Rear(-0.5f, 0.5f, -0.5f, yb);
        Rear(-0.5f, 0.5f, yt, 0.5f);
        Rear(-0.5f, -hx, yb, yt);
        Rear(hx, 0.5f, yb, yt);
        // The cavity, faces looking into it.
        float d = zc + 0.5f, w = 2f * hx, h = yt - yb;
        Face(new Vector3(-hx, yb, -0.5f), Vector3.forward * d, Vector3.right * w, Div(d, Z), Div(w, X));   // floor (+Y)
        Face(new Vector3(-hx, yt, -0.5f), Vector3.right * w, Vector3.forward * d, Div(w, X), Div(d, Z));   // ceiling (-Y)
        Face(new Vector3(-hx, yb, -0.5f), Vector3.up * h, Vector3.forward * d, Div(h, Y), Div(d, Z));      // left wall (+X)
        Face(new Vector3(hx, yb, -0.5f), Vector3.forward * d, Vector3.up * h, Div(d, Z), Div(h, Y));       // right wall (-X)
        Face(new Vector3(hx, yb, zc), Vector3.left * w, Vector3.up * h, Div(w, X), Div(h, Y));             // front wall (-Z)

        var m = new Mesh { name = subdivided ? "TrunkBodyDent" : "TrunkBody" };
        m.SetVertices(v);
        m.SetUVs(0, uv);
        m.SetTriangles(tri, 0);
        m.RecalculateNormals();
        m.RecalculateBounds();
        meshes[(size, subdivided)] = m;
        return m;
    }
}
