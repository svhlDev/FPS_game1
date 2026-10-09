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
//   Sampling (Burst jobs)    coarse + narrow-band fine grid: the body (hands cut off), and each hand
//                            on its own finer grid (BodyRules.hands.cell; fingers are ~1 cm thick)
//   Extract  (worker task)   surface nets + Laplacian smoothing, per piece
//   Project  (Burst jobs)    re-project onto the SDF + regions (for decimation importance)
//   Decimate (worker task)   quadric decimation to the triangle budget (body; each hand its own)
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

    // Grids: one per piece (the body, then each hand). mode = the kernel's cutMode for the piece.
    class Piece
    {
        public byte mode; public float cell; public int target;
        public Vector3 min; public int nx, ny, nz, mx, my, mz;
        public NativeArray<float> coarse, fine; public NativeArray<byte> evaluated;
        public float[] grid;
        public List<Vector3> v; public List<int> t;
        public void Dispose() { if (coarse.IsCreated) coarse.Dispose(); if (fine.IsCreated) fine.Dispose(); if (evaluated.IsCreated) evaluated.Dispose(); }
    }
    readonly List<Piece> pieces = new List<Piece>();
    float handCell;
    // Vertices through the stages (all pieces, concatenated; vertPiece = each vertex's piece mode)
    List<Vector3> verts; List<int> tris; float[] importance; byte[] vertPiece;
    NativeArray<Vector3> pos, nrm; NativeArray<Vector2> reg, lay; NativeArray<BoneWeight> wts; NativeArray<byte> pieceN;
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
        var hr = BodyRules.Default.hands;
        handCell = hr.cell;
        bool hands = sdf.HandCount > 0;
        Piece Add(Bounds b, float cell, byte mode, int target)
        {
            var p = new Piece { mode = mode, cell = cell, target = target };
            p.min = b.min - Vector3.one * cell;
            Vector3 size = b.size + Vector3.one * cell * 2f;
            p.nx = Mathf.CeilToInt(size.x / cell) + 1; p.ny = Mathf.CeilToInt(size.y / cell) + 1; p.nz = Mathf.CeilToInt(size.z / cell) + 1;
            int C = BodyMesher.CoarseStep;
            p.mx = (p.nx - 1) / C + 2; p.my = (p.ny - 1) / C + 2; p.mz = (p.nz - 1) / C + 2;
            p.coarse = new NativeArray<float>(p.mx * p.my * p.mz, Allocator.Persistent);
            p.fine = new NativeArray<float>(p.nx * p.ny * p.nz, Allocator.Persistent);
            p.evaluated = new NativeArray<byte>(p.nx * p.ny * p.nz, Allocator.Persistent);
            var k = sdf.Kernel; k.cutMode = mode;
            var hc = new BodyMesher.CoarseJob { kernel = k, min = p.min, step = cell * C, mx = p.mx, my = p.my, values = p.coarse }.Schedule(p.coarse.Length, 64);
            var hf = new BodyMesher.FineJob { kernel = k, coarse = p.coarse, min = p.min, cell = cell, band = cell * C * 1.9f, nx = p.nx, ny = p.ny, mx = p.mx, my = p.my, values = p.fine, evaluated = p.evaluated }
                .Schedule(p.fine.Length, 256, hc);
            handle = JobHandle.CombineDependencies(handle, hf);
            pieces.Add(p);
            return p;
        }
        handle = default;
        Add(sdf.bounds, ms.cell, (byte)(hands ? 1 : 0), ms.targetTriangles);
        for (int h = 0; h < sdf.HandCount; h++) Add(sdf.HandBounds[h], handCell, (byte)(2 + h), hr.triangles);
        JobHandle.ScheduleBatchedJobs();
        CurrentStage = Stage.Sampling;
    }

    void AfterSampling()
    {
        int iters = ms.smoothIterations; float strength = ms.smoothStrength;
        // (The grids are copied out on the workers; their native arrays are freed after.)
        task = Task.Run(() =>
        {
            // (One worker per body, pieces in turn: the pool runs bodies side by side, and more threads
            // per body only crowd the frame.)
            foreach (var p in pieces)
            {
                p.grid = p.fine.ToArray();
                var v = new List<Vector3>(); var t = new List<int>();
                SurfaceNets.Extract(p.grid, p.nx, p.ny, p.nz, p.min, p.cell, v, t);
                BodyMesher.Smooth(v, t, iters, strength);
                p.v = v; p.t = t; p.grid = null;
            }
            Concat();
        });
        CurrentStage = Stage.Extract;
    }

    // All pieces' vertices and triangles in one list (vertPiece: each vertex's piece mode).
    void Concat()
    {
        verts = new List<Vector3>(); tris = new List<int>();
        var vp = new List<byte>();
        foreach (var p in pieces)
        {
            int o = verts.Count;
            verts.AddRange(p.v);
            foreach (int i in p.t) tris.Add(i + o);
            for (int i = 0; i < p.v.Count; i++) vp.Add(p.mode);
        }
        vertPiece = vp.ToArray();
    }

    void AfterExtract()
    {
        foreach (var p in pieces) p.Dispose();
        int n = verts.Count;
        pos = new NativeArray<Vector3>(verts.ToArray(), Allocator.Persistent);
        nrm = new NativeArray<Vector3>(n, Allocator.Persistent);
        reg = new NativeArray<Vector2>(n, Allocator.Persistent);
        lay = new NativeArray<Vector2>(n, Allocator.Persistent);
        pieceN = new NativeArray<byte>(vertPiece, Allocator.Persistent);
        float cell = ms.cell;
        var hp = new BodyMesher.ProjectJob { kernel = sdf.Kernel, positions = pos, piece = pieceN, cell = cell, handCell = handCell, steps = 3 }.Schedule(n, 64);
        handle = new BodyMesher.AttribJob { kernel = sdf.Kernel, positions = pos, piece = pieceN, h = ms.normalStep * cell, hHand = ms.normalStep * handCell, normals = nrm, region = reg, layers = lay }.Schedule(n, 64, hp);
        JobHandle.ScheduleBatchedJobs();
        CurrentStage = Stage.Project;
    }

    void AfterProject()
    {
        int n = pos.Length;
        importance = new float[n];
        for (int i = 0; i < n; i++) { verts[i] = pos[i]; importance[i] = vertPiece[i] >= 2 ? 1f : BodyMesher.Importance(ms, (BoneKind)(int)reg[i].x); }
        DisposeVertexArrays();
        task = Task.Run(() =>
        {
            // Back into pieces (projected), each decimated to its own budget.
            int o = 0, to = 0;
            foreach (var p in pieces)
            {
                int nv = p.v.Count, nt = p.t.Count;
                for (int i = 0; i < nv; i++) p.v[i] = verts[o + i];
                o += nv; to += nt;
            }
            o = 0;
            var imp = importance;
            var offsets = new int[pieces.Count];
            for (int k = 0; k < pieces.Count; k++) { offsets[k] = o; o += pieces[k].v.Count; }
            for (int k = 0; k < pieces.Count; k++)
            {
                var p = pieces[k];
                if (p.t.Count / 3 <= p.target) continue;
                var pi = new float[p.v.Count];
                System.Array.Copy(imp, offsets[k], pi, 0, pi.Length);
                MeshDecimator.Run(p.v, p.t, pi, p.target, out var dv, out var dt, out _);
                p.v = dv; p.t = dt;
            }
            Concat();
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
        pieceN = new NativeArray<byte>(vertPiece, Allocator.Persistent);
        float cell = ms.cell;
        var hp = new BodyMesher.ProjectJob { kernel = sdf.Kernel, positions = pos, piece = pieceN, cell = cell, handCell = handCell, steps = 3 }.Schedule(n, 64);
        var ha = new BodyMesher.AttribJob { kernel = sdf.Kernel, positions = pos, piece = pieceN, h = ms.normalStep * cell, hHand = ms.normalStep * handCell, normals = nrm, region = reg, layers = lay }.Schedule(n, 64, hp);
        var hw = new BodySkinner.WeightJob { kernel = sdf.Kernel, positions = pos, power = ss.falloffPower, softness = ss.softness, boost = ss.ownBoneBoost, reach = ss.jointReach,
                                             knuckle = BodyRules.Default.hands.knuckleBlend, weights = wts }.Schedule(n, 64, hp);
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
        if (pieceN.IsCreated) pieceN.Dispose();
    }

    void Cleanup()
    {
        try { handle.Complete(); } catch { }
        foreach (var p in pieces) p.Dispose();
        DisposeVertexArrays();
        if (wts.IsCreated) wts.Dispose();
        sdf?.Dispose();
    }
}
