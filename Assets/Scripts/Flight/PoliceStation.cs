using UnityEngine;

// Where arrested players are taken: a landing pad on the street and the station door (built by
// SkyAvenueBuilder). Transport cars land on the pad; Busted puts the player outside the door.
public class PoliceStation : MonoBehaviour
{
    [Tooltip("Centre of the landing pad's top surface.")]
    public Transform pad;
    [Tooltip("Where the player stands when released (facing out).")]
    public Transform door;

    static PoliceStation cached;
    static bool searched;

    public static PoliceStation Find()
    {
        if (cached == null && !searched)
        {
            searched = true;
            cached = FindAnyObjectByType<PoliceStation>();
        }
        return cached;
    }

    void OnEnable() { cached = this; searched = true; }
    void OnDisable() { if (cached == this) { cached = null; searched = false; } }
}
