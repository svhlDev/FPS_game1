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
    static readonly Color[] Shirts = { new Color(0.32f, 0.3f, 0.34f), new Color(0.38f, 0.26f, 0.24f), new Color(0.22f, 0.3f, 0.32f), new Color(0.4f, 0.37f, 0.3f), new Color(0.26f, 0.27f, 0.36f), new Color(0.3f, 0.33f, 0.26f), new Color(0.45f, 0.42f, 0.4f) };
    static readonly Color[] PantsColors = { new Color(0.14f, 0.14f, 0.16f), new Color(0.2f, 0.19f, 0.17f), new Color(0.16f, 0.18f, 0.22f), new Color(0.24f, 0.22f, 0.2f) };

    public static Material[] Clothes(CharacterFigure.Role role, int seed, Material skin)
    {
        Color shirt, pants, shoes = new Color(0.07f, 0.07f, 0.08f);
        switch (role)
        {
            case CharacterFigure.Role.Police: shirt = new Color(0.06f, 0.1f, 0.3f); pants = new Color(0.04f, 0.06f, 0.16f); shoes = new Color(0.03f, 0.03f, 0.03f); break;
            case CharacterFigure.Role.Player: shirt = new Color(0.78f, 0.78f, 0.8f); pants = new Color(0.22f, 0.23f, 0.26f); break;
            default:
                var r = new PcgRandom(seed, "clothes");
                shirt = Shirts[r.Range(0, Shirts.Length)]; pants = PantsColors[r.Range(0, PantsColors.Length)]; break;
        }
        return new[] { CharacterFigure.Mat(shirt), CharacterFigure.Mat(pants), skin, CharacterFigure.Mat(shoes) };
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
            m = CharacterFigure.Mat(iris);
            m = new Material(m) { name = $"Iris {ColorUtility.ToHtmlStringRGB(iris)}" };
            m.SetFloat("_Smoothness", 0.85f);
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
