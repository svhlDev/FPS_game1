using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// Surface nets: sample an SDF on a grid; one vertex per cell the surface passes through (the average of
// the cell's edge crossings), and a quad for every grid edge the surface crosses, joining the four
// cells around it. Smooth organic surfaces with clean quads.
// Phase 3 uses it as the quick debug mesh (triangle normals); Phase 4 builds on it.
public static class SurfaceNets
{
    public struct Result { public Mesh mesh; public int samples, vertices, triangles; public float sampleMs, meshMs; }

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
                    int x0 = x / C, y0 = y / C, z0 = z / C;
                    float fx = (x - x0 * C) / (float)C, fy = (y - y0 * C) / (float)C, fz = (z - z0 * C) / (float)C;
                    float c00 = Mathf.Lerp(coarse[CI(x0, y0, z0)], coarse[CI(x0 + 1, y0, z0)], fx);
                    float c10 = Mathf.Lerp(coarse[CI(x0, y0 + 1, z0)], coarse[CI(x0 + 1, y0 + 1, z0)], fx);
                    float c01 = Mathf.Lerp(coarse[CI(x0, y0, z0 + 1)], coarse[CI(x0 + 1, y0, z0 + 1)], fx);
                    float c11 = Mathf.Lerp(coarse[CI(x0, y0 + 1, z0 + 1)], coarse[CI(x0 + 1, y0 + 1, z0 + 1)], fx);
                    float est = Mathf.Lerp(Mathf.Lerp(c00, c10, fy), Mathf.Lerp(c01, c11, fy), fz);
                    if (Mathf.Abs(est) > band) { v[Idx(x, y, z)] = est; continue; }
                    v[Idx(x, y, z)] = sdf(min + new Vector3(x, y, z) * cell);
                    evaluated++;
                }
        res.samples = evaluated;
        res.sampleMs = (float)sw.Elapsed.TotalMilliseconds;
        sw.Restart();

        // One vertex per surface cell.
        int cx = nx - 1, cy = ny - 1, cz = nz - 1;
        var cellVert = new int[cx * cy * cz];
        var verts = new List<Vector3>();
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
        var tris = new List<int>();
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
                    float d0 = v[Idx(x, y, z)];
                    bool in0 = d0 < 0f;
                    // Edge along +x from (x,y,z): cells around it vary in y and z.
                    if (in0 != (v[Idx(x + 1, y, z)] < 0f))
                        Quad(cellVert[CIdx(x, y - 1, z - 1)], cellVert[CIdx(x, y, z - 1)], cellVert[CIdx(x, y, z)], cellVert[CIdx(x, y - 1, z)], !in0);
                    if (in0 != (v[Idx(x, y + 1, z)] < 0f))
                        Quad(cellVert[CIdx(x - 1, y, z - 1)], cellVert[CIdx(x - 1, y, z)], cellVert[CIdx(x, y, z)], cellVert[CIdx(x, y, z - 1)], !in0);
                    if (in0 != (v[Idx(x, y, z + 1)] < 0f))
                        Quad(cellVert[CIdx(x - 1, y - 1, z)], cellVert[CIdx(x, y - 1, z)], cellVert[CIdx(x, y, z)], cellVert[CIdx(x - 1, y, z)], !in0);
                }

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

    static Vector3 Corner(int c) => new Vector3(c & 1, (c >> 1) & 1, (c >> 2) & 1);
    static readonly int[] EdgeA = { 0, 2, 4, 6, 0, 1, 4, 5, 0, 1, 2, 3 };
    static readonly int[] EdgeB = { 1, 3, 5, 7, 2, 3, 6, 7, 4, 5, 6, 7 };
}
