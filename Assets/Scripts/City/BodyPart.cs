using UnityEngine;

// Hit location of a figure part (location-based damage later; punches use it now).
public class BodyPart : MonoBehaviour
{
    public enum Location { Head, Body, Arm, Leg, Hand, Foot }
    public Location location;
}
