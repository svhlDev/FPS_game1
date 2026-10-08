using System.Collections.Generic;
using UnityEngine;

// Fire as a thick fluid made of chunks. Burning things that impact or explode spray chunks; chunks fly
// (gravity, a little drag), and on hitting:
//   a wall (normal.y < 0.5) : stick and run down it at slideSpeed until the surface below is horizontal,
//                             or the wall ends (then fall again);
//   a floor                 : land;
//   a car or a character    : 15 fire damage and heat, then drop and land beneath it.
// Landing makes a fire patch that burns patchLife s. Landing within mergeRadius of a patch pools into
// it instead (bigger, hotter). Patches on a car ride along with it.
// Heat: a 2 m spatial hash refreshed every 0.25 s; each patch adds its intensity to its cell (half to
// the neighbours). A Flammable standing in enough heat for 1 s ignites. Buildings, skywalks and decks
// never get a Flammable: fire lands and burns on them, they never burn.
// All logic only within activeRadius of the player; farther patches just count down.
public class FireSystem : MonoBehaviour
{
    public int maxChunks = 300;
    public int maxPatches = 400;
    public float patchLife = 120f;
    public float mergeRadius = 1.2f;
    public float slideSpeed = 2f;
    public float chunkDamage = 15f;
    public float activeRadius = 250f;
    const float Cell = 2f;

    struct Chunk { public Vector3 pos, vel, wallNormal; public bool sliding; public float age; }
    public class Patch
    {
        public Vector3 pos;              // world, or local to `car`
        public FlyingVehicle car;
        public float intensity, radius, life;
        public Vector3 World => car != null ? car.transform.TransformPoint(pos) : pos;
    }

    static FireSystem instance;
    readonly List<Chunk> chunks = new List<Chunk>(300);
    readonly List<Patch> patches = new List<Patch>(400);
    readonly Dictionary<long, float> heat = new Dictionary<long, float>();
    readonly Dictionary<long, List<Patch>> patchCells = new Dictionary<long, List<Patch>>();
    static readonly List<Flammable> flammables = new List<Flammable>();
    float nextHeat;
    static readonly RaycastHit[] hits = new RaycastHit[8];

    public static FireSystem I
    {
        get
        {
            if (instance == null) instance = new GameObject("FireSystem").AddComponent<FireSystem>();
            return instance;
        }
    }

    public static int PatchCount => instance != null ? instance.patches.Count : 0;
    public static int ChunkCount => instance != null ? instance.chunks.Count : 0;
    public static void Register(Flammable f) { if (!flammables.Contains(f)) flammables.Add(f); }
    public static void Unregister(Flammable f) => flammables.Remove(f);

    // Throw `count` chunks from `pos`: a hemisphere biased upward plus `bias` velocity.
    public static void Spray(Vector3 pos, Vector3 bias, int count, float minSpeed, float maxSpeed)
    {
        var f = I;
        for (int i = 0; i < count && f.chunks.Count < f.maxChunks; i++)
        {
            Vector3 dir = Random.onUnitSphere;
            dir.y = Mathf.Abs(dir.y) + 0.3f;
            f.chunks.Add(new Chunk { pos = pos + dir.normalized * 0.5f, vel = dir.normalized * Random.Range(minSpeed, maxSpeed) + bias });
        }
    }

    // Heat at a world point (0 = none).
    public static float HeatAt(Vector3 p) => instance != null && instance.heat.TryGetValue(Key(p), out float h) ? h : 0f;

    static long Key(Vector3 p) => Key(Mathf.FloorToInt(p.x / Cell), Mathf.FloorToInt(p.y / Cell), Mathf.FloorToInt(p.z / Cell));
    static long Key(int x, int y, int z) => ((long)(x & 0x1FFFFF) << 42) | ((long)(y & 0x1FFFFF) << 21) | (long)(z & 0x1FFFFF);

    Vector3 PlayerPos()
    {
        var fpc = FirstPersonController.Instance;
        if (fpc != null && fpc.isActiveAndEnabled) return fpc.transform.position;
        var car = FlyingVehicle.Driven;
        if (car != null) return car.transform.position;
        return Camera.main != null ? Camera.main.transform.position : Vector3.zero;
    }

    void Update()
    {
        float dt = Time.deltaTime;
        Vector3 player = PlayerPos();
        float active2 = activeRadius * activeRadius;

        // ---------- chunks ----------
        for (int i = chunks.Count - 1; i >= 0; i--)
        {
            var c = chunks[i];
            c.age += dt;
            bool done = c.age > 20f;
            if (!done && (c.pos - player).sqrMagnitude < active2) done = StepChunk(ref c, dt);
            else if (!done) { c.vel += Physics.gravity * dt; c.pos += c.vel * dt; done = c.pos.y < -10f; }
            if (done) { chunks[i] = chunks[chunks.Count - 1]; chunks.RemoveAt(chunks.Count - 1); continue; }
            chunks[i] = c;
            Effects.Flame(c.pos, 0.6f);
        }

        // ---------- patches ----------
        for (int i = patches.Count - 1; i >= 0; i--)
        {
            var p = patches[i];
            p.life -= dt;
            if (p.life <= 0f || (p.car == null && p.pos.y < -50f) || (p.car != null && !p.car.isActiveAndEnabled))
            {
                patches[i] = patches[patches.Count - 1];
                patches.RemoveAt(patches.Count - 1);
                continue;
            }
            Vector3 w = p.World;
            if ((w - player).sqrMagnitude > active2) continue;
            float fade = Mathf.Clamp01(p.life / 10f);
            Effects.Scorch(w, p.radius * 1.1f);
            int flames = Mathf.Clamp(Mathf.CeilToInt(p.radius * 2f), 1, 6);
            for (int k = 0; k < flames; k++)
            {
                float a = k * 2.399f + i;
                Vector3 off = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * p.radius * 0.6f * (k == 0 ? 0f : 1f);
                Effects.Flame(w + off, (0.6f + 0.35f * Mathf.Sqrt(p.intensity)) * fade);
            }
        }

        // ---------- heat and ignition ----------
        if (Time.time >= nextHeat)
        {
            nextHeat = Time.time + 0.25f;
            RebuildHeat(player, active2);
            for (int i = flammables.Count - 1; i >= 0; i--)
            {
                var f = flammables[i];
                if (f == null) { flammables.RemoveAt(i); continue; }
                if (heat.Count == 0) { f.Exposure = 0f; continue; }
                Vector3 fp = f.transform.position + Vector3.up * 0.5f;
                float h = HeatAt(fp);
                if (h >= f.ignitionHeat) { f.Exposure += 0.25f; if (f.Exposure >= 1f) f.Ignite(); }
                else f.Exposure = 0f;
            }
        }
    }

    // One chunk step. True when it's gone (landed or lost).
    bool StepChunk(ref Chunk c, float dt)
    {
        if (c.sliding)
        {
            // Run down the wall; land on the first horizontal surface, fall off where the wall ends.
            Vector3 next = c.pos + Vector3.down * slideSpeed * dt;
            if (Physics.Raycast(c.pos, Vector3.down, out var below, slideSpeed * dt + 0.15f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)
                && below.normal.y >= 0.5f)
            { Land(below.point, below.collider); return true; }
            if (!Physics.Raycast(next + c.wallNormal * 0.2f, -c.wallNormal, 0.5f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            { c.sliding = false; c.vel = c.wallNormal * 0.5f; }
            c.pos = next;
            return false;
        }

        c.vel += Physics.gravity * dt;
        c.vel *= Mathf.Exp(-0.15f * dt);
        Vector3 step = c.vel * dt;
        float len = step.magnitude;
        if (len < 1e-5f) return false;
        int n = Physics.RaycastNonAlloc(c.pos, step / len, hits, len + 0.05f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Collide);
        int best = -1; float bd = float.MaxValue;
        for (int i = 0; i < n; i++)
        {
            var col = hits[i].collider;
            if (col.isTrigger && col.GetComponent<BodyPart>() == null) continue;
            if (hits[i].distance < bd) { bd = hits[i].distance; best = i; }
        }
        if (best < 0) { c.pos += step; return c.pos.y < -10f; }

        var h = hits[best];
        // A car or a character: burn it, then drop beneath it.
        var car = h.collider.GetComponentInParent<FlyingVehicle>();
        var flam = h.collider.GetComponentInParent<Flammable>();
        if (car != null || (flam != null && flam.kind == Flammable.Kind.Character))
        {
            if (car != null && car.Health != null) car.Health.Damage(chunkDamage, h.point, h.normal);
            if (flam != null) flam.AddHeat(1f, chunkDamage);
            float drop = car != null ? car.BodyHalfExtents.y * 2f + 0.3f : 2f;
            c.pos = new Vector3(h.point.x, (car != null ? car.transform.position.y : h.point.y) - drop, h.point.z);
            c.vel = new Vector3(0f, Mathf.Min(c.vel.y, -2f), 0f);
            return false;
        }
        if (h.normal.y < 0.5f)
        {
            c.sliding = true;
            c.wallNormal = new Vector3(h.normal.x, 0f, h.normal.z).normalized;
            c.pos = h.point + h.normal * 0.05f;
            return false;
        }
        Land(h.point, h.collider);
        return true;
    }

    // A chunk lands: pool into a nearby patch, or start one (riding along on a car).
    void Land(Vector3 point, Collider surface)
    {
        var car = surface != null ? surface.GetComponentInParent<FlyingVehicle>() : null;
        Patch near = NearestPatch(point, mergeRadius);
        if (near == null && patches.Count >= maxPatches) near = NearestPatch(point, float.MaxValue);
        if (near != null)
        {
            near.intensity += 1f;
            near.radius = Mathf.Min(3f, 0.6f + 0.25f * Mathf.Sqrt(near.intensity));
            near.life = patchLife;
            return;
        }
        var p = new Patch { intensity = 1f, radius = 0.6f, life = patchLife, car = car };
        p.pos = car != null ? car.transform.InverseTransformPoint(point) : point;
        patches.Add(p);
        AddToCells(p);
    }

    Patch NearestPatch(Vector3 point, float within)
    {
        Patch best = null; float bd = within * within;
        if (within < 10f)
        {
            int cx = Mathf.FloorToInt(point.x / Cell), cy = Mathf.FloorToInt(point.y / Cell), cz = Mathf.FloorToInt(point.z / Cell);
            for (int x = -1; x <= 1; x++) for (int y = -1; y <= 1; y++) for (int z = -1; z <= 1; z++)
            {
                if (!patchCells.TryGetValue(Key(cx + x, cy + y, cz + z), out var list)) continue;
                foreach (var p in list)
                {
                    float d = (p.World - point).sqrMagnitude;
                    if (d < bd) { bd = d; best = p; }
                }
            }
            return best;
        }
        foreach (var p in patches)
        {
            float d = (p.World - point).sqrMagnitude;
            if (d < bd) { bd = d; best = p; }
        }
        return best;
    }

    void AddToCells(Patch p)
    {
        long k = Key(p.World);
        if (!patchCells.TryGetValue(k, out var list)) patchCells[k] = list = new List<Patch>();
        list.Add(p);
    }

    // Heat per cell from the live patches near the player (and the patch lookup grid).
    void RebuildHeat(Vector3 player, float active2)
    {
        heat.Clear();
        patchCells.Clear();
        foreach (var p in patches)
        {
            Vector3 w = p.World;
            AddToCells(p);
            if ((w - player).sqrMagnitude > active2) continue;
            int cx = Mathf.FloorToInt(w.x / Cell), cy = Mathf.FloorToInt(w.y / Cell), cz = Mathf.FloorToInt(w.z / Cell);
            for (int x = -1; x <= 1; x++) for (int y = -1; y <= 1; y++) for (int z = -1; z <= 1; z++)
            {
                long k = Key(cx + x, cy + y, cz + z);
                float add = x == 0 && y == 0 && z == 0 ? p.intensity : p.intensity * 0.5f;
                heat.TryGetValue(k, out float cur);
                heat[k] = cur + add;
            }
        }
    }
}
