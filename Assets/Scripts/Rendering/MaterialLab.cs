using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// Graphics Upgrade Phase 0: the Material Lab (Tools > Build Material Lab -> Assets/Scenes/MaterialLab.unity).
// Rows of test shapes per material, characters in each outfit, a car, a weapon, a road and a facade,
// under the city's night lighting with slowly orbiting neon lights. Everything in the graphics upgrade
// is tuned here first.
//   Fly camera: hold right mouse to look, WASD / QE to move, Shift faster, F frames the next exhibit.
//   Panel (Tab hides it): lighting presets (night neon, overcast, shaft beam), wetness, a toggle per
//   feature (GraphicsFeatures: every phase registers its own, for before / after), a distance slider
//   (dollies the camera away from the focus, to check far filtering).
public class MaterialLab : MonoBehaviour
{
    public enum Preset { NightNeon, Overcast, ShaftBeam }

    [Header("Built by the scene builder")]
    public Transform focus;                 // what the distance slider dollies away from
    public Transform characterRow, gunStand;
    public Light beam;                      // the shaft daylight beam (preset 3)
    public List<Light> neon = new List<Light>();
    public List<Transform> exhibits = new List<Transform>();
    public ReflectionProbe probe;

    [Header("Orbiting neon")]
    public float orbitSpeed = 9f;           // degrees per second
    public float orbitRadius = 14f;

    public Preset CurrentPreset { get; private set; }
    public bool Wet { get; private set; }
    public float Distance { get; private set; } = 12f;
    public static MaterialLab Instance { get; private set; }

    Camera cam;
    float yaw = 180f, pitch = 12f;
    bool panel = true;
    readonly List<float> neonBase = new List<float>();
    readonly List<float> neonAngle = new List<float>();
    int exhibit;

    void Awake() => Instance = this;

    void Start()
    {
        cam = Camera.main;
        foreach (var l in neon)
        {
            neonBase.Add(l.intensity);
            Vector3 d = l.transform.position - focus.position;
            neonAngle.Add(Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg);
        }
        SpawnCharacters();
        if (gunStand != null)
        {
            var g = Weapon.BuildTGun(gunStand, gunStand.gameObject.layer, Weapon.Mode.Stun);
            g.transform.localPosition = Vector3.zero; g.transform.localRotation = Quaternion.Euler(0f, 90f, 0f);
        }
        if (probe != null) probe.RenderProbe();
        RegisterFeatures();
        SetPreset(Preset.NightNeon);
        var t = focus.position;
        cam.transform.SetPositionAndRotation(t + new Vector3(0f, 3f, -Distance), Quaternion.LookRotation(new Vector3(0f, -0.2f, 1f)));
        yaw = cam.transform.eulerAngles.y; pitch = cam.transform.eulerAngles.x;
    }

    // One of each outfit: casual T-shirt, long sleeves, the trench coat, the police uniform; both sexes.
    void SpawnCharacters()
    {
        if (characterRow == null) return;
        var coat = Outfit.Casual(); coat.garments.Add(GarmentDef.Coat());
        var looks = new (string, CharacterSheet, CharacterFigure.Role, Outfit)[]
        {
            ("T-shirt", new CharacterSheet(Sex.Male, 10, 10, 10, 2001), CharacterFigure.Role.Civilian, Outfit.Casual(false)),
            ("Long sleeves", new CharacterSheet(Sex.Female, 10, 10, 12, 2002), CharacterFigure.Role.Civilian, Outfit.Casual(true)),
            ("Trench coat", new CharacterSheet(Sex.Male, 13, 10, 9, 2003), CharacterFigure.Role.Civilian, coat),
            ("Police", new CharacterSheet(Sex.Male, 14, 10, 13, 2004), CharacterFigure.Role.Police, Wardrobe.For(CharacterFigure.Role.Police, 2004)),
            ("Heavy, coat", new CharacterSheet(Sex.Female, 11, 10, 2, 2005), CharacterFigure.Role.Civilian, coat),
        };
        for (int i = 0; i < looks.Length; i++)
        {
            var (name, sheet, role, outfit) = looks[i];
            var go = new GameObject(name);
            go.transform.SetParent(characterRow, false);
            go.transform.localPosition = new Vector3(i * 1.3f, 0f, 0f);
            go.transform.localRotation = Quaternion.identity;
            CharacterFigure.BuildGenerated(go.transform, sheet, role, true, false, null, null, outfit);
            var a = go.AddComponent<FigureAnimator>(); a.Grounded = true;
            exhibits.Add(go.transform);
        }
    }

    // ---------- presets ----------

    public void SetPreset(Preset p)
    {
        CurrentPreset = p;
        var tod = TimeOfDay.Instance;
        if (tod != null) { tod.freeze = true; tod.SetHour(p == Preset.Overcast ? 12f : 22f); }
        for (int i = 0; i < neon.Count; i++) neon[i].intensity = neonBase[i] * (p == Preset.Overcast ? 0.3f : 1f);
        if (beam != null) beam.enabled = p == Preset.ShaftBeam;
    }

    public void SetWet(bool wet) { Wet = wet; Shader.SetGlobalFloat("_GlobalWetness", wet ? 1f : 0f); }

    public void SetDistance(float d)
    {
        Distance = d;
        if (cam == null || focus == null) return;
        Vector3 dir = cam.transform.forward;
        cam.transform.position = focus.position - dir * d;
    }

    void RegisterFeatures()
    {
        var vol = FindAnyObjectByType<Volume>();
        if (vol != null) GraphicsFeatures.Register("Post-processing", () => vol.enabled, v => vol.enabled = v);
        var fog = HeightFogSettings.Active;
        if (fog != null) GraphicsFeatures.Register("Height fog", () => fog.enabled, v => fog.enabled = v);
        GraphicsFeatures.Register("Distance fog", () => RenderSettings.fog, v => RenderSettings.fog = v);
        GraphicsFeatures.Register("Orbiting neon", () => neon.Count > 0 && neon[0].enabled, v => { foreach (var l in neon) l.enabled = v; });
        if (probe != null) GraphicsFeatures.Register("Reflection probe", () => probe.enabled, v => probe.enabled = v);
        var sun = RenderSettings.sun;
        if (sun == null) foreach (var l in FindObjectsByType<Light>()) if (l.type == LightType.Directional && l != beam) { sun = l; break; }
        if (sun != null) GraphicsFeatures.Register("Moon / sun shadows", () => sun.shadows != LightShadows.None, v => sun.shadows = v ? LightShadows.Soft : LightShadows.None);
        GraphicsFeatures.RegisterLightingStack();
    }

    // ---------- runtime ----------

    void Update()
    {
        // Neon orbits the focus.
        for (int i = 0; i < neon.Count; i++)
        {
            neonAngle[i] += orbitSpeed * Time.deltaTime * (i % 2 == 0 ? 1f : -0.7f);
            var l = neon[i].transform;
            float a = neonAngle[i] * Mathf.Deg2Rad;
            l.position = new Vector3(focus.position.x + Mathf.Sin(a) * orbitRadius, l.position.y, focus.position.z + Mathf.Cos(a) * orbitRadius);
            if (neon[i].type == LightType.Spot) l.rotation = Quaternion.LookRotation(focus.position - l.position);
        }
        FlyCamera();
    }

    void FlyCamera()
    {
        var kb = Keyboard.current; var mouse = Mouse.current;
        if (cam == null || kb == null || mouse == null) return;
        if (kb.tabKey.wasPressedThisFrame) panel = !panel;
        if (kb.fKey.wasPressedThisFrame && exhibits.Count > 0)
        {
            var e = exhibits[exhibit++ % exhibits.Count];
            focus.position = e.position + Vector3.up * 0.9f;
            cam.transform.position = focus.position - cam.transform.forward * Distance;
        }
        if (mouse.rightButton.isPressed)
        {
            Vector2 d = mouse.delta.ReadValue() * 0.15f;
            yaw += d.x; pitch = Mathf.Clamp(pitch - d.y, -85f, 85f);
            cam.transform.rotation = Quaternion.Euler(pitch, yaw, 0f);
        }
        Vector3 move = Vector3.zero;
        if (kb.wKey.isPressed) move += Vector3.forward; if (kb.sKey.isPressed) move += Vector3.back;
        if (kb.aKey.isPressed) move += Vector3.left; if (kb.dKey.isPressed) move += Vector3.right;
        if (kb.eKey.isPressed) move += Vector3.up; if (kb.qKey.isPressed) move += Vector3.down;
        float speed = kb.leftShiftKey.isPressed ? 20f : 5f;
        cam.transform.position += cam.transform.rotation * move * speed * Time.deltaTime;
    }

    void OnGUI()
    {
        if (!panel) return;
        GUILayout.BeginArea(new Rect(10, 10, 270, Screen.height - 20), GUI.skin.box);
        GUILayout.Label("<b>Material Lab</b>  (Tab hides, F next exhibit, RMB look, WASD/QE)", new GUIStyle(GUI.skin.label) { richText = true, wordWrap = true });
        GUILayout.Label("Lighting");
        foreach (Preset p in Enum.GetValues(typeof(Preset)))
            if (GUILayout.Toggle(CurrentPreset == p, p.ToString(), GUI.skin.button) && CurrentPreset != p) SetPreset(p);
        GUILayout.BeginHorizontal();
        foreach (GraphicsQuality.Level q in Enum.GetValues(typeof(GraphicsQuality.Level)))
            if (GUILayout.Toggle(GraphicsQuality.Current == q, q.ToString(), GUI.skin.button) && GraphicsQuality.Current != q) GraphicsQuality.Set(q);
        GUILayout.EndHorizontal();
        bool wet = GUILayout.Toggle(Wet, "Wet");
        if (wet != Wet) SetWet(wet);
        GUILayout.Space(6);
        GUILayout.Label("Features (before / after)");
        foreach (var f in GraphicsFeatures.All)
        {
            bool v = f.get(), nv = GUILayout.Toggle(v, f.name);
            if (nv != v) f.set(nv);
        }
        GUILayout.Space(6);
        GUILayout.Label($"Distance {Distance:0} m");
        float d = GUILayout.HorizontalSlider(Distance, 2f, 250f);
        if (Mathf.Abs(d - Distance) > 0.01f) SetDistance(d);
        GUILayout.EndArea();
    }
}

// Graphics features that can be switched on and off (Material Lab before / after; later a quality
// setting). Each phase registers its own.
public static class GraphicsFeatures
{
    public class Feature { public string name; public Func<bool> get; public Action<bool> set; }
    static readonly List<Feature> all = new List<Feature>();
    public static IReadOnlyList<Feature> All => all;

    public static void Register(string name, Func<bool> get, Action<bool> set)
    {
        all.RemoveAll(f => f.name == name);
        all.Add(new Feature { name = name, get = get, set = set });
    }

    public static bool Set(string name, bool on)
    {
        var f = all.Find(x => x.name == name);
        if (f == null) return false;
        f.set(on);
        return true;
    }

    public static bool Get(string name) { var f = all.Find(x => x.name == name); return f != null && f.get(); }

    // Phase 1, the lighting stack: SSAO (the renderer feature), the city's lights and probes, the
    // tonemapper (ACES, off = Neutral), film grain. Note: switching SSAO in the editor's play mode changes
    // the renderer asset itself.
    public const string Ssao = "SSAO", CityLightsName = "City lights", CityProbesName = "City reflection probes",
                        Aces = "ACES tonemap (off: Neutral)", Grain = "Film grain";

    public static void RegisterLightingStack()
    {
        if (GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset urp)
            foreach (var d in urp.rendererDataList)
                if (d != null)
                    foreach (var f in d.rendererFeatures)
                        if (f is ScreenSpaceAmbientOcclusion s) { Register(Ssao, () => s.isActive, v => s.SetActive(v)); break; }
        var lights = CityLights.Instance;
        if (lights != null) Register(CityLightsName, () => lights.On, v => { lights.On = v; lights.Refresh(); });
        var probes = CityProbes.Instance;
        if (probes != null && probes.probes.Count > 0) Register(CityProbesName, () => probes.On, v => probes.SetOn(v));
        Volume vol = null;
        foreach (var v in UnityEngine.Object.FindObjectsByType<Volume>()) if (v.isGlobal) { vol = v; break; }
        if (vol != null && vol.sharedProfile != null)
        {
            var p = vol.profile; // the runtime copy (TimeOfDay uses the same one)
            if (p.TryGet(out Tonemapping tone))
                Register(Aces, () => tone.mode.value == TonemappingMode.ACES, v => tone.mode.value = v ? TonemappingMode.ACES : TonemappingMode.Neutral);
            if (p.TryGet(out FilmGrain grain))
                Register(Grain, () => grain.active, v => grain.active = v);
        }
    }
}
