using System;
using System.Collections.Generic;
using UnityEngine;

public enum SurfaceType { Concrete, Asphalt, PaintedMetal, BrushedMetal, Chrome, Glass, Rubber, CarPaint, Cotton, Denim, Wool, Leather, Vinyl, RoadPaint, Hazard }

// Graphics Phase 2: every lit material in the game comes from here: one shared FPS/Surface material per
// (type, colour, variant), cached, never one per object (SRP Batcher and instancing friendly).
//   Variant picks the pattern space and where edges come from:
//     City     world space, box-face edges (static city geometry: primitives and slabs)
//     Object   object space, box-face edges (rigid movers built from primitives: cars, props, weapons)
//     Smooth   object space, screen-space curvature (smooth meshes without box UVs: the lab shapes)
//     Skinned  rest-pose space (TEXCOORD3), vertex-colour edges / cavities (generated bodies, garments)
// In the editor (not playing) the builders' materials are saved as assets under Assets/Graybox/Surfaces
// (scenes reference them); EditorHook is set by MaterialLibraryEditor.
public static class MaterialLibrary
{
    public enum Variant { City, Object, Smooth, Skinned }

    public const string ShaderName = "FPS/Surface";

    // Per type defaults (the look table in the Graphics spec).
    public struct Preset
    {
        public float smoothMin, smoothMax, metallic, detailScale, detailStrength, wear, grime, flake, sheen, edgeWidth;
        public Func<Color, Color> second;
    }

    static readonly Color BareMetal = new Color(0.55f, 0.55f, 0.57f);

    public static Preset For(SurfaceType t)
    {
        Preset p = new Preset { smoothMin = 0.2f, smoothMax = 0.4f, detailScale = 1f, detailStrength = 0.6f, wear = 0.4f, grime = 0.4f, edgeWidth = 0.04f, second = c => c };
        switch (t)
        {
            case SurfaceType.Concrete:     p.smoothMin = 0.08f; p.smoothMax = 0.28f; p.wear = 0.4f; p.grime = 0.6f; p.edgeWidth = 0.05f; break;
            case SurfaceType.Asphalt:      p.smoothMin = 0.15f; p.smoothMax = 0.35f; p.detailStrength = 0.5f; p.wear = 0.6f; p.grime = 0.3f; break;
            case SurfaceType.PaintedMetal: p.smoothMin = 0.35f; p.smoothMax = 0.55f; p.detailStrength = 0.5f; p.wear = 0.5f; p.grime = 0.5f; p.second = c => BareMetal; break;
            case SurfaceType.BrushedMetal: p.metallic = 1f; p.smoothMin = 0.45f; p.smoothMax = 0.72f; p.grime = 0.2f; break;
            case SurfaceType.Chrome:       p.metallic = 1f; p.smoothMin = 0.82f; p.smoothMax = 0.97f; p.detailStrength = 0f; p.grime = 0.1f; break;
            case SurfaceType.Glass:        p.smoothMin = 0.96f; p.smoothMax = 0.98f; p.detailStrength = 0f; p.wear = 0f; p.grime = 0.1f; break;
            case SurfaceType.Rubber:       p.smoothMin = 0.15f; p.smoothMax = 0.35f; p.detailStrength = 0.3f; p.wear = 0.2f; p.grime = 0.3f; break;
            case SurfaceType.CarPaint:     p.metallic = 0.25f; p.smoothMin = 0.5f; p.smoothMax = 0.95f; p.flake = 0.5f; p.wear = 0.15f; p.grime = 0.25f; p.detailStrength = 0f;
                                           p.second = c => new Color(0.42f, 0.42f, 0.44f); break;
            case SurfaceType.Cotton:       p.smoothMin = p.smoothMax = 0.15f; p.sheen = 0.6f; p.wear = 0.3f; p.grime = 0.2f; p.detailStrength = 0.7f; break;
            case SurfaceType.Denim:        p.smoothMin = p.smoothMax = 0.2f; p.sheen = 0.3f; p.wear = 0.5f; p.grime = 0.2f; p.detailStrength = 0.8f;
                                           p.second = c => new Color(c.r * 1.6f + 0.15f, c.g * 1.6f + 0.15f, c.b * 1.5f + 0.18f); break;
            case SurfaceType.Wool:         p.smoothMin = p.smoothMax = 0.08f; p.sheen = 0.9f; p.wear = 0.2f; p.grime = 0.2f; p.detailStrength = 0.6f; break;
            case SurfaceType.Leather:      p.smoothMin = 0.25f; p.smoothMax = 0.6f; p.sheen = 0.3f; p.wear = 0.5f; p.grime = 0.3f; p.detailStrength = 0.6f; break;
            case SurfaceType.Vinyl:        p.smoothMin = 0.75f; p.smoothMax = 0.9f; p.detailStrength = 0f; p.wear = 0.1f; p.grime = 0.15f; break;
            // Road markings: worn through to the asphalt (second colour) in patches, faintly retroreflective.
            case SurfaceType.RoadPaint:    p.smoothMin = 0.25f; p.smoothMax = 0.4f; p.detailStrength = 0f; p.wear = 0.6f; p.grime = 0.3f;
                                           p.second = c => new Color(0.06f, 0.06f, 0.07f); break;
            // Hazard striping: the colour and black diagonals, chipped to bare metal on edges.
            case SurfaceType.Hazard:       p.smoothMin = 0.3f; p.smoothMax = 0.45f; p.detailStrength = 0.3f; p.wear = 0.6f; p.grime = 0.5f;
                                           p.second = c => new Color(0.03f, 0.03f, 0.03f); break;
        }
        return p;
    }

    // Set by the editor: saves / loads the material as an asset (key = file name) and returns that asset.
    public static Func<string, Material, Material> EditorHook;

    static readonly Dictionary<string, Material> cache = new Dictionary<string, Material>();
    static Shader shader;

    public static string Key(SurfaceType type, Color c, Variant v) => $"{type}_{ColorUtility.ToHtmlStringRGB(c)}_{v}";

    public static Material Get(SurfaceType type, Color colour, Variant variant = Variant.City)
    {
        string key = Key(type, colour, variant);
        if (cache.TryGetValue(key, out var m) && m != null) return m;
        if (shader == null) shader = Shader.Find(ShaderName);
        if (shader == null) { Debug.LogError($"MaterialLibrary: shader {ShaderName} not found"); shader = Shader.Find("Universal Render Pipeline/Lit"); }
        m = new Material(shader) { name = key, enableInstancing = true };
        Configure(m, type, colour, variant);
        if (EditorHook != null && !Application.isPlaying) m = EditorHook(key, m);
        cache[key] = m;
        return m;
    }

    public static void Configure(Material m, SurfaceType type, Color colour, Variant variant)
    {
        var p = For(type);
        m.SetFloat("_Type", (int)type);
        m.SetColor("_BaseColor", colour);
        m.SetColor("_SecondColor", p.second(colour));
        m.SetFloat("_SmoothMin", p.smoothMin);
        m.SetFloat("_SmoothMax", p.smoothMax);
        m.SetFloat("_Metallic", p.metallic);
        m.SetFloat("_DetailScale", p.detailScale);
        m.SetFloat("_DetailStrength", p.detailStrength);
        m.SetFloat("_Wear", p.wear);
        m.SetFloat("_Grime", variant == Variant.City ? p.grime : p.grime * 0.6f);
        m.SetFloat("_Flake", p.flake);
        m.SetFloat("_Sheen", p.sheen);
        m.SetFloat("_EdgeWidth", p.edgeWidth);
        m.SetFloat("_Space", variant == Variant.City ? 0f : variant == Variant.Skinned ? 2f : 1f);
        m.SetFloat("_EdgeSource", variant == Variant.Smooth ? 3f : variant == Variant.Skinned ? 2f : 1f);
        m.enableInstancing = true;
    }

    public static void ClearCache() => cache.Clear();
}
