using System.Collections.Generic;
using UnityEngine;

// One per scene. Owns the altitude grid, watches the player's vehicle, dispatches police on violations.
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
    [Tooltip("Seconds of free flight before it counts as a violation.")]
    public float freeFlightGraceSeconds = 8f;
    [Tooltip("Police must be this close to notice a violation.")]
    public float detectionRadius = 90f;
    public int fineAmount = 500;
    public int credits = 5000;

    readonly List<PoliceUnit> units = new List<PoliceUnit>();
    FlyingVehicle player;
    float freeTimer;
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

    public void Register(PoliceUnit u) { if (!units.Contains(u)) units.Add(u); }
    public void Unregister(PoliceUnit u) => units.Remove(u);
    public void SetPlayerVehicle(FlyingVehicle v) { player = v; freeTimer = 0f; }

    void Update()
    {
        if (player == null) return;
        if (player.Mode == FlightMode.Free)
        {
            freeTimer += Time.deltaTime;
            if (freeTimer > freeFlightGraceSeconds) ReportViolation(player, "Unregistered free flight");
        }
        else freeTimer = Mathf.Max(0f, freeTimer - Time.deltaTime);
    }

    public void ReportViolation(FlyingVehicle v, string reason)
    {
        if (v == null || units.Exists(u => u.Target == v)) return;

        PoliceUnit nearest = null; float best = detectionRadius;
        foreach (var u in units)
        {
            if (u.IsPursuing) continue;
            float d = Vector3.Distance(u.transform.position, v.transform.position);
            if (d < best) { best = d; nearest = u; }
        }
        if (nearest != null)
        {
            nearest.BeginPursuit(v);
            Show($"{reason}: POLICE IN PURSUIT");
        }
    }

    public void IssueFine(FlyingVehicle v)
    {
        credits -= fineAmount;
        freeTimer = 0f;
        v.ForceStop();
        Show($"Apprehended. Fined {fineAmount} cr");
    }

    public void Escaped() => Show("You lost them");

    void Show(string msg) { message = msg; messageUntil = Time.time + 3f; }

    void OnGUI()
    {
        float x = Screen.width - 320;
        GUI.Label(new Rect(x, 20, 300, 25), $"Credits: {credits}");
        if (player != null && units.Exists(u => u.Target == player))
            GUI.Label(new Rect(x, 45, 300, 25), "*** PURSUIT ***");
        if (player != null && player.Mode == FlightMode.Free)
            GUI.Label(new Rect(x, 70, 300, 25), $"Unregistered: {freeTimer:0.0} / {freeFlightGraceSeconds:0}s");
        if (Time.time < messageUntil)
            GUI.Label(new Rect(Screen.width / 2f - 200, 60, 400, 25), message);
    }
}
