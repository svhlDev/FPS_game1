using UnityEngine;

// One per scene. Owns the altitude grid and the player's credits (police: see PoliceDispatch).
// Altitude grid: layer n has its floor at n * layerSpacing (n = 0 is the ground) and a ride height
// of floor + hoverHeight, where ride height is the car collider's UNDERSIDE. All geometry snaps to
// floors, so hovering over a rooftop is simply being in that roof's layer.
public class TrafficAuthority : MonoBehaviour
{
    public static TrafficAuthority Instance { get; private set; }

    [Tooltip("Height of one layer (m).")]
    public float layerSpacing = DefaultSpacing;
    [Tooltip("Gap between a layer's floor and a car's underside riding in it (m).")]
    public float hoverHeight = DefaultHover;
    public int maxLayer = DefaultMaxLayer;

    const float DefaultSpacing = 5f, DefaultHover = 0.5f;
    const int DefaultMaxLayer = 60;
    public int credits = 5000;

    string message; float messageUntil;

    void Awake() => Instance = this;

    // Defaults apply when there's no authority (edit mode, builders).
    public static float Spacing => Current != null ? Current.layerSpacing : DefaultSpacing;
    public static float Hover => Current != null ? Current.hoverHeight : DefaultHover;
    public static int MaxLayer => Current != null ? Current.maxLayer : DefaultMaxLayer;

    // Instance, or (before its Awake has run, or in edit mode) the scene's authority found by search.
    // Lanes rebuild in OnEnable, which can run before this object's Awake on scene load.
    static TrafficAuthority Current
    {
        get
        {
            if (Instance != null) return Instance;
            if (lookup == null && Time.frameCount != lookupFrame)
            {
                lookupFrame = Time.frameCount; // at most one search per frame when there is none
                lookup = FindAnyObjectByType<TrafficAuthority>();
            }
            return lookup;
        }
    }
    static TrafficAuthority lookup;
    static int lookupFrame = -1;

    public static float FloorHeight(int n) => n * Spacing;
    // Height of a riding car's underside in layer n.
    public static float RideHeight(int n) => FloorHeight(n) + Hover;
    // Layer whose ride height is closest to the given underside height.
    public static int NearestLayer(float undersideY) =>
        Mathf.Clamp(Mathf.RoundToInt((undersideY - Hover) / Spacing), 0, MaxLayer);

    public void Fine(int amount)
    {
        credits -= amount;
        Show($"Fined {amount} cr");
    }

    void Show(string msg) { message = msg; messageUntil = Time.time + 3f; }

    void OnGUI()
    {
        float x = Screen.width - 320;
        GUI.Label(new Rect(x, 20, 300, 25), $"Credits: {credits}");
        if (Time.time < messageUntil)
            GUI.Label(new Rect(Screen.width / 2f - 200, 60, 400, 25), message);
    }
}
