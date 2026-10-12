using System;
using System.Collections.Generic;
using System.Linq;
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
    public readonly List<ClothChainSet> chains = new List<ClothChainSet>();   // skirts' spring chains (bones after the plan's)
    public const int Skin = 0;           // submesh; garment g is submesh 1 + g
    public Mesh wig;                     // rigid hair on the head joint (head joint space), or null
    public Mesh hairCards;               // Face Fix Part 9: hair cards (skinned: the head, long hair's chains), or null
    public WigStyle wigStyle;
    public Vector3 jawPivot;             // root space, rest
    public Vector3 mouthCentre;          // root space, rest (the mouth cavity sits behind it)
    public int jawBone = -1;             // renderer bone index of the jaw (after the chain bones)
    public string neckWeld = "";         // how the head and body were joined (Face Fix Part 7; not in presets)
    public EyeShape eye;                 // the eye opening (Face Fix Part 3: the blink cap and the eye's lid shadow follow it)
}

// Phase 8: generating a body without stalling the frame. A staged state machine, advanced by Step()
// once per frame by whoever owns it (BodyPool):
//   Start    (worker, then main ~1 ms)  plan + SDF on a worker; then schedule the sampling jobs: the body (hands cut off), each hand
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
    public string NeckReport { get; private set; } = "";
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
    // The face: marks for the bake, the jaw (bone, hinge, weights), the wig.
    BodySurfaceBake.FaceMarks faceMarks;
    BodySdfKernel.Ellipsoid skullCopy;
    Vector3 jawPivot, mouthCentre;
    float mouthHalf = 0.025f;
    int jawBone = -1;
    WigStyle wigStyle;
    bool hasWig; Vector3[] wigV, wigN; int[] wigT; Vector2[] wigUV; int wigBrowTris;
    HairStyle hairStyle; ClothChainSet hairChains;
    bool hasCards; Vector3[] cardV, cardN; Vector4[] cardT; Vector2[] cardUV, cardUV2; BoneWeight[] cardW; int[] cardTri;
    readonly List<ClothChainSet> chainSets = new List<ClothChainSet>();
    Matrix4x4[] bindposes;
    int skinBefore;

    class Parts { public Vector3[] v, n; public Vector2[] r, l; public BoneWeight[] w; public List<int>[] subs; public BodySurfaceBake.Result bake; }

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
                case Stage.Start:
                    // The plan and the SDF (snapping face parts, placing the ears: hundreds of SDF samples) on
                    // a worker; the jobs are scheduled here once they're ready.
                    if (task == null)
                    {
                        wall.Start();
                        SurfaceNets.Init();
                        var warm = BodyRules.Default;   // (loads on the main thread)
                        task = Task.Run(() => { plan = BodyPlanner.Generate(sheet, null, template); sdf = new BodySDF(plan); });
                        if (sync) task.Wait();
                        else break;
                    }
                    if (TaskDone()) StartBuild();
                    break;
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
        AddBody(sdf.bounds, ms.cell, (byte)(hands || sdf.HasHeadPiece ? 1 : 0), ms.targetTriangles);
        for (int h = 0; h < sdf.HandCount; h++) AddBody(sdf.HandBounds[h], handCell, (byte)(2 + h), hr.triangles);
        // The head on its own finer grid (the face parts are ~1 cm).
        if (sdf.HasHeadPiece) AddBody(sdf.HeadBounds, BodyRules.Default.mesh.headCell, (byte)sdf.HeadMode, BodyRules.Default.mesh.headTriangles);

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
                // Field grid: fine enough for its ease (a 5 cm coat closing doesn't need 1 cm cells).
                var f = new GarmentField(sdf, def, below, fieldDep, Mathf.Clamp(def.ease / 2.5f, 0.01f, 0.02f));
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
        // Skirts: their spring chains, measured on the finished fields (a few hundred samples).
        int nextBone = plan.bones.Count;
        for (int g = 0; g < fields.Count; g++)
        {
            var def = outfit.garments[g];
            if (!def.HasSkirt) continue;
            var cs = ClothChainSet.Build(fields[g].Kernel, def, g, fields[g].SkirtHemY, nextBone);
            chainSets.Add(cs);
            nextBone += cs.BoneCount;
        }
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
        int headMode = sdf.HasHeadPiece ? sdf.HeadMode : 255;
        var hp = new BodyMesher.ProjectJob { kernel = sdf.Kernel, positions = pos, piece = pieceN, cell = cell, handCell = handCell, headCell = BodyRules.Default.mesh.headCell, headMode = headMode, steps = 3 }.Schedule(n, 64);
        var h = new BodyMesher.AttribJob { kernel = sdf.Kernel, positions = pos, piece = pieceN, h = ms.normalStep * cell, hHand = ms.normalStep * handCell, hHead = ms.normalStep * BodyRules.Default.mesh.headCell,
                                           headMode = headMode, normals = nrm, region = reg, layers = lay }.Schedule(n, 64, hp);
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
            if (sdf.HasHeadPiece && vertPiece[i] == sdf.HeadMode)
            {
                // The head's own grid: keep the mouth (it opens) and the eyes detailed.
                float dm = (verts[i] - sdf.MouthCentre).magnitude, de = Mathf.Min((verts[i] - sdf.EyeL).magnitude, (verts[i] - sdf.EyeR).magnitude);
                float da = Mathf.Min((verts[i] - sdf.EarL).magnitude, (verts[i] - sdf.EarR).magnitude);
                importance[i] = 1f + 10f * Mathf.Exp(-dm * dm / (0.025f * 0.025f)) + 4f * Mathf.Exp(-de * de / (0.02f * 0.02f)) + 4f * Mathf.Exp(-da * da / (0.03f * 0.03f));
                // (Face Fix Part 8: the back of the head - mostly under hair - is decimated harder than the face.)
                float front = Vector3.Dot(verts[i] - sdf.Canon.o, sdf.Canon.z) / sdf.Canon.halfD;
                importance[i] *= Mathf.Lerp(0.35f, 1f, Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-0.6f, 0.1f, front)));
            }
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
        // Hair (Face Fix Part 9): the style first - long hair's strand-group chains join the chain bones.
        wigStyle = WigBuilder.Pick(sheet);
        hairStyle = HairStyle.For(wigStyle);
        if (hairStyle != null && hairStyle.chains && sdf.HasHeadPiece)
        {
            int nb = plan.bones.Count; foreach (var c0 in chainSets) nb += c0.BoneCount;
            hairChains = HairCards.Chains(sdf.Kernel, hairStyle, sdf.Canon, nb);
            chainSets.Add(hairChains);
        }
        bindposes = BodySkinner.BindPoses(sdf);
        // Chain bones after the plan's (bind = each joint, unrotated, in root space), then the jaw.
        var bp = new List<Matrix4x4>(bindposes);
        foreach (var cs in chainSets)
            for (int p = 0; p < cs.panels; p++)
                for (int b = 0; b < cs.bones; b++) bp.Add(Matrix4x4.Translate(cs.Joint(p, b)).inverse);
        jawBone = bp.Count;
        bp.Add(Matrix4x4.Translate(sdf.JawPivot).inverse);
        bindposes = bp.ToArray();
        int pelvisBone = plan.Index("Hips");
        var T = tris.ToArray(); var CT = ctris.ToArray();
        int ng = fields.Count;
        faceMarks = BodySurfaceBake.FaceMarks.From(sdf, wigStyle);
        var browPrims = new List<BodySdfKernel.FacePrim>();
        for (int j = 0; j < sdf.FacePrims.Count; j++) if (sdf.FacePartNames[j].StartsWith("Brow.")) browPrims.Add(sdf.FacePrims[j]);
        skullCopy = sdf.Kernel.skull; jawPivot = sdf.JawPivot; mouthCentre = sdf.MouthCentre; mouthHalf = sdf.Canon.mouthHalfWidth;
        var kernelCopy = sdf.Kernel;
        Vector3 headJoint = plan.JointPos(plan.Index("Head"));
        bool weld = sdf.HasHeadPiece;
        Vector3 cutO = sdf.HeadCutOrigin, cutAxis = sdf.HeadCutAxis; float cutR = sdf.HeadCutRadius;
        int headMode = sdf.HeadMode; float headCell = BodyRules.Default.mesh.headCell, bodyCell = ms.cell;
        var VP = vertPiece;
        var canon = sdf.Canon; int headBone = plan.Index("Head"); var hs = hairStyle; var hch = hairChains;
        task = Task.Run(() =>
        {
            if (weld) { T = WeldNeck(V, N, T, VP, headMode, kernelCopy, cutO, cutAxis, cutR, bodyCell, headCell, out var rep); NeckReport = rep; }
            SplitParts(V, N, R, L, W, Cov, T, CV, CN, CR, CL, CW, CF, CG, CT, ng, pelvisBone);
            // (Under long hair cards the shell is bob length: the cards hang beyond it.)
            var shellStyle = hs != null && wigStyle == WigStyle.Long ? WigStyle.Bob : wigStyle;
            hasWig = WigBuilder.Build(kernelCopy, shellStyle, sheet.seed, headJoint, browPrims, out wigV, out wigN, out wigT, out wigUV, out wigBrowTris, hs != null);
            if (hs != null) hasCards = HairCards.Build(kernelCopy, hs, sheet.seed, canon, headBone, hch, out cardV, out cardN, out cardT, out cardUV, out cardUV2, out cardW, out cardTri);
        });
        CurrentStage = Stage.Split;
    }

    // Head mesh: triangles in the head region. Body mesh: submesh 0 the visible skin (triangles with all
    // three vertices covered by a garment are dropped), then per garment its triangles minus the hidden
    // ones (any vertex inside the layers beneath it, or all three under an outer garment). Garment
    // weights are smoothed over the garment surface first (cloth spanning a crotch or an armpit mustn't
    // snap to one bone).
    void SplitParts(Vector3[] V, Vector3[] N, Vector2[] R, Vector2[] L, BoneWeight[] W, byte[] Cov, int[] T,
                    Vector3[] CV, Vector3[] CN, Vector2[] CR, Vector2[] CL, BoneWeight[] CW, byte[] CF, byte[] CG, int[] CT, int ng, int pelvisBone)
    {
        if (CV.Length > 0) CW = SmoothWeights(CW, CT, 3);
        // Skirts: their vertices below the ring go to the spring chains.
        foreach (var cs in chainSets)
            for (int i = 0; i < CV.Length; i++)
                if (CG[i] == cs.garment && cs.Weight(CV[i], CW[i], pelvisBone) is BoneWeight w) CW[i] = w;
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
        // Graphics Phase 3: rest pose, curvature, contact shadow, stitching and seam data per vertex.
        var kinds = BodySurfaceBake.Kinds(plan);
        int[][] Arr(List<int>[] l) { var a = new int[l.Length][]; for (int i = 0; i < l.Length; i++) a[i] = l[i].ToArray(); return a; }
        body.bake = BodySurfaceBake.Bake(body.v, body.n, body.w, Arr(body.subs), kinds);
        head.bake = BodySurfaceBake.Bake(head.v, head.n, head.w, Arr(head.subs), kinds, body.bake.hems, faceMarks);
        // The lower face (below the mouth line, in front of the hinge, above the neck) follows the jaw.
        // The mouth: the band where the jaw weight changes is the mouth line (dark inside: a thin line
        // closed, the opening when the jaw drops); the strips just above and below it are the lips.
        // Inside the mouth's width the split is sharp (the upper lip stays on the head, the lower one goes
        // with the jaw); the triangles bridging the two are the inside of the mouth: dark (a line when
        // closed, they stretch into the open mouth when the jaw drops).
        if (head.bake.uv4 == null) head.bake.uv4 = new Vector3[head.v.Length];
        var wjs = new float[head.v.Length]; var inM = new float[head.v.Length];
        for (int i = 0; i < head.v.Length; i++)
        {
            Vector3 d = head.v[i] - mouthCentre;
            float dx = Mathf.Abs(Vector3.Dot(d, skullCopy.x)), dy = Vector3.Dot(d, skullCopy.y), dz = Vector3.Dot(d, skullCopy.z);
            // The mouth's width from the canon (the pupils' distance); its corners wrap back round the muzzle.
            float inMouth = (1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(mouthHalf - 0.004f, mouthHalf + 0.003f, dx))) * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-0.045f, -0.025f, dz));
            float wj = JawWeight(head.v[i], skullCopy, mouthCentre, jawPivot);
            // Inside the mouth the split is sharp; the lower lip follows the jaw fully at the middle, less toward
            // the corners (they barely move): a lens-shaped opening, not a box.
            float across = Mathf.Clamp01(dx / Mathf.Max(0.005f, mouthHalf));
            if (inMouth > 0.5f) wj = dy < -0.0015f ? Mathf.Lerp(0.3f, 1f, 1f - across * across) : 0f;
            wjs[i] = wj; inM[i] = inMouth;
            if (wj > 0.001f) head.w[i] = BlendIn(head.w[i], jawBone, wj);
            float G(float x) => Mathf.Exp(-x * x);
            float lip = (G((dy - 0.004f) / 0.004f) + G((dy + 0.0045f) / 0.005f)) * inMouth;
            var mk = head.bake.uv4[i];
            head.bake.uv4[i] = new Vector3(mk.x, Mathf.Max(mk.y, Mathf.Clamp01(lip)), mk.z);
        }
        // The bridging triangles get their own copies of their vertices (dark: the inside of the mouth), so
        // the lips next to them never darken; closed, every bridging vertex sits on the mouth line (the
        // band has no area: nothing dark shows), and the jaw opens it.
        var ht = head.subs[0];
        var dup = new Dictionary<int, int>();
        var V2 = new List<Vector3>(head.v); var N2 = new List<Vector3>(head.n); var R2 = new List<Vector2>(head.r); var L2 = new List<Vector2>(head.l);
        var W2 = new List<BoneWeight>(head.w); var C2 = new List<Color>(head.bake.colors); var U0 = new List<Vector2>(head.bake.uv0);
        var U3 = new List<Vector3>(head.bake.uv3); var U4 = new List<Vector3>(head.bake.uv4);
        var Z = new List<Vector2>(head.bake.zones ?? new Vector2[head.v.Length]);
        var bridge = new HashSet<int>();
        var backs = new List<int>();
        for (int k2 = 0; k2 + 2 < ht.Count; k2 += 3)
        {
            int a = ht[k2], b = ht[k2 + 1], c = ht[k2 + 2];
            bool mouthTri = inM[a] > 0.5f && inM[b] > 0.5f && inM[c] > 0.5f;
            float lo = Mathf.Min(wjs[a], Mathf.Min(wjs[b], wjs[c])), hi = Mathf.Max(wjs[a], Mathf.Max(wjs[b], wjs[c]));
            if (!mouthTri || hi - lo < 0.5f) continue;
            for (int q = 0; q < 3; q++)
            {
                int v = ht[k2 + q];
                bridge.Add(v);
                if (!dup.TryGetValue(v, out int nvx))
                {
                    nvx = V2.Count; dup[v] = nvx;
                    V2.Add(V2[v]); N2.Add(N2[v]); R2.Add(R2[v]); L2.Add(L2[v]); W2.Add(W2[v]); C2.Add(C2[v]); U0.Add(U0[v]); U3.Add(U3[v]);
                    U4.Add(new Vector3(U4[v].x, 0f, 1f)); Z.Add(Vector2.zero);
                }
                ht[k2 + q] = nvx;
            }
            backs.Add(ht[k2]); backs.Add(ht[k2 + 2]); backs.Add(ht[k2 + 1]);
        }
        // The inside of the mouth shows from both sides: opened, a bridging triangle can face away (the lips'
        // groove folds them), and a culled one would show the head's inside through the mouth.
        ht.AddRange(backs);
        foreach (int v in new List<int>(bridge)) U4[v] = new Vector3(U4[v].x, U4[v].y, 0f);   // the originals: lip, not inside
        foreach (int v in dup.Values) bridge.Add(v);
        for (int i = 0; i < V2.Count; i++)
        {
            if (!bridge.Contains(i)) continue;
            float dy = Vector3.Dot(V2[i] - mouthCentre, skullCopy.y);
            V2[i] -= skullCopy.y * dy;
            U3[i] = V2[i];
        }
        head.v = V2.ToArray(); head.n = N2.ToArray(); head.r = R2.ToArray(); head.l = L2.ToArray(); head.w = W2.ToArray();
        var hb = head.bake; hb.colors = C2.ToArray(); hb.uv0 = U0.ToArray(); hb.uv3 = U3.ToArray(); hb.uv4 = U4.ToArray(); hb.zones = Z.ToArray(); head.bake = hb;
        bodyParts = body; headParts = head;
    }

    // Face Fix Part 7: one surface at the neck. The body and the head piece are cut at the same plane
    // (no overlap): each closes with a flat cap there. The caps go; both open rings are moved onto the
    // skin's cross-section in that plane (the uncut body's surface, radially from the neck's axis), get
    // the uncut body's normals, and are stitched together with a strip of triangles - shared vertices,
    // one mesh surface, no gap or step. Returns the new triangle list.
    static int[] WeldNeck(Vector3[] V, Vector3[] N, int[] T, byte[] piece, int headMode, BodySdfKernel k, Vector3 o, Vector3 axis, float radius,
                          float bodyCell, float headCell, out string report)
    {
        report = "no neck weld";
        k.cutMode = 0;
        float S(Vector3 p) => Vector3.Dot(p - o, axis);
        bool Near(int i, float cell) => Mathf.Abs(S(V[i])) < 0.0015f && (V[i] - o - axis * S(V[i])).magnitude < radius;
        // The caps: triangles lying in the plane, facing out of their piece through it.
        var keep = new List<int>(T.Length);
        int capB = 0, capH = 0;
        for (int t = 0; t + 2 < T.Length; t += 3)
        {
            int a = T[t], b = T[t + 1], c = T[t + 2];
            bool head = piece[a] == headMode, body = piece[a] == 1;
            if ((head || body) && piece[b] == piece[a] && piece[c] == piece[a])
            {
                float cell = head ? headCell : bodyCell;
                // (All three on the plane: the projection puts the cap's vertices on it to well under a mm.)
                if (Near(a, cell) && Near(b, cell) && Near(c, cell)) { if (head) capH++; else capB++; continue; }
            }
            keep.Add(a); keep.Add(b); keep.Add(c);
        }
        if (capH == 0 || capB == 0) { report = $"no neck caps (head {capH}, body {capB})"; return T; }
        // The open rings: edges used once, near the plane, per piece; walked into ordered loops (the longest
        // per piece: the neck), so any cross-section shape (a double chin) zips in order.
        var use = new Dictionary<long, int>();
        for (int t = 0; t + 2 < keep.Count; t += 3)
            for (int e = 0; e < 3; e++)
            {
                int a = keep[t + e], b = keep[t + (e + 1) % 3];
                long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                use.TryGetValue(key, out int n); use[key] = n + 1;
            }
        var adjH = new Dictionary<int, List<int>>(); var adjB = new Dictionary<int, List<int>>();
        foreach (var kv in use)
        {
            if (kv.Value != 1) continue;
            int a = (int)(kv.Key >> 32), b = (int)(kv.Key & 0xffffffff);
            if (piece[a] != piece[b]) continue;
            float cell = piece[a] == headMode ? headCell : bodyCell;
            if (Mathf.Abs(S(V[a])) > cell || Mathf.Abs(S(V[b])) > cell) continue;
            var adj = piece[a] == headMode ? adjH : piece[a] == 1 ? adjB : null;
            if (adj == null) continue;
            if (!adj.TryGetValue(a, out var la)) adj[a] = la = new List<int>(2);
            if (!adj.TryGetValue(b, out var lb)) adj[b] = lb = new List<int>(2);
            la.Add(b); lb.Add(a);
        }
        int loops = 0;
        var extra = new List<List<int>>();   // the other (small) loops: cap leftovers, filled below
        List<int> Loop(Dictionary<int, List<int>> adj)
        {
            var seen = new HashSet<int>(); List<int> best = null; float bestLen = 0f; loops = 0;
            foreach (int s0 in adj.Keys)
            {
                if (seen.Contains(s0)) continue;
                var loop = new List<int> { s0 }; seen.Add(s0);
                int prev = -1, cur = s0; float len = 0f;
                while (true)
                {
                    int next = -1;
                    foreach (int nb in adj[cur]) if (nb != prev && !seen.Contains(nb)) { next = nb; break; }
                    if (next < 0) break;
                    len += (V[next] - V[cur]).magnitude;
                    seen.Add(next); loop.Add(next); prev = cur; cur = next;
                }
                if (loop.Count >= 3) { loops++; extra.Add(loop); }
                if (loop.Count >= 3 && len > bestLen) { bestLen = len; best = loop; }
            }
            if (best != null) extra.Remove(best);
            return best;
        }
        var h = Loop(adjH); int loopsH = loops; var bl = Loop(adjB); int loopsB = loops;
        if (h == null || bl == null) { report = $"neck rings not found (head {adjH.Count}, body {adjB.Count} boundary vertices)"; return T; }
        // Onto the plane, then onto the uncut skin within it (gradient steps); normals from the uncut body.
        Vector3 refX = Vector3.Cross(axis, Vector3.forward).sqrMagnitude > 1e-6f ? Vector3.Cross(axis, Vector3.forward).normalized : Vector3.right;
        Vector3 refZ = Vector3.Cross(refX, axis).normalized;
        foreach (var ring in new[] { h, bl })
            foreach (int i in ring)
            {
                Vector3 p = V[i] - axis * S(V[i]);
                for (int it = 0; it < 12; it++)
                {
                    float d = k.Skin(p);
                    if (Mathf.Abs(d) < 2e-5f) break;
                    Vector3 g = k.Normal(p, 0.001f); g -= axis * Vector3.Dot(g, axis);
                    if (g.sqrMagnitude < 1e-8f) break;
                    p -= g.normalized * Mathf.Clamp(d, -0.01f, 0.01f);
                }
                V[i] = p;
                N[i] = k.Normal(p, 0.002f);
            }
        // Same direction round (signed area in the plane), the body loop starting nearest the head's start.
        float Area(List<int> l)
        {
            float A = 0f;
            for (int q = 0; q < l.Count; q++)
            {
                Vector3 p0 = V[l[q]] - o, p1 = V[l[(q + 1) % l.Count]] - o;
                A += Vector3.Dot(p0, refX) * Vector3.Dot(p1, refZ) - Vector3.Dot(p1, refX) * Vector3.Dot(p0, refZ);
            }
            return A;
        }
        if (Area(h) < 0f) h.Reverse();
        if (Area(bl) < 0f) bl.Reverse();
        int start = 0; float bestD = float.MaxValue;
        for (int q = 0; q < bl.Count; q++) { float d = (V[bl[q]] - V[h[0]]).sqrMagnitude; if (d < bestD) { bestD = d; start = q; } }
        bl = bl.GetRange(start, bl.Count - start).Concat(bl.GetRange(0, start)).ToList();
        // Arc-length parameters round each loop.
        float[] Param(List<int> l)
        {
            var t = new float[l.Count + 1]; float total = 0f;
            for (int q = 0; q < l.Count; q++) { t[q] = total; total += (V[l[(q + 1) % l.Count]] - V[l[q]]).magnitude; }
            for (int q = 0; q < l.Count; q++) t[q] /= Mathf.Max(total, 1e-6f);
            t[l.Count] = 1f;
            return t;
        }
        var th = Param(h); var tb = Param(bl);
        // Zip: walk both loops round, always advancing the one whose next vertex comes first.
        var seam = new List<int>();
        int i0 = 0, j0 = 0, nH = h.Count, nB = bl.Count;
        while (i0 < nH || j0 < nB)
        {
            bool advH = j0 >= nB || (i0 < nH && th[i0 + 1] <= tb[j0 + 1]);
            int a = h[i0 % nH], b = bl[j0 % nB];
            int c = advH ? h[(i0 + 1) % nH] : bl[(j0 + 1) % nB];
            // Outward: along the skin's normals.
            Vector3 fn = Vector3.Cross(V[b] - V[a], V[c] - V[a]);
            if (Vector3.Dot(fn, N[a] + N[b] + N[c]) < 0f) { seam.Add(a); seam.Add(c); seam.Add(b); } else { seam.Add(a); seam.Add(b); seam.Add(c); }
            if (advH) i0++; else j0++;
        }
        keep.AddRange(seam);
        // Leftover little loops (a cap fragment the plane test missed): fan-filled, facing out with the skin.
        foreach (var lp in extra)
            for (int q = 1; q + 1 < lp.Count; q++)
            {
                int a = lp[0], b = lp[q], c = lp[q + 1];
                Vector3 fn = Vector3.Cross(V[b] - V[a], V[c] - V[a]);
                if (Vector3.Dot(fn, N[a] + N[b] + N[c]) < 0f) { keep.Add(a); keep.Add(c); keep.Add(b); } else { keep.Add(a); keep.Add(b); keep.Add(c); }
            }
        // Watertight check: edges used once within 2 cm of the plane (should be none).
        var use2 = new Dictionary<long, int>();
        for (int t = 0; t + 2 < keep.Count; t += 3)
            for (int e = 0; e < 3; e++)
            {
                int a = keep[t + e], b = keep[t + (e + 1) % 3];
                long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                use2.TryGetValue(key, out int n); use2[key] = n + 1;
            }
        int open = 0, over = 0;
        foreach (var kv in use2)
        {
            int a = (int)(kv.Key >> 32), b = (int)(kv.Key & 0xffffffff);
            if (Mathf.Abs(S(V[a])) > 0.02f || Mathf.Abs(S(V[b])) > 0.02f || (piece[a] != 1 && piece[a] != headMode)) continue;
            if (kv.Value == 1) open++; else if (kv.Value > 2) over++;
        }
        report = $"neck welded: caps {capH} head / {capB} body triangles dropped, rings {nH} head / {nB} body vertices, {seam.Count / 3} seam triangles, loops {loopsH} / {loopsB}, {open} open / {over} non-manifold edges left near it";
        return keep.ToArray();
    }

    // How much a head vertex follows the jaw (0..1).
    public static float JawWeight(Vector3 v, BodySdfKernel.Ellipsoid sk, Vector3 mouth, Vector3 pivot)
    {
        float dy = Vector3.Dot(v - mouth, sk.y), dx = Mathf.Abs(Vector3.Dot(v - mouth, sk.x)), dz = Vector3.Dot(v - pivot, sk.z);
        float below = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-0.006f, 0.002f, dy));     // under the mouth line
        float front = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-0.01f, 0.025f, dz));          // in front of the hinge
        float notNeck = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-0.05f, -0.075f, dy));  // not the neck under the chin
        float inner = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.05f, 0.075f, dx));      // not the far sides
        return below * front * notNeck * inner;
    }

    // Moves weight w of a vertex onto bone b (the rest scaled down; top 4 kept).
    static BoneWeight BlendIn(BoneWeight o, int b, float w)
    {
        var list = new List<KeyValuePair<int, float>>
        {
            new KeyValuePair<int, float>(o.boneIndex0, o.weight0 * (1f - w)), new KeyValuePair<int, float>(o.boneIndex1, o.weight1 * (1f - w)),
            new KeyValuePair<int, float>(o.boneIndex2, o.weight2 * (1f - w)), new KeyValuePair<int, float>(b, w),
        };
        list.Sort((x, y) => y.Value.CompareTo(x.Value));
        float sum = 0f; foreach (var kv in list) sum += kv.Value;
        sum = Mathf.Max(sum, 1e-6f);
        return new BoneWeight
        {
            boneIndex0 = list[0].Key, weight0 = list[0].Value / sum, boneIndex1 = list[1].Key, weight1 = list[1].Value / sum,
            boneIndex2 = list[2].Key, weight2 = list[2].Value / sum, boneIndex3 = list[3].Key, weight3 = list[3].Value / sum,
        };
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
            BodySurfaceBake.Apply(m, p.bake);
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
            eyeL = sdf.EyeL, eyeR = sdf.EyeR, eyeRadius = sdf.EyeRadius, eye = sdf.Eye,
            height = plan.height, width = width, triangles = triCount,
            skinTriangles = bodyParts.subs[0].Count / 3, skinTrianglesBeforeRemoval = skinBefore, garmentTriangles = gt,
        };
        Asset.chains.AddRange(chainSets);
        Asset.neckWeld = NeckReport;
        Asset.jawBone = jawBone; Asset.jawPivot = jawPivot; Asset.mouthCentre = mouthCentre; Asset.wigStyle = wigStyle;
        if (hasWig)
        {
            Asset.wig = WigMesh("Wig " + sheet, wigV, wigN, wigT, wigUV, wigBrowTris);
        }
        if (hasCards)
        {
            Asset.hairCards = CardMesh("Hair " + sheet, cardV, cardN, cardT, cardUV, cardUV2, cardW, cardTri, bindposes);
        }
        DisposeFields();
        sdf.Dispose(); sdf = null;
        wall.Stop();
        Asset.wallMs = (float)wall.Elapsed.TotalMilliseconds;
        Asset.mainThreadMs = (float)mainMs;
        CurrentStage = Stage.Done;
    }

    // Hair cards (Face Fix Part 9): skinned like the body (its bind poses).
    public static Mesh CardMesh(string name, Vector3[] v, Vector3[] n, Vector4[] t, Vector2[] uv, Vector2[] uv2, BoneWeight[] w, int[] tri, Matrix4x4[] bind)
    {
        var m = new Mesh { name = name, indexFormat = v.Length > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
        m.vertices = v; m.normals = n; m.tangents = t; m.uv = uv; m.SetUVs(1, uv2);
        m.triangles = tri; m.boneWeights = w; m.bindposes = bind;
        m.RecalculateBounds();
        return m;
    }

    // The wig's mesh: submesh 0 the hair, submesh 1 the brows (its last browTris triangles, FPS/Brow).
    public static Mesh WigMesh(string name, Vector3[] v, Vector3[] n, int[] t, Vector2[] uv, int browTris)
    {
        var wm = new Mesh { name = name, indexFormat = v.Length > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
        wm.vertices = v; wm.normals = n;
        if (uv != null && uv.Length == v.Length) wm.uv = uv;
        int split = Mathf.Clamp(t.Length - browTris * 3, 0, t.Length);
        wm.subMeshCount = 2;
        var hair = new int[split]; System.Array.Copy(t, hair, split);
        var brows = new int[t.Length - split]; System.Array.Copy(t, split, brows, 0, brows.Length);
        wm.SetTriangles(hair, 0); wm.SetTriangles(brows, 1);
        wm.RecalculateBounds();
        return wm;
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
