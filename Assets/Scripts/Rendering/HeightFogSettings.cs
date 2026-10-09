using UnityEngine;

// Per-scene settings for the height fog / smog pass (HeightFogFeature). The pass only runs in scenes
// that have one of these, so other scenes are unaffected even though the feature sits on the renderer.
[ExecuteAlways]
public class HeightFogSettings : MonoBehaviour
{
    public static HeightFogSettings Active { get; private set; }

    [Tooltip("Smog top height (m): full density below, exponential falloff above.")]
    public float smogTop = 120f;
    [Tooltip("Extra smog top height per district (m), from a smooth low-frequency noise over XZ.")]
    public float smogTopVariation = 120f;
    [Tooltip("Height (m) over which the main term falls by 1/e above the smog top.")]
    public float smogFalloff = 120f;
    [Tooltip("Second, longer falloff (m) that softens the transition into clear air.")]
    public float smogFalloffLong = 400f;
    [Tooltip("Share of the long falloff term (0-1).")]
    [Range(0f, 1f)] public float smogLongWeight = 0.3f;
    [Tooltip("Density (per m) at and below the smog top.")]
    public float smogDensity = 0.012f;
    [Tooltip("Size of a district noise cell (m).")]
    public float districtCellSize = 600f;
    [Tooltip("Sky pixels are treated as this far away, so the horizon sinks into smog.")]
    public float skyDistance = 1200f;
    [Tooltip("Smog colour near the ground (night: dark blue-green; TimeOfDay drives it at runtime).")]
    public Color lowColor = new Color(0.015f, 0.045f, 0.05f);
    [Tooltip("Smog colour near its top (night: a little lighter blue-green, no warm city glow).")]
    public Color highColor = new Color(0.03f, 0.08f, 0.085f);

    void OnEnable() => Active = this;
    void OnDisable() { if (Active == this) Active = null; }
}
