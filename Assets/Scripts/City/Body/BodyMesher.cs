using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Rendering;

// Phase 4: the body SDF to a mesh.
//   1. Sample (Burst, parallel): a coarse grid every 4 cells, then the fine grid (cell, ~1.5 cm) only
//      in the narrow band around the surface; elsewhere the coarse estimate (only its sign matters).
//   2. Surface nets (SurfaceNets.Extract): one vertex per surface cell, quads across crossed edges.
//   3. Smooth: a couple of Laplacian passes (toward the neighbours' average), then re-projected onto the
//      SDF surface (Newton steps along the gradient), so smoothing never shrinks the body.
//   4. Decimate to the triangle budget (quadric edge collapse); the head, hands and feet weigh more, so
//      the torso and limbs give up their triangles first.
//   5. Re-project, normals from the SDF gradient (not the triangles), and per-vertex data:
//      uv1 = (body region = BoneKind of the nearest bone, nearest bone index),
//      uv2 = (fat layer thickness, muscle layer thickness) at that point (for materials and clothing).
// Reports timings, triangle counts and holes (edges without exactly two faces).
public static class BodyMesher
{
    public class Settings
    {
        public float cell = 0.015f;
        public int targetTriangles = 10000;
        public int smoothIterations = 2;
        public float smoothStrength = 0.5f;
        public float normalStep = 1f;   // gradient sampled +- this many cells (smooths over kinks in the field)
        public float headImportance = 6f, handImportance = 4f, footImportance = 2f, neckImportance = 2f;
    }

    public class Result
    {
        public Mesh mesh;
        public int rawNonManifold, rawBoundary, flippedCorners, passes;
        public int samplesEvaluated, gridPoints, rawTriangles, triangles, vertices, boundaryEdges, nonManifoldEdges;
        public float sampleMs, extractMs, smoothMs, decimateMs, finishMs, totalMs;
        public readonly List<string> nonManifoldAt = new List<string>();
        public override string ToString() =>
            $"{triangles} tris ({rawTriangles} raw), {vertices} verts, holes {boundaryEdges}, non-manifold {nonManifoldEdges} (raw {rawBoundary} / {rawNonManifold}, {flippedCorners} corners flipped in {passes} passes); " +
            $"{totalMs:0} ms (sample {sampleMs:0}, extract {extractMs:0}, smooth {smoothMs:0}, decimate {decimateMs:0}, finish {finishMs:0})";
    }

    const int C = 4; // coarse step in cells

    [BurstCompile]
    internal struct CoarseJob : IJobParallelFor
    {
        public BodySdfKernel kernel;
        public Vector3 min;
        public float step;
        public int mx, my;
        [WriteOnly] public NativeArray<float> values;
        public void Execute(int i)
        {
            int x = i % mx, y = (i / mx) % my, z = i / (mx * my);
            values[i] = kernel.Skin(min + new Vector3(x, y, z) * step);
        }
    }

    [BurstCompile]
    internal struct FineJob : IJobParallelFor
    {
        public BodySdfKernel kernel;
        [ReadOnly] public NativeArray<float> coarse;
        public Vector3 min;
        public float cell, band;
        public int nx, ny, mx, my;
        [WriteOnly] public NativeArray<float> values;
        [WriteOnly] public NativeArray<byte> evaluated;
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
            if (Mathf.Abs(est) > band) { values[i] = est; evaluated[i] = 0; return; }
            values[i] = kernel.Skin(min + new Vector3(x, y, z) * cell);
            evaluated[i] = 1;
        }
    }

    // Newton steps onto the surface: p -= n * d.
    [BurstCompile]
    internal struct ProjectJob : IJobParallelFor
    {
        public BodySdfKernel kernel;
        public NativeArray<Vector3> positions;
        public float h, maxStep;
        public int steps;
        public void Execute(int i)
        {
            Vector3 p = positions[i];
            for (int s = 0; s < steps; s++)
            {
                float d = kernel.Skin(p);
                if (Mathf.Abs(d) < 1e-5f) break;
                Vector3 n = kernel.Normal(p, h);
                p -= n * Mathf.Clamp(d, -maxStep, maxStep);
            }
            positions[i] = p;
        }
    }

    [BurstCompile]
    internal struct AttribJob : IJobParallelFor
    {
        public BodySdfKernel kernel;
        [ReadOnly] public NativeArray<Vector3> positions;
        public float h;
        [WriteOnly] public NativeArray<Vector3> normals;
        [WriteOnly] public NativeArray<Vector2> region, layers;
        public void Execute(int i)
        {
            Vector3 p = positions[i];
            var s = kernel.Eval(p);
            normals[i] = kernel.Normal(p, h);
            region[i] = new Vector2(kernel.bones[s.nearest].kind, s.nearest);
            // At the skin: the muscle surface lies `muscle` below it (the fat layer), the core `core`
            // below (fat + muscle).
            float fat = Mathf.Max(0f, s.muscle - s.skin), muscle = Mathf.Max(0f, s.core - s.muscle);
            layers[i] = new Vector2(fat, muscle);
        }
    }

    public static Result Build(BodySDF sdf, Settings settings = null)
    {
        settings ??= new Settings();
        var res = new Result();
        var total = System.Diagnostics.Stopwatch.StartNew();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var kernel = sdf.BuildKernel(Allocator.TempJob);
        float cell = settings.cell;
        Bounds bounds = sdf.bounds;
        Vector3 min = bounds.min - Vector3.one * cell;
        Vector3 size = bounds.size + Vector3.one * cell * 2f;
        int nx = Mathf.CeilToInt(size.x / cell) + 1, ny = Mathf.CeilToInt(size.y / cell) + 1, nz = Mathf.CeilToInt(size.z / cell) + 1;
        int mx = (nx - 1) / C + 2, my = (ny - 1) / C + 2, mz = (nz - 1) / C + 2;

        // ---------- 1. sample ----------
        var coarse = new NativeArray<float>(mx * my * mz, Allocator.TempJob);
        var fine = new NativeArray<float>(nx * ny * nz, Allocator.TempJob);
        var evaluated = new NativeArray<byte>(nx * ny * nz, Allocator.TempJob);
        new CoarseJob { kernel = kernel, min = min, step = cell * C, mx = mx, my = my, values = coarse }.Schedule(coarse.Length, 64).Complete();
        new FineJob { kernel = kernel, coarse = coarse, min = min, cell = cell, band = cell * C * 1.9f, nx = nx, ny = ny, mx = mx, my = my, values = fine, evaluated = evaluated }
            .Schedule(fine.Length, 256).Complete();
        var grid = fine.ToArray();
        int evalCount = coarse.Length;
        for (int i = 0; i < evaluated.Length; i++) evalCount += evaluated[i];
        coarse.Dispose(); fine.Dispose(); evaluated.Dispose();
        res.samplesEvaluated = evalCount;
        res.gridPoints = grid.Length;
        res.sampleMs = (float)sw.Elapsed.TotalMilliseconds; sw.Restart();

        // ---------- 2. surface nets ----------
        var verts = new List<Vector3>();
        var tris = new List<int>();
        res.flippedCorners = SurfaceNets.Extract(grid, nx, ny, nz, min, cell, verts, tris);
        res.passes = SurfaceNets.LastPasses;
        res.rawTriangles = tris.Count / 3;
        res.extractMs = (float)sw.Elapsed.TotalMilliseconds;
        CountEdges(tris, out res.rawBoundary, out res.rawNonManifold);
        sw.Restart();

        // ---------- 3. smooth + re-project ----------
        var adj = Neighbours(verts.Count, tris);
        var tmp = new Vector3[verts.Count];
        for (int it = 0; it < settings.smoothIterations; it++)
        {
            for (int i = 0; i < verts.Count; i++)
            {
                var nb = adj[i];
                if (nb.Count == 0) { tmp[i] = verts[i]; continue; }
                Vector3 avg = Vector3.zero;
                foreach (int j in nb) avg += verts[j];
                avg /= nb.Count;
                tmp[i] = Vector3.Lerp(verts[i], avg, settings.smoothStrength);
            }
            for (int i = 0; i < verts.Count; i++) verts[i] = tmp[i];
        }
        Project(kernel, verts, cell, 3);
        res.smoothMs = (float)sw.Elapsed.TotalMilliseconds; sw.Restart();

        // ---------- 4. decimate ----------
        var importance = new float[verts.Count];
        {
            var posN = new NativeArray<Vector3>(verts.ToArray(), Allocator.TempJob);
            var nrm = new NativeArray<Vector3>(verts.Count, Allocator.TempJob);
            var reg = new NativeArray<Vector2>(verts.Count, Allocator.TempJob);
            var lay = new NativeArray<Vector2>(verts.Count, Allocator.TempJob);
            new AttribJob { kernel = kernel, positions = posN, h = settings.normalStep * cell, normals = nrm, region = reg, layers = lay }.Schedule(verts.Count, 64).Complete();
            for (int i = 0; i < verts.Count; i++)
            {
                var k = (BoneKind)(int)reg[i].x;
                importance[i] = k == BoneKind.Head ? settings.headImportance : k == BoneKind.Hand ? settings.handImportance
                              : k == BoneKind.Foot ? settings.footImportance : k == BoneKind.Neck ? settings.neckImportance : 1f;
            }
            posN.Dispose(); nrm.Dispose(); reg.Dispose(); lay.Dispose();
        }
        List<Vector3> dv; List<int> dt;
        if (tris.Count / 3 > settings.targetTriangles)
            MeshDecimator.Run(verts, tris, importance, settings.targetTriangles, out dv, out dt, out _);
        else { dv = verts; dt = tris; }
        res.decimateMs = (float)sw.Elapsed.TotalMilliseconds; sw.Restart();

        // ---------- 5. re-project, normals, attributes ----------
        Project(kernel, dv, cell, 3);
        var positions = new NativeArray<Vector3>(dv.ToArray(), Allocator.TempJob);
        var normals = new NativeArray<Vector3>(dv.Count, Allocator.TempJob);
        var region = new NativeArray<Vector2>(dv.Count, Allocator.TempJob);
        var layers = new NativeArray<Vector2>(dv.Count, Allocator.TempJob);
        new AttribJob { kernel = kernel, positions = positions, h = settings.normalStep * cell, normals = normals, region = region, layers = layers }.Schedule(dv.Count, 64).Complete();
        var mesh = new Mesh { name = "Body " + sdf.Plan.sheet, indexFormat = dv.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
        mesh.SetVertices(positions);
        mesh.SetNormals(normals);
        mesh.SetUVs(1, region);
        mesh.SetUVs(2, layers);
        mesh.SetTriangles(dt, 0);
        mesh.RecalculateBounds();
        positions.Dispose(); normals.Dispose(); region.Dispose(); layers.Dispose();
        kernel.Dispose();
        res.mesh = mesh;
        res.triangles = dt.Count / 3;
        res.vertices = dv.Count;
        CountEdges(dt, out res.boundaryEdges, out res.nonManifoldEdges, dv, sdf, res.nonManifoldAt);
        res.finishMs = (float)sw.Elapsed.TotalMilliseconds;
        res.totalMs = (float)total.Elapsed.TotalMilliseconds;
        return res;
    }

    static void Project(BodySdfKernel kernel, List<Vector3> verts, float cell, int steps)
    {
        var p = new NativeArray<Vector3>(verts.ToArray(), Allocator.TempJob);
        new ProjectJob { kernel = kernel, positions = p, h = cell * 0.25f, maxStep = cell, steps = steps }.Schedule(p.Length, 64).Complete();
        for (int i = 0; i < verts.Count; i++) verts[i] = p[i];
        p.Dispose();
    }

    // Laplacian smoothing: each vertex moves toward its neighbours' average (re-project afterwards).
    internal static void Smooth(List<Vector3> verts, List<int> tris, int iterations, float strength)
    {
        var adj = Neighbours(verts.Count, tris);
        var tmp = new Vector3[verts.Count];
        for (int it = 0; it < iterations; it++)
        {
            for (int i = 0; i < verts.Count; i++)
            {
                var nb = adj[i];
                if (nb.Count == 0) { tmp[i] = verts[i]; continue; }
                Vector3 avg = Vector3.zero;
                foreach (int j in nb) avg += verts[j];
                tmp[i] = Vector3.Lerp(verts[i], avg / nb.Count, strength);
            }
            for (int i = 0; i < verts.Count; i++) verts[i] = tmp[i];
        }
    }

    internal static float Importance(Settings s, BoneKind k) =>
        k == BoneKind.Head ? s.headImportance : k == BoneKind.Hand ? s.handImportance
        : k == BoneKind.Foot ? s.footImportance : k == BoneKind.Neck ? s.neckImportance : 1f;

    internal const int CoarseStep = C;

    static List<int>[] Neighbours(int n, List<int> tris)
    {
        var adj = new List<int>[n];
        for (int i = 0; i < n; i++) adj[i] = new List<int>(6);
        void Link(int a, int b) { if (!adj[a].Contains(b)) adj[a].Add(b); if (!adj[b].Contains(a)) adj[b].Add(a); }
        for (int t = 0; t < tris.Count; t += 3) { Link(tris[t], tris[t + 1]); Link(tris[t + 1], tris[t + 2]); Link(tris[t + 2], tris[t]); }
        return adj;
    }

    // Edges used by one face (holes) or more than two (non-manifold).
    public static void CountEdges(List<int> tris, out int boundary, out int nonManifold, List<Vector3> verts = null, BodySDF sdf = null, List<string> where = null)
    {
        var count = new Dictionary<long, int>(tris.Count);
        for (int t = 0; t < tris.Count; t += 3)
            for (int e = 0; e < 3; e++)
            {
                int a = tris[t + e], b = tris[t + (e + 1) % 3];
                long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                count.TryGetValue(key, out int c);
                count[key] = c + 1;
            }
        boundary = 0; nonManifold = 0;
        foreach (var kv in count)
        {
            if (kv.Value == 1) boundary++;
            else if (kv.Value > 2)
            {
                nonManifold++;
                if (where != null && verts != null && sdf != null && where.Count < 12)
                {
                    Vector3 m = (verts[(int)(kv.Key >> 32)] + verts[(int)(kv.Key & 0xffffffff)]) * 0.5f;
                    sdf.Eval(m, out int nb, out _);
                    where.Add($"{sdf.Plan.bones[nb].name}({m.x:0.00},{m.y:0.00},{m.z:0.00})");
                }
            }
        }
    }
}
