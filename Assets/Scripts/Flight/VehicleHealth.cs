using System.Collections.Generic;
using UnityEngine;

// Handling and toughness per kind of car.
[System.Serializable]
public struct VehicleProfile
{
    public string name;
    public float maxSpeedScale, accelScale, layerShiftScale, headingTurnRate, lateralGripScale, maxHealth;
    public Color colour;
    public bool emissiveTrim;

    public static readonly VehicleProfile Standard = new VehicleProfile
    { name = "Standard", maxSpeedScale = 1f, accelScale = 1f, layerShiftScale = 1f, headingTurnRate = 90f, lateralGripScale = 1f, maxHealth = 400f };
    public static readonly VehicleProfile Police = new VehicleProfile
    { name = "Police", maxSpeedScale = 1f, accelScale = 1f, layerShiftScale = 1f, headingTurnRate = 90f, lateralGripScale = 1f, maxHealth = 600f };
    // The rare one: hot pink, twice the speed, acceleration and layer-shift rate, sharp steering.
    public static readonly VehicleProfile Pink = new VehicleProfile
    { name = "Pink", maxSpeedScale = 2f, accelScale = 2f, layerShiftScale = 2f, headingTurnRate = 150f, lateralGripScale = 1.5f, maxHealth = 300f,
      colour = new Color(1f, 0.25f, 0.65f), emissiveTrim = true };
}

// A car's health and the damage you can see on it:
//   colour  : body colour -> brown at 50% -> near black at 0, in 10% steps (shared materials per step,
//             so instanced traffic keeps batching).
//   dents   : the body mesh becomes a subdivided box (only once damaged) and impacts push it in.
//   smoke   : below half health, more as it drops.
//   critical: at criticalFraction the car fails (FlyingVehicle nosedives it), flames, drains fast.
//   0       : Explosion, then a burning black wreck that falls (FlyingVehicle.UpdateWreck); it burns for
//             wreckBurnTime once at rest, then smoulders.
//   burning : fire damage per second, drips fire chunks while moving.
[DefaultExecutionOrder(-5)]
public class VehicleHealth : MonoBehaviour
{
    public enum State { Ok, Critical, Wreck }

    public float maxHealth = 400f;
    [Tooltip("Fraction of max health where the car goes critical.")]
    public float criticalFraction = 0.18f;
    [Tooltip("Critical: share of max health lost per second.")]
    public float criticalDrain = 0.05f;
    public float fireDamagePerSecond = 8f;
    public float wreckBurnTime = 120f;

    public float Health { get; private set; }
    public float Fraction => Mathf.Clamp01(Health / Mathf.Max(1f, maxHealth));
    public State Current { get; private set; }
    public bool Critical => Current == State.Critical;
    public bool Wrecked => Current == State.Wreck;
    public bool Burning { get; private set; }
    public bool Smouldering => Wrecked && restTime >= 0f && Time.time - restTime > wreckBurnTime;

    FlyingVehicle car;
    Transform body;
    MeshFilter bodyFilter;
    Renderer bodyRend;
    Material baseMat;
    int colourStep = -1;
    Mesh dentMesh;
    Vector3[] dentVerts;
    float nextPuff, nextDrip, restTime = -1f;
    static readonly Dictionary<(Material, int), Material> stepMats = new Dictionary<(Material, int), Material>();
    static readonly Color Brown = new Color(0.35f, 0.22f, 0.12f), Black = new Color(0.05f, 0.05f, 0.05f);
    static Mesh boxTemplate;

    void Awake()
    {
        car = GetComponent<FlyingVehicle>();
        var bc = GetComponentInChildren<BoxCollider>();
        if (bc != null)
        {
            body = bc.transform;
            bodyFilter = body.GetComponent<MeshFilter>();
            bodyRend = body.GetComponent<Renderer>();
        }
        Health = maxHealth;
        enabled = false; // runs only once something has happened to it
    }

    public void SetBaseMaterial(Material m) { baseMat = m; colourStep = -1; ApplyColour(); }

    public void Init(float max)
    {
        maxHealth = max;
        Health = max;
        Current = State.Ok;
        Burning = false;
    }

    // Damage at a world point; `normal` points out of the car at the hit (dents push against it).
    public void Damage(float amount, Vector3 point, Vector3 normal)
    {
        if (amount <= 0f || Current == State.Wreck && Health <= 0f) return;
        enabled = true;
        Health = Mathf.Max(0f, Health - amount);
        Dent(point, normal, amount);
        ApplyColour();
        if (Current == State.Ok && Health <= maxHealth * criticalFraction) GoCritical();
        if (Health <= 0f && Current != State.Wreck) Explode();
    }

    public void Damage(float amount) => Damage(amount, transform.position + Random.insideUnitSphere, Random.onUnitSphere);

    public void Ignite()
    {
        if (Burning) return;
        Burning = true;
        enabled = true;
    }

    void GoCritical()
    {
        Current = State.Critical;
        Burning = true;
        car?.OnCritical();
    }

    void Explode()
    {
        Current = State.Wreck;
        Burning = true;
        Health = 0f;
        ApplyColour();
        // Extra crumple all over.
        for (int i = 0; i < 6; i++) Dent(transform.position + Random.insideUnitSphere * 2f, Random.onUnitSphere, 120f);
        Explosion.At(transform.position, car != null ? car.Velocity : Vector3.zero, this);
        car?.OnWrecked();
    }

    // The wreck has come to rest: burns for wreckBurnTime, then smoulders.
    public void OnRest() { if (restTime < 0f) restTime = Time.time; }

    void Update()
    {
        float dt = Time.deltaTime;
        if (Current == State.Critical) Damage(maxHealth * criticalDrain * dt);
        else if (Burning && Current == State.Ok) Damage(fireDamagePerSecond * dt);
        bool flames = Burning && !Smouldering;
        Vector3 top = transform.position + Vector3.up * (car != null ? car.BodyHalfExtents.y : 0.75f);
        float near = Camera.main != null ? (Camera.main.transform.position - top).sqrMagnitude : 0f;
        if (near > 500f * 500f) return;

        // Smoke below half health, thicker as it drops (and from smouldering wrecks).
        if ((Fraction < 0.5f || Smouldering) && Time.time >= nextPuff)
        {
            float rate = Smouldering ? 3f : Mathf.Lerp(2f, 14f, 1f - Fraction / 0.5f);
            nextPuff = Time.time + 1f / rate;
            Effects.Puff(top + Random.insideUnitSphere * 0.6f, Random.insideUnitSphere * 0.5f, Random.Range(0.4f, 0.8f), Random.Range(1.5f, 2.5f));
        }
        if (flames)
        {
            float s = car != null ? car.BodyHalfExtents.x : 1.5f;
            for (int i = 0; i < 4; i++)
                Effects.Flame(top + transform.rotation * new Vector3((i - 1.5f) * s * 0.5f, 0f, (i % 2 == 0 ? 1 : -1) * s * 0.6f), Random.Range(1.0f, 1.8f));
            // Burning and moving: drip fire.
            if (car != null && car.Velocity.sqrMagnitude > 4f && Time.time >= nextDrip)
            {
                nextDrip = Time.time + 2f;
                FireSystem.Spray(top - Vector3.up * 1f, car.Velocity * 0.5f, 1, 2f, 4f);
            }
        }
        if (Smouldering) Burning = false;
    }

    // ---------- colour ----------

    void ApplyColour()
    {
        if (bodyRend == null) return;
        if (baseMat == null) baseMat = bodyRend.sharedMaterial;
        int step = Current == State.Wreck ? 10 : Mathf.Clamp(Mathf.FloorToInt((1f - Fraction) * 10f), 0, 10);
        if (step == colourStep) return;
        colourStep = step;
        bodyRend.sharedMaterial = StepMaterial(baseMat, step);
    }

    static Material StepMaterial(Material baseMat, int step)
    {
        if (step == 0 || baseMat == null) return baseMat;
        if (stepMats.TryGetValue((baseMat, step), out var m) && m != null) return m;
        Color c0 = baseMat.HasProperty("_BaseColor") ? baseMat.GetColor("_BaseColor") : baseMat.color;
        float t = step / 10f; // 0 healthy .. 1 dead
        Color c = t < 0.5f ? Color.Lerp(c0, Brown, t / 0.5f) : Color.Lerp(Brown, Black, (t - 0.5f) / 0.5f);
        m = new Material(baseMat) { name = baseMat.name + "_dmg" + step, enableInstancing = true };
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c); else m.color = c;
        stepMats[(baseMat, step)] = m;
        return m;
    }

    // ---------- dents ----------

    void Dent(Vector3 point, Vector3 normal, float damage)
    {
        if (bodyFilter == null) return;
        if (dentMesh == null)
        {
            dentMesh = Instantiate(BoxTemplate());
            dentVerts = dentMesh.vertices;
            bodyFilter.sharedMesh = dentMesh;
            TrafficSystem.RefreshRenderable(car);
        }
        Vector3 scale = body.lossyScale;
        Vector3 local = body.InverseTransformPoint(point);
        // Contact point onto the box surface along the normal (normal in body space, scaled to metres).
        Vector3 nLocal = body.InverseTransformDirection(-normal).normalized;
        float depth = Mathf.Min(0.35f, damage * 0.003f);
        const float radius = 0.8f;
        // Push along the impact (into the body), converted to the unit-box space.
        Vector3 pushLocal = new Vector3(nLocal.x / Mathf.Max(scale.x, 0.01f), nLocal.y / Mathf.Max(scale.y, 0.01f), nLocal.z / Mathf.Max(scale.z, 0.01f)) * depth;
        // Nearest surface point of the box to the contact.
        Vector3 surface = new Vector3(Mathf.Clamp(local.x, -0.5f, 0.5f), Mathf.Clamp(local.y, -0.5f, 0.5f), Mathf.Clamp(local.z, -0.5f, 0.5f));
        bool any = false;
        for (int i = 0; i < dentVerts.Length; i++)
        {
            Vector3 d = Vector3.Scale(dentVerts[i] - surface, scale);
            float dist = d.magnitude;
            if (dist >= radius) continue;
            float f = 1f - dist / radius;
            dentVerts[i] += pushLocal * f * f;
            any = true;
        }
        if (!any) return;
        dentMesh.vertices = dentVerts;
        dentMesh.RecalculateNormals();
        dentMesh.RecalculateBounds();
    }

    // Unit box (-0.5..0.5) subdivided 6 x 3 x 10, faces welded per face (hard edges).
    static Mesh BoxTemplate()
    {
        if (boxTemplate != null) return boxTemplate;
        var v = new List<Vector3>(); var tri = new List<int>(); var uv = new List<Vector2>();
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
                    // Wound so cross(a, b) (the outward normal) is the front face.
                    tri.Add(p); tri.Add(p + 1); tri.Add(p + na + 1);
                    tri.Add(p + 1); tri.Add(p + na + 2); tri.Add(p + na + 1);
                }
        }
        const int X = 6, Y = 3, Z = 10;
        // Each face: origin corner, two edge vectors (wound so normals point out).
        Face(new Vector3(-0.5f, -0.5f, 0.5f), Vector3.right, Vector3.up, X, Y);       // +Z
        Face(new Vector3(0.5f, -0.5f, -0.5f), Vector3.left, Vector3.up, X, Y);        // -Z
        Face(new Vector3(0.5f, -0.5f, 0.5f), Vector3.back, Vector3.up, Z, Y);         // +X
        Face(new Vector3(-0.5f, -0.5f, -0.5f), Vector3.forward, Vector3.up, Z, Y);    // -X
        Face(new Vector3(-0.5f, 0.5f, 0.5f), Vector3.right, Vector3.back, X, Z);      // +Y
        Face(new Vector3(-0.5f, -0.5f, -0.5f), Vector3.right, Vector3.forward, X, Z); // -Y
        boxTemplate = new Mesh { name = "DentBox" };
        boxTemplate.SetVertices(v);
        boxTemplate.SetUVs(0, uv);
        boxTemplate.SetTriangles(tri, 0);
        boxTemplate.RecalculateNormals();
        boxTemplate.RecalculateBounds();
        return boxTemplate;
    }
}
