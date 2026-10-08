using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using static FlightGrayboxBuilder;

// Building variation for CityDressing: tower archetypes, recessed floors, bays, balconies, sky bridges,
// cables and extra roof/facade decoration. Same rules as the look pass: built at edit time from the
// city seed; gameplay geometry has colliders and sits on the grid; decoration has no colliders; nothing
// enters the lane clearance; no Light components.
public static partial class CityDressing
{
    public enum Archetype { Slab, PodiumShaft, TwinShafts, Notched, Pyramid }

    // Facade style ids, matching kStyleCell in CityFacade.shader.
    public const int StyleGrid = 0, StyleRibbon = 1, StyleCurtain = 2, StyleResidential = 3, StyleIndustrial = 4;
    public const int StyleStorefront = 5; // shader-only: every building's street level (first 10 m)

    const float MinShaft = 12f;          // narrowest part of any tower (m)
    const float RecessInset = 4f;
    const float ColumnSize = 1.5f;
    const float RailHeight = 1.1f, RailGap = 1.5f, SlabThickness = 0.3f;

    // A box in the tower-local frame: s = depth in from the canyon face, l = along the canyon, y in layers.
    struct Part
    {
        public float s0, s1, l0, l1;
        public int y0, y1;
        public bool podium, recessed;
        public Part(float s0, float s1, float l0, float l1, int y0, int y1, bool podium = false, bool recessed = false)
        {
            this.s0 = s0; this.s1 = s1; this.l0 = l0; this.l1 = l1; this.y0 = y0; this.y1 = y1;
            this.podium = podium; this.recessed = recessed;
        }
        public float Depth => s1 - s0;
        public float Length => l1 - l0;
    }

    static float Rand(System.Random rng, float a, float b) => Mathf.Lerp(a, b, (float)rng.NextDouble());

    // ---------- archetypes ----------

    static Archetype PickArchetype(System.Random rng)
    {
        double r = rng.NextDouble();
        return r < 0.30 ? Archetype.Slab : r < 0.55 ? Archetype.PodiumShaft : r < 0.70 ? Archetype.TwinShafts
             : r < 0.90 ? Archetype.Notched : Archetype.Pyramid;
    }

    // Residential on pyramids and podiums, curtain wall and ribbon mostly on shafts. (Industrial is
    // applied by the shader in the underworld band.)
    static int PickStyle(Archetype a, bool podium, System.Random rng)
    {
        double r = rng.NextDouble();
        if (podium) return r < 0.5 ? StyleResidential : StyleGrid;
        switch (a)
        {
            case Archetype.Slab: return r < 0.5 ? StyleGrid : r < 0.7 ? StyleRibbon : StyleResidential;
            case Archetype.PodiumShaft: return r < 0.45 ? StyleCurtain : r < 0.8 ? StyleRibbon : StyleGrid;
            case Archetype.TwinShafts: return r < 0.5 ? StyleCurtain : StyleRibbon;
            case Archetype.Notched: return r < 0.5 ? StyleGrid : StyleResidential;
            default: return r < 0.7 ? StyleResidential : StyleGrid; // pyramid
        }
    }

    // `style`: force one facade style for the whole tower (district builders), else picked per archetype.
    static void BuildArchetype(Tower t, Archetype a, float D, float L, int H, bool allowRecess, System.Random rng, Kit kit, int? style = null)
    {
        var parts = new List<Part>();
        switch (a)
        {
            case Archetype.PodiumShaft: PodiumShaft(parts, D, L, H, rng); break;
            case Archetype.TwinShafts: if (!TwinShafts(t, parts, D, L, H, rng)) { a = Archetype.PodiumShaft; PodiumShaft(parts, D, L, H, rng); } break;
            case Archetype.Notched: Notched(parts, D, L, H, rng); break;
            case Archetype.Pyramid: Pyramid(parts, D, L, H, rng); break;
            default: SlabArchetype(parts, D, L, H, rng); break;
        }
        t.archetype = a;
        t.style = PickStyle(a, false, rng);
        int podiumStyle = PickStyle(a, true, rng);
        if (style.HasValue) t.style = podiumStyle = style.Value;

        var columns = new List<Part>();
        if (allowRecess) AddRecesses(parts, columns, rng);

        foreach (var p in parts)
            AddMass(t, t.Frame(p.s0, p.s1, p.l0, p.l1, p.y0 * kit.spacing, p.y1 * kit.spacing),
                    p.podium ? podiumStyle : t.style, p.recessed ? "RecessedFloor" : p.podium ? "Podium" : "Mass", p.recessed, kit);
        foreach (var c in columns)
        {
            var b = t.Frame(c.s0, c.s1, c.l0, c.l1, c.y0 * kit.spacing, c.y1 * kit.spacing);
            MarkStatic(Slab(t.root, "Column", b.center, b.size, kit.bridge).gameObject, true);
            t.features.Add(b);
        }
    }

    // Base plus 1-3 setbacks (the original look-pass tower).
    static void SlabArchetype(List<Part> parts, float D, float L, int H, System.Random rng)
    {
        int setbacks = 1 + rng.Next(3);
        int baseTop = Mathf.Clamp(Mathf.RoundToInt(H * Rand(rng, 0.35f, 0.6f)), 3, Mathf.Max(3, H - setbacks));
        parts.Add(new Part(0f, D, -L * 0.5f, L * 0.5f, 0, baseTop));
        AddSetbacks(parts, D, L, baseTop, H, setbacks, 0.75f, 0.9f, rng);
    }

    // Each setback is narrower (shrinking from the alleys and the rear; the canyon face stays put).
    static void AddSetbacks(List<Part> parts, float D, float L, int bottom, int H, int count, float sMin, float sMax, System.Random rng)
    {
        for (int s = 1; s <= count && bottom < H; s++)
        {
            int remaining = H - bottom;
            int step = s == count ? remaining
                     : Mathf.Clamp(Mathf.RoundToInt(remaining * Rand(rng, 0.3f, 0.6f)), 1, Mathf.Max(1, remaining - (count - s)));
            D = Mathf.Max(MinShaft, D * Rand(rng, sMin, sMax));
            L = Mathf.Max(MinShaft, L * Rand(rng, sMin, sMax));
            parts.Add(new Part(0f, D, -L * 0.5f, L * 0.5f, bottom, bottom + step));
            bottom += step;
        }
    }

    // Wide podium (3-6 layers, its roof a big terrace) and a much narrower shaft, optionally one setback.
    static void PodiumShaft(List<Part> parts, float D, float L, int H, System.Random rng)
    {
        int P = Mathf.Clamp(3 + rng.Next(4), 3, H - 3);
        parts.Add(new Part(0f, D, -L * 0.5f, L * 0.5f, 0, P, podium: true));
        float f = Rand(rng, 0.4f, 0.6f);
        float sd = Mathf.Max(MinShaft, D * f), sl = Mathf.Max(MinShaft, L * f);
        if (rng.NextDouble() < 0.5 || H - P < 4)
        {
            parts.Add(new Part(0f, sd, -sl * 0.5f, sl * 0.5f, P, H));
            return;
        }
        int mid = Mathf.Clamp(P + Mathf.RoundToInt((H - P) * Rand(rng, 0.6f, 0.85f)), P + 1, H - 1);
        parts.Add(new Part(0f, sd, -sl * 0.5f, sl * 0.5f, P, mid));
        float sd2 = Mathf.Max(MinShaft * 0.85f, sd * 0.85f), sl2 = Mathf.Max(MinShaft * 0.85f, sl * 0.85f);
        parts.Add(new Part(0f, sd2, -sl2 * 0.5f, sl2 * 0.5f, mid, H));
    }

    // Shared podium, two shafts side by side along the canyon with a 10-14 m slot between them
    // (a wall-bounce slot; joined later by sky bridges). False if the footprint is too short.
    static bool TwinShafts(Tower t, List<Part> parts, float D, float L, int H, System.Random rng)
    {
        float gap = Rand(rng, 10f, 14f);
        float shaftLen = (L - gap) * 0.5f;
        if (shaftLen < MinShaft || H < 8) return false;
        int P = Mathf.Clamp(3 + rng.Next(4), 3, H - 4);
        parts.Add(new Part(0f, D, -L * 0.5f, L * 0.5f, 0, P, podium: true));
        float dA = Mathf.Max(MinShaft, D * Rand(rng, 0.6f, 0.9f)), dB = Mathf.Max(MinShaft, D * Rand(rng, 0.6f, 0.9f));
        int hB = Mathf.Clamp(Mathf.RoundToInt(H * Rand(rng, 0.7f, 1f)), P + 3, H);
        parts.Add(new Part(0f, dA, -L * 0.5f, -gap * 0.5f, P, H));
        parts.Add(new Part(0f, dB, gap * 0.5f, L * 0.5f, P, hB));
        t.hasTwinGap = true;
        t.twinGapL = new Vector2(-gap * 0.5f, gap * 0.5f);
        t.twinGapY = new Vector2Int(P, Mathf.Min(H, hB));
        t.twinGapDepth = Mathf.Min(dA, dB);
        return true;
    }

    // Slab with 1-2 rear corners cut out (full or partial height): L and T plans. The canyon side
    // keeps its full width.
    static void Notched(List<Part> parts, float D, float L, int H, System.Random rng)
    {
        int setbacks = 1 + rng.Next(2);
        int baseTop = Mathf.Clamp(Mathf.RoundToInt(H * Rand(rng, 0.4f, 0.7f)), 4, Mathf.Max(4, H - setbacks));
        int notches = 1 + rng.Next(2);
        float nd = D * Rand(rng, 0.3f, 0.5f), nl = L * Rand(rng, 0.25f, 0.4f);
        int side = rng.Next(2) == 0 ? -1 : 1;
        int na = 0, nb = baseTop;
        double v = rng.NextDouble();
        if (v < 0.33) nb = Mathf.Clamp(2 + rng.Next(Mathf.Max(1, baseTop - 3)), 2, baseTop - 1);       // notch at the bottom
        else if (v < 0.66) na = Mathf.Clamp(2 + rng.Next(Mathf.Max(1, baseTop - 3)), 1, baseTop - 2);  // notch at the top

        float front = D - nd;
        parts.Add(new Part(0f, front, -L * 0.5f, L * 0.5f, 0, baseTop));
        float rl0 = -L * 0.5f, rl1 = L * 0.5f;
        if (notches == 2) { rl0 += nl; rl1 -= nl; }
        else if (side > 0) rl1 -= nl;
        else rl0 += nl;
        parts.Add(new Part(front, D, rl0, rl1, na, nb));
        if (na > 0) parts.Add(new Part(front, D, -L * 0.5f, L * 0.5f, 0, na));
        if (nb < baseTop) parts.Add(new Part(front, D, -L * 0.5f, L * 0.5f, nb, baseTop));
        AddSetbacks(parts, D, L, baseTop, H, setbacks, 0.75f, 0.9f, rng);
    }

    // Base plus 4-5 setbacks of 1-3 layers each: a stack of walkable terraces.
    static void Pyramid(List<Part> parts, float D, float L, int H, System.Random rng)
    {
        int tiers = 4 + rng.Next(2);
        var heights = new int[tiers];
        int sum = 0;
        for (int i = 0; i < tiers; i++) { heights[i] = 1 + rng.Next(3); sum += heights[i]; }
        while (H - sum < 3 && sum > tiers)
            for (int i = 0; i < tiers && H - sum < 3; i++) if (heights[i] > 1) { heights[i]--; sum--; }
        int b = Mathf.Max(3, H - sum);
        parts.Add(new Part(0f, D, -L * 0.5f, L * 0.5f, 0, b));
        float d = D, l = L;
        for (int i = 0; i < tiers && b < H; i++)
        {
            d = Mathf.Max(MinShaft, d * Rand(rng, 0.8f, 0.9f));
            l = Mathf.Max(MinShaft, l * Rand(rng, 0.8f, 0.9f));
            int top = i == tiers - 1 ? H : Mathf.Min(H, b + heights[i]);
            parts.Add(new Part(0f, d, -l * 0.5f, l * 0.5f, b, top));
            b = top;
        }
    }

    // 0-2 open "sky lobby" floors: one layer of a part becomes an inner box inset 4 m, with corner
    // columns. The floor (the lower box's roof) is walkable; the part above stays full size.
    static void AddRecesses(List<Part> parts, List<Part> columns, System.Random rng)
    {
        int count = rng.Next(3);
        for (int n = 0; n < count; n++)
        {
            var candidates = new List<int>();
            for (int i = 0; i < parts.Count; i++)
                if (!parts[i].recessed && parts[i].y1 - parts[i].y0 >= 5 && parts[i].Depth >= 16f && parts[i].Length >= 16f)
                    candidates.Add(i);
            if (candidates.Count == 0) return;
            int idx = candidates[rng.Next(candidates.Count)];
            var p = parts[idx];
            int layer = p.y0 + 2 + rng.Next(p.y1 - p.y0 - 4);   // y0+2 .. y1-3

            parts[idx] = new Part(p.s0, p.s1, p.l0, p.l1, p.y0, layer, p.podium);
            parts.Add(new Part(p.s0 + RecessInset, p.s1 - RecessInset, p.l0 + RecessInset, p.l1 - RecessInset, layer, layer + 1, p.podium, recessed: true));
            parts.Add(new Part(p.s0, p.s1, p.l0, p.l1, layer + 1, p.y1, p.podium));
            for (int cs = 0; cs < 2; cs++)
            for (int cl = 0; cl < 2; cl++)
            {
                float s0 = cs == 0 ? p.s0 : p.s1 - ColumnSize, l0 = cl == 0 ? p.l0 : p.l1 - ColumnSize;
                columns.Add(new Part(s0, s0 + ColumnSize, l0, l0 + ColumnSize, layer, layer + 1));
            }
        }
    }

    // ---------- faces ----------

    enum FaceKind { Canyon, Rear, AlleyMinus, AlleyPlus }

    static void Face(Tower t, Bounds b, FaceKind kind, out Vector3 normal, out Vector3 center, out Vector3 tangent, out float length)
    {
        Vector3 along = t.Along, across = t.Across;
        switch (kind)
        {
            case FaceKind.Canyon: normal = t.canyonNormal; tangent = along; break;
            case FaceKind.Rear: normal = -t.canyonNormal; tangent = along; break;
            case FaceKind.AlleyMinus: normal = -along; tangent = across; break;
            default: normal = along; tangent = across; break;
        }
        center = b.center + Vector3.Scale(normal, b.extents);
        length = Vector3.Dot(b.size, tangent);
    }

    // Is this mass part of the continuous canyon wall?
    static bool IsFlush(Tower t, Bounds b)
    {
        float face = Vector3.Dot(b.center, t.Across) + t.dirSign * Vector3.Dot(b.extents, t.Across);
        return Mathf.Abs(face - t.canyonFace) < 0.05f;
    }

    // Another mass of this tower sits directly on top of b (b's roof is a setback line).
    static bool MassAbove(Tower t, Bounds b)
    {
        foreach (var o in t.masses)
            if (Mathf.Abs(o.min.y - b.max.y) < 0.01f && o.min.x < b.max.x && o.max.x > b.min.x && o.min.z < b.max.z && o.max.z > b.min.z)
                return true;
        return false;
    }

    // ClimbAlley wall, or the narrow slot between two parts of the tower (twin shafts): keep it flat.
    static bool IsClimbFace(Tower t, Bounds b, int side)
    {
        if (side < 0 ? t.climbMinus : t.climbPlus) return true;
        Vector3 along = t.Along, across = t.Across;
        float face = Vector3.Dot(b.center, along) + side * Vector3.Dot(b.extents, along);
        foreach (var o in t.masses)
        {
            if (o == b || o.max.y <= b.min.y || o.min.y >= b.max.y) continue;
            float a0 = Vector3.Dot(o.min, across), a1 = Vector3.Dot(o.max, across);
            float b0 = Vector3.Dot(b.min, across), b1 = Vector3.Dot(b.max, across);
            if (a1 <= b0 || a0 >= b1) continue;
            float otherFace = Vector3.Dot(o.center, along) - side * Vector3.Dot(o.extents, along);
            float gap = (otherFace - face) * side;
            if (gap > 0.5f && gap < 15f) return true;
        }
        return false;
    }

    // Bays and balconies: canyon face only outside the traffic band; never on climb walls.
    static bool FaceAllowed(Tower t, Bounds b, FaceKind kind, float y0, float y1, Kit kit)
    {
        switch (kind)
        {
            case FaceKind.Canyon: return y1 <= kit.trafficMin || y0 >= kit.trafficMax;
            case FaceKind.Rear: return true;
            case FaceKind.AlleyMinus: return !IsClimbFace(t, b, -1);
            default: return !IsClimbFace(t, b, 1);
        }
    }

    static bool HitsFeature(Tower t, Bounds b)
    {
        foreach (var f in t.features) if (f.Intersects(b)) return true;
        return false;
    }

    // Free space for new gameplay geometry: clear of lanes, keep-outs, this tower's masses and features.
    static bool Free(Tower t, Bounds b, Kit kit)
    {
        if (!kit.clearance.IsClear(b, LaneClearance) || Blocked(b, kit) || HitsFeature(t, b)) return false;
        var shrunk = new Bounds(b.center, b.size - Vector3.one * 0.1f);
        foreach (var m in t.masses) if (m.Intersects(shrunk)) return false;
        return true;
    }

    // ---------- bays ----------

    // 0-3 boxes stuck onto faces: 4-12 m wide, 2-5 m deep, 1-4 layers tall, tops walkable.
    public static void AddBays(Tower t, System.Random rng, Kit kit)
    {
        int count = rng.Next(4);
        int baseCount = t.masses.Count;
        for (int i = 0; i < count; i++)
        {
            int mi = rng.Next(baseCount);
            if (t.massRecessed[mi]) continue;
            var b = t.masses[mi];
            var kind = (FaceKind)rng.Next(4);
            int lo = Mathf.Max(1, Mathf.RoundToInt(b.min.y / kit.spacing)), hi = Mathf.RoundToInt(b.max.y / kit.spacing);
            int span = 1 + rng.Next(4);
            if (hi - lo < span) continue;
            int y0 = lo + rng.Next(hi - lo - span + 1), y1 = y0 + span;
            if (!FaceAllowed(t, b, kind, y0 * kit.spacing, y1 * kit.spacing, kit)) continue;

            Face(t, b, kind, out var nrm, out var center, out var tan, out float faceLen);
            float w = Mathf.Min(Rand(rng, 4f, 12f), faceLen - 2f);
            if (w < 4f) continue;
            float d = Rand(rng, 2f, 5f);
            float slide = Mathf.Max(0f, (faceLen - w) * 0.5f - 1f);
            var c = center + nrm * (d * 0.5f) + tan * Rand(rng, -slide, slide);
            c.y = (y0 + y1) * 0.5f * kit.spacing;
            var bay = new Bounds(c, Abs(tan) * w + Abs(nrm) * d + Vector3.up * (span * kit.spacing));
            if (!Free(t, bay, kit)) continue;
            AddMass(t, bay, t.massStyle[mi], "Bay", false, kit);
        }
    }

    // ---------- balconies ----------

    // Balconies are the ledge / firing-position system: slabs on 10 m lines, railings with 1.5 m gaps
    // at each end, a LedgeMarker each. Residential masses get them on 30-60% of their layer lines,
    // office styles rarely, industrial never.
    public static void AddBalconies(Tower t, System.Random rng, Kit kit)
    {
        int count = t.masses.Count;
        var root = new GameObject("Balconies").transform;
        root.SetParent(t.root, false);
        for (int mi = 0; mi < count; mi++)
        {
            if (t.massRecessed[mi]) continue;
            var b = t.masses[mi];
            int style = t.massStyle[mi];
            float chance = style == StyleResidential ? Rand(rng, 0.3f, 0.6f) : style == StyleIndustrial ? 0f : 0.05f;
            if (chance <= 0f) continue;
            int lo = Mathf.Max(1, Mathf.RoundToInt(b.min.y / kit.spacing) + 1), hi = Mathf.RoundToInt(b.max.y / kit.spacing) - 1;
            for (int n = lo; n <= hi; n++)
            {
                if (rng.NextDouble() >= chance) continue;
                float y = n * kit.spacing;
                double type = rng.NextDouble();
                if (style == StyleResidential && type < 0.25 && WrapAllowed(t, b, y, kit)) Wrap(t, b, y, root, rng, kit);
                else if (style == StyleResidential && type < 0.6) OnRandomFace(t, b, y, true, root, rng, kit);
                else OnRandomFace(t, b, y, false, root, rng, kit);
            }
        }
    }

    static bool WrapAllowed(Tower t, Bounds b, float y, Kit kit) =>
        FaceAllowed(t, b, FaceKind.Canyon, y - SlabThickness, y + RailHeight, kit) &&
        FaceAllowed(t, b, FaceKind.AlleyMinus, y, y, kit) && FaceAllowed(t, b, FaceKind.AlleyPlus, y, y, kit);

    static void OnRandomFace(Tower t, Bounds b, float y, bool row, Transform root, System.Random rng, Kit kit)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var kind = (FaceKind)rng.Next(4);
            if (!FaceAllowed(t, b, kind, y - SlabThickness, y + RailHeight, kit)) continue;
            Face(t, b, kind, out _, out _, out _, out float faceLen);
            float width = row ? faceLen - 2f : Rand(rng, 3f, 5f);
            float depth = row ? 1.5f : Rand(rng, 1.5f, 2f);
            if (width < 3f) continue;
            float slide = Mathf.Max(0f, (faceLen - width) * 0.5f - 0.5f);
            if (Balcony(t, b, kind, y, width, row ? 0f : Rand(rng, -slide, slide), depth, row, root, rng, kit)) return;
        }
    }

    // Wrap-around terrace: a 2 m ring on all four faces (front/back slabs cover the corners).
    static void Wrap(Tower t, Bounds b, float y, Transform root, System.Random rng, Kit kit)
    {
        const float depth = 2f;
        foreach (var kind in new[] { FaceKind.Canyon, FaceKind.Rear })
        {
            Face(t, b, kind, out _, out _, out _, out float len);
            Balcony(t, b, kind, y, len + depth * 2f, 0f, depth, false, root, rng, kit);
        }
        foreach (var kind in new[] { FaceKind.AlleyMinus, FaceKind.AlleyPlus })
        {
            Face(t, b, kind, out _, out _, out _, out float len);
            Balcony(t, b, kind, y, len, 0f, depth, false, root, rng, kit);
        }
    }

    static bool Balcony(Tower t, Bounds host, FaceKind kind, float y, float width, float offset, float depth, bool row,
                        Transform root, System.Random rng, Kit kit)
    {
        Face(t, host, kind, out var nrm, out var center, out var tan, out _);
        var c = center + nrm * (depth * 0.5f) + tan * offset;
        c.y = y - SlabThickness * 0.5f;
        var slabSize = Abs(tan) * width + Abs(nrm) * depth + Vector3.up * SlabThickness;
        var total = new Bounds(c + Vector3.up * (RailHeight * 0.5f), slabSize + Vector3.up * RailHeight);
        if (!Free(t, total, kit)) return false;
        t.features.Add(total);

        var slab = Slab(root, row ? "BalconyRow" : "Balcony", c, slabSize, kit.bridge);
        var marker = slab.gameObject.AddComponent<LedgeMarker>();
        marker.outward = nrm;
        marker.width = width;
        MarkStatic(slab.gameObject, true);

        float railLen = width - RailGap * 2f;
        if (railLen >= 0.5f)
            MarkStatic(Slab(root, "Railing", c + nrm * (depth * 0.5f - 0.05f) + Vector3.up * (SlabThickness * 0.5f + RailHeight * 0.5f),
                            Abs(tan) * railLen + Abs(nrm) * 0.08f + Vector3.up * RailHeight, kit.bridge).gameObject, true);

        // Small signs over some balcony rows (decoration).
        if (row && rng.NextDouble() < 0.3)
            Box(root, "RowSign", new Vector3(center.x, y + 2.4f, center.z) + nrm * 0.06f + tan * Rand(rng, -width * 0.3f, width * 0.3f),
                Abs(tan) * 2.4f + Abs(nrm) * 0.12f + Vector3.up * 0.6f, NeonFor(y, rng, kit), kit);
        return true;
    }

    // ---------- sky bridges between twin shafts ----------

    public static void AddTwinBridges(Tower t, System.Random rng, Kit kit)
    {
        if (!t.hasTwinGap) return;
        int lo = t.twinGapY.x + 2, hi = t.twinGapY.y - 1;
        if (hi < lo || t.twinGapDepth < 9f) return;
        int count = 1 + rng.Next(2);
        int last = -1;
        for (int i = 0; i < count; i++)
        {
            int layer = lo + rng.Next(hi - lo + 1);
            if (layer == last) continue;
            last = layer;
            float top = layer * kit.spacing;
            var deck = t.Frame(3f, 7f, t.twinGapL.x - 0.5f, t.twinGapL.y + 0.5f, top - 0.5f, top);
            var total = new Bounds(deck.center + Vector3.up * 0.8f, deck.size + Vector3.up * 1.6f);
            if (!kit.clearance.IsClear(total, LaneClearance) || Blocked(total, kit) || HitsFeature(t, total)) continue;
            t.features.Add(total);

            var root = new GameObject("SkyBridge").transform;
            root.SetParent(t.root, false);
            MarkStatic(Slab(root, "Deck", deck.center, deck.size, kit.bridge).gameObject, true);
            Vector3 along = t.Along, across = t.Across;
            float span = deck.size[t.depthAlongX ? 2 : 0];
            for (int s = -1; s <= 1; s += 2)
                MarkStatic(Slab(root, "Railing", deck.center + across * (s * 1.95f) + Vector3.up * (0.25f + RailHeight * 0.5f),
                                along * (span - 1f) + across * 0.1f + Vector3.up * RailHeight, kit.bridge).gameObject, true);
            Box(root, "UnderGlow", deck.center - Vector3.up * 0.3f, along * (span - 1f) + across * 0.3f + Vector3.up * 0.1f,
                kit.neon[rng.Next(kit.neon.Length)], kit);
        }
    }

    // ---------- cables across alleys ----------

    // 0-3 sagging cables between neighbouring towers' facing alley walls, above or below the traffic band.
    public static void AddCables(Transform parent, Tower a, Tower b, System.Random rng, Kit kit)
    {
        int count = rng.Next(4);
        for (int i = 0; i < count; i++)
        {
            bool below = rng.NextDouble() < 0.5;
            float y = below ? Rand(rng, 20f, kit.trafficMin - 5f)
                            : Rand(rng, kit.trafficMax + 5f, Mathf.Min(a.heightLayers, b.heightLayers) * kit.spacing - 10f);
            if (!MassAt(a, y, out var ma) || !MassAt(b, y, out var mb)) continue;
            Vector3 along = a.Along, across = a.Across;
            float minDepth = Mathf.Min(Vector3.Dot(ma.size, across), Vector3.Dot(mb.size, across));
            if (minDepth < 10f) continue;
            float s = Rand(rng, 4f, minDepth - 4f);
            float acrossCoord = a.canyonFace - a.dirSign * s;
            Vector3 pa = across * acrossCoord + along * (Vector3.Dot(ma.center, along) + Vector3.Dot(ma.extents, along)) + Vector3.up * y;
            Vector3 pb = across * acrossCoord + along * (Vector3.Dot(mb.center, along) - Vector3.Dot(mb.extents, along)) + Vector3.up * y;
            float sag = Rand(rng, 1f, 3f);
            const int segments = 6;
            Vector3 prev = pa;
            for (int k = 1; k <= segments; k++)
            {
                float u = k / (float)segments;
                Vector3 p = Vector3.Lerp(pa, pb, u) - Vector3.up * (sag * 4f * u * (1f - u));
                Segment(parent, "Cable", prev, p, 0.08f, kit.decoDark, kit);
                prev = p;
            }
        }
    }

    // ---------- extra decoration ----------

    // Roof props, kept off the parts of the roof covered by another mass. Big roofs sometimes get a
    // landing pad marking (painted square + emissive edge ring; the roof itself is the walkable surface).
    static void RoofDressing(Tower t, Bounds b, Transform deco, System.Random rng, Kit kit)
    {
        float sx = b.size.x, sz = b.size.z;
        if (sx < 8f || sz < 8f) return;
        float roofY = b.max.y;
        Bounds? pad = null;
        if (sx >= 22f && sz >= 22f && rng.NextDouble() < 0.3)
        {
            var c = new Vector3(b.center.x, roofY, b.center.z);
            if (!RoofCovered(t, b, c, 8f))
            {
                const float size = 12f;
                Box(deco, "PadPaint", c + Vector3.up * 0.02f, new Vector3(size, 0.04f, size), kit.padPaint, kit);
                var ring = rng.NextDouble() < 0.5 ? kit.neon[0] : kit.neon[2];
                for (int s = -1; s <= 1; s += 2)
                {
                    Box(deco, "PadRing", c + new Vector3(s * size * 0.5f, 0.05f, 0f), new Vector3(0.3f, 0.06f, size), ring, kit);
                    Box(deco, "PadRing", c + new Vector3(0f, 0.05f, s * size * 0.5f), new Vector3(size, 0.06f, 0.3f), ring, kit);
                }
                pad = new Bounds(c, new Vector3(size + 4f, 10f, size + 4f));
            }
        }

        int props = 1 + rng.Next(4);
        for (int i = 0; i < props; i++)
        {
            var p = new Vector3(Rand(rng, b.min.x + 3f, b.max.x - 3f), roofY, Rand(rng, b.min.z + 3f, b.max.z - 3f));
            int kind = rng.Next(6);
            if (RoofCovered(t, b, p, 2.5f) || (pad.HasValue && pad.Value.Contains(p))) continue;
            switch (kind)
            {
                case 0: // water tank
                    Cylinder(deco, "WaterTank", p + Vector3.up * 1.6f, new Vector3(3f, 1.6f, 3f), kit.decoDark, kit);
                    break;
                case 1: // cooling unit with a fan
                    Box(deco, "CoolingUnit", p + Vector3.up * 0.75f, new Vector3(3f, 1.5f, 2f), kit.decoDark, kit);
                    Cylinder(deco, "Fan", p + Vector3.up * 1.55f, new Vector3(1.4f, 0.05f, 1.4f), kit.decoDark, kit);
                    break;
                case 2: // antenna mast
                    float h = Rand(rng, 6f, 12f);
                    Cylinder(deco, "Mast", p + Vector3.up * h * 0.5f, new Vector3(0.3f, h * 0.5f, 0.3f), kit.decoDark, kit);
                    break;
                case 3: // satellite dish on a post
                    Cylinder(deco, "DishPost", p + Vector3.up * 0.8f, new Vector3(0.25f, 0.8f, 0.25f), kit.decoDark, kit);
                    Rotated(PrimitiveType.Cylinder, deco, "Dish", p + Vector3.up * 1.8f, Quaternion.Euler(35f, Rand(rng, 0f, 360f), 0f),
                            new Vector3(2.2f, 0.08f, 2.2f), new Bounds(p + Vector3.up * 1.8f, Vector3.one * 2.4f), kit.decoDark, kit);
                    break;
                case 4: // small rooftop shack
                    Box(deco, "Shack", p + Vector3.up * 1.5f, new Vector3(4f, 3f, 3f), kit.decoDark, kit);
                    break;
                default:
                    Box(deco, "Vent", p + Vector3.up * 0.6f, new Vector3(2f, 1.2f, 2f), kit.decoDark, kit);
                    break;
            }
        }
    }

    // Any mass or feature standing on this roof near p.
    static bool RoofCovered(Tower t, Bounds roofOf, Vector3 p, float margin)
    {
        float y = roofOf.max.y + 0.05f;
        foreach (var o in t.masses)
            if (o.min.y <= y && o.max.y > y && p.x > o.min.x - margin && p.x < o.max.x + margin && p.z > o.min.z - margin && p.z < o.max.z + margin)
                return true;
        foreach (var o in t.features)
            if (o.min.y <= y + 1f && o.max.y > y && p.x > o.min.x - margin && p.x < o.max.x + margin && p.z > o.min.z - margin && p.z < o.max.z + margin)
                return true;
        return false;
    }

    // Three parallel pipes on a LedgeAlley wall (within the 0.3 m depth limit).
    static void PipeBundle(Transform deco, Bounds b, Vector3 wallNormal, Vector3 across, System.Random rng, Kit kit)
    {
        float len = Mathf.Min(b.size.y - 2f, Rand(rng, 15f, 40f));
        if (len < 6f) return;
        Vector3 wallCenter = b.center + Vector3.Scale(wallNormal, b.extents);
        float wallW = Vector3.Dot(b.size, across);
        float y0 = Rand(rng, b.min.y, b.max.y - len);
        float x = Rand(rng, -wallW * 0.4f, wallW * 0.4f);
        for (int i = 0; i < 3; i++)
        {
            var c = new Vector3(wallCenter.x, y0 + len * 0.5f, wallCenter.z) + across * (x + (i - 1) * 0.35f) + wallNormal * 0.14f;
            Cylinder(deco, "PipeBundle", c, new Vector3(0.22f, len * 0.5f, 0.22f), kit.decoDark, kit);
        }
    }

    // Thin cylinder between two points (cables).
    static void Segment(Transform parent, string name, Vector3 a, Vector3 b, float thickness, Material mat, Kit kit)
    {
        Vector3 d = b - a;
        float len = d.magnitude;
        if (len < 0.01f) return;
        var bounds = new Bounds((a + b) * 0.5f, Vector3.zero);
        bounds.Encapsulate(a);
        bounds.Encapsulate(b);
        bounds.Expand(thickness);
        Rotated(PrimitiveType.Cylinder, parent, name, (a + b) * 0.5f, Quaternion.FromToRotation(Vector3.up, d / len),
                new Vector3(thickness, len * 0.5f, thickness), bounds, mat, kit);
    }

    // Collider-free decoration with a rotation. Same lane / keep-out checks as Deco.
    static void Rotated(PrimitiveType type, Transform parent, string name, Vector3 center, Quaternion rot, Vector3 scale,
                        Bounds bounds, Material mat, Kit kit)
    {
        if (!kit.clearance.IsClear(bounds, LaneClearance) || Blocked(bounds, kit)) return;
        var go = GameObject.CreatePrimitive(type);
        go.name = name;
        Object.DestroyImmediate(go.GetComponent<Collider>());
        go.transform.SetParent(parent, false);
        go.transform.SetPositionAndRotation(center, rot);
        go.transform.localScale = scale;
        var r = go.GetComponent<Renderer>();
        r.sharedMaterial = mat;
        r.shadowCastingMode = ShadowCastingMode.Off;
        go.layer = kit.detailLayer;
        MarkStatic(go, false);
    }
}
