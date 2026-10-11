using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

// Clothing Phase 1: a garment's distance field, from the body's.
//   1. Sample the body (skin distance + nearest bone) on a grid (cell ~1 cm; narrow band, coarse elsewhere).
//   2. Ease: morphological closing, closed = Erode(Dilate(skin, r), r) with r = ease, as separable min /
//      max filters over the grid (a cube element): hollows narrower than about r are bridged (navel,
//      spine groove, pec gap, armpit, under the breasts), convex areas stay. Pants close each leg on its
//      own (the other leg masked out), so the tubes stay apart and the crotch bridges per leg.
//   3. Drape: drape(p) = min over k of closed(p + up k step) + k step slope, k = 0..N over hangLength,
//      counting only the drape region above (torso, pelvis; coats: thighs too): fabric hangs from the
//      chest, breasts, belly and buttocks. Blended in by a smoothed weight on that region, so arms and
//      legs get ease only.
//   4. Shell: outer = drape - thickness - gap, inner = skin - gap; garment = max(outer, -inner), kept
//      where it covers (points nearest a covered bone) and cut by its planes (perpendicular to a bone at
//      a fraction of it, or horizontal at a multiple of the hip height), with a rounded hem.
// GarmentKernel evaluates it at any point (Burst); dispose the field when done.
public class GarmentField : IDisposable
{
    public readonly GarmentDef def;
    public GarmentKernel Kernel;
    public Bounds bounds;
    public float buildMs;

    const int C = 4;          // coarse step
    const byte Far = 255;     // nearest bone unknown (outside the sampled band)

    public struct Cut
    {
        public int kind;       // 0 bone, 1 height
        public int bone;       // BoneKind
        public float at;
        public float keep;     // +1 keep the start / above
        public int applyMask;  // height cuts: bit per BoneKind
    }

    // Done when the field is ready (scheduled builds); temporaries are freed by Complete().
    public JobHandle Handle;
    readonly List<IDisposable> temps = new List<IDisposable>();
    GarmentKernel dummy; bool ownsDummy;

    // Blocking build on the body alone (tests, lineup previews).
    public GarmentField(BodySDF sdf, GarmentDef g, float cell = 0.01f)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        dummy = GarmentKernel.Dummy(sdf.Kernel, Allocator.TempJob); ownsDummy = true;
        def = g;
        Schedule(sdf, g, DressedKernel.Make(sdf.Kernel, null, dummy), default, cell);
        Complete();
        buildMs = (float)sw.Elapsed.TotalMilliseconds;
    }

    // Scheduled build (BodyBuild): `below` holds the garments under this one (body + earlier layers are
    // its base: it rests on them); `dep` = when they're ready. Nothing runs on the calling thread.
    public GarmentField(BodySDF sdf, GarmentDef g, DressedKernel below, JobHandle dep, float cell = 0.01f)
    {
        def = g;
        Schedule(sdf, g, below, dep, cell);
    }

    public void Complete()
    {
        Handle.Complete();
        foreach (var t in temps) t.Dispose();
        temps.Clear();
        if (ownsDummy) { dummy.Dispose(); ownsDummy = false; }
    }

    // (Uninitialized: every job writes all of its output before anything reads it.)
    NativeArray<T> Temp<T>(int n) where T : struct { var a = new NativeArray<T>(n, Allocator.Persistent, NativeArrayOptions.UninitializedMemory); temps.Add(a); return a; }

    // Grid bounds: the covered bones, padded by girth, ease, the shell and the drape.
    static Bounds CoverBounds(BodySDF sdf, GarmentDef g)
    {
        var plan = sdf.Plan;
        int cover = 0; foreach (var k in g.covers) cover |= 1 << (int)k;
        var b = new Bounds(); bool any = false;
        float pad = g.ease * 2f + g.thickness + g.gap + g.hangLength * 0.5f + 0.06f;
        for (int i = 0; i < plan.bones.Count; i++)
        {
            var bone = plan.bones[i];
            if (bone.kind == BoneKind.Finger || (cover & (1 << (int)bone.kind)) == 0) continue;
            float r = Mathf.Max(bone.Outer.x, Mathf.Max(bone.Outer.y, bone.Outer.z)) * 1.3f + pad;
            var e = new Bounds(sdf.start[i], Vector3.one * r * 2f); e.Encapsulate(new Bounds(sdf.end[i], Vector3.one * r * 2f));
            if (!any) { b = e; any = true; } else b.Encapsulate(e);
        }
        // Breasts and the like hang off the bones: the body bounds clamp it.
        if (!any) b = sdf.bounds;
        var body = sdf.bounds; body.Expand(pad * 2f);
        b.SetMinMax(Vector3.Max(b.min, body.min), Vector3.Min(b.max, body.max));
        return b;
    }

    void Schedule(BodySDF sdf, GarmentDef g, DressedKernel below, JobHandle dep, float cell)
    {
        var body = sdf.Kernel;
        bounds = CoverBounds(sdf, g);
        Vector3 min = bounds.min;
        int nx = Mathf.CeilToInt(bounds.size.x / cell) + 1, ny = Mathf.CeilToInt(bounds.size.y / cell) + 1, nz = Mathf.CeilToInt(bounds.size.z / cell) + 1;
        int n = nx * ny * nz;
        int mx = (nx - 1) / C + 2, my = (ny - 1) / C + 2, mz = (nz - 1) / C + 2;

        // 1. base distance (body + the layers below) + nearest bone
        var coarse = Temp<float>(mx * my * mz);
        var values = Temp<float>(n);
        // Nearest bone per cell: kept with the kernel (garment surfaces look it up instead of evaluating the body).
        var nearest = new NativeArray<byte>(n, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        var h = new BaseCoarseJob { kernel = below, min = min, step = cell * C, nx = mx, ny = my, values = coarse }.Schedule(coarse.Length, 64, dep);
        float band = Mathf.Max(cell * C * 1.9f, g.ease * 2f + g.thickness + 0.03f);
        h = new SampleJob { kernel = below, coarse = coarse, min = min, cell = cell, band = band, nx = nx, ny = ny, mx = mx, my = my, values = values, nearest = nearest }.Schedule(n, 256, h);

        // 2. closing (per leg for split garments)
        int nb = body.bones.Length;
        var closed = Temp<float>(n);
        var tmpA = Temp<float>(n);
        var tmpB = Temp<float>(n);
        int r = Mathf.Max(1, Mathf.RoundToInt(g.ease / cell));
        // Split garments: each leg on its own, then the whole body again above the crotch (so the legs'
        // own closings don't leave a notch where they meet the pelvis). Sleeves: each arm closed on its own
        // and the torso without the arms, so the armpit isn't webbed shut (it would stretch into a bat
        // wing when the arms go up).
        bool splitArms = System.Array.IndexOf(g.covers, BoneKind.UpperArm) >= 0;
        var groups = new List<(int legSide, int arm)>();   // legSide: 0 all legs, +-1 that leg only; arm: 0 no arms, +-1 that arm only, 2 all
        foreach (int side in g.splitLegs ? new[] { -1, 1, 0 } : new[] { 0 }) groups.Add((side, splitArms ? 0 : 2));
        if (splitArms) { groups.Add((9, -1)); groups.Add((9, 1)); }
        int pelvis = sdf.Plan.Index("Hips");
        float crotchY = pelvis >= 0 ? sdf.end[pelvis].y : 0f;
        bool first = true;
        foreach (var (side, arm) in groups)
        {
            var exclude = Temp<byte>(nb);
            for (int i = 0; i < nb; i++)
            {
                var b = body.bones[i];
                bool leg = b.kind == (int)BoneKind.Thigh || b.kind == (int)BoneKind.Shin || b.kind == (int)BoneKind.Foot;
                bool isArm = b.kind == (int)BoneKind.UpperArm || b.kind == (int)BoneKind.Forearm || b.kind == (int)BoneKind.Hand;
                bool ex;
                if (side == 9) ex = !(isArm && (b.side == arm || (arm < 0 && b.side == 0)));                  // one arm alone
                else ex = (side != 0 && leg && b.side == -side) || (isArm && arm == 0);
                exclude[i] = (byte)(ex ? 1 : 0);
            }
            h = new MaskJob { values = values, nearest = nearest, exclude = exclude, output = tmpA }.Schedule(n, 1024, h);
            h = Filter(tmpA, tmpB, nx, ny, nz, r, 0, h);    // dilate: min filter (result in tmpA)
            h = Filter(tmpA, tmpB, nx, ny, nz, r, 1, h);    // erode: max filter
            bool aboveOnly = g.splitLegs && side == 0;
            h = new CombineJob { a = tmpA, output = closed, first = first ? 1 : 0, aboveOnly = aboveOnly ? 1 : 0, nx = nx, ny = ny,
                                 minY = min.y, cell = cell, fromY = crotchY - 0.01f, toY = crotchY + 0.05f }.Schedule(n, 1024, h);
            first = false;
        }

        // 3. drape
        int drapeMask = 0; foreach (var k in g.drapeOn) drapeMask |= 1 << (int)k;
        var kinds = Temp<int>(nb);
        for (int i = 0; i < nb; i++) kinds[i] = body.bones[i].kind;
        h = new WeightJob { nearest = nearest, kinds = kinds, mask = drapeMask, output = tmpA }.Schedule(n, 1024, h);
        h = Filter(tmpA, tmpB, nx, ny, nz, Mathf.Max(1, Mathf.RoundToInt(0.03f / cell)), 2, h);   // smooth the region's edge (~3 cm)
        var drape = new NativeArray<float>(n, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        int steps = 8;
        int stepCells = Mathf.Max(1, Mathf.RoundToInt(g.hangLength / steps / cell));
        h = new DrapeJob { closed = closed, nearest = nearest, kinds = kinds, mask = drapeMask, weight = tmpA, nx = nx, ny = ny, steps = g.hangLength > 0f ? steps : 0,
                           stepCells = stepCells, stepRise = stepCells * cell * g.slope, output = drape }.Schedule(n, 256, h);
        Handle = h;
        JobHandle.ScheduleBatchedJobs();

        // 4. the kernel (its grid is ready once Handle completes)
        var cuts = new NativeArray<Cut>(g.cuts.Count, Allocator.Persistent);
        for (int i = 0; i < g.cuts.Count; i++)
        {
            var c = g.cuts[i];
            int am = 0; foreach (var k in c.applyTo) am |= 1 << (int)k;
            cuts[i] = new Cut { kind = c.kind == GarmentCut.Kind.Bone ? 0 : 1, bone = (int)c.bone, at = c.at, keep = c.keepStart ? 1f : -1f, applyMask = am };
        }
        int cover = 0; foreach (var k in g.covers) cover |= 1 << (int)k;
        float hipY = sdf.Plan.bones[sdf.Plan.Index("Hips")].localPos.y;
        Kernel = new GarmentKernel
        {
            body = body, drape = drape, nearest = nearest, min = min, cell = cell, nx = nx, ny = ny, nz = nz, cuts = cuts, coverMask = cover, hipY = hipY,
            thickness = g.thickness, gap = g.gap, hemBlend = g.hemBlend,
        };
        if (g.HasSkirt) SetSkirt(sdf, g.skirt, hipY);
    }

    public void Dispose()
    {
        Complete();
        Kernel.Dispose();
    }

    // Skirt geometry from the plan: its axis under the hips, the hem at mid-shin (the shin cut), the slit
    // tops between the knee and the hip.
    void SetSkirt(BodySDF sdf, SkirtDef sk, float hipY)
    {
        var plan = sdf.Plan;
        int hips = plan.Index("Hips"), kneeL = plan.Index("KneeL"), chest = plan.Index("Chest");
        float hipWidth = 2f * Mathf.Abs(sdf.start[plan.Index("HipL")].x) + 2f * plan.bones[plan.Index("HipL")].Outer.x;
        float shinAt = 0.5f;
        foreach (var c in def.cuts) if (c.kind == GarmentCut.Kind.Bone && c.bone == BoneKind.Shin) shinAt = c.at;
        float hem = Mathf.Lerp(sdf.start[kneeL].y, sdf.end[kneeL].y, shinAt);
        float knee = sdf.start[kneeL].y;
        Kernel.hasSkirt = 1;
        Kernel.skirtTop = sk.startAt * hipY;
        Kernel.axis = new Vector2(sdf.start[hips].x, sdf.start[hips].z);
        // radius r0 ~ half the hip width; r = r0 + flare * hipWidth * drop  ->  k = 1 + 2 flare drop
        Kernel.flareK = 2f * sk.flare * hipWidth / Mathf.Max(0.05f, hipWidth);
        Kernel.hemY = hem;
        Kernel.chestY = chest >= 0 ? (sdf.start[chest].y + sdf.end[chest].y) * 0.5f : hipY * 1.5f;
        Kernel.openHem = sk.openingHem * 0.5f; Kernel.openChest = sk.openingChest * 0.5f;
        Kernel.lapel = sk.lapel; Kernel.lapelWidth = sk.lapelWidth;
        Kernel.slitTopY = Mathf.Lerp(knee, hipY, sk.slitTop);
        Kernel.slitHalf = sk.slitHalfWidth;
        Kernel.slitCount = Mathf.Min(4, sk.slitAngles.Length);
        for (int i = 0; i < Kernel.slitCount; i++) Kernel.slitAngles[i] = sk.slitAngles[i];
        SkirtHemY = hem;
    }

    public float SkirtHemY { get; private set; }

    // Debug view (Phase 1 check): the outer solid quick-meshed (surface nets at `cell`, one smoothing pass,
    // triangle normals). The body pokes out where the garment doesn't cover.
    public Mesh BuildDebugMesh(float cell = 0.006f)
    {
        Vector3 min = bounds.min;
        int nx = Mathf.CeilToInt(bounds.size.x / cell) + 1, ny = Mathf.CeilToInt(bounds.size.y / cell) + 1, nz = Mathf.CeilToInt(bounds.size.z / cell) + 1;
        int mx = (nx - 1) / C + 2, my = (ny - 1) / C + 2, mz = (nz - 1) / C + 2;
        var coarse = new NativeArray<float>(mx * my * mz, Allocator.TempJob);
        var fine = new NativeArray<float>(nx * ny * nz, Allocator.TempJob);
        var h = new OuterJob { kernel = Kernel, min = min, step = cell * C, nx = mx, ny = my, values = coarse }.Schedule(coarse.Length, 64);
        h = new OuterFineJob { kernel = Kernel, coarse = coarse, min = min, cell = cell, band = cell * C * 1.9f, nx = nx, ny = ny, mx = mx, my = my, values = fine }.Schedule(fine.Length, 256, h);
        h.Complete();
        var grid = fine.ToArray();
        coarse.Dispose(); fine.Dispose();
        var v = new List<Vector3>(); var t = new List<int>();
        SurfaceNets.Init();
        SurfaceNets.Extract(grid, nx, ny, nz, min, cell, v, t);
        BodyMesher.Smooth(v, t, 1, 0.5f);
        var mesh = new Mesh { name = def.name + " (debug)", indexFormat = v.Count > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16 };
        mesh.SetVertices(v); mesh.SetTriangles(t, 0); mesh.RecalculateNormals(); mesh.RecalculateBounds();
        return mesh;
    }

    [BurstCompile]
    struct OuterJob : IJobParallelFor
    {
        public GarmentKernel kernel;
        public Vector3 min;
        public float step;
        public int nx, ny;
        [WriteOnly] public NativeArray<float> values;
        public void Execute(int i) { int x = i % nx, y = (i / nx) % ny, z = i / (nx * ny); values[i] = kernel.OuterSolid(min + new Vector3(x, y, z) * step); }
    }

    [BurstCompile]
    struct OuterFineJob : IJobParallelFor
    {
        public GarmentKernel kernel;
        [ReadOnly] public NativeArray<float> coarse;
        public Vector3 min;
        public float cell, band;
        public int nx, ny, mx, my;
        [WriteOnly] public NativeArray<float> values;
        public void Execute(int i)
        {
            int x = i % nx, y = (i / nx) % ny, z = i / (nx * ny);
            int x0 = x / C, y0 = y / C, z0 = z / C;
            float fx = (x - x0 * C) / (float)C, fy = (y - y0 * C) / (float)C, fz = (z - z0 * C) / (float)C;
            float c00 = Mathf.Lerp(coarse[x0 + mx * (y0 + my * z0)], coarse[x0 + 1 + mx * (y0 + my * z0)], fx);
            float c10 = Mathf.Lerp(coarse[x0 + mx * (y0 + 1 + my * z0)], coarse[x0 + 1 + mx * (y0 + 1 + my * z0)], fx);
            float c01 = Mathf.Lerp(coarse[x0 + mx * (y0 + my * (z0 + 1))], coarse[x0 + 1 + mx * (y0 + my * (z0 + 1))], fx);
            float c11 = Mathf.Lerp(coarse[x0 + mx * (y0 + 1 + my * (z0 + 1))], coarse[x0 + 1 + mx * (y0 + 1 + my * (z0 + 1))], fx);
            float est = Mathf.Lerp(Mathf.Lerp(c00, c10, fy), Mathf.Lerp(c01, c11, fy), fz);
            values[i] = Mathf.Abs(est) > band ? est : kernel.OuterSolid(min + new Vector3(x, y, z) * cell);
        }
    }

    // Three separable passes (x, y, z). mode 0 min, 1 max, 2 mean. The result ends in `data`.
    static JobHandle Filter(NativeArray<float> data, NativeArray<float> tmp, int nx, int ny, int nz, int r, int mode, JobHandle dep)
    {
        dep = new FilterJob { input = data, output = tmp, nx = nx, ny = ny, nz = nz, axis = 0, r = r, mode = mode }.Schedule(ny * nz, 16, dep);
        dep = new FilterJob { input = tmp, output = data, nx = nx, ny = ny, nz = nz, axis = 1, r = r, mode = mode }.Schedule(nx * nz, 16, dep);
        dep = new FilterJob { input = data, output = tmp, nx = nx, ny = ny, nz = nz, axis = 2, r = r, mode = mode }.Schedule(nx * ny, 16, dep);
        return new CopyJob { input = tmp, output = data }.Schedule(data.Length, 1024, dep);
    }

    // ---------- jobs ----------

    [BurstCompile]
    struct BaseCoarseJob : IJobParallelFor
    {
        public DressedKernel kernel;
        public Vector3 min;
        public float step;
        public int nx, ny;
        [WriteOnly] public NativeArray<float> values;
        public void Execute(int i)
        {
            int x = i % nx, y = (i / nx) % ny, z = i / (nx * ny);
            Vector3 p = min + new Vector3(x, y, z) * step;
            values[i] = kernel.Base(p, kernel.body.Eval(p), kernel.count);
        }
    }

    [BurstCompile]
    struct SampleJob : IJobParallelFor
    {
        public DressedKernel kernel;
        [ReadOnly] public NativeArray<float> coarse;
        public Vector3 min;
        public float cell, band;
        public int nx, ny, mx, my;
        [WriteOnly] public NativeArray<float> values;
        [WriteOnly] public NativeArray<byte> nearest;
        public void Execute(int i)
        {
            int x = i % nx, y = (i / nx) % ny, z = i / (nx * ny);
            int x0 = x / C, y0 = y / C, z0 = z / C;
            float fx = (x - x0 * C) / (float)C, fy = (y - y0 * C) / (float)C, fz = (z - z0 * C) / (float)C;
            float c00 = Mathf.Lerp(coarse[x0 + mx * (y0 + my * z0)], coarse[x0 + 1 + mx * (y0 + my * z0)], fx);
            float c10 = Mathf.Lerp(coarse[x0 + mx * (y0 + 1 + my * z0)], coarse[x0 + 1 + mx * (y0 + 1 + my * z0)], fx);
            float c01 = Mathf.Lerp(coarse[x0 + mx * (y0 + my * (z0 + 1))], coarse[x0 + 1 + mx * (y0 + my * (z0 + 1))], fx);
            float c11 = Mathf.Lerp(coarse[x0 + mx * (y0 + 1 + my * (z0 + 1))], coarse[x0 + 1 + mx * (y0 + 1 + my * (z0 + 1))], fx);
            float est = Mathf.Lerp(Mathf.Lerp(c00, c10, fy), Mathf.Lerp(c01, c11, fy), fz);
            if (Mathf.Abs(est) > band) { values[i] = est; nearest[i] = Far; return; }
            Vector3 p = min + new Vector3(x, y, z) * cell;
            var s = kernel.body.Eval(p);
            values[i] = kernel.Base(p, s, kernel.count);
            nearest[i] = (byte)s.nearest;
        }
    }

    [BurstCompile]
    struct MaskJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float> values;
        [ReadOnly] public NativeArray<byte> nearest;
        [ReadOnly] public NativeArray<byte> exclude;
        [WriteOnly] public NativeArray<float> output;
        public void Execute(int i)
        {
            byte b = nearest[i];
            output[i] = b != Far && exclude[b] != 0 ? Mathf.Max(values[i], 1f) : values[i];
        }
    }

    [BurstCompile]
    struct FilterJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float> input;
        [NativeDisableParallelForRestriction] [WriteOnly] public NativeArray<float> output;
        public int nx, ny, nz, axis, r, mode;
        public void Execute(int line)
        {
            int len, stride, start;
            if (axis == 0) { len = nx; stride = 1; start = line * nx; }                                   // line = y + ny z
            else if (axis == 1) { len = ny; stride = nx; int x = line % nx, z = line / nx; start = x + nx * ny * z; }
            else { len = nz; stride = nx * ny; start = line; }                                            // line = x + nx y
            for (int i = 0; i < len; i++)
            {
                int a = Mathf.Max(0, i - r), b = Mathf.Min(len - 1, i + r);
                float acc = mode == 0 ? float.MaxValue : mode == 1 ? float.MinValue : 0f;
                for (int k = a; k <= b; k++)
                {
                    float v = input[start + k * stride];
                    if (mode == 0) acc = Mathf.Min(acc, v); else if (mode == 1) acc = Mathf.Max(acc, v); else acc += v;
                }
                output[start + i * stride] = mode == 2 ? acc / (b - a + 1) : acc;
            }
        }
    }

    [BurstCompile]
    struct CopyJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float> input;
        [WriteOnly] public NativeArray<float> output;
        public void Execute(int i) => output[i] = input[i];
    }

    [BurstCompile]
    struct CombineJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float> a;
        public NativeArray<float> output;
        public int first, aboveOnly, nx, ny;
        public float minY, cell, fromY, toY;
        public void Execute(int i)
        {
            if (first != 0) { output[i] = a[i]; return; }
            float m = Mathf.Min(output[i], a[i]);
            if (aboveOnly != 0)
            {
                float y = minY + ((i / nx) % ny) * cell;
                m = Mathf.Lerp(output[i], m, Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(fromY, toY, y)));
            }
            output[i] = m;
        }
    }

    [BurstCompile]
    struct WeightJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<byte> nearest;
        [ReadOnly] public NativeArray<int> kinds;
        public int mask;
        [WriteOnly] public NativeArray<float> output;
        public void Execute(int i)
        {
            byte b = nearest[i];
            output[i] = b != Far && (mask & (1 << kinds[b])) != 0 ? 1f : 0f;
        }
    }

    [BurstCompile]
    struct DrapeJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float> closed, weight;
        [ReadOnly] public NativeArray<byte> nearest;
        [ReadOnly] public NativeArray<int> kinds;
        public int mask, nx, ny, steps, stepCells;
        public float stepRise;
        [WriteOnly] public NativeArray<float> output;
        public void Execute(int i)
        {
            float c = closed[i], w = weight[i];
            if (w <= 0f || steps == 0) { output[i] = c; return; }
            int y = (i / nx) % ny;
            float d = c;
            for (int k = 1; k <= steps; k++)
            {
                int yy = y + k * stepCells;
                if (yy >= ny) break;
                int j = i + k * stepCells * nx;
                // Only the drape region holds the fabric up (not an arm above, say).
                byte b = nearest[j];
                if (b != Far && (mask & (1 << kinds[b])) == 0) continue;
                d = Mathf.Min(d, closed[j] + k * stepRise);
            }
            output[i] = Mathf.Lerp(c, d, w);
        }
    }
}

// Burst-side evaluation of one garment (body kernel + the draped grid + cuts).
public struct GarmentKernel
{
    public BodySdfKernel body;
    [ReadOnly] public NativeArray<float> drape;
    [ReadOnly] public NativeArray<byte> nearest;           // nearest body bone per grid cell (255: far from the body)
    [ReadOnly] public NativeArray<GarmentField.Cut> cuts;
    public Vector3 min;
    public float cell;
    public int nx, ny, nz, coverMask;
    public float hipY, thickness, gap, hemBlend;
    // Skirt (coats): below skirtTop one cone (the hip cross-section scaled out by 1 + flareK * drop), an
    // open front (half width openHem at hemY .. openChest at chestY), lapels, slits, a level hem.
    public int hasSkirt, slitCount;
    public float skirtTop, flareK, hemY, chestY, openHem, openChest, lapel, lapelWidth, slitTopY, slitHalf;
    public Vector2 axis;          // the skirt's axis (x, z)
    public Vector4 slitAngles;    // degrees, 0 = front, + = right

    // The draped, eased body distance (trilinear on the grid).
    public float Draped(Vector3 p)
    {
        Vector3 g = (p - min) / cell;
        int x0 = Mathf.Clamp((int)Mathf.Floor(g.x), 0, nx - 2), y0 = Mathf.Clamp((int)Mathf.Floor(g.y), 0, ny - 2), z0 = Mathf.Clamp((int)Mathf.Floor(g.z), 0, nz - 2);
        float fx = Mathf.Clamp01(g.x - x0), fy = Mathf.Clamp01(g.y - y0), fz = Mathf.Clamp01(g.z - z0);
        int sx = nx, sxy = nx * ny;
        int I(int x, int y, int z) => x + sx * y + sxy * z;
        float c00 = Mathf.Lerp(drape[I(x0, y0, z0)], drape[I(x0 + 1, y0, z0)], fx);
        float c10 = Mathf.Lerp(drape[I(x0, y0 + 1, z0)], drape[I(x0 + 1, y0 + 1, z0)], fx);
        float c01 = Mathf.Lerp(drape[I(x0, y0, z0 + 1)], drape[I(x0 + 1, y0, z0 + 1)], fx);
        float c11 = Mathf.Lerp(drape[I(x0, y0 + 1, z0 + 1)], drape[I(x0 + 1, y0 + 1, z0 + 1)], fx);
        return Mathf.Lerp(Mathf.Lerp(c00, c10, fy), Mathf.Lerp(c01, c11, fy), fz);
    }

    // The draped field, with the coat's skirt and lapels.
    public float Shape(Vector3 p)
    {
        float d = Draped(p);
        if (hasSkirt == 0) return d;
        if (p.y < skirtTop + 0.04f)
        {
            // The hip slice, scaled out with the drop (torso cells only: the hanging arms aren't skirt).
            float k = 1f + flareK * Mathf.Max(0f, skirtTop - p.y);
            Vector3 q = new Vector3(axis.x + (p.x - axis.x) / k, skirtTop, axis.y + (p.z - axis.y) / k);
            byte nb = NearestAt(q);
            int kind = nb == 255 ? -1 : body.bones[nb].kind;
            bool torso = kind == (int)BoneKind.Pelvis || kind == (int)BoneKind.Lumbar || kind == (int)BoneKind.Thigh;
            float skirt = torso ? k * Draped(q) : 0.3f;
            float t = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(skirtTop + 0.04f, skirtTop - 0.04f, p.y));
            d = Mathf.Lerp(d, skirt, t);
        }
        // Lapels: the opening's edges stand out a little (above the skirt).
        float e = Mathf.Abs(p.x - axis.x) - OpenHalf(p.y);
        if (p.z > axis.y && p.y > skirtTop && e > 0f && e < lapelWidth) d -= lapel * (1f - Mathf.SmoothStep(0f, 1f, e / lapelWidth));
        return d;
    }

    float OpenHalf(float y) => Mathf.Lerp(openHem, openChest, Mathf.Clamp01((y - hemY) / Mathf.Max(0.01f, chestY - hemY)));

    public byte NearestAt(Vector3 p)
    {
        Vector3 g = (p - min) / cell;
        int x = Mathf.Clamp(Mathf.RoundToInt(g.x), 0, nx - 1), y = Mathf.Clamp(Mathf.RoundToInt(g.y), 0, ny - 1), z = Mathf.Clamp(Mathf.RoundToInt(g.z), 0, nz - 1);
        return nearest[x + nx * (y + ny * z)];
    }

    // Open front, slits from the hem to mid-thigh, a level hem.
    public float SkirtRegion(Vector3 p)
    {
        float d = -0.05f;
        Vector2 v = new Vector2(p.x - axis.x, p.z - axis.y);
        if (v.y > 0f) d = Mathf.Max(d, OpenHalf(p.y) - Mathf.Abs(v.x));
        for (int i = 0; i < slitCount; i++)
        {
            float a = slitAngles[i] * Mathf.Deg2Rad;
            Vector2 dir = new Vector2(Mathf.Sin(a), Mathf.Cos(a)), n = new Vector2(dir.y, -dir.x);
            if (Vector2.Dot(v, dir) > 0f) d = Mathf.Max(d, Mathf.Min(slitHalf - Mathf.Abs(Vector2.Dot(v, n)), slitTopY - p.y));
        }
        return Mathf.Max(d, hemY - p.y);
    }

    // Covers the layers under it here (they can be dropped)? Not under the skirt: the legs move inside it.
    public bool Covers(Vector3 p, int nearestBone, float margin) => Region(p, nearestBone) < -margin && (hasSkirt == 0 || p.y > skirtTop);

    // Where the garment is (negative inside): covered by nearest bone, inside every cut.
    public float Region(Vector3 p, int nearest)
    {
        var b = body.bones[nearest];
        if ((coverMask & (1 << b.kind)) == 0) return 0.05f;
        float d = -0.05f;
        if (hasSkirt != 0) d = Mathf.Max(d, SkirtRegion(p));
        for (int i = 0; i < cuts.Length; i++)
        {
            var c = cuts[i];
            if (c.kind == 0)
            {
                if (c.bone != b.kind) continue;
                float t = Vector3.Dot(p - b.o, b.y) - c.at * b.length;
                d = Mathf.Max(d, c.keep * t);
            }
            else
            {
                if ((c.applyMask & (1 << b.kind)) == 0) continue;
                d = Mathf.Max(d, c.keep * (c.at * hipY - p.y));
            }
        }
        return d;
    }

    // The garment shell (negative inside the fabric).
    public float Eval(Vector3 p)
    {
        var s = body.Eval(p);
        float outer = Shape(p) - thickness - gap, inner = s.skin - gap;
        return BodySdfKernel.SMax(Mathf.Max(outer, -inner), Region(p, s.nearest), hemBlend);
    }

    // The solid the garment's outer surface encloses (body included), cut like the garment: the debug view,
    // and (Part A) the garment's mesh: its surface, minus whatever lies inside the layers beneath. Region by
    // the grid's nearest bone (no body evaluation: meshing samples this a lot).
    public float OuterSolid(Vector3 p)
    {
        byte nb = NearestAt(p);
        // Far from the body (outside the sampled band): only a skirt reaches out there.
        float region = nb != 255 ? Region(p, nb) : hasSkirt != 0 && p.y < skirtTop ? SkirtRegion(p) : 0.05f;
        return BodySdfKernel.SMax(Shape(p) - thickness - gap, region, hemBlend);
    }
    public float OuterSolidGiven(Vector3 p, BodySdfKernel.Sample s) => OuterSolid(p);

    public Vector3 Gradient(Vector3 p, float h)
    {
        Vector3 g = new Vector3(
            OuterSolid(p + new Vector3(h, 0f, 0f)) - OuterSolid(p - new Vector3(h, 0f, 0f)),
            OuterSolid(p + new Vector3(0f, h, 0f)) - OuterSolid(p - new Vector3(0f, h, 0f)),
            OuterSolid(p + new Vector3(0f, 0f, h)) - OuterSolid(p - new Vector3(0f, 0f, h)));
        float m = g.magnitude;
        return m > 1e-9f ? g / m : Vector3.up;
    }

    // A placeholder for empty DressedKernel slots (jobs need every container assigned).
    public static GarmentKernel Dummy(BodySdfKernel body, Allocator alloc) => new GarmentKernel
    {
        body = body, drape = new NativeArray<float>(8, alloc), nearest = new NativeArray<byte>(8, alloc), cuts = new NativeArray<GarmentField.Cut>(0, alloc), nx = 2, ny = 2, nz = 2, cell = 1f, coverMask = 0,
    };

    public void Dispose()
    {
        if (drape.IsCreated) drape.Dispose();
        if (nearest.IsCreated) nearest.Dispose();
        if (cuts.IsCreated) cuts.Dispose();
    }
}

// The body plus up to MaxGarments garments, for evaluating layers: a garment's base is the body and every
// garment under it (min of their solids), so outer layers rest on inner ones.
public struct DressedKernel
{
    public const int MaxGarments = 6;
    public BodySdfKernel body;
    public GarmentKernel g0, g1, g2, g3, g4, g5;
    public int count;

    public GarmentKernel Get(int i) => i == 0 ? g0 : i == 1 ? g1 : i == 2 ? g2 : i == 3 ? g3 : i == 4 ? g4 : g5;

    // Body + garments [0, upto): distance to the solid they make together.
    public float Base(Vector3 p, BodySdfKernel.Sample s, int upto)
    {
        float b = s.skin;
        int n = Mathf.Min(upto, count);
        for (int i = 0; i < n; i++) b = Mathf.Min(b, Get(i).OuterSolidGiven(p, s));
        return b;
    }

    public static DressedKernel Make(BodySdfKernel body, IList<GarmentKernel> garments, GarmentKernel dummy)
    {
        var k = new DressedKernel { body = body, count = garments != null ? Mathf.Min(garments.Count, MaxGarments) : 0 };
        GarmentKernel G(int i) => garments != null && i < garments.Count ? garments[i] : dummy;
        k.g0 = G(0); k.g1 = G(1); k.g2 = G(2); k.g3 = G(3); k.g4 = G(4); k.g5 = G(5);
        return k;
    }
}

// Part A: garment meshing jobs (BodyBuild). A garment's mesh is the surface of its outer solid, kept
// only outside the layers beneath it (the hem lip stays: the ring from the layer below out to the outer
// surface); the inside of the fabric is never seen, so the thin shell needn't be resolved.
public static class GarmentMeshJobs
{
    const int C = 4;

    [BurstCompile]
    public struct CoarseJob : IJobParallelFor
    {
        public GarmentKernel kernel;
        public Vector3 min;
        public float step;
        public int nx, ny;
        [WriteOnly] public NativeArray<float> values;
        public void Execute(int i) { int x = i % nx, y = (i / nx) % ny, z = i / (nx * ny); values[i] = kernel.OuterSolid(min + new Vector3(x, y, z) * step); }
    }

    [BurstCompile]
    public struct FineJob : IJobParallelFor
    {
        public GarmentKernel kernel;
        [ReadOnly] public NativeArray<float> coarse;
        public Vector3 min;
        public float cell, band;
        public int nx, ny, mx, my;
        [WriteOnly] public NativeArray<float> values;
        public void Execute(int i)
        {
            int x = i % nx, y = (i / nx) % ny, z = i / (nx * ny);
            int x0 = x / C, y0 = y / C, z0 = z / C;
            float fx = (x - x0 * C) / (float)C, fy = (y - y0 * C) / (float)C, fz = (z - z0 * C) / (float)C;
            float c00 = Mathf.Lerp(coarse[x0 + mx * (y0 + my * z0)], coarse[x0 + 1 + mx * (y0 + my * z0)], fx);
            float c10 = Mathf.Lerp(coarse[x0 + mx * (y0 + 1 + my * z0)], coarse[x0 + 1 + mx * (y0 + 1 + my * z0)], fx);
            float c01 = Mathf.Lerp(coarse[x0 + mx * (y0 + my * (z0 + 1))], coarse[x0 + 1 + mx * (y0 + my * (z0 + 1))], fx);
            float c11 = Mathf.Lerp(coarse[x0 + mx * (y0 + 1 + my * (z0 + 1))], coarse[x0 + 1 + mx * (y0 + 1 + my * (z0 + 1))], fx);
            float est = Mathf.Lerp(Mathf.Lerp(c00, c10, fy), Mathf.Lerp(c01, c11, fy), fz);
            values[i] = Mathf.Abs(est) > band ? est : kernel.OuterSolid(min + new Vector3(x, y, z) * cell);
        }
    }

    // Newton steps onto each vertex's garment surface (piece = garment index per vertex).
    [BurstCompile]
    public struct ProjectJob : IJobParallelFor
    {
        public DressedKernel kernel;
        public NativeArray<Vector3> positions;
        [ReadOnly] public NativeArray<byte> garment;
        [ReadOnly] public NativeArray<float> cells;
        public int steps;
        public void Execute(int i)
        {
            var g = kernel.Get(garment[i]);
            float c = cells[garment[i]], h = c * 0.25f;
            Vector3 p = positions[i];
            for (int s = 0; s < steps; s++)
            {
                float d = g.OuterSolid(p);
                if (Mathf.Abs(d) < 1e-5f) break;
                p -= g.Gradient(p, h) * Mathf.Clamp(d, -c, c);
            }
            positions[i] = p;
        }
    }

    // Normals (the garment surface's gradient), regions (nearest body bone), layers (garment, standoff),
    // flags (bit 0: inside the layers beneath, dropped; bit 1: under an outer garment, dropped unless the
    // whole triangle isn't), and where on the body the vertex sits (for skin weights).
    [BurstCompile]
    public struct AttribJob : IJobParallelFor
    {
        public DressedKernel kernel;
        [ReadOnly] public NativeArray<Vector3> positions;
        [ReadOnly] public NativeArray<byte> garment;
        [ReadOnly] public NativeArray<float> cells, gaps;
        public float coverMargin;
        [WriteOnly] public NativeArray<Vector3> normals, onBody;
        [WriteOnly] public NativeArray<Vector2> region, layers;
        [WriteOnly] public NativeArray<byte> flags;
        public void Execute(int i)
        {
            int gi = garment[i];
            var g = kernel.Get(gi);
            Vector3 p = positions[i];
            var s = kernel.body.Eval(p);
            normals[i] = g.Gradient(p, cells[gi] * 0.5f);
            region[i] = new Vector2(kernel.body.bones[s.nearest].kind, s.nearest);
            float below = kernel.Base(p, s, gi);
            layers[i] = new Vector2(gi, Mathf.Max(0f, below));
            byte f = 0;
            if (below < -gaps[gi] * 0.5f) f |= 1;
            for (int k = gi + 1; k < kernel.count; k++)
            {
                var o = kernel.Get(k);
                if (o.Covers(p, s.nearest, coverMargin) && o.OuterSolidGiven(p, s) < -0.002f) { f |= 2; break; }
            }
            flags[i] = f;
            // The body surface point under the vertex (one step along the skin's gradient).
            Vector3 n = kernel.body.Normal(p, 0.004f);
            onBody[i] = p - n * s.skin;
        }
    }

    // Body vertices under a garment (covered by its region, with a margin): their triangles are dropped.
    [BurstCompile]
    public struct BodyCoverJob : IJobParallelFor
    {
        public DressedKernel kernel;
        [ReadOnly] public NativeArray<Vector3> positions;
        public float coverMargin;
        [WriteOnly] public NativeArray<byte> covered;
        public void Execute(int i)
        {
            Vector3 p = positions[i];
            var s = kernel.body.Eval(p);
            byte c = 0;
            for (int k = 0; k < kernel.count; k++)
                if (kernel.Get(k).Covers(p, s.nearest, coverMargin)) { c = 1; break; }
            covered[i] = c;
        }
    }
}
