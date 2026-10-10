using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// Laser burn marks and the lethal burn chain.
// Marks: every laser hit leaves a small quad (0.06 m stun, 0.09 m lethal) flat on the surface, parented
// to what was hit (building, car, body part; it moves with it). It glows in the beam colour, cools to a
// dull orange ember over 3 s, then to a dark scorch over the next 10 s. Drawn instanced, one batch per
// material state (the cooling is quantised into steps). A mark goes once it is older than 5 minutes
// and out of view for 2 s (frustum plus an occlusion line, checked round robin), or when what it sits on
// is gone or pooled. Pool of 300: a new mark when full recycles the oldest out of view (or the oldest).
// Burn chain (lethal only): each target (a character or a car) remembers its first hit's spot, in the
// hit part's local space, and time. A hit within 0.25 m of that spot and 3 s of the first hit continues
// the chain, anything else starts a new one. Ignition chance per chain hit: 5, 15, 30, 55, 100%.
// Chain hits share one mark that grows and glows brighter with each, so the spot visibly heats up.
[DefaultExecutionOrder(205)]
public class BurnMarks : MonoBehaviour
{
    public const int MaxMarks = 300;
    public const float StunSize = 0.06f, LethalSize = 0.09f;
    public const float GlowTime = 3f, CoolTime = 10f;
    public const float ChainRadius = 0.25f, ChainTime = 3f;
    public static readonly float[] IgniteChance = { 0.05f, 0.15f, 0.30f, 0.55f, 1f };

    class Mark
    {
        public Transform parent;
        public Vector3 localPos, localNormal;
        public float size, born, lastSeen;
        public bool stun;
        public int heat;          // chain hits (brightness)
        public bool live;
    }

    class Chain { public Transform part; public Vector3 local; public float time; public int count; public Mark mark; }

    static BurnMarks instance;
    readonly List<Mark> marks = new List<Mark>(MaxMarks);
    readonly Dictionary<Object, Chain> chains = new Dictionary<Object, Chain>();
    // Material states: 0 stun glow, 1..5 lethal glow by heat, then GlowSteps ember steps, CoolSteps
    // scorch steps.
    const int HeatLevels = 5, GlowSteps = 4, CoolSteps = 4;
    Material[] mats;
    List<Matrix4x4>[] batches;
    Mesh quad;
    int checkCursor;
    readonly Plane[] frustum = new Plane[6];
    static readonly RaycastHit[] occl = new RaycastHit[4];

    public static BurnMarks I => instance != null ? instance : instance = new GameObject("BurnMarks").AddComponent<BurnMarks>();
    public static int LiveCount { get { int n = 0; if (instance != null) foreach (var m in instance.marks) if (m.live) n++; return n; } }
    public static int Ignitions;

    void Awake()
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
        quad = go.GetComponent<MeshFilter>().sharedMesh;
        Destroy(go);
        int n = 1 + HeatLevels + GlowSteps + CoolSteps;
        mats = new Material[n];
        batches = new List<Matrix4x4>[n];
        for (int i = 0; i < n; i++) batches[i] = new List<Matrix4x4>(64);
        Color ember = new Color(1f, 0.38f, 0.08f) * 1.3f, scorch = new Color(0.035f, 0.03f, 0.025f);
        mats[0] = Unlit(LaserWeapon.StunColor * 5f);
        for (int h = 0; h < HeatLevels; h++) mats[1 + h] = Unlit(LaserWeapon.LethalColor * (4f + 2.5f * h));
        for (int g = 0; g < GlowSteps; g++) mats[1 + HeatLevels + g] = Unlit(Color.Lerp(LaserWeapon.LethalColor * 3f, ember, (g + 1f) / GlowSteps));
        for (int c = 0; c < CoolSteps; c++) mats[1 + HeatLevels + GlowSteps + c] = Unlit(Color.Lerp(ember * 0.6f, scorch, (c + 1f) / CoolSteps));
    }

    static Material Unlit(Color c)
    {
        var m = new Material(Shader.Find("Universal Render Pipeline/Unlit")) { enableInstancing = true, name = "Burn" };
        m.SetColor("_BaseColor", c);
        return m;
    }

    void OnEnable() => RenderPipelineManager.beginCameraRendering += Draw;
    void OnDisable() => RenderPipelineManager.beginCameraRendering -= Draw;

    // ---------- hits ----------

    // A laser hit `h` (stun or lethal) from direction `dir`. Leaves (or grows) a mark; lethal hits feed
    // the target's burn chain and may set it on fire. Returns true if this hit ignited the target.
    public static bool Hit(RaycastHit h, Vector3 dir, Weapon.Mode mode)
    {
        if (h.collider == null) return false;
        var b = I;
        bool stun = mode == Weapon.Mode.Stun;
        Transform surface = h.collider.transform;
        if (stun) { b.Add(surface, h.point, h.normal, StunSize, true, 0); return false; }

        // The target: a character (by its health) or a car.
        Object target = null;
        var ch = h.collider.GetComponentInParent<CharacterHealth>();
        var car = ch == null ? h.collider.GetComponentInParent<FlyingVehicle>() : null;
        var corpse = ch == null && car == null ? h.collider.GetComponentInParent<Ragdoll>() : null;
        if (ch != null) target = ch; else if (car != null) target = car; else if (corpse != null) target = corpse;
        if (target == null) { b.Add(surface, h.point, h.normal, LethalSize, false, 0); return false; }

        Chain c = null;
        if (b.chains.TryGetValue(target, out var prev) && prev.part == surface && prev.part != null && Time.time - prev.time <= ChainTime
            && Vector3.Distance(prev.part.TransformPoint(prev.local), h.point) <= ChainRadius && prev.mark != null && prev.mark.live)
        {
            c = prev;
            c.count++;
            var m = c.mark;
            m.heat = Mathf.Min(HeatLevels - 1, c.count - 1);
            m.size = LethalSize * (1f + 0.35f * (c.count - 1));
            m.born = Time.time;   // glowing again
            m.lastSeen = Time.time;
        }
        else
        {
            c = new Chain { part = surface, local = surface.InverseTransformPoint(h.point), time = Time.time, count = 1 };
            c.mark = b.Add(surface, h.point, h.normal, LethalSize, false, 0);
            b.chains[target] = c;
        }
        float chance = IgniteChance[Mathf.Clamp(c.count - 1, 0, IgniteChance.Length - 1)];
        if (Random.value < chance)
        {
            Ignite(target);
            b.chains.Remove(target);
            return true;
        }
        return false;
    }

    // A mark only (the police car's continuous beam: one every so often while it burns a surface).
    public static void MarkOnly(RaycastHit h, Weapon.Mode mode)
    {
        if (h.collider == null) return;
        bool stun = mode == Weapon.Mode.Stun;
        I.Add(h.collider.transform, h.point, h.normal, stun ? StunSize : LethalSize, stun, 0);
    }

    static void Ignite(Object target)
    {
        Ignitions++;
        if (target is FlyingVehicle car) { if (car.Health != null) car.Health.Ignite(); return; }
        if (target is CharacterHealth ch)
        {
            var f = ch.GetComponent<Flammable>();
            if (f != null) f.Ignite();
            else { var fpc = ch.GetComponent<FirstPersonController>(); if (fpc != null) fpc.Ignite(); }
        }
    }

    // Chain length on a target right now (tests).
    public static int ChainCount(Object target) => instance != null && instance.chains.TryGetValue(target, out var c) ? c.count : 0;

    Mark Add(Transform parent, Vector3 point, Vector3 normal, float size, bool stun, int heat)
    {
        Mark m = null;
        foreach (var x in marks) if (!x.live) { m = x; break; }
        if (m == null && marks.Count < MaxMarks) { m = new Mark(); marks.Add(m); }
        if (m == null)
        {
            // Full: the oldest out of view, else the oldest.
            Camera cam = Camera.main;
            if (cam != null) GeometryUtility.CalculateFrustumPlanes(cam, frustum);
            foreach (var x in marks) if ((cam == null || !InFrustum(x)) && (m == null || x.born < m.born)) m = x;
            if (m == null) foreach (var x in marks) if (m == null || x.born < m.born) m = x;
            foreach (var kv in chains) if (kv.Value.mark == m) kv.Value.mark = null;
        }
        m.parent = parent;
        m.localPos = parent.InverseTransformPoint(point + normal * 0.004f);
        m.localNormal = parent.InverseTransformDirection(normal);
        m.size = size; m.stun = stun; m.heat = heat;
        m.born = m.lastSeen = Time.time;
        m.live = true;
        return m;
    }

    bool InFrustum(Mark m)
    {
        if (m.parent == null) return false;
        Vector3 p = m.parent.TransformPoint(m.localPos);
        return GeometryUtility.TestPlanesAABB(frustum, new Bounds(p, Vector3.one * m.size));
    }

    // ---------- life and visibility ----------

    void Update()
    {
        var cam = Camera.main;
        if (cam != null) GeometryUtility.CalculateFrustumPlanes(cam, frustum);
        Vector3 cp = cam != null ? cam.transform.position : Vector3.zero;
        int n = marks.Count;
        // Visibility, round robin: 40 marks a frame (an occlusion line each when in the frustum).
        for (int k = 0; k < Mathf.Min(40, n); k++)
        {
            checkCursor = (checkCursor + 1) % n;
            var m = marks[checkCursor];
            if (!m.live || m.parent == null || cam == null) continue;
            Vector3 p = m.parent.TransformPoint(m.localPos);
            if ((p - cp).sqrMagnitude > 400f * 400f || !InFrustum(m)) continue;
            Vector3 to = p - cp; float d = to.magnitude;
            bool blocked = false;
            int hits = Physics.RaycastNonAlloc(cp, to / d, occl, d - 0.05f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < hits && !blocked; i++)
                if (!occl[i].collider.transform.IsChildOf(m.parent) && m.parent != occl[i].collider.transform) blocked = true;
            if (!blocked) m.lastSeen = Time.time;
        }
        foreach (var m in marks)
        {
            if (!m.live) continue;
            if (m.parent == null || !m.parent.gameObject.activeInHierarchy) { m.live = false; continue; }
            if (Time.time - m.born > OffscreenTimer.Lifetime && Time.time - m.lastSeen > OffscreenTimer.HiddenTime) m.live = false;
        }
    }

    void Draw(ScriptableRenderContext ctx, Camera cam)
    {
        if (cam.cameraType == CameraType.Preview || cam.cameraType == CameraType.Reflection) return;
        foreach (var b in batches) b.Clear();
        float now = Time.time;
        foreach (var m in marks)
        {
            if (!m.live || m.parent == null) continue;
            float age = now - m.born;
            int state;
            if (age < GlowTime * 0.25f) state = m.stun ? 0 : 1 + m.heat;
            else if (age < GlowTime) state = 1 + HeatLevels + Mathf.Clamp((int)((age - GlowTime * 0.25f) / (GlowTime * 0.75f) * GlowSteps), 0, GlowSteps - 1);
            else state = 1 + HeatLevels + GlowSteps + Mathf.Clamp((int)((age - GlowTime) / CoolTime * CoolSteps), 0, CoolSteps - 1);
            Vector3 p = m.parent.TransformPoint(m.localPos);
            Vector3 nrm = m.parent.TransformDirection(m.localNormal).normalized;
            if (nrm.sqrMagnitude < 0.5f) nrm = Vector3.up;
            // The quad faces -Z: look along -normal so its front faces out of the surface.
            var rot = Quaternion.LookRotation(-nrm, Mathf.Abs(nrm.y) > 0.9f ? Vector3.forward : Vector3.up);
            batches[state].Add(Matrix4x4.TRS(p, rot, new Vector3(m.size, m.size, 1f)));
        }
        for (int i = 0; i < batches.Length; i++)
        {
            var b = batches[i];
            if (b.Count == 0) continue;
            var rp = new RenderParams(mats[i]) { camera = cam, shadowCastingMode = ShadowCastingMode.Off, receiveShadows = false,
                                                 worldBounds = new Bounds(Vector3.zero, Vector3.one * 100000f) };
            Graphics.RenderMeshInstanced(rp, quad, 0, b, b.Count, 0);
        }
    }

    // Test hooks.
    public static float OldestAge { get { float a = 0f; if (instance != null) foreach (var m in instance.marks) if (m.live) a = Mathf.Max(a, Time.time - m.born); return a; } }
    public static void DebugAge(float seconds) { if (instance != null) foreach (var m in instance.marks) if (m.live) { m.born -= seconds; } }
    public static void DebugHideAll(float seconds) { if (instance != null) foreach (var m in instance.marks) if (m.live) m.lastSeen -= seconds; }
}
