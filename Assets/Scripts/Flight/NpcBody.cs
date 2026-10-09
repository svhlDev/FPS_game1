using UnityEngine;

// A driver thrown out of a car (placeholder person: capsule + visor). Drivers are data only
// (FlyingVehicle.hasDriver) until they're ejected; then this is spawned with the car's velocity plus a
// shove out of the door. Falls under gravity, tracking its peak height: landing after a fall of
// deadlyFall m or more kills it (lies flat, despawns after a minute). A shorter fall: it gets up and
// walks away from the player, despawning once out of view. Landing on a car's roof counts as landing
// (and it rides along). Lives on the Player layer, so cars never push it.
// Runs after the player (whose Update syncs the moved cars' colliders).
[DefaultExecutionOrder(110)]
[RequireComponent(typeof(CharacterController))]
public class NpcBody : MonoBehaviour
{
    public float gravity = -25f;
    public float deadlyFall = 10f;
    public float fleeSpeed = 3f;
    public float corpseLifetime = 60f;
    public float fleeLifetime = 60f;

    public bool Dead { get; private set; }
    public bool Grounded { get; private set; }

    CharacterController cc;
    FigureAnimator anim;
    Vector3 velocity;
    float peakY;
    bool unconscious;
    float despawnAt, outOfViewSince;
    Renderer bodyRend;
    FlyingVehicle platform; Vector3 platformLocal; float platformYaw;
    Collider groundCollider;

    // Thrown out of `car` through the door on `side` (-1 left / driver, +1 right).
    public static NpcBody Eject(FlyingVehicle car, int side, FlyingVehicle.DriverKind kind, bool unconscious = false)
    {
        Quaternion rot = car.PlatformRotation;
        Vector3 right = rot * Vector3.right;
        Vector3 pos = car.transform.position + right * side * (car.BodyHalfExtents.x + 0.7f) + rot * Vector3.forward * 0.4f;
        pos.y = car.transform.position.y - 0.6f;
        var npc = Spawn(pos, rot, kind == FlyingVehicle.DriverKind.Officer ? CharacterFigure.Role.Police : CharacterFigure.Role.Civilian);
        npc.velocity = car.Velocity + right * side * 3f;
        npc.unconscious = unconscious;
        return npc;
    }

    // The full shared figure (police blue or civilian colours), posed by FigureAnimator.
    static int spawnSeed;

    static NpcBody Spawn(Vector3 pos, Quaternion rot, CharacterFigure.Role role)
    {
        var go = new GameObject("NpcBody");
        go.transform.SetPositionAndRotation(pos, Quaternion.Euler(0f, rot.eulerAngles.y, 0f));
        int layer = LayerMask.NameToLayer("Player");
        if (layer >= 0) go.layer = layer;
        var cc = go.AddComponent<CharacterController>();
        float h = CharacterFigure.DefaultHeight;
        cc.height = h; cc.radius = 0.2f; cc.center = new Vector3(0f, h * 0.5f, 0f);
        cc.minMoveDistance = 0f;
        int seed = ++spawnSeed;
        var asset = role == CharacterFigure.Role.Police ? BodyPool.Officer(seed) : BodyPool.Civilian(seed * 7919);
        var fig = asset != null ? CharacterFigure.Assemble(go.transform, asset, role, false, true, seed)
                                : CharacterFigure.Build(go.transform, role);
        cc.height = fig.Height; cc.center = new Vector3(0f, fig.Height * 0.5f, 0f);
        var npc = go.AddComponent<NpcBody>();
        npc.anim = go.AddComponent<FigureAnimator>();
        Flammable.Add(go, Flammable.Kind.Character);
        npc.bodyRend = fig.Renderers[0];
        return npc;
    }

    void Awake()
    {
        cc = GetComponent<CharacterController>();
        peakY = transform.position.y;
    }

    void Update()
    {
        float dt = Time.deltaTime;
        if (Dead)
        {
            if (Time.time > despawnAt) Destroy(gameObject);
            return;
        }

        // Carried by a car it landed on.
        if (platform != null)
        {
            Quaternion rot = platform.PlatformRotation;
            float yaw = rot.eulerAngles.y;
            transform.Rotate(0f, Mathf.DeltaAngle(platformYaw, yaw), 0f);
            platformYaw = yaw;
            cc.Move(platform.PlatformPosition + rot * platformLocal - transform.position);
        }

        Vector3 move = Vector3.zero;
        if (Grounded && !unconscious && platform == null)
        {
            // Get away from the player.
            var player = FirstPersonController.Instance;
            Vector3 away = player != null ? transform.position - player.transform.position : transform.forward;
            away.y = 0f;
            if (away.sqrMagnitude > 0.01f)
            {
                away.Normalize();
                transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(away), 360f * dt);
                move = transform.forward * fleeSpeed;
            }

            bool seen = bodyRend.isVisible;
            if (seen) outOfViewSince = Time.time;
            else if (Time.time - outOfViewSince > 3f || Time.time > despawnAt) { Destroy(gameObject); return; }
        }

        if (!Grounded)
        {
            velocity.y += gravity * dt;
            velocity.x *= Mathf.Exp(-0.3f * dt);
            velocity.z *= Mathf.Exp(-0.3f * dt);
            peakY = Mathf.Max(peakY, transform.position.y);
        }
        else velocity = new Vector3(0f, -2f, 0f);

        groundCollider = null;
        var flags = cc.Move((Grounded ? move + velocity : velocity) * dt);
        bool was = Grounded;
        Grounded = (flags & CollisionFlags.Below) != 0;
        if (Grounded && !was) Landed();
        else if (!Grounded && was) { platform = null; peakY = transform.position.y; }
        if (platform != null)
            platformLocal = Quaternion.Inverse(platform.PlatformRotation) * (transform.position - platform.PlatformPosition);

        // Pose: limp when knocked out, flailing in the air, running away on the ground.
        anim.Velocity = Grounded ? move : velocity;
        anim.Grounded = Grounded;
        anim.LookYaw = transform.eulerAngles.y;
        anim.CurrentPose = unconscious ? FigureAnimator.Pose.Fallen : !Grounded ? FigureAnimator.Pose.Air : FigureAnimator.Pose.Normal;
    }

    void Landed()
    {
        var car = groundCollider != null ? groundCollider.GetComponentInParent<FlyingVehicle>() : null;
        if (car != null)
        {
            platform = car;
            platformYaw = car.PlatformRotation.eulerAngles.y;
            platformLocal = Quaternion.Inverse(car.PlatformRotation) * (transform.position - car.PlatformPosition);
        }
        // A deadly fall, or knocked out: lies flat (a knocked-out body just stays down, placeholder).
        if (peakY - transform.position.y >= deadlyFall || unconscious)
        {
            Dead = true;
            anim.CurrentPose = FigureAnimator.Pose.Fallen;
            anim.Velocity = Vector3.zero;
            transform.rotation = Quaternion.Euler(0f, transform.eulerAngles.y, 0f);
            despawnAt = Time.time + corpseLifetime;
            cc.enabled = false;
            if (car != null) transform.SetParent(car.transform, true); // rides along on the roof
            return;
        }
        despawnAt = Time.time + fleeLifetime;
        outOfViewSince = Time.time;
    }

    void OnControllerColliderHit(ControllerColliderHit hit)
    {
        if (hit.normal.y > 0.5f) groundCollider = hit.collider;
    }
}
