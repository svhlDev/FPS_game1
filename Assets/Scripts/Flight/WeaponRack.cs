using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

// The police armory: a wall rack beside the police station door holding one of every weapon, mounted
// barrel-up with a coloured tag light under each (built from primitives at runtime).
//   E on a weapon you don't have: take it (it goes into its slot, the number key draws it). The rack's
//     copy is gone until it restocks (restockTime s; the tag light goes dark meanwhile).
//   E on a weapon you already have: refill its ammo (the rack keeps its copy).
//   Either one in sight of police is theft: wanted 1 (PoliceDispatch.ReportArmoryTheft).
// Placement: if the scene has no WeaponRack, one is put up automatically when a scene with a
// PoliceStation loads: flush against the station's facade, a few metres to the side of the door,
// on whichever side has a clear stretch of wall (a probe ray finds the facade, a box check keeps it
// out of lamps and benches). Put a WeaponRack in the scene yourself to place it by hand (the object's
// +Z faces out of the wall, its origin is the floor at the wall); it builds itself on play.
public class WeaponRack : MonoBehaviour
{
    [Tooltip("Seconds until a taken weapon is back on the rack.")]
    public float restockTime = 90f;
    [Tooltip("How close (from the eyes) the player must be to use the rack.")]
    public float useRange = 2.6f;
    public bool reportTheft = true;

    public const float Width = 2.1f;
    static readonly List<WeaponRack> all = new List<WeaponRack>();
    public static IReadOnlyList<WeaponRack> All => all;
    public static int Taken, Refills;

    public class Slot
    {
        public Weapon.Kind kind;
        public Weapon display;
        public Renderer tag;
        public float restockAt = -1f;
        public bool Stocked => restockAt < 0f;
        public float RestockIn => Stocked ? 0f : Mathf.Max(0f, restockAt - Time.time);
    }
    public readonly List<Slot> Slots = new List<Slot>();
    bool built;
    static Material panelMat, postMat, chromeMat, blueNeon, whiteNeon, darkTag, redLight;

    void Awake() => Build();
    void OnEnable() { if (!all.Contains(this)) all.Add(this); }
    void OnDisable() => all.Remove(this);

    // Centre of a slot's weapon in the world (what the player looks at).
    public Vector3 SlotCenter(Slot s) => s.display != null ? s.display.transform.TransformPoint(new Vector3(0f, 0.04f, 0.02f)) : transform.position;

    void Update()
    {
        foreach (var s in Slots)
        {
            if (s.Stocked || Time.time < s.restockAt) continue;
            s.restockAt = -1f;
            if (s.display != null) s.display.gameObject.SetActive(true);
            if (s.tag != null) s.tag.sharedMaterial = Weapon.AccentMat(s.kind);
        }
    }

    // E on a slot. True if something happened (a weapon taken or ammo refilled).
    public bool Use(Slot s, PlayerWeapon pw, FirstPersonController fpc)
    {
        if (s == null || pw == null) return false;
        var have = pw.Get(s.kind);
        if (have != null)
        {
            if (have.Unlimited || have.ammo >= have.maxAmmo) { fpc?.Flash($"{have.DisplayName} is full"); return false; }
            pw.GiveKind(s.kind);
            Refills++;
        }
        else
        {
            if (!s.Stocked) return false;
            pw.GiveKind(s.kind);
            Taken++;
            s.restockAt = Time.time + restockTime;
            if (s.display != null) s.display.gameObject.SetActive(false);
            if (s.tag != null) s.tag.sharedMaterial = darkTag;
        }
        if (reportTheft) PoliceDispatch.Instance?.ReportArmoryTheft(SlotCenter(s));
        return true;
    }

    // What E would do on that slot ("" for nothing).
    public string PromptFor(Slot s, PlayerWeapon pw)
    {
        if (s == null || pw == null) return "";
        var have = pw.Get(s.kind);
        string name = Weapon.NameOf(s.kind);
        if (have != null)
            return have.Unlimited || have.ammo >= have.maxAmmo ? $"{name}  (full)" : $"E  refill {name}  ({have.ammo}/{have.maxAmmo})";
        if (!s.Stocked) return $"{name}  -  restocked in {Mathf.CeilToInt(s.RestockIn)} s";
        return $"E  take {name}";
    }

    // ---------- the rack ----------

    void Build()
    {
        if (built) return;
        built = true;
        EnsureMats();
        var root = transform;
        // Back panel, side posts, hood with a police-blue light strip, lower cabinet.
        Part(PrimitiveType.Cube, root, new Vector3(0f, 1.3f, 0.03f), new Vector3(Width, 1.5f, 0.06f), Quaternion.identity, panelMat, true);
        foreach (float sx in new[] { -1f, 1f })
        {
            Part(PrimitiveType.Cube, root, new Vector3(sx * (Width * 0.5f + 0.035f), 1.075f, 0.16f), new Vector3(0.07f, 2.15f, 0.32f), Quaternion.identity, postMat, true);
            Part(PrimitiveType.Cube, root, new Vector3(sx * (Width * 0.5f + 0.035f), 1.75f, 0.325f), new Vector3(0.03f, 0.03f, 0.01f), Quaternion.identity, redLight, false);
        }
        Part(PrimitiveType.Cube, root, new Vector3(0f, 2.2f, 0.21f), new Vector3(Width + 0.2f, 0.1f, 0.42f), Quaternion.identity, postMat, true);
        Part(PrimitiveType.Cube, root, new Vector3(0f, 2.13f, 0.4f), new Vector3(Width, 0.035f, 0.035f), Quaternion.identity, blueNeon, false);
        Part(PrimitiveType.Cube, root, new Vector3(0f, 2.2f, 0.425f), new Vector3(1.2f, 0.05f, 0.01f), Quaternion.identity, whiteNeon, false);
        Part(PrimitiveType.Cube, root, new Vector3(0f, 0.25f, 0.18f), new Vector3(Width, 0.5f, 0.36f), Quaternion.identity, postMat, true);
        Part(PrimitiveType.Cube, root, new Vector3(0f, 0.46f, 0.365f), new Vector3(Width - 0.1f, 0.02f, 0.01f), Quaternion.identity, whiteNeon, false);
        for (int i = -1; i <= 1; i += 2)
            Part(PrimitiveType.Cube, root, new Vector3(i * Width * 0.25f, 0.25f, 0.362f), new Vector3(0.012f, 0.42f, 0.008f), Quaternion.identity, panelMat, false);
        // Security bar across the top of the weapons.
        Part(PrimitiveType.Cylinder, root, new Vector3(0f, 1.75f, 0.2f), new Vector3(0.025f, Width * 0.5f, 0.025f), Quaternion.Euler(0f, 0f, 90f), chromeMat, true);

        // One column per weapon: two pegs, the weapon barrel-up (its left side facing out), a tag light.
        int n = Weapon.KindCount;
        float spacing = (Width - 0.3f) / (n - 1);
        for (int i = 0; i < n; i++)
        {
            var kind = (Weapon.Kind)i;
            float x = -(Width - 0.3f) * 0.5f + i * spacing;
            var anchor = new GameObject("Slot_" + kind).transform;
            anchor.SetParent(root, false);
            anchor.localPosition = new Vector3(x, 1.38f, 0.12f);
            Part(PrimitiveType.Cube, root, new Vector3(x, 1.3f, 0.09f), new Vector3(0.03f, 0.03f, 0.07f), Quaternion.identity, chromeMat, false);
            Part(PrimitiveType.Cube, root, new Vector3(x, 1.5f, 0.09f), new Vector3(0.03f, 0.03f, 0.07f), Quaternion.identity, chromeMat, false);
            var display = Weapon.Build(kind, anchor, gameObject.layer);
            display.transform.localPosition = Vector3.zero;
            display.transform.localRotation = Quaternion.LookRotation(Vector3.up, Vector3.left);
            display.transform.localScale = Vector3.one * 1.15f; // a touch bigger than in the hand: readable on the wall
            display.Refill();
            display.ShowAmmo();
            var tag = Part(PrimitiveType.Cube, root, new Vector3(x, 1.08f, 0.07f), new Vector3(spacing * 0.7f, 0.025f, 0.015f), Quaternion.identity, Weapon.AccentMat(kind), false);
            Slots.Add(new Slot { kind = kind, display = display, tag = tag });
        }

        // One solid box for the whole rack (bullets stop on it, nobody walks through it).
        var box = gameObject.AddComponent<BoxCollider>();
        box.center = new Vector3(0f, 1.125f, 0.21f);
        box.size = new Vector3(Width + 0.2f, 2.25f, 0.42f);
    }

    static void EnsureMats()
    {
        if (panelMat != null) return;
        panelMat = MaterialLibrary.Get(SurfaceType.PaintedMetal, new Color(0.07f, 0.09f, 0.14f), MaterialLibrary.Variant.Object);
        postMat = MaterialLibrary.Get(SurfaceType.BrushedMetal, new Color(0.16f, 0.17f, 0.19f), MaterialLibrary.Variant.Object);
        chromeMat = MaterialLibrary.Get(SurfaceType.Chrome, new Color(0.7f, 0.72f, 0.75f), MaterialLibrary.Variant.Object);
        blueNeon = WeaponFx.Unlit(new Color(0.2f, 0.4f, 1f) * 3f);
        whiteNeon = WeaponFx.Unlit(new Color(0.9f, 0.95f, 1f) * 2f);
        redLight = WeaponFx.Unlit(new Color(1f, 0.1f, 0.08f) * 3f);
        darkTag = WeaponFx.Unlit(new Color(0.05f, 0.05f, 0.06f));
    }

    static Renderer Part(PrimitiveType type, Transform parent, Vector3 pos, Vector3 size, Quaternion rot, Material mat, bool shadows)
    {
        var r = WeaponFx.Prim(type, parent, pos, size, rot, mat, shadows);
        r.gameObject.layer = parent.gameObject.layer;
        return r;
    }

    // ---------- automatic placement at the police station ----------

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        SceneManager.sceneLoaded += OnSceneLoaded;
        EnsureAtStation();
    }

    static void OnSceneLoaded(Scene s, LoadSceneMode mode) => EnsureAtStation();

    public static WeaponRack EnsureAtStation()
    {
        var existing = FindAnyObjectByType<WeaponRack>();
        if (existing != null) return existing;
        var station = FindAnyObjectByType<PoliceStation>();
        if (station == null || station.door == null) return null;

        Transform door = station.door;
        Vector3 fwd = new Vector3(door.forward.x, 0f, door.forward.z);
        if (fwd.sqrMagnitude < 1e-4f) return null;
        fwd.Normalize();
        Vector3 right = Vector3.Cross(Vector3.up, fwd);
        Vector3 wall = door.position - fwd * 2f;   // the door marker stands 2 m out from the facade
        int mask = Physics.DefaultRaycastLayers;
        foreach (var layer in new[] { "Player", "Traffic" })
        {
            int l = LayerMask.NameToLayer(layer);
            if (l >= 0) mask &= ~(1 << l);
        }
        Quaternion rot = Quaternion.LookRotation(fwd);
        Vector3 place = wall + right * 4.6f;
        place.y = door.position.y - 0.05f;
        foreach (float off in new[] { 4.6f, -4.6f, 6f, -6f, 7.5f, -7.5f })
        {
            Vector3 probe = wall + right * off + fwd * 1.5f + Vector3.up * 1.3f;
            if (!Physics.Raycast(probe, -fwd, out var hit, 3f, mask, QueryTriggerInteraction.Ignore)) continue;
            if (Vector3.Dot(hit.normal, fwd) < 0.8f) continue;                        // not a wall facing the street
            if (Mathf.Abs(Vector3.Dot(hit.point - wall, fwd)) > 0.6f) continue;      // a post or a lamp, not the facade
            Vector3 p = hit.point;
            p.y = Physics.Raycast(p + fwd * 0.3f + Vector3.up * 1.5f, Vector3.down, out var g, 4f, mask, QueryTriggerInteraction.Ignore)
                ? g.point.y : door.position.y - 0.05f;
            Vector3 c = p + fwd * 0.26f + Vector3.up * 1.15f;
            if (Physics.CheckBox(c, new Vector3(Width * 0.5f + 0.1f, 0.95f, 0.19f), rot, mask, QueryTriggerInteraction.Ignore)) continue;
            place = p;
            break;
        }
        var go = new GameObject("PoliceArmoryRack");
        go.transform.SetParent(station.transform, true);
        go.transform.SetPositionAndRotation(place, rot);
        return go.AddComponent<WeaponRack>();
    }
}
