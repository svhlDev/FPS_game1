using UnityEngine;

// Per-scene settings for the height fog / smog pass (HeightFogFeature). The pass only runs in scenes
// that have one of these, so other scenes are unaffected even though the feature sits on the renderer.
[ExecuteAlways]
public class HeightFogSettings : MonoBehaviour
{
    public static HeightFogSettings Active { get; private set; }

    [Tooltip("Smog top height (m): full density below, exponential falloff above.")]
    public float smogTop = 150f;
    [Tooltip("Extra smog top height per district (m), from a smooth low-frequency noise over XZ.")]
    public float smogTopVariation = 230f;
    [Tooltip("Height (m) over which density falls by 1/e above the smog top.")]
    public float smogFalloff = 40f;
    [Tooltip("Density (per m) at and below the smog top.")]
    public float smogDensity = 0.012f;
    [Tooltip("Size of a district noise cell (m).")]
    public float districtCellSize = 600f;
    [Tooltip("Sky pixels are treated as this far away, so the horizon sinks into smog.")]
    public float skyDistance = 3000f;
    [Tooltip("Smog colour near the ground.")]
    public Color lowColor = new Color(0.08f, 0.05f, 0.07f);
    [Tooltip("Smog colour near its top: warmer and brighter, the city glow lit from below.")]
    public Color highColor = new Color(0.42f, 0.24f, 0.2f);

    void OnEnable() => Active = this;
    void OnDisable() { if (Active == this) Active = null; }
}
