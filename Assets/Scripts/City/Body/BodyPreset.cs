using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using UnityEngine;
using UnityEngine.Rendering;

// Preset body pool: generated bodies baked once in the editor (Tools > Characters > Bake Body Pool) into
// Resources/BodyPoolPreset.bytes, so the game starts with a full crowd instead of generating ~150 bodies
// in the background. BodyPool loads it at startup (decoded on a worker, meshes uploaded a few per frame)
// and still generates any slot the preset doesn't hold (hybrid).
// Format: gzip of a small header (version, rules hash, count), then per body: slot (index, officer),
// JSON for the sheet / plan / outfit / spring chains, scalars, and the body and head meshes, quantized:
// positions 16-bit in the mesh's bounds (~0.03 mm), normals and bone indices / weights as bytes, the
// region channel as bytes, the layer channel as half floats (~23 bytes a vertex instead of 80).
// A version mismatch skips the preset (the layout changed); a rules mismatch still loads it, with a
// warning to re-bake (the bodies are valid, just not what the current rules would make).
public static class BodyPreset
{
    public const int Version = 7;   // 2: quantized vertices; 3: face masks (uv4), the jaw, the wig; 4: hair brows, scalp stubble; 5: the eye shape; 6: brow strands; 7: hair cards
    public const string ResourceName = "BodyPoolPreset";

    [Serializable] class ChainList { public List<ClothChainSet> chains = new List<ClothChainSet>(); }

    // CPU-side mesh (decoded on a worker; turned into a Mesh on the main thread).
    public class MeshData
    {
        public string name;
        public Vector3[] v, n; public Vector2[] uv1, uv2; public Vector3[] uv4; public Vector2[] zones; public BoneWeight[] w; public Matrix4x4[] bind;
        public int[][] subs;
        public BodySurfaceBake.Result bake;   // computed after decoding (not stored in the file)
        public Mesh ToMesh()
        {
            var m = new Mesh { name = name, indexFormat = v.Length > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            m.vertices = v; m.normals = n; m.SetUVs(1, uv1); m.SetUVs(2, uv2);
            m.subMeshCount = subs.Length;
            for (int i = 0; i < subs.Length; i++) m.SetTriangles(subs[i], i, false);
            m.boneWeights = w; m.bindposes = bind;
            // (The face marks and colour zones come from the file: the decode's bake has no face to take them from.)
            var bk = bake; bk.uv4 = uv4; bk.zones = zones;
            BodySurfaceBake.Apply(m, bk);
            if (uv4 != null) m.SetUVs(4, uv4);
            m.RecalculateBounds();
            return m;
        }
    }

    public class Entry
    {
        public int index; public bool officer;
        public CharacterSheet sheet; public BodyPlan plan; public Outfit outfit; public List<ClothChainSet> chains;
        public Vector3 eyeL, eyeR; public float eyeRadius, height, width;
        public int triangles, skinTriangles, skinBefore; public int[] garmentTriangles;
        public MeshData body, head;
        public WigStyle wigStyle; public Vector3 jawPivot, mouthCentre; public int jawBone; public EyeShape eye;
        public Vector3[] wigV, wigN; public int[] wigT; public Vector2[] wigUV; public int wigBrowTris;
        public Vector3[] cardV, cardN; public Vector4[] cardT; public Vector2[] cardUV, cardUV2; public BoneWeight[] cardW; public int[] cardTri;

        public BodyAsset ToAsset()
        {
            var a = new BodyAsset
            {
                sheet = sheet, plan = plan, outfit = outfit, body = body.ToMesh(), head = head.ToMesh(),
                eyeL = eyeL, eyeR = eyeR, eyeRadius = eyeRadius, height = height, width = width,
                triangles = triangles, skinTriangles = skinTriangles, skinTrianglesBeforeRemoval = skinBefore, garmentTriangles = garmentTriangles,
            };
            if (chains != null) a.chains.AddRange(chains);
            a.wigStyle = wigStyle; a.jawPivot = jawPivot; a.mouthCentre = mouthCentre; a.jawBone = jawBone; a.eye = eye;
            if (wigV != null && wigV.Length > 0)
            {
                a.wig = BodyBuild.WigMesh("Wig", wigV, wigN, wigT, wigUV, wigBrowTris);
            }
            if (cardV != null && cardV.Length > 0)
            {
                a.hairCards = BodyBuild.CardMesh("Hair", cardV, cardN, cardT, cardUV, cardUV2, cardW, cardTri, a.body.bindposes);
            }
            return a;
        }
    }

    // Changes whenever the body / clothing rules change (the bake goes stale).
    public static int RulesHash()
    {
        string s = JsonUtility.ToJson(BodyRules.Default);
        unchecked { int h = 17; foreach (char c in s) h = h * 31 + c; return h; }
    }

    // ---------- write (editor bake) ----------

    public static byte[] Encode(List<(int index, bool officer, BodyAsset asset)> bodies)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, System.IO.Compression.CompressionLevel.Optimal, true))
        using (var w = new BinaryWriter(gz))
        {
            w.Write(Version); w.Write(RulesHash()); w.Write(bodies.Count);
            foreach (var (index, officer, a) in bodies)
            {
                w.Write(index); w.Write(officer);
                w.Write(JsonUtility.ToJson(a.sheet)); w.Write(JsonUtility.ToJson(a.plan)); w.Write(JsonUtility.ToJson(a.outfit ?? Outfit.None()));
                w.Write(JsonUtility.ToJson(new ChainList { chains = a.chains }));
                W(w, a.eyeL); W(w, a.eyeR); w.Write(a.eyeRadius); w.Write(a.height); w.Write(a.width);
                w.Write(a.triangles); w.Write(a.skinTriangles); w.Write(a.skinTrianglesBeforeRemoval);
                var gt = a.garmentTriangles ?? new int[0];
                w.Write(gt.Length); foreach (int t in gt) w.Write(t);
                w.Write((int)a.wigStyle); W(w, a.jawPivot); W(w, a.mouthCentre); w.Write(a.jawBone);
                var es = a.eye;
                foreach (float f in new[] { es.upper, es.lower, es.inner, es.outer, es.tilt, es.thickU, es.thickL, es.crease, es.creaseDepth }) w.Write(f);
                WriteMesh(w, a.body); WriteMesh(w, a.head);
                var wv = a.wig != null ? a.wig.vertices : new Vector3[0];
                var wn = a.wig != null ? a.wig.normals : new Vector3[0];
                var wt = a.wig != null ? a.wig.triangles : new int[0];
                w.Write(wv.Length); for (int i = 0; i < wv.Length; i++) { W(w, wv[i]); W(w, wn[i]); }
                // (In submesh order: the hair, then the brows.)
                var wh = a.wig != null ? a.wig.GetTriangles(0) : new int[0]; var wb = a.wig != null && a.wig.subMeshCount > 1 ? a.wig.GetTriangles(1) : new int[0];
                w.Write(wh.Length + wb.Length); foreach (int i in wh) w.Write((ushort)i); foreach (int i in wb) w.Write((ushort)i);
                w.Write(wb.Length / 3);
                var wu = new List<Vector2>(); if (a.wig != null) a.wig.GetUVs(0, wu);
                w.Write(wu.Count); foreach (var u in wu) { w.Write(u.x); w.Write(u.y); }
                // Hair cards (Face Fix Part 9).
                var hm = a.hairCards;
                int hn = hm != null ? hm.vertexCount : 0;
                w.Write(hn);
                if (hn > 0)
                {
                    var hv = hm.vertices; var hnn = hm.normals; var ht = hm.tangents; var hu = new List<Vector2>(); hm.GetUVs(0, hu); var hu2 = new List<Vector2>(); hm.GetUVs(1, hu2);
                    var hw = hm.boneWeights;
                    for (int i = 0; i < hn; i++)
                    {
                        W(w, hv[i]); W(w, hnn[i]); W(w, (Vector3)ht[i]); w.Write(hu[i].x); w.Write(hu[i].y); w.Write(hu2[i].x); w.Write(hu2[i].y);
                        var b = hw[i];
                        w.Write((byte)b.boneIndex0); w.Write((byte)b.boneIndex1); w.Write((byte)b.boneIndex2); w.Write((byte)b.boneIndex3);
                        w.Write(b.weight0); w.Write(b.weight1); w.Write(b.weight2); w.Write(b.weight3);
                    }
                    var htr = hm.triangles; w.Write(htr.Length); foreach (int i in htr) w.Write((ushort)i);
                }
            }
        }
        return ms.ToArray();
    }

    static void W(BinaryWriter w, Vector3 v) { w.Write(v.x); w.Write(v.y); w.Write(v.z); }
    static Vector3 V3(BinaryReader r) => new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

    static void WriteMesh(BinaryWriter w, Mesh m)
    {
        w.Write(m.name);
        var v = m.vertices; var n = m.normals;
        var uv1 = new List<Vector2>(); m.GetUVs(1, uv1); var uv2 = new List<Vector2>(); m.GetUVs(2, uv2);
        var uv4 = new List<Vector3>(); m.GetUVs(4, uv4);
        var uv3 = new List<Vector4>(); m.GetUVs(3, uv3);
        var bw = m.boneWeights; var bind = m.bindposes;
        w.Write(v.Length);
        var bmin = Vector3.one * float.MaxValue; var bmax = -bmin;
        foreach (var p in v) { bmin = Vector3.Min(bmin, p); bmax = Vector3.Max(bmax, p); }
        Vector3 ext = Vector3.Max(bmax - bmin, Vector3.one * 1e-4f);
        W(w, bmin); W(w, ext);
        byte Q(float x) => (byte)Mathf.Clamp(Mathf.RoundToInt(x * 255f), 0, 255);
        byte S(float x) => (byte)Mathf.Clamp(Mathf.RoundToInt(x * 127f) + 128, 0, 255);
        for (int i = 0; i < v.Length; i++)
        {
            Vector3 q = v[i] - bmin;
            w.Write((ushort)Mathf.RoundToInt(q.x / ext.x * 65535f)); w.Write((ushort)Mathf.RoundToInt(q.y / ext.y * 65535f)); w.Write((ushort)Mathf.RoundToInt(q.z / ext.z * 65535f));
            w.Write(S(n[i].x)); w.Write(S(n[i].y)); w.Write(S(n[i].z));
            w.Write((byte)Mathf.RoundToInt(uv1[i].x)); w.Write((byte)Mathf.RoundToInt(uv1[i].y));
            w.Write(Mathf.FloatToHalf(uv2[i].x)); w.Write(Mathf.FloatToHalf(uv2[i].y));
            Vector3 mk = i < uv4.Count ? uv4[i] : Vector3.zero;
            w.Write(Q(mk.x)); w.Write(Q(mk.y)); w.Write(Q(mk.z));
            // The colour zones, unpacked from uv3.w (BodySurfaceBake.PackMarks).
            float pw = i < uv3.Count ? uv3[i].w : 0f;
            w.Write(Q(Mathf.Floor(pw / 8192f) % 32f / 31f)); w.Write(Q(Mathf.Floor(pw / 262144f) / 31f));
            var b = bw[i];
            w.Write((byte)b.boneIndex0); w.Write((byte)b.boneIndex1); w.Write((byte)b.boneIndex2); w.Write((byte)b.boneIndex3);
            w.Write(Q(b.weight0)); w.Write(Q(b.weight1)); w.Write(Q(b.weight2)); w.Write(Q(b.weight3));
        }
        w.Write(bind.Length);
        foreach (var mat in bind) for (int k = 0; k < 16; k++) w.Write(mat[k]);
        w.Write(m.subMeshCount);
        for (int s = 0; s < m.subMeshCount; s++)
        {
            var t = m.GetTriangles(s);
            w.Write(t.Length);
            foreach (int i in t) { if (v.Length > 65000) w.Write(i); else w.Write((ushort)i); }
        }
    }

    // ---------- read (runtime; safe on a worker thread) ----------

    public static List<Entry> Decode(byte[] data, out string problem)
    {
        problem = null;
        var list = new List<Entry>();
        using var gz = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
        using var r = new BinaryReader(gz);
        int version = r.ReadInt32(), rules = r.ReadInt32(), count = r.ReadInt32();
        if (version != Version) { problem = $"preset version {version}, expected {Version}: re-bake (Tools > Characters > Bake Body Pool)"; return list; }
        if (rules != rulesHashForDecode) problem = "the body rules changed since the preset was baked: re-bake (Tools > Characters > Bake Body Pool)";
        for (int b = 0; b < count; b++)
        {
            var e = new Entry { index = r.ReadInt32(), officer = r.ReadBoolean() };
            e.sheet = JsonUtility.FromJson<CharacterSheet>(r.ReadString());
            e.plan = JsonUtility.FromJson<BodyPlan>(r.ReadString());
            e.outfit = JsonUtility.FromJson<Outfit>(r.ReadString());
            e.chains = JsonUtility.FromJson<ChainList>(r.ReadString()).chains;
            e.eyeL = V3(r); e.eyeR = V3(r); e.eyeRadius = r.ReadSingle(); e.height = r.ReadSingle(); e.width = r.ReadSingle();
            e.triangles = r.ReadInt32(); e.skinTriangles = r.ReadInt32(); e.skinBefore = r.ReadInt32();
            e.garmentTriangles = new int[r.ReadInt32()];
            for (int i = 0; i < e.garmentTriangles.Length; i++) e.garmentTriangles[i] = r.ReadInt32();
            e.wigStyle = (WigStyle)r.ReadInt32(); e.jawPivot = V3(r); e.mouthCentre = V3(r); e.jawBone = r.ReadInt32();
            e.eye = new EyeShape { upper = r.ReadSingle(), lower = r.ReadSingle(), inner = r.ReadSingle(), outer = r.ReadSingle(), tilt = r.ReadSingle(),
                                   thickU = r.ReadSingle(), thickL = r.ReadSingle(), crease = r.ReadSingle(), creaseDepth = r.ReadSingle() };
            e.body = ReadMesh(r); e.head = ReadMesh(r);
            int wn = r.ReadInt32();
            e.wigV = new Vector3[wn]; e.wigN = new Vector3[wn];
            for (int i = 0; i < wn; i++) { e.wigV[i] = V3(r); e.wigN[i] = V3(r); }
            e.wigT = new int[r.ReadInt32()];
            for (int i = 0; i < e.wigT.Length; i++) e.wigT[i] = r.ReadUInt16();
            e.wigBrowTris = r.ReadInt32();
            e.wigUV = new Vector2[r.ReadInt32()];
            for (int q = 0; q < e.wigUV.Length; q++) e.wigUV[q] = new Vector2(r.ReadSingle(), r.ReadSingle());
            int cn = r.ReadInt32();
            if (cn > 0)
            {
                e.cardV = new Vector3[cn]; e.cardN = new Vector3[cn]; e.cardT = new Vector4[cn]; e.cardUV = new Vector2[cn]; e.cardUV2 = new Vector2[cn]; e.cardW = new BoneWeight[cn];
                for (int q = 0; q < cn; q++)
                {
                    e.cardV[q] = V3(r); e.cardN[q] = V3(r); var tt = V3(r); e.cardT[q] = new Vector4(tt.x, tt.y, tt.z, 1f);
                    e.cardUV[q] = new Vector2(r.ReadSingle(), r.ReadSingle()); e.cardUV2[q] = new Vector2(r.ReadSingle(), r.ReadSingle());
                    e.cardW[q] = new BoneWeight { boneIndex0 = r.ReadByte(), boneIndex1 = r.ReadByte(), boneIndex2 = r.ReadByte(), boneIndex3 = r.ReadByte(),
                                                  weight0 = r.ReadSingle(), weight1 = r.ReadSingle(), weight2 = r.ReadSingle(), weight3 = r.ReadSingle() };
                }
                e.cardTri = new int[r.ReadInt32()];
                for (int q = 0; q < e.cardTri.Length; q++) e.cardTri[q] = r.ReadUInt16();
            }
            var kinds = BodySurfaceBake.Kinds(e.plan);
            e.body.bake = BodySurfaceBake.Bake(e.body.v, e.body.n, e.body.w, e.body.subs, kinds);
            e.head.bake = BodySurfaceBake.Bake(e.head.v, e.head.n, e.head.w, e.head.subs, kinds, e.body.bake.hems);
            list.Add(e);
        }
        return list;
    }

    // (The rules hash needs the BodyRules asset, which only loads on the main thread: read it there first.)
    static int rulesHashForDecode;
    public static void PrepareDecode() => rulesHashForDecode = RulesHash();

    static MeshData ReadMesh(BinaryReader r)
    {
        var m = new MeshData { name = r.ReadString() };
        int nv = r.ReadInt32();
        m.v = new Vector3[nv]; m.n = new Vector3[nv]; m.uv1 = new Vector2[nv]; m.uv2 = new Vector2[nv]; m.uv4 = new Vector3[nv]; m.zones = new Vector2[nv]; m.w = new BoneWeight[nv];
        Vector3 bmin = V3(r), ext = V3(r);
        float Sn(byte b) => (b - 128) / 127f;
        for (int i = 0; i < nv; i++)
        {
            m.v[i] = bmin + new Vector3(r.ReadUInt16() / 65535f * ext.x, r.ReadUInt16() / 65535f * ext.y, r.ReadUInt16() / 65535f * ext.z);
            m.n[i] = new Vector3(Sn(r.ReadByte()), Sn(r.ReadByte()), Sn(r.ReadByte())).normalized;
            m.uv1[i] = new Vector2(r.ReadByte(), r.ReadByte());
            m.uv2[i] = new Vector2(Mathf.HalfToFloat(r.ReadUInt16()), Mathf.HalfToFloat(r.ReadUInt16()));
            m.uv4[i] = new Vector3(r.ReadByte() / 255f, r.ReadByte() / 255f, r.ReadByte() / 255f);
            m.zones[i] = new Vector2(r.ReadByte() / 255f, r.ReadByte() / 255f);
            int b0 = r.ReadByte(), b1 = r.ReadByte(), b2 = r.ReadByte(), b3 = r.ReadByte();
            float w0 = r.ReadByte(), w1 = r.ReadByte(), w2 = r.ReadByte(), w3 = r.ReadByte();
            float sum = Mathf.Max(1f, w0 + w1 + w2 + w3);
            m.w[i] = new BoneWeight { boneIndex0 = b0, boneIndex1 = b1, boneIndex2 = b2, boneIndex3 = b3, weight0 = w0 / sum, weight1 = w1 / sum, weight2 = w2 / sum, weight3 = w3 / sum };
        }
        m.bind = new Matrix4x4[r.ReadInt32()];
        for (int i = 0; i < m.bind.Length; i++) { var mat = new Matrix4x4(); for (int k = 0; k < 16; k++) mat[k] = r.ReadSingle(); m.bind[i] = mat; }
        m.subs = new int[r.ReadInt32()][];
        for (int s = 0; s < m.subs.Length; s++)
        {
            var t = new int[r.ReadInt32()];
            for (int i = 0; i < t.Length; i++) t[i] = nv > 65000 ? r.ReadInt32() : r.ReadUInt16();
            m.subs[s] = t;
        }
        return m;
    }
}
