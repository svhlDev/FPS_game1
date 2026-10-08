using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

// Full-screen height fog / smog, after transparents and before post-processing. Reconstructs world
// position from the camera depth texture and integrates exponential height fog analytically along the
// view ray (no raymarching, so no noise). Parameters come from the scene's HeightFogSettings; without
// one, the pass does nothing.
public class HeightFogFeature : ScriptableRendererFeature
{
    [Tooltip("Hidden/FPS/HeightFog. Referenced here so builds include it.")]
    public Shader shader;

    Material material;
    HeightFogPass pass;

    static readonly int SmogTop = Shader.PropertyToID("_SmogTop");
    static readonly int SmogTopVariation = Shader.PropertyToID("_SmogTopVariation");
    static readonly int SmogFalloff = Shader.PropertyToID("_SmogFalloff");
    static readonly int SmogDensity = Shader.PropertyToID("_SmogDensity");
    static readonly int DistrictCell = Shader.PropertyToID("_DistrictCell");
    static readonly int SkyDistance = Shader.PropertyToID("_SkyDistance");
    static readonly int LowColor = Shader.PropertyToID("_SmogLowColor");
    static readonly int HighColor = Shader.PropertyToID("_SmogHighColor");

    public override void Create()
    {
        pass = new HeightFogPass { renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing };
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        var s = HeightFogSettings.Active;
        if (s == null || shader == null) return;
        var type = renderingData.cameraData.cameraType;
        if (type == CameraType.Preview || type == CameraType.Reflection) return;

        if (material == null) material = CoreUtils.CreateEngineMaterial(shader);
        material.SetFloat(SmogTop, s.smogTop);
        material.SetFloat(SmogTopVariation, s.smogTopVariation);
        material.SetFloat(SmogFalloff, Mathf.Max(0.01f, s.smogFalloff));
        material.SetFloat(SmogDensity, s.smogDensity);
        material.SetFloat(DistrictCell, Mathf.Max(1f, s.districtCellSize));
        material.SetFloat(SkyDistance, s.skyDistance);
        material.SetColor(LowColor, s.lowColor);
        material.SetColor(HighColor, s.highColor);

        pass.material = material;
        pass.ConfigureInput(ScriptableRenderPassInput.Depth);
        renderer.EnqueuePass(pass);
    }

    protected override void Dispose(bool disposing) => CoreUtils.Destroy(material);

    class HeightFogPass : ScriptableRenderPass
    {
        public Material material;

        class PassData
        {
            public TextureHandle source;
            public Material material;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            var resources = frameData.Get<UniversalResourceData>();
            if (material == null || resources.isActiveTargetBackBuffer) return;

            TextureHandle source = resources.activeColorTexture;
            var desc = renderGraph.GetTextureDesc(source);
            desc.name = "_HeightFogColor";
            desc.clearBuffer = false;
            TextureHandle dest = renderGraph.CreateTexture(desc);

            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Height Fog", out var data))
            {
                data.source = source;
                data.material = material;
                builder.UseTexture(source);
                if (resources.cameraDepthTexture.IsValid()) builder.UseTexture(resources.cameraDepthTexture);
                builder.UseAllGlobalTextures(true);
                builder.SetRenderAttachment(dest, 0);
                builder.SetRenderFunc((PassData d, RasterGraphContext ctx) =>
                    Blitter.BlitTexture(ctx.cmd, d.source, new Vector4(1f, 1f, 0f, 0f), d.material, 0));
            }
            resources.cameraColor = dest;
        }
    }
}
