using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// Surface nets: sample an SDF on a grid; one vertex per cell the surface passes through (the average of
// the cell's edge crossings), and a quad for every grid edge the surface crosses, joining the four
// cells around it. Smooth organic surfaces with clean quads.
//   Extract : the mesh from a sampled grid (shared with BodyMesher, which samples with Burst).
//   Build   : quick managed version (samples with a delegate, triangle normals) for debugging.
public static class SurfaceNets
{
    public struct Result { public Mesh mesh; public int samples, vertices, triangles; public float sampleMs, meshMs; }

    // Grid values v (x fastest, then y, then z), nx * ny * nz points at min + (x, y, z) * cell.
    // Ambiguous cells (two separate pieces of surface in one cell: a gap or bridge thinner than a cell)
    // would share one vertex and make non-manifold edges; they're resolved first by moving the outside
    // corner nearest the surface inside (sub-cell gaps and pinches fill). Returns the corners flipped.
    public static int Extract(float[] v, int nx, int ny, int nz, Vector3 min, float cell, List<Vector3> verts, List<int> tris)
    {
        int Idx(int x, int y, int z) => x + nx * (y + ny * z);
        int cx = nx - 1, cy = ny - 1, cz = nz - 1;
        int flipped = ResolveAmbiguous(v, nx, ny, nz);
        var cellVert = new int[cx * cy * cz];
        int CIdx(int x, int y, int z) => x + cx * (y + cy * z);
        var corner = new float[8];
        for (int z = 0; z < cz; z++)
            for (int y = 0; y < cy; y++)
                for (int x = 0; x < cx; x++)
                {
                    int inside = 0;
                    for (int c = 0; c < 8; c++)
                    {
                        corner[c] = v[Idx(x + (c & 1), y + ((c >> 1) & 1), z + ((c >> 2) & 1))];
                        if (corner[c] < 0f) inside++;
                    }
                    if (inside == 0 || inside == 8) { cellVert[CIdx(x, y, z)] = -1; continue; }
                    Vector3 sum = Vector3.zero; int cnt = 0;
                    for (int e = 0; e < 12; e++)
                    {
                        int a = EdgeA[e], b = EdgeB[e];
                        float da = corner[a], db = corner[b];
                        if ((da < 0f) == (db < 0f)) continue;
                        float t = da / (da - db);
                        sum += Vector3.Lerp(Corner(a), Corner(b), t);
                        cnt++;
                    }
                    cellVert[CIdx(x, y, z)] = verts.Count;
                    verts.Add(min + (new Vector3(x, y, z) + sum / cnt) * cell);
                }

        // A quad per crossed grid edge, from the four cells sharing it.
        void Quad(int a, int b, int c, int d, bool flip)
        {
            if (a < 0 || b < 0 || c < 0 || d < 0) return;
            if (flip) { tris.Add(a); tris.Add(c); tris.Add(b); tris.Add(a); tris.Add(d); tris.Add(c); }
            else { tris.Add(a); tris.Add(b); tris.Add(c); tris.Add(a); tris.Add(c); tris.Add(d); }
        }
        for (int z = 1; z < cz; z++)
            for (int y = 1; y < cy; y++)
                for (int x = 1; x < cx; x++)
                {
                    bool in0 = v[Idx(x, y, z)] < 0f;
                    if (in0 != (v[Idx(x + 1, y, z)] < 0f))
                        Quad(cellVert[CIdx(x, y - 1, z - 1)], cellVert[CIdx(x, y, z - 1)], cellVert[CIdx(x, y, z)], cellVert[CIdx(x, y - 1, z)], !in0);
                    if (in0 != (v[Idx(x, y + 1, z)] < 0f))
                        Quad(cellVert[CIdx(x - 1, y, z - 1)], cellVert[CIdx(x - 1, y, z)], cellVert[CIdx(x, y, z)], cellVert[CIdx(x, y, z - 1)], !in0);
                    if (in0 != (v[Idx(x, y, z + 1)] < 0f))
                        Quad(cellVert[CIdx(x - 1, y - 1, z)], cellVert[CIdx(x, y - 1, z)], cellVert[CIdx(x, y, z)], cellVert[CIdx(x - 1, y, z)], !in0);
                }
        return flipped;
    }

    // Corner masks (bit c = corner c inside) whose inside or outside corners fall apart into more than
    // one piece along the cube's edges.
    static bool[] ambiguous;

    static bool[] AmbiguousTable()
    {
        var t = new bool[256];
        int Pieces(int mask)
        {
            int seen = 0, pieces = 0;
            for (int c = 0; c < 8; c++)
            {
                if ((mask >> c & 1) == 0 || (seen >> c & 1) != 0) continue;
                pieces++;
                var stack = new Stack<int>(); stack.Push(c); seen |= 1 << c;
                while (stack.Count > 0)
                {
                    int a = stack.Pop();
                    for (int bit = 0; bit < 3; bit++)
                    {
                        int b = a ^ (1 << bit);
                        if ((mask >> b & 1) != 0 && (seen >> b & 1) == 0) { seen |= 1 << b; stack.Push(b); }
                    }
                }
            }
            return pieces;
        }
        // A face with its inside corners on one diagonal and outside on the other: the two cells sharing
        // that face would both put four quads on the mesh edge between them.
        bool FaceDiagonal(int mask)
        {
            for (int axis = 0; axis < 3; axis++)
                for (int side = 0; side < 2; side++)
                {
                    int u = (axis + 1) % 3, w = (axis + 2) % 3;
                    int Corner(int a, int b) => (side << axis) | (a << u) | (b << w);
                    bool c00 = (mask >> Corner(0, 0) & 1) != 0, c11 = (mask >> Corner(1, 1) & 1) != 0;
                    bool c01 = (mask >> Corner(0, 1) & 1) != 0, c10 = (mask >> Corner(1, 0) & 1) != 0;
                    if (c00 == c11 && c01 == c10 && c00 != c01) return true;
                }
            return false;
        }
        for (int m = 1; m < 255; m++) t[m] = Pieces(m) > 1 || Pieces(~m & 255) > 1 || FaceDiagonal(m);
        return t;
    }

    public static int LastPasses;
    // Build the lookup table up front (on the main thread) before extracting on worker threads.
    public static void Init() => ambiguous ??= AmbiguousTable();
    static int ResolveAmbiguous(float[] v, int nx, int ny, int nz)
    {
        ambiguous ??= AmbiguousTable();
        int Idx(int x, int y, int z) => x + nx * (y + ny * z);
        int flips = 0;
        for (int pass = 0; pass < 32; pass++)
        {
            int changed = 0;
            for (int z = 0; z < nz - 1; z++)
                for (int y = 0; y < ny - 1; y++)
                    for (int x = 0; x < nx - 1; x++)
                    {
                        int mask = 0;
                        for (int c = 0; c < 8; c++)
                            if (v[Idx(x + (c & 1), y + ((c >> 1) & 1), z + ((c >> 2) & 1))] < 0f) mask |= 1 << c;
                        if (!ambiguous[mask]) continue;
                        // Fill: the outside corner nearest the surface goes inside. Always this way, so
                        // the passes can't undo each other and must finish.
                        int best = -1; float bv = float.MaxValue;
                        for (int c = 0; c < 8; c++)
                        {
                            float a = v[Idx(x + (c & 1), y + ((c >> 1) & 1), z + ((c >> 2) & 1))];
                            if (a >= 0f && a < bv) { bv = a; best = c; }
                        }
                        if (best < 0) continue;
                        v[Idx(x + (best & 1), y + ((best >> 1) & 1), z + ((best >> 2) & 1))] = -1e-5f;
                        changed++;
                    }
            flips += changed;
            LastPasses = pass + 1;
            if (changed == 0) break;
        }
        return flips;
    }

    public static Result Build(Func<Vector3, float> sdf, Bounds bounds, float cell)
    {
        var res = new Result();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Vector3 min = bounds.min - Vector3.one * cell;
        Vector3 size = bounds.size + Vector3.one * cell * 2f;
        int nx = Mathf.CeilToInt(size.x / cell) + 1, ny = Mathf.CeilToInt(size.y / cell) + 1, nz = Mathf.CeilToInt(size.z / cell) + 1;
        var v = new float[nx * ny * nz];
        int Idx(int x, int y, int z) => x + nx * (y + ny * z);
        // Narrow band: a coarse grid first (every C cells); fine samples are only evaluated where the
        // coarse field says the surface may be near, elsewhere the coarse value is interpolated (only its
        // sign matters there). The SDF is roughly distance-like, so the band is safe.
        const int C = 4;
        int mx = (nx - 1) / C + 2, my = (ny - 1) / C + 2, mz = (nz - 1) / C + 2;
        var coarse = new float[mx * my * mz];
        int CI(int x, int y, int z) => x + mx * (y + my * z);
        for (int z = 0; z < mz; z++)
            for (int y = 0; y < my; y++)
                for (int x = 0; x < mx; x++)
                    coarse[CI(x, y, z)] = sdf(min + new Vector3(x, y, z) * cell * C);
        int evaluated = coarse.Length;
        float band = cell * C * 1.9f;
        for (int z = 0; z < nz; z++)
            for (int y = 0; y < ny; y++)
                for (int x = 0; x < nx; x++)
                {
                    float est = CoarseEstimate(coarse, mx, my, x, y, z, C);
                    if (Mathf.Abs(est) > band) { v[Idx(x, y, z)] = est; continue; }
                    v[Idx(x, y, z)] = sdf(min + new Vector3(x, y, z) * cell);
                    evaluated++;
                }
        res.samples = evaluated;
        res.sampleMs = (float)sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        var verts = new List<Vector3>();
        var tris = new List<int>();
        Extract(v, nx, ny, nz, min, cell, verts, tris);
        var mesh = new Mesh { name = "SurfaceNets", indexFormat = verts.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
        mesh.SetVertices(verts);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        res.mesh = mesh;
        res.vertices = verts.Count;
        res.triangles = tris.Count / 3;
        res.meshMs = (float)sw.Elapsed.TotalMilliseconds;
        return res;
    }

    // Trilinear estimate of a fine grid point from the coarse grid (every C fine cells).
    public static float CoarseEstimate(float[] coarse, int mx, int my, int x, int y, int z, int C)
    {
        int CI(int a, int b, int c) => a + mx * (b + my * c);
        int x0 = x / C, y0 = y / C, z0 = z / C;
        float fx = (x - x0 * C) / (float)C, fy = (y - y0 * C) / (float)C, fz = (z - z0 * C) / (float)C;
        float c00 = Mathf.Lerp(coarse[CI(x0, y0, z0)], coarse[CI(x0 + 1, y0, z0)], fx);
        float c10 = Mathf.Lerp(coarse[CI(x0, y0 + 1, z0)], coarse[CI(x0 + 1, y0 + 1, z0)], fx);
        float c01 = Mathf.Lerp(coarse[CI(x0, y0, z0 + 1)], coarse[CI(x0 + 1, y0, z0 + 1)], fx);
        float c11 = Mathf.Lerp(coarse[CI(x0, y0 + 1, z0 + 1)], coarse[CI(x0 + 1, y0 + 1, z0 + 1)], fx);
        return Mathf.Lerp(Mathf.Lerp(c00, c10, fy), Mathf.Lerp(c01, c11, fy), fz);
    }

    static Vector3 Corner(int c) => new Vector3(c & 1, (c >> 1) & 1, (c >> 2) & 1);
    static readonly int[] EdgeA = { 0, 2, 4, 6, 0, 1, 4, 5, 0, 1, 2, 3 };
    static readonly int[] EdgeB = { 1, 3, 5, 7, 2, 3, 6, 7, 4, 5, 6, 7 };
}
