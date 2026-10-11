using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Rendering;

// A finished generated body, shareable by any number of characters (meshes are skinned per renderer):
// the plan (skeleton), the body mesh (submesh 0: skin; then one submesh per garment of the outfit, in
// outfit order) and the head mesh, the eyes, and its size for the far tier.
public class BodyAsset
{
    public CharacterSheet sheet;
    public BodyPlan plan;
    public Outfit outfit;
    public Mesh body, head;
    public Vector3 eyeL, eyeR;          // rest pose, root space
    public float eyeRadius;
    public float height, width;          // standing height, widest torso (for far-tier scaling)
    public int triangles, skinTriangles, skinTrianglesBeforeRemoval;
    public int[] garmentTriangles;       // per garment, after hidden parts were dropped
    public float wallMs, mainThreadMs;   // generation: start to finish, and main-thread time spent on it
    public const int Skin = 0;           // submesh; garment g is submesh 1 + g
}

// Phase 8: generating a body without stalling the frame. A staged state machine, advanced by Step()
// once per frame by whoever owns it (BodyPool):
//   Start    (main, ~1 ms)  plan + SDF; schedule the sampling jobs: the body (hands cut off), each hand
//                            on its own finer grid, and (Clothing Part A) each garment of the outfit:
//                            its field (closing + drape on the body and the garments before it, chained),
//                            then its surface sampled on its own grid
//   Sampling (Burst jobs)    coarse + narrow-band fine grids, garment fields
//   Extract  (worker task)   surface nets + Laplacian smoothing, per piece
//   Project  (Burst jobs)    re-project onto the SDF / garment surfaces, regions, which skin is covered
//   Decimate (worker task)   quadric decimation, each piece to its own budget (covered skin cheap)
//   Final    (Burst jobs)    re-project, normals, regions/layers, skin weights (garments: the body's,
//                            at the body point under them), cover flags
//   Split    (worker task)   head mesh; body mesh = visible skin + one submesh per garment (covered skin
//                            and garment parts hidden under outer layers dropped; garment weights smoothed)
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
    // Wall-clock milliseconds each stage took (start of the stage to the step that finished it).
    public readonly float[] StageWallMs = new float[9];
    double stageStart;

    readonly CharacterSheet sheet;
    readonly ISpeciesTemplate template;
    readonly Outfit outfit;
    readonly BodyMesher.Settings ms = new BodyMesher.Settings();
    readonly BodySkinner.Settings ss = new BodySkinner.Settings();
    BodyPlan plan;
    BodySDF sdf;
    JobHandle handle;
    Task task;
    readonly System.Diagnostics.Stopwatch wall = new System.Diagnostics.Stopwatch();
    double mainMs;

    // Covered skin and hidden garment parts: this far inside a covering garment's edge.
    const float CoverMargin = 0.015f;
    // Before decimation, skin this deep under a garment is dropped already (the decimator would spend
    // time on it; the open edge it leaves sits well under the cloth).
    const float PreCoverMargin = 0.03f;
    const float CoveredImportance = 0.05f;

    // Grids: one per piece (the body, each hand, each garment). mode = the kernel's cutMode for body
    // pieces; garment pieces have garment >= 0.
    class Piece
    {
        public byte mode; public int garment = -1; public float cell; public int target;
        public Vector3 min; public int nx, ny, nz, mx, my, mz;
        public NativeArray<float> coarse, fine; public NativeArray<byte> evaluated;
        public float[] grid;
        public List<Vector3> v; public List<int> t;
        public void Dispose() { if (coarse.IsCreated) coarse.Dispose(); if (fine.IsCreated) fine.Dispose(); if (evaluated.IsCreated) evaluated.Dispose(); }
    }
    readonly List<Piece> pieces = new List<Piece>();
    readonly List<GarmentField> fields = new List<GarmentField>();
    GarmentKernel dummy; bool hasDummy;
    DressedKernel dressed;
    float handCell;
    // Vertices through the stages: the body's pieces (skin) and the garments' (cloth), each concatenated.
    List<Vector3> verts, cverts; List<int> tris, ctris; float[] importance; byte[] vertPiece, vertGarment;
    NativeArray<Vector3> pos, nrm; NativeArray<Vector2> reg, lay; NativeArray<BoneWeight> wts; NativeArray<byte> pieceN, covered;
    NativeArray<Vector3> cpos, cnrm, conBody; NativeArray<Vector2> creg, clay; NativeArray<BoneWeight> cwts; NativeArray<byte> cgarm, cflags;
    NativeArray<float> gCells, gGaps;
    Parts bodyParts, headParts;
    Matrix4x4[] bindposes;
    int skinBefore;

    class Parts { public Vector3[] v, n; public Vector2[] r, l; public BoneWeight[] w; public List<int>[] subs; }

    public BodyBuild(CharacterSheet sheet, ISpeciesTemplate template = null, Outfit outfit = null)
    {
        this.sheet = sheet;
        this.template = template;
        this.outfit = outfit ?? Outfit.None();
    }

    bool sync;

    void ForPieces(Action<int> body)
    {
        if (sync) Parallel.For(0, pieces.Count, body);
        else for (int k = 0; k < pieces.Count; k++) body(k);
    }

    public BodyAsset RunSync()
    {
        sync = true;
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
        if (CurrentStage != stageAtStart)
        {
            double now = wall.Elapsed.TotalMilliseconds;
            StageWallMs[(int)stageAtStart] = (float)(now - stageStart);
            stageStart = now;
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

    Piece NewPiece(Bounds b, float cell, int target)
    {
        var p = new Piece { cell = cell, target = target };
        p.min = b.min - Vector3.one * cell;
        Vector3 size = b.size + Vector3.one * cell * 2f;
        p.nx = Mathf.CeilToInt(size.x / cell) + 1; p.ny = Mathf.CeilToInt(size.y / cell) + 1; p.nz = Mathf.CeilToInt(size.z / cell) + 1;
        int C = BodyMesher.CoarseStep;
        p.mx = (p.nx - 1) / C + 2; p.my = (p.ny - 1) / C + 2; p.mz = (p.nz - 1) / C + 2;
        // (Uninitialized: the sampling jobs write every cell.)
        p.coarse = new NativeArray<float>(p.mx * p.my * p.mz, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        p.fine = new NativeArray<float>(p.nx * p.ny * p.nz, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        p.evaluated = new NativeArray<byte>(p.nx * p.ny * p.nz, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        pieces.Add(p);
        return p;
    }

    void StartBuild()
    {
        wall.Start();
        SurfaceNets.Init();
        plan = BodyPlanner.Generate(sheet, null, template);
        sdf = new BodySDF(plan);
        var hr = BodyRules.Default.hands;
        handCell = hr.cell;
        bool hands = sdf.HandCount > 0;
        int C = BodyMesher.CoarseStep;
        void AddBody(Bounds b, float cell, byte mode, int target)
        {
            var p = NewPiece(b, cell, target);
            p.mode = mode;
            var k = sdf.Kernel; k.cutMode = mode;
            var hc = new BodyMesher.CoarseJob { kernel = k, min = p.min, step = cell * C, mx = p.mx, my = p.my, values = p.coarse }.Schedule(p.coarse.Length, 64);
            var hf = new BodyMesher.FineJob { kernel = k, coarse = p.coarse, min = p.min, cell = cell, band = cell * C * 1.9f, nx = p.nx, ny = p.ny, mx = p.mx, my = p.my, values = p.fine, evaluated = p.evaluated }
                .Schedule(p.fine.Length, 256, hc);
            handle = JobHandle.CombineDependencies(handle, hf);
        }
        handle = default;
        AddBody(sdf.bounds, ms.cell, (byte)(hands ? 1 : 0), ms.targetTriangles);
        for (int h = 0; h < sdf.HandCount; h++) AddBody(sdf.HandBounds[h], handCell, (byte)(2 + h), hr.triangles);

        // Garments: each field rests on the body and the garments before it (chained), then its surface
        // is sampled on its own grid.
        int ng = Mathf.Min(outfit.Count, DressedKernel.MaxGarments);
        if (ng > 0)
        {
            dummy = GarmentKernel.Dummy(sdf.Kernel, Allocator.Persistent); hasDummy = true;
            var kernels = new List<GarmentKernel>();
            JobHandle fieldDep = default;
            for (int g = 0; g < ng; g++)
            {
                var def = outfit.garments[g];
                var below = DressedKernel.Make(sdf.Kernel, kernels, dummy);
                var f = new GarmentField(sdf, def, below, fieldDep);
                fields.Add(f);
                kernels.Add(f.Kernel);
                fieldDep = f.Handle;
                var p = NewPiece(f.bounds, def.meshCell, def.triangles);
                p.garment = g;
                float cell = def.meshCell;
                var hc = new GarmentMeshJobs.CoarseJob { kernel = f.Kernel, min = p.min, step = cell * C, nx = p.mx, ny = p.my, values = p.coarse }.Schedule(p.coarse.Length, 64, f.Handle);
                var hf = new GarmentMeshJobs.FineJob { kernel = f.Kernel, coarse = p.coarse, min = p.min, cell = cell, band = cell * C * 1.9f, nx = p.nx, ny = p.ny, mx = p.mx, my = p.my, values = p.fine }
                    .Schedule(p.fine.Length, 256, hc);
                handle = JobHandle.CombineDependencies(handle, hf);
            }
            dressed = DressedKernel.Make(sdf.Kernel, kernels, dummy);
            gCells = new NativeArray<float>(ng, Allocator.Persistent);
            gGaps = new NativeArray<float>(ng, Allocator.Persistent);
            for (int g = 0; g < ng; g++) { gCells[g] = outfit.garments[g].meshCell; gGaps[g] = outfit.garments[g].gap; }
        }
        JobHandle.ScheduleBatchedJobs();
        CurrentStage = Stage.Sampling;
    }

    void AfterSampling()
    {
        foreach (var f in fields) f.Complete();   // (frees the fields' temporaries; their grids stay)
        int iters = ms.smoothIterations; float strength = ms.smoothStrength;
        // (The grids are copied out on the workers; their native arrays are freed after.)
        task = Task.Run(() =>
        {
            // (Async: one worker per body, pieces in turn - the pool runs bodies side by side, and more
            // threads per body only crowd the frame. RunSync blocks anyway: pieces in parallel.)
            ForPieces(k =>
            {
                var p = pieces[k];
                p.grid = p.fine.ToArray();
                var v = new List<Vector3>(); var t = new List<int>();
                SurfaceNets.Extract(p.grid, p.nx, p.ny, p.nz, p.min, p.cell, v, t);
                BodyMesher.Smooth(v, t, iters, strength);
                p.v = v; p.t = t; p.grid = null;
            });
            Concat();
        });
        CurrentStage = Stage.Extract;
    }

    // Body pieces' vertices and triangles in one list (vertPiece: each vertex's piece mode), garment
    // pieces' in another (vertGarment: each vertex's garment).
    void Concat()
    {
        verts = new List<Vector3>(); tris = new List<int>(); cverts = new List<Vector3>(); ctris = new List<int>();
        var vp = new List<byte>(); var vg = new List<byte>();
        foreach (var p in pieces)
        {
            bool cloth = p.garment >= 0;
            var V = cloth ? cverts : verts; var T = cloth ? ctris : tris;
            int o = V.Count;
            V.AddRange(p.v);
            foreach (int i in p.t) T.Add(i + o);
            for (int i = 0; i < p.v.Count; i++) { if (cloth) vg.Add((byte)p.garment); else vp.Add(p.mode); }
        }
        vertPiece = vp.ToArray(); vertGarment = vg.ToArray();
    }

    JobHandle ScheduleBodyProject(bool weights)
    {
        float margin = weights ? CoverMargin : PreCoverMargin;
        int n = verts.Count;
        pos = new NativeArray<Vector3>(verts.ToArray(), Allocator.Persistent);
        nrm = new NativeArray<Vector3>(n, Allocator.Persistent);
        reg = new NativeArray<Vector2>(n, Allocator.Persistent);
        lay = new NativeArray<Vector2>(n, Allocator.Persistent);
        covered = new NativeArray<byte>(n, Allocator.Persistent);
        pieceN = new NativeArray<byte>(vertPiece, Allocator.Persistent);
        float cell = ms.cell;
        var hp = new BodyMesher.ProjectJob { kernel = sdf.Kernel, positions = pos, piece = pieceN, cell = cell, handCell = handCell, steps = 3 }.Schedule(n, 64);
        var h = new BodyMesher.AttribJob { kernel = sdf.Kernel, positions = pos, piece = pieceN, h = ms.normalStep * cell, hHand = ms.normalStep * handCell, normals = nrm, region = reg, layers = lay }.Schedule(n, 64, hp);
        if (fields.Count > 0)
            h = JobHandle.CombineDependencies(h, new GarmentMeshJobs.BodyCoverJob { kernel = dressed, positions = pos, coverMargin = margin, covered = covered }.Schedule(n, 64, hp));
        if (weights)
        {
            wts = new NativeArray<BoneWeight>(n, Allocator.Persistent);
            h = JobHandle.CombineDependencies(h, new BodySkinner.WeightJob { kernel = sdf.Kernel, positions = pos, power = ss.falloffPower, softness = ss.softness, boost = ss.ownBoneBoost,
                                                                           reach = ss.jointReach, knuckle = BodyRules.Default.hands.knuckleBlend, weights = wts }.Schedule(n, 64, hp));
        }
        return h;
    }

    JobHandle ScheduleClothProject(bool weights)
    {
        int n = cverts.Count;
        cpos = new NativeArray<Vector3>(cverts.ToArray(), Allocator.Persistent);
        cnrm = new NativeArray<Vector3>(n, Allocator.Persistent);
        conBody = new NativeArray<Vector3>(n, Allocator.Persistent);
        creg = new NativeArray<Vector2>(n, Allocator.Persistent);
        clay = new NativeArray<Vector2>(n, Allocator.Persistent);
        cflags = new NativeArray<byte>(n, Allocator.Persistent);
        cgarm = new NativeArray<byte>(vertGarment, Allocator.Persistent);
        if (n == 0) return default;
        var hp = new GarmentMeshJobs.ProjectJob { kernel = dressed, positions = cpos, garment = cgarm, cells = gCells, steps = 3 }.Schedule(n, 64);
        var h = new GarmentMeshJobs.AttribJob { kernel = dressed, positions = cpos, garment = cgarm, cells = gCells, gaps = gGaps, coverMargin = CoverMargin,
                                                normals = cnrm, onBody = conBody, region = creg, layers = clay, flags = cflags }.Schedule(n, 64, hp);
        if (weights)
        {
            cwts = new NativeArray<BoneWeight>(n, Allocator.Persistent);
            h = new BodySkinner.WeightJob { kernel = sdf.Kernel, positions = conBody, power = ss.falloffPower, softness = ss.softness, boost = ss.ownBoneBoost,
                                            reach = ss.jointReach, knuckle = BodyRules.Default.hands.knuckleBlend, weights = cwts }.Schedule(n, 64, h);
        }
        return h;
    }

    void AfterExtract()
    {
        foreach (var p in pieces) p.Dispose();
        handle = JobHandle.CombineDependencies(ScheduleBodyProject(false), ScheduleClothProject(false));
        JobHandle.ScheduleBatchedJobs();
        CurrentStage = Stage.Project;
    }

    void AfterProject()
    {
        int n = pos.Length;
        importance = new float[n];
        for (int i = 0; i < n; i++)
        {
            verts[i] = pos[i];
            importance[i] = vertPiece[i] >= 2 ? 1f : BodyMesher.Importance(ms, (BoneKind)(int)reg[i].x);
            if (covered[i] != 0) importance[i] *= CoveredImportance;   // hidden under clothes: dropped later, spend little on it
        }
        for (int i = 0; i < cpos.Length; i++) cverts[i] = cpos[i];
        var cov = covered.ToArray();
        DisposeVertexArrays();
        task = Task.Run(() =>
        {
            // Back into pieces (projected), each decimated to its own budget.
            int ob = 0, oc = 0;
            var offsets = new int[pieces.Count];
            foreach (var p in pieces)
            {
                bool cloth = p.garment >= 0;
                int o = cloth ? oc : ob;
                offsets[pieces.IndexOf(p)] = o;
                for (int i = 0; i < p.v.Count; i++) p.v[i] = cloth ? cverts[o + i] : verts[o + i];
                if (cloth) oc += p.v.Count; else ob += p.v.Count;
            }
            var imp = importance;
            ForPieces(k =>
            {
                var p = pieces[k];
                var pi = new float[p.v.Count];
                if (p.garment >= 0) for (int i = 0; i < pi.Length; i++) pi[i] = 1f;
                else
                {
                    System.Array.Copy(imp, offsets[k], pi, 0, pi.Length);
                    if (fields.Count > 0) DropCovered(p, cov, offsets[k], ref pi);
                }
                if (p.t.Count / 3 <= p.target) return;
                // Body: the covered skin is cheap, so the budget goes to what stays visible.
                MeshDecimator.Run(p.v, p.t, pi, p.target, out var dv, out var dt, out _);
                p.v = dv; p.t = dt;
            });
            Concat();
        });
        CurrentStage = Stage.Decimate;
    }

    // Body piece: triangles whose three vertices are deep under a garment go, then unused vertices.
    static void DropCovered(Piece p, byte[] cov, int offset, ref float[] imp)
    {
        var keepT = new List<int>(p.t.Count);
        for (int t = 0; t < p.t.Count; t += 3)
        {
            int a = p.t[t], b = p.t[t + 1], c = p.t[t + 2];
            if (cov[offset + a] != 0 && cov[offset + b] != 0 && cov[offset + c] != 0) continue;
            keepT.Add(a); keepT.Add(b); keepT.Add(c);
        }
        var map = new int[p.v.Count];
        for (int i = 0; i < map.Length; i++) map[i] = -1;
        var nv = new List<Vector3>(); var ni = new List<float>();
        for (int i = 0; i < keepT.Count; i++)
        {
            int v = keepT[i];
            if (map[v] < 0) { map[v] = nv.Count; nv.Add(p.v[v]); ni.Add(imp[v]); }
            keepT[i] = map[v];
        }
        p.v = nv; p.t = keepT; imp = ni.ToArray();
    }

    void AfterDecimate()
    {
        handle = JobHandle.CombineDependencies(ScheduleBodyProject(true), ScheduleClothProject(true));
        JobHandle.ScheduleBatchedJobs();
        CurrentStage = Stage.Final;
    }

    void AfterFinal()
    {
        var V = pos.ToArray(); var N = nrm.ToArray(); var R = reg.ToArray(); var L = lay.ToArray(); var W = wts.ToArray(); var Cov = covered.ToArray();
        var CV = cpos.ToArray(); var CN = cnrm.ToArray(); var CR = creg.ToArray(); var CL = clay.ToArray(); var CF = cflags.ToArray();
        var CW = cwts.IsCreated ? cwts.ToArray() : new BoneWeight[0];
        var CG = vertGarment;
        DisposeVertexArrays();
        bindposes = BodySkinner.BindPoses(sdf);
        var T = tris.ToArray(); var CT = ctris.ToArray();
        int ng = fields.Count;
        task = Task.Run(() => SplitParts(V, N, R, L, W, Cov, T, CV, CN, CR, CL, CW, CF, CG, CT, ng));
        CurrentStage = Stage.Split;
    }

    // Head mesh: triangles in the head region. Body mesh: submesh 0 the visible skin (triangles with all
    // three vertices covered by a garment are dropped), then per garment its triangles minus the hidden
    // ones (any vertex inside the layers beneath it, or all three under an outer garment). Garment
    // weights are smoothed over the garment surface first (cloth spanning a crotch or an armpit mustn't
    // snap to one bone).
    void SplitParts(Vector3[] V, Vector3[] N, Vector2[] R, Vector2[] L, BoneWeight[] W, byte[] Cov, int[] T,
                    Vector3[] CV, Vector3[] CN, Vector2[] CR, Vector2[] CL, BoneWeight[] CW, byte[] CF, byte[] CG, int[] CT, int ng)
    {
        if (CV.Length > 0) CW = SmoothWeights(CW, CT, 3);
        // One vertex list: body vertices then cloth vertices.
        int nb = V.Length;
        Vector3 Pv(int i) => i < nb ? V[i] : CV[i - nb];
        BoneKind K(int v) => (BoneKind)(int)R[v].x;
        var body = new Parts { subs = new List<int>[1 + ng] }; var head = new Parts { subs = new List<int>[1] };
        for (int i = 0; i < body.subs.Length; i++) body.subs[i] = new List<int>();
        head.subs[0] = new List<int>();
        var bodyMap = new Dictionary<int, int>(); var headMap = new Dictionary<int, int>();
        var bv = new List<int>(); var hv = new List<int>();
        void Add(Parts p, Dictionary<int, int> map, List<int> list, List<int> dst, int a, int b, int c)
        {
            foreach (int v in new[] { a, b, c })
            {
                if (!map.TryGetValue(v, out int m)) { m = list.Count; map[v] = m; list.Add(v); }
                dst.Add(m);
            }
        }
        skinBefore = 0;
        for (int t = 0; t < T.Length; t += 3)
        {
            int a = T[t], b = T[t + 1], c = T[t + 2];
            int heads = (K(a) == BoneKind.Head ? 1 : 0) + (K(b) == BoneKind.Head ? 1 : 0) + (K(c) == BoneKind.Head ? 1 : 0);
            if (heads >= 2) { Add(head, headMap, hv, head.subs[0], a, b, c); continue; }
            skinBefore++;
            if (Cov[a] != 0 && Cov[b] != 0 && Cov[c] != 0) continue;   // under the clothes
            Add(body, bodyMap, bv, body.subs[BodyAsset.Skin], a, b, c);
        }
        for (int t = 0; t < CT.Length; t += 3)
        {
            int a = CT[t], b = CT[t + 1], c = CT[t + 2];
            if (((CF[a] | CF[b] | CF[c]) & 1) != 0) continue;            // inside the layers beneath
            if ((CF[a] & CF[b] & CF[c] & 2) != 0) continue;              // under an outer garment
            Add(body, bodyMap, bv, body.subs[1 + CG[a]], a + nb, b + nb, c + nb);
        }
        void Fill(Parts p, List<int> src)
        {
            int n = src.Count;
            p.v = new Vector3[n]; p.n = new Vector3[n]; p.r = new Vector2[n]; p.l = new Vector2[n]; p.w = new BoneWeight[n];
            for (int i = 0; i < n; i++)
            {
                int o = src[i];
                if (o < nb) { p.v[i] = V[o]; p.n[i] = N[o]; p.r[i] = R[o]; p.l[i] = L[o]; p.w[i] = W[o]; }
                else { o -= nb; p.v[i] = CV[o]; p.n[i] = CN[o]; p.r[i] = CR[o]; p.l[i] = CL[o]; p.w[i] = CW[o]; }
            }
        }
        Fill(body, bv); Fill(head, hv);
        bodyParts = body; headParts = head;
    }

    // A few passes of averaging each vertex's weights with its mesh neighbours' (top 4 kept).
    static BoneWeight[] SmoothWeights(BoneWeight[] W, int[] T, int iterations)
    {
        int n = W.Length;
        var adj = new List<int>[n];
        for (int i = 0; i < n; i++) adj[i] = new List<int>(6);
        void Link(int a, int b) { if (!adj[a].Contains(b)) adj[a].Add(b); if (!adj[b].Contains(a)) adj[b].Add(a); }
        for (int t = 0; t < T.Length; t += 3) { Link(T[t], T[t + 1]); Link(T[t + 1], T[t + 2]); Link(T[t + 2], T[t]); }
        var cur = W; var acc = new Dictionary<int, float>(16);
        for (int it = 0; it < iterations; it++)
        {
            var next = new BoneWeight[n];
            for (int i = 0; i < n; i++)
            {
                acc.Clear();
                void Put(BoneWeight w, float s)
                {
                    void One(int b, float x) { if (x <= 0f) return; acc.TryGetValue(b, out float o); acc[b] = o + x * s; }
                    One(w.boneIndex0, w.weight0); One(w.boneIndex1, w.weight1); One(w.boneIndex2, w.weight2); One(w.boneIndex3, w.weight3);
                }
                Put(cur[i], 1f);
                float share = adj[i].Count > 0 ? 1f / adj[i].Count : 0f;
                foreach (int j in adj[i]) Put(cur[j], share);
                // Top 4.
                int i0 = 0, i1 = 0, i2 = 0, i3 = 0; float w0 = 0f, w1 = 0f, w2 = 0f, w3 = 0f;
                foreach (var kv in acc)
                {
                    float w = kv.Value; int b = kv.Key;
                    if (w > w0) { i3 = i2; w3 = w2; i2 = i1; w2 = w1; i1 = i0; w1 = w0; i0 = b; w0 = w; }
                    else if (w > w1) { i3 = i2; w3 = w2; i2 = i1; w2 = w1; i1 = b; w1 = w; }
                    else if (w > w2) { i3 = i2; w3 = w2; i2 = b; w2 = w; }
                    else if (w > w3) { i3 = b; w3 = w; }
                }
                float sum = Mathf.Max(1e-6f, w0 + w1 + w2 + w3);
                next[i] = new BoneWeight { boneIndex0 = i0, weight0 = w0 / sum, boneIndex1 = i1, weight1 = w1 / sum, boneIndex2 = i2, weight2 = w2 / sum, boneIndex3 = i3, weight3 = w3 / sum };
            }
            cur = next;
        }
        return cur;
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
        var gt = new int[fields.Count];
        for (int g = 0; g < gt.Length; g++) gt[g] = bodyParts.subs[1 + g].Count / 3;
        Asset = new BodyAsset
        {
            sheet = sheet, plan = plan, outfit = outfit,
            body = Build("Body " + sheet, bodyParts), head = Build("Head " + sheet, headParts),
            eyeL = sdf.EyeL, eyeR = sdf.EyeR, eyeRadius = sdf.EyeRadius,
            height = plan.height, width = width, triangles = triCount,
            skinTriangles = bodyParts.subs[0].Count / 3, skinTrianglesBeforeRemoval = skinBefore, garmentTriangles = gt,
        };
        DisposeFields();
        sdf.Dispose(); sdf = null;
        wall.Stop();
        Asset.wallMs = (float)wall.Elapsed.TotalMilliseconds;
        Asset.mainThreadMs = (float)mainMs;
        CurrentStage = Stage.Done;
    }

    void DisposeFields()
    {
        foreach (var f in fields) f.Dispose();
        fields.Clear();
        if (hasDummy) { dummy.Dispose(); hasDummy = false; }
        if (gCells.IsCreated) gCells.Dispose();
        if (gGaps.IsCreated) gGaps.Dispose();
    }

    void DisposeVertexArrays()
    {
        void D<T>(ref NativeArray<T> a) where T : struct { if (a.IsCreated) a.Dispose(); }
        D(ref pos); D(ref nrm); D(ref reg); D(ref lay); D(ref pieceN); D(ref covered); D(ref wts);
        D(ref cpos); D(ref cnrm); D(ref conBody); D(ref creg); D(ref clay); D(ref cflags); D(ref cgarm); D(ref cwts);
    }

    void Cleanup()
    {
        try { handle.Complete(); } catch { }
        foreach (var p in pieces) p.Dispose();
        DisposeVertexArrays();
        try { DisposeFields(); } catch { }
        sdf?.Dispose();
    }
}
