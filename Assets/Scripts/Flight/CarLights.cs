using UnityEngine;

// Head and tail lights for a FlyingVehicle: emissive boxes built by the builders, plus (Graphics Phase 1)
// one real unshadowed spot ahead and one small red point behind, created at Start and switched on and off
// by the CityLights budget (only the nearest cars get them). Two states only: on, or off while the car is
// parked. No per-frame work: FlyingVehicle calls SetOn when the car parks or someone gets in. Materials
// are swapped (sharedMaterial) only on that change, so the SRP Batcher keeps batching them;
// TrafficSystem reads IsOn for instanced drawing.
[RequireComponent(typeof(FlyingVehicle))]
public class CarLights : MonoBehaviour
{
    public Renderer[] headlights;
    public Renderer[] taillights;
    public Material headOn, tailOn, off;

    [Header("Real lights")]
    public bool realLights = true;
    public float headIntensity = 120f, headRange = 35f, headAngle = 60f;
    public float tailIntensity = 1.2f, tailRange = 5f;
    Light headBeam, tailGlow;

    public bool IsOn { get; private set; } = true;
    bool applied;

    public void SetOn(bool on)
    {
        if (applied && on == IsOn) return;
        applied = true;
        IsOn = on;
        Set(headlights, on ? headOn : off);
        Set(taillights, on ? tailOn : off);
        if (headBeam != null) headBeam.gameObject.SetActive(on);
        if (tailGlow != null) tailGlow.gameObject.SetActive(on);
    }

    void Start()
    {
        if (!realLights || headlights == null || headlights.Length == 0 || headlights[0] == null) return;
        Vector3 front = Vector3.zero, back = Vector3.zero;
        foreach (var r in headlights) front += r.transform.localPosition / headlights.Length;
        if (taillights != null && taillights.Length > 0) foreach (var r in taillights) back += r.transform.localPosition / taillights.Length;
        headBeam = MakeLight("HeadBeam", front + new Vector3(0f, 0f, 0.2f), LightType.Spot, new Color(1f, 0.92f, 0.8f), headIntensity, headRange);
        headBeam.spotAngle = headAngle; headBeam.innerSpotAngle = headAngle * 0.4f;
        headBeam.transform.localRotation = Quaternion.Euler(8f, 0f, 0f);
        tailGlow = MakeLight("TailGlow", back - new Vector3(0f, 0f, 1f), LightType.Point, new Color(1f, 0.08f, 0.05f), tailIntensity, tailRange);
        CityLights.Register(headBeam, CityLights.Kind.Headlight);
        CityLights.Register(tailGlow, CityLights.Kind.Taillight);
        headBeam.gameObject.SetActive(IsOn);
        tailGlow.gameObject.SetActive(IsOn);
    }

    void OnDestroy()
    {
        CityLights.Unregister(headBeam);
        CityLights.Unregister(tailGlow);
    }

    Light MakeLight(string name, Vector3 local, LightType type, Color c, float intensity, float range)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);
        go.transform.localPosition = local;
        var l = go.AddComponent<Light>();
        l.type = type; l.color = c; l.intensity = intensity; l.range = range;
        l.shadows = LightShadows.None;
        l.renderMode = LightRenderMode.ForcePixel;
        return l;
    }

    static void Set(Renderer[] renderers, Material m)
    {
        if (renderers == null || m == null) return;
        foreach (var r in renderers) if (r != null) r.sharedMaterial = m;
    }
}
