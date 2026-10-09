using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Rendering;

// A finished generated body, shareable by any number of characters (meshes are skinned per renderer):
// the plan (skeleton), the body mesh (submeshes: shirt, pants, skin, shoes - placeholder clothing
// regions, clothing proper is a later doc) and the head mesh, the eyes, and its size for the far tier.
public class BodyAsset
{
    public CharacterSheet sheet;
    public BodyPlan plan;
    public Mesh body, head;
    public Vector3 eyeL, eyeR;          // rest pose, root space
    public float eyeRadius;
    public float height, width;          // standing height, widest torso (for far-tier scaling)
    public int triangles;
    public float wallMs, mainThreadMs;   // generation: start to finish, and main-thread time spent on it
    public const int Shirt = 0, Pants = 1, Skin = 2, Shoes = 3;
}

// Phase 8: generating a body without stalling the frame. A staged state machine, advanced by Step()
// once per frame by whoever owns it (BodyPool):
//   Start    (main, ~1 ms)  plan + SDF, schedule the sampling jobs
//   Sampling (Burst jobs)    coarse + narrow-band fine grid
//   Extract  (worker task)   surface nets + Laplacian smoothing
//   Project  (Burst jobs)    re-project onto the SDF + regions (for decimation importance)
//   Decimate (worker task)   quadric decimation to the triangle budget
//   Final    (Burst jobs)    re-project, normals, regions/layers, skin weights
//   Split    (worker task)   body (4 clothing-region submeshes) and head vertex/index arrays
//   Upload   (main)          Mesh objects
// Only the start and the upload touch the main thread. RunSync() does the same stages in one go.
public class BodyBuild
{
    public enum Stage { Start, Sampling, Extract, Project, Decimate, Final, Split, Done, Failed }
    public Stage CurrentStage { get; private set; } = Stage.Start;
    public BodyAsset Asset { get; private set; }
    public Exception Error { get; private set; }
    // Main-thread milliseconds spent in each stage's step (for the perf report).
    public readonly float[] StageMainMs = new float[9];

    readonly CharacterSheet sheet;
    readonly ISpeciesTemplate template;
    readonly BodyMesher.Settings ms = new BodyMesher.Settings();
    readonly BodySkinner.Settings ss = new BodySkinner.Settings();
    BodyPlan plan;
    BodySDF sdf;
    JobHandle handle;
    Task task;
    readonly System.Diagnostics.Stopwatch wall = new System.Diagnostics.Stopwatch();
    double mainMs;

    // Grid
    Vector3 min; int nx, ny, nz, mx, my, mz;
    NativeArray<float> coarse, fine;
    NativeArray<byte> evaluated;
    // Vertices through the stages
    List<Vector3> verts; List<int> tris; float[] importance;
    NativeArray<Vector3> pos, nrm; NativeArray<Vector2> reg, lay; NativeArray<BoneWeight> wts;
    Parts bodyParts, headParts;
    Matrix4x4[] bindposes;

    class Parts { public Vector3[] v, n; public Vector2[] r, l; public BoneWeight[] w; public List<int>[] subs; }

    public BodyBuild(CharacterSheet sheet, ISpeciesTemplate template = null)
    {
        this.sheet = sheet;
        this.template = template;
    }

    public BodyAsset RunSync()
    {
        while (!Step())
        {
            handle.Complete();
            task?.Wait();
        }
        if (Error != null) throw Error;
        return Asset;
    }

    // Advance one stage if the current one is finished. True when done (or failed).
    public bool Step()
    {
        if (CurrentStage == Stage.Done || CurrentStage == Stage.Failed) return true;
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var stageAtStart = CurrentStage;
        try
        {
            switch (CurrentStage)
            {
                case Stage.Start: StartBuild(); break;
                case Stage.Sampling: if (Ready()) AfterSampling(); break;
                case Stage.Extract: if (TaskDone()) AfterExtract(); break;
                case Stage.Project: if (Ready()) AfterProject(); break;
                case Stage.Decimate: if (TaskDone()) AfterDecimate(); break;
                case Stage.Final: if (Ready()) AfterFinal(); break;
                case Stage.Split: if (TaskDone()) Upload(); break;
            }
        }
        catch (Exception e)
        {
            Error = e;
            CurrentStage = Stage.Failed;
            Cleanup();
            Debug.LogException(e);
        }
        double dms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        mainMs += dms;
        StageMainMs[(int)stageAtStart] += (float)dms;
        return CurrentStage == Stage.Done || CurrentStage == Stage.Failed;
    }

    bool Ready() { if (!handle.IsCompleted) return false; handle.Complete(); return true; }
    bool TaskDone()
    {
        if (!task.IsCompleted) return false;
        if (task.IsFaulted) throw task.Exception.InnerException ?? task.Exception;
        return true;
    }

    // ---------- stages ----------

    void StartBuild()
    {
        wall.Start();
        SurfaceNets.Init();
        plan = BodyPlanner.Generate(sheet, null, template);
        sdf = new BodySDF(plan);
        float cell = ms.cell;
        Bounds b = sdf.bounds;
        min = b.min - Vector3.one * cell;
        Vector3 size = b.size + Vector3.one * cell * 2f;
        nx = Mathf.CeilToInt(size.x / cell) + 1; ny = Mathf.CeilToInt(size.y / cell) + 1; nz = Mathf.CeilToInt(size.z / cell) + 1;
        int C = BodyMesher.CoarseStep;
        mx = (nx - 1) / C + 2; my = (ny - 1) / C + 2; mz = (nz - 1) / C + 2;
        coarse = new NativeArray<float>(mx * my * mz, Allocator.Persistent);
        fine = new NativeArray<float>(nx * ny * nz, Allocator.Persistent);
        evaluated = new NativeArray<byte>(nx * ny * nz, Allocator.Persistent);
        var hc = new BodyMesher.CoarseJob { kernel = sdf.Kernel, min = min, step = cell * C, mx = mx, my = my, values = coarse }.Schedule(coarse.Length, 64);
        handle = new BodyMesher.FineJob { kernel = sdf.Kernel, coarse = coarse, min = min, cell = cell, band = cell * C * 1.9f, nx = nx, ny = ny, mx = mx, my = my, values = fine, evaluated = evaluated }
            .Schedule(fine.Length, 256, hc);
        JobHandle.ScheduleBatchedJobs();
        CurrentStage = Stage.Sampling;
    }

    void AfterSampling()
    {
        var grid = fine.ToArray();
        coarse.Dispose(); fine.Dispose(); evaluated.Dispose();
        int gx = nx, gy = ny, gz = nz; Vector3 gmin = min; float cell = ms.cell;
        int iters = ms.smoothIterations; float strength = ms.smoothStrength;
        task = Task.Run(() =>
        {
            var v = new List<Vector3>(); var t = new List<int>();
            SurfaceNets.Extract(grid, gx, gy, gz, gmin, cell, v, t);
            BodyMesher.Smooth(v, t, iters, strength);
            verts = v; tris = t;
        });
        CurrentStage = Stage.Extract;
    }

    void AfterExtract()
    {
        int n = verts.Count;
        pos = new NativeArray<Vector3>(verts.ToArray(), Allocator.Persistent);
        nrm = new NativeArray<Vector3>(n, Allocator.Persistent);
        reg = new NativeArray<Vector2>(n, Allocator.Persistent);
        lay = new NativeArray<Vector2>(n, Allocator.Persistent);
        float cell = ms.cell;
        var hp = new BodyMesher.ProjectJob { kernel = sdf.Kernel, positions = pos, h = cell * 0.25f, maxStep = cell, steps = 3 }.Schedule(n, 64);
        handle = new BodyMesher.AttribJob { kernel = sdf.Kernel, positions = pos, h = ms.normalStep * cell, normals = nrm, region = reg, layers = lay }.Schedule(n, 64, hp);
        JobHandle.ScheduleBatchedJobs();
        CurrentStage = Stage.Project;
    }

    void AfterProject()
    {
        int n = pos.Length;
        importance = new float[n];
        for (int i = 0; i < n; i++) { verts[i] = pos[i]; importance[i] = BodyMesher.Importance(ms, (BoneKind)(int)reg[i].x); }
        DisposeVertexArrays();
        int target = ms.targetTriangles;
        task = Task.Run(() =>
        {
            if (tris.Count / 3 > target)
            {
                MeshDecimator.Run(verts, tris, importance, target, out var dv, out var dt, out _);
                verts = dv; tris = dt;
            }
        });
        CurrentStage = Stage.Decimate;
    }

    void AfterDecimate()
    {
        int n = verts.Count;
        pos = new NativeArray<Vector3>(verts.ToArray(), Allocator.Persistent);
        nrm = new NativeArray<Vector3>(n, Allocator.Persistent);
        reg = new NativeArray<Vector2>(n, Allocator.Persistent);
        lay = new NativeArray<Vector2>(n, Allocator.Persistent);
        wts = new NativeArray<BoneWeight>(n, Allocator.Persistent);
        float cell = ms.cell;
        var hp = new BodyMesher.ProjectJob { kernel = sdf.Kernel, positions = pos, h = cell * 0.25f, maxStep = cell, steps = 3 }.Schedule(n, 64);
        var ha = new BodyMesher.AttribJob { kernel = sdf.Kernel, positions = pos, h = ms.normalStep * cell, normals = nrm, region = reg, layers = lay }.Schedule(n, 64, hp);
        var hw = new BodySkinner.WeightJob { kernel = sdf.Kernel, positions = pos, power = ss.falloffPower, softness = ss.softness, boost = ss.ownBoneBoost, reach = ss.jointReach, weights = wts }.Schedule(n, 64, hp);
        handle = JobHandle.CombineDependencies(ha, hw);
        JobHandle.ScheduleBatchedJobs();
        CurrentStage = Stage.Final;
    }

    void AfterFinal()
    {
        var V = pos.ToArray(); var N = nrm.ToArray(); var R = reg.ToArray(); var L = lay.ToArray(); var W = wts.ToArray();
        DisposeVertexArrays();
        wts.Dispose();
        bindposes = BodySkinner.BindPoses(sdf);
        var T = tris.ToArray();
        task = Task.Run(() => SplitParts(V, N, R, L, W, T));
        CurrentStage = Stage.Split;
    }

    // Triangles by region (the majority of their vertices): head -> head mesh; the rest -> body
    // submeshes shirt (torso, arms), pants (pelvis, legs), skin (neck, hands), shoes (feet).
    void SplitParts(Vector3[] V, Vector3[] N, Vector2[] R, Vector2[] L, BoneWeight[] W, int[] T)
    {
        int Sub(BoneKind k)
        {
            switch (k)
            {
                case BoneKind.Head: return -1;
                case BoneKind.Pelvis: case BoneKind.Thigh: case BoneKind.Shin: return BodyAsset.Pants;
                case BoneKind.Neck: case BoneKind.Hand: return BodyAsset.Skin;
                case BoneKind.Foot: return BodyAsset.Shoes;
                default: return BodyAsset.Shirt;
            }
        }
        BoneKind K(int v) => (BoneKind)(int)R[v].x;
        Parts Make(int subCount) => new Parts { subs = new List<int>[subCount] };
        var body = Make(4); var head = Make(1);
        for (int i = 0; i < 4; i++) body.subs[i] = new List<int>();
        head.subs[0] = new List<int>();
        var bodyMap = new Dictionary<int, int>(); var headMap = new Dictionary<int, int>();
        var bv = new List<int>(); var hv = new List<int>();
        for (int t = 0; t < T.Length; t += 3)
        {
            int a = T[t], b = T[t + 1], c = T[t + 2];
            int sa = Sub(K(a)), sb = Sub(K(b)), sc = Sub(K(c));
            int s = sa == sb || sa == sc ? sa : sb == sc ? sb : sa;   // majority
            bool isHead = s == -1;
            var map = isHead ? headMap : bodyMap; var list = isHead ? hv : bv;
            var dst = isHead ? head.subs[0] : body.subs[s];
            foreach (int v in new[] { a, b, c })
            {
                if (!map.TryGetValue(v, out int m)) { m = list.Count; map[v] = m; list.Add(v); }
                dst.Add(m);
            }
        }
        void Fill(Parts p, List<int> src)
        {
            int n = src.Count;
            p.v = new Vector3[n]; p.n = new Vector3[n]; p.r = new Vector2[n]; p.l = new Vector2[n]; p.w = new BoneWeight[n];
            for (int i = 0; i < n; i++) { int o = src[i]; p.v[i] = V[o]; p.n[i] = N[o]; p.r[i] = R[o]; p.l[i] = L[o]; p.w[i] = W[o]; }
        }
        Fill(body, bv); Fill(head, hv);
        bodyParts = body; headParts = head;
    }

    void Upload()
    {
        Mesh Build(string name, Parts p)
        {
            var m = new Mesh { name = name, indexFormat = p.v.Length > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            m.vertices = p.v; m.normals = p.n;
            m.SetUVs(1, p.r); m.SetUVs(2, p.l);
            m.subMeshCount = p.subs.Length;
            for (int i = 0; i < p.subs.Length; i++) m.SetTriangles(p.subs[i], i, false);
            m.boneWeights = p.w;
            m.bindposes = bindposes;
            m.RecalculateBounds();
            // (Kept CPU-readable: tests bake skinned meshes; UploadMeshData(true) would free ~0.3 MB per body.)
            return m;
        }
        float width = 0f;
        foreach (var b in plan.bones)
            if (b.kind == BoneKind.Chest || b.kind == BoneKind.Lumbar || b.kind == BoneKind.Pelvis) width = Mathf.Max(width, b.Outer.y * 2f);
        int triCount = 0;
        foreach (var l in bodyParts.subs) triCount += l.Count / 3;
        triCount += headParts.subs[0].Count / 3;
        Asset = new BodyAsset
        {
            sheet = sheet, plan = plan,
            body = Build("Body " + sheet, bodyParts), head = Build("Head " + sheet, headParts),
            eyeL = sdf.EyeL, eyeR = sdf.EyeR, eyeRadius = sdf.EyeRadius,
            height = plan.height, width = width, triangles = triCount,
        };
        sdf.Dispose(); sdf = null;
        wall.Stop();
        Asset.wallMs = (float)wall.Elapsed.TotalMilliseconds;
        Asset.mainThreadMs = (float)mainMs;
        CurrentStage = Stage.Done;
    }

    void DisposeVertexArrays()
    {
        if (pos.IsCreated) pos.Dispose();
        if (nrm.IsCreated) nrm.Dispose();
        if (reg.IsCreated) reg.Dispose();
        if (lay.IsCreated) lay.Dispose();
    }

    void Cleanup()
    {
        try { handle.Complete(); } catch { }
        if (coarse.IsCreated) coarse.Dispose();
        if (fine.IsCreated) fine.Dispose();
        if (evaluated.IsCreated) evaluated.Dispose();
        DisposeVertexArrays();
        if (wts.IsCreated) wts.Dispose();
        sdf?.Dispose();
    }
}
