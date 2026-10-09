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
    static readonly Vector3 CarBoundsSize = new Vector3(5f, 3f, 5f);   // covers any heading (car 1.6 x 1.15 x 4)
    static readonly Bounds WorldBounds = new Bounds(Vector3.zero, Vector3.one * 100000f);

    static readonly List<FlyingVehicle> drawnCars = new List<FlyingVehicle>();
    static readonly Dictionary<FlyingVehicle, DrawPart[]> drawParts = new Dictionary<FlyingVehicle, DrawPart[]>();
    static readonly Dictionary<(Mesh, Material), Batch> batches = new Dictionary<(Mesh, Material), Batch>();
    static readonly List<Batch> batchList = new List<Batch>();
    static readonly Plane[] frustum = new Plane[6];
    static int trafficLayer = -1;

    // Occlusion: a CullingGroup on the main camera with one sphere per car. It uses the scene's baked
    // occlusion data, so cars hidden behind buildings aren't drawn. Results lag a frame (the spheres
    // are padded for that). Off with -noocclusion (benchmark comparison) or when there's no bake.
    static CullingGroup cullGroup;
    static Camera cullCamera;
    static BoundingSphere[] spheres = new BoundingSphere[256];
    public static bool OcclusionEnabled = !System.Array.Exists(System.Environment.GetCommandLineArgs(), a => a == "-noocclusion");

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

    // A part's mesh was replaced (a dented body): pick up the new mesh.
    public static void RefreshRenderable(FlyingVehicle car)
    {
        if (car == null || !drawParts.TryGetValue(car, out var parts)) return;
        for (int i = 0; i < parts.Length; i++)
        {
            var mf = parts[i].renderer != null ? parts[i].renderer.GetComponent<MeshFilter>() : null;
            if (mf != null && mf.sharedMesh != null) parts[i].mesh = mf.sharedMesh;
        }
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
    static int groupCount = -1;
    static bool groupValid;

    // Spheres follow the cars; the camera's culling computes their visibility this frame and it is read
    // next frame. A changed car count invalidates the results for one frame (everything is drawn).
    static void UpdateCullGroup(Camera cam)
    {
        if (cullGroup == null || cullCamera != cam)
        {
            cullGroup?.Dispose();
            cullGroup = new CullingGroup { targetCamera = cam };
            cullCamera = cam;
            cullGroup.SetBoundingSpheres(spheres);
            groupCount = -1;
        }
        int n = drawnCars.Count;
        if (spheres.Length < n)
        {
            spheres = new BoundingSphere[Mathf.NextPowerOfTwo(n)];
            cullGroup.SetBoundingSpheres(spheres);
            groupCount = -1;
        }
        for (int i = 0; i < n; i++)
        {
            var car = drawnCars[i];
            spheres[i] = new BoundingSphere(car != null ? car.transform.position : Vector3.zero, 6f);
        }
        groupValid = n == groupCount;
        if (!groupValid) { cullGroup.SetBoundingSphereCount(n); groupCount = n; }
    }

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

        bool useGroup = OcclusionEnabled && cam == Camera.main && cam.useOcclusionCulling;
        if (useGroup) UpdateCullGroup(cam);

        foreach (var b in batchList) b.matrices.Clear();
        for (int i = 0; i < drawnCars.Count; i++)
        {
            var car = drawnCars[i];
            if (car == null || !car.isActiveAndEnabled) continue;
            Vector3 pos = car.transform.position;
            if ((pos - camPos).sqrMagnitude > cull2) continue;
            if (!GeometryUtility.TestPlanesAABB(frustum, new Bounds(pos, CarBoundsSize))) continue;
            if (useGroup && groupValid && !cullGroup.IsVisible(i)) continue; // behind a building (last frame)
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
