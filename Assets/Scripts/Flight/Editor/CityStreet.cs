using System.Collections.Generic;
using UnityEngine;
using static FlightGrayboxBuilder;

// Street level (y = 0) of a canyon between two building rows along Z: walkable, with colliders on
// everything you can stand on. The canyon floor is a road (4 marked lanes, 2 per direction, plus
// loading strips out to the kerbs), with 6 m sidewalks along the tower bases and paved alleys between
// towers. Dressing: emissive street lamps every 30 m (no Light components), a few street-level blade
// signs, vents and pipes. Storefront windows come from the facade shader (first 10 m of every building).
public static partial class CityDressing
{
    public const float SidewalkWidth = 6f;
    const float KerbHeight = 0.15f;
    public const float StreetLaneWidth = 3.5f;
    const float LampSpacing = 30f;
    const float LampHeight = 7f;
    const float MarkingDash = 4f, MarkingGap = 8f;
    const int StreetBladeSigns = 14;

    // One building plot: side (-1 west, +1 east), footprint centre and size (x = depth, y = length along Z).
    public struct StreetPlot
    {
        public int side;
        public Vector3 center;
        public Vector2 footprint;
    }

    public static void BuildStreet(Transform parent, float halfW, float halfL, List<StreetPlot> plots, System.Random rng, Kit kit)
    {
        var root = new GameObject("Street").transform;
        root.SetParent(parent, false);
        var asphalt = LitMaterial("StreetAsphalt", new Color(0.06f, 0.06f, 0.07f));
        var paving = LitMaterial("StreetPaving", new Color(0.2f, 0.2f, 0.21f));
        var marking = Neon("StreetMarking", new Color(0.75f, 0.75f, 0.7f), 0.6f);       // under the bloom threshold
        var centerLine = Neon("StreetCenterLine", new Color(0.9f, 0.65f, 0.15f), 0.7f);
        var lampHead = Neon("StreetLamp", new Color(1f, 0.82f, 0.6f), 3f);

        float length = halfL * 2f;
        float roadHalf = halfW - SidewalkWidth;

        // Road surface: the ground plane under it collides; this is just the darker top.
        Flat(root, "Road", new Vector3(0f, 0.005f, 0f), new Vector3(roadHalf * 2f, 0.01f, length), asphalt, false);

        // Markings: double centre line, dashed lane dividers, solid edge lines.
        float lineY = 0.015f;
        for (int s = -1; s <= 1; s += 2)
        {
            Flat(root, "CenterLine", new Vector3(s * 0.2f, lineY, 0f), new Vector3(0.15f, 0.01f, length), centerLine, false);
            Flat(root, "EdgeLine", new Vector3(s * StreetLaneWidth * 2f, lineY, 0f), new Vector3(0.2f, 0.01f, length), marking, false);
            for (float z = -halfL; z + MarkingDash <= halfL; z += MarkingDash + MarkingGap)
                Flat(root, "LaneDash", new Vector3(s * StreetLaneWidth, lineY, z + MarkingDash * 0.5f), new Vector3(0.15f, 0.01f, MarkingDash), marking, false);
        }

        // Sidewalks along the tower bases (full canyon length), kerb height, with colliders.
        for (int s = -1; s <= 1; s += 2)
            Flat(root, "Sidewalk", new Vector3(s * (halfW - SidewalkWidth * 0.5f), KerbHeight * 0.5f, 0f),
                 new Vector3(SidewalkWidth, KerbHeight, length), paving, true);

        // Alleys between neighbouring towers of a row: paved at sidewalk height.
        for (int s = -1; s <= 1; s += 2)
        {
            var row = plots.FindAll(p => p.side == s);
            row.Sort((a, b) => a.center.z.CompareTo(b.center.z));
            for (int i = 0; i + 1 < row.Count; i++)
            {
                float z0 = row[i].center.z + row[i].footprint.y * 0.5f, z1 = row[i + 1].center.z - row[i + 1].footprint.y * 0.5f;
                if (z1 - z0 < 0.5f) continue;
                float depth = Mathf.Max(row[i].footprint.x, row[i + 1].footprint.x);
                Flat(root, "AlleyPaving", new Vector3(s * (halfW + depth * 0.5f), KerbHeight * 0.5f, (z0 + z1) * 0.5f),
                     new Vector3(depth, KerbHeight, z1 - z0), paving, true);
            }
        }

        // Street lamps on the kerb side of both sidewalks, arm over the road.
        for (int s = -1; s <= 1; s += 2)
        {
            float x = s * (roadHalf + 0.5f);
            for (float z = -halfL + LampSpacing * 0.5f; z < halfL; z += LampSpacing)
            {
                var pole = Slab(root, "LampPole", new Vector3(x, KerbHeight + LampHeight * 0.5f, z), new Vector3(0.2f, LampHeight, 0.2f), kit.decoDark);
                Street(pole.gameObject, kit, true);
                var arm = Slab(root, "LampArm", new Vector3(x - s * 0.9f, KerbHeight + LampHeight - 0.1f, z), new Vector3(1.8f, 0.12f, 0.12f), kit.decoDark);
                Street(arm.gameObject, kit, false);
                var head = Slab(root, "LampHead", new Vector3(x - s * 1.6f, KerbHeight + LampHeight - 0.25f, z), new Vector3(0.7f, 0.15f, 0.35f), lampHead);
                Street(head.gameObject, kit, false);
            }
        }

        // Street-level blade signs: neon slabs sticking out of the facades above head height.
        for (int i = 0; i < StreetBladeSigns; i++)
        {
            int s = rng.NextDouble() < 0.5 ? -1 : 1;
            float z = Mathf.Lerp(-halfL + 10f, halfL - 10f, (float)rng.NextDouble());
            if (!OnFacade(plots, s, z, 1f)) continue;
            float h = Mathf.Lerp(2.5f, 4.5f, (float)rng.NextDouble());
            var sign = Slab(root, "StreetBladeSign", new Vector3(s * (halfW - 0.8f), 4f + h * 0.5f, z), new Vector3(1.4f, h, 0.2f),
                            kit.neon[rng.Next(kit.neon.Length)]);
            Street(sign.gameObject, kit, false);
        }

        // Vents against the walls (solid: you walk round them) and pipes up the first floors (decoration).
        for (int s = -1; s <= 1; s += 2)
        {
            for (float z = -halfL + 5f; z < halfL - 5f; z += Mathf.Lerp(12f, 30f, (float)rng.NextDouble()))
            {
                if (!OnFacade(plots, s, z, 1.5f)) continue;
                if (rng.NextDouble() < 0.5)
                {
                    var vent = Slab(root, "Vent", new Vector3(s * (halfW - 0.6f), KerbHeight + 0.5f, z), new Vector3(1.2f, 1f, 2f), kit.decoDark);
                    Street(vent.gameObject, kit, true);
                }
                else
                {
                    float ph = Mathf.Lerp(6f, 10f, (float)rng.NextDouble());
                    var pipe = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                    pipe.name = "Pipe";
                    pipe.transform.SetParent(root, false);
                    pipe.transform.position = new Vector3(s * (halfW - 0.2f), KerbHeight + ph * 0.5f, z);
                    pipe.transform.localScale = new Vector3(0.25f, ph * 0.5f, 0.25f);
                    pipe.GetComponent<Renderer>().sharedMaterial = kit.decoDark;
                    Street(pipe, kit, false);
                }
            }
        }
    }

    // Police station (PoliceStation): a door in the east-row facade nearest z, with a POLICE sign, and a
    // landing pad on the road's loading strip in front of it. Transport cars land on the pad; busted
    // players are released at the door.
    public static void BuildPoliceStation(Transform parent, float halfW, float z, List<StreetPlot> plots, Kit kit)
    {
        // Snap z onto an east-row facade (not an alley).
        if (!OnFacade(plots, 1, z, 4f))
        {
            float best = float.MaxValue, bz = z;
            foreach (var p in plots)
                if (p.side == 1 && Mathf.Abs(p.center.z - z) < best) { best = Mathf.Abs(p.center.z - z); bz = p.center.z; }
            z = bz;
        }
        BuildPoliceStationAt(parent, new Vector3(halfW, 0f, z), Vector3.left, new Vector3(halfW - SidewalkWidth - 8f, 0f, z), kit);
    }

    // General form: `facade` = point on the building face at street level, `outward` = the face normal
    // (horizontal, axis-aligned), `padCenter` = landing pad centre on the road.
    public static void BuildPoliceStationAt(Transform parent, Vector3 facade, Vector3 outward, Vector3 padCenter, Kit kit)
    {
        var root = new GameObject("PoliceStation").transform;
        root.SetParent(parent, false);
        var station = root.gameObject.AddComponent<PoliceStation>();
        var blue = Neon("PoliceStationBlue", new Color(0.2f, 0.4f, 1f), 3f);
        var white = Neon("PoliceStationWhite", new Color(0.9f, 0.95f, 1f), 2f);
        Vector3 along = new Vector3(Mathf.Abs(outward.z), 0f, Mathf.Abs(outward.x));
        Vector3 Size(float n, float h, float a) => outward.x != 0f ? new Vector3(n, h, a) : new Vector3(a, h, n);
        Vector3 At(float outBy, float y) => new Vector3(facade.x, y, facade.z) + outward * outBy;

        // Door (a dark recess look) with the sign above it.
        var doorPanel = Slab(root, "StationDoorPanel", At(0.05f, KerbHeight + 1.5f), Size(0.1f, 3f, 3f), kit.decoDark);
        Street(doorPanel.gameObject, kit, false);
        var sign = Slab(root, "StationSign", At(0.15f, KerbHeight + 4.2f), Size(0.2f, 1f, 6f), blue);
        Street(sign.gameObject, kit, false);
        var signBand = Slab(root, "StationSignBand", At(0.2f, KerbHeight + 4.2f), Size(0.2f, 0.25f, 5f), white);
        Street(signBand.gameObject, kit, false);
        var door = new GameObject("Door").transform;
        door.SetParent(root, false);
        door.SetPositionAndRotation(At(2f, KerbHeight + 0.05f), Quaternion.LookRotation(outward));
        station.door = door;

        // Landing pad on the road, edged with blue lights.
        Vector3 pc = new Vector3(padCenter.x, 0f, padCenter.z);
        Flat(root, "StationPad", pc + Vector3.up * 0.012f, Size(10f, 0.01f, 14f), kit.padPaint, false);
        for (int i = 0; i < 4; i++)
        {
            float n = i < 2 ? (i == 0 ? -5f : 5f) : 0f, a = i >= 2 ? (i == 2 ? -7f : 7f) : 0f;
            Vector3 size = i < 2 ? Size(0.2f, 0.02f, 14f) : Size(10f, 0.02f, 0.2f);
            Flat(root, "StationPadEdge", pc + outward * n + along * a + Vector3.up * 0.02f, size, blue, false);
        }
        var pad = new GameObject("Pad").transform;
        pad.SetParent(root, false);
        pad.position = pc;
        station.pad = pad;
    }

    // A tower face (not an alley) at z on that side, with `margin` m to spare.
    static bool OnFacade(List<StreetPlot> plots, int side, float z, float margin)
    {
        foreach (var p in plots)
            if (p.side == side && Mathf.Abs(z - p.center.z) <= p.footprint.y * 0.5f - margin) return true;
        return false;
    }

    // Flat surface box; walkable ones keep their collider.
    static void Flat(Transform parent, string name, Vector3 center, Vector3 size, Material mat, bool collider)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.position = center;
        go.transform.localScale = size;
        go.GetComponent<Renderer>().sharedMaterial = mat;
        if (!collider) Object.DestroyImmediate(go.GetComponent<Collider>());
        go.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        MarkStatic(go, false);
    }

    // Street prop: Detail layer (culled with the rest of the decoration), static, collider only if solid.
    static void Street(GameObject go, Kit kit, bool collider)
    {
        if (!collider) Object.DestroyImmediate(go.GetComponent<Collider>());
        go.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        go.layer = kit.detailLayer;
        MarkStatic(go, false);
    }
}
