using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// The day / night cycle, one per scene. A 24-minute day by default (1 real minute = 1 game hour),
// starting at startHour; T skips 3 hours (testing). Blends between a night look and a day look:
//   night 20:00 - 05:00, dawn 05:00 - 07:00, day 07:00 - 18:00, dusk 18:00 - 20:00 (smoothstep).
// Drives: the directional light (dim cool moon at night, overcast sun by day, its direction following
// the hour), flat ambient (and the ambient probe), the sky gradient (and stars), RenderSettings fog,
// the smog's low / high colours (HeightFogSettings), the cloud deck, the facades' lit-window scale
// (_LitFraction), neon / sign / hologram emission, and the post volume (exposure, split toning).
// Night: dark, blue-green fog, the neon and windows carry the light. Day: flat overcast grey.
// Materials are found by shader in the scene's renderers and restored when this is destroyed (in the
// editor, play mode would otherwise leave the assets changed).
[DefaultExecutionOrder(-50)]
public class TimeOfDay : MonoBehaviour
{
    public static TimeOfDay Instance { get; private set; }

    [Header("Clock")]
    [Tooltip("Real minutes per game day.")]
    public float dayMinutes = 24f;
    [Range(0f, 24f)] public float startHour = 22f;
    [Tooltip("T skips this many hours (testing).")]
    public float skipHours = 3f;
    public Vector2 dawn = new Vector2(5f, 7f), dusk = new Vector2(18f, 20f);

    [System.Serializable]
    public class Look
    {
        public Color fogLow, fogHigh, renderFog, ambient, skyZenith, lightColor;
        public float lightIntensity, litFraction, emission, postExposure, stars;
        public Color splitShadows;
        public Color cloudTop, cloudUnder, cloudCanyon;
    }

    [Header("Night")]
    public Look night = new Look
    {
        fogLow = new Color(0.015f, 0.045f, 0.05f), fogHigh = new Color(0.03f, 0.08f, 0.085f), renderFog = new Color(0.02f, 0.05f, 0.06f),
        ambient = new Color(0.03f, 0.04f, 0.05f), skyZenith = new Color(0.003f, 0.006f, 0.008f),
        lightColor = new Color(0.6f, 0.75f, 0.9f), lightIntensity = 0.12f,
        litFraction = 1f, emission = 1f, postExposure = -0.4f, stars = 0.8f,
        splitShadows = new Color(0.42f, 0.56f, 0.56f),   // a slight teal
        cloudTop = new Color(0.03f, 0.05f, 0.06f), cloudUnder = new Color(0.06f, 0.16f, 0.17f), cloudCanyon = new Color(0.12f, 0.3f, 0.3f),
    };
    [Header("Day (overcast)")]
    public Look day = new Look
    {
        fogLow = new Color(0.30f, 0.31f, 0.32f), fogHigh = new Color(0.48f, 0.49f, 0.50f), renderFog = new Color(0.42f, 0.43f, 0.44f),
        ambient = new Color(0.35f, 0.36f, 0.38f), skyZenith = new Color(0.46f, 0.47f, 0.48f),
        lightColor = new Color(0.93f, 0.96f, 1f), lightIntensity = 0.9f,
        litFraction = 0.3f, emission = 0.35f, postExposure = 0f, stars = 0f,
        splitShadows = new Color(0.5f, 0.5f, 0.5f),      // neutral: no toning
        cloudTop = new Color(0.55f, 0.56f, 0.58f), cloudUnder = new Color(0.42f, 0.43f, 0.45f), cloudCanyon = new Color(0.42f, 0.43f, 0.45f),
    };

    [Header("Light direction")]
    public Vector3 moonEuler = new Vector3(40f, 160f, 0f);
    [Tooltip("Sun elevation at the ends of the day and at noon (deg).")]
    public Vector2 sunElevation = new Vector2(12f, 55f);

    // 0..24
    public float Hour { get; private set; }
    // 0 = night, 1 = day.
    public float DayAmount { get; private set; }

    // Materials (originals kept for scaling and restoring).
    readonly List<(Material m, float v)> facades = new List<(Material, float)>(), holograms = new List<(Material, float)>();
    readonly List<(Material m, Color c)> neons = new List<(Material, Color)>();
    readonly List<(Material m, Color top, Color under, Color canyon)> clouds = new List<(Material, Color, Color, Color)>();
    Material sky; Color skyTop, skyHorizon, skyBottom; float skyStars;
    Light sun;
    Volume volume;
    ColorAdjustments adjust; SplitToning split;
    Camera cam;

    static readonly int LitFraction = Shader.PropertyToID("_LitFraction"), Intensity = Shader.PropertyToID("_Intensity"),
                        BaseColor = Shader.PropertyToID("_BaseColor"), TopColor = Shader.PropertyToID("_TopColor"),
                        HorizonColor = Shader.PropertyToID("_HorizonColor"), BottomColor = Shader.PropertyToID("_BottomColor"),
                        StarBrightness = Shader.PropertyToID("_StarBrightness"), UnderColor = Shader.PropertyToID("_UnderColor"),
                        CanyonGlowColor = Shader.PropertyToID("_CanyonGlowColor");

    void Awake()
    {
        Instance = this;
        Hour = Mathf.Repeat(startHour, 24f);
    }

    void Start()
    {
        foreach (var l in FindObjectsByType<Light>())
            if (l.type == LightType.Directional && (sun == null || l.intensity > sun.intensity)) sun = l;
        foreach (var v in FindObjectsByType<Volume>())
            if (v.isGlobal) { volume = v; break; }
        if (volume != null && volume.sharedProfile != null)
        {
            var p = volume.profile;   // a runtime copy: the asset stays as authored
            p.TryGet(out adjust);
            p.TryGet(out split);
            // The magenta highlight toning is gone for good; shadows follow the look.
            if (split != null) { split.highlights.overrideState = true; split.highlights.value = new Color(0.5f, 0.5f, 0.5f); split.shadows.overrideState = true; }
            if (adjust != null) adjust.postExposure.overrideState = true;
        }
        cam = Camera.main;

        var seen = new HashSet<Material>();
        foreach (var r in FindObjectsByType<Renderer>(FindObjectsInactive.Include))
            foreach (var m in r.sharedMaterials)
            {
                if (m == null || m.shader == null || !seen.Add(m)) continue;
                string sh = m.shader.name;
                if (sh == "FPS/CityFacade" && m.HasProperty(LitFraction)) facades.Add((m, m.GetFloat(LitFraction)));
                else if (sh == "FPS/Hologram" && m.HasProperty(Intensity)) holograms.Add((m, m.GetFloat(Intensity)));
                else if (sh == "FPS/CloudDeck") clouds.Add((m, m.GetColor(TopColor), m.GetColor(UnderColor), m.GetColor(CanyonGlowColor)));
                else if (m.name.StartsWith("Neon") && m.HasProperty(BaseColor)) neons.Add((m, m.GetColor(BaseColor)));
            }
        sky = RenderSettings.skybox;
        if (sky != null && sky.shader != null && sky.shader.name == "FPS/NightSky")
        {
            skyTop = sky.GetColor(TopColor); skyHorizon = sky.GetColor(HorizonColor); skyBottom = sky.GetColor(BottomColor); skyStars = sky.GetFloat(StarBrightness);
        }
        else sky = null;
        Apply();
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
        foreach (var (m, v) in facades) if (m != null) m.SetFloat(LitFraction, v);
        foreach (var (m, v) in holograms) if (m != null) m.SetFloat(Intensity, v);
        foreach (var (m, c) in neons) if (m != null) m.SetColor(BaseColor, c);
        foreach (var (m, t, u, g) in clouds) if (m != null) { m.SetColor(TopColor, t); m.SetColor(UnderColor, u); m.SetColor(CanyonGlowColor, g); }
        if (sky != null) { sky.SetColor(TopColor, skyTop); sky.SetColor(HorizonColor, skyHorizon); sky.SetColor(BottomColor, skyBottom); sky.SetFloat(StarBrightness, skyStars); }
    }

    // Jump the clock (tests, debug).
    public void SetHour(float h) { Hour = Mathf.Repeat(h, 24f); Apply(); }

    void Update()
    {
        var kb = Keyboard.current;
        if (kb != null && kb.tKey.wasPressedThisFrame) Hour = Mathf.Repeat(Hour + skipHours, 24f);
        Hour = Mathf.Repeat(Hour + Time.deltaTime * 24f / Mathf.Max(0.01f, dayMinutes * 60f), 24f);
        Apply();
    }

    public float DayAt(float h)
    {
        float S(float a, float b, float x) { float t = Mathf.Clamp01((x - a) / Mathf.Max(1e-3f, b - a)); return t * t * (3f - 2f * t); }
        return h < 12f ? S(dawn.x, dawn.y, h) : 1f - S(dusk.x, dusk.y, h);
    }

    void Apply()
    {
        float d = DayAt(Hour);
        DayAmount = d;
        Color C(Color a, Color b) => Color.Lerp(a, b, d);
        float F(float a, float b) => Mathf.Lerp(a, b, d);

        // Light: the moon fixed, the sun across the sky from dawn to dusk (east to west, low to noon).
        if (sun != null)
        {
            float span = Mathf.InverseLerp(dawn.x, dusk.y, Hour);                       // 0 at dawn .. 1 at dusk
            float elev = Mathf.Lerp(sunElevation.x, sunElevation.y, Mathf.Sin(Mathf.Clamp01(span) * Mathf.PI));
            Quaternion sunRot = Quaternion.Euler(elev, Mathf.Lerp(80f, 280f, Mathf.Clamp01(span)), 0f);
            sun.transform.rotation = Quaternion.Slerp(Quaternion.Euler(moonEuler), sunRot, d);
            sun.color = C(night.lightColor, day.lightColor);
            sun.intensity = F(night.lightIntensity, day.lightIntensity);
        }

        Color amb = C(night.ambient, day.ambient);
        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = amb;
        var sh = new SphericalHarmonicsL2();
        sh.AddAmbientLight(amb);
        RenderSettings.ambientProbe = sh;

        Color fogLow = C(night.fogLow, day.fogLow), fogHigh = C(night.fogHigh, day.fogHigh), renderFog = C(night.renderFog, day.renderFog);
        RenderSettings.fogColor = renderFog;
        var hf = HeightFogSettings.Active;
        if (hf != null) { hf.lowColor = fogLow; hf.highColor = fogHigh; }
        if (cam != null && cam.clearFlags == CameraClearFlags.SolidColor) cam.backgroundColor = renderFog;

        // Sky: the horizon follows the smog top, the zenith near black at night; flat grey by day.
        if (sky != null)
        {
            sky.SetColor(TopColor, C(night.skyZenith, day.skyZenith));
            sky.SetColor(HorizonColor, fogHigh);
            sky.SetColor(BottomColor, fogLow);
            sky.SetFloat(StarBrightness, F(night.stars, day.stars));
        }

        float lit = F(night.litFraction, day.litFraction), glow = F(night.emission, day.emission);
        foreach (var (m, v) in facades) m.SetFloat(LitFraction, v * lit);
        foreach (var (m, v) in holograms) m.SetFloat(Intensity, v * glow);
        foreach (var (m, c) in neons) m.SetColor(BaseColor, new Color(c.r * glow, c.g * glow, c.b * glow, c.a));
        foreach (var (m, _, _, _) in clouds)
        {
            m.SetColor(TopColor, C(night.cloudTop, day.cloudTop));
            m.SetColor(UnderColor, C(night.cloudUnder, day.cloudUnder));
            m.SetColor(CanyonGlowColor, C(night.cloudCanyon, day.cloudCanyon));
        }

        if (adjust != null) adjust.postExposure.value = F(night.postExposure, day.postExposure);
        if (split != null) split.shadows.value = C(night.splitShadows, day.splitShadows);
    }
}
