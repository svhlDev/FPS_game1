using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// Graphics Phase 6: one global quality setting for the graphics upgrade. Scales the expensive parts down:
//                    High   Medium   Low
//   detail normals   full   half     off    (_QualityDetailCut: FPS/Surface, BodySkin pores)
//   interior rooms   on     on       off    (_QualityInteriorOff: CityFacade rooms, reveals, streaks)
//   decals           all    70 %     40 %   (_QualityDecalCut: FPS/Decal drops that fraction, by seed)
//   SSAO             on     on       off
//   city lights      128    96       48     (CityLights budget)
//   probe refresh    every 20 frames, 40, 80
// The shader globals are "cuts" (0 = full quality), so a scene that never sets them renders at High.
// Pick with -quality low|medium|high on the command line, the Material Lab panel, or Set() from code;
// the choice is kept in PlayerPrefs.
public static class GraphicsQuality
{
    public enum Level { Low, Medium, High }

    static readonly int DetailCut = Shader.PropertyToID("_QualityDetailCut");
    static readonly int InteriorOff = Shader.PropertyToID("_QualityInteriorOff");
    static readonly int DecalCut = Shader.PropertyToID("_QualityDecalCut");
    const string PrefKey = "GraphicsQuality";

    public static Level Current { get; private set; } = Level.High;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        var level = (Level)Mathf.Clamp(PlayerPrefs.GetInt(PrefKey, (int)Level.High), 0, 2);
        var args = System.Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "-quality" && System.Enum.TryParse(args[i + 1], true, out Level l)) level = l;
        Set(level, false);
    }

    public static void Set(Level level, bool remember = true)
    {
        Current = level;
        Shader.SetGlobalFloat(DetailCut, level == Level.High ? 0f : level == Level.Medium ? 0.5f : 1f);
        Shader.SetGlobalFloat(InteriorOff, level == Level.Low ? 1f : 0f);
        Shader.SetGlobalFloat(DecalCut, level == Level.High ? 0f : level == Level.Medium ? 0.3f : 0.6f);
        if (GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset urp)
            foreach (var d in urp.rendererDataList)
                if (d != null)
                    foreach (var f in d.rendererFeatures)
                        if (f is ScreenSpaceAmbientOcclusion ssao) ssao.SetActive(level != Level.Low);
        var lights = CityLights.Instance;
        if (lights != null) { lights.maxActive = level == Level.High ? 128 : level == Level.Medium ? 96 : 48; lights.Refresh(); }
        var probes = CityProbes.Instance;
        if (probes != null) probes.refreshFrames = level == Level.High ? 20 : level == Level.Medium ? 40 : 80;
        if (remember) PlayerPrefs.SetInt(PrefKey, (int)level);
    }
}
