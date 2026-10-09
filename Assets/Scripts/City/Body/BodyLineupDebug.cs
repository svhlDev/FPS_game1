using System.Collections.Generic;
using UnityEngine;

// Debug lineup of generated skeletons: bones as lines, girth profiles as rings at the start, middle and
// end of each bone: red = muscle surface, orange = skin (muscle + fat), so the gap between them is the
// fat. In rows. Gizmos in the editor (Tools > Characters > Body Lineup adds one to the scene);
// in play mode it builds the real bone hierarchies and also draws them as line meshes, so they show
// in the game view and in builds.
//   Row 0: individuality (4 seeds per sex, average sheet)
//   Rows 1-3: STR 1 / 10 / 20, columns DEX 1 / 10 / 20 (male left, female right), INT 10
//   Row 4: INT sweep 1, 5, 10, 15, 20 (male then female)
//   Row 5: body plan rules (0, 1, 3, 4, 5 arms)
// previewMeshes shows the generated body mesh (BodySDF -> BodyMesher, A-pose bind pose) instead of the
// rings: built one per frame in play mode; in the editor use the component's context menu
// "Build preview meshes" (they draw as gizmos).
// previewOutfit (Clothing Phase 1 check): the STR / DEX rows also get a T-shirt and pants, each as its
// garment field's outer solid, quick-meshed in the garment's colour.
public class BodyLineupDebug : MonoBehaviour
{
    public float spacing = 1.0f;
    public float rowHeight = 2.2f;
    public int baseSeed = 1000;
    public bool drawGirth = true;
    public bool previewMeshes = true;
    public float meshCell = 0.015f;
    public int meshTriangles = 10000;
    public Color skinColor = new Color(0.82f, 0.64f, 0.52f);
    public bool previewOutfit = true;
    public int[] outfitRows = { 1, 2, 3 };
    public float garmentCell = 0.006f;
    public float LastMeshMs { get; private set; }
    public int MeshesBuilt { get; private set; }
    public readonly List<(string label, BodyMesher.Result result)> MeshStats = new List<(string, BodyMesher.Result)>();
    readonly List<(Mesh mesh, Vector3 offset)> editorMeshes = new List<(Mesh, Vector3)>();
    readonly List<(Mesh mesh, Vector3 offset, Color color)> editorGarments = new List<(Mesh, Vector3, Color)>();

    public struct Entry { public string label; public BodyPlan plan; public Vector3 offset; public int row; }
    public readonly List<Entry> Entries = new List<Entry>();
    public static readonly string[] RowNames = { "seed variation", "STR 1  (DEX 1/10/20)", "STR 10 (DEX 1/10/20)", "STR 20 (DEX 1/10/20)", "INT 1-20", "arms 0/1/3/4/5" };
    static readonly int[] Sweep = { 1, 5, 10, 15, 20 };

    public void Generate()
    {
        Entries.Clear();
        var rules = BodyRules.Default;
        void Add(int row, int col, string label, CharacterSheet sheet, ISpeciesTemplate t = null) =>
            Entries.Add(new Entry { label = label, plan = BodyPlanner.Generate(sheet, rules, t), offset = new Vector3(-col * spacing, -row * rowHeight, 0f), row = row });

        for (int i = 0; i < 4; i++)
        {
            Add(0, i, $"M #{i}", new CharacterSheet(Sex.Male, 10, 10, 10, baseSeed + i));
            Add(0, i + 5, $"F #{i}", new CharacterSheet(Sex.Female, 10, 10, 10, baseSeed + i));
        }
        int[] grid = { 1, 10, 20 };
        for (int r = 0; r < 3; r++)
            for (int k = 0; k < 3; k++)
            {
                Add(1 + r, k, $"M S{grid[r]} D{grid[k]}", new CharacterSheet(Sex.Male, grid[r], 10, grid[k], baseSeed));
                Add(1 + r, k + 4, $"F S{grid[r]} D{grid[k]}", new CharacterSheet(Sex.Female, grid[r], 10, grid[k], baseSeed));
            }
        for (int k = 0; k < Sweep.Length; k++)
        {
            Add(4, k, $"M INT {Sweep[k]}", new CharacterSheet(Sex.Male, 10, Sweep[k], 10, baseSeed));
            Add(4, k + Sweep.Length + 1, $"F INT {Sweep[k]}", new CharacterSheet(Sex.Female, 10, Sweep[k], 10, baseSeed));
        }
        int c = 0;
        foreach (int arms in new[] { 0, 1, 3, 4, 5 })
            Add(5, c++, $"{arms} arms", new CharacterSheet(Sex.Male, 10, 10, 10, baseSeed + 7), new HumanTemplate { armCount = arms });
    }

    // ---------- play mode: real hierarchies + line meshes ----------

    readonly List<BodySkeleton> skeletons = new List<BodySkeleton>();
    Mesh boneLines, muscleLines, skinLines;

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
        if (previewMeshes) { drawGirth = false; StartCoroutine(BuildMeshes()); }
        boneLines = LineObject("Bones", new Color(0.3f, 0.9f, 1f) * 2f);
        muscleLines = LineObject("Muscle", new Color(1f, 0.15f, 0.12f) * 1.6f);
        skinLines = LineObject("Skin", new Color(1f, 0.75f, 0.35f) * 1.2f);
    }

    System.Collections.IEnumerator BuildMeshes()
    {
        var mat = CharacterFigure.Mat(skinColor);
        foreach (var e in Entries)
        {
            var (mesh, r) = BuildMesh(e.plan);
            MeshStats.Add((e.label, r));
            var go = new GameObject("Mesh " + e.label);
            go.transform.SetParent(transform, false);
            go.transform.localPosition = e.offset;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            MeshesBuilt++;
            if (Dressed(e))
                foreach (var (gm, color) in BuildGarments(e.plan))
                {
                    var g = new GameObject("Garment " + gm.name);
                    g.transform.SetParent(go.transform, false);
                    g.AddComponent<MeshFilter>().sharedMesh = gm;
                    g.AddComponent<MeshRenderer>().sharedMaterial = CharacterFigure.Mat(color);
                }
            yield return null;
        }
    }

    bool Dressed(Entry e) => previewOutfit && System.Array.IndexOf(outfitRows, e.row) >= 0;

    // The casual outfit's garments as quick debug meshes (outer solids).
    public List<(Mesh mesh, Color color)> BuildGarments(BodyPlan plan, Outfit outfit = null)
    {
        outfit ??= Outfit.Casual();
        var list = new List<(Mesh, Color)>();
        using var sdf = new BodySDF(plan);
        foreach (var g in outfit.garments)
        {
            using var field = new GarmentField(sdf, g);
            list.Add((field.BuildDebugMesh(garmentCell), g.color));
        }
        return list;
    }

    public (Mesh mesh, BodyMesher.Result r) BuildMesh(BodyPlan plan)
    {
        using var sdf = new BodySDF(plan);
        var r = BodyMesher.Build(sdf, new BodyMesher.Settings { cell = meshCell, targetTriangles = meshTriangles });
        LastMeshMs = r.totalMs;
        return (r.mesh, r);
    }

    [ContextMenu("Build preview meshes")]
    void BuildEditorMeshes()
    {
        Generate();
        editorMeshes.Clear(); editorGarments.Clear();
        foreach (var e in Entries)
        {
            editorMeshes.Add((BuildMesh(e.plan).mesh, e.offset));
            if (Dressed(e)) foreach (var (m, c) in BuildGarments(e.plan)) editorGarments.Add((m, e.offset, c));
        }
    }

    [ContextMenu("Clear preview meshes")]
    void ClearEditorMeshes() { editorMeshes.Clear(); editorGarments.Clear(); }

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
        var bv = new List<Vector3>(); var mv = new List<Vector3>(); var sv = new List<Vector3>();
        foreach (var sk in skeletons)
            foreach (var b in sk.Bones)
            {
                bv.Add(transform.InverseTransformPoint(b.Start)); bv.Add(transform.InverseTransformPoint(b.End));
                if (!drawGirth) continue;
                AddCircles(mv, b.Start, b.Dir, b.spec.length, b.spec.MuscleSurface, p => transform.InverseTransformPoint(p));
                AddCircles(sv, b.Start, b.Dir, b.spec.length, b.spec.Outer, p => transform.InverseTransformPoint(p));
            }
        Fill(boneLines, bv);
        Fill(muscleLines, mv);
        Fill(skinLines, sv);
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
        if (editorMeshes.Count > 0)
        {
            Gizmos.color = skinColor;
            foreach (var (m, off) in editorMeshes) Gizmos.DrawMesh(m, transform.TransformPoint(off), transform.rotation);
            foreach (var (m, off, c) in editorGarments) { Gizmos.color = c; Gizmos.DrawMesh(m, transform.TransformPoint(off), transform.rotation); }
        }
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
                AddCircles(lines, s, (en - s).normalized, plan.bones[i].length, plan.bones[i].MuscleSurface, p => p);
                Gizmos.color = new Color(1f, 0.2f, 0.15f, 0.8f);
                for (int k = 0; k < lines.Count; k += 2) Gizmos.DrawLine(lines[k], lines[k + 1]);
                lines.Clear();
                AddCircles(lines, s, (en - s).normalized, plan.bones[i].length, plan.bones[i].Outer, p => p);
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

    void OnValidate() { Entries.Clear(); editorMeshes.Clear(); editorGarments.Clear(); }
}
