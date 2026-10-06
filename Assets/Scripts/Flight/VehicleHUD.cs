using UnityEngine;
using UnityEngine.InputSystem;

// The single game HUD: controls overlay (F1, shown for the first 10 s), Esc to release the
// cursor / quit, and the readout for the car the player is driving. Created on first use,
// so cars themselves never run OnGUI.
public class VehicleHUD : MonoBehaviour
{
    const float OverlayStartSeconds = 10f;
    const float QuitConfirmSeconds = 2f;

    static readonly string[] Controls =
    {
        "WASD move | Shift sprint | Space jump / cycle layer in car",
        "E hijack / exit door | hold E exit to roof | Ctrl magnet on/off",
        "Ctrl+Space free flight | Scroll camera zoom | Esc release / quit",
        "F1 toggle this help",
    };

    static VehicleHUD instance;

    float overlayUntil;
    bool overlayPinned;
    float escPressedAt = float.NegativeInfinity;

    public static void Ensure()
    {
        if (instance == null) instance = new GameObject("HUD").AddComponent<VehicleHUD>();
    }

    bool OverlayVisible => overlayPinned || Time.unscaledTime < overlayUntil;

    void Start() => overlayUntil = Time.unscaledTime + OverlayStartSeconds;

    void Update()
    {
        var kb = Keyboard.current;
        var mouse = Mouse.current;

        // Esc once releases the cursor; again within 2 s quits. Clicking back in re-locks.
        if (kb != null && kb.escapeKey.wasPressedThisFrame)
        {
            bool released = Cursor.lockState != CursorLockMode.Locked;
            if (released && Time.unscaledTime - escPressedAt <= QuitConfirmSeconds) Quit();
            else
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
                escPressedAt = Time.unscaledTime;
            }
        }
        else if (Cursor.lockState != CursorLockMode.Locked && mouse != null && mouse.leftButton.wasPressedThisFrame)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
            escPressedAt = float.NegativeInfinity;
        }

        if (kb != null && kb.f1Key.wasPressedThisFrame)
        {
            overlayPinned = !OverlayVisible;
            overlayUntil = 0f;
        }
    }

    static void Quit()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }

    void OnGUI()
    {
        float y = 20f;

        if (OverlayVisible)
        {
            const float lineH = 20f;
            GUI.Box(new Rect(10, 10, 470, Controls.Length * lineH + 12), GUIContent.none);
            for (int i = 0; i < Controls.Length; i++)
                GUI.Label(new Rect(20, 16 + i * lineH, 460, lineH), Controls[i]);
            y = 10 + Controls.Length * lineH + 22;
        }

        if (Cursor.lockState != CursorLockMode.Locked)
            GUI.Label(new Rect(Screen.width / 2f - 150, 20, 400, 25),
                      "Cursor released: click to resume, Esc again to quit");

        var car = FlyingVehicle.Driven;
        if (car == null) return;

        var mode = car.Mode;
        string where = mode == FlightMode.Free ? "none" : car.CurrentLayer.ToString();
        GUI.Label(new Rect(20, y, 500, 25), $"Mode: {mode}   Layer: {where}   Speed: {car.HudSpeed:0}");
        if (mode == FlightMode.Lane && car.path != null)
            GUI.Label(new Rect(20, y + 25, 700, 25),
                $"Magnet: {car.MagnetHold * 100f:0}%   Upper open: {car.IsLayerOpen(LaneLayer.Upper)}   " +
                $"Lower open: {car.IsLayerOpen(LaneLayer.Lower)}   No-switch: {car.InNoSwitchZone}");
        string flash = car.FlashMessage;
        if (flash != null) GUI.Label(new Rect(20, y + 50, 500, 25), flash);
        GUI.Label(new Rect(20, Screen.height - 30, 900, 25),
            "Space: cycle layer   Ctrl: magnet off/on   Ctrl+Space: free flight/lock layer   Scroll: zoom   E: exit (door)   hold E: roof");
    }
}
