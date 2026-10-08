using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using static FlightGrayboxBuilder;

// City look kit, shared by the builders. Everything is generated at edit time from a seeded
// System.Random, so the same seed always gives the same city.
//
// Split between GAMEPLAY geometry (building masses, roofs, ledges, bridges: colliders, tops on the
// grid) and DECORATION (neon, signs, AC units, pipes, roof machinery, antennas, holograms: no
// colliders, never changes what you can stand on or run along). Light comes from emissive materials
// picked up by Bloom, never from Light components.
// Tower archetypes, bays, recessed floors, balconies and the extra decoration live in CityVariation.cs.
public static partial class CityDressing
{
    const string Folder = "Assets/Graybox/City";
    const float LaneClearance = 6f;   // nothing within this of any lane centre line
    const float MaxWallDecoDepth = 0.3f; // alley-wall decoration never protrudes more than this

    // ---------- kit ----------

    public class Kit
    {
        public float spacing;                 // grid layer height
        public float trafficMin, trafficMax;  // world heights of the traffic band
        public Material facade, decoDark, aircraftRed, laneNorth, laneSouth, haze, bridge, sodium, padPaint;
        public Material[] neon, holograms, flickerHolograms; // flicker variants: ~1 in 10 holograms
        public int detailLayer;               // decoration: culled at the camera's Detail distance
        public Clearance clearance;
        public readonly List<Bounds> keepOut = new List<Bounds>();

        // 0 = underworld, 1 = traffic band, 2 = upper city
        public int Band(float y) => y < trafficMin ? 0 : y < trafficMax ? 1 : 2;
        // Sign / hologram density per band.
        public float Density(float y) => Band(y) == 1 ? 1f : Band(y) == 0 ? 0.3f : 0.15f;
    }

    public class Tower
    {
        public Transform root;
        public readonly List<Bounds> masses = new List<Bounds>();   // gameplay boxes with the facade (incl. bays)
        public readonly List<int> massStyle = new List<int>();      // facade style per mass
        public readonly List<bool> massRecessed = new List<bool>(); // the inner box of a recessed floor
        public readonly List<Bounds> features = new List<Bounds>(); // balconies, columns, bridges: for overlap checks
        public Vector3 canyonNormal;  // horizontal unit vector from the tower toward the canyon
        public int heightLayers;
        public bool tall;             // gets a blinking aircraft light
        public Archetype archetype;
        public int style;             // main facade style
        public bool climbMinus, climbPlus; // alley on that side (along the canyon axis) is a ClimbAlley

        // Tower-local frame: s = depth in from the canyon face, l = along the canyon from the centre.
        public bool depthAlongX;
        public float canyonFace, dirSign, lengthCenter;
        public Color tint;
        public float seed;
        public Vector2 twinGapL;      // twin shafts: the slot between them (l range) ...
        public Vector2Int twinGapY;   // ... and its layer range (podium top to the lower shaft top)
        public float twinGapDepth;
        public bool hasTwinGap;

        public Vector3 Along => depthAlongX ? Vector3.forward : Vector3.right;
        public Vector3 Across => depthAlongX ? Vector3.right : Vector3.forward;

        public Bounds Frame(float s0, float s1, float l0, float l1, float y0, float y1)
        {
            float a = canyonFace - dirSign * s0, b = canyonFace - dirSign * s1;
            float dMin = Mathf.Min(a, b), dMax = Mathf.Max(a, b);
            float lMin = lengthCenter + l0, lMax = lengthCenter + l1;
            var bounds = new Bounds();
            bounds.SetMinMax(depthAlongX ? new Vector3(dMin, y0, lMin) : new Vector3(lMin, y0, dMin),
                             depthAlongX ? new Vector3(dMax, y1, lMax) : new Vector3(lMax, y1, dMax));
            return bounds;
        }
    }

    public static Kit CreateKit(float spacing, int trafficMinLayer, int trafficMaxLayer)
    {
        Directory.CreateDirectory(Folder);
        var kit = new Kit { spacing = spacing, trafficMin = trafficMinLayer * spacing, trafficMax = (trafficMaxLayer + 1) * spacing };
        kit.detailLayer = Mathf.Max(0, EnsureLayer("Detail"));

        kit.facade = ShaderMaterial("CityFacade", "FPS/CityFacade", m =>
        {
            m.SetVector("_BandHeights", new Vector4(kit.trafficMin, kit.trafficMax, 0f, 0f));
            m.SetFloat("_LitFraction", 1.3f); // global scale; base lit fraction comes per facade style
            m.SetFloat("_EmissionStrength", 1.6f);
            m.SetVector("_BandLit", new Vector4(0.5f, 1.2f, 0.7f, 0f));
        });
        kit.decoDark = LitMaterial("DecoDark", new Color(0.12f, 0.12f, 0.13f));
        kit.bridge = LitMaterial("BridgeDeck", new Color(0.2f, 0.21f, 0.24f));
        kit.padPaint = LitMaterial("LandingPadPaint", new Color(0.26f, 0.27f, 0.3f));
        kit.neon = new[]
        {
            Neon("NeonCyan", new Color(0.2f, 0.9f, 1f), 4f),
            Neon("NeonMagenta", new Color(1f, 0.2f, 0.75f), 4f),
            Neon("NeonAmber", new Color(1f, 0.6f, 0.15f), 4f),
            Neon("NeonGreen", new Color(0.35f, 1f, 0.45f), 3.5f),
            Neon("NeonViolet", new Color(0.6f, 0.3f, 1f), 4f),
        };
        kit.sodium = Neon("NeonSodium", new Color(1f, 0.5f, 0.12f), 3f);
        kit.aircraftRed = Neon("AircraftRed", new Color(1f, 0.08f, 0.05f), 6f);
        kit.laneNorth = Neon("LaneGuideNorth", new Color(0.2f, 0.9f, 1f), 0.8f);   // faint: below the bloom threshold
        kit.laneSouth = Neon("LaneGuideSouth", new Color(1f, 0.2f, 0.75f), 0.8f);

        var palettes = new (string name, Color a, Color b)[]
        {
            ("HoloCyanPink", new Color(0.2f, 0.9f, 1f), new Color(1f, 0.3f, 0.8f)),
            ("HoloAmberRed", new Color(1f, 0.7f, 0.2f), new Color(1f, 0.15f, 0.1f)),
            ("HoloGreenTeal", new Color(0.4f, 1f, 0.5f), new Color(0.1f, 0.6f, 0.8f)),
            ("HoloVioletBlue", new Color(0.65f, 0.35f, 1f), new Color(0.2f, 0.4f, 1f)),
            ("HoloWhiteCyan", new Color(0.9f, 0.95f, 1f), new Color(0.3f, 0.8f, 1f)),
        };
        kit.holograms = new Material[palettes.Length];
        kit.flickerHolograms = new Material[palettes.Length];
        for (int i = 0; i < palettes.Length; i++)
        {
            var p = palettes[i];
            float seed = i * 3.7f;
            kit.holograms[i] = ShaderMaterial(p.name, "FPS/Hologram", m =>
            {
                m.SetColor("_ColorA", p.a);
                m.SetColor("_ColorB", p.b);
                m.SetFloat("_Intensity", 1.6f);
                m.SetFloat("_Seed", seed);
                m.SetFloat("_FlickerRate", 0f);
            });
            kit.flickerHolograms[i] = ShaderMaterial(p.name + "_Flicker", "FPS/Hologram", m =>
            {
                m.SetColor("_ColorA", p.a);
                m.SetColor("_ColorB", p.b);
                m.SetFloat("_Intensity", 1.6f);
                m.SetFloat("_Seed", seed);
                m.SetFloat("_FlickerRate", 0.03f);
            });
        }
        kit.haze = ShaderMaterial("UnderworldHaze", "FPS/Hologram", m =>
        {
            m.SetColor("_ColorA", new Color(1f, 0.45f, 0.15f));
            m.SetColor("_ColorB", new Color(0.8f, 0.3f, 0.2f));
            m.SetFloat("_Intensity", 0.08f); // a bit denser: the canyon floor fades into glow
            m.SetFloat("_Pattern", 0f);
            m.SetFloat("_ScanlineStrength", 0f);
            m.SetFloat("_FlickerRate", 0f);
            m.SetFloat("_EdgeFade", 0.3f);
        });
        return kit;
    }

    // ---------- towers ----------

    // Gameplay masses only, from one of the archetypes (CityVariation.cs): slab with setbacks,
    // podium + shaft, twin shafts, notched, stepped pyramid; plus optional recessed floors. Every top is on
    // the grid, and the canyon face stays flush in every archetype (shapes step back from the alleys and
    // the rear), so the canyon wall stays continuous and decks stay attached. Each mass is its own mesh:
    // vertex colour = wall tint + building seed, uv2.x = facade style, so per-building variation survives
    // static batching.
    public static Tower BuildTower(Transform parent, string name, Vector3 baseCenter, Vector2 footprint, int heightLayers,
                                   Vector3 canyonNormal, Color wallTint, System.Random rng, Kit kit,
                                   Archetype? archetype = null, bool allowRecess = true)
    {
        var t = new Tower { canyonNormal = canyonNormal, heightLayers = heightLayers, tint = wallTint };
        t.root = new GameObject(name).transform;
        t.root.SetParent(parent, false);

        t.depthAlongX = Mathf.Abs(canyonNormal.x) > 0.5f;
        float depth = footprint.x, length = footprint.y;     // depth = across the canyon axis
        t.dirSign = t.depthAlongX ? canyonNormal.x : canyonNormal.z;
        t.canyonFace = (t.depthAlongX ? baseCenter.x : baseCenter.z) + t.dirSign * depth * 0.5f;
        t.lengthCenter = t.depthAlongX ? baseCenter.z : baseCenter.x;
        t.seed = (float)rng.NextDouble();

        BuildArchetype(t, archetype ?? PickArchetype(rng), depth, length, heightLayers, allowRecess, rng, kit);
        return t;
    }

    static void AddMass(Tower t, Bounds b, int style, string name, bool recessed, Kit kit)
    {
        var go = new GameObject(name);
        go.transform.SetParent(t.root, false);
        go.transform.position = b.center;
        go.transform.localScale = b.size;
        go.AddComponent<MeshFilter>().sharedMesh = ColoredCube(new Color(t.tint.r, t.tint.g, t.tint.b, t.seed), style);
        go.AddComponent<MeshRenderer>().sharedMaterial = kit.facade;
        go.AddComponent<BoxCollider>();
        MarkStatic(go, true);
        t.masses.Add(b);
        t.massStyle.Add(style);
        t.massRecessed.Add(recessed);
    }

    // Decoration (collider-free) plus canyon-face ledges and lane guide strips for one tower.
    public static void DressTower(Tower t, System.Random rng, Kit kit, float[] laneRideHeights, Material laneGuide)
    {
        var deco = new GameObject("Decoration").transform;
        deco.SetParent(t.root, false);
        Vector3 n = t.canyonNormal;
        Vector3 along = new Vector3(Mathf.Abs(n.z), 0f, Mathf.Abs(n.x)); // horizontal axis along the canyon face

        int massCount = t.masses.Count;
        for (int k = 0; k < massCount; k++)
        {
            var b = t.masses[k];
            bool recessed = t.massRecessed[k];
            bool flush = IsFlush(t, b) && !recessed;                      // on the continuous canyon wall
            bool top = !MassAbove(t, b);
            Vector3 face = b.center + Vector3.Scale(n, b.extents);        // point on the canyon face
            float faceLen = Vector3.Dot(b.size, along);

            // Vertical neon strips at the two canyon-side corners.
            for (int s = -1; s <= 1; s += 2)
            {
                if (!flush || rng.NextDouble() > 0.45) continue;
                float h = b.size.y * Mathf.Lerp(0.5f, 1f, (float)rng.NextDouble());
                var c = new Vector3(0f, b.min.y + h * 0.5f, 0f) + Flat(face) + along * (s * faceLen * 0.5f) + n * 0.1f;
                Box(deco, "NeonCorner", c, new Vector3(0.3f, h, 0.3f), NeonFor(c.y, rng, kit), kit);
            }

            // Neon band along the setback line (the roof edge of a mass with another above it).
            if (!top && rng.NextDouble() < 0.5)
            {
                var mat = NeonFor(b.max.y, rng, kit);
                var mid = new Vector3(b.center.x, b.max.y - 0.3f, b.center.z);
                Vector3 across = new Vector3(Mathf.Abs(n.x), 0f, Mathf.Abs(n.z));
                float depthLen = Vector3.Dot(b.size, across);
                Vector3 thin = Vector3.up * 0.25f;
                // Front and back (along the canyon), then the two alley sides.
                for (int s = -1; s <= 1; s += 2)
                    Box(deco, "NeonBand", mid + n * (s * (depthLen * 0.5f + 0.12f)), along * faceLen + across * 0.25f + thin, mat, kit);
                for (int s = -1; s <= 1; s += 2)
                    Box(deco, "NeonBand", mid + along * (s * (faceLen * 0.5f + 0.12f)), across * depthLen + along * 0.25f + thin, mat, kit);
            }

            // Alley walls (the two faces along the canyon axis). ClimbAlley walls (and the slot between twin
            // shafts) get nothing that sticks out; LedgeAlley walls get AC units, pipes and pipe bundles.
            for (int s = -1; s <= 1; s += 2)
            {
                if (recessed || IsClimbFace(t, b, s)) continue;
                Vector3 wn = along * s;
                Vector3 wallCenter = b.center + Vector3.Scale(wn, b.extents);
                Vector3 across = new Vector3(Mathf.Abs(n.x), 0f, Mathf.Abs(n.z));
                float wallW = Vector3.Dot(b.size, across);
                int units = 2 + rng.Next(5);
                for (int u = 0; u < units; u++)
                {
                    float y = Mathf.Lerp(b.min.y + 2f, b.max.y - 2f, (float)rng.NextDouble());
                    var c = new Vector3(wallCenter.x, y, wallCenter.z) + across * Mathf.Lerp(-wallW * 0.4f, wallW * 0.4f, (float)rng.NextDouble()) + wn * 0.15f;
                    Box(deco, "ACUnit", c, Vector3.Scale(across, Vector3.one) * 1.2f + Vector3.up * 0.8f + Vector3.Scale(wn * s, Vector3.one) * 0.3f, kit.decoDark, kit);
                }
                int pipes = (b.min.y < kit.trafficMin ? 2 : 0) + rng.Next(2);
                for (int p = 0; p < pipes; p++)
                {
                    float len = Mathf.Min(b.size.y - 2f, Mathf.Lerp(10f, 40f, (float)rng.NextDouble()));
                    if (len < 4f) continue;
                    float y0 = Mathf.Lerp(b.min.y, b.max.y - len, (float)rng.NextDouble());
                    var c = new Vector3(wallCenter.x, y0 + len * 0.5f, wallCenter.z) + across * Mathf.Lerp(-wallW * 0.45f, wallW * 0.45f, (float)rng.NextDouble()) + wn * 0.14f;
                    Cylinder(deco, "Pipe", c, new Vector3(0.24f, len * 0.5f, 0.24f), kit.decoDark, kit);
                }
                if (rng.NextDouble() < 0.3) PipeBundle(deco, b, wn, across, rng, kit);
            }

            // Roof props (tanks, cooling units, masts, dishes, shacks, landing pads), off covered parts.
            if (!recessed) RoofDressing(t, b, deco, rng, kit);

            // Lane guide strips on the canyon face at each traffic level's ride height.
            if (laneGuide != null && flush)
                foreach (float y in laneRideHeights)
                    if (y >= b.min.y && y < b.max.y)
                        Box(deco, "LaneGuide", new Vector3(face.x, y, face.z) + n * 0.06f,
                            Vector3.Scale(along, b.size) + new Vector3(Mathf.Abs(n.x), 0f, Mathf.Abs(n.z)) * 0.12f + Vector3.up * 0.25f, laneGuide, kit);

            // Blade signs sticking out of the canyon face, mostly in the traffic band.
            int signs = flush ? rng.Next(3) : 0;
            for (int s = 0; s < signs; s++)
            {
                float h = Mathf.Lerp(8f, 14f, (float)rng.NextDouble());
                float y = Mathf.Lerp(Mathf.Max(b.min.y, kit.trafficMin - 20f) + h * 0.5f, Mathf.Min(b.max.y, kit.trafficMax + 20f) - h * 0.5f, (float)rng.NextDouble());
                if (y - h * 0.5f < b.min.y || y + h * 0.5f > b.max.y || rng.NextDouble() > kit.Density(y)) continue;
                var c = new Vector3(face.x, y, face.z) + along * Mathf.Lerp(-faceLen * 0.4f, faceLen * 0.4f, (float)rng.NextDouble()) + n * 1.5f;
                Box(deco, "BladeSign", c, Vector3.Scale(new Vector3(Mathf.Abs(n.x), 0f, Mathf.Abs(n.z)), Vector3.one) * 3f + Vector3.up * h + along * 0.4f,
                    NeonFor(y, rng, kit), kit);
            }
        }

        // Crown on the highest mass: a spire or an antenna cluster; aircraft light on the tallest.
        var crownBase = t.masses[0];
        foreach (var m in t.masses) if (m.max.y > crownBase.max.y) crownBase = m;
        Vector3 roof = new Vector3(crownBase.center.x, crownBase.max.y, crownBase.center.z);
        float tipY;
        // Crowns scale with the tower (x1 at 400 m).
        float crownScale = Mathf.Max(1f, t.heightLayers * kit.spacing / 400f);
        if (rng.NextDouble() < 0.5)
        {
            float h = Mathf.Lerp(20f, 60f, (float)rng.NextDouble()) * crownScale;
            float r = 1.2f * Mathf.Sqrt(crownScale);
            Cylinder(deco, "Spire", roof + Vector3.up * h * 0.5f, new Vector3(r, h * 0.5f, r), kit.decoDark, kit);
            tipY = roof.y + h;
            roof.y = tipY;
        }
        else
        {
            int count = 2 + rng.Next(3);
            tipY = roof.y;
            for (int i = 0; i < count; i++)
            {
                float h = Mathf.Lerp(8f, 25f, (float)rng.NextDouble()) * crownScale;
                float spread = 4f * crownScale;
                var p = roof + new Vector3(Mathf.Lerp(-spread, spread, (float)rng.NextDouble()), h * 0.5f, Mathf.Lerp(-spread, spread, (float)rng.NextDouble()));
                float r = 0.25f * Mathf.Sqrt(crownScale);
                Cylinder(deco, "Antenna", p, new Vector3(r, h * 0.5f, r), kit.decoDark, kit);
                if (roof.y + h > tipY) { tipY = roof.y + h; }
            }
            roof.y = tipY;
        }
        if (t.tall)
        {
            var light = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            light.name = "AircraftLight";
            Object.DestroyImmediate(light.GetComponent<Collider>());
            light.transform.SetParent(deco, false);
            light.transform.position = new Vector3(roof.x, tipY + 0.5f, roof.z);
            light.transform.localScale = Vector3.one * 1.2f * Mathf.Sqrt(crownScale);
            light.GetComponent<Renderer>().sharedMaterial = kit.aircraftRed;
            light.GetComponent<Renderer>().shadowCastingMode = ShadowCastingMode.Off;
            light.layer = kit.detailLayer;
            light.AddComponent<AircraftLight>().phase = (float)rng.NextDouble();
            // Not static: it toggles its renderer.
        }
    }

    // Gameplay ledges on the canyon face only (alley walls stay flat for wall running).
    public static void AddLedges(Tower t, System.Random rng, Kit kit)
    {
        Vector3 n = t.canyonNormal;
        Vector3 along = new Vector3(Mathf.Abs(n.z), 0f, Mathf.Abs(n.x));
        int count = rng.Next(3);
        for (int i = 0; i < count; i++)
        {
            int mi = rng.Next(t.masses.Count);
            var b = t.masses[mi];
            if (t.massRecessed[mi] || !IsFlush(t, b)) continue; // only on the continuous canyon wall
            int lo = Mathf.CeilToInt(b.min.y / kit.spacing) + 1, hi = Mathf.FloorToInt(b.max.y / kit.spacing) - 1;
            if (hi < lo) continue;
            float top = rng.Next(lo, hi + 1) * kit.spacing;
            float width = Mathf.Lerp(4f, 8f, (float)rng.NextDouble());
            float faceLen = Vector3.Dot(b.size, along);
            float slide = faceLen * 0.5f - width * 0.5f - 1f;
            if (slide < 0f) continue;
            Vector3 face = b.center + Vector3.Scale(n, b.extents);
            var c = new Vector3(face.x, top - 0.25f, face.z) + n * 0.5f + along * Mathf.Lerp(-slide, slide, (float)rng.NextDouble());
            var size = along * width + new Vector3(Mathf.Abs(n.x), 0f, Mathf.Abs(n.z)) * 1f + Vector3.up * 0.5f;
            var bounds = new Bounds(c, size);
            if (!kit.clearance.IsClear(bounds, LaneClearance) || Blocked(bounds, kit)) continue;
            var ledge = Slab(t.root, "Ledge", c, size, kit.bridge);
            var marker = ledge.gameObject.AddComponent<LedgeMarker>();
            marker.outward = n;
            marker.width = width;
            MarkStatic(ledge.gameObject, true);
        }
    }

    // Big flat billboard panel on the canyon face, in and just above the traffic band.
    public static void AddHologram(Tower t, System.Random rng, Kit kit)
    {
        Vector3 n = t.canyonNormal;
        Vector3 along = new Vector3(Mathf.Abs(n.z), 0f, Mathf.Abs(n.x));
        float w = Mathf.Lerp(12f, 20f, (float)rng.NextDouble());
        float h = Mathf.Lerp(16f, 28f, (float)rng.NextDouble());
        float y = Mathf.Lerp(kit.trafficMin + h * 0.5f, kit.trafficMax + 30f, (float)rng.NextDouble());
        Bounds? host = null;
        for (int i = 0; i < t.masses.Count; i++)
        {
            var b = t.masses[i];
            if (!t.massRecessed[i] && IsFlush(t, b) && y - h * 0.5f >= b.min.y && y + h * 0.5f <= b.max.y) host = b;
        }
        if (!host.HasValue) return;
        var m = host.Value;
        float faceLen = Vector3.Dot(m.size, along);
        if (faceLen < w + 4f) w = faceLen - 4f;
        if (w < 8f) return;
        Vector3 face = m.center + Vector3.Scale(n, m.extents);
        var c = new Vector3(face.x, y, face.z) + n * 0.6f + along * Mathf.Lerp(-(faceLen - w) * 0.5f + 2f, (faceLen - w) * 0.5f - 2f, (float)rng.NextDouble());
        var bounds = new Bounds(c, along * w + Vector3.up * h + n * 0.2f);
        if (!kit.clearance.IsClear(bounds, LaneClearance) || Blocked(bounds, kit)) return;

        var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        quad.name = "Hologram";
        Object.DestroyImmediate(quad.GetComponent<Collider>());
        quad.transform.SetParent(t.root, false);
        quad.transform.SetPositionAndRotation(c, Quaternion.LookRotation(-n)); // quad faces along -Z: show it to the canyon
        quad.transform.localScale = new Vector3(w, h, 1f);
        var r = quad.GetComponent<Renderer>();
        var set = rng.NextDouble() < 0.1 ? kit.flickerHolograms : kit.holograms; // only a few flicker
        r.sharedMaterial = set[rng.Next(set.Length)];
        r.shadowCastingMode = ShadowCastingMode.Off;
        r.receiveShadows = false;
        quad.layer = kit.detailLayer;
        MarkStatic(quad, false);
    }

    // Walkway across an alley between two towers' facing walls, deck top on a grid line.
    // Gameplay deck and 1.1 m railings (colliders), neon strip underneath (decoration). No pillars.
    public static bool AddBridge(Transform parent, Tower a, Tower b, Vector3 alleyAxis, System.Random rng, Kit kit)
    {
        // Heights where both towers have a mass whose canyon-side strip covers the bridge.
        Vector3 n = a.canyonNormal;
        int maxLayer = Mathf.Min(a.heightLayers, b.heightLayers) - 2;
        if (maxLayer < 6) return false;
        int layer = 6 + rng.Next(maxLayer - 5);
        float top = layer * kit.spacing;
        if (!MassAt(a, top - 1f, out var ma) || !MassAt(b, top - 1f, out var mb)) return false;

        // 4 m wide walkway, 5 m in from the canyon face.
        Vector3 faceA = ma.center + Vector3.Scale(n, ma.extents);
        Vector3 across = new Vector3(Mathf.Abs(n.x), 0f, Mathf.Abs(n.z));
        Vector3 inward = -n;
        float offset = 5f;
        // Ends: the two facing alley walls.
        float aEnd = Vector3.Dot(ma.center, alleyAxis) + Vector3.Dot(ma.extents, Abs(alleyAxis));
        float bEnd = Vector3.Dot(mb.center, alleyAxis) - Vector3.Dot(mb.extents, Abs(alleyAxis));
        if (bEnd < aEnd) { float tmp = aEnd; aEnd = bEnd; bEnd = tmp; }
        float span = bEnd - aEnd + 1f; // 0.5 m into each wall
        if (span < 4f || span > 40f) return false;
        if (Vector3.Dot(mb.extents, across) * 2f < offset + 4f || Vector3.Dot(ma.extents, across) * 2f < offset + 4f) return false;

        Vector3 mid = Vector3.Scale(faceA, across) + inward * (offset + 2f) + alleyAxis * ((aEnd + bEnd) * 0.5f);
        mid.y = top - 0.25f;
        var deckSize = Abs(alleyAxis) * span + across * 4f + Vector3.up * 0.5f;
        var bounds = new Bounds(mid + Vector3.up * 1f, deckSize + Vector3.up * 2f);
        if (!kit.clearance.IsClear(bounds, LaneClearance) || Blocked(bounds, kit)) return false;
        if (HitsFeature(a, bounds) || HitsFeature(b, bounds)) return false;
        a.features.Add(bounds);
        b.features.Add(bounds);

        var root = new GameObject("Bridge").transform;
        root.SetParent(parent, false);
        MarkStatic(Slab(root, "Deck", mid, deckSize, kit.bridge).gameObject, true);
        for (int s = -1; s <= 1; s += 2)
            MarkStatic(Slab(root, "Railing", mid + across * (s * 1.95f) + Vector3.up * 0.8f,
                            Abs(alleyAxis) * (span - 1f) + across * 0.1f + Vector3.up * 1.1f, kit.bridge).gameObject, true);
        Box(root, "UnderGlow", mid - Vector3.up * 0.3f, Abs(alleyAxis) * (span - 1f) + across * 0.3f + Vector3.up * 0.1f,
            kit.neon[rng.Next(kit.neon.Length)], kit);
        return true;
    }

    // Large additive planes low in the canyon: the glowing haze of the underworld seen from above.
    public static void AddUnderworldHaze(Transform parent, Vector3 center, float size, float[] heights, Kit kit)
    {
        foreach (float y in heights)
        {
            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = "UnderworldHaze";
            Object.DestroyImmediate(quad.GetComponent<Collider>());
            quad.transform.SetParent(parent, false);
            quad.transform.SetPositionAndRotation(new Vector3(center.x, y, center.z), Quaternion.Euler(90f, 0f, 0f));
            quad.transform.localScale = new Vector3(size, size, 1f);
            var r = quad.GetComponent<Renderer>();
            r.sharedMaterial = kit.haze;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            MarkStatic(quad, false);
        }
    }

    // ---------- atmosphere ----------

    // Night: exponential fog, gradient skybox, dim cool moonlight (the scene's existing directional
    // light), low ambient, and a global post volume (Bloom, ACES, split toning, vignette).
    public static void SetupAtmosphere(Camera cam, string profileName)
    {
        // Lighter and bluer than the towers so it reads against them. Opaque shaders fog toward this colour;
        // additive ones (holograms, haze) fade to black, so neon doesn't cut through.
        var fogColor = new Color(0.10f, 0.08f, 0.17f);
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.Exponential;
        // Only for very long distances now: the smog (HeightFogFeature) does the near work.
        RenderSettings.fogDensity = 0.0015f;
        RenderSettings.fogColor = fogColor;

        var sky = ShaderMaterial("NightSky", "FPS/NightSky", m => m.SetColor("_HorizonColor", fogColor * 1.4f));
        RenderSettings.skybox = sky;
        cam.clearFlags = CameraClearFlags.Skybox;
        cam.allowHDR = true;
        cam.farClipPlane = 2500f; // the smog hides the cutoff
        var cull = cam.GetComponent<CameraCullDistances>();
        if (cull == null) cull = cam.gameObject.AddComponent<CameraCullDistances>(); // Detail 700 m, Traffic 1000 m
        cam.allowMSAA = false;
        var camData = cam.GetUniversalAdditionalCameraData();
        camData.renderPostProcessing = true;
        camData.requiresDepthTexture = true; // the height fog reads it
        camData.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
        camData.antialiasingQuality = AntialiasingQuality.High;

        var ambient = new Color(0.05f, 0.05f, 0.085f);
        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = ambient;
        var sh = new SphericalHarmonicsL2();
        sh.AddAmbientLight(ambient);
        RenderSettings.ambientProbe = sh;

        foreach (var l in Object.FindObjectsByType<Light>())
        {
            if (l.type != LightType.Directional) continue;
            l.name = "Moon";
            l.color = new Color(0.6f, 0.7f, 1f);
            l.intensity = 0.3f;
            l.shadows = LightShadows.None;
            l.transform.rotation = Quaternion.Euler(40f, 160f, 0f);
        }

        string path = $"{Folder}/{profileName}.asset";
        AssetDatabase.DeleteAsset(path);
        var profile = ScriptableObject.CreateInstance<VolumeProfile>();
        AssetDatabase.CreateAsset(profile, path);

        var bloom = AddOverride<Bloom>(profile);
        bloom.threshold.value = 1.2f;
        bloom.intensity.value = 0.8f;
        bloom.scatter.value = 0.7f;
        bloom.clamp.value = 20f;
        bloom.highQualityFiltering.value = true;
        var tone = AddOverride<Tonemapping>(profile);
        tone.mode.value = TonemappingMode.ACES;
        var adjust = AddOverride<ColorAdjustments>(profile);
        adjust.contrast.value = 8f;
        adjust.saturation.value = 8f;
        var split = AddOverride<SplitToning>(profile);
        split.shadows.value = new Color(0.25f, 0.6f, 0.62f);
        split.highlights.value = new Color(0.8f, 0.4f, 0.75f);
        var vignette = AddOverride<Vignette>(profile);
        vignette.intensity.value = 0.25f;
        vignette.smoothness.value = 0.4f;
        EditorUtility.SetDirty(profile);
        AssetDatabase.SaveAssets();

        var vol = new GameObject("PostProcessVolume").AddComponent<Volume>();
        vol.isGlobal = true;
        vol.sharedProfile = profile;
    }

    // ---------- smog and clouds ----------

    // Scene object carrying the smog parameters; the renderer feature reads them (and is a no-op
    // in scenes without one). Also makes sure the feature is installed.
    public static HeightFogSettings AddHeightFog()
    {
        EnsureHeightFogFeature();
        return new GameObject("HeightFog").AddComponent<HeightFogSettings>();
    }

    // Adds HeightFogFeature to every URP renderer in the project (the same steps as the renderer's
    // "Add Renderer Feature" button) and turns the depth texture on in every URP asset.
    public static void EnsureHeightFogFeature()
    {
        var shader = Shader.Find("Hidden/FPS/HeightFog");
        if (shader == null) Debug.LogError("Shader 'Hidden/FPS/HeightFog' not found. Has Unity imported Assets/Shaders?");
        foreach (var guid in AssetDatabase.FindAssets("t:UniversalRendererData"))
        {
            var data = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(AssetDatabase.GUIDToAssetPath(guid));
            if (data == null) continue;
            HeightFogFeature feature = null;
            foreach (var f in data.rendererFeatures) if (f is HeightFogFeature h) feature = h;
            if (feature == null)
            {
                feature = ScriptableObject.CreateInstance<HeightFogFeature>();
                feature.name = "HeightFog";
                feature.hideFlags |= HideFlags.HideInHierarchy;
                AssetDatabase.AddObjectToAsset(feature, data);
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feature, out _, out long localId);
                var so = new SerializedObject(data);
                var list = so.FindProperty("m_RendererFeatures");
                var map = so.FindProperty("m_RendererFeatureMap");
                list.arraySize++;
                list.GetArrayElementAtIndex(list.arraySize - 1).objectReferenceValue = feature;
                map.arraySize++;
                map.GetArrayElementAtIndex(map.arraySize - 1).longValue = localId;
                so.ApplyModifiedProperties();
            }
            feature.shader = shader;
            feature.SetActive(true);
            EditorUtility.SetDirty(feature);
            EditorUtility.SetDirty(data);
        }
        foreach (var guid in AssetDatabase.FindAssets("t:UniversalRenderPipelineAsset"))
        {
            var asset = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(AssetDatabase.GUIDToAssetPath(guid));
            if (asset == null || asset.supportsCameraDepthTexture) continue;
            asset.supportsCameraDepthTexture = true;
            EditorUtility.SetDirty(asset);
        }
        AssetDatabase.SaveAssets();
    }

    // Two big cloud planes. Subdivided planes (not quads) so per-vertex fog interpolates well.
    // canyonAxis: direction of the canyon through `center`, where the underside glow is strongest.
    public static void AddCloudDeck(Transform parent, Vector3 center, float size, float[] heights, Vector3 canyonAxis)
    {
        for (int i = 0; i < heights.Length; i++)
        {
            int layer = i;
            var mat = ShaderMaterial($"CloudDeck_{i}", "FPS/CloudDeck", m =>
            {
                m.SetFloat("_Scale", layer == 0 ? 900f : 1300f);
                m.SetFloat("_Coverage", layer == 0 ? 0.55f : 0.45f);
                m.SetFloat("_Opacity", layer == 0 ? 0.85f : 0.7f);
                m.SetFloat("_Seed", 11f + layer * 5f);
                m.SetVector("_Scroll", layer == 0 ? new Vector4(4f, 0f, 1.5f, 0f) : new Vector4(-2.5f, 0f, 3f, 0f));
                m.SetVector("_GlowCenter", new Vector4(center.x, center.z, 0f, 0f));
                m.SetVector("_GlowAxis", new Vector4(canyonAxis.x, canyonAxis.z, 0f, 0f));
                m.renderQueue = (int)RenderQueue.Transparent + layer;
            });
            var plane = GameObject.CreatePrimitive(PrimitiveType.Plane);
            plane.name = $"CloudDeck_{heights[i]:0}";
            Object.DestroyImmediate(plane.GetComponent<Collider>());
            plane.transform.SetParent(parent, false);
            plane.transform.position = new Vector3(center.x, heights[i], center.z);
            plane.transform.localScale = new Vector3(size / 10f, 1f, size / 10f);
            var r = plane.GetComponent<Renderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            MarkStatic(plane, false);
        }
    }

    static T AddOverride<T>(VolumeProfile profile) where T : VolumeComponent
    {
        var c = profile.Add<T>(true);
        c.name = typeof(T).Name;
        AssetDatabase.AddObjectToAsset(c, profile);
        return c;
    }

    // ---------- lane clearance ----------

    // Sampled points of every level of every lane, bucketed on a coarse grid, for "is anything
    // within N m of a lane" checks while placing.
    public class Clearance
    {
        const float Cell = 12f;
        readonly Dictionary<Vector3Int, List<Vector3>> cells = new Dictionary<Vector3Int, List<Vector3>>();

        public Clearance(IEnumerable<LanePath> lanes, float step = 3f)
        {
            foreach (var lane in lanes)
            {
                var levels = new List<int>(lane.Levels);
                foreach (int level in levels)
                    for (float d = 0f; d < lane.Length; d += step)
                    {
                        if (lane.LaneWeight(level, d, out var off) < 0.05f) continue;
                        lane.Sample(d, out var p, out var f);
                        Add(lane.ToWorld(p, f, off));
                    }
            }
        }

        void Add(Vector3 p)
        {
            var k = Key(p);
            if (!cells.TryGetValue(k, out var list)) cells[k] = list = new List<Vector3>();
            list.Add(p);
        }

        static Vector3Int Key(Vector3 p) => new Vector3Int(Mathf.FloorToInt(p.x / Cell), Mathf.FloorToInt(p.y / Cell), Mathf.FloorToInt(p.z / Cell));

        public bool IsClear(Bounds b, float margin)
        {
            var lo = Key(b.min - Vector3.one * margin);
            var hi = Key(b.max + Vector3.one * margin);
            float m2 = margin * margin;
            for (int x = lo.x; x <= hi.x; x++)
            for (int y = lo.y; y <= hi.y; y++)
            for (int z = lo.z; z <= hi.z; z++)
            {
                if (!cells.TryGetValue(new Vector3Int(x, y, z), out var list)) continue;
                foreach (var p in list)
                    if (b.SqrDistance(p) < m2) return false;
            }
            return true;
        }
    }

    // ---------- helpers ----------

    static bool MassAt(Tower t, float y, out Bounds mass)
    {
        for (int i = 0; i < t.masses.Count; i++)
            if (!t.massRecessed[i] && y >= t.masses[i].min.y && y < t.masses[i].max.y && IsFlush(t, t.masses[i])) { mass = t.masses[i]; return true; }
        mass = default;
        return false;
    }

    static bool Blocked(Bounds b, Kit kit)
    {
        foreach (var k in kit.keepOut) if (k.Intersects(b)) return true;
        return false;
    }

    static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);
    static Vector3 Abs(Vector3 v) => new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));

    static Material NeonFor(float y, System.Random rng, Kit kit) =>
        kit.Band(y) == 0 && rng.NextDouble() < 0.7 ? kit.sodium : kit.neon[rng.Next(kit.neon.Length)];

    // Collider-free decoration box. Skipped if it would come near a lane or into a keep-out zone.
    static void Box(Transform parent, string name, Vector3 center, Vector3 size, Material mat, Kit kit) =>
        Deco(PrimitiveType.Cube, parent, name, center, size, mat, kit, new Bounds(center, size));

    static void Cylinder(Transform parent, string name, Vector3 center, Vector3 scale, Material mat, Kit kit) =>
        Deco(PrimitiveType.Cylinder, parent, name, center, scale, mat, kit, new Bounds(center, new Vector3(scale.x, scale.y * 2f, scale.z)));

    static void Deco(PrimitiveType type, Transform parent, string name, Vector3 center, Vector3 scale, Material mat, Kit kit, Bounds bounds)
    {
        if (!kit.clearance.IsClear(bounds, LaneClearance) || Blocked(bounds, kit)) return;
        var go = GameObject.CreatePrimitive(type);
        go.name = name;
        Object.DestroyImmediate(go.GetComponent<Collider>());
        go.transform.SetParent(parent, false);
        go.transform.position = center;
        go.transform.localScale = scale;
        var r = go.GetComponent<Renderer>();
        r.sharedMaterial = mat;
        r.shadowCastingMode = ShadowCastingMode.Off;
        go.layer = kit.detailLayer;
        MarkStatic(go, false);
    }

    static void MarkStatic(GameObject go, bool occluder)
    {
        var flags = StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccludeeStatic;
        if (occluder) flags |= StaticEditorFlags.OccluderStatic;
        GameObjectUtility.SetStaticEditorFlags(go, flags);
    }

    static Mesh baseCube;

    // Unit cube with every vertex coloured (per-building tint and seed for the facade shader) and the
    // facade style id in uv2.x.
    static Mesh ColoredCube(Color c, int style = 0)
    {
        if (baseCube == null)
        {
            var tmp = GameObject.CreatePrimitive(PrimitiveType.Cube);
            baseCube = tmp.GetComponent<MeshFilter>().sharedMesh;
            Object.DestroyImmediate(tmp);
        }
        var mesh = Object.Instantiate(baseCube);
        mesh.name = "TowerMass";
        var colors = new Color[mesh.vertexCount];
        for (int i = 0; i < colors.Length; i++) colors[i] = c;
        mesh.colors = colors;
        var uv2 = new Vector2[mesh.vertexCount];
        for (int i = 0; i < uv2.Length; i++) uv2[i] = new Vector2(style, 0f);
        mesh.uv2 = uv2;
        return mesh;
    }

    // ---------- materials ----------

    static Material ShaderMaterial(string name, string shaderName, System.Action<Material> setup)
    {
        string path = $"{Folder}/{name}.mat";
        var shader = Shader.Find(shaderName);
        if (shader == null)
        {
            Debug.LogError($"Shader '{shaderName}' not found. Has Unity imported Assets/Shaders?");
            shader = Shader.Find("Universal Render Pipeline/Unlit");
        }
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null) { mat = new Material(shader); AssetDatabase.CreateAsset(mat, path); }
        else mat.shader = shader;
        setup(mat);
        EditorUtility.SetDirty(mat);
        return mat;
    }

    static Material LitMaterial(string name, Color c) => ShaderMaterial(name, "Universal Render Pipeline/Lit", m =>
    {
        m.SetColor("_BaseColor", c);
        m.SetFloat("_Smoothness", 0.3f);
        m.enableInstancing = true;
    });

    // Unlit emissive colour; intensity > 1 makes it bloom.
    static Material Neon(string name, Color c, float intensity) => ShaderMaterial(name, "Universal Render Pipeline/Unlit", m =>
    {
        m.SetColor("_BaseColor", new Color(c.r * intensity, c.g * intensity, c.b * intensity, 1f));
        m.enableInstancing = true;
    });
}
