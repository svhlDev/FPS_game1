using UnityEngine;
using UnityEngine.InputSystem;

// E on things around you, on foot (before FirstPersonController's own E, which it then skips), first
// match wins:
//   1. a trunk opening the camera ray goes into, within 2.5 m of the camera, the car slower than 1 m/s
//      (from the ground, another car's roof, anywhere) -> "E open trunk" / "E close trunk"; open and
//      looking right at an item in it -> "E pick up grenade"
//   2. an item in an open trunk within 2 m, looked at -> "E pick up grenade"
//   3. a dropped T-gun within 2 m, looked at -> "E pick up laser" (only without a gun of your own)
//   4. otherwise FirstPersonController's E (get into / take a car); the prompt says which.
// "Looked at": within lookAngle degrees of the centre of the view.
[DefaultExecutionOrder(95)]
[RequireComponent(typeof(FirstPersonController))]
public class PlayerInteract : MonoBehaviour
{
    public float pickupRange = 2f;
    public float lookAngle = 14f;
    
    enum Kind { None, Gun, TrunkItem, Trunk }
    Kind kind;
    GunPickup gun;
    CarTrunk trunk;
    Transform item;
    public string Prompt { get; private set; }

    FirstPersonController fpc;
    PlayerWeapon weapon;
    PlayerGrenades grenades;
    static readonly Collider[] near = new Collider[32];
    int trafficMask;

    void Awake()
    {
        fpc = GetComponent<FirstPersonController>();
        weapon = GetComponent<PlayerWeapon>();
        grenades = GetComponent<PlayerGrenades>();
        int traffic = LayerMask.NameToLayer("Traffic");
        trafficMask = traffic >= 0 ? 1 << traffic : Physics.DefaultRaycastLayers;
    }

    void Update()
    {
        Find();
        var kb = Keyboard.current;
        if (kind == Kind.None || kb == null || !kb.eKey.wasPressedThisFrame) return;
        fpc.BlockInteractThisFrame(); // ours, not a car to get into
        Use();
    }

    // Test hook: what E would do now ("" if nothing), and doing it.
    public string DebugPrompt() { Find(); return Prompt ?? ""; }
    public bool DebugUse() { Find(); if (kind == Kind.None) return false; Use(); return true; }

    void Use()
    {
        switch (kind)
        {
            case Kind.Gun:
                if (weapon != null) weapon.Give(gun);
                break;
            case Kind.TrunkItem:
                if (grenades != null && grenades.Add()) { trunk.TakeItem(item); fpc.Flash("Grenade  (hold G to throw)"); }
                else fpc.Flash("Can't carry more grenades");
                break;
            case Kind.Trunk:
                if (trunk.IsOpen) trunk.Close();
                else
                {
                    trunk.Open();
                    if (trunk.Police) PoliceDispatch.Instance?.ReportTrunkTheft(trunk.OpeningCentre);
                }
                break;
        }
        Find();
    }

    void Find()
    {
        kind = Kind.None; gun = null; trunk = null; item = null; Prompt = null;
        if (!fpc.isActiveAndEnabled || fpc.Restrained || fpc.Stunned || fpc.Frozen || fpc.IsHanging || fpc.playerCamera == null) return;
        var cam = fpc.playerCamera.transform;
        Vector3 eye = fpc.cameraRoot.position;
        var ray = new Ray(cam.position, cam.forward);
        float Angle(Vector3 p) => Vector3.Angle(cam.forward, p - cam.position);

        // 1. A trunk the camera ray goes into (wherever we stand), within trunkRange of the camera. Open
        //    and looking straight at an item in it: that item.
        int n = Physics.OverlapSphereNonAlloc(cam.position, trunkRange + 3f, near, trafficMask, QueryTriggerInteraction.Ignore);
        float bestDist = float.MaxValue;
        for (int i = 0; i < n; i++)
        {
            var car = near[i].GetComponentInParent<FlyingVehicle>();
            var t = car != null ? car.Trunk : null;
            if (t == null || !t.CanOpen || !t.RayHitsOpening(ray, trunkRange, out float d) || d >= bestDist) continue;
            bestDist = d; kind = Kind.Trunk; trunk = t;
        }
        if (kind == Kind.Trunk && trunk.IsOpen && trunk.OpenAmount > 0.6f)
        {
            float best = lookAngle;
            foreach (var it in trunk.Items)
            {
                if (it == null) continue;
                float a = Angle(it.position);
                if (a < best) { best = a; kind = Kind.TrunkItem; item = it; }
            }
        }

        // 2. An item in an open trunk, looked at from elsewhere.
        if (kind == Kind.None)
        {
            float best = lookAngle;
            for (int i = 0; i < n; i++)
            {
                var car = near[i].GetComponentInParent<FlyingVehicle>();
                var t = car != null ? car.Trunk : null;
                if (t == null || !t.IsOpen || t.OpenAmount <= 0.6f) continue;
                foreach (var it in t.Items)
                {
                    if (it == null || (it.position - eye).sqrMagnitude > pickupRange * pickupRange) continue;
                    float a = Angle(it.position);
                    if (a < best) { best = a; kind = Kind.TrunkItem; trunk = t; item = it; }
                }
            }
        }

        // 3. A dropped gun (only without one of our own).
        if (kind == Kind.None && weapon != null && !weapon.HasGun)
        {
            float best = lookAngle;
            foreach (var g in GunPickup.All)
            {
                if (g == null) continue;
                Vector3 c = g.Center;
                if ((c - eye).sqrMagnitude > pickupRange * pickupRange) continue;
                float a = Angle(c);
                if (a < best) { best = a; kind = Kind.Gun; gun = g; }
            }
        }

        Prompt = kind switch
        {
            Kind.Gun => "E  pick up laser",
            Kind.TrunkItem => "E  pick up grenade",
            Kind.Trunk => trunk.IsOpen ? "E  close trunk" : "E  open trunk",
            _ => CarPrompt(cam),
        };
    }

    // 4. Nothing of ours: what FirstPersonController's E will do (the roof car, or the car looked at).
    string CarPrompt(Transform cam)
    {
        var roof = fpc.Platform;
        if (roof != null) return roof.IsOccupied ? null : "E  take car";
        int mask = Physics.DefaultRaycastLayers & ~(1 << Mathf.Max(0, LayerMask.NameToLayer("Player")));
        float back = Vector3.Distance(cam.position, fpc.cameraRoot.position);
        if (!Physics.Raycast(cam.position, cam.forward, out var hit, fpc.interactRange + back, mask, QueryTriggerInteraction.Ignore)) return null;
        if (Vector3.Distance(hit.point, fpc.cameraRoot.position) > fpc.interactRange) return null;
        var car = hit.collider.GetComponentInParent<FlyingVehicle>();
        return car != null && !car.IsOccupied ? (car.hasDriver ? "E  take car" : "E  get in") : null;
    }

    public float trunkRange = CarTrunk.OpenRange;

    void OnGUI()
    {
        if (Prompt == null) return;
        float cx = Screen.width * 0.5f, cy = Screen.height * 0.5f;
        var prev = GUI.color;
        GUI.color = new Color(1f, 1f, 1f, 0.95f);
        GUI.Label(new Rect(cx - 60f, cy + 40f, 240f, 25f), Prompt);
        GUI.color = prev;
    }
}
