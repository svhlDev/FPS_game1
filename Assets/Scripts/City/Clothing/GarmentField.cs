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

    public GarmentField(BodySDF sdf, GarmentDef g, float cell = 0.01f)
    {
        def = g;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var body = sdf.Kernel;
        float pad = g.ease * 2f + g.thickness + g.gap + 0.03f;
        bounds = sdf.bounds;
        bounds.Expand(pad * 2f);
        Vector3 min = bounds.min;
        int nx = Mathf.CeilToInt(bounds.size.x / cell) + 1, ny = Mathf.CeilToInt(bounds.size.y / cell) + 1, nz = Mathf.CeilToInt(bounds.size.z / cell) + 1;
        int n = nx * ny * nz;
        int mx = (nx - 1) / C + 2, my = (ny - 1) / C + 2, mz = (nz - 1) / C + 2;

        // 1. body distance + nearest bone
        var coarse = new NativeArray<float>(mx * my * mz, Allocator.TempJob);
        var values = new NativeArray<float>(n, Allocator.TempJob);
        var nearest = new NativeArray<byte>(n, Allocator.TempJob);
        var h = new BodyMesher.CoarseJob { kernel = body, min = min, step = cell * C, mx = mx, my = my, values = coarse }.Schedule(coarse.Length, 64);
        float band = Mathf.Max(cell * C * 1.9f, g.ease * 2f + g.thickness + 0.02f);
        h = new SampleJob { kernel = body, coarse = coarse, min = min, cell = cell, band = band, nx = nx, ny = ny, mx = mx, my = my, values = values, nearest = nearest }.Schedule(n, 256, h);
        h.Complete();
        coarse.Dispose();

        // 2. closing (per leg for split garments)
        int nb = body.bones.Length;
        var closed = new NativeArray<float>(n, Allocator.Persistent);
        var tmpA = new NativeArray<float>(n, Allocator.TempJob);
        var tmpB = new NativeArray<float>(n, Allocator.TempJob);
        int r = Mathf.Max(1, Mathf.RoundToInt(g.ease / cell));
        // Split garments: each leg on its own, then the whole body again above the crotch (so the legs'
        // own closings don't leave a notch where they meet the pelvis).
        var sides = g.splitLegs ? new[] { -1, 1, 0 } : new[] { 0 };
        int pelvis = sdf.Plan.Index("Hips");
        float crotchY = pelvis >= 0 ? sdf.end[pelvis].y : 0f;
        bool first = true;
        foreach (int side in sides)
        {
            var exclude = new NativeArray<byte>(nb, Allocator.TempJob);
            for (int i = 0; i < nb; i++)
            {
                var b = body.bones[i];
                bool leg = b.kind == (int)BoneKind.Thigh || b.kind == (int)BoneKind.Shin || b.kind == (int)BoneKind.Foot;
                exclude[i] = (byte)(side != 0 && leg && b.side == -side ? 1 : 0);
            }
            h = new MaskJob { values = values, nearest = nearest, exclude = exclude, output = tmpA }.Schedule(n, 1024);
            h = Filter(tmpA, tmpB, nx, ny, nz, r, 0, h);    // dilate: min filter (result in tmpA)
            h = Filter(tmpA, tmpB, nx, ny, nz, r, 1, h);    // erode: max filter
            bool aboveOnly = g.splitLegs && side == 0;
            h = new CombineJob { a = tmpA, output = closed, first = first ? 1 : 0, aboveOnly = aboveOnly ? 1 : 0, nx = nx, ny = ny,
                                 minY = min.y, cell = cell, fromY = crotchY - 0.01f, toY = crotchY + 0.05f }.Schedule(n, 1024, h);
            h.Complete();
            exclude.Dispose();
            first = false;
        }

        // 3. drape
        int drapeMask = 0; foreach (var k in g.drapeOn) drapeMask |= 1 << (int)k;
        var kinds = new NativeArray<int>(nb, Allocator.TempJob);
        for (int i = 0; i < nb; i++) kinds[i] = body.bones[i].kind;
        h = new WeightJob { nearest = nearest, kinds = kinds, mask = drapeMask, output = tmpA }.Schedule(n, 1024);
        h = Filter(tmpA, tmpB, nx, ny, nz, Mathf.Max(1, Mathf.RoundToInt(0.03f / cell)), 2, h);   // smooth the region's edge (~3 cm)
        var drape = new NativeArray<float>(n, Allocator.Persistent);
        int steps = 8;
        int stepCells = Mathf.Max(1, Mathf.RoundToInt(g.hangLength / steps / cell));
        h = new DrapeJob { closed = closed, nearest = nearest, kinds = kinds, mask = drapeMask, weight = tmpA, nx = nx, ny = ny, steps = g.hangLength > 0f ? steps : 0,
                           stepCells = stepCells, stepRise = stepCells * cell * g.slope, output = drape }.Schedule(n, 256, h);
        h.Complete();
        kinds.Dispose(); tmpA.Dispose(); tmpB.Dispose(); values.Dispose(); nearest.Dispose(); closed.Dispose();

        // 4. the kernel
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
            body = body, drape = drape, min = min, cell = cell, nx = nx, ny = ny, nz = nz, cuts = cuts, coverMask = cover, hipY = hipY,
            thickness = g.thickness, gap = g.gap, hemBlend = g.hemBlend,
        };
        buildMs = (float)sw.Elapsed.TotalMilliseconds;
    }

    public void Dispose()
    {
        if (Kernel.drape.IsCreated) Kernel.drape.Dispose();
        if (Kernel.cuts.IsCreated) Kernel.cuts.Dispose();
    }

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
    struct SampleJob : IJobParallelFor
    {
        public BodySdfKernel kernel;
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
            var s = kernel.Eval(min + new Vector3(x, y, z) * cell);
            values[i] = s.skin;
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
    [ReadOnly] public NativeArray<GarmentField.Cut> cuts;
    public Vector3 min;
    public float cell;
    public int nx, ny, nz, coverMask;
    public float hipY, thickness, gap, hemBlend;

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

    // Where the garment is (negative inside): covered by nearest bone, inside every cut.
    public float Region(Vector3 p, int nearest)
    {
        var b = body.bones[nearest];
        if ((coverMask & (1 << b.kind)) == 0) return 0.05f;
        float d = -0.05f;
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
        float outer = Draped(p) - thickness - gap, inner = s.skin - gap;
        return BodySdfKernel.SMax(Mathf.Max(outer, -inner), Region(p, s.nearest), hemBlend);
    }

    // The solid the garment's outer surface encloses (body included), cut like the garment: the debug view.
    public float OuterSolid(Vector3 p)
    {
        var s = body.Eval(p);
        return BodySdfKernel.SMax(Draped(p) - thickness - gap, Region(p, s.nearest), hemBlend);
    }
}
