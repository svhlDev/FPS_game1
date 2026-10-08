using UnityEngine;

// Head and tail lights for a FlyingVehicle: emissive boxes built by the builders, no Light components.
// Tail lights switch to the brake material while the car decelerates hard; a parked car's lights are
// off until someone gets in. Materials are swapped (sharedMaterial) only when the state changes, so
// the SRP Batcher keeps batching them.
[DefaultExecutionOrder(10)] // after the vehicles have moved this frame
[RequireComponent(typeof(FlyingVehicle))]
public class CarLights : MonoBehaviour
{
    public Renderer[] headlights;
    public Renderer[] taillights;
    public Material headOn, tailOn, brakeOn, off;
    [Tooltip("Deceleration (m/s^2) that turns the brake lights on.")]
    public float brakeDecel = 3f;
    [Tooltip("Brake lights go off again once deceleration drops below this (avoids flicker at the threshold).")]
    public float releaseDecel = 1.5f;

    enum State { Unset, Off, Driving, Braking }

    FlyingVehicle car;
    float lastSpeed;
    State state = State.Unset;

    void Awake() => car = GetComponent<FlyingVehicle>();

    void Update()
    {
        float dt = Time.deltaTime;
        float speed = Vector3.Dot(car.Velocity, transform.forward);
        float decel = dt > 0f ? (lastSpeed - speed) / dt : 0f;
        lastSpeed = speed;

        State next;
        if (car.IsParked) next = State.Off;
        else if (state == State.Braking) next = decel > releaseDecel ? State.Braking : State.Driving;
        else next = decel > brakeDecel ? State.Braking : State.Driving;
        if (next == state) return;

        state = next;
        Set(headlights, next == State.Off ? off : headOn);
        Set(taillights, next == State.Off ? off : next == State.Braking ? brakeOn : tailOn);
    }

    static void Set(Renderer[] renderers, Material m)
    {
        foreach (var r in renderers) if (r != null) r.sharedMaterial = m;
    }
}
