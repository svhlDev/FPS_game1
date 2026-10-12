using UnityEngine;

// Face Fix Part 9: hair cards only near the camera; farther away the hair shell under them stands in.
public class HairLOD : MonoBehaviour
{
    public Renderer cards;
    public float distance = 20f;

    void LateUpdate()
    {
        if (cards == null) return;
        var cam = Camera.main;
        bool near = cam == null || (cam.transform.position - transform.position).sqrMagnitude < distance * distance;
        if (cards.enabled != near) cards.enabled = near;
    }
}
