using UnityEngine;

// The car's health gauge, in the world on the dashboard: 10 segments, green -> amber -> red, flashing
// when critical. Only for the driven car in first person; there is no health bar in third person (you
// read the car by looking at it). Parented to the cockpit, so it moves with the car.
public class DashboardGauge : MonoBehaviour
{
    const int Segments = 10;
    Renderer[] segs;
    FlyingVehicle car;
    static Material green, amber, red, off;

    public static void Ensure(FlyingVehicle car)
    {
        if (car.GetComponent<DashboardGauge>() == null) car.gameObject.AddComponent<DashboardGauge>();
    }

    void Start()
    {
        car = GetComponent<FlyingVehicle>();
        Transform anchor = car.cockpitAnchor != null ? car.cockpitAnchor : transform;
        var root = new GameObject("DashboardGauge").transform;
        root.SetParent(anchor, false);
        root.localPosition = new Vector3(0f, -0.32f, 0.65f);
        root.localRotation = Quaternion.Euler(-25f, 0f, 0f); // tilted toward the driver
        if (green == null)
        {
            green = Mat(new Color(0.2f, 1f, 0.35f) * 2f); amber = Mat(new Color(1f, 0.65f, 0.1f) * 2f);
            red = Mat(new Color(1f, 0.1f, 0.06f) * 2.5f); off = Mat(new Color(0.06f, 0.06f, 0.07f));
        }
        segs = new Renderer[Segments];
        for (int i = 0; i < Segments; i++)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "Segment";
            Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(root, false);
            go.transform.localPosition = new Vector3((i - (Segments - 1) * 0.5f) * 0.045f, 0f, 0f);
            go.transform.localScale = new Vector3(0.035f, 0.025f, 0.006f);
            var r = go.GetComponent<Renderer>();
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.sharedMaterial = off;
            go.layer = gameObject.layer;
            segs[i] = r;
        }
    }

    static Material Mat(Color c)
    {
        var shader = Shader.Find("Universal Render Pipeline/Unlit");
        var m = new Material(shader);
        m.SetColor("_BaseColor", c);
        return m;
    }

    void LateUpdate()
    {
        if (segs == null) return;
        bool show = FlyingVehicle.Driven == car && car.CameraZoom < 0.05f && car.Health != null;
        for (int i = 0; i < Segments; i++) segs[i].enabled = show;
        if (!show) return;
        float f = car.Health.Fraction;
        int lit = Mathf.CeilToInt(f * Segments);
        bool flashOff = car.Health.Critical && Mathf.Repeat(Time.time * 4f, 1f) < 0.5f;
        var on = f > 0.5f ? green : f > 0.25f ? amber : red;
        for (int i = 0; i < Segments; i++) segs[i].sharedMaterial = i < lit && !flashOff ? on : off;
    }
}
