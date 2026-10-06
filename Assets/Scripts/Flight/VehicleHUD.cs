using UnityEngine;

// The one on-screen readout for the car the player is driving. Created on first use, so cars
// themselves never run OnGUI.
public class VehicleHUD : MonoBehaviour
{
    static VehicleHUD instance;

    public static void Ensure()
    {
        if (instance == null) instance = new GameObject("VehicleHUD").AddComponent<VehicleHUD>();
    }

    void OnGUI()
    {
        var car = FlyingVehicle.Driven;
        if (car == null) return;

        var mode = car.Mode;
        string where = mode == FlightMode.Free ? "none" : car.CurrentLayer.ToString();
        GUI.Label(new Rect(20, 20, 500, 25), $"Mode: {mode}   Layer: {where}   Speed: {car.HudSpeed:0}");
        if (mode == FlightMode.Lane && car.path != null)
            GUI.Label(new Rect(20, 45, 700, 25),
                $"Magnet: {car.MagnetHold * 100f:0}%   Upper open: {car.IsLayerOpen(LaneLayer.Upper)}   " +
                $"Lower open: {car.IsLayerOpen(LaneLayer.Lower)}   No-switch: {car.InNoSwitchZone}");
        string flash = car.FlashMessage;
        if (flash != null) GUI.Label(new Rect(20, 70, 500, 25), flash);
        GUI.Label(new Rect(20, Screen.height - 30, 900, 25),
            "Space: cycle layer   Ctrl: magnet off/on   Ctrl+Space: free flight/lock layer   Scroll: zoom   E: exit (door)   hold E: roof");
    }
}
