using System.Collections.Generic;
using System.Text;
using UnityEngine;

// Face Fix Part 0: an automated SDF sanity test for heads. Builds the extremes (both sexes x DEX 1 / 10 /
// 20 x STR 1 / 20 x INT 1 / 20) plus random seeds and checks every head-mesh vertex (rest pose):
//   box    inside the canon box + margins (width, crown / cranium top, back of the cranium, nose front);
//          below the chin line only the neck (a tube round the neck bone) and the under-chin
//   shape  no farther from the nearest head shape (cranium + jaw core, the neck, a face part) than the
//          thickest fat there plus a margin: blend inflation ("tentacles") grows surface far from all
//          of them
// A failing vertex is reported with the face parts near it, the fat thickness and the face fat mask.
// (Ears count as shapes.) Run after every face change: scenario "facesanity", or BodyPoolBaker-style in the editor.
public static class FaceSanity
{
    public struct Case { public string label; public CharacterSheet sheet; }

    public static List<Case> Cases(int randomSeeds = 20)
    {
        var list = new List<Case>();
        int seed = 9100;
        foreach (var sex in new[] { Sex.Male, Sex.Female })
            foreach (int dex in new[] { 1, 10, 20 })
                foreach (int str in new[] { 1, 20 })
                    foreach (int intel in new[] { 1, 20 })
                        list.Add(new Case { label = $"{(sex == Sex.Male ? "M" : "F")} STR{str} INT{intel} DEX{dex}", sheet = new CharacterSheet(sex, str, intel, dex, seed++) });
        for (int i = 0; i < randomSeeds; i++)
        {
            var sh = CharacterSheet.Roll(77000 + i * 13, 0f, 0f, 0f, 5f);
            list.Add(new Case { label = $"rnd {sh}", sheet = sh });
        }
        return list;
    }

    // Checks one built body. Returns the number of bad vertices; `detail` describes the worst.
    public static int Check(BodyAsset a, out string detail)
    {
        detail = "";
        var plan = a.plan;
        int hi = plan.Index("Head"), ni = plan.Index("Neck");
        var c = HeadCanon.From(plan, hi, plan.JointPos(hi), Vector3.right, Vector3.up, Vector3.forward);
        using var sdf = new BodySDF(plan);
        var k = sdf.Kernel;
        Vector3 neckA = plan.JointPos(ni), neckB = plan.JointPos(hi);
        var nb = plan.bones[ni];
        float neckR = Mathf.Max(nb.Outer.x, Mathf.Max(nb.Outer.y, nb.Outer.z));
        var hb = plan.bones[hi];
        float headFat = Mathf.Max(hb.fat.x, Mathf.Max(hb.fat.y, hb.fat.z)) * 1.7f;
        Vector3 cc = sdf.SkullCentre, cr = sdf.SkullRadii;   // the head's egg (with INT's growth)
        float top = cc.y + cr.y + 0.02f + headFat * 0.5f, back = cc.z - cr.z * 1.1f - 0.02f - headFat * 0.5f;
        var v = a.head.vertices;
        int bad = 0, offSurface = 0; float worst = 0f; Vector3 worstP = default; string worstWhy = "";
        foreach (var p in v)
        {
            Vector3 d = p - c.o;
            string why = null; float excess = 0f;
            void Over(string w, float e) { if (e > excess) { excess = e; why = w; } }
            // Wide: the head's width (+ the neck's fat below the mouth: jowls and a heavy neck may be wider).
            Over("wide", Mathf.Abs(d.x) - (c.halfW * 1.3f + 0.01f + (d.y < c.mouth ? headFat : headFat * 0.4f)));
            Over("above the cranium", p.y - top);
            // Behind: the cranium's back (below the skull's middle the neck and a heavy nape may reach further).
            Over("behind the cranium", back - p.z - (p.y < cc.y ? 0.02f + headFat : 0f));
            Over("in front of the face", d.z - c.halfD * 1.6f);
            if (d.y < c.chin)
            {
                // Below the chin line: the neck tube, or the under-chin (a few cm down, in front of the neck).
                Vector3 ab = neckB - neckA;
                float t = Mathf.Clamp01(Vector3.Dot(p - neckA, ab) / ab.sqrMagnitude);
                Vector3 q = p - (neckA + ab * t); q.y = 0f;
                float tube = q.magnitude - (neckR * 1.35f + 0.012f);
                float underChin = Mathf.Max(c.chin - d.y - (0.03f + headFat), -d.z);
                // (Body surface the head's grid reaches - the trapezius, the collar - is the body's, not the face's.)
                bool body = k.bones[k.Eval(p).nearest].kind != (int)BoneKind.Head;
                if (!body) Over("below the chin, off the neck", Mathf.Min(tube, underChin));
            }
            // Far from every shape: blend inflation. The skin sits the local fat out from the muscle surface,
            // plus at most one smooth-min push (a quarter of the fat blend radius) in a concave corner.
            var smp = k.Eval(p);
            if (Mathf.Abs(smp.skin) > 0.008f) { offSurface++; continue; }   // a vertex off the SDF surface: the head / body seam (Part 7)
            float shapes = smp.muscle;
            for (int j = 0; j < k.breasts.Length; j++) shapes = Mathf.Min(shapes, BodySdfKernel.EllipsoidDist(k.breasts[j].ToLocal(p), k.breasts[j].r));
            if (k.earL.on != 0) shapes = Mathf.Min(shapes, Mathf.Min(BodySdfKernel.EarDist(k.earL, p), BodySdfKernel.EarDist(k.earR, p)));
            float kFat = Mathf.Min(sdf.fatBlendMin + sdf.fatBlendPerMetre * smp.fat, sdf.fatBlendMax);
            Over("far from every shape", shapes - (smp.fat + kFat * 0.25f + 0.01f));
            if (why == null) continue;
            bad++;
            if (excess > worst) { worst = excess; worstP = p; worstWhy = why; }
        }
        if (offSurface > 0) detail = $"(warn: {offSurface} vertices off the SDF surface, the head / body seam) ";
        if (bad > 0)
        {
            Vector3 d = worstP - c.o;
            var reg = new List<Vector2>(); a.head.GetUVs(1, reg);
            var kinds = new Dictionary<int, int>(); int low = 0;
            for (int i = 0; i < v.Length; i++) if (v[i].y < c.o.y + c.chin - 0.1f) { low++; int kk = reg.Count > i ? (int)reg[i].x : -1; kinds.TryGetValue(kk, out int cnt); kinds[kk] = cnt + 1; }
            var ks = new StringBuilder(); foreach (var kv in kinds) ks.Append($"{(BoneKind)kv.Key} {kv.Value} ");
            detail += $"[{low} vertices > 10 cm under the chin: {ks}] {bad} of {v.Length} vertices; worst {worstWhy} by {worst * 100f:0.0} cm at face ({d.x * 100f:0.0}, {d.y * 100f:0.0}, {d.z * 100f:0.0}) cm; " +
                     $"face fat mask {k.FaceFatMask(worstP):0.00}, head fat {headFat * 100f:0.0} cm; {sdf.DescribePoint(worstP)}";
        }
        return bad;
    }

    static void Free(Object o) { if (o == null) return; if (Application.isPlaying) Object.Destroy(o); else Object.DestroyImmediate(o); }

    // Builds every case and checks it. True when all pass.
    public static bool Run(out string report, int randomSeeds = 20, string only = null)
    {
        var sb = new StringBuilder();
        int failed = 0, n = 0;
        foreach (var cs in Cases(randomSeeds))
        {
            if (only != null && !cs.label.Contains(only)) continue;
            Debug.Log($"face sanity: building {cs.label}");
            var a = new BodyBuild(cs.sheet).RunSync();
            int bad = Check(a, out string detail);
            n++;
            if (bad > 0) { failed++; sb.AppendLine($"FAIL {cs.label}: {detail}"); }
            else sb.AppendLine($"ok   {cs.label} {detail}");
            Free(a.body); Free(a.head); Free(a.wig);
        }
        sb.AppendLine(failed == 0 ? $"PASS face sanity: {n} heads inside their bounds" : $"FAIL face sanity: {failed} of {n} heads have surface outside their bounds");
        report = sb.ToString();
        return failed == 0;
    }
}
