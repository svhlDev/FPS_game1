using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// Graphics Phase 1, the lighting stack (builder side):
//   - real unshadowed lights for street lamps and blade signs, collected while a scene is built and handed
//     to its CityLights budget in SetupAtmosphere;
//   - reflection probes on a grid through the open space (streets, alleys, canyons), skipping points
//     inside buildings, handed to CityProbes (rendered at runtime, so they follow the day / night cycle);
//   - SSAO on the PC renderer, HDR colour grading.
public static partial class CityDressing
{
    static readonly List<Light> pendingLights = new List<Light>();
    static readonly List<CityLights.Kind> pendingKinds = new List<CityLights.Kind>();
    static readonly List<ReflectionProbe> pendingProbes = new List<ReflectionProbe>();

    static void ClearPendingLighting() { pendingLights.Clear(); pendingKinds.Clear(); pendingProbes.Clear(); }

    // Lamp: a wide warm spot straight down from the lamp head.
    public const float LampLightIntensity = 300f, LampLightRange = 20f;
    // Sign: a point light in front of the sign, the sign's colour.
    public const float SignLightIntensity = 160f, SignLightRange = 22f;

    public static Light AddCityLight(Transform parent, string name, Vector3 pos, Quaternion rot, LightType type, Color color,
                                     float intensity, float range, float spotAngle, CityLights.Kind kind)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.SetPositionAndRotation(pos, rot);
        var l = go.AddComponent<Light>();
        l.type = type; l.color = color; l.intensity = intensity; l.range = range;
        if (type == LightType.Spot) { l.spotAngle = spotAngle; l.innerSpotAngle = spotAngle * 0.55f; }
        l.shadows = LightShadows.None;
        l.renderMode = LightRenderMode.ForcePixel;
        l.lightmapBakeType = LightmapBakeType.Realtime;
        l.enabled = false; // CityLights switches it on near the camera
        pendingLights.Add(l); pendingKinds.Add(kind);
        return l;
    }

    public static void AddLampLight(Transform parent, Vector3 headPos) =>
        AddCityLight(parent, "LampLight", headPos + Vector3.down * 0.25f, Quaternion.LookRotation(Vector3.down, Vector3.forward), LightType.Spot,
                     new Color(1f, 0.82f, 0.6f), LampLightIntensity, LampLightRange, 130f, CityLights.Kind.Lamp);

    public static void AddSignLight(Transform parent, Vector3 signFront, Material neon)
    {
        Color c = neon.HasProperty("_BaseColor") ? neon.GetColor("_BaseColor") : Color.white;
        float m = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
        if (m > 1e-4f) c /= m;
        c.a = 1f;
        AddCityLight(parent, "SignLight", signFront, Quaternion.identity, LightType.Point, c, SignLightIntensity, SignLightRange, 0f, CityLights.Kind.Sign);
    }

    // Probes every `spacing` metres over `area` at the given heights, wherever a probe isn't inside
    // geometry (needs the colliders in place). Box projection, boxes as big as the spacing.
    public static int AddProbeGrid(Transform parent, Bounds area, float spacing, float[] heights, float boxHeight, int resolution = 64)
    {
        Physics.SyncTransforms();
        var root = new GameObject("ReflectionProbes").transform;
        root.SetParent(parent, false);
        int count = 0;
        int nx = Mathf.Max(1, Mathf.RoundToInt(area.size.x / spacing)), nz = Mathf.Max(1, Mathf.RoundToInt(area.size.z / spacing));
        int mask = ~0;
        int traffic = LayerMask.NameToLayer("Traffic"), player = LayerMask.NameToLayer("Player");
        if (traffic >= 0) mask &= ~(1 << traffic);
        if (player >= 0) mask &= ~(1 << player);
        foreach (float y in heights)
            for (int i = 0; i < nx; i++)
                for (int k = 0; k < nz; k++)
                {
                    var p = new Vector3(area.min.x + (i + 0.5f) * area.size.x / nx, y, area.min.z + (k + 0.5f) * area.size.z / nz);
                    if (Physics.CheckSphere(p, 1.5f, ~0, QueryTriggerInteraction.Ignore)) continue;
                    AddProbe(root, p, new Vector3(spacing * 1.1f, boxHeight, spacing * 1.1f), resolution, mask);
                    count++;
                }
        return count;
    }

    public static ReflectionProbe AddProbe(Transform parent, Vector3 pos, Vector3 size, int resolution, int cullingMask = ~0)
    {
        var go = new GameObject("Probe");
        go.transform.SetParent(parent, false);
        go.transform.position = pos;
        var p = go.AddComponent<ReflectionProbe>();
        p.mode = ReflectionProbeMode.Realtime;
        p.refreshMode = ReflectionProbeRefreshMode.ViaScripting;
        p.timeSlicingMode = ReflectionProbeTimeSlicingMode.NoTimeSlicing;
        p.resolution = resolution;
        p.hdr = true;
        p.boxProjection = true;
        p.size = size;
        p.blendDistance = 4f;
        p.nearClipPlane = 0.5f;
        p.farClipPlane = 400f;
        p.cullingMask = cullingMask;
        p.shadowDistance = 0f;
        pendingProbes.Add(p);
        return p;
    }

    // Called from SetupAtmosphere: the scene's light budget and probe refresher get everything placed so far.
    static void FinishLighting()
    {
        var cl = new GameObject("CityLights").AddComponent<CityLights>();
        cl.lights = new List<Light>(pendingLights);
        cl.kinds = new List<CityLights.Kind>(pendingKinds);
        var cp = new GameObject("CityProbes").AddComponent<CityProbes>();
        cp.probes = new List<ReflectionProbe>(pendingProbes);
        Debug.Log($"City lighting: {pendingLights.Count} lights ({Count(CityLights.Kind.Lamp)} lamps, {Count(CityLights.Kind.Sign)} signs), {pendingProbes.Count} reflection probes");
        ClearPendingLighting();
    }

    static int Count(CityLights.Kind k) { int n = 0; foreach (var x in pendingKinds) if (x == k) n++; return n; }

    // ---------- renderer: SSAO, HDR grading ----------

    public const float SsaoIntensity = 0.6f, SsaoRadius = 0.3f, SsaoDirect = 0.15f, SsaoFalloff = 60f;

    // Adds URP's SSAO feature (depth normals source) to every URP renderer, and switches colour grading
    // to HDR in every URP asset. Same steps as the renderer's "Add Renderer Feature" button; the settings
    // are internal to URP, so they go through SerializedObject.
    public static void EnsureLightingStack()
    {
        foreach (var guid in AssetDatabase.FindAssets("t:UniversalRendererData"))
        {
            var data = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(AssetDatabase.GUIDToAssetPath(guid));
            if (data == null) continue;
            ScreenSpaceAmbientOcclusion ssao = null;
            foreach (var f in data.rendererFeatures) if (f is ScreenSpaceAmbientOcclusion s) ssao = s;
            if (ssao == null)
            {
                ssao = ScriptableObject.CreateInstance<ScreenSpaceAmbientOcclusion>();
                ssao.name = "SSAO";
                ssao.hideFlags |= HideFlags.HideInHierarchy;
                AssetDatabase.AddObjectToAsset(ssao, data);
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(ssao, out _, out long localId);
                var so = new SerializedObject(data);
                var list = so.FindProperty("m_RendererFeatures");
                var map = so.FindProperty("m_RendererFeatureMap");
                list.arraySize++;
                list.GetArrayElementAtIndex(list.arraySize - 1).objectReferenceValue = ssao;
                map.arraySize++;
                map.GetArrayElementAtIndex(map.arraySize - 1).longValue = localId;
                so.ApplyModifiedProperties();
            }
            var fs = new SerializedObject(ssao);
            void F(string n, float v) { var p = fs.FindProperty("m_Settings." + n); if (p != null) p.floatValue = v; else Debug.LogWarning("SSAO setting not found: " + n); }
            void E(string n, int v) { var p = fs.FindProperty("m_Settings." + n); if (p != null) p.enumValueIndex = v; else Debug.LogWarning("SSAO setting not found: " + n); }
            void B(string n, bool v) { var p = fs.FindProperty("m_Settings." + n); if (p != null) p.boolValue = v; else Debug.LogWarning("SSAO setting not found: " + n); }
            E("Source", (int)ScreenSpaceAmbientOcclusionSettingsSource.DepthNormals);
            F("Intensity", SsaoIntensity);
            F("Radius", SsaoRadius);
            F("DirectLightingStrength", SsaoDirect);
            F("Falloff", SsaoFalloff);
            B("Downsample", false);
            B("AfterOpaque", false);
            fs.ApplyModifiedProperties();
            ssao.SetActive(true);
            EditorUtility.SetDirty(ssao);
            EditorUtility.SetDirty(data);
        }
        foreach (var guid in AssetDatabase.FindAssets("t:UniversalRenderPipelineAsset"))
        {
            var asset = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(AssetDatabase.GUIDToAssetPath(guid));
            if (asset == null) continue;
            var so = new SerializedObject(asset);
            var grading = so.FindProperty("m_ColorGradingMode");
            if (grading != null && grading.intValue != (int)ColorGradingMode.HighDynamicRange)
            {
                grading.intValue = (int)ColorGradingMode.HighDynamicRange;
                so.ApplyModifiedProperties();
                EditorUtility.SetDirty(asset);
            }
        }
        AssetDatabase.SaveAssets();
    }

    // DepthSource is internal to URP: DepthNormals is index 1 (Depth = 0).
    enum ScreenSpaceAmbientOcclusionSettingsSource { Depth = 0, DepthNormals = 1 }

    [MenuItem("Tools/Graphics/Install Lighting Stack (SSAO, HDR grading)")]
    static void InstallMenu() { EnsureLightingStack(); Debug.Log("Lighting stack installed"); }
}
