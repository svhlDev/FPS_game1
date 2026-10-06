using UnityEngine;

// Hovers at its post. When dispatched, chases the player's vehicle.
// Stay close for apprehendTime -> fine. Get far enough for escapeTime -> they give up.
// Pursuit speed sits below the middle lane's top speed, so the fast lane is your escape route.
public class PoliceUnit : MonoBehaviour
{
    public float pursuitSpeed = 60f;
    public float turnRate = 4f;
    public float followDistance = 6f;
    public float apprehendRange = 10f;
    public float apprehendTime = 2.5f;
    public float escapeRange = 220f;
    public float escapeTime = 5f;
    public float bobAmplitude = 0.5f;

    public FlyingVehicle Target { get; private set; }
    public bool IsPursuing => Target != null;

    Vector3 home;
    float closeTimer, farTimer;

    void Start()
    {
        home = transform.position;
        TrafficAuthority.Instance?.Register(this);
    }

    void OnDestroy() => TrafficAuthority.Instance?.Unregister(this);

    public void BeginPursuit(FlyingVehicle v) { Target = v; closeTimer = 0f; farTimer = 0f; }
    void EndPursuit() => Target = null;

    void Update()
    {
        float dt = Time.deltaTime;
        if (!IsPursuing)
        {
            Vector3 post = home + Vector3.up * Mathf.Sin(Time.time * 1.5f) * bobAmplitude;
            transform.position = Vector3.MoveTowards(transform.position, post, pursuitSpeed * 0.3f * dt);
            return;
        }
        if (!Target.IsOccupied) { EndPursuit(); return; } // driver bailed out

        Vector3 goal = Target.transform.position - Target.transform.forward * followDistance + Vector3.up * 2f;
        Vector3 to = goal - transform.position;
        transform.position = Vector3.MoveTowards(transform.position, goal, pursuitSpeed * dt);
        if (to.sqrMagnitude > 0.01f)
            transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(to), turnRate * dt);

        float dist = Vector3.Distance(transform.position, Target.transform.position);
        closeTimer = dist < apprehendRange ? closeTimer + dt : 0f;
        farTimer = dist > escapeRange ? farTimer + dt : 0f;

        if (closeTimer >= apprehendTime) { TrafficAuthority.Instance.IssueFine(Target); EndPursuit(); }
        else if (farTimer >= escapeTime) { TrafficAuthority.Instance.Escaped(); EndPursuit(); }
    }
}
