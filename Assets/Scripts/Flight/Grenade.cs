using System.Collections.Generic;
using UnityEngine;

// A thrown grenade: a small dark cylinder (0.06 m across, 0.09 m tall) with a glowing orange band. A
// rigidbody with a sphere collider that bounces off surfaces and cars (bounciness 0.3) and rides a car
// it comes to rest on (CarCarry). Explodes fuse s after release: Explosion.At(radius 6, damage 150,
// impulse 14) and 12-18 incendiary fire chunks (FireSystem patches burn their 2 minutes). Thrown by the
// player (PlayerGrenades); a blast in sight of police is wanted 2 and lethal force.
public class Grenade : MonoBehaviour
{
    public const float Diameter = 0.06f, Height = 0.09f;
    public const float Fuse = 3f, Radius = 6f, Damage = 150f, Impulse = 14f, Bounciness = 0.3f;

    public float ExplodeAt { get; private set; }
    public bool ByPlayer { get; private set; }
    public Rigidbody Body { get; private set; }
    readonly CarCarry carry = new CarCarry();
    readonly List<Rigidbody> bodyList = new List<Rigidbody>(1);
    static Material shell, band;
    static PhysicsMaterial bouncy;
    static readonly List<Grenade> live = new List<Grenade>();
    public static IReadOnlyList<Grenade> Live => live;
    public static int Exploded;

    // The model alone (trunk contents, the hand): cylinder plus band, no colliders.
    public static Transform BuildModel(Transform parent, int layer)
    {
        if (shell == null)
        {
            shell = CharacterFigure.Mat(new Color(0.07f, 0.075f, 0.08f));
            band = new Material(Shader.Find("Universal Render Pipeline/Unlit")) { name = "Grenade band", enableInstancing = true };
            band.SetColor("_BaseColor", new Color(1f, 0.45f, 0.08f) * 3f);
        }
        var root = new GameObject("Grenade").transform;
        root.SetParent(parent, false);
        root.gameObject.layer = layer;
        Part(root, new Vector3(Diameter, Height * 0.5f, Diameter), shell, layer);          // cylinder primitive is 2 tall
        Part(root, new Vector3(Diameter * 1.06f, Height * 0.09f, Diameter * 1.06f), band, layer);
        return root;
    }

    static void Part(Transform root, Vector3 scale, Material m, int layer)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        Destroy(go.GetComponent<Collider>());
        go.layer = layer;
        go.transform.SetParent(root, false);
        go.transform.localScale = scale;
        var r = go.GetComponent<Renderer>();
        r.sharedMaterial = m;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
    }

    public static Grenade Throw(Vector3 pos, Vector3 velocity, Collider ignore, bool byPlayer)
    {
        var t = BuildModel(null, 0);
        t.position = pos;
        t.rotation = Random.rotation;
        var g = t.gameObject.AddComponent<Grenade>();
        if (bouncy == null)
            bouncy = new PhysicsMaterial("Grenade") { bounciness = Bounciness, bounceCombine = PhysicsMaterialCombine.Maximum,
                                                      dynamicFriction = 0.5f, staticFriction = 0.6f };
        var col = t.gameObject.AddComponent<SphereCollider>();
        col.radius = Height * 0.5f;
        col.sharedMaterial = bouncy;
        if (ignore != null) Physics.IgnoreCollision(col, ignore);
        var rb = t.gameObject.AddComponent<Rigidbody>();
        rb.mass = 0.4f;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        rb.angularDamping = 0.8f;
        rb.linearVelocity = velocity;
        rb.angularVelocity = Random.insideUnitSphere * 10f;
        g.Body = rb;
        g.bodyList.Add(rb);
        g.ExplodeAt = Time.time + Fuse;
        g.ByPlayer = byPlayer;
        return g;
    }

    void OnEnable() => live.Add(this);
    void OnDisable() => live.Remove(this);

    void FixedUpdate() { if (Body != null) carry.Step(bodyList, Body.position, 0.12f); }

    void Update()
    {
        if (Time.time >= ExplodeAt) Explode();
    }

    public void Explode()
    {
        Vector3 p = transform.position;
        Vector3 v = Body != null ? Body.linearVelocity : Vector3.zero;
        Destroy(gameObject);
        Exploded++;
        Explosion.At(p, v, null, Radius, Damage, Impulse, Damage, 12, 18, ByPlayer);
        if (ByPlayer) PoliceDispatch.Instance?.ReportExplosion(p);
    }
}
