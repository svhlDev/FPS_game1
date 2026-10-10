using UnityEngine;
using UnityEngine.InputSystem;

// E on things lying around, on foot (before FirstPersonController's own E, which it then skips):
//   a dropped T-gun within 2 m that you look at   -> "E pick up laser" (only without a gun of your own)
//   an item in an open trunk within 2 m, looked at -> "E pick up grenade"
//   a car's rear within 1.5 m, looked at, the car slower than 1 m/s -> "E open trunk" / "E close trunk"
// The looked-at choice is the one closest to the centre of the view (within lookAngle degrees).
[DefaultExecutionOrder(95)]
[RequireComponent(typeof(FirstPersonController))]
public class PlayerInteract : MonoBehaviour
{
    public float pickupRange = 2f;
    public float lookAngle = 14f;
    public float trunkLookAngle = 35f;

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
        float best = float.MaxValue;

        float Angle(Vector3 p) => Vector3.Angle(cam.forward, p - cam.position);

        if (weapon != null && !weapon.HasGun)
            foreach (var g in GunPickup.All)
            {
                if (g == null) continue;
                Vector3 c = g.Center;
                if ((c - eye).sqrMagnitude > pickupRange * pickupRange) continue;
                float a = Angle(c);
                if (a < lookAngle && a < best) { best = a; kind = Kind.Gun; gun = g; }
            }

        float trunkBest = float.MaxValue;
        int n = Physics.OverlapSphereNonAlloc(transform.position + Vector3.up, 4f, near, trafficMask, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < n; i++)
        {
            var car = near[i].GetComponentInParent<FlyingVehicle>();
            var t = car != null ? car.Trunk : null;
            if (t == null) continue;
            if (t.IsOpen && t.OpenAmount > 0.6f)
                foreach (var it in t.Items)
                {
                    if (it == null || (it.position - eye).sqrMagnitude > pickupRange * pickupRange) continue;
                    float a = Angle(it.position);
                    if (a < lookAngle && a < best) { best = a; kind = Kind.TrunkItem; trunk = t; item = it; }
                }
            // The rear: within reach of the opening, standing behind the car, looking at it.
            Vector3 o = t.OpeningCentre;
            Vector3 flat = transform.position - o; flat.y = 0f;
            if (flat.magnitude > OpenRangeFlat || Mathf.Abs(o.y - (transform.position.y + 1f)) > 1.6f) continue;
            if (Vector3.Dot(flat, t.RearNormal) < 0.1f) continue;
            if (!t.CanOpen) continue;
            // Items and guns come first; among trunks, the one most in view.
            float ta = Angle(o);
            if (ta < trunkLookAngle && (kind == Kind.None || (kind == Kind.Trunk && ta < trunkBest))) { trunkBest = ta; kind = Kind.Trunk; trunk = t; }
        }

        Prompt = kind switch
        {
            Kind.Gun => "E  pick up laser",
            Kind.TrunkItem => "E  pick up grenade",
            Kind.Trunk => trunk.IsOpen ? "E  close trunk" : "E  open trunk",
            _ => null,
        };
    }

    const float OpenRangeFlat = CarTrunk.OpenRange;

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
