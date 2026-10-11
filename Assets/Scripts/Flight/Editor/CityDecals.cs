using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// Graphics Phase 5: decals scattered by rule (FPS/Decal, procedural, no textures). Each decal is a quad
// laid 2 cm off a wall or road; all quads of one type are merged into one static mesh (one draw per type
// and chunk). Rules:
//   roads    oil stains and skid marks, three times as dense within 25 m of an intersection
//   walls    (street level of every tower mass that reaches the ground, outward faces only, never inside
//            another building) grime at the foot, leaks running down from higher up, posters and
//            graffiti at eye height, both much likelier near intersections and in the market
public static partial class CityDressing
{
    public enum DecalType { Grime, Leak, Poster, Graffiti, Oil, Skid }

    class DecalMesh
    {
        public readonly List<Vector3> v = new List<Vector3>();
        public readonly List<Vector3> n = new List<Vector3>();
        public readonly List<Vector2> uv = new List<Vector2>();
        public readonly List<Color> c = new List<Color>();
        public readonly List<int> t = new List<int>();
    }

    public static int ScatterDecals(Transform parent, List<StreetDef> streets, List<Intersection> crossings, List<Tower> towers,
                                    List<Rect> marketBlocks, int seed)
    {
        var rng = new System.Random(seed);
        var meshes = new Dictionary<DecalType, DecalMesh>();
        float R() => (float)rng.NextDouble();
        float NearCrossing(Vector3 p)
        {
            float best = float.MaxValue;
            foreach (var x in crossings) best = Mathf.Min(best, Vector2.Distance(new Vector2(p.x, p.z), new Vector2(x.center.x, x.center.z)));
            return best;
        }
        bool InMarket(Vector3 p) { foreach (var b in marketBlocks) if (b.Contains(new Vector2(p.x, p.z))) return true; return false; }
        void Quad(DecalType type, Vector3 centre, Vector3 right, Vector3 up, Vector3 normal, float w, float h)
        {
            if (!meshes.TryGetValue(type, out var m)) meshes[type] = m = new DecalMesh();
            int b = m.v.Count;
            Vector3 hr = right * (w * 0.5f), hu = up * (h * 0.5f), off = normal * 0.02f;
            m.v.Add(centre - hr - hu + off); m.v.Add(centre + hr - hu + off); m.v.Add(centre + hr + hu + off); m.v.Add(centre - hr + hu + off);
            m.uv.Add(new Vector2(0, 0)); m.uv.Add(new Vector2(1, 0)); m.uv.Add(new Vector2(1, 1)); m.uv.Add(new Vector2(0, 1));
            var col = new Color(R(), R(), 0f, 1f);
            for (int k = 0; k < 4; k++) { m.n.Add(normal); m.c.Add(col); }
            // Winding so the quad faces along `normal`.
            if (Vector3.Dot(Vector3.Cross(right, up), normal) < 0f) { m.t.Add(b); m.t.Add(b + 1); m.t.Add(b + 2); m.t.Add(b); m.t.Add(b + 2); m.t.Add(b + 3); }
            else { m.t.Add(b); m.t.Add(b + 2); m.t.Add(b + 1); m.t.Add(b); m.t.Add(b + 3); m.t.Add(b + 2); }
        }

        // Roads.
        foreach (var st in streets)
        {
            float len = st.max - st.min;
            int samples = Mathf.CeilToInt(len / 12f);
            for (int i = 0; i < samples; i++)
            {
                float a = Mathf.Lerp(st.min, st.max, R());
                float across = (R() - 0.5f) * 2f * (st.RoadHalf - 1f);
                Vector3 p = st.northSouth ? new Vector3(st.center + across, 0.012f, a) : new Vector3(a, 0.012f, st.center + across);
                float near = NearCrossing(p);
                float chance = near < 25f ? 0.9f : 0.3f;
                if (R() > chance) continue;
                Vector3 along = st.northSouth ? Vector3.forward : Vector3.right;
                Vector3 side = Vector3.Cross(Vector3.up, along);
                if (R() < 0.55f) { float s = Mathf.Lerp(0.8f, 2.2f, R()); Quad(DecalType.Oil, p, side, along, Vector3.up, s, s * Mathf.Lerp(0.7f, 1.3f, R())); }
                else Quad(DecalType.Skid, p, side, along, Vector3.up, 1.7f, Mathf.Lerp(3f, 9f, R()));
            }
        }

        // Walls at street level.
        Physics.SyncTransforms();
        foreach (var t in towers)
            foreach (var b in t.masses)
            {
                if (b.min.y > 0.5f) continue;
                for (int f = 0; f < 4; f++)
                {
                    Vector3 nrm = f == 0 ? Vector3.right : f == 1 ? Vector3.left : f == 2 ? Vector3.forward : Vector3.back;
                    Vector3 along = Vector3.Cross(Vector3.up, nrm);
                    float faceLen = Mathf.Abs(Vector3.Dot(b.size, along));
                    Vector3 faceCentre = b.center + Vector3.Scale(nrm, b.extents);
                    for (float s = -faceLen * 0.5f + 3f; s < faceLen * 0.5f - 3f; s += Mathf.Lerp(5f, 10f, R()))
                    {
                        Vector3 foot = new Vector3(faceCentre.x, 0f, faceCentre.z) + along * s;
                        // Outward and open: not against or inside another building.
                        if (Physics.CheckSphere(foot + nrm * 0.8f + Vector3.up * 1.5f, 0.5f, ~0, QueryTriggerInteraction.Ignore)) continue;
                        float near = NearCrossing(foot);
                        bool busy = near < 30f || InMarket(foot);
                        float roll = R();
                        if (roll < 0.35f)
                        {
                            float w = Mathf.Lerp(2f, 4.5f, R());
                            Quad(DecalType.Grime, foot + Vector3.up * (KerbHeight + 0.7f), along, Vector3.up, nrm, w, 1.4f);
                        }
                        else if (roll < 0.55f)
                        {
                            float h = Mathf.Lerp(3f, 8f, R());
                            Quad(DecalType.Leak, foot + Vector3.up * Mathf.Lerp(6f, 14f, R()), along, Vector3.up, nrm, Mathf.Lerp(0.6f, 1.4f, R()), h);
                        }
                        else if (roll < (busy ? 0.85f : 0.65f))
                        {
                            Quad(DecalType.Poster, foot + Vector3.up * Mathf.Lerp(1.4f, 2.2f, R()), along, Vector3.up, nrm, 0.7f, 1f);
                            if (busy && R() < 0.5f)   // posters come in runs
                                Quad(DecalType.Poster, foot + along * 0.8f + Vector3.up * Mathf.Lerp(1.4f, 2.2f, R()), along, Vector3.up, nrm, 0.7f, 1f);
                        }
                        else if (roll < (busy ? 1f : 0.75f))
                        {
                            float w = Mathf.Lerp(1.5f, 4f, R());
                            Quad(DecalType.Graffiti, foot + Vector3.up * (KerbHeight + Mathf.Lerp(0.6f, 1.6f, R())), along, Vector3.up, nrm, w, w * 0.45f);
                        }
                    }
                }
            }

        var root = new GameObject("Decals").transform;
        root.SetParent(parent, false);
        int quads = 0;
        foreach (var kv in meshes)
        {
            var dm = kv.Value;
            var mesh = new Mesh { name = "Decals_" + kv.Key, indexFormat = dm.v.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            mesh.SetVertices(dm.v); mesh.SetNormals(dm.n); mesh.SetUVs(0, dm.uv); mesh.SetColors(dm.c); mesh.SetTriangles(dm.t, 0);
            mesh.RecalculateBounds();
            var go = new GameObject("Decals_" + kv.Key);
            go.transform.SetParent(root, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = DecalMaterial(kv.Key);
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            quads += dm.v.Count / 4;
        }
        return quads;
    }

    static Material DecalMaterial(DecalType t) => ShaderMaterial("Decal_" + t, "FPS/Decal", m =>
    {
        m.SetFloat("_Type", (int)t);
        m.SetFloat("_Opacity", 1f);
        m.enableInstancing = true;
    });
}
