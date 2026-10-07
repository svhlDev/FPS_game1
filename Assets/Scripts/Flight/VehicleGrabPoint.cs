using System.Collections.Generic;
using UnityEngine;

// Something on the back of a car the player can catch from the air (rack, bar, tow hook).
// Put it on a child transform of the FlyingVehicle; the player hangs below and behind it.
// Runs before the cars move so PreviousPosition is where it was at the end of last frame.
[DefaultExecutionOrder(-20)]
public class VehicleGrabPoint : MonoBehaviour
{
    public static readonly List<VehicleGrabPoint> All = new List<VehicleGrabPoint>();

    public FlyingVehicle Vehicle { get; private set; }
    public Vector3 PreviousPosition { get; private set; }

    void Awake() => Vehicle = GetComponentInParent<FlyingVehicle>();
    void Update() => PreviousPosition = transform.position;
    void OnEnable()
    {
        All.Add(this);
        PreviousPosition = transform.position;
    }
    void OnDisable() => All.Remove(this);

    void OnDrawGizmos()
    {
        Gizmos.color = Color.green;
        Gizmos.DrawWireSphere(transform.position, 0.3f);
    }
}
