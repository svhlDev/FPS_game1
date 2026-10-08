using UnityEngine;

// Guiding-light settings for a LanePath. The lights themselves are baked at edit time by the
// builders (LaneLightBaker): one combined static mesh per colour, placed next to the path.
// Middle lane = cyan, lanes above = yellow, below = magenta, no-switch sections = red.
[RequireComponent(typeof(LanePath))]
public class LaneLights : MonoBehaviour
{
    public float spacing = 15f;
    public float size = 0.6f;
    public Color middleColor = Color.cyan;
    public Color upperColor = Color.yellow;
    public Color lowerColor = Color.magenta;
    public Color noSwitchColor = Color.red;
}
