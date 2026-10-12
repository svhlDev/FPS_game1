using System.Collections.Generic;
using UnityEngine;

// Graphics Phase 3: per-vertex surface data for generated bodies and garments, read by FPS/BodySkin and
// FPS/Surface (Skinned variant). Pure C#, safe on worker threads; runs after a body is built (BodyBuild's
// Split stage) and after a preset body is decoded (BodyPreset), so the preset file needs no new data.
//   uv3     rest-pose position (the mesh's own vertices): patterns stick to the skin and cloth instead
//           of swimming when the skeleton moves
//   colour  r  convex edges (mesh curvature): fabric wear, knuckles
//           g  cavities and fold valleys (concave curvature): grime, darker folds, skin creases
//           b  contact shadow: garment near its hems, skin just outside each cuff / hem / collar
//           a  skin redness: knuckles, nose, ears, lips (convex head / hand parts) and creases
//   uv4     (head skin) x: scalp stubble (short hair coloured on the skin the wig leaves bare: nape,
//           sides; none on bald heads), y: lips (tinted), z: the inside of the mouth (BodyBuild)
//   uv0     x  distance (m) to the garment's nearest hem / cuff / opening (stitching runs ~1 cm in)
//           y  side-seam coordinate: the rest normal's z where the normal points sideways (seam where
//              it crosses 0), else 1
public static class BodySurfaceBake
{
    const float HemReach = 0.06f;   // hem queries look this far (one grid cell: 27 cells per query)

    public struct Result { public Color[] colors; public Vector2[] uv0; public Vector3[] uv3, uv4; public List<Vector3> hems; }

    // The face parts that mark the skin: kind 1 brow, 2 lip, 3 mouth line.
    public class FaceMarks
    {
        public readonly List<BodySdfKernel.FacePrim> prims = new List<BodySdfKernel.FacePrim>();
        public readonly List<byte> kinds = new List<byte>();
        public BodySdfKernel.Ellipsoid skull;
        public bool stubble;            // the scalp gets short hair (not on bald heads)
        public static FaceMarks From(BodySDF sdf, WigStyle wig)
        {
            var m = new FaceMarks { skull = sdf.Kernel.skull, stubble = wig != WigStyle.None };
            for (int i = 0; i < sdf.FacePrims.Count; i++)
            {
                string n = sdf.FacePartNames[i];
                byte k = n.StartsWith("Brow.") ? (byte)1 : n.StartsWith("Lip") ? (byte)2 : n == "MouthLine" ? (byte)3 : (byte)0;
                if (k == 0) continue;
                m.prims.Add(sdf.FacePrims[i]); m.kinds.Add(k);
            }
            return m;
        }
    }

    // subs: submesh triangle lists (submesh 0 = skin, then garments; the head mesh has only skin).
    // kinds: bone kind per bone index (bones past it count as cloth chains). hems: garment openings from
    // the body mesh (the head mesh is baked after the body, with the body's hems, for the collar shadow).
    public static Result Bake(Vector3[] v, Vector3[] n, BoneWeight[] w, IList<int[]> subs, BoneKind[] kinds, List<Vector3> hems = null, FaceMarks face = null)
    {
        int nv = v.Length;
        var res = new Result { colors = new Color[nv], uv0 = new Vector2[nv], uv3 = (Vector3[])v.Clone(), hems = new List<Vector3>() };
        if (nv == 0) return res;

        // Which submesh each vertex belongs to (vertices aren't shared between submeshes).
        var sub = new int[nv];
        for (int s = 0; s < subs.Count; s++) foreach (int i in subs[s]) sub[i] = s;

        // Neighbours and open edges (edges used by one triangle: hems, cuffs, collars, openings).
        var adj = new List<int>[nv];
        for (int i = 0; i < nv; i++) adj[i] = new List<int>(6);
        var edgeUse = new Dictionary<long, int>();
        void Edge(int a, int b)
        {
            if (!adj[a].Contains(b)) adj[a].Add(b);
            if (!adj[b].Contains(a)) adj[b].Add(a);
            long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
            edgeUse.TryGetValue(key, out int c); edgeUse[key] = c + 1;
        }
        foreach (var t in subs)
            for (int k = 0; k + 2 < t.Length; k += 3) { Edge(t[k], t[k + 1]); Edge(t[k + 1], t[k + 2]); Edge(t[k + 2], t[k]); }
        var open = new bool[nv];
        foreach (var kv in edgeUse)
            if (kv.Value == 1) { open[(int)(kv.Key >> 32)] = true; open[(int)(kv.Key & 0xffffffff)] = true; }

        // Curvature (1/m): + convex, - concave; one smoothing pass.
        var k0 = new float[nv];
        for (int i = 0; i < nv; i++)
        {
            float sum = 0f; int c = 0;
            foreach (int j in adj[i])
            {
                Vector3 d = v[i] - v[j];
                float l2 = d.sqrMagnitude;
                if (l2 < 1e-10f) continue;
                sum += Vector3.Dot(n[i] - n[j], d) / l2; c++;
            }
            k0[i] = c > 0 ? sum / c : 0f;
        }
        var curv = new float[nv];
        for (int i = 0; i < nv; i++)
        {
            float s = k0[i]; int c = 1;
            foreach (int j in adj[i]) { s += k0[j]; c++; }
            curv[i] = s / c;
        }

        // Garment openings of this mesh (open edges on garment submeshes), plus the body's for the head.
        var hemPts = new List<Vector3>();
        for (int i = 0; i < nv; i++) if (sub[i] > 0 && open[i]) hemPts.Add(v[i]);
        res.hems.AddRange(hemPts);
        if (hems != null) hemPts.AddRange(hems);
        var grid = new HemGrid(hemPts, HemReach);

        for (int i = 0; i < nv; i++)
        {
            bool garment = sub[i] > 0;
            float k = curv[i];
            float convex = Mathf.Clamp01((k - 20f) / 60f);
            float cavity = Mathf.Clamp01((-k - 5f) / 40f);
            float hemDist = grid.Nearest(v[i], HemReach);
            float ao;
            if (garment) ao = 0.5f * Mathf.Clamp01(1f - hemDist / 0.03f);               // under the hem's lip
            else ao = 0.8f * Mathf.Clamp01(1f - hemDist / 0.045f);                       // skin just outside a cuff / collar
            float red = 0f;
            if (!garment)
            {
                var kind = Kind(w[i], kinds);
                if (kind == BoneKind.Head) red = Mathf.Clamp01((k - 25f) / 60f) * Mathf.Clamp01(n[i].z * 3f);   // nose, lips (the face, not the nape)
                else if (kind == BoneKind.Finger || kind == BoneKind.Hand) red = Mathf.Clamp01((k - 40f) / 80f); // knuckles
                red = Mathf.Max(red, cavity * 0.5f);
            }
            res.colors[i] = new Color(convex, cavity, ao, red);
            float seam = 1f;
            if (garment && Mathf.Abs(n[i].x) > 0.5f) seam = n[i].z;
            res.uv0[i] = new Vector2(garment ? Mathf.Min(hemDist, 1f) : 1f, seam);
        }
        if (face != null && face.prims.Count > 0)
        {
            res.uv4 = new Vector3[nv];
            for (int i = 0; i < nv; i++)
            {
                if (sub[i] != 0) continue;
                float lip = 9f;
                for (int j = 0; j < face.prims.Count; j++)
                    if (face.kinds[j] == 2) lip = Mathf.Min(lip, Mathf.Max(0f, BodySdfKernel.FaceDist(face.prims[j], v[i])));
                // Scalp: outside the face window (the same ellipse as the wigs' hairline), down to a nape line.
                float stub = 0f;
                if (face.stubble)
                {
                    var sk = face.skull;
                    Vector3 l = sk.ToLocal(v[i]);
                    Vector3 u = new Vector3(l.x / sk.r.x, l.y / sk.r.y, l.z / sk.r.z);
                    float window = Mathf.Min(u.z - 0.05f, 1f - new Vector2(Mathf.Abs(u.x) / 0.86f, (u.y + 0.2f) / 0.78f).magnitude);
                    stub = Mathf.Clamp01(-window / 0.06f) * Mathf.Clamp01((u.y + 0.62f) / 0.1f);
                }
                // The lips lie under a little fat: the skin over them is a few mm out.
                res.uv4[i] = new Vector3(stub, 1f - Mathf.SmoothStep(0f, 1f, (lip - 0.003f) / 0.006f), 0f);
            }
        }
        return res;
    }

    static BoneKind Kind(BoneWeight w, BoneKind[] kinds)
    {
        int b = w.boneIndex0;
        return kinds != null && b >= 0 && b < kinds.Length ? kinds[b] : BoneKind.Chest;
    }

    public static BoneKind[] Kinds(BodyPlan plan)
    {
        var k = new BoneKind[plan.bones.Count];
        for (int i = 0; i < k.Length; i++) k[i] = plan.bones[i].kind;
        return k;
    }

    // Applies a bake to a mesh (main thread).
    public static void Apply(Mesh m, Result r)
    {
        if (r.colors == null) return;
        m.colors = r.colors;
        m.SetUVs(0, r.uv0);
        m.SetUVs(3, r.uv3);
        if (r.uv4 != null) m.SetUVs(4, r.uv4);
    }

    // Uniform hash grid of hem points for nearest-distance queries.
    class HemGrid
    {
        readonly float cell;
        readonly Dictionary<Vector3Int, List<Vector3>> cells = new Dictionary<Vector3Int, List<Vector3>>();
        public HemGrid(List<Vector3> pts, float cell)
        {
            this.cell = cell;
            foreach (var p in pts)
            {
                var key = Key(p);
                if (!cells.TryGetValue(key, out var l)) cells[key] = l = new List<Vector3>();
                l.Add(p);
            }
        }
        Vector3Int Key(Vector3 p) => new Vector3Int(Mathf.FloorToInt(p.x / cell), Mathf.FloorToInt(p.y / cell), Mathf.FloorToInt(p.z / cell));
        // Distance to the nearest point, or maxDist if none is closer.
        public float Nearest(Vector3 p, float maxDist)
        {
            if (cells.Count == 0) return maxDist;
            int r = Mathf.CeilToInt(maxDist / cell);
            var c = Key(p);
            float best = maxDist * maxDist;
            for (int x = -r; x <= r; x++)
            for (int y = -r; y <= r; y++)
            for (int z = -r; z <= r; z++)
            {
                if (!cells.TryGetValue(new Vector3Int(c.x + x, c.y + y, c.z + z), out var l)) continue;
                foreach (var q in l) { float d = (q - p).sqrMagnitude; if (d < best) best = d; }
            }
            return Mathf.Sqrt(best);
        }
    }
}
