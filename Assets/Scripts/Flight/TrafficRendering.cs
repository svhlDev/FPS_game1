using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;

// Instanced drawing for every car: bodies, head/tail lights and racks are drawn with
// Graphics.RenderMeshInstanced, one batch per (mesh, material), instead of thousands of renderers.
// Cars keep their GameObjects, colliders and scripts; their MeshRenderers are disabled. Each camera
// draws only the cars inside its frustum and within its Traffic cull distance (CameraCullDistances).
// CarLights still swaps the light renderers' sharedMaterial between on and off; that decides which
// batch a light goes in.
public partial class TrafficSystem
{
    struct DrawPart
    {
        public Transform transform;
        public MeshRenderer renderer;
        public Mesh mesh;
    }

    sealed class Batch
    {
        public Mesh mesh;
        public Material material;
        public readonly List<Matrix4x4> matrices = new List<Matrix4x4>(256);
    }

    const float DefaultTrafficCull = 1000f;
    const int MaxPerCall = 1023;
    static readonly Vector3 CarBoundsSize = new Vector3(9f, 5f, 9f);   // covers any heading
    static readonly Bounds WorldBounds = new Bounds(Vector3.zero, Vector3.one * 100000f);

    static readonly List<FlyingVehicle> drawnCars = new List<FlyingVehicle>();
    static readonly Dictionary<FlyingVehicle, DrawPart[]> drawParts = new Dictionary<FlyingVehicle, DrawPart[]>();
    static readonly Dictionary<(Mesh, Material), Batch> batches = new Dictionary<(Mesh, Material), Batch>();
    static readonly List<Batch> batchList = new List<Batch>();
    static readonly Plane[] frustum = new Plane[6];
    static int trafficLayer = -1;

    // Take over drawing this car. Parts whose material can't be instanced keep their renderer.
    public static void AddRenderable(FlyingVehicle car)
    {
        EnsureInstance();
        if (drawParts.ContainsKey(car)) return;
        var parts = new List<DrawPart>();
        foreach (var r in car.GetComponentsInChildren<MeshRenderer>(true))
        {
            var mf = r.GetComponent<MeshFilter>();
            var mat = r.sharedMaterial;
            if (mf == null || mf.sharedMesh == null || mat == null || !mat.enableInstancing) continue;
            r.enabled = false;
            parts.Add(new DrawPart { transform = r.transform, renderer = r, mesh = mf.sharedMesh });
        }
        drawParts[car] = parts.ToArray();
        drawnCars.Add(car);
        if (trafficLayer < 0) trafficLayer = car.gameObject.layer;
    }

    public static void RemoveRenderable(FlyingVehicle car)
    {
        if (!drawParts.TryGetValue(car, out var parts)) return;
        foreach (var p in parts) if (p.renderer != null) p.renderer.enabled = true;
        drawParts.Remove(car);
        drawnCars.Remove(car);
    }

    void OnEnable() => RenderPipelineManager.beginCameraRendering += DrawForCamera;
    void OnDisable() => RenderPipelineManager.beginCameraRendering -= DrawForCamera;

    static readonly ProfilerMarker DrawMarker = new ProfilerMarker("TrafficSystem.Draw");

    static void DrawForCamera(ScriptableRenderContext context, Camera cam)
    {
        using var _ = DrawMarker.Auto();
        if (drawnCars.Count == 0 || cam.cameraType == CameraType.Preview || cam.cameraType == CameraType.Reflection) return;

        float cull = DefaultTrafficCull;
        var distances = cam.GetComponent<CameraCullDistances>();
        if (distances != null && distances.DistanceFor("Traffic") > 0f) cull = distances.DistanceFor("Traffic");
        float cull2 = cull * cull;
        Vector3 camPos = cam.transform.position;
        GeometryUtility.CalculateFrustumPlanes(cam, frustum);

        foreach (var b in batchList) b.matrices.Clear();
        for (int i = 0; i < drawnCars.Count; i++)
        {
            var car = drawnCars[i];
            if (car == null || !car.isActiveAndEnabled) continue;
            Vector3 pos = car.transform.position;
            if ((pos - camPos).sqrMagnitude > cull2) continue;
            if (!GeometryUtility.TestPlanesAABB(frustum, new Bounds(pos, CarBoundsSize))) continue;
            foreach (var p in drawParts[car])
            {
                var mat = p.renderer.sharedMaterial;
                var key = (p.mesh, mat);
                if (!batches.TryGetValue(key, out var batch))
                {
                    batch = new Batch { mesh = p.mesh, material = mat };
                    batches[key] = batch;
                    batchList.Add(batch);
                }
                batch.matrices.Add(p.transform.localToWorldMatrix);
            }
        }

        foreach (var b in batchList)
        {
            int n = b.matrices.Count;
            if (n == 0) continue;
            var rp = new RenderParams(b.material)
            {
                camera = cam,
                layer = Mathf.Max(0, trafficLayer),
                shadowCastingMode = ShadowCastingMode.Off,
                receiveShadows = false,
                worldBounds = WorldBounds,
            };
            for (int start = 0; start < n; start += MaxPerCall)
                Graphics.RenderMeshInstanced(rp, b.mesh, 0, b.matrices, Mathf.Min(MaxPerCall, n - start), start);
        }
    }
}
