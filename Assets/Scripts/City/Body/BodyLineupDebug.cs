using System.Collections.Generic;
using UnityEngine;

// Debug lineup of generated skeletons: bones as lines, girth profiles as circles (start, middle, end of
// each bone), in rows. Gizmos in the editor (Tools > Characters > Body Lineup adds one to the scene);
// in play mode it builds the real bone hierarchies and also draws them as line meshes, so they show
// in the game view and in builds.
//   Row 0: individuality (4 seeds per sex, average sheet)
//   Rows 1-3: STR / INT / DEX sweeps 1, 5, 10, 15, 20 (male then female)
//   Row 4: body plan rules (0, 1, 3, 4, 5 arms)
public class BodyLineupDebug : MonoBehaviour
{
    public float spacing = 0.9f;
    public float rowHeight = 2.2f;
    public int baseSeed = 1000;
    public bool drawGirth = true;

    public struct Entry { public string label; public BodyPlan plan; public Vector3 offset; }
    public readonly List<Entry> Entries = new List<Entry>();
    public static readonly string[] RowNames = { "seed variation", "STR 1-20", "INT 1-20", "DEX 1-20", "arms 0/1/3/4/5" };
    static readonly int[] Sweep = { 1, 5, 10, 15, 20 };

    public void Generate()
    {
        Entries.Clear();
        var rules = BodyRules.Default;
        void Add(int row, int col, string label, CharacterSheet sheet, ISpeciesTemplate t = null) =>
            Entries.Add(new Entry { label = label, plan = BodyPlanner.Generate(sheet, rules, t), offset = new Vector3(-col * spacing, -row * rowHeight, 0f) });

        for (int i = 0; i < 4; i++)
        {
            Add(0, i, $"M #{i}", new CharacterSheet(Sex.Male, 10, 10, 10, baseSeed + i));
            Add(0, i + 5, $"F #{i}", new CharacterSheet(Sex.Female, 10, 10, 10, baseSeed + i));
        }
        for (int k = 0; k < Sweep.Length; k++)
            for (int sx = 0; sx < 2; sx++)
            {
                var sex = sx == 0 ? Sex.Male : Sex.Female;
                int col = k + sx * (Sweep.Length + 1), v = Sweep[k];
                Add(1, col, $"STR {v}", new CharacterSheet(sex, v, 10, 10, baseSeed));
                Add(2, col, $"INT {v}", new CharacterSheet(sex, 10, v, 10, baseSeed));
                Add(3, col, $"DEX {v}", new CharacterSheet(sex, 10, 10, v, baseSeed));
            }
        int c = 0;
        foreach (int arms in new[] { 0, 1, 3, 4, 5 })
            Add(4, c++, $"{arms} arms", new CharacterSheet(Sex.Male, 10, 10, 10, baseSeed + 7), new HumanTemplate { armCount = arms });
    }

    // ---------- play mode: real hierarchies + line meshes ----------

    readonly List<BodySkeleton> skeletons = new List<BodySkeleton>();
    Mesh boneLines, girthLines;

    void Start()
    {
        if (!Application.isPlaying) return;
        Generate();
        foreach (var e in Entries)
        {
            var root = new GameObject("Body " + e.label).transform;
            root.SetParent(transform, false);
            root.localPosition = e.offset;
            skeletons.Add(BodySkeleton.Build(root, e.plan));
        }
        boneLines = LineObject("Bones", new Color(0.3f, 0.9f, 1f) * 2f);
        girthLines = LineObject("Girth", new Color(1f, 0.75f, 0.35f) * 1.2f);
    }

    Mesh LineObject(string name, Color c)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);
        var mesh = new Mesh { name = name };
        mesh.MarkDynamic();
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var mat = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
        mat.SetColor("_BaseColor", c);
        var r = go.AddComponent<MeshRenderer>();
        r.sharedMaterial = mat;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        return mesh;
    }

    void LateUpdate()
    {
        if (boneLines == null) return;
        var bv = new List<Vector3>(); var gv = new List<Vector3>();
        foreach (var sk in skeletons)
            foreach (var b in sk.Bones)
            {
                bv.Add(transform.InverseTransformPoint(b.Start)); bv.Add(transform.InverseTransformPoint(b.End));
                if (drawGirth) AddCircles(gv, b.Start, b.Dir, b.spec.length, b.spec.girth, p => transform.InverseTransformPoint(p));
            }
        Fill(boneLines, bv);
        Fill(girthLines, gv);
    }

    static void Fill(Mesh m, List<Vector3> v)
    {
        m.Clear();
        m.SetVertices(v);
        var idx = new int[v.Count];
        for (int i = 0; i < idx.Length; i++) idx[i] = i;
        m.SetIndices(idx, MeshTopology.Lines, 0);
        m.RecalculateBounds();
    }

    // Three rings per bone (start, middle, end), perpendicular to it.
    static void AddCircles(List<Vector3> v, Vector3 start, Vector3 dir, float length, Vector3 girth, System.Func<Vector3, Vector3> xf)
    {
        Vector3 a = Vector3.Cross(dir, Mathf.Abs(dir.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
        Vector3 b = Vector3.Cross(dir, a);
        const int seg = 14;
        for (int k = 0; k < 3; k++)
        {
            Vector3 c = start + dir * length * (k * 0.5f);
            float r = girth[k];
            for (int i = 0; i < seg; i++)
            {
                float t0 = i * Mathf.PI * 2f / seg, t1 = (i + 1) * Mathf.PI * 2f / seg;
                v.Add(xf(c + (a * Mathf.Cos(t0) + b * Mathf.Sin(t0)) * r));
                v.Add(xf(c + (a * Mathf.Cos(t1) + b * Mathf.Sin(t1)) * r));
            }
        }
    }

    // ---------- editor: gizmos straight from the plans ----------

    void OnDrawGizmos()
    {
        if (Application.isPlaying) return;
        if (Entries.Count == 0) Generate();
        var lines = new List<Vector3>();
        foreach (var e in Entries)
        {
            Vector3 o = transform.TransformPoint(e.offset);
            var plan = e.plan;
            for (int i = 0; i < plan.bones.Count; i++)
            {
                Vector3 s = o + plan.JointPos(i), en = o + plan.BoneEnd(i);
                Gizmos.color = plan.bones[i].kind == BoneKind.Hand || plan.bones[i].kind == BoneKind.Foot ? Color.yellow : Color.cyan;
                Gizmos.DrawLine(s, en);
                if (!drawGirth) continue;
                lines.Clear();
                AddCircles(lines, s, (en - s).normalized, plan.bones[i].length, plan.bones[i].girth, p => p);
                Gizmos.color = new Color(1f, 0.75f, 0.35f, 0.8f);
                for (int k = 0; k < lines.Count; k += 2) Gizmos.DrawLine(lines[k], lines[k + 1]);
            }
#if UNITY_EDITOR
            UnityEditor.Handles.Label(o + Vector3.up * (plan.height + 0.15f), e.label);
#endif
        }
#if UNITY_EDITOR
        for (int r = 0; r < RowNames.Length; r++)
            UnityEditor.Handles.Label(transform.TransformPoint(new Vector3(1.2f, -r * rowHeight + 0.8f, 0f)), RowNames[r]);
#endif
    }

    void OnValidate() => Entries.Clear();
}
