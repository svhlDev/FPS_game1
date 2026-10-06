using System.Collections.Generic;
using UnityEngine;

// One per scene. Owns layer altitudes, watches the player's vehicle, dispatches police on violations.
public class TrafficAuthority : MonoBehaviour
{
    public static TrafficAuthority Instance { get; private set; }

    [Tooltip("World altitude of Lower, Middle, Upper layers.")]
    public float[] layerAltitudes = { 20f, 40f, 60f };
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

    public static float LayerAltitude(LaneLayer layer) =>
        Instance != null ? Instance.layerAltitudes[(int)layer] : 20f + 20f * (int)layer;

    public static LaneLayer NearestLayer(float y)
    {
        LaneLayer best = LaneLayer.Middle; float bestDiff = float.MaxValue;
        for (int i = 0; i < 3; i++)
        {
            float diff = Mathf.Abs(LayerAltitude((LaneLayer)i) - y);
            if (diff < bestDiff) { bestDiff = diff; best = (LaneLayer)i; }
        }
        return best;
    }

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
