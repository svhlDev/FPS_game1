using UnityEngine;

// Head and tail lights for a FlyingVehicle: emissive boxes built by the builders, no Light components.
// Two states only: on, or off while the car is parked. No per-frame work: FlyingVehicle calls SetOn
// when the car parks or someone gets in. Materials are swapped (sharedMaterial) only on that change,
// so the SRP Batcher keeps batching them; TrafficSystem reads IsOn for instanced drawing.
[RequireComponent(typeof(FlyingVehicle))]
public class CarLights : MonoBehaviour
{
    public Renderer[] headlights;
    public Renderer[] taillights;
    public Material headOn, tailOn, off;

    public bool IsOn { get; private set; } = true;
    bool applied;

    public void SetOn(bool on)
    {
        if (applied && on == IsOn) return;
        applied = true;
        IsOn = on;
        Set(headlights, on ? headOn : off);
        Set(taillights, on ? tailOn : off);
    }

    static void Set(Renderer[] renderers, Material m)
    {
        if (renderers == null || m == null) return;
        foreach (var r in renderers) if (r != null) r.sharedMaterial = m;
    }
}
