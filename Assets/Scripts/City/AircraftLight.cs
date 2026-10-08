using UnityEngine;

// Blinking red aircraft warning light on the tallest crowns: toggles the renderer, no Light component.
// Only a handful exist per scene.
[RequireComponent(typeof(Renderer))]
public class AircraftLight : MonoBehaviour
{
    public float period = 1.6f;
    [Range(0f, 1f)] public float onFraction = 0.25f;
    public float phase;

    Renderer rend;

    void Awake() => rend = GetComponent<Renderer>();

    void Update()
    {
        bool on = Mathf.Repeat(Time.time / period + phase, 1f) < onFraction;
        if (rend.enabled != on) rend.enabled = on;
    }
}
