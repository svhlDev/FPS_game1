using UnityEngine;
using UnityEngine.InputSystem;

// Scroll-wheel zoom shared by the car camera and the on-foot camera, so both feel identical.
// Works as a 0..1 fraction of the owner's max distance: each notch moves `step`, and the
// camera glides to the new level over `smoothTime`.
[System.Serializable]
public class CameraZoom
{
    [Tooltip("Fraction of the max distance per scroll notch.")]
    public float step = 0.2f;
    [Tooltip("Seconds to glide to a new zoom level. 0 = snap.")]
    public float smoothTime = 0.12f;

    float target, current, vel;

    public float Current => current;

    public void HandleScroll(Mouse mouse)
    {
        if (mouse == null) return;
        float scroll = mouse.scroll.ReadValue().y;
        if (Mathf.Abs(scroll) > 0.01f) target = Mathf.Clamp01(target - Mathf.Sign(scroll) * step);
    }

    public float Tick(float dt)
    {
        current = smoothTime > 0f ? Mathf.SmoothDamp(current, target, ref vel, smoothTime, Mathf.Infinity, dt) : target;
        return current;
    }

    public void Snap(float fraction)
    {
        target = current = Mathf.Clamp01(fraction);
        vel = 0f;
    }
}
