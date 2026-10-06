using System.Collections.Generic;
using UnityEngine;

// Something on the back of a car the player can catch from the air (rack, bar, tow hook).
// Put it on a child transform of the FlyingVehicle; the player hangs below and behind it.
public class VehicleGrabPoint : MonoBehaviour
{
    public static readonly List<VehicleGrabPoint> All = new List<VehicleGrabPoint>();

    public FlyingVehicle Vehicle { get; private set; }

    void Awake() => Vehicle = GetComponentInParent<FlyingVehicle>();
    void OnEnable() => All.Add(this);
    void OnDisable() => All.Remove(this);

    void OnDrawGizmos()
    {
        Gizmos.color = Color.green;
        Gizmos.DrawWireSphere(transform.position, 0.3f);
    }
}
