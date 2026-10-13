using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

// Face Metrics Fix Step 0: measurements instead of looking at renders. For every lineup character (M/F x
// DEX 1 / 10 / 20 x STR 1 / 20 x INT 1 / 10 / 15 / 20 - the same seed across DEX - plus random seeds) and
// every fill level (bone, + muscle, + fat, final) it measures, with a pass threshold each:
//   open      boundary edges of the welded head + body skin round the head (the mouth's slit excepted)    0
//   flipped   head triangles facing against the field's gradient at their centre, or turned over
//             relative to the finished head                                                              0
//   pieces    connected pieces of the welded skin that reach above the neck's cut                         1
//   seam      the head ring's distance from its body twin (the body doesn't change with the levels)   < 0.5 mm
//   width     front-view silhouette widths (ears left out) at the ear, cheekbone and jaw lines; the bone
//             width must not depend on DEX (spread < 3% across DEX 1 / 10 / 20, same seed), and the widths
//             must grow bone -> + muscle -> + fat -> final
//   midline   the face's midline profile (hairline -> chin -> under the jaw), local concavities (deeper than
//             0.5 mm under a chord of <= 20 mm): only the nose's root, the under-nose angle, the mouth line
//             and the chin crease (lean <= 3 mm, fat <= 6 mm deep, <= half the mouth's width) are allowed
//   jaw       front silhouette: jaw width >= 0.6 x cheekbone width; the jaw angle a corner (the outline
//             turns >= 15 degrees there)
//   brows     the gap between the brows' inner ends >= 0.7 eye widths; no brow tint on the midline
//   roots     hair card roots outside their mask (scalp / brow / beard zone), head-hair cards over the face    0
//   ears      each ear's gap from the head (the ear left out of the field) down its height: attached
//             (<= 0.5 mm) over the top 80%
// Field measurements use the SDF at each level (the kernel's muscle / fat / brain scales); mesh ones the
// head mesh moved by its fill-level blend shapes. Writes a table (the scenario log) and JSON.
public static class FaceMetrics
{
    public struct Case { public string label, group; public CharacterSheet sheet; }

    public static List<Case> Cases(int randomSeeds = 20)
    {
        var list = new List<Case>();
        int g = 0;
        foreach (var sex in new[] { Sex.Male, Sex.Female })
            foreach (int str in new[] { 1, 20 })
                foreach (int intel in new[] { 1, 10, 15, 20 })
                {
                    int seed = 9300 + g; string group = $"{(sex == Sex.Male ? "M" : "F")} STR{str} INT{intel}"; g++;
                    foreach (int dex in new[] { 1, 10, 20 })
                        list.Add(new Case { label = $"{group} DEX{dex}", group = group, sheet = new CharacterSheet(sex, str, intel, dex, seed) });
                }
        for (int i = 0; i < randomSeeds; i++)
        {
            var sh = CharacterSheet.Roll(78000 + i * 17, 0f, 0f, 0f, 5f);
            list.Add(new Case { label = $"rnd {sh}", group = null, sheet = sh });
        }
        return list;
    }

    public static readonly string[] Levels = { "bone", "muscle", "fat", "final" };

    public class Concavity { public string kind; public float y, depth, width; public bool ok; }

    public class LevelResult
    {
        public int open, flippedField, flippedTurn, pieces;
        public float seamGap, offSurface;   // offSurface: 95th percentile of the head mesh's distance from the level's field (m)
        public float wEar, wCheek, wJaw, wChin, jawCorner;
        public float mEar, mCheek, mJaw;   // the same widths on the head mesh at this level (what's seen), ears left out
        public List<Concavity> midline = new List<Concavity>();
        public float earGapL, earGapR;
        public string detail = "";
        public bool Pass(out string why)
        {
            var sb = new StringBuilder();
            if (open > 0) sb.Append("open ");
            if (flippedField > 0 || flippedTurn > 0) sb.Append("flipped ");
            if (pieces != 1) sb.Append("pieces ");
            if (seamGap >= 0.0005f) sb.Append("seam ");
            foreach (var c in midline) if (!c.ok) { sb.Append("midline "); break; }
            if (wJaw < 0.6f * wCheek || jawCorner < 15f) sb.Append("jaw ");
            if (earGapL > 0.0005f || earGapR > 0.0005f) sb.Append("ears ");
            why = sb.ToString().Trim();
            return why.Length == 0;
        }
    }

    public class HeadResult
    {
        public string label, group; public int dex;
        public float buildMs; public int headTris, bodyTris;
        public LevelResult[] levels = new LevelResult[4];
        public float browGap, browGapRatio, browTintMid;
        public int rootsScalp, rootsBrow, rootsBeard, cardsOnFace;
        public string error;
        public bool PassHead(out string why)
        {
            var sb = new StringBuilder();
            if (browGapRatio < 0.7f) sb.Append("browgap ");
            if (browTintMid > 0.02f) sb.Append("browtint ");
            if (rootsScalp + rootsBrow + rootsBeard + cardsOnFace > 0) sb.Append("roots ");
            why = sb.ToString().Trim();
            return why.Length == 0;
        }
    }

    static string F(float v, string fmt = "0.0") => v.ToString(fmt, CultureInfo.InvariantCulture);

    // Builds and measures every case (only: a label filter). Returns the table; json gets the JSON.
    public static bool Run(out string table, out string json, int randomSeeds = 20, string only = null, System.Action<string> progress = null)
    {
        bool fl = BodyBuild.FillLevels;
        BodyBuild.FillLevels = true;
        var results = new List<HeadResult>();
        foreach (var cs in Cases(randomSeeds))
        {
            if (only != null && !cs.label.Contains(only)) continue;
            progress?.Invoke($"face metrics: {cs.label}");
            var r = new HeadResult { label = cs.label, group = cs.group, dex = cs.sheet.DEX };
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var a = new BodyBuild(cs.sheet).RunSync();
                r.buildMs = (float)sw.Elapsed.TotalMilliseconds; r.headTris = a.head.triangles.Length / 3; r.bodyTris = a.body.triangles.Length / 3;
                Measure(a, r);
                Free(a.body); Free(a.head); Free(a.wig); Free(a.hairCards);
            }
            catch (System.Exception e) { r.error = e.GetType().Name + ": " + e.Message; }
            results.Add(r);
        }
        BodyBuild.FillLevels = fl;
        return Report(results, out table, out json);
    }

    static void Free(Object o) { if (o == null) return; if (Application.isPlaying) Object.Destroy(o); else Object.DestroyImmediate(o); }

    // ---------------- measuring one head ----------------

    static void Measure(BodyAsset a, HeadResult r)
    {
        var plan = a.plan;
        using var sdf = new BodySDF(plan);
        var c = sdf.Canon;
        var k0 = sdf.Kernel; k0.cutMode = 0;
        bool weld = sdf.HasHeadPiece;
        Vector3 cutO = sdf.HeadCutOrigin, cutAxis = sdf.HeadCutAxis;

        // The welded skin (head + body) at the finished positions.
        var hm = a.head; var hv = hm.vertices; var ht = hm.GetTriangles(0);
        var bv = a.body.vertices; var bt = a.body.GetTriangles(0);
        var ids = new Dictionary<Vector3Int, int>();
        Vector3Int Key(Vector3 p) => new Vector3Int(Mathf.RoundToInt(p.x * 5000f), Mathf.RoundToInt(p.y * 5000f), Mathf.RoundToInt(p.z * 5000f));
        var pos = new List<Vector3>();
        int Id(Vector3 p) { var key = Key(p); if (!ids.TryGetValue(key, out int id)) { id = pos.Count; ids[key] = id; pos.Add(p); } return id; }
        var bodyIds = new HashSet<int>();
        var bmap = new int[bv.Length]; for (int i = 0; i < bv.Length; i++) { bmap[i] = Id(bv[i]); bodyIds.Add(bmap[i]); }
        var hmap = new int[hv.Length]; for (int i = 0; i < hv.Length; i++) hmap[i] = Id(hv[i]);
        // Head vertices with a body twin: the seam ring.
        var ring = new List<int>(); for (int i = 0; i < hv.Length; i++) if (bodyIds.Contains(hmap[i])) ring.Add(i);
        float Above(Vector3 p) => weld ? Vector3.Dot(p - cutO, cutAxis) : p.y - (c.o.y + c.chin - 0.05f);
        Vector3 mouth = a.mouthCentre; float mouthHalf = c.mouthHalfWidth;
        // (The mouth's slit and the box behind it: open / double-sided / its own piece by design.)
        bool MouthSlit(Vector3 p) { Vector3 d = p - mouth; return Mathf.Abs(Vector3.Dot(d, c.x)) < mouthHalf + 0.006f && Mathf.Abs(Vector3.Dot(d, c.y)) < 0.016f && Vector3.Dot(d, c.z) > -0.045f; }

        // Open edges and pieces (topology: the same at every level).
        var use = new Dictionary<long, int>();
        void Edges(int[] t, int[] map)
        {
            for (int i = 0; i + 2 < t.Length; i += 3)
                for (int e = 0; e < 3; e++)
                {
                    int p0 = map[t[i + e]], p1 = map[t[i + (e + 1) % 3]];
                    if (p0 == p1) continue;
                    long key = p0 < p1 ? ((long)p0 << 32) | (uint)p1 : ((long)p1 << 32) | (uint)p0;
                    use.TryGetValue(key, out int n); use[key] = n + 1;
                }
        }
        Edges(ht, hmap); Edges(bt, bmap);
        bool dressed = a.outfit != null && a.outfit.Count > 0;
        string Cm(Vector3 p) { Vector3 d = p - c.o; return $"({F(Vector3.Dot(d, c.x) * 100f)},{F(Vector3.Dot(d, c.y) * 100f)},{F(Vector3.Dot(d, c.z) * 100f)})"; }
        int open = 0; var openAt = new StringBuilder();
        foreach (var kv in use)
        {
            if (kv.Value != 1 || dressed) continue;
            Vector3 p0 = pos[(int)(kv.Key >> 32)], p1 = pos[(int)(kv.Key & 0xffffffff)];
            if (Above(p0) < -0.03f && Above(p1) < -0.03f) continue;
            if (MouthSlit(p0) && MouthSlit(p1)) continue;
            if (open < 4) openAt.Append(Cm(p0));
            open++;
        }
        var parent = new int[pos.Count]; for (int i = 0; i < parent.Length; i++) parent[i] = i;
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        void Union(int[] t, int[] map) { for (int i = 0; i + 2 < t.Length; i += 3) { int p0 = Find(map[t[i]]); parent[Find(map[t[i + 1]])] = p0; parent[Find(map[t[i + 2]])] = p0; } }
        Union(ht, hmap); Union(bt, bmap);
        var headPieces = new HashSet<int>();
        var usedH = new bool[hv.Length]; foreach (int i in ht) usedH[i] = true;
        for (int i = 0; i < hv.Length; i++) if (usedH[i] && Above(hv[i]) > 0.01f && !MouthSlit(hv[i])) headPieces.Add(Find(hmap[i]));
        // (The extra pieces: their vertex count and centre.)
        var pieceAt = new StringBuilder();
        if (headPieces.Count > 1)
        {
            var cnt = new Dictionary<int, int>(); var sum = new Dictionary<int, Vector3>();
            for (int i = 0; i < hv.Length; i++) { if (!usedH[i]) continue; int rt = Find(hmap[i]); if (!headPieces.Contains(rt)) continue; cnt.TryGetValue(rt, out int n0); cnt[rt] = n0 + 1; sum.TryGetValue(rt, out var s0); sum[rt] = s0 + hv[i]; }
            int big = -1; foreach (var kv in cnt) if (big < 0 || kv.Value > cnt[big]) big = kv.Key;
            foreach (var kv in cnt) if (kv.Key != big) pieceAt.Append($"{kv.Value}v@{Cm(sum[kv.Key] / kv.Value)} ");
        }

        // Fill-level positions of the head mesh.
        var lv = new Vector3[4][];
        for (int l = 0; l < 3; l++)
        {
            int bi = hm.GetBlendShapeIndex(BodyBuild.FillShapes[l]);
            lv[l] = (Vector3[])hv.Clone();
            if (bi < 0) continue;
            var dv = new Vector3[hv.Length];
            hm.GetBlendShapeFrameVertices(bi, 0, dv, null, null);
            for (int i = 0; i < hv.Length; i++) lv[l][i] += dv[i];
        }
        lv[3] = hv;

        var earMask = new bool[hv.Length];
        for (int i = 0; i < hv.Length; i++) earMask[i] = k0.earL.on != 0 && Mathf.Min(BodySdfKernel.EarDist(k0.earL, hv[i]), BodySdfKernel.EarDist(k0.earR, hv[i])) < 0.004f;
        float fatChin = 0f;
        {
            var hb = plan.bones[plan.Index("Head")];
            fatChin = Mathf.Max(hb.fat.x, Mathf.Max(hb.fat.y, hb.fat.z));
        }
        for (int l = 0; l < 4; l++)
        {
            var k = k0;
            k.muscleScale = l >= 1 ? 1f : 0f; k.fatScale = l >= 2 ? 1f : 0f; k.brainScale = l >= 3 ? 1f : 0f;
            var res = new LevelResult { open = open, pieces = headPieces.Count };
            var flipAt = new StringBuilder(); int seamWorst = -1;
            // (Where the flips are, by region.)
            var regions = new Dictionary<string, int>();
            void Region(Vector3 p)
            {
                Vector3 d = p - c.o; float x = Vector3.Dot(d, c.x), y = Vector3.Dot(d, c.y), z = Vector3.Dot(d, c.z);
                string rg = k0.earL.on != 0 && Mathf.Min(BodySdfKernel.EarDist(k0.earL, p), BodySdfKernel.EarDist(k0.earR, p)) < 0.006f ? "ear"
                          : Mathf.Abs(x) < c.noseHalfWidth * 1.3f && y > c.noseBase - 0.006f && y < c.brow && z > c.halfD * 0.5f ? "nose"
                          : Mathf.Abs(x) < c.mouthHalfWidth + 0.01f && y > c.mouth - 0.015f && y < c.noseBase && z > 0f ? "mouth"
                          : y < c.chin + 0.01f && z > c.earZ ? "under jaw"
                          : y < c.chin - 0.01f ? "neck" : y > c.brow ? "cranium" : "face";
                regions.TryGetValue(rg, out int cnt); regions[rg] = cnt + 1;
            }
            // Flipped: against the field's gradient, or turned over from the finished head.
            var v = lv[l];
            for (int i = 0; i + 2 < ht.Length; i += 3)
            {
                Vector3 p0 = v[ht[i]], p1 = v[ht[i + 1]], p2 = v[ht[i + 2]];
                Vector3 fn = Vector3.Cross(p1 - p0, p2 - p0);
                if (fn.sqrMagnitude < 1e-14f) continue;
                Vector3 ctr = (p0 + p1 + p2) / 3f;
                if (MouthSlit(hv[ht[i]])) continue;
                const float h = 0.0005f;
                float s0 = k.Skin(ctr);
                Vector3 g = new Vector3(k.Skin(ctr + new Vector3(h, 0, 0)) - s0, k.Skin(ctr + new Vector3(0, h, 0)) - s0, k.Skin(ctr + new Vector3(0, 0, h)) - s0);
                if (g.sqrMagnitude > 1e-14f && Vector3.Dot(fn.normalized, g.normalized) < -0.2f) { res.flippedField++; Region(ctr); }
                if (l < 3)
                {
                    Vector3 f3 = Vector3.Cross(hv[ht[i + 1]] - hv[ht[i]], hv[ht[i + 2]] - hv[ht[i]]);
                    if (f3.sqrMagnitude > 1e-14f && Vector3.Dot(fn, f3) < 0f) res.flippedTurn++;
                }
            }
            {
                var off = new List<float>(hv.Length);
                for (int i = 0; i < hv.Length; i += 2) if (!MouthSlit(hv[i]) && !earMask[i]) off.Add(Mathf.Abs(k.Skin(v[i])));
                off.Sort(); res.offSurface = off.Count > 0 ? off[(int)(off.Count * 0.95f)] : 0f;
                if (l == 3)
                {
                    var rg = new Dictionary<string, int>(); int nOff = 0;
                    for (int i = 0; i < hv.Length; i += 2)
                    {
                        if (MouthSlit(hv[i]) || earMask[i]) continue;
                        float sd = k.Skin(v[i]); if (Mathf.Abs(sd) < 0.002f) continue;
                        nOff++;
                        Vector3 d = v[i] - c.o; float x = Vector3.Dot(d, c.x), y = Vector3.Dot(d, c.y), z = Vector3.Dot(d, c.z);
                        string key = (sd > 0 ? "out " : "in ") + (y < c.chin ? "neck" : y < c.mouth ? "jaw" : y < c.noseBase ? "mouth band" : y < c.brow ? (Mathf.Abs(x) < c.halfW * 0.45f ? "eyes/nose" : "cheek/temple") : "cranium") + (z < c.earZ ? " back" : "");
                        rg.TryGetValue(key, out int cn); rg[key] = cn + 1;
                        if (nOff <= 6)
                        {
                            var kc = k; kc.cutMode = 2 + k.cuts.Length - 1;
                            var hbx = sdf.HeadBounds; bool inBox = hbx.Contains(v[i]);
                            Vector3 bd = Vector3.Max(hbx.min - v[i], v[i] - hbx.max);
                            res.detail += $"off {F(sd * 1000f)}mm (head cut field {F(kc.Skin(v[i]) * 1000f)}mm, {(inBox ? "in" : "OUT of")} the head grid, box margin {F(-Mathf.Max(bd.x, Mathf.Max(bd.y, bd.z)) * 1000f)}mm) at {Cm(v[i])} {F(Above(v[i]) * 100f)} cm above the cut; ";
                        }
                    }
                    var rs = new StringBuilder(); foreach (var kv in rg) rs.Append($"{kv.Key} {kv.Value}, ");
                    res.detail += $"vertices > 2 mm off the field (of {hv.Length / 2}): {rs}; ";
                }
            }
            // Seam: the head ring against its body twin (the body stays put).
            foreach (int i in ring) { float gp = (v[i] - hv[i]).magnitude; if (gp > res.seamGap) { res.seamGap = gp; seamWorst = i; } }
            if (open > 0) res.detail += $"open at {openAt}; ";
            if (pieceAt.Length > 0) res.detail += $"extra pieces {pieceAt}; ";
            if (l == 0) res.detail += $"[{a.neckWeld}] ";
            if (res.flippedField > 0) { foreach (var kv in regions) flipAt.Append($"{kv.Key} {kv.Value} "); res.detail += $"flipped by region: {flipAt}; "; }
            if (seamWorst >= 0 && res.seamGap >= 0.0005f) res.detail += $"seam worst at {Cm(hv[seamWorst])} {F(Above(hv[seamWorst]) * 1000f)} mm above the cut ({ring.Count} ring vertices); ";
            // Widths (front silhouette, ears left out).
            var ke = k; ke.earL.on = 0; ke.earR.on = 0;
            float earY = (c.earTop + c.earBottom) * 0.5f, cheekY = -0.17f * c.halfH, jawY = -0.66f * c.halfH;
            res.wEar = Silhouette(ke, c, earY); res.wCheek = Silhouette(ke, c, cheekY); res.wJaw = Silhouette(ke, c, jawY);
            res.wChin = Silhouette(ke, c, c.chin + 0.01f);
            res.mEar = MeshWidth(v, earMask, c, earY, false); res.mCheek = MeshWidth(v, earMask, c, cheekY, true); res.mJaw = MeshWidth(v, earMask, c, jawY, true);
            // The jaw's corner: how much the outline turns, the best over the jaw line +- 1 cm (the angle sits
            // a little higher or lower per head).
            for (float jy = jawY - 0.01f; jy <= jawY + 0.0101f; jy += 0.005f)
            {
                float w0 = Silhouette(ke, c, jy), up = Silhouette(ke, c, jy + 0.015f), dn = Silhouette(ke, c, jy - 0.015f);
                float a1 = Mathf.Atan2((up - w0) * 0.5f, 0.015f) * Mathf.Rad2Deg, a2 = Mathf.Atan2((w0 - dn) * 0.5f, 0.015f) * Mathf.Rad2Deg;
                res.jawCorner = Mathf.Max(res.jawCorner, a2 - a1);
            }
            // Midline profile.
            res.midline = Midline(ke, c, a.sheet.DEX, fatChin);
            // Ears.
            res.earGapL = EarGap(k, k.EarAt(k.earL), out string prof); res.earGapR = EarGap(k, k.EarAt(k.earR), out _);
            if (res.earGapL > 0.0005f || res.earGapR > 0.0005f) res.detail += $"left ear gap top->bottom mm {prof}; ";
            r.levels[l] = res;
        }

        // Brows and roots (the finished head).
        {
            bool female = a.sheet.sex == Sex.Female;
            var lines = sdf.EyeRadius > 0f ? FaceHair.BrowLines(k0, c, sdf.EyeL, sdf.EyeR, a.sheet.seed, female) : null;
            var st = HairStyle.For(a.wigStyle);
            float innerL = float.MaxValue, innerR = float.MaxValue;
            if (a.hairCards != null)
            {
                var cv = a.hairCards.vertices; var cu = new List<Vector2>(); a.hairCards.GetUVs(0, cu); var cu2 = new List<Vector2>(); a.hairCards.GetUVs(1, cu2);
                for (int i = 0; i + 1 < cv.Length; i++)
                {
                    if (cu[i].y != 0f || cu[i].x != 0f) continue;
                    Vector3 root = (cv[i] + cv[i + 1]) * 0.5f;
                    // (Back onto the skin: roots stand a little off it.)
                    for (int it = 0; it < 3; it++) root -= k0.Normal(root, 0.002f) * k0.Skin(root);
                    var u = HairCards.FaceU(c, root);
                    bool faceHair = cu2[i].y < -0.5f;
                    // The card's vertices: to the next card's root.
                    int end = i + 2; while (end + 1 < cv.Length && !(cu[end].y == 0f && cu[end].x == 0f)) end++;
                    if (!faceHair)
                    {
                        if (st != null && !HairCards.ScalpU(u, st.nape, 0.02f)) r.rootsScalp++;
                        for (int j = i + 2; j < end; j++) if (HairCards.OnFaceU(HairCards.FaceU(c, cv[j]))) { r.cardsOnFace++; break; }
                    }
                    else if (Vector3.Dot(root - c.o, c.y) > 0f)
                    {
                        if (lines == null || FaceHair.BrowTint(lines, c, root, female) < 0.05f) r.rootsBrow++;
                        float x = Vector3.Dot(root - c.o, c.x);
                        if (x > 0f) innerR = Mathf.Min(innerR, x); else innerL = Mathf.Min(innerL, -x);
                    }
                    else if (FaceHair.Zone(c, root).All < 0.02f) r.rootsBeard++;
                    i = end - 1;
                }
            }
            r.browGap = innerL < float.MaxValue && innerR < float.MaxValue ? innerL + innerR : 0f;
            r.browGapRatio = r.browGap / Mathf.Max(c.eyeWidth, 1e-4f);
            if (lines != null)
                for (float y = 0f; y < c.brow + 0.015f; y += 0.001f)
                {
                    Vector3 p = c.o + c.y * y + c.z * (c.halfD * 2f);
                    for (int it = 0; it < 200; it++) { float d = k0.Skin(p); if (d < 0.0002f) break; p -= c.z * Mathf.Clamp(d * 0.9f, 0.0002f, 0.01f); }
                    r.browTintMid = Mathf.Max(r.browTintMid, FaceHair.BrowTint(lines, c, p, female));
                }
        }
    }

    // The head mesh's width at height y (vertices within 3 mm of it; the ears' left out; face lines: in
    // front of the ears only - the neck behind the jaw is the neck).
    static float MeshWidth(Vector3[] v, bool[] ear, HeadCanon c, float y, bool front)
    {
        float lo = 0f, hi = 0f;
        for (int i = 0; i < v.Length; i++)
        {
            if (ear[i]) continue;
            Vector3 d = v[i] - c.o;
            if (Mathf.Abs(Vector3.Dot(d, c.y) - y) > 0.003f) continue;
            if (front && Vector3.Dot(d, c.z) < c.earZ) continue;
            float x = Vector3.Dot(d, c.x);
            lo = Mathf.Min(lo, x); hi = Mathf.Max(hi, x);
        }
        return hi - lo;
    }

    // The front-view silhouette's width at height y (from the eye line): per side, the farthest surface
    // crossing over the head's depth - the head's own field (a fat neck behind the jaw isn't the head).
    static float Silhouette(BodySdfKernel k, HeadCanon c, float y)
    {
        float w = 0f;
        foreach (float side in new[] { 1f, -1f })
        {
            float best = 0f;
            for (int zi = 0; zi <= 24; zi++)
            {
                float z = Mathf.Lerp(-c.halfD * 1.15f, c.halfD * 1.35f, zi / 24f);
                Vector3 b = c.o + c.y * y + c.z * z;
                float x = c.halfW * 2.2f;
                if (k.HeadSkin(b + c.x * side * x) < 0f) { best = Mathf.Max(best, x); continue; }
                bool hit = false;
                for (int it = 0; it < 120 && x > 0f; it++)
                {
                    float d = k.HeadSkin(b + c.x * side * x);
                    if (d < 0.0002f) { hit = true; break; }
                    x -= Mathf.Clamp(d * 0.8f, 0.0003f, 0.01f);
                }
                if (hit) best = Mathf.Max(best, x);
            }
            w += best;
        }
        return w;
    }

    // The midline profile (pivot behind the mouth; rays in the face's middle plane, from up round the front
    // to under the jaw), and its local concavities.
    static List<Concavity> Midline(BodySdfKernel k, HeadCanon c, int dex, float fat)
    {
        var res = new List<Concavity>();
        Vector3 piv = c.o + c.y * c.mouth - c.z * (c.halfD * 0.3f);
        var pts = new List<Vector2>();   // (forward, up) from the eye line's centre
        float chinFront = float.MinValue; bool belowChin = false;
        for (float th = 0f; th <= 215f; th += 0.5f)
        {
            float rad = th * Mathf.Deg2Rad;
            Vector3 dir = c.y * Mathf.Cos(rad) + c.z * Mathf.Sin(rad);
            float r = 0.3f; bool hit = false;
            for (int it = 0; it < 200 && r > 0f; it++)
            {
                float d = k.Skin(piv + dir * r);
                if (d < 0.0001f) { hit = true; break; }
                r -= Mathf.Clamp(d * 0.8f, 0.0001f, 0.01f);
            }
            if (!hit) continue;
            Vector3 p = piv + dir * r - c.o;
            var q = new Vector2(Vector3.Dot(p, c.z), Vector3.Dot(p, c.y));
            if (q.y > c.hairline) continue;
            if (q.y < c.chin + 0.01f) belowChin = true;
            if (q.y < c.mouth - 0.01f) chinFront = Mathf.Max(chinFront, q.x);
            if (belowChin && q.x < chinFront - 0.035f) break;
            pts.Add(q);
        }
        int n = pts.Count;
        if (n < 10) return res;
        var arc = new float[n]; for (int i = 1; i < n; i++) arc[i] = arc[i - 1] + (pts[i] - pts[i - 1]).magnitude;
        var depth = new float[n];
        const float W = 0.02f;
        for (int i = 1; i < n - 1; i++)
        {
            float best = 0f;
            for (int j = i - 1; j >= 0 && arc[i] - arc[j] <= W; j--)
                for (int m = i + 1; m < n && arc[m] - arc[j] <= W; m++)
                {
                    Vector2 t = pts[m] - pts[j]; float len = t.magnitude; if (len < 1e-5f) continue; t /= len;
                    Vector2 nOut = new Vector2(-t.y, t.x);
                    best = Mathf.Max(best, Vector2.Dot(pts[j] - pts[i], nOut));
                }
            depth[i] = best;
        }
        float lim = 0.003f + 0.003f * Mathf.Clamp01(fat / 0.025f);
        for (int i = 0; i < n; )
        {
            if (depth[i] <= 0.0005f) { i++; continue; }
            int s = i, deep = i;
            while (i < n && depth[i] > 0.0005f) { if (depth[i] > depth[deep]) deep = i; i++; }
            var cv = new Concavity { y = pts[deep].y, depth = depth[deep], width = (pts[i - 1] - pts[s]).magnitude };
            float y = cv.y;
            if (y > -0.015f && y < c.brow + 0.012f) { cv.kind = "nose root"; cv.ok = true; }
            else if (y > c.noseBase - 0.012f && y < c.noseBase + 0.008f) { cv.kind = "under nose"; cv.ok = true; }
            else if (Mathf.Abs(y - c.mouth) <= 0.007f) { cv.kind = "mouth line"; cv.ok = true; }
            else if (y < c.mouth - 0.007f && y > c.mouth - 0.03f) { cv.kind = "chin crease"; cv.ok = cv.depth <= lim && cv.width <= c.mouthHalfWidth; }
            else { cv.kind = "other"; cv.ok = false; }
            res.Add(cv);
        }
        return res;
    }

    // Face Metrics lower face: the side profile's numbers on the finished head (mm, forward from the eye line's
    // centre): upper lip, lower lip, the fold under it, the chin's front; the nose tip's projection in front of
    // the cheek; the jaw's width at the jaw line (cm).
    public static string Profile(BodyPlan plan)
    {
        using var sdf = new BodySDF(plan);
        var c = sdf.Canon; var k = sdf.Kernel; k.cutMode = 0;
        float Front(float x, float y)
        {
            Vector3 b = c.o + c.x * x + c.y * y;
            float z = c.halfD * 2.2f;
            for (int it = 0; it < 300; it++) { float d = k.Skin(b + c.z * z); if (d < 0.0001f) break; z -= Mathf.Clamp(d * 0.8f, 0.0001f, 0.01f); }
            return z;
        }
        float MaxF(float y0, float y1) { float m = float.MinValue; for (float y = y0; y <= y1; y += 0.0005f) m = Mathf.Max(m, Front(0f, y)); return m; }
        float upper = MaxF(c.mouth + 0.001f, c.mouth + 0.012f);
        float lower = MaxF(c.mouth - 0.012f, c.mouth - 0.001f);
        // The lower lip's front height, then the chin below it (to the chin line), and the deepest point between.
        float yl = c.mouth - 0.006f, best = float.MinValue;
        for (float y = c.mouth - 0.014f; y <= c.mouth; y += 0.0005f) { float f = Front(0f, y); if (f > best) { best = f; yl = y; } }
        float chin = float.MinValue, yc = c.chin;
        for (float y = c.chin; y <= yl - 0.006f; y += 0.0005f) { float f = Front(0f, y); if (f > chin) { chin = f; yc = y; } }
        float fold = float.MaxValue; for (float y = yc; y <= yl; y += 0.0005f) fold = Mathf.Min(fold, Front(0f, y));
        float foldDepth = Mathf.Min(lower, chin) - fold;
        float noseTip = MaxF(c.noseBase - 0.002f, c.noseBase + 0.03f);
        float cheek = Front(c.halfW * 0.45f, c.noseBase + 0.005f);
        var ke = k; ke.earL.on = 0; ke.earR.on = 0;
        float jaw = Silhouette(ke, c, -0.66f * c.halfH);
        float cheekW = Silhouette(ke, c, -0.17f * c.halfH);
        var parts = new StringBuilder();
        // (Each layer's front at the lips and the chin: bone, muscle, skin - HeadLayers - and the whole field.)
        // (The midline, every 2 mm from above the mouth down to the chin: y | field front / muscle front / bone front, mm.)
        parts.Append("midline y|field/muscle/bone: ");
        for (float yy = c.mouth + 0.012f; yy >= c.chin - 0.004f; yy -= 0.002f)
        {
            float LF(int which)
            {
                Vector3 b0 = c.o + c.y * yy; float z = c.halfD * 2.2f;
                for (int it = 0; it < 400; it++)
                {
                    Vector3 q = b0 + c.z * z; float d;
                    if (which == 3) d = k.Skin(q); else { k.HeadLayers(q, 0f, out float hb, out float hm, out _); d = which == 0 ? hb : hm; }
                    if (d < 0.0001f) break; z -= Mathf.Clamp(d * 0.8f, 0.0001f, 0.01f);
                }
                return z;
            }
            parts.Append($"{F((yy - c.mouth) * 1000f, "0")}|{F(LF(3) * 1000f, "0")}/{F(LF(1) * 1000f, "0")}/{F(LF(0) * 1000f, "0")} ");
        }
        parts.Append("; ");
        foreach (var (lbl, yy) in new[] { ("upper lip", c.mouth + 0.005f), ("lower lip", c.mouth - 0.006f), ("chin", c.chin + 0.012f) })
        {
            float L(int which)
            {
                Vector3 b0 = c.o + c.y * yy; float z = c.halfD * 2.2f;
                for (int it = 0; it < 400; it++)
                {
                    Vector3 q = b0 + c.z * z; float d;
                    if (which == 3) d = k.Skin(q);
                    else { k.HeadLayers(q, 0f, out float hb, out float hm, out float hs); d = which == 0 ? hb : which == 1 ? hm : hs; }
                    if (d < 0.0001f) break; z -= Mathf.Clamp(d * 0.8f, 0.0001f, 0.01f);
                }
                return z;
            }
            parts.Append($"{lbl} front bone {F(L(0) * 1000f)} muscle {F(L(1) * 1000f)} skin(no fat) {F(L(2) * 1000f)} field {F(L(3) * 1000f)}; ");
        }
        for (int j = 0; j < sdf.FacePartNames.Count; j++)
        {
            string nm = sdf.FacePartNames[j];
            if (nm != "Chin" && nm != "LowerJawArc" && nm != "UpperJawArc" && nm != "LipLower" && !nm.StartsWith("LipUpper")) continue;
            var fp = sdf.FacePrims[j]; Vector3 d = fp.o - c.o;
            parts.Append($"{nm} o({F(Vector3.Dot(d, c.y) * 1000f)},{F(Vector3.Dot(d, c.z) * 1000f)}) r({F(fp.r.x * 1000f)},{F(fp.r.y * 1000f)},{F(fp.r.z * 1000f)}) press {F(fp.press * 1000f)}; ");
        }
        return $"[{parts}] jaw/cheek {F(jaw / Mathf.Max(cheekW, 1e-4f), "0.000")}, chin vs lower lip {F((chin - lower) / (2f * c.halfH) * 100f, "0.0")}% of head height; " +
               $"profile mm: upper lip {F(upper * 1000f)}, lower lip {F(lower * 1000f)} ({F((lower - upper) * 1000f)} vs upper), fold {F(foldDepth * 1000f)} deep, chin {F(chin * 1000f)} ({F((chin - lower) * 1000f)} vs lower lip); " +
               $"nose tip {F(noseTip * 1000f)} ({F((noseTip - cheek) * 1000f)} mm in front of the cheek); jaw width {F(jaw * 100f, "0.00")} cm";
    }

    // An ear's attachment: at each height down its top 80%, a walk from its front edge (inside the ear)
    // straight in toward the head until the head (the field without the ears) is reached; the most air
    // crossed on the way (the field with the ears above 0) is the gap there. Returns the largest.
    static float EarGap(BodySdfKernel k, BodySdfKernel.Ear e, out string profile)
    {
        profile = "";
        if (e.on == 0) return 0f;
        var sbp = new StringBuilder();
        var kn = k; kn.earL.on = 0; kn.earR.on = 0;
        float worst = 0f;
        for (int hi = 0; hi <= 16; hi++)
        {
            float f = hi / 16f;                // 0 top .. 1 bottom
            if (f > 0.8f) break;
            float a = e.h * (1f - 2f * f) * 0.95f;
            float b = -e.w * Mathf.Sqrt(Mathf.Max(0f, 1f - (a / e.h) * (a / e.h))) * 0.8f;
            Vector3 p0 = e.c + e.u * a + e.v * b;
            Vector3 dir = Vector3.ProjectOnPlane(k.skull.o - p0, k.skull.y).normalized;
            float air = 0f;
            for (float t = 0f; t < 0.05f; t += 0.0005f)
            {
                Vector3 q = p0 + dir * t;
                if (kn.HeadSkin(q) <= 0f) break;
                air = Mathf.Max(air, k.HeadSkin(q));
            }
            worst = Mathf.Max(worst, air); sbp.Append(F(air * 1000f)).Append(' ');
        }
        profile = sbp.ToString();
        return worst;
    }

    // ---------------- report ----------------

    static bool Report(List<HeadResult> results, out string table, out string json)
    {
        var sb = new StringBuilder(); var js = new StringBuilder();
        sb.AppendLine("head | level | open | flip field/turn | pieces | seam mm | width ear/cheek/jaw/chin cm (mesh ear/cheek/jaw) | jaw/cheek | jaw corner deg | midline (kind depth mm width mm) | ear gap L/R mm | result");
        js.Append("[");
        int fails = 0, n = 0;
        var metricFails = new Dictionary<string, int>();
        void Count(string why) { foreach (var w in why.Split(' ')) if (w.Length > 0) { metricFails.TryGetValue(w, out int x); metricFails[w] = x + 1; } }
        for (int hi = 0; hi < results.Count; hi++)
        {
            var r = results[hi]; n++;
            if (hi > 0) js.Append(",");
            js.Append("{\"head\":\"").Append(r.label).Append("\"");
            if (r.error != null) { sb.AppendLine($"{r.label} | ERROR {r.error}"); js.Append(",\"error\":\"").Append(r.error.Replace("\"", "'")).Append("\"}"); fails++; Count("error"); continue; }
            bool headOk = r.PassHead(out string hwhy);
            js.Append(",\"buildMs\":").Append(F(r.buildMs, "0")).Append(",\"headTris\":").Append(r.headTris).Append(",\"bodyTris\":").Append(r.bodyTris);
            js.Append(",\"browGapRatio\":").Append(F(r.browGapRatio, "0.000")).Append(",\"browTintMid\":").Append(F(r.browTintMid, "0.000"))
              .Append(",\"rootsScalp\":").Append(r.rootsScalp).Append(",\"rootsBrow\":").Append(r.rootsBrow).Append(",\"rootsBeard\":").Append(r.rootsBeard)
              .Append(",\"cardsOnFace\":").Append(r.cardsOnFace).Append(",\"levels\":[");
            bool allOk = headOk; var why = new StringBuilder(hwhy);
            for (int l = 0; l < 4; l++)
            {
                var x = r.levels[l];
                bool ok = x.Pass(out string lwhy);
                if (!ok) { allOk = false; why.Append(" ").Append(Levels[l]).Append(":").Append(lwhy.Replace(' ', ',')); Count(lwhy); }
                var mid = new StringBuilder();
                foreach (var cv in x.midline) mid.Append($"{cv.kind}{(cv.ok ? "" : "!")} {F(cv.depth * 1000f)}/{F(cv.width * 1000f, "0")}{(cv.kind == "other" ? $" @y{F(cv.y * 100f)}" : "")}; ");
                if (!ok && x.detail.Length > 0) sb.AppendLine($"    {r.label} {Levels[l]}: {x.detail}");
                sb.AppendLine($"{r.label} | {Levels[l]} | {x.open} | {x.flippedField}/{x.flippedTurn} | {x.pieces} | {F(x.seamGap * 1000f, "0.00")} off {F(x.offSurface * 1000f, "0.0")} | {F(x.wEar * 100f)}/{F(x.wCheek * 100f)}/{F(x.wJaw * 100f)}/{F(x.wChin * 100f)} ({F(x.mEar * 100f)}/{F(x.mCheek * 100f)}/{F(x.mJaw * 100f)}) | {F(x.wJaw / Mathf.Max(x.wCheek, 1e-4f), "0.00")} | {F(x.jawCorner, "0")} | {mid} | {F(x.earGapL * 1000f)}/{F(x.earGapR * 1000f)} | {(ok ? "ok" : "FAIL " + lwhy)}");
                if (l > 0) js.Append(",");
                js.Append("{\"level\":\"").Append(Levels[l]).Append("\",\"open\":").Append(x.open).Append(",\"flippedField\":").Append(x.flippedField).Append(",\"flippedTurn\":").Append(x.flippedTurn)
                  .Append(",\"pieces\":").Append(x.pieces).Append(",\"seamGapMm\":").Append(F(x.seamGap * 1000f, "0.000"))
                  .Append(",\"widthCm\":{\"ear\":").Append(F(x.wEar * 100f, "0.00")).Append(",\"cheek\":").Append(F(x.wCheek * 100f, "0.00")).Append(",\"jaw\":").Append(F(x.wJaw * 100f, "0.00")).Append(",\"chin\":").Append(F(x.wChin * 100f, "0.00")).Append("}")
                  .Append(",\"jawCornerDeg\":").Append(F(x.jawCorner, "0.0")).Append(",\"earGapMm\":[").Append(F(x.earGapL * 1000f, "0.00")).Append(",").Append(F(x.earGapR * 1000f, "0.00")).Append("]")
                  .Append(",\"midline\":[");
                for (int q = 0; q < x.midline.Count; q++)
                {
                    var cv = x.midline[q]; if (q > 0) js.Append(",");
                    js.Append("{\"kind\":\"").Append(cv.kind).Append("\",\"yCm\":").Append(F(cv.y * 100f, "0.00")).Append(",\"depthMm\":").Append(F(cv.depth * 1000f, "0.00")).Append(",\"widthMm\":").Append(F(cv.width * 1000f, "0.0")).Append(",\"ok\":").Append(cv.ok ? "true" : "false").Append("}");
                }
                js.Append("],\"pass\":").Append(ok ? "true" : "false").Append("}");
            }
            js.Append("],\"pass\":").Append(allOk ? "true" : "false").Append("}");
            sb.AppendLine($"{r.label} | build {F(r.buildMs, "0")} ms, head {r.headTris} / body {r.bodyTris} triangles");
            sb.AppendLine($"{r.label} | heads | brow gap {F(r.browGapRatio, "0.00")} eye widths, tint on midline {F(r.browTintMid, "0.00")} | roots outside masks scalp {r.rootsScalp} brow {r.rootsBrow} beard {r.rootsBeard}, head-hair cards over the face {r.cardsOnFace} | {(headOk ? "ok" : "FAIL " + hwhy)}");
            if (!headOk) Count(hwhy);
            if (!allOk) fails++;
        }
        js.Append("]");
        // Bone width against DEX (same seed): spread < 3%; widths growing with the levels.
        int spreadFails = 0, monoFails = 0;
        var groups = new Dictionary<string, List<HeadResult>>();
        foreach (var r in results) if (r.group != null && r.error == null) { if (!groups.TryGetValue(r.group, out var l)) groups[r.group] = l = new List<HeadResult>(); l.Add(r); }
        foreach (var kv in groups)
        {
            var g = kv.Value; if (g.Count < 2) continue;
            var line = new StringBuilder($"{kv.Key} | bone width over DEX");
            bool ok = true;
            foreach (var (nm, get) in new (string, System.Func<LevelResult, float>)[] { ("ear", x => x.wEar), ("cheek", x => x.wCheek), ("jaw", x => x.wJaw),
                                                                                       ("mesh ear", x => x.mEar), ("mesh cheek", x => x.mCheek), ("mesh jaw", x => x.mJaw) })
            {
                float mn = float.MaxValue, mx = 0f, sum = 0f;
                foreach (var r in g) { float w = get(r.levels[0]); mn = Mathf.Min(mn, w); mx = Mathf.Max(mx, w); sum += w; }
                float spread = (mx - mn) / Mathf.Max(sum / g.Count, 1e-4f);
                line.Append($" {nm} {F(spread * 100f)}%");
                if (spread >= 0.03f) ok = false;
            }
            if (!ok) spreadFails++;
            sb.AppendLine(line.Append(ok ? " | ok" : " | FAIL spread").ToString());
        }
        foreach (var r in results)
        {
            if (r.error != null) continue;
            bool ok = true;
            for (int l = 1; l < 4; l++)
                foreach (var (a0, a1) in new[] { (r.levels[l - 1].wEar, r.levels[l].wEar), (r.levels[l - 1].wCheek, r.levels[l].wCheek), (r.levels[l - 1].wJaw, r.levels[l].wJaw),
                                                 (r.levels[l - 1].mEar, r.levels[l].mEar), (r.levels[l - 1].mCheek, r.levels[l].mCheek), (r.levels[l - 1].mJaw, r.levels[l].mJaw) })
                    if (a1 < a0 - 0.0002f) ok = false;
            if (!ok) { monoFails++; sb.AppendLine($"{r.label} | widths not growing bone -> final | FAIL"); }
        }
        if (spreadFails > 0) metricFails["bone-width-dex"] = spreadFails;
        if (monoFails > 0) metricFails["width-growth"] = monoFails;
        var sum2 = new StringBuilder();
        foreach (var kv in metricFails) sum2.Append($"{kv.Key} {kv.Value}, ");
        bool pass = fails == 0 && spreadFails == 0 && monoFails == 0;
        sb.AppendLine(pass ? $"PASS face metrics: {n} heads x 4 levels" : $"FAIL face metrics: {fails} of {n} heads fail; bone width spread {spreadFails} groups, width growth {monoFails} heads; failing metrics (heads x levels): {sum2}");
        table = sb.ToString(); json = js.ToString();
        return pass;
    }
}
