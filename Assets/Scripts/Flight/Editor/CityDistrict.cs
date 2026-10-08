using System.Collections.Generic;
using UnityEngine;
using static FlightGrayboxBuilder;

// District pieces (DistrictBuilder): a street grid with intersections and traffic lights, skywalk rings
// and bridges at a few layers, the hollow tower, market stalls. All walkable surfaces have colliders;
// every walkable area is also recorded as a WalkRect for the pedestrian graph.
public static partial class CityDressing
{
    // A straight street: runs north-south (along Z) at x = center, or east-west at z = center, from
    // min to max along its axis. halfWidth includes the sidewalks.
    public struct StreetDef
    {
        public string name;
        public bool northSouth;
        public float center, halfWidth, min, max;
        public int lanesPerDir;
        public float RoadHalf => halfWidth - SidewalkWidth;
    }

    public struct Intersection
    {
        public Vector3 center;
        public Vector2 half;   // x, z: the full street widths (stop lines at the edges)
        public int ns, ew;     // street indices
    }

    // A walkable rectangle (top surface at area.center.y; size.y unused).
    public struct WalkRect
    {
        public Bounds area;
        public bool skywalk;
        public bool crossing;  // a crosswalk: walk only on the signal's walk phase
        public bool alongNorthSouth; // crossing direction
    }

    // ---------- streets ----------

    public static List<Intersection> BuildDistrictStreets(Transform parent, List<StreetDef> streets, List<Rect> blocks,
                                                          System.Random rng, Kit kit, List<WalkRect> walk)
    {
        var root = new GameObject("Streets").transform;
        root.SetParent(parent, false);
        var asphalt = LitMaterial("StreetAsphalt", new Color(0.06f, 0.06f, 0.07f));
        var paving = LitMaterial("StreetPaving", new Color(0.2f, 0.2f, 0.21f));
        var marking = Neon("StreetMarking", new Color(0.75f, 0.75f, 0.7f), 0.6f);
        var centerLine = Neon("StreetCenterLine", new Color(0.9f, 0.65f, 0.15f), 0.7f);
        var lampHead = Neon("StreetLamp", new Color(1f, 0.82f, 0.6f), 3f);

        // Intersections: every north-south street crossing every east-west one.
        var crossings = new List<Intersection>();
        for (int i = 0; i < streets.Count; i++)
        {
            if (!streets[i].northSouth) continue;
            for (int j = 0; j < streets.Count; j++)
            {
                if (streets[j].northSouth) continue;
                var a = streets[i]; var b = streets[j];
                if (b.center < a.min || b.center > a.max || a.center < b.min || a.center > b.max) continue;
                crossings.Add(new Intersection { center = new Vector3(a.center, 0f, b.center), half = new Vector2(a.halfWidth, b.halfWidth), ns = i, ew = j });
            }
        }

        for (int si = 0; si < streets.Count; si++)
        {
            var st = streets[si];
            float len = st.max - st.min, mid = (st.min + st.max) * 0.5f;
            Flat(root, $"Road_{st.name}", Pos(st, mid, 0f, 0.005f), Size(st, len, st.RoadHalf * 2f, 0.01f), asphalt, false);

            // Segments between the crossing streets: markings and crosswalks.
            var cuts = new List<Vector2>();
            foreach (var c in crossings)
            {
                if (c.ns != si && c.ew != si) continue;
                var other = streets[c.ns == si ? c.ew : c.ns];
                cuts.Add(new Vector2(other.center - other.halfWidth, other.center + other.halfWidth));
            }
            cuts.Sort((x, y) => x.x.CompareTo(y.x));
            float from = st.min;
            for (int k = 0; k <= cuts.Count; k++)
            {
                float to = k < cuts.Count ? cuts[k].x : st.max;
                if (to - from > 4f) Markings(root, st, from, to, marking, centerLine, k > 0, k < cuts.Count);
                if (k < cuts.Count) from = cuts[k].y;
            }
        }

        // Sidewalks round every block (kerb height, colliders), paving inside the block (alleys), lamps.
        foreach (var b in blocks)
        {
            float w = SidewalkWidth;
            Flat(root, "BlockPaving", new Vector3(b.center.x, KerbHeight * 0.5f, b.center.y), new Vector3(b.width, KerbHeight, b.height), paving, true);
            var strips = new[]
            {
                new Rect(b.xMin - w, b.yMin - w, b.width + 2f * w, w),      // south (with corners)
                new Rect(b.xMin - w, b.yMax, b.width + 2f * w, w),          // north
                new Rect(b.xMin - w, b.yMin, w, b.height),                  // west
                new Rect(b.xMax, b.yMin, w, b.height),                      // east
            };
            foreach (var r in strips)
            {
                Flat(root, "Sidewalk", new Vector3(r.center.x, KerbHeight * 0.5f, r.center.y), new Vector3(r.width, KerbHeight, r.height), paving, true);
                walk.Add(new WalkRect { area = new Bounds(new Vector3(r.center.x, KerbHeight, r.center.y), new Vector3(r.width, 0f, r.height)) });
            }
            // Lamps on the kerb edge, arms over the road.
            for (int e = 0; e < 4; e++)
            {
                bool alongX = e < 2;
                float edgeLen = alongX ? b.width : b.height;
                for (float s = LampSpacing * 0.5f; s < edgeLen; s += LampSpacing)
                {
                    Vector3 outN = e == 0 ? Vector3.back : e == 1 ? Vector3.forward : e == 2 ? Vector3.left : Vector3.right;
                    Vector3 basePt = alongX ? new Vector3(b.xMin + s, 0f, e == 0 ? b.yMin : b.yMax) : new Vector3(e == 2 ? b.xMin : b.xMax, 0f, b.yMin + s);
                    Lamp(root, basePt + outN * (w - 0.5f), outN, lampHead, kit);
                }
            }
        }
        // Crosswalks (the zebras just outside each intersection) are walkable on the signal's walk phase.
        foreach (var c in crossings)
        {
            var ns = streets[c.ns]; var ew = streets[c.ew];
            for (int s = -1; s <= 1; s += 2)
            {
                // Across the north-south street (people walk east-west).
                walk.Add(new WalkRect
                {
                    area = new Bounds(new Vector3(ns.center, 0.02f, ew.center + s * (ew.halfWidth + 2f)), new Vector3(ns.RoadHalf * 2f, 0f, 4f)),
                    crossing = true, alongNorthSouth = false,
                });
                // Across the east-west street (people walk north-south).
                walk.Add(new WalkRect
                {
                    area = new Bounds(new Vector3(ns.center + s * (ns.halfWidth + 2f), 0.02f, ew.center), new Vector3(4f, 0f, ew.RoadHalf * 2f)),
                    crossing = true, alongNorthSouth = true,
                });
            }
        }
        return crossings;
    }

    static Vector3 Pos(StreetDef st, float along, float across, float y) =>
        st.northSouth ? new Vector3(st.center + across, y, along) : new Vector3(along, y, st.center + across);

    static Vector3 Size(StreetDef st, float along, float across, float h) =>
        st.northSouth ? new Vector3(across, h, along) : new Vector3(along, h, across);

    // Centre line, lane dividers, edge lines for one segment; zebra crosswalks at segment ends that
    // meet an intersection.
    static void Markings(Transform root, StreetDef st, float from, float to, Material marking, Material centerLine, bool zebraStart, bool zebraEnd)
    {
        float y = 0.015f, len = to - from, mid = (from + to) * 0.5f;
        float zebra = 4f;
        float a = from + (zebraStart ? zebra : 0f), b = to - (zebraEnd ? zebra : 0f);
        if (b - a > 1f)
        {
            float m = (a + b) * 0.5f, l = b - a;
            for (int s = -1; s <= 1; s += 2)
            {
                Flat(root, "CenterLine", Pos(st, m, s * 0.2f, y), Size(st, l, 0.15f, 0.01f), centerLine, false);
                Flat(root, "EdgeLine", Pos(st, m, s * st.lanesPerDir * StreetLaneWidth, y), Size(st, l, 0.2f, 0.01f), marking, false);
                if (st.lanesPerDir >= 2)
                    for (float d = a; d + MarkingDash <= b; d += MarkingDash + MarkingGap)
                        Flat(root, "LaneDash", Pos(st, d + MarkingDash * 0.5f, s * StreetLaneWidth, y), Size(st, MarkingDash, 0.15f, 0.01f), marking, false);
            }
        }
        // Zebra stripes across the road.
        for (int e = 0; e < 2; e++)
        {
            if (e == 0 && !zebraStart || e == 1 && !zebraEnd) continue;
            float c = e == 0 ? from + zebra * 0.5f : to - zebra * 0.5f;
            for (float x = -st.RoadHalf + 0.6f; x < st.RoadHalf - 0.3f; x += 1.2f)
                Flat(root, "Zebra", Pos(st, c, x, y), Size(st, zebra - 0.6f, 0.6f, 0.01f), marking, false);
        }
    }

    static void Lamp(Transform root, Vector3 kerb, Vector3 outN, Material head, Kit kit)
    {
        Vector3 Sz(float a, float h, float o) => Mathf.Abs(outN.x) > 0.5f ? new Vector3(o, h, a) : new Vector3(a, h, o);
        var pole = Slab(root, "LampPole", kerb + Vector3.up * (KerbHeight + LampHeight * 0.5f), new Vector3(0.2f, LampHeight, 0.2f), kit.decoDark);
        Street(pole.gameObject, kit, true);
        var arm = Slab(root, "LampArm", kerb + outN * 0.9f + Vector3.up * (KerbHeight + LampHeight - 0.1f), Sz(0.12f, 0.12f, 1.8f), kit.decoDark);
        Street(arm.gameObject, kit, false);
        var h = Slab(root, "LampHead", kerb + outN * 1.6f + Vector3.up * (KerbHeight + LampHeight - 0.25f), Sz(0.35f, 0.15f, 0.7f), head);
        Street(h.gameObject, kit, false);
    }

    // ---------- traffic lights ----------

    public static TrafficSignal BuildSignal(Transform parent, Intersection c, float offset, Kit kit)
    {
        var go = new GameObject("TrafficSignal");
        go.transform.SetParent(parent, false);
        go.transform.position = c.center;
        var sig = go.AddComponent<TrafficSignal>();
        sig.halfExtents = c.half;
        sig.offset = offset;
        sig.greenOn = Neon("SignalGreen", new Color(0.2f, 1f, 0.45f), 4f);
        sig.amberOn = Neon("SignalAmber", new Color(1f, 0.65f, 0.1f), 4f);
        sig.redOn = Neon("SignalRed", new Color(1f, 0.08f, 0.05f), 4f);
        sig.lampOff = Neon("SignalOff", new Color(0.05f, 0.05f, 0.05f), 1f);

        var ns = new List<Renderer>(); var ew = new List<Renderer>();
        for (int k = 0; k < 4; k++)
        {
            float sx = (k & 1) == 0 ? -1f : 1f, sz = (k & 2) == 0 ? -1f : 1f;
            // Pole on the sidewalk corner.
            Vector3 corner = c.center + new Vector3(sx * (c.half.x - 1f), 0f, sz * (c.half.y - 1f));
            var pole = Slab(go.transform, "SignalPole", corner + Vector3.up * (KerbHeight + 3f), new Vector3(0.25f, 6f, 0.25f), kit.decoDark);
            Street(pole.gameObject, kit, true);
            // Corners (-,-) and (+,+) face north-south traffic, the other two east-west.
            bool forNs = sx * sz > 0f;
            Vector3 face = forNs ? new Vector3(0f, 0f, sz) : new Vector3(sx, 0f, 0f);
            var list = forNs ? ns : ew;
            var housing = Slab(go.transform, "SignalHead", corner + Vector3.up * (KerbHeight + 5.4f) + face * 0.3f,
                               forNs ? new Vector3(0.5f, 1.5f, 0.3f) : new Vector3(0.3f, 1.5f, 0.5f), kit.decoDark);
            Street(housing.gameObject, kit, false);
            for (int l = 0; l < 3; l++) // green bottom, amber, red top
            {
                var lamp = Slab(go.transform, "SignalLamp", corner + Vector3.up * (KerbHeight + 4.95f + l * 0.45f) + face * 0.5f,
                                Vector3.one * 0.32f, sig.lampOff);
                Street(lamp.gameObject, kit, false);
                lamp.gameObject.layer = 0; // always drawn (not culled with the detail layer)
                list.Add(lamp.GetComponent<Renderer>());
            }
        }
        sig.northSouthLamps = ns.ToArray();
        sig.eastWestLamps = ew.ToArray();
        return sig;
    }

    // ---------- skywalks ----------

    public class SkywalkKit
    {
        public Material deck, rail, taxiPad;
        public readonly List<Bounds> solids = new List<Bounds>(); // every tower mass and feature, for overlap checks
        public readonly List<Bounds> masses = new List<Bounds>(); // tower masses only (what decks may attach to)
        public readonly List<Bounds> decks = new List<Bounds>();  // placed skywalk decks
        public readonly List<Vector3> taxiPads = new List<Vector3>();
    }

    public static SkywalkKit CreateSkywalkKit(Kit kit)
    {
        return new SkywalkKit
        {
            deck = LitMaterial("SkywalkDeck", new Color(0.28f, 0.29f, 0.32f)),
            rail = LitMaterial("SkywalkRail", new Color(0.12f, 0.13f, 0.15f)),
            taxiPad = Neon("TaxiPad", new Color(1f, 0.75f, 0.15f), 1.5f),
        };
    }

    // The tower's footprint at height y: masses that start at or below y and reach a few metres above it.
    public static bool FootprintAt(Tower t, float y, out Rect fp)
    {
        bool any = false;
        float x0 = 0, x1 = 0, z0 = 0, z1 = 0;
        foreach (var b in t.masses)
        {
            if (b.min.y > y + 0.5f || b.max.y < y + 4f) continue;
            if (!any) { x0 = b.min.x; x1 = b.max.x; z0 = b.min.z; z1 = b.max.z; any = true; }
            else { x0 = Mathf.Min(x0, b.min.x); x1 = Mathf.Max(x1, b.max.x); z0 = Mathf.Min(z0, b.min.z); z1 = Mathf.Max(z1, b.max.z); }
        }
        fp = Rect.MinMaxRect(x0, z0, x1, z1);
        return any;
    }

    // Ring walkway round a tower at a layer: deck slabs along each side (0 south, 1 north, 2 west, 3 east)
    // `widths[side]` wide (0 = none), falling back to 8 / 4 m where that doesn't fit (lanes, keep-outs,
    // other buildings). Decks only run where a wall of this tower actually backs them at that height
    // (notched / stepped towers get pieces, never a slab hanging off empty space). Railings on the
    // outer edges with a 2 m gap at a taxi pad. Returns pieces built.
    public static int AddSkywalkRing(Tower t, Transform parent, int layer, float[] widths, Kit kit, SkywalkKit sk,
                                     List<WalkRect> walk, System.Random rng)
    {
        float y = layer * kit.spacing;
        if (!FootprintAt(t, y, out Rect fp)) return 0;
        var root = new GameObject($"Skywalk_L{layer}").transform;
        root.SetParent(parent, false);
        int built = 0;
        float wW = widths[2], wE = widths[3];
        for (int side = 0; side < 4; side++)
        {
            float width = widths[side];
            if (width < 2f) continue;
            bool alongX = side < 2;
            float a0 = alongX ? fp.xMin : fp.yMin, a1 = alongX ? fp.xMax : fp.yMax;
            foreach (var run in BackedRuns(t, fp, side, y))
            {
                // Corner extensions for the south / north pieces that reach the footprint's ends.
                float r0 = run.x, r1 = run.y;
                if (alongX && r0 <= a0 + 0.5f) r0 -= wW;
                if (alongX && r1 >= a1 - 0.5f) r1 += wE;
                foreach (float w in new[] { width, 8f, 4f })
                {
                    if (w > width) continue;
                    Rect r;
                    switch (side)
                    {
                        case 0: r = Rect.MinMaxRect(r0, fp.yMin - w, r1, fp.yMin); break;
                        case 1: r = Rect.MinMaxRect(r0, fp.yMax, r1, fp.yMax + w); break;
                        case 2: r = Rect.MinMaxRect(fp.xMin - w, r0, fp.xMin, r1); break;
                        default: r = Rect.MinMaxRect(fp.xMax, r0, fp.xMax + w, r1); break;
                    }
                    if (!PlaceDeck(root, r, y, kit, sk, t, walk, true)) continue;
                    Vector3 outN = side == 0 ? Vector3.back : side == 1 ? Vector3.forward : side == 2 ? Vector3.left : Vector3.right;
                    float edgeLen = alongX ? r.width : r.height;
                    Vector3 edgeMid = new Vector3(r.center.x, y, r.center.y) + outN * ((alongX ? r.height : r.width) * 0.5f - 0.15f);
                    Vector3 along = alongX ? Vector3.right : Vector3.forward;
                    float gap = 2f, half = edgeLen * 0.5f;
                    float gapAt = Mathf.Lerp(-half * 0.5f, half * 0.5f, (float)rng.NextDouble());
                    RailRun(root, edgeMid, along, -half, gapAt - gap * 0.5f, sk);
                    RailRun(root, edgeMid, along, gapAt + gap * 0.5f, half, sk);
                    if (w >= 8f && edgeLen >= 12f)
                    {
                        var pad = Slab(root, "TaxiPad", edgeMid - outN * 2.2f + along * gapAt + Vector3.up * 0.02f,
                                       alongX ? new Vector3(8f, 0.02f, 4f) : new Vector3(4f, 0.02f, 8f), sk.taxiPad);
                        Street(pad.gameObject, kit, false);
                        sk.taxiPads.Add(pad.position);
                    }
                    built++;
                    break;
                }
            }
        }
        return built;
    }

    // Stretches (along the side) where one of the tower's masses forms the wall right behind the
    // footprint edge at height y. Runs shorter than 6 m are dropped.
    static List<Vector2> BackedRuns(Tower t, Rect fp, int side, float y)
    {
        var runs = new List<Vector2>();
        bool alongX = side < 2;
        float a0 = alongX ? fp.xMin : fp.yMin, a1 = alongX ? fp.xMax : fp.yMax;
        float start = float.NaN, last = a0;
        for (float a = a0 + 1f; a <= a1 - 0.99f; a += 2f)
        {
            Vector3 p = side switch
            {
                0 => new Vector3(a, y + 2f, fp.yMin + 0.5f),
                1 => new Vector3(a, y + 2f, fp.yMax - 0.5f),
                2 => new Vector3(fp.xMin + 0.5f, y + 2f, a),
                _ => new Vector3(fp.xMax - 0.5f, y + 2f, a),
            };
            bool backed = false;
            foreach (var b in t.masses)
                if (b.min.y <= y + 0.5f && b.Contains(p)) { backed = true; break; }
            if (backed && float.IsNaN(start)) start = a - 1f;
            if (!backed && !float.IsNaN(start)) { if (a - 1f - start >= 6f) runs.Add(new Vector2(start, a - 1f)); start = float.NaN; }
            last = a;
        }
        if (!float.IsNaN(start) && a1 - start >= 6f) runs.Add(new Vector2(start, a1));
        return runs;
    }

    // A wall of some tower right behind point p (at deck height y)?
    static bool Backed(SkywalkKit sk, Vector3 p, float y)
    {
        foreach (var b in sk.masses)
            if (b.min.y <= y + 0.5f && b.max.y >= y + 3f && b.Contains(p)) return true;
        return false;
    }

    // A walkable bridge deck between two towers (axis-aligned rect at height y), railings on the long
    // sides. False if it would hit a lane, a keep-out or a building.
    // Bridges may run over ring decks (their top sits 2 cm lower, so there's no z-fighting).
    // Both ends must land on a tower wall across the bridge's width.
    public static bool AddSkywalkBridge(Transform parent, Rect r, float y, bool spanAlongX, Kit kit, SkywalkKit sk, List<WalkRect> walk)
    {
        for (int end = 0; end < 2; end++)
            for (int k = -1; k <= 1; k++)
            {
                float across = spanAlongX ? r.center.y + k * r.height * 0.35f : r.center.x + k * r.width * 0.35f;
                float at = spanAlongX ? (end == 0 ? r.xMin - 0.5f : r.xMax + 0.5f) : (end == 0 ? r.yMin - 0.5f : r.yMax + 0.5f);
                Vector3 p = spanAlongX ? new Vector3(at, y + 2f, across) : new Vector3(across, y + 2f, at);
                if (!Backed(sk, p, y)) return false;
            }
        y -= 0.02f;
        if (!PlaceDeck(parent, r, y, kit, sk, null, walk, false)) return false;
        Vector3 along = spanAlongX ? Vector3.right : Vector3.forward;
        Vector3 side = spanAlongX ? Vector3.forward : Vector3.right;
        float halfLen = (spanAlongX ? r.width : r.height) * 0.5f, halfW = (spanAlongX ? r.height : r.width) * 0.5f;
        Vector3 c = new Vector3(r.center.x, y, r.center.y);
        RailRun(parent, c + side * (halfW - 0.15f), along, -halfLen, halfLen, sk);
        RailRun(parent, c - side * (halfW - 0.15f), along, -halfLen, halfLen, sk);
        return true;
    }

    static bool PlaceDeck(Transform parent, Rect r, float y, Kit kit, SkywalkKit sk, Tower own, List<WalkRect> walk, bool checkDecks)
    {
        if (r.width < 2f || r.height < 2f) return false;
        var deck = new Bounds(new Vector3(r.center.x, y - 0.6f, r.center.y), new Vector3(r.width, 1.2f, r.height));
        var space = new Bounds(new Vector3(r.center.x, y + 2f, r.center.y), new Vector3(r.width, 5f, r.height)); // people + headroom
        if (kit.clearance != null && (!kit.clearance.IsClear(deck, LaneClearance) || !kit.clearance.IsClear(space, LaneClearance))) return false;
        if (Blocked(deck, kit)) return false;
        var shrunk = new Bounds(deck.center, deck.size - new Vector3(0.2f, 0f, 0.2f));
        var shrunkSpace = new Bounds(space.center, space.size - new Vector3(0.2f, 0f, 0.2f));
        foreach (var s in sk.solids)
        {
            if (own != null && own.masses.Contains(s)) continue;
            if (s.Intersects(shrunk) || s.Intersects(shrunkSpace)) return false;
        }
        if (checkDecks)
        foreach (var d in sk.decks)
            if (d.Intersects(new Bounds(deck.center, deck.size - new Vector3(0.6f, 0f, 0.6f)))) return false;
        var go = Slab(parent, "SkywalkDeck", deck.center, deck.size, sk.deck).gameObject;
        go.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        MarkStatic(go, true);
        sk.decks.Add(deck);
        walk.Add(new WalkRect { area = new Bounds(new Vector3(r.center.x, y, r.center.y), new Vector3(r.width, 0f, r.height)), skywalk = true });
        return true;
    }

    static void RailRun(Transform parent, Vector3 mid, Vector3 along, float a, float b, SkywalkKit sk)
    {
        if (b - a < 0.5f) return;
        Vector3 c = mid + along * ((a + b) * 0.5f) + Vector3.up * 0.55f;
        Vector3 size = along.x != 0f ? new Vector3(b - a, 1.1f, 0.2f) : new Vector3(0.2f, 1.1f, b - a);
        var go = Slab(parent, "Railing", c, size, sk.rail).gameObject;
        go.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        MarkStatic(go, false);
    }

    // ---------- the hollow tower ----------

    // A tower with an open atrium: solid up to floorLayer (the plaza floor), walls round the atrium up to
    // atriumTop with the side facing `open` cut away (a 40 m opening you can fly or walk into), solid
    // above. The plaza gets benches, stalls and planters (blend spots) and a landing pad; the atrium
    // walls get interior balconies and ledges (LedgeMarker).
    public static Tower BuildHollowTower(Transform parent, string name, Rect fp, Rect atrium, int floorLayer, int atriumTop,
                                         int topLayer, Vector3 open, Color tint, int style, System.Random rng, Kit kit,
                                         out Vector3 plazaCenter, out Vector3 padCenter)
    {
        var t = new Tower { canyonNormal = open, heightLayers = topLayer, tint = tint, tall = topLayer >= 45 };
        t.root = new GameObject(name).transform;
        t.root.SetParent(parent, false);
        t.depthAlongX = Mathf.Abs(open.x) > 0.5f;
        t.dirSign = t.depthAlongX ? open.x : open.z;
        t.canyonFace = t.depthAlongX ? (open.x < 0f ? fp.xMin : fp.xMax) : (open.z < 0f ? fp.yMin : fp.yMax);
        t.lengthCenter = t.depthAlongX ? fp.center.y : fp.center.x;
        t.seed = (float)rng.NextDouble();
        t.style = style;
        t.archetype = Archetype.Slab;

        float s = kit.spacing, yF = floorLayer * s, yT = atriumTop * s, yTop = topLayer * s;
        Bounds Box(float x0, float x1, float z0, float z1, float y0, float y1)
        {
            var b = new Bounds();
            b.SetMinMax(new Vector3(x0, y0, z0), new Vector3(x1, y1, z1));
            return b;
        }
        AddMass(t, Box(fp.xMin, fp.xMax, fp.yMin, fp.yMax, 0f, yF), style, "Base", false, kit);
        AddMass(t, Box(fp.xMin, fp.xMax, atrium.yMax, fp.yMax, yF, yT), style, "AtriumWallN", false, kit);
        AddMass(t, Box(fp.xMin, fp.xMax, fp.yMin, atrium.yMin, yF, yT), style, "AtriumWallS", false, kit);
        // East / west walls between them; the side facing `open` is left out (the opening).
        if (!(open.x > 0.5f)) AddMass(t, Box(atrium.xMax, fp.xMax, atrium.yMin, atrium.yMax, yF, yT), style, "AtriumWallE", false, kit);
        if (!(open.x < -0.5f)) AddMass(t, Box(fp.xMin, atrium.xMin, atrium.yMin, atrium.yMax, yF, yT), style, "AtriumWallW", false, kit);
        AddMass(t, Box(fp.xMin, fp.xMax, fp.yMin, fp.yMax, yT, yTop), style, "Crown", false, kit);

        // Plaza: the floor is the base's roof. Landing pad toward the opening, blend spots elsewhere.
        var plaza = new GameObject("Plaza").transform;
        plaza.SetParent(t.root, false);
        plazaCenter = new Vector3(atrium.center.x, yF, atrium.center.y);
        float openX = open.x < 0f ? fp.xMin : fp.xMax;
        padCenter = new Vector3((openX + (open.x < 0f ? atrium.xMin : atrium.xMax)) * 0.5f, yF, atrium.center.y);
        var padMat = kit.padPaint;
        Flat(plaza, "LandingPad", padCenter + Vector3.up * 0.02f, new Vector3(16f, 0.04f, 16f), padMat, false);
        var edge = kit.neon[0];
        for (int k = 0; k < 4; k++)
        {
            Vector3 o = k == 0 ? Vector3.forward : k == 1 ? Vector3.back : k == 2 ? Vector3.left : Vector3.right;
            Vector3 sz = k < 2 ? new Vector3(16f, 0.05f, 0.25f) : new Vector3(0.25f, 0.05f, 16f);
            Flat(plaza, "PadEdge", padCenter + o * 8f + Vector3.up * 0.04f, sz, edge, false);
        }
        var benchMat = kit.decoDark;
        var planterMat = LitMaterial("Planter", new Color(0.12f, 0.3f, 0.14f));
        for (int i = 0; i < 26; i++)
        {
            Vector3 p = new Vector3(Mathf.Lerp(atrium.xMin + 3f, atrium.xMax - 3f, (float)rng.NextDouble()), yF,
                                    Mathf.Lerp(atrium.yMin + 3f, atrium.yMax - 3f, (float)rng.NextDouble()));
            if ((p - padCenter).sqrMagnitude < 12f * 12f) continue;
            int kind = rng.Next(3);
            if (kind == 0) Solid(plaza, "Bench", p + Vector3.up * 0.25f, new Vector3(2.2f, 0.5f, 0.7f), benchMat);
            else if (kind == 1)
            {
                Flammable.Add(Solid(plaza, "Stall", p + Vector3.up * 1.2f, new Vector3(3f, 2.4f, 2.4f), benchMat).gameObject, Flammable.Kind.Prop);
                var awn = Slab(plaza, "StallAwning", p + Vector3.up * 2.55f, new Vector3(3.6f, 0.15f, 3f), kit.neon[rng.Next(kit.neon.Length)]);
                Street(awn.gameObject, kit, false);
            }
            else Solid(plaza, "Planter", p + Vector3.up * 0.5f, new Vector3(2f, 1f, 2f), planterMat);
        }

        // Interior balconies with ledges on the atrium walls (one per wall per layer, staggered).
        for (int layer = floorLayer + 1; layer < atriumTop; layer++)
        {
            float y = layer * s;
            for (int wall = 0; wall < 4; wall++)
            {
                if (wall == 2 && open.x < -0.5f || wall == 3 && open.x > 0.5f) continue;
                if (rng.NextDouble() < 0.35) continue;
                float u = Mathf.Lerp(0.2f, 0.8f, (float)rng.NextDouble());
                Vector3 c; Vector3 size; Vector3 outN;
                switch (wall)
                {
                    case 0: c = new Vector3(Mathf.Lerp(atrium.xMin, atrium.xMax, u), y, atrium.yMax - 1f); size = new Vector3(8f, 0.3f, 2f); outN = Vector3.back; break;
                    case 1: c = new Vector3(Mathf.Lerp(atrium.xMin, atrium.xMax, u), y, atrium.yMin + 1f); size = new Vector3(8f, 0.3f, 2f); outN = Vector3.forward; break;
                    case 2: c = new Vector3(atrium.xMin + 1f, y, Mathf.Lerp(atrium.yMin, atrium.yMax, u)); size = new Vector3(2f, 0.3f, 8f); outN = Vector3.right; break;
                    default: c = new Vector3(atrium.xMax - 1f, y, Mathf.Lerp(atrium.yMin, atrium.yMax, u)); size = new Vector3(2f, 0.3f, 8f); outN = Vector3.left; break;
                }
                var bal = Slab(plaza, "AtriumBalcony", c - Vector3.up * 0.15f, size, kit.bridge);
                MarkStatic(bal.gameObject, false);
                var ledge = bal.gameObject.AddComponent<LedgeMarker>();
                ledge.outward = outN;
                ledge.width = 8f;
                t.features.Add(new Bounds(c, size));
            }
        }
        return t;
    }

    static Transform Solid(Transform parent, string name, Vector3 center, Vector3 size, Material mat)
    {
        var t = Slab(parent, name, center, size, mat);
        t.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        MarkStatic(t.gameObject, false);
        return t;
    }

    // ---------- market ----------

    // Street market on a block's sidewalks: stalls (solid) with neon awnings and a few blade signs.
    public static void AddMarketStalls(Transform parent, Rect block, System.Random rng, Kit kit)
    {
        var root = new GameObject("Market").transform;
        root.SetParent(parent, false);
        for (int e = 0; e < 4; e++)
        {
            bool alongX = e < 2;
            float len = alongX ? block.width : block.height;
            Vector3 outN = e == 0 ? Vector3.back : e == 1 ? Vector3.forward : e == 2 ? Vector3.left : Vector3.right;
            for (float s = 6f; s < len - 6f; s += Mathf.Lerp(6f, 11f, (float)rng.NextDouble()))
            {
                if (rng.NextDouble() < 0.3) continue;
                Vector3 edge = alongX ? new Vector3(block.xMin + s, 0f, e == 0 ? block.yMin : block.yMax)
                                      : new Vector3(e == 2 ? block.xMin : block.xMax, 0f, block.yMin + s);
                Vector3 c = edge + outN * 1.4f; // against the building side of the sidewalk
                Vector3 size = alongX ? new Vector3(3f, 2.3f, 2f) : new Vector3(2f, 2.3f, 3f);
                var stall = Solid(root, "MarketStall", c + Vector3.up * (KerbHeight + 1.15f), size, kit.decoDark);
                Flammable.Add(stall.gameObject, Flammable.Kind.Prop);
                var awn = Slab(root, "MarketAwning", c + outN * 0.6f + Vector3.up * (KerbHeight + 2.45f),
                               alongX ? new Vector3(3.4f, 0.12f, 3.2f) : new Vector3(3.2f, 0.12f, 3.4f), kit.neon[rng.Next(kit.neon.Length)]);
                Street(awn.gameObject, kit, false);
                if (rng.NextDouble() < 0.25)
                {
                    float h = Mathf.Lerp(2.5f, 4.5f, (float)rng.NextDouble());
                    var sign = Slab(root, "MarketBladeSign", edge + outN * 0.8f + Vector3.up * (4f + h * 0.5f),
                                    alongX ? new Vector3(0.2f, h, 1.4f) : new Vector3(1.4f, h, 0.2f), NeonFor(1f, rng, kit));
                    Street(sign.gameObject, kit, false);
                }
            }
        }
    }
}
