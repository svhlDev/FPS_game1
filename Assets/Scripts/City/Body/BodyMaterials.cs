using System.Collections.Generic;
using UnityEngine;

// Phase 7: shared materials for generated bodies (one per skin tone / eye look, never per character).
//   Skin  : FPS/BodySkin (Resources/BodySkin.shader: wrap-lit skin, all lights), tone from the seed.
//   Eyes  : off-white sclera; iris in a seed colour, glowing (unlit HDR, blooms) by INT glow, in steps.
public static class BodyMaterials
{
    // Seed-chosen skin tones, light to dark.
    public static readonly Color[] SkinTones =
    {
        new Color(0.95f, 0.80f, 0.69f), new Color(0.88f, 0.70f, 0.57f), new Color(0.80f, 0.60f, 0.46f), new Color(0.70f, 0.50f, 0.36f),
        new Color(0.58f, 0.40f, 0.28f), new Color(0.46f, 0.31f, 0.21f), new Color(0.36f, 0.24f, 0.16f), new Color(0.27f, 0.18f, 0.12f),
    };
    public static readonly Color[] IrisColors =
    {
        new Color(0.25f, 0.16f, 0.09f), new Color(0.12f, 0.08f, 0.05f), new Color(0.22f, 0.36f, 0.52f), new Color(0.26f, 0.38f, 0.22f), new Color(0.4f, 0.32f, 0.2f),
    };

    static Shader skinShader;
    static readonly Dictionary<Color, Material> skins = new Dictionary<Color, Material>();
    static readonly Dictionary<(Color, int), Material> irises = new Dictionary<(Color, int), Material>();
    static Material sclera;

    public static Shader SkinShader
    {
        get
        {
            if (skinShader == null) skinShader = Resources.Load<Shader>("BodySkin");
            if (skinShader == null) skinShader = Shader.Find("FPS/BodySkin");
            return skinShader;
        }
    }

    public static Color SkinTone(int seed) => SkinTones[new PcgRandom(seed, "skinTone").Range(0, SkinTones.Length)];

    public static Material Skin(Color tone)
    {
        if (skins.TryGetValue(tone, out var m) && m != null) return m;
        var sh = SkinShader;
        m = sh != null ? new Material(sh) : CharacterFigure.Mat(tone);
        m.name = "Skin " + ColorUtility.ToHtmlStringRGB(tone);
        m.enableInstancing = true;
        m.SetColor("_BaseColor", tone);
        skins[tone] = m;
        return m;
    }

    // Placeholder clothing by role (clothing proper is a later doc): body submeshes shirt, pants, skin,
    // shoes. Civilians: muted seed colours; police: navy uniform; the player: light shirt, dark pants.
    // (Clothing Part A: the submeshes are real garments now, one per garment of the body's outfit.)
    static readonly Color[] Shirts = { new Color(0.32f, 0.3f, 0.34f), new Color(0.38f, 0.26f, 0.24f), new Color(0.22f, 0.3f, 0.32f), new Color(0.4f, 0.37f, 0.3f), new Color(0.26f, 0.27f, 0.36f), new Color(0.3f, 0.33f, 0.26f), new Color(0.45f, 0.42f, 0.4f) };
    static readonly Color[] PantsColors = { new Color(0.14f, 0.14f, 0.16f), new Color(0.2f, 0.19f, 0.17f), new Color(0.16f, 0.18f, 0.22f), new Color(0.24f, 0.22f, 0.2f) };

    static readonly Color[] CoatColors = { new Color(0.25f, 0.2f, 0.16f), new Color(0.16f, 0.16f, 0.17f), new Color(0.3f, 0.27f, 0.2f), new Color(0.12f, 0.14f, 0.13f) };

    // Materials for a generated body's submeshes: the skin, then one per garment of its outfit, coloured
    // by the garment's slot (top, bottom, feet, outer) for the role; civilians from the seed (so the
    // same pooled body dresses differently per pedestrian).
    public static Material[] Clothes(BodyAsset asset, CharacterFigure.Role role, int seed, Material skin)
    {
        Color top, bottom, feet = new Color(0.07f, 0.07f, 0.08f), outer;
        var r = new PcgRandom(seed, "clothes");
        switch (role)
        {
            case CharacterFigure.Role.Police: top = new Color(0.06f, 0.1f, 0.3f); bottom = new Color(0.04f, 0.06f, 0.16f); feet = new Color(0.03f, 0.03f, 0.03f); outer = new Color(0.04f, 0.06f, 0.16f); break;
            case CharacterFigure.Role.Player: top = new Color(0.78f, 0.78f, 0.8f); bottom = new Color(0.22f, 0.23f, 0.26f); outer = new Color(0.25f, 0.2f, 0.16f); break;
            default:
                top = Shirts[r.Range(0, Shirts.Length)]; bottom = PantsColors[r.Range(0, PantsColors.Length)]; outer = CoatColors[r.Range(0, CoatColors.Length)]; break;
        }
        var garments = asset != null && asset.outfit != null ? asset.outfit.garments : new List<GarmentDef>();
        int n = asset != null && asset.body != null ? asset.body.subMeshCount : 1;
        var mats = new Material[n];
        mats[0] = skin;
        for (int i = 1; i < n; i++)
        {
            var def = i - 1 < garments.Count ? garments[i - 1] : null;
            var slot = def != null ? def.slot : GarmentSlot.Top;
            var colour = slot == GarmentSlot.Top ? top : slot == GarmentSlot.Bottom ? bottom : slot == GarmentSlot.Feet ? feet : outer;
            mats[i] = Garment(def != null ? def.fabric : Fabric.Cotton, colour);
        }
        return mats;
    }

    // Graphics Phase 3: garments use the surface library's fabric types in rest-pose space, with the
    // per-vertex edge / fold / hem data BodySurfaceBake writes (stitching, seams, contact shadow).
    public static SurfaceType SurfaceOf(Fabric f) => f switch
    {
        Fabric.Denim => SurfaceType.Denim, Fabric.Wool => SurfaceType.Wool, Fabric.Leather => SurfaceType.Leather,
        Fabric.Vinyl => SurfaceType.Vinyl, _ => SurfaceType.Cotton,
    };

    public static Material Garment(Fabric fabric, Color colour) => MaterialLibrary.Get(SurfaceOf(fabric), colour, MaterialLibrary.Variant.Skinned);

    // Hair colours (Head spec Part E): black, dark brown, brown, auburn, blond, grey; rarely a dyed neon.
    static readonly Color[] HairColours =
    {
        new Color(0.03f, 0.025f, 0.02f), new Color(0.07f, 0.045f, 0.03f), new Color(0.16f, 0.1f, 0.06f),
        new Color(0.3f, 0.1f, 0.04f), new Color(0.62f, 0.48f, 0.26f), new Color(0.45f, 0.44f, 0.43f),
    };
    static readonly Color[] NeonHair = { new Color(0.9f, 0.1f, 0.6f), new Color(0.1f, 0.75f, 0.9f), new Color(0.5f, 0.2f, 0.95f) };

    public static Material Wig(int seed) => MaterialLibrary.Get(SurfaceType.Hair, HairColour(seed), MaterialLibrary.Variant.Object);

    public static Color HairColour(int seed)
    {
        var r = new PcgRandom(seed, "hairColour");
        if (r.Value() < 0.05f) return NeonHair[r.Range(0, NeonHair.Length)];
        float v = r.Value();   // dark hair most common, grey rarest
        int i = v < 0.3f ? 0 : v < 0.55f ? 1 : v < 0.75f ? 2 : v < 0.85f ? 3 : v < 0.95f ? 4 : 5;
        return HairColours[i];
    }

    public static Material Sclera()
    {
        if (sclera != null) return sclera;
        sclera = CharacterFigure.Mat(new Color(0.88f, 0.86f, 0.82f));
        return sclera;
    }

    // Iris with the INT glow (0..1), quantized to 8 steps so bodies share materials.
    public static Material Iris(Color iris, float glow)
    {
        int step = Mathf.RoundToInt(Mathf.Clamp01(glow) * 8f);
        if (irises.TryGetValue((iris, step), out var m) && m != null) return m;
        var rules = BodyRules.Default;
        if (step == 0)
        {
            // Glossy (vinyl-like: wet eye) in the surface library.
            m = MaterialLibrary.Get(SurfaceType.Vinyl, iris, MaterialLibrary.Variant.Object);
        }
        else
        {
            // Glowing: unlit HDR colour (blooms; no dependence on Lit's emission variant surviving
            // shader stripping in player builds).
            m = new Material(Shader.Find("Universal Render Pipeline/Unlit")) { name = $"Iris {ColorUtility.ToHtmlStringRGB(iris)} glow {step}", enableInstancing = true };
            float t = step / 8f;
            m.SetColor("_BaseColor", Color.Lerp(iris, rules.eyeGlowColor * rules.eyeGlowIntensity, t));
        }
        irises[(iris, step)] = m;
        return m;
    }
}
