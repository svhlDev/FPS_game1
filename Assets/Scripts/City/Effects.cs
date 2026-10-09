using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// Placeholder visual effects, all drawn instanced per camera (no Light components, no GameObjects).
// Glows are emissive unlit colours bright enough to bloom:
//   Puff     : grey smoke cubes that rise, grow and shrink away.
//   Debris   : dark tumbling boxes thrown by explosions (no colliders).
//   Flash    : a short additive fireball sphere (explosions).
//   Ring     : an expanding additive shockwave ring.
//   Glow     : a short flash sphere in a laser mode colour (T-gun muzzle and impact).
//   Flame    : an immediate-mode flickering flame billboard for this frame (burning cars, fire patches,
//              chunks). Call every frame while it should show.
//   Scorch   : an immediate-mode dark decal flat on a surface for this frame.
// Created on first use.
[DefaultExecutionOrder(200)]
public class Effects : MonoBehaviour
{
    struct Particle { public Vector3 pos, vel, spin; public float age, life, size; public byte kind; }
    const byte PuffK = 0, DebrisK = 1, FlashK = 2, RingK = 3, StunK = 4, LethalK = 5;

    static Effects instance;
    readonly List<Particle> particles = new List<Particle>(1024);
    readonly List<Matrix4x4> puffM = new List<Matrix4x4>(), debrisM = new List<Matrix4x4>(), flashM = new List<Matrix4x4>(), ringM = new List<Matrix4x4>(),
        stunM = new List<Matrix4x4>(), lethalM = new List<Matrix4x4>();
    // Immediate-mode requests for this frame.
    readonly List<Vector4> flames = new List<Vector4>(1024);   // xyz pos, w size
    readonly List<Vector4> scorches = new List<Vector4>(256);   // xyz pos, w radius
    readonly List<Matrix4x4> flameM = new List<Matrix4x4>(1024), scorchM = new List<Matrix4x4>(256);
    int requestFrame = -1;
    Mesh cube, quad, sphere, ring;
    Material puffMat, debrisMat, flashMat, flameMat, emberMat, scorchMat, stunMat, lethalMat;

    static Effects I
    {
        get
        {
            if (instance == null) instance = new GameObject("Effects").AddComponent<Effects>();
            return instance;
        }
    }

    public static void Puff(Vector3 pos, Vector3 vel, float size, float life) => I.Add(pos, vel, size, life, PuffK);
    public static void Debris(Vector3 pos, Vector3 vel, float size) => I.Add(pos, vel, size, Random.Range(1.2f, 2.2f), DebrisK);
    public static void Flash(Vector3 pos, float radius) => I.Add(pos, Vector3.zero, radius, 0.35f, FlashK);
    public static void Ring(Vector3 pos, float radius) => I.Add(pos, Vector3.zero, radius, 0.5f, RingK);
    public static void Glow(Vector3 pos, float radius, bool stun, float life = 0.12f) => I.Add(pos, Vector3.zero, radius, life, stun ? StunK : LethalK);

    public static void Flame(Vector3 pos, float size)
    {
        var e = I;
        e.BeginRequests();
        if (e.flames.Count < 4000) e.flames.Add(new Vector4(pos.x, pos.y, pos.z, size));
    }

    public static void Scorch(Vector3 pos, float radius)
    {
        var e = I;
        e.BeginRequests();
        if (e.scorches.Count < 1000) e.scorches.Add(new Vector4(pos.x, pos.y, pos.z, radius));
    }

    void BeginRequests()
    {
        if (requestFrame == Time.frameCount) return;
        requestFrame = Time.frameCount;
        flames.Clear(); scorches.Clear();
    }

    void Add(Vector3 pos, Vector3 vel, float size, float life, byte kind)
    {
        if (particles.Count >= 2000) return;
        particles.Add(new Particle { pos = pos, vel = vel, size = size, life = life, kind = kind, spin = Random.insideUnitSphere * 360f });
    }

    void Awake()
    {
        cube = Prim(PrimitiveType.Cube);
        sphere = Prim(PrimitiveType.Sphere);
        quad = Prim(PrimitiveType.Quad);
        ring = RingMesh();
        puffMat = Unlit(new Color(0.26f, 0.26f, 0.28f));   // reads as smoke at night (lit grey goes black)
        debrisMat = Lit(new Color(0.06f, 0.06f, 0.06f));
        scorchMat = Lit(new Color(0.03f, 0.025f, 0.02f));
        flashMat = Unlit(new Color(1f, 0.75f, 0.35f) * 4f);
        flameMat = Unlit(new Color(1f, 0.42f, 0.08f) * 3f);
        emberMat = flameMat;
        stunMat = Unlit(new Color(0.2f, 0.6f, 1f) * 4f);
        lethalMat = Unlit(new Color(1f, 0.12f, 0.08f) * 4f);
    }

    static Mesh Prim(PrimitiveType t)
    {
        var go = GameObject.CreatePrimitive(t);
        var m = go.GetComponent<MeshFilter>().sharedMesh;
        Destroy(go);
        return m;
    }

    static Material Lit(Color c) => CharacterFigure.Mat(c);

    // Emissive unlit colour (HDR above 1 blooms), instancing on: flames, flash and ring glow like the car
    // lights do, no Light components.
    static Material Unlit(Color c)
    {
        var shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null) return null;
        var m = new Material(shader) { enableInstancing = true };
        m.SetColor("_BaseColor", c);
        return m;
    }

    static Mesh RingMesh()
    {
        const int seg = 40;
        var v = new Vector3[(seg + 1) * 2]; var uv = new Vector2[v.Length]; var tri = new int[seg * 6];
        for (int i = 0; i <= seg; i++)
        {
            float a = i * Mathf.PI * 2f / seg;
            var d = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
            v[i * 2] = d * 0.85f; v[i * 2 + 1] = d;
            uv[i * 2] = new Vector2(i / (float)seg, 0.3f); uv[i * 2 + 1] = new Vector2(i / (float)seg, 0.7f);
            if (i == seg) break;
            int t = i * 6, b = i * 2;
            tri[t] = b; tri[t + 1] = b + 2; tri[t + 2] = b + 1; tri[t + 3] = b + 1; tri[t + 4] = b + 2; tri[t + 5] = b + 3;
        }
        var m = new Mesh { vertices = v, uv = uv, triangles = tri };
        m.RecalculateBounds();
        return m;
    }

    void OnEnable() => RenderPipelineManager.beginCameraRendering += Draw;
    void OnDisable() => RenderPipelineManager.beginCameraRendering -= Draw;

    void Update()
    {
        float dt = Time.deltaTime;
        puffM.Clear(); debrisM.Clear(); flashM.Clear(); ringM.Clear(); stunM.Clear(); lethalM.Clear();
        for (int i = particles.Count - 1; i >= 0; i--)
        {
            var p = particles[i];
            p.age += dt;
            if (p.age >= p.life) { particles[i] = particles[particles.Count - 1]; particles.RemoveAt(particles.Count - 1); continue; }
            float t = p.age / p.life;
            switch (p.kind)
            {
                case PuffK:
                    p.vel *= Mathf.Exp(-0.8f * dt);
                    p.pos += (p.vel + Vector3.up * 1.2f) * dt;
                    puffM.Add(Matrix4x4.TRS(p.pos, Quaternion.Euler(p.spin * t), Vector3.one * p.size * (0.6f + 1.4f * t) * (1f - t * t)));
                    break;
                case DebrisK:
                    p.vel += Physics.gravity * dt;
                    p.pos += p.vel * dt;
                    debrisM.Add(Matrix4x4.TRS(p.pos, Quaternion.Euler(p.spin * p.age * 3f), Vector3.one * p.size * (1f - t * 0.5f)));
                    break;
                case FlashK:
                    flashM.Add(Matrix4x4.TRS(p.pos, Quaternion.identity, Vector3.one * p.size * 2f * Mathf.Sin(Mathf.Min(1f, t * 1.3f) * Mathf.PI) + Vector3.one * 0.01f));
                    break;
                case StunK:
                case LethalK:
                    (p.kind == StunK ? stunM : lethalM).Add(Matrix4x4.TRS(p.pos, Quaternion.identity, Vector3.one * p.size * 2f * (1f - t) + Vector3.one * 0.005f));
                    break;
                case RingK:
                    ringM.Add(Matrix4x4.TRS(p.pos + Vector3.up * 0.3f, Quaternion.identity, new Vector3(p.size * t, 1f, p.size * t)));
                    break;
            }
            particles[i] = p;
        }
    }

    void Draw(ScriptableRenderContext ctx, Camera cam)
    {
        if (cam.cameraType == CameraType.Preview || cam.cameraType == CameraType.Reflection) return;
        Batch(cam, cube, puffMat, puffM);
        Batch(cam, cube, debrisMat, debrisM);
        Batch(cam, sphere, flashMat, flashM);
        Batch(cam, ring, flashMat, ringM);
        Batch(cam, sphere, stunMat, stunM);
        Batch(cam, sphere, lethalMat, lethalM);

        // Billboards facing this camera (flicker in size), flat scorch decals.
        bool fresh = requestFrame == Time.frameCount || requestFrame == Time.frameCount - 1;
        flameM.Clear(); scorchM.Clear();
        if (fresh)
        {
            Vector3 cp = cam.transform.position;
            float far2 = 450f * 450f;
            for (int i = 0; i < flames.Count; i++)
            {
                var f = flames[i];
                Vector3 p = new Vector3(f.x, f.y, f.z);
                if ((p - cp).sqrMagnitude > far2) continue;
                Vector3 to = cp - p; to.y = 0f;
                var rot = to.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(-to) : Quaternion.identity;
                float flick = 0.8f + 0.4f * Mathf.PerlinNoise(Time.time * 9f + i * 0.37f, i);
                flameM.Add(Matrix4x4.TRS(p + Vector3.up * f.w * 0.5f * flick, rot, new Vector3(f.w * 0.8f, f.w * 1.2f * flick, 1f)));
            }
            for (int i = 0; i < scorches.Count; i++)
            {
                var s = scorches[i];
                scorchM.Add(Matrix4x4.TRS(new Vector3(s.x, s.y + 0.03f, s.z), Quaternion.Euler(90f, 0f, 0f), new Vector3(s.w * 2f, s.w * 2f, 1f)));
            }
        }
        Batch(cam, quad, scorchMat, scorchM);
        Batch(cam, quad, flameMat, flameM);
    }

    static void Batch(Camera cam, Mesh mesh, Material mat, List<Matrix4x4> m)
    {
        if (mat == null || m.Count == 0) return;
        var rp = new RenderParams(mat) { camera = cam, shadowCastingMode = ShadowCastingMode.Off, receiveShadows = false,
                                         worldBounds = new Bounds(Vector3.zero, Vector3.one * 100000f) };
        for (int s = 0; s < m.Count; s += 1023)
            Graphics.RenderMeshInstanced(rp, mesh, 0, m, Mathf.Min(1023, m.Count - s), s);
    }
}
