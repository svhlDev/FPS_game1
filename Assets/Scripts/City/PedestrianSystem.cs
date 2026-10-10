using System.Collections.Generic;
using UnityEngine;

// Crowds on the WalkGraph (sidewalks, skywalks, bridges, crosswalks). Each pedestrian picks a goal (a
// building entrance or a taxi pad in its part of the graph), paths there with A*, walks at 1.2-1.6 m/s
// with its own small lane offset, waits at crosswalks for the walk phase, and disappears into the
// building (or a taxi) at the end; a new one then appears at an entrance out of view.
//   Near tier (within nearRadius of the player): the shared jointed figure (CharacterFigure, varied
//     muted colours, posed by FigureAnimator) with a CharacterController
//     on the Player layer (cars never push them), separation from neighbours, reactions:
//       danger (gunfire, a crash) within dangerRadius -> flee; an officer within 2 m -> step aside;
//       the player on a car roof within 40 m -> stop and look up for a moment.
//   Far tier: no GameObject, positions advanced along the path, drawn as one combined slab-and-head
//     mesh with RenderMeshInstanced (one batch), scaled per pedestrian to its generated body's height
//     and width so promotion doesn't pop.
//   Bodies: each pedestrian has a seed; promotion assembles the pooled generated body for it
//     (BodyPool.Civilian), or the primitive figure while the pool is still filling.
//   Promotion at nearRadius, demotion at farRadius (hysteresis).
// Health (CharacterHealth, 60, near tier only, full again on promotion): stun shots knock down, lethal
// hits make them run; at 0 the body becomes a ragdoll and the pedestrian leaves the crowd (a new one
// appears out of view later).
// The near tier is the crowd for blending in (NearAgents).
[DefaultExecutionOrder(120)]
public class PedestrianSystem : MonoBehaviour
{
    public int budget = 600;
    public float nearRadius = 120f;
    public float farRadius = 140f;
    public Vector2 walkSpeed = new Vector2(1.2f, 1.6f);
    public float fleeSpeed = 4f;
    public float separation = 0.8f;
    public float dangerRadius = 30f;
    public float gravity = -25f;
    public int pathsPerFrame = 6;
    public Material bodyMaterial;
    [HideInInspector] public Material visorMaterial; // unused since the shared figure (kept for old scenes)

    public static PedestrianSystem Instance { get; private set; }

    public enum State : byte { Walking, Waiting, Fleeing, Staring, AtPad, Gone, Down }

    public class Ped
    {
        public Vector3 pos, vel;
        public float yaw;
        public float speed, lane;
        public List<int> path = new List<int>();
        public int index;          // next node in path
        public int goal;
        public State state;
        internal float until;
        internal Vector3 fleeFrom;
        public Transform near;
        internal CharacterController cc;
        internal FigureAnimator anim;
        internal float bob, vy, lastStare;
        internal bool downLethal;
        public int seed;
        internal BodyAsset body;   // cached once the pool is full
        public bool IsNear => near != null;
    }

    readonly List<Ped> peds = new List<Ped>();
    readonly List<Ped> nearList = new List<Ped>();
    public IReadOnlyList<Ped> NearAgents => nearList;
    public IReadOnlyList<Ped> All => peds;

    readonly Queue<Ped> needPath = new Queue<Ped>();
    readonly Stack<Transform> pool = new Stack<Transform>();
    readonly List<Matrix4x4> bodyMatrices = new List<Matrix4x4>(1024);
    Mesh farMesh;
    readonly Dictionary<long, List<Ped>> grid = new Dictionary<long, List<Ped>>();
    WalkGraph graph;
    int[] component;
    List<List<int>> componentGoals, componentEntrances;
    readonly List<int> walkNodes = new List<int>();
    static readonly List<(Vector3 p, float r, float t)> dangers = new List<(Vector3, float, float)>();
    int playerLayer;

    // Gunfire, crashes, explosions: everyone near flees.
    public static void ReportDanger(Vector3 p, float radius = 30f)
    {
        // One report per spot is enough (bursts and scraping crashes repeat every frame).
        foreach (var d in dangers) if (Time.time - d.t < 0.3f && (d.p - p).sqrMagnitude < 25f) return;
        dangers.Add((p, radius, Time.time));
    }

    // A T-gun shot hit `c`: if it's a near pedestrian, stun: down stunTime s, then flees from `from`;
    // lethal: runs from `from` (CharacterHealth decides whether it dies). True if it was a pedestrian.
    public static bool Shot(Collider c, bool lethal, float stunTime, Vector3 from)
    {
        var p = Find(c);
        if (p == null) return false;
        if (lethal)
        {
            if (p.state != State.Down) { p.state = State.Fleeing; p.until = Time.time + 6f; p.fleeFrom = from; }
            return true;
        }
        p.state = State.Down;
        p.downLethal = p.downLethal || lethal;
        p.until = Time.time + (p.downLethal ? 30f : stunTime);
        p.fleeFrom = from;
        return true;
    }

    public static bool IsPedestrian(Collider c) => Find(c) != null;

    public static Ped Find(Collider c)
    {
        if (Instance == null || c == null) return null;
        foreach (var p in Instance.nearList) if (p.near != null && c.transform.IsChildOf(p.near)) return p;
        return null;
    }

    void Awake() => Instance = this;
    void OnDestroy()
    {
        if (Instance == this) Instance = null;
        cullGroup?.Dispose();
    }

    void LateUpdate() { if (cullGroup != null) cullReady = true; }
    // Far tier is drawn per camera (like the traffic): positions are gathered in Update.
    void OnEnable() => UnityEngine.Rendering.RenderPipelineManager.beginCameraRendering += DrawFar;
    void OnDisable() => UnityEngine.Rendering.RenderPipelineManager.beginCameraRendering -= DrawFar;

    void Start()
    {
        graph = WalkGraph.Instance != null ? WalkGraph.Instance : FindAnyObjectByType<WalkGraph>();
        if (graph == null || graph.Count == 0) { enabled = false; return; }
        playerLayer = LayerMask.NameToLayer("Player");
        BuildComponents();
        // Walk nodes in parts of the graph with at least two goals (initial placement).
        for (int i = 0; i < graph.Count; i++)
            if (graph.kinds[i] == WalkGraph.Kind.Walk && componentGoals[component[i]].Count >= 2) walkNodes.Add(i);

        // Initial crowd: spread over the walk nodes of every part of the graph.
        var rng = new System.Random(7);
        for (int i = 0; i < budget; i++)
        {
            var p = new Ped();
            int start = RandomStart(rng, false);
            if (start < 0) continue;
            Place(p, start, rng);
            peds.Add(p);
        }
    }

    // Connected parts of the graph (street level, each skywalk level...), with their goals.
    void BuildComponents()
    {
        int n = graph.Count;
        component = new int[n];
        for (int i = 0; i < n; i++) component[i] = -1;
        componentGoals = new List<List<int>>();
        componentEntrances = new List<List<int>>();
        var stack = new Stack<int>();
        int c = 0;
        for (int i = 0; i < n; i++)
        {
            if (component[i] >= 0) continue;
            componentGoals.Add(new List<int>());
            componentEntrances.Add(new List<int>());
            stack.Push(i); component[i] = c;
            while (stack.Count > 0)
            {
                int k = stack.Pop();
                if (graph.kinds[k] == WalkGraph.Kind.Entrance) { componentGoals[c].Add(k); componentEntrances[c].Add(k); }
                else if (graph.kinds[k] == WalkGraph.Kind.TaxiPad) componentGoals[c].Add(k);
                for (int e = graph.adjStart[k]; e < graph.adjStart[k + 1]; e++)
                {
                    int nb = graph.adj[e];
                    if (component[nb] >= 0) continue;
                    component[nb] = c;
                    stack.Push(nb);
                }
            }
            c++;
        }
    }

    // A start node: an entrance (respawns: out of view) in a part of the graph that has somewhere to go.
    int RandomStart(System.Random rng, bool entranceOutOfView)
    {
        var cam = Camera.main;
        for (int attempt = 0; attempt < 30; attempt++)
        {
            int c = rng.Next(componentGoals.Count);
            if (componentGoals[c].Count < 2) continue;
            if (!entranceOutOfView)
                return walkNodes.Count > 0 ? walkNodes[rng.Next(walkNodes.Count)] : -1; // anywhere with somewhere to go
            var ents = componentEntrances[c];
            if (ents.Count == 0) continue;
            int e = ents[rng.Next(ents.Count)];
            Vector3 p = graph.positions[e];
            if ((p - Center()).sqrMagnitude > 350f * 350f) continue; // keep the crowd round the player
            if (cam != null)
            {
                Vector3 v = cam.WorldToViewportPoint(p);
                if (v.z > 0f && v.z < 200f && v.x > -0.05f && v.x < 1.05f && v.y > -0.05f && v.y < 1.05f) continue;
            }
            return e;
        }
        return -1;
    }

    System.Random rand = new System.Random(11);

    void Place(Ped p, int node, System.Random rng)
    {
        p.pos = graph.positions[node];
        p.seed = rng.Next();
        p.body = null;
        p.speed = Mathf.Lerp(walkSpeed.x, walkSpeed.y, (float)rng.NextDouble());
        p.lane = (float)rng.NextDouble() * 2f - 1f;
        p.state = State.Walking;
        p.path.Clear();
        p.path.Add(node);
        p.index = 0;
        p.goal = -1;
        needPath.Enqueue(p);
    }

    // Path to a random goal in the same part of the graph, within 250 m when possible.
    void Repath(Ped p)
    {
        int from = p.path.Count > 0 ? p.path[Mathf.Clamp(p.index, 0, p.path.Count - 1)] : NearestNode(p.pos);
        var goals = componentGoals[component[from]];
        if (goals.Count == 0) { Despawn(p); return; }
        int goal = goals[rand.Next(goals.Count)];
        for (int k = 0; k < 6; k++)
        {
            int g2 = goals[rand.Next(goals.Count)];
            if (g2 != from && (graph.positions[g2] - graph.positions[from]).sqrMagnitude < 250f * 250f) { goal = g2; break; }
        }
        if (goal == from || !graph.FindPath(from, goal, p.path)) { Despawn(p); return; }
        p.goal = goal;
        p.index = 1;
    }

    int NearestNode(Vector3 pos)
    {
        int best = 0; float bd = float.MaxValue;
        for (int i = 0; i < graph.Count; i++)
        {
            float d = (graph.positions[i] - pos).sqrMagnitude;
            if (d < bd) { bd = d; best = i; }
        }
        return best;
    }

    void Despawn(Ped p)
    {
        Demote(p);
        p.state = State.Gone;
    }

    Vector3 Center()
    {
        var fpc = FirstPersonController.Instance;
        if (fpc != null && fpc.isActiveAndEnabled) return fpc.transform.position;
        var car = FlyingVehicle.Driven;
        if (car != null) return car.transform.position;
        var cam = Camera.main;
        return cam != null ? cam.transform.position : Vector3.zero;
    }

    // ---------- tiers ----------

    void Promote(Ped p)
    {
        Transform t;
        if (pool.Count > 0) { t = pool.Pop(); t.gameObject.SetActive(true); }
        else t = CreateAgentObject();
        t.SetPositionAndRotation(p.pos, Quaternion.Euler(0f, p.yaw, 0f));
        p.near = t;
        p.cc = t.GetComponent<CharacterController>();
        p.anim = t.GetComponent<FigureAnimator>();
        Dress(p, t);
        var health = t.GetComponent<CharacterHealth>();
        if (health != null) health.ResetHealth();
        var flam = t.GetComponent<Flammable>();
        if (flam != null) flam.Extinguish();
        p.vy = 0f;
        nearList.Add(p);
    }

    void Demote(Ped p)
    {
        if (p.near == null) return;
        p.near.gameObject.SetActive(false);
        pool.Push(p.near);
        if (p.near.GetComponent<CharacterFigure>()?.Generated == true) GeneratedNear--;
        p.near = null; p.cc = null; p.anim = null;
        nearList.Remove(p);
    }

    // The pedestrian's own body on the pooled agent object: the generated one for its seed (rebuilt only
    // when the agent last wore another body; clothes recoloured either way), else the primitive figure.
    void Dress(Ped p, Transform t)
    {
        var fig = t.GetComponent<CharacterFigure>();
        var asset = BodyAsset(p);
        if (asset != null)
        {
            if (fig == null || fig.Hips == null || fig.Asset != asset) fig = CharacterFigure.Assemble(t, asset, CharacterFigure.Role.Civilian, false, false, p.seed);
            else fig.Renderers[0].sharedMaterials = BodyMaterials.Clothes(CharacterFigure.Role.Civilian, p.seed, fig.HeadRenderer.sharedMaterial);
        }
        else if (fig == null || fig.Generated || fig.Hips == null)
            fig = CharacterFigure.Build(t, CharacterFigure.Role.Civilian, rand, CharacterFigure.DefaultHeight, false, false);
        p.cc.height = fig.Height;
        p.cc.center = new Vector3(0f, fig.Height * 0.5f, 0f);
        GeneratedNear += fig.Generated ? 1 : 0;
    }

    BodyAsset BodyAsset(Ped p)
    {
        if (p.body != null) return p.body;
        var a = global::BodyPool.Civilian(p.seed);
        var pool = global::BodyPool.Instance;
        if (a != null && pool != null && !pool.Filling) p.body = a; // stable once the pool is full
        return a;
    }

    public int GeneratedNear { get; private set; }

    // Far-tier scale from the body (the far mesh is 1.5 m tall, its torso 0.30 m wide).
    Vector3 FarScale(Ped p)
    {
        var a = BodyAsset(p);
        if (a == null) return Vector3.one;
        float w = Mathf.Clamp(a.width / 0.30f, 0.6f, 2.5f);
        return new Vector3(w, a.height / CharacterFigure.DefaultHeight, w);
    }
    public Vector3 DebugFarScale(Ped p) => FarScale(p);

    // Test hook: demote the whole near tier (it re-promotes, re-dressed, next frame).
    public void DebugRedress()
    {
        foreach (var p in new List<Ped>(nearList)) Demote(p);
        GeneratedNear = 0;
    }

    Transform CreateAgentObject()
    {
        var go = new GameObject("Pedestrian");
        if (playerLayer >= 0) go.layer = playerLayer;
        var cc = go.AddComponent<CharacterController>();
        float h = CharacterFigure.DefaultHeight;
        cc.height = h; cc.radius = 0.2f; cc.center = new Vector3(0f, h * 0.5f, 0f);
        cc.minMoveDistance = 0f;
        cc.stepOffset = 0.35f;
        // Near pedestrians have no per-part hit colliders (cost); their CharacterController is the hit surface.
        CharacterFigure.Build(go.transform, CharacterFigure.Role.Civilian, rand, h, false, false);
        go.AddComponent<FigureAnimator>();
        Flammable.Add(go, Flammable.Kind.Character);
        CharacterHealth.Add(go, CharacterFigure.Role.Civilian).Died = OnDied;
        return go.transform;
    }

    // Killed: the body falls as a ragdoll, the pedestrian leaves the crowd (its agent goes back to the
    // pool and is dressed again when reused).
    bool OnDied(CharacterHealth h, DamageInfo d)
    {
        Ped p = null;
        foreach (var x in nearList) if (x.near == h.transform) { p = x; break; }
        var fig = h.GetComponent<CharacterFigure>();
        if (fig != null && fig.Hips != null)
        {
            if (fig.Generated && p != null) GeneratedNear--;
            Ragdoll.FromFigure(fig, h.Velocity, d);
        }
        PedestriansKilled++;
        if (p != null) Despawn(p); else h.gameObject.SetActive(false);
        return true;
    }
    public static int PedestriansKilled;

    // ---------- update ----------

    void Update()
    {
        float dt = Time.deltaTime;
        Vector3 center = Center();
        dangers.RemoveAll(d => Time.time - d.t > 0.5f);

        // Pathfinding is spread over frames.
        for (int k = 0; k < pathsPerFrame && needPath.Count > 0; k++)
        {
            var p = needPath.Dequeue();
            if (p.state != State.Gone) Repath(p);
        }

        // Respawn the gone ones at entrances out of view.
        for (int i = 0; i < peds.Count; i++)
        {
            var p = peds[i];
            if (p.state != State.Gone) continue;
            int start = RandomStart(rand, true);
            if (start < 0) continue;
            Place(p, start, rand);
            break; // one per frame
        }

        // Spatial hash of the near tier for separation.
        grid.Clear();
        foreach (var p in nearList)
        {
            long key = Cell(p.pos);
            if (!grid.TryGetValue(key, out var list)) grid[key] = list = new List<Ped>();
            list.Add(p);
        }

        var fpc = FirstPersonController.Instance;
        bool roofRider = fpc != null && fpc.isActiveAndEnabled && fpc.Platform != null;
        float near2 = nearRadius * nearRadius, far2 = farRadius * farRadius;
        bodyMatrices.Clear();
        // Far tier visibility (frustum + baked occlusion) from last frame's camera culling.
        var cam = Camera.main;
        bool useGroup = TrafficSystem.OcclusionEnabled && cam != null && cam.useOcclusionCulling;
        if (useGroup && (cullGroup == null || cullGroup.targetCamera != cam))
        {
            cullGroup?.Dispose();
            spheres = new BoundingSphere[peds.Count];
            cullGroup = new CullingGroup { targetCamera = cam };
            cullGroup.SetBoundingSpheres(spheres);
            cullGroup.SetBoundingSphereCount(peds.Count);
            cullReady = false;
        }

        for (int i = 0; i < peds.Count; i++)
        {
            var p = peds[i];
            if (p.state == State.Gone || p.goal < 0) continue;
            float d2 = (p.pos - center).sqrMagnitude;
            if (p.near == null && d2 < near2) Promote(p);
            else if (p.near != null && d2 > far2) Demote(p);

            Vector3 want = Steer(p, dt, roofRider ? fpc.transform.position : (Vector3?)null);
            if (p.state == State.Gone) continue;

            if (p.near != null) UpdateNear(p, want, dt);
            else
            {
                p.pos += want * dt;
                if (useGroup)
                {
                    bool hidden = cullReady && !cullGroup.IsVisible(i);
                    spheres[i] = new BoundingSphere(p.pos + Vector3.up, 1.5f);
                    if (hidden) continue;
                }
                if (want.sqrMagnitude > 0.01f) p.yaw = Mathf.Atan2(want.x, want.z) * Mathf.Rad2Deg;
                var rot = Quaternion.Euler(0f, p.yaw, 0f);
                bodyMatrices.Add(Matrix4x4.TRS(p.pos, rot, FarScale(p)));
            }
        }
    }

    static long Cell(Vector3 p) => ((long)Mathf.FloorToInt(p.x / 2f) << 32) ^ (uint)Mathf.FloorToInt(p.z / 2f);

    // Desired velocity: along the path (with a lane offset), waiting at crosswalks, fleeing, staring.
    Vector3 Steer(Ped p, float dt, Vector3? roofRider)
    {
        // Shot: on the ground, then up and running (or gone).
        if (p.state == State.Down)
        {
            if (Time.time < p.until) return Vector3.zero;
            if (p.downLethal) { p.downLethal = false; Despawn(p); return Vector3.zero; }
            p.state = State.Fleeing; p.until = Time.time + 5f;
        }
        // Danger nearby: run from it.
        foreach (var d in dangers)
            if ((d.p - p.pos).sqrMagnitude < d.r * d.r && p.state != State.Fleeing)
            {
                p.state = State.Fleeing; p.until = Time.time + 4f; p.fleeFrom = d.p;
            }
        if (p.state == State.Fleeing)
        {
            if (Time.time > p.until) { p.state = State.Walking; p.path.Clear(); p.path.Add(NearestNode(p.pos)); p.index = 0; needPath.Enqueue(p); return Vector3.zero; }
            Vector3 away = p.pos - p.fleeFrom; away.y = 0f;
            return away.sqrMagnitude > 0.01f ? away.normalized * fleeSpeed : Vector3.zero;
        }
        if (p.state == State.Staring) { if (Time.time > p.until) p.state = State.Walking; else return Vector3.zero; }
        if (p.state == State.AtPad) { if (Time.time > p.until) Despawn(p); return Vector3.zero; }
        if (roofRider.HasValue && p.near != null && Time.time - p.lastStare > 20f && (roofRider.Value - p.pos).sqrMagnitude < 40f * 40f)
        {
            p.state = State.Staring; p.until = Time.time + 2f; p.lastStare = Time.time;
            Vector3 look = roofRider.Value - p.pos; look.y = 0f;
            if (look.sqrMagnitude > 0.01f) p.yaw = Mathf.Atan2(look.x, look.z) * Mathf.Rad2Deg;
            return Vector3.zero;
        }

        if (p.index >= p.path.Count)
        {
            // Arrived: into the building, or wait for a taxi.
            if (p.goal >= 0 && graph.kinds[p.goal] == WalkGraph.Kind.TaxiPad && p.state != State.AtPad)
            {
                p.state = State.AtPad; p.until = Time.time + Random.Range(5f, 15f);
                return Vector3.zero;
            }
            Despawn(p);
            return Vector3.zero;
        }
        int node = p.path[p.index];
        int prev = p.path[Mathf.Max(0, p.index - 1)];

        // About to step onto a crosswalk: wait for the walk phase.
        if (graph.kinds[node] == WalkGraph.Kind.Crossing && graph.kinds[prev] != WalkGraph.Kind.Crossing)
        {
            var s = graph.signals[node];
            if (s != null && !s.WalkAllowed((TrafficSignal.Axis)graph.crossAxis[node]))
            {
                p.state = State.Waiting;
                Vector3 toWait = graph.positions[prev] - p.pos; toWait.y = 0f;
                return toWait.sqrMagnitude > 1f ? toWait.normalized * p.speed * 0.5f : Vector3.zero;
            }
            p.state = State.Walking;
        }

        Vector3 a = graph.positions[prev], b = graph.positions[node];
        Vector3 seg = b - a; seg.y = 0f;
        Vector3 side = seg.sqrMagnitude > 0.01f ? Vector3.Cross(Vector3.up, seg.normalized) : Vector3.zero;
        float halfW = Mathf.Max(0.5f, graph.widths[node] * 0.5f - 0.6f);
        Vector3 target = b + side * p.lane * halfW;
        Vector3 to = target - p.pos; to.y = 0f;
        if (to.magnitude < 0.8f) { p.index++; return seg.sqrMagnitude > 0.01f ? seg.normalized * p.speed : Vector3.zero; }
        return to.normalized * p.speed;
    }

    void UpdateNear(Ped p, Vector3 want, float dt)
    {
        // Separation from neighbours.
        long key = Cell(p.pos);
        Vector3 push = Vector3.zero;
        int cx = Mathf.FloorToInt(p.pos.x / 2f), cz = Mathf.FloorToInt(p.pos.z / 2f);
        for (int dx = -1; dx <= 1; dx++)
        for (int dz = -1; dz <= 1; dz++)
        {
            if (!grid.TryGetValue(((long)(cx + dx) << 32) ^ (uint)(cz + dz), out var list)) continue;
            foreach (var o in list)
            {
                if (o == p) continue;
                Vector3 r = p.pos - o.pos; r.y = 0f;
                float d = r.magnitude;
                if (d < separation && d > 0.01f) push += r / d * (separation - d) * 3f;
            }
        }
        // Make room for officers.
        foreach (var o in OfficerAgent.All)
        {
            Vector3 r = p.pos - o.transform.position; r.y = 0f;
            float d = r.magnitude;
            if (d < 2f && d > 0.01f) push += r / d * (2f - d) * 2f;
        }
        Vector3 vel = want + push;
        if (vel.sqrMagnitude > 0.01f) p.yaw = Mathf.MoveTowardsAngle(p.yaw, Mathf.Atan2(vel.x, vel.z) * Mathf.Rad2Deg, 360f * dt);

        p.vy = p.cc.isGrounded ? -2f : p.vy + gravity * dt;
        p.cc.Move((vel + Vector3.up * p.vy) * dt);
        p.near.rotation = Quaternion.Euler(0f, p.yaw, 0f);
        p.pos = p.near.position;
        // Pose: walking at its own pace, stopping and looking up at a roof rider.
        if (p.anim != null)
        {
            p.anim.Velocity = vel;
            p.anim.Grounded = p.cc.isGrounded;
            p.anim.LookYaw = p.yaw;
            p.anim.LookPitch = p.state == State.Staring ? -35f : 0f;
            p.anim.CurrentPose = p.state == State.Down ? (p.downLethal ? FigureAnimator.Pose.Fallen : FigureAnimator.Pose.Stunned) : FigureAnimator.Pose.Normal;
        }
        // Fell off something: back to the nearest node.
        if (p.pos.y < -5f) Despawn(p);
    }

    public int FarDrawn => bodyMatrices.Count;
    CullingGroup cullGroup;
    BoundingSphere[] spheres;
    bool cullReady;
    public int DrawCalls { get; private set; }

    void DrawFar(UnityEngine.Rendering.ScriptableRenderContext context, Camera cam)
    {
        DrawCalls++;
        if (bodyMaterial == null || bodyMatrices.Count == 0) return;
        if (cam.cameraType == CameraType.Preview || cam.cameraType == CameraType.Reflection) return;
        var rp = new RenderParams(bodyMaterial) { camera = cam, shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off, receiveShadows = false,
                                                   worldBounds = new Bounds(Vector3.zero, Vector3.one * 100000f) };
        if (farMesh == null) farMesh = CharacterFigure.FarMesh();
        for (int s = 0; s < bodyMatrices.Count; s += 1023)
            Graphics.RenderMeshInstanced(rp, farMesh, 0, bodyMatrices, Mathf.Min(1023, bodyMatrices.Count - s), s);
    }
}
