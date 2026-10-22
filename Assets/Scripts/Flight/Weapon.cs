using System.Collections.Generic;
using UnityEngine;

// A hand weapon. Barrel along local +Z; the transform's origin is the Grip (where the hand holds it),
// Muzzle is a child at the emitter on the barrel's nose. Every weapon is one-handed (ArmAim on the right
// arm) and built from primitives at runtime.
// Kinds (PlayerWeapon fires them; officers only carry the T-gun):
//   TGun        : the slim laser pistol. Stun / Lethal (B). Infinite charge.
//   Scatter     : a laser shotgun. 9 beams in a 5-degree cone, Stun / Lethal (B), heavy kick.
//   Arc         : arc caster. A lightning bolt that jumps to up to 4 more targets (people, cars) 8 m apart.
//   Limpet      : magnetic charge launcher. Lobbed charges stick to whatever they hit (cars, people,
//                 walls); RMB sets them all off.
//   Repulsor    : kinetic cone blast. Hold LMB to charge, release to shove everything in front of you;
//                 fired at your feet it launches you upward.
//   Singularity : fires an orb that opens a short-lived micro black hole: pulls in cars, people and
//                 bodies, then collapses in a blast.
// The T-gun model: a slim gunmetal barrel bar sitting just above the hand, a matte black grip stem under
// the barrel's centre raked back 12 degrees, two mode strips along the barrel sides and an emitter square
// on the nose in the mode colour (blue stun, red lethal).
public class Weapon : MonoBehaviour
{
    public enum Mode { Stun, Lethal }
    public enum Kind { TGun, Scatter, Arc, Limpet, Repulsor, Singularity }
    public const int KindCount = 6;

    public Transform muzzle;
    public Transform grip;
    public Kind kind = Kind.TGun;

    [Header("Stun")]
    public float stunInterval = 0.6f;
    public float stunRange = 60f;
    public float stunTime = 2f;
    public float carHiccup = 0.5f;

    [Header("Lethal")]
    public float lethalInterval = 0.25f;
    public float lethalRange = 300f;
    public float damage = 25f;          // people (head x2, body x1, limbs x0.6)
    public float carDamage = 15f;       // through VehicleHealth

    [Header("Recoil")]
    public float recoilPitch = 6f;      // degrees, barrel kicks up
    public float recoilBack = 0.05f;    // metres, hand pushed back

    [Header("Ammo (-1 = unlimited)")]
    public int ammo = -1;
    public int maxAmmo = -1;

    public Mode CurrentMode { get; private set; } = Mode.Stun;
    // Only the lasers have a stun mode; everything else is always lethal (and police treat it so).
    public bool HasModes => kind == Kind.TGun || kind == Kind.Scatter;
    public float FireInterval => !HasModes ? lethalInterval : CurrentMode == Mode.Stun ? stunInterval : lethalInterval;
    public float Range => !HasModes ? lethalRange : CurrentMode == Mode.Stun ? stunRange : lethalRange;
    public bool Unlimited => maxAmmo < 0;
    public bool HasAmmo => Unlimited || ammo > 0;
    public void Refill() { if (!Unlimited) ammo = maxAmmo; }
    public bool Spend(int n = 1)
    {
        if (Unlimited) return true;
        if (ammo < n) return false;
        ammo -= n;
        return true;
    }

    public string DisplayName => NameOf(kind);
    public Color Accent => AccentOf(kind, CurrentMode);

    // Grip to muzzle along the barrel (for the arm's close-range pull-back).
    public float Length => muzzle != null ? muzzle.localPosition.z : 0.2f;

    readonly List<Renderer> modeParts = new List<Renderer>();
    readonly List<Renderer> glowParts = new List<Renderer>();
    static Material gunmetal, stemBlack, sightMat, darkSteel, chrome;
    static readonly Dictionary<Kind, Material> accentMats = new Dictionary<Kind, Material>();

    public static string NameOf(Kind k) => k switch
    {
        Kind.TGun => "T-gun",
        Kind.Scatter => "Scatter laser",
        Kind.Arc => "Arc caster",
        Kind.Limpet => "Limpet launcher",
        Kind.Repulsor => "Repulsor",
        Kind.Singularity => "Singularity gun",
        _ => k.ToString(),
    };

    public static Color AccentOf(Kind k, Mode m = Mode.Lethal) => k switch
    {
        Kind.TGun => LaserWeapon.ColorOf(m),
        Kind.Scatter => LaserWeapon.ColorOf(m),
        Kind.Arc => ArcBolts.BoltColor,
        Kind.Limpet => new Color(1f, 0.55f, 0.1f),
        Kind.Repulsor => new Color(0.75f, 0.35f, 1f),
        Kind.Singularity => new Color(0.55f, 0.2f, 1f),
        _ => Color.white,
    };

    // Shared emissive material for a kind's glowing parts (bright enough to bloom).
    public static Material AccentMat(Kind k)
    {
        if (accentMats.TryGetValue(k, out var m) && m != null) return m;
        m = new Material(Shader.Find("Universal Render Pipeline/Unlit")) { name = k + " accent", enableInstancing = true };
        m.SetColor("_BaseColor", AccentOf(k) * 3f);
        accentMats[k] = m;
        return m;
    }

    public void SetMode(Mode m)
    {
        if (!HasModes) m = Mode.Lethal;
        CurrentMode = m;
        var mat = LaserWeapon.ModeMat(m);
        foreach (var r in modeParts) if (r != null) r.sharedMaterial = mat;
    }

    // The glowing parts swell with a charge (Repulsor) without new materials: k = 1 is the built size.
    public IReadOnlyList<Renderer> GlowParts => glowParts;
    Vector3[] glowBase;
    public void SetGlow(float k)
    {
        if (glowBase == null)
        {
            glowBase = new Vector3[glowParts.Count];
            for (int i = 0; i < glowParts.Count; i++) glowBase[i] = glowParts[i] != null ? glowParts[i].transform.localScale : Vector3.one;
        }
        for (int i = 0; i < glowParts.Count; i++) if (glowParts[i] != null) glowParts[i].transform.localScale = glowBase[i] * k;
    }

    // Limpet: the six drum windows show the charges left (the bore glow, part 0, always shows).
    public void ShowAmmo()
    {
        if (kind != Kind.Limpet) return;
        for (int i = 1; i < glowParts.Count; i++) if (glowParts[i] != null) glowParts[i].enabled = Unlimited || ammo >= i;
    }

    // Any kind (the rack, the player's inventory).
    public static Weapon Build(Kind k, Transform parent, int layer, Mode mode = Mode.Lethal)
    {
        switch (k)
        {
            case Kind.Scatter: return BuildScatter(parent, layer, mode);
            case Kind.Arc: return BuildArc(parent, layer);
            case Kind.Limpet: return BuildLimpet(parent, layer);
            case Kind.Repulsor: return BuildRepulsor(parent, layer);
            case Kind.Singularity: return BuildSingularity(parent, layer);
            default: return BuildTGun(parent, layer, mode);
        }
    }

    static void EnsureMats()
    {
        if (gunmetal != null) return;
        // Gunmetal: dark painted finish worn to bare metal on the edges (surface library).
        gunmetal = MaterialLibrary.Get(SurfaceType.PaintedMetal, new Color(0.2f, 0.21f, 0.23f), MaterialLibrary.Variant.Object);
        stemBlack = MaterialLibrary.Get(SurfaceType.Rubber, new Color(0.03f, 0.03f, 0.035f), MaterialLibrary.Variant.Object);
        darkSteel = MaterialLibrary.Get(SurfaceType.BrushedMetal, new Color(0.12f, 0.12f, 0.13f), MaterialLibrary.Variant.Object);
        chrome = MaterialLibrary.Get(SurfaceType.Chrome, new Color(0.7f, 0.72f, 0.75f), MaterialLibrary.Variant.Object);
        sightMat = new Material(Shader.Find("Universal Render Pipeline/Unlit")) { name = "GunSight", enableInstancing = true };
        sightMat.SetColor("_BaseColor", new Color(0.2f, 1f, 0.35f) * 3f);
    }

    static Weapon NewRoot(Transform parent, int layer, Kind k, string name)
    {
        EnsureMats();
        var root = new GameObject(name).transform;
        root.SetParent(parent, false);
        root.gameObject.layer = layer;
        var w = root.gameObject.AddComponent<Weapon>();
        w.kind = k;
        w.grip = root;
        w.SetMode(Mode.Lethal); // the lasers set their own mode after building
        return w;
    }

    static void Muzzle(Weapon w, Vector3 at)
    {
        w.muzzle = new GameObject("Muzzle").transform;
        w.muzzle.SetParent(w.transform, false);
        w.muzzle.localPosition = at;
    }

    // ---------- T-gun ----------

    public static Weapon BuildTGun(Transform parent, int layer, Mode mode = Mode.Stun)
    {
        var w = NewRoot(parent, layer, Kind.TGun, "TGun");
        var root = w.transform;

        // Barrel bar 0.032 x 0.036 x 0.20 (z -0.10 .. 0.10), its underside just inside the top of the
        // 0.08 hand (hand centre = origin). The last 0.015 is a lower nose with 45-degree chamfers.
        const float by = 0.045f, hw = 0.032f, hh = 0.036f;
        Box(root, new Vector3(0f, by, -0.0075f), new Vector3(hw, hh, 0.185f), Quaternion.identity, gunmetal, layer);
        Box(root, new Vector3(0f, by, 0.0925f), new Vector3(hw, 0.024f, 0.015f), Quaternion.identity, gunmetal, layer);
        foreach (float sy in new[] { 1f, -1f })
            Box(root, new Vector3(0f, by + sy * 0.012f, 0.085f), new Vector3(hw, 0.0085f, 0.0085f), Quaternion.Euler(45f, 0f, 0f), gunmetal, layer);

        // Grip stem 0.026 x 0.085 x 0.036, raked back 12 degrees; the hand covers its upper half.
        Box(root, new Vector3(0f, -0.021f, -0.004f), new Vector3(0.026f, 0.085f, 0.036f), Quaternion.Euler(12f, 0f, 0f), stemBlack, layer);

        // Emissive sight: a small green dot on top at the back of the barrel (Phase 5).
        Box(root, new Vector3(0f, by + hh * 0.5f + 0.003f, -0.085f), new Vector3(0.006f, 0.006f, 0.01f), Quaternion.identity, sightMat, layer)
            .shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

        // Mode strips along the barrel sides and the emitter square on the nose.
        foreach (float sx in new[] { 1f, -1f })
            w.modeParts.Add(Box(root, new Vector3(sx * (hw * 0.5f + 0.002f), by, -0.01f), new Vector3(0.004f, 0.006f, 0.15f), Quaternion.identity, null, layer));
        w.modeParts.Add(Box(root, new Vector3(0f, by, 0.1015f), new Vector3(0.018f, 0.018f, 0.003f), Quaternion.identity, null, layer));

        Muzzle(w, new Vector3(0f, by, 0.104f));
        w.SetMode(mode);
        return w;
    }

    // ---------- Scatter laser ----------
    // A chunky slab body over the hand, a fat square shroud at the front with a 3 x 3 emitter grid in the
    // mode colour, a slide-like heat sink on top and a pump grip underneath.
    static Weapon BuildScatter(Transform parent, int layer, Mode mode)
    {
        var w = NewRoot(parent, layer, Kind.Scatter, "ScatterLaser");
        var root = w.transform;
        const float by = 0.05f;
        Box(root, new Vector3(0f, by, 0.0f), new Vector3(0.046f, 0.05f, 0.24f), Quaternion.identity, gunmetal, layer);
        Box(root, new Vector3(0f, by, 0.135f), new Vector3(0.066f, 0.066f, 0.05f), Quaternion.identity, darkSteel, layer);
        // Heat sink fins on top.
        for (int i = 0; i < 5; i++)
            Box(root, new Vector3(0f, by + 0.03f, -0.06f + i * 0.035f), new Vector3(0.05f, 0.012f, 0.008f), Quaternion.identity, darkSteel, layer);
        // Pump grip under the front.
        Box(root, new Vector3(0f, by - 0.04f, 0.075f), new Vector3(0.034f, 0.028f, 0.09f), Quaternion.identity, stemBlack, layer);
        // Grip stem.
        Box(root, new Vector3(0f, -0.022f, -0.03f), new Vector3(0.03f, 0.09f, 0.04f), Quaternion.Euler(14f, 0f, 0f), stemBlack, layer);
        // Emitter grid on the shroud's face, mode strips on the sides.
        for (int x = -1; x <= 1; x++)
            for (int y = -1; y <= 1; y++)
                w.modeParts.Add(Box(root, new Vector3(x * 0.017f, by + y * 0.017f, 0.161f), new Vector3(0.011f, 0.011f, 0.003f), Quaternion.identity, null, layer));
        foreach (float sx in new[] { 1f, -1f })
            w.modeParts.Add(Box(root, new Vector3(sx * 0.0245f, by + 0.008f, -0.02f), new Vector3(0.004f, 0.008f, 0.16f), Quaternion.identity, null, layer));
        Box(root, new Vector3(0f, by + 0.04f, -0.1f), new Vector3(0.006f, 0.006f, 0.01f), Quaternion.identity, sightMat, layer)
            .shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

        Muzzle(w, new Vector3(0f, by, 0.165f));
        w.stunInterval = 0.9f; w.stunRange = 30f; w.stunTime = 1.6f; w.carHiccup = 0.35f;
        w.lethalInterval = 0.85f; w.lethalRange = 45f; w.damage = 11f; w.carDamage = 5f;
        w.recoilPitch = 16f; w.recoilBack = 0.12f;
        w.ammo = w.maxAmmo = 24;
        w.SetMode(mode);
        return w;
    }

    // ---------- Arc caster ----------
    // A short body with three glowing coil rings round the barrel and two chrome prongs forking out of the
    // nose; the bolt leaves from between the prongs.
    static Weapon BuildArc(Transform parent, int layer)
    {
        var w = NewRoot(parent, layer, Kind.Arc, "ArcCaster");
        var root = w.transform;
        var glow = AccentMat(Kind.Arc);
        const float by = 0.048f;
        Box(root, new Vector3(0f, by, -0.03f), new Vector3(0.04f, 0.044f, 0.16f), Quaternion.identity, gunmetal, layer);
        Cyl(root, new Vector3(0f, by, 0.06f), 0.022f, 0.08f, darkSteel, layer);
        for (int i = 0; i < 3; i++)
            w.glowParts.Add(Cyl(root, new Vector3(0f, by, 0.035f + i * 0.022f), 0.034f, 0.008f, glow, layer));
        foreach (float sx in new[] { 1f, -1f })
        {
            Box(root, new Vector3(sx * 0.016f, by, 0.115f), new Vector3(0.006f, 0.01f, 0.05f), Quaternion.Euler(0f, sx * -12f, 0f), chrome, layer);
            w.glowParts.Add(Box(root, new Vector3(sx * 0.02f, by, 0.142f), new Vector3(0.007f, 0.007f, 0.007f), Quaternion.identity, glow, layer));
        }
        // Battery pack hanging under the back, grip stem.
        w.glowParts.Add(Box(root, new Vector3(0f, by - 0.035f, -0.075f), new Vector3(0.03f, 0.022f, 0.05f), Quaternion.identity, glow, layer));
        Box(root, new Vector3(0f, -0.021f, -0.01f), new Vector3(0.027f, 0.085f, 0.036f), Quaternion.Euler(12f, 0f, 0f), stemBlack, layer);

        Muzzle(w, new Vector3(0f, by, 0.145f));
        w.lethalInterval = 0.5f; w.lethalRange = 35f; w.damage = 20f; w.carDamage = 12f;
        w.stunTime = 1.0f; w.carHiccup = 1.5f;
        w.recoilPitch = 4f; w.recoilBack = 0.03f;
        w.ammo = w.maxAmmo = 40;
        return w;
    }

    // ---------- Limpet launcher ----------
    // A fat short tube with a revolver drum on top showing the orange glow of the loaded charges.
    static Weapon BuildLimpet(Transform parent, int layer)
    {
        var w = NewRoot(parent, layer, Kind.Limpet, "LimpetLauncher");
        var root = w.transform;
        var glow = AccentMat(Kind.Limpet);
        const float by = 0.05f;
        Cyl(root, new Vector3(0f, by, 0.03f), 0.06f, 0.22f, gunmetal, layer);
        Cyl(root, new Vector3(0f, by, 0.135f), 0.07f, 0.02f, darkSteel, layer);              // muzzle collar
        w.glowParts.Add(Cyl(root, new Vector3(0f, by, 0.146f), 0.044f, 0.003f, glow, layer)); // bore glow
        // Drum on top (axis along the barrel) with six charge windows.
        Cyl(root, new Vector3(0f, by + 0.05f, -0.03f), 0.06f, 0.07f, darkSteel, layer);
        for (int i = 0; i < 6; i++)
        {
            float a = i * 60f * Mathf.Deg2Rad;
            var p = new Vector3(Mathf.Cos(a) * 0.031f, by + 0.05f + Mathf.Sin(a) * 0.031f, 0.006f);
            w.glowParts.Add(Box(root, p, new Vector3(0.012f, 0.012f, 0.004f), Quaternion.identity, glow, layer));
        }
        Box(root, new Vector3(0f, -0.022f, -0.035f), new Vector3(0.03f, 0.09f, 0.04f), Quaternion.Euler(14f, 0f, 0f), stemBlack, layer);
        Box(root, new Vector3(0f, by - 0.04f, 0.06f), new Vector3(0.02f, 0.02f, 0.06f), Quaternion.identity, stemBlack, layer);

        Muzzle(w, new Vector3(0f, by, 0.15f));
        w.lethalInterval = 0.45f; w.lethalRange = 120f; w.damage = 110f; w.carDamage = 140f;
        w.recoilPitch = 10f; w.recoilBack = 0.07f;
        w.ammo = w.maxAmmo = 8;
        return w;
    }

    // ---------- Repulsor ----------
    // A flared bell: stacked rings widening toward the front, a violet field plate inside the mouth and
    // a capacitor bar along the top that glows with the charge.
    static Weapon BuildRepulsor(Transform parent, int layer)
    {
        var w = NewRoot(parent, layer, Kind.Repulsor, "Repulsor");
        var root = w.transform;
        var glow = AccentMat(Kind.Repulsor);
        const float by = 0.05f;
        Box(root, new Vector3(0f, by, -0.05f), new Vector3(0.042f, 0.046f, 0.12f), Quaternion.identity, gunmetal, layer);
        for (int i = 0; i < 4; i++)
        {
            float d = 0.05f + i * 0.018f;
            Cyl(root, new Vector3(0f, by, 0.025f + i * 0.026f), d, 0.024f, i % 2 == 0 ? darkSteel : chrome, layer);
        }
        w.glowParts.Add(Cyl(root, new Vector3(0f, by, 0.1f), 0.085f, 0.003f, glow, layer));      // field plate
        w.glowParts.Add(Box(root, new Vector3(0f, by + 0.03f, -0.05f), new Vector3(0.012f, 0.012f, 0.1f), Quaternion.identity, glow, layer));
        Box(root, new Vector3(0f, -0.021f, -0.04f), new Vector3(0.028f, 0.088f, 0.038f), Quaternion.Euler(12f, 0f, 0f), stemBlack, layer);

        Muzzle(w, new Vector3(0f, by, 0.11f));
        w.lethalInterval = 0.6f; w.lethalRange = 16f; w.damage = 14f; w.carDamage = 10f;
        w.recoilPitch = 18f; w.recoilBack = 0.14f;
        w.ammo = w.maxAmmo = 12;
        return w;
    }

    // ---------- Singularity gun ----------
    // A compact body with a glass-dark sphere chamber on top holding a bright violet core, and a short
    // chrome barrel ringed with violet.
    static Weapon BuildSingularity(Transform parent, int layer)
    {
        var w = NewRoot(parent, layer, Kind.Singularity, "SingularityGun");
        var root = w.transform;
        var glow = AccentMat(Kind.Singularity);
        const float by = 0.048f;
        Box(root, new Vector3(0f, by, -0.03f), new Vector3(0.044f, 0.044f, 0.17f), Quaternion.identity, gunmetal, layer);
        Cyl(root, new Vector3(0f, by, 0.08f), 0.026f, 0.07f, chrome, layer);
        w.glowParts.Add(Cyl(root, new Vector3(0f, by, 0.1f), 0.034f, 0.01f, glow, layer));
        w.glowParts.Add(Cyl(root, new Vector3(0f, by, 0.07f), 0.034f, 0.01f, glow, layer));
        Sphere(root, new Vector3(0f, by + 0.045f, -0.035f), 0.06f, darkSteel, layer);
        w.glowParts.Add(Sphere(root, new Vector3(0f, by + 0.045f, -0.035f), 0.034f, glow, layer));
        Box(root, new Vector3(0f, -0.021f, -0.02f), new Vector3(0.027f, 0.085f, 0.036f), Quaternion.Euler(12f, 0f, 0f), stemBlack, layer);

        Muzzle(w, new Vector3(0f, by, 0.118f));
        w.lethalInterval = 1.2f; w.lethalRange = 40f; w.damage = 30f; w.carDamage = 180f;
        w.recoilPitch = 12f; w.recoilBack = 0.09f;
        w.ammo = w.maxAmmo = 3;
        return w;
    }

    // ---------- primitives ----------

    static Renderer Box(Transform parent, Vector3 pos, Vector3 size, Quaternion rot, Material mat, int layer) =>
        Prim(PrimitiveType.Cube, parent, pos, size, rot, mat, layer);

    // Cylinder along the barrel (local Z): diameter d, length len.
    static Renderer Cyl(Transform parent, Vector3 pos, float d, float len, Material mat, int layer) =>
        Prim(PrimitiveType.Cylinder, parent, pos, new Vector3(d, len * 0.5f, d), Quaternion.Euler(90f, 0f, 0f), mat, layer);

    static Renderer Sphere(Transform parent, Vector3 pos, float d, Material mat, int layer) =>
        Prim(PrimitiveType.Sphere, parent, pos, Vector3.one * d, Quaternion.identity, mat, layer);

    static Renderer Prim(PrimitiveType type, Transform parent, Vector3 pos, Vector3 size, Quaternion rot, Material mat, int layer)
    {
        var go = GameObject.CreatePrimitive(type);
        Destroy(go.GetComponent<Collider>());
        go.layer = layer;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = pos;
        go.transform.localRotation = rot;
        go.transform.localScale = size;
        var r = go.GetComponent<Renderer>();
        r.sharedMaterial = mat;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
        return r;
    }
}
