using System.Collections.Generic;
using UnityEngine;

// A T-gun lying in the world (dropped by a dead officer). A rigidbody with a small box collider, thrown
// with the hand's velocity; the mode strips keep glowing, so it can be found at night. Rides a car it
// lands on (CarCarry). The player picks it up with E (PlayerInteract -> PlayerWeapon.Give). Despawns
// once older than 5 minutes and out of view for 2 s.
public class GunPickup : MonoBehaviour
{
    static readonly List<GunPickup> all = new List<GunPickup>();
    public static IReadOnlyList<GunPickup> All => all;

    public Weapon Gun { get; private set; }
    public Rigidbody Body { get; private set; }
    public float Born { get; private set; }
    readonly List<Renderer> renderers = new List<Renderer>();
    readonly OffscreenTimer offscreen = new OffscreenTimer();
    readonly CarCarry carry = new CarCarry();
    readonly List<Rigidbody> bodyList = new List<Rigidbody>(1);

    public static GunPickup Drop(Weapon gun, Vector3 velocity)
    {
        if (gun == null) return null;
        gun.transform.SetParent(null, true);
        var p = gun.gameObject.AddComponent<GunPickup>();
        p.Gun = gun;
        p.Born = Time.time;
        var box = gun.gameObject.AddComponent<BoxCollider>();
        box.center = new Vector3(0f, 0.015f, 0f);
        box.size = new Vector3(0.04f, 0.12f, 0.22f);
        var rb = gun.gameObject.AddComponent<Rigidbody>();
        rb.mass = 1.2f;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
        rb.linearVelocity = velocity;
        rb.angularVelocity = Random.insideUnitSphere * 6f;
        p.Body = rb;
        p.bodyList.Add(rb);
        p.renderers.AddRange(gun.GetComponentsInChildren<Renderer>());
        return p;
    }

    void OnEnable() => all.Add(this);
    void OnDisable() => all.Remove(this);

    // Taken by the player: back to a plain gun (no physics).
    public Weapon Take()
    {
        var gun = Gun;
        Destroy(GetComponent<BoxCollider>());
        Destroy(Body);
        Destroy(this);
        all.Remove(this);
        return gun;
    }

    // Centre of the gun (for aiming the pickup).
    public Vector3 Center => Gun != null ? Gun.transform.TransformPoint(new Vector3(0f, 0.02f, 0f)) : transform.position;

    void FixedUpdate() { if (Body != null) carry.Step(bodyList, Body.position, 0.25f); }

    void Update()
    {
        offscreen.Tick(renderers);
        if (Time.time - Born > OffscreenTimer.Lifetime && offscreen.HiddenFor >= OffscreenTimer.HiddenTime) Destroy(gameObject);
    }
}
