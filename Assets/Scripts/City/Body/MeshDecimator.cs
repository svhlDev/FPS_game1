using System.Collections.Generic;
using UnityEngine;

// Quadric edge-collapse decimation (Garland & Heckbert). Each vertex carries the sum of the squared
// distances to the planes of its faces (a 4x4 quadric); collapsing an edge costs the quadric error at
// the new position (the better of the two ends and the midpoint). The cheapest edges go first until the
// face budget is met.
//   Per-vertex importance multiplies the quadrics (the head and hands keep detail, limbs and torso
//   give it up first).
//   Collapses that would flip a face or pinch the surface (more than two shared neighbours, the link
//   condition) are skipped, so a closed mesh stays closed and manifold.
public static class MeshDecimator
{
    struct Entry { public double cost; public int u, v, vu, vv; public Vector3 pos; }

    public static void Run(List<Vector3> pos, List<int> tris, float[] importance, int targetTris, out List<Vector3> outPos, out List<int> outTris, out int[] oldIndex)
    {
        int nv = pos.Count, nf = tris.Count / 3;
        var P = pos.ToArray();
        var F = tris.ToArray();
        var Q = new double[nv * 10];
        var vf = new List<int>[nv];
        for (int i = 0; i < nv; i++) vf[i] = new List<int>(8);
        var faceDead = new bool[nf];
        var vDead = new bool[nv];
        var ver = new int[nv];

        for (int f = 0; f < nf; f++)
        {
            int a = F[f * 3], b = F[f * 3 + 1], c = F[f * 3 + 2];
            vf[a].Add(f); vf[b].Add(f); vf[c].Add(f);
            Vector3 n = Vector3.Cross(P[b] - P[a], P[c] - P[a]);
            float area = n.magnitude * 0.5f;
            if (area < 1e-12f) continue;
            n.Normalize();
            double d = -Vector3.Dot(n, P[a]);
            float w = Mathf.Max(importance[a], Mathf.Max(importance[b], importance[c])) * area;
            AddPlane(Q, a, n, d, w); AddPlane(Q, b, n, d, w); AddPlane(Q, c, n, d, w);
        }

        var heap = new List<Entry>(nf * 2);
        var seen = new HashSet<long>();
        for (int f = 0; f < nf; f++)
            for (int e = 0; e < 3; e++)
            {
                int u = F[f * 3 + e], v = F[f * 3 + (e + 1) % 3];
                long key = u < v ? ((long)u << 32) | (uint)v : ((long)v << 32) | (uint)u;
                if (seen.Add(key)) Push(heap, Make(P, Q, ver, u, v));
            }

        int alive = nf;
        var tmp = new HashSet<int>();
        while (alive > targetTris && heap.Count > 0)
        {
            var e = Pop(heap);
            if (vDead[e.u] || vDead[e.v] || ver[e.u] != e.vu || ver[e.v] != e.vv) continue;
            if (!CanCollapse(P, F, vf, faceDead, e.u, e.v, e.pos, tmp)) continue;
            // Collapse v into u at e.pos.
            int u = e.u, v = e.v;
            P[u] = e.pos;
            for (int k = 0; k < 10; k++) Q[u * 10 + k] += Q[v * 10 + k];
            foreach (int f in vf[v])
            {
                if (faceDead[f]) continue;
                int a = F[f * 3], b = F[f * 3 + 1], c = F[f * 3 + 2];
                if (a == u || b == u || c == u)
                {
                    faceDead[f] = true; alive--;
                    continue;
                }
                if (a == v) F[f * 3] = u; else if (b == v) F[f * 3 + 1] = u; else F[f * 3 + 2] = u;
                vf[u].Add(f);
            }
            vDead[v] = true;
            vf[v].Clear();
            vf[u].RemoveAll(f => faceDead[f]);
            ver[u]++;
            // New costs for every edge of u.
            tmp.Clear();
            foreach (int f in vf[u])
                for (int k = 0; k < 3; k++) { int w = F[f * 3 + k]; if (w != u) tmp.Add(w); }
            foreach (int w in tmp) Push(heap, Make(P, Q, ver, u, w));
        }

        // Compact.
        var remap = new int[nv];
        outPos = new List<Vector3>();
        var old = new List<int>();
        for (int i = 0; i < nv; i++)
        {
            remap[i] = -1;
            if (vDead[i] || vf[i].Count == 0) continue;
            remap[i] = outPos.Count;
            outPos.Add(P[i]);
            old.Add(i);
        }
        outTris = new List<int>(alive * 3);
        for (int f = 0; f < nf; f++)
        {
            if (faceDead[f]) continue;
            int a = remap[F[f * 3]], b = remap[F[f * 3 + 1]], c = remap[F[f * 3 + 2]];
            if (a < 0 || b < 0 || c < 0 || a == b || b == c || a == c) continue;
            outTris.Add(a); outTris.Add(b); outTris.Add(c);
        }
        oldIndex = old.ToArray();
    }

    static void AddPlane(double[] Q, int v, Vector3 n, double d, float w)
    {
        int o = v * 10;
        double a = n.x, b = n.y, c = n.z;
        Q[o] += w * a * a; Q[o + 1] += w * a * b; Q[o + 2] += w * a * c; Q[o + 3] += w * a * d;
        Q[o + 4] += w * b * b; Q[o + 5] += w * b * c; Q[o + 6] += w * b * d;
        Q[o + 7] += w * c * c; Q[o + 8] += w * c * d; Q[o + 9] += w * d * d;
    }

    static double Error(double[] Q, int u, int v, Vector3 p)
    {
        int a = u * 10, b = v * 10;
        double x = p.x, y = p.y, z = p.z;
        double q0 = Q[a] + Q[b], q1 = Q[a + 1] + Q[b + 1], q2 = Q[a + 2] + Q[b + 2], q3 = Q[a + 3] + Q[b + 3];
        double q4 = Q[a + 4] + Q[b + 4], q5 = Q[a + 5] + Q[b + 5], q6 = Q[a + 6] + Q[b + 6];
        double q7 = Q[a + 7] + Q[b + 7], q8 = Q[a + 8] + Q[b + 8], q9 = Q[a + 9] + Q[b + 9];
        return q0 * x * x + 2 * q1 * x * y + 2 * q2 * x * z + 2 * q3 * x
             + q4 * y * y + 2 * q5 * y * z + 2 * q6 * y
             + q7 * z * z + 2 * q8 * z + q9;
    }

    static Entry Make(Vector3[] P, double[] Q, int[] ver, int u, int v)
    {
        Vector3 best = P[u]; double c = Error(Q, u, v, P[u]);
        double cv = Error(Q, u, v, P[v]); if (cv < c) { c = cv; best = P[v]; }
        Vector3 mid = (P[u] + P[v]) * 0.5f;
        double cm = Error(Q, u, v, mid); if (cm < c) { c = cm; best = mid; }
        // A small length term breaks ties on flat areas (shortest edges first: even triangles).
        c += 1e-9 * (P[u] - P[v]).sqrMagnitude;
        return new Entry { cost = c, u = u, v = v, vu = ver[u], vv = ver[v], pos = best };
    }

    static bool CanCollapse(Vector3[] P, int[] F, List<int>[] vf, bool[] faceDead, int u, int v, Vector3 pos, HashSet<int> tmp)
    {
        // Link condition: u and v share exactly the two neighbours across their two faces.
        tmp.Clear();
        foreach (int f in vf[u]) { if (faceDead[f]) continue; for (int k = 0; k < 3; k++) { int w = F[f * 3 + k]; if (w != u) tmp.Add(w); } }
        int shared = 0;
        var nv = new HashSet<int>();
        foreach (int f in vf[v]) { if (faceDead[f]) continue; for (int k = 0; k < 3; k++) { int w = F[f * 3 + k]; if (w != v && nv.Add(w) && tmp.Contains(w)) shared++; } }
        if (shared != 2) return false;
        // No face may flip or collapse to a sliver.
        if (Flips(P, F, vf[u], faceDead, u, v, pos) || Flips(P, F, vf[v], faceDead, v, u, pos)) return false;
        return true;
    }

    static bool Flips(Vector3[] P, int[] F, List<int> faces, bool[] faceDead, int moving, int other, Vector3 pos)
    {
        foreach (int f in faces)
        {
            if (faceDead[f]) continue;
            int a = F[f * 3], b = F[f * 3 + 1], c = F[f * 3 + 2];
            if (a == other || b == other || c == other) continue; // dies in the collapse
            Vector3 pa = P[a], pb = P[b], pc = P[c];
            Vector3 n0 = Vector3.Cross(pb - pa, pc - pa);
            if (a == moving) pa = pos; else if (b == moving) pb = pos; else pc = pos;
            Vector3 n1 = Vector3.Cross(pb - pa, pc - pa);
            float m0 = n0.magnitude, m1 = n1.magnitude;
            if (m1 < 1e-10f) return true;
            if (m0 > 1e-10f && Vector3.Dot(n0, n1) / (m0 * m1) < 0.25f) return true;
            // Sliver: the new face's smallest angle shouldn't get tiny.
            float e1 = (pb - pa).sqrMagnitude, e2 = (pc - pb).sqrMagnitude, e3 = (pa - pc).sqrMagnitude;
            if (m1 * m1 < 0.02f * Mathf.Max(e1, Mathf.Max(e2, e3)) * Mathf.Max(e1, Mathf.Max(e2, e3)) * 0.25f) return true;
        }
        return false;
    }

    // ---------- binary min-heap ----------

    static void Push(List<Entry> h, Entry e)
    {
        h.Add(e);
        int i = h.Count - 1;
        while (i > 0)
        {
            int p = (i - 1) / 2;
            if (h[p].cost <= h[i].cost) break;
            (h[p], h[i]) = (h[i], h[p]);
            i = p;
        }
    }

    static Entry Pop(List<Entry> h)
    {
        var top = h[0];
        int last = h.Count - 1;
        h[0] = h[last];
        h.RemoveAt(last);
        int i = 0, n = h.Count;
        while (true)
        {
            int l = i * 2 + 1, r = l + 1, m = i;
            if (l < n && h[l].cost < h[m].cost) m = l;
            if (r < n && h[r].cost < h[m].cost) m = r;
            if (m == i) break;
            (h[m], h[i]) = (h[i], h[m]);
            i = m;
        }
        return top;
    }
}
