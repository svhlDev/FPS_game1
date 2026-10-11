using System;
using System.Collections.Generic;
using UnityEngine;

// Garments as data (Clothing spec). A garment is a solid shell between an inner surface (the body, a small
// gap off the skin) and an outer surface (the body closed over its hollows by `ease`, then hung from its
// high points by `hangLength` / `slope`), kept where the garment covers (`covers`: by nearest bone) and
// cut at its ends (`cuts`). GarmentField turns one into a distance field; adding a garment type is a
// new GarmentDef, no new mesh code.
public enum Fabric { Cotton, Denim, Wool, Leather, Vinyl }
// What a garment is for (picks its colour from the wardrobe's palettes).
public enum GarmentSlot { Top, Bottom, Feet, Outer, Head }

[Serializable]
public class GarmentCut
{
    public enum Kind { Bone, Height }
    public Kind kind;
    [Tooltip("Bone: perpendicular to every bone of this kind, at `at` x its length.")]
    public BoneKind bone;
    [Tooltip("Bone: fraction along the bone. Height: multiple of the hip joints' height.")]
    public float at;
    [Tooltip("Keep the part toward the bone's start (Bone) / above the plane (Height).")]
    public bool keepStart = true;
    [Tooltip("Height: the bone kinds the plane cuts (others ignore it).")]
    public BoneKind[] applyTo = new BoneKind[0];

    public static GarmentCut AtBone(BoneKind b, float at, bool keepStart = true) => new GarmentCut { kind = Kind.Bone, bone = b, at = at, keepStart = keepStart };
    public static GarmentCut AtHeight(float hipMultiple, bool keepAbove, params BoneKind[] applyTo) =>
        new GarmentCut { kind = Kind.Height, at = hipMultiple, keepStart = keepAbove, applyTo = applyTo };
}

[Serializable]
public class GarmentDef
{
    public string name = "Garment";
    [Tooltip("Bone kinds whose region (points nearest them) the garment covers.")]
    public BoneKind[] covers = new BoneKind[0];
    public List<GarmentCut> cuts = new List<GarmentCut>();

    [Header("Shape (m)")]
    [Tooltip("Hollows narrower than about this are bridged (morphological closing radius).")]
    public float ease = 0.015f;
    public float thickness = 0.004f;
    [Tooltip("Inner surface this far off the skin (never z-fights it).")]
    public float gap = 0.003f;
    [Tooltip("Hangs from the body's widest point up to this far above (0 = no drape).")]
    public float hangLength = 0.15f;
    [Tooltip("0 = hangs dead vertical, 1 = no drape (follows the body).")]
    public float slope = 0.25f;
    [Tooltip("Where the drape applies (by nearest bone); arms and legs get ease only.")]
    public BoneKind[] drapeOn = { BoneKind.Pelvis, BoneKind.Lumbar, BoneKind.Chest };
    [Tooltip("Pants: each leg closed on its own, so the tubes stay apart.")]
    public bool splitLegs;
    [Tooltip("Hem: the cut's rounding radius (m).")]
    public float hemBlend = 0.004f;

    [Header("Skirt (coats): below the hips one flared cone, the legs free inside; dangles on spring chains")]
    public SkirtDef skirt;

    [Header("Mesh")]
    [Tooltip("Meshing grid cell (m) for the garment's surface.")]
    public float meshCell = 0.015f;
    [Tooltip("Triangle budget (decimated to it).")]
    public int triangles = 2500;

    [Header("Look")]
    public GarmentSlot slot = GarmentSlot.Top;
    public Fabric fabric = Fabric.Cotton;
    public Color color = new Color(0.3f, 0.32f, 0.36f);
    [Tooltip("Outfit layer (informational; the outfit's list order is the layering: later rests on earlier).")]
    public int layer;

    public GarmentDef Clone() { var c = (GarmentDef)MemberwiseClone(); c.cuts = new List<GarmentCut>(cuts); return c; }
    public bool HasSkirt => skirt != null && skirt.enabled;


    static readonly BoneKind[] Torso = { BoneKind.Pelvis, BoneKind.Lumbar, BoneKind.Chest };

    // ---------- first set (Phase 7 extends these) ----------

    public static GarmentDef TShirt() => new GarmentDef
    {
        name = "T-shirt", layer = 1, slot = GarmentSlot.Top, triangles = 3000, ease = 0.015f, thickness = 0.004f, hangLength = 0.15f, slope = 0.25f,
        covers = new[] { BoneKind.Pelvis, BoneKind.Lumbar, BoneKind.Chest, BoneKind.Neck, BoneKind.UpperArm, BoneKind.Thigh },
        cuts =
        {
            GarmentCut.AtBone(BoneKind.Neck, 0.25f),                   // crew neck
            GarmentCut.AtBone(BoneKind.UpperArm, 0.5f),                // short sleeves
            GarmentCut.AtHeight(0.86f, true, BoneKind.Pelvis, BoneKind.Lumbar, BoneKind.Thigh), // hem below the hips
        },
    };

    public static GarmentDef LongSleeve()
    {
        var g = TShirt();
        g.name = "Long-sleeve shirt";
        g.covers = new[] { BoneKind.Pelvis, BoneKind.Lumbar, BoneKind.Chest, BoneKind.Neck, BoneKind.UpperArm, BoneKind.Forearm, BoneKind.Thigh };
        g.cuts[1] = GarmentCut.AtBone(BoneKind.Forearm, 0.92f);
        return g;
    }

    public static GarmentDef Sweater()
    {
        var g = LongSleeve();
        g.name = "Sweater"; g.ease = 0.03f; g.thickness = 0.008f; g.fabric = Fabric.Wool;
        return g;
    }

    public static GarmentDef Pants() => new GarmentDef
    {
        name = "Pants", layer = 0, slot = GarmentSlot.Bottom, triangles = 2500, ease = 0.015f, thickness = 0.005f, hangLength = 0.1f, slope = 0.35f, splitLegs = true,
        fabric = Fabric.Denim, color = new Color(0.16f, 0.2f, 0.3f),
        covers = new[] { BoneKind.Pelvis, BoneKind.Lumbar, BoneKind.Thigh, BoneKind.Shin },
        drapeOn = new[] { BoneKind.Pelvis },
        cuts =
        {
            GarmentCut.AtHeight(1.12f, false, BoneKind.Pelvis, BoneKind.Lumbar),   // waist
            GarmentCut.AtBone(BoneKind.Shin, 0.9f),                                  // ankle
        },
    };

    // Shoes: a shell over the small feet and the ankle (Phase 7 adds the sole slab and boots).
    public static GarmentDef Shoes() => new GarmentDef
    {
        name = "Shoes", layer = 0, slot = GarmentSlot.Feet, ease = 0.006f, thickness = 0.006f, gap = 0.002f, hangLength = 0f, meshCell = 0.008f, triangles = 700,
        fabric = Fabric.Leather, color = new Color(0.07f, 0.07f, 0.08f), drapeOn = new BoneKind[0],
        covers = new[] { BoneKind.Foot, BoneKind.Shin },
        cuts = { GarmentCut.AtBone(BoneKind.Shin, 0.9f, false) },   // just above the ankle
    };

    public static GarmentDef Shorts()
    {
        var g = Pants();
        g.name = "Shorts"; g.fabric = Fabric.Cotton;
        g.covers = new[] { BoneKind.Pelvis, BoneKind.Lumbar, BoneKind.Thigh };
        g.cuts[1] = GarmentCut.AtBone(BoneKind.Thigh, 0.75f);
        return g;
    }

    // Long coat: torso to the knees, wide ease, heavy drape (Phase 7 adds the flare, slits and collar).
    // Trench coat: worn over an outfit, to mid-shin, open down the front with lapels, a high collar, a
    // flared skirt below the hips with side slits and a back vent, the skirt on spring chains.
    public static GarmentDef Coat() => new GarmentDef
    {
        name = "Trench coat", layer = 2, slot = GarmentSlot.Outer, triangles = 4000, ease = 0.05f, thickness = 0.015f, hangLength = 0.3f, slope = 0.15f,
        fabric = Fabric.Wool, color = new Color(0.25f, 0.2f, 0.16f),
        covers = new[] { BoneKind.Pelvis, BoneKind.Lumbar, BoneKind.Chest, BoneKind.Neck, BoneKind.UpperArm, BoneKind.Forearm, BoneKind.Thigh, BoneKind.Shin },
        drapeOn = new[] { BoneKind.Pelvis, BoneKind.Lumbar, BoneKind.Chest },
        cuts =
        {
            GarmentCut.AtBone(BoneKind.Neck, 0.7f),       // high collar (the ease stands it off the neck)
            GarmentCut.AtBone(BoneKind.Forearm, 0.9f),
            GarmentCut.AtBone(BoneKind.Shin, 0.5f),       // mid-shin
        },
        skirt = new SkirtDef { enabled = true },
    };
}

[Serializable]
public class SkirtDef
{
    public bool enabled;
    [Tooltip("Where the skirt leaves the body: this x the hip joints' height.")]
    public float startAt = 1f;
    [Tooltip("The cone's radius grows by this x the hip width per metre down.")]
    public float flare = 0.25f;
    [Tooltip("Front opening (m): full width at the hem, narrowing to the chest (the lapels nearly meet up top).")]
    public float openingHem = 0.12f, openingChest = 0.06f;
    [Tooltip("Lapels: the opening's edges thicken outward by this (m), over lapelWidth.")]
    public float lapel = 0.012f, lapelWidth = 0.04f;
    [Tooltip("Slits from the hem up to mid-thigh: angles round the body (deg, 0 = front, + = right), half width (m).")]
    public float[] slitAngles = { -65f, 65f, 180f };
    public float slitHalfWidth = 0.012f;
    [Tooltip("Slit tops: this far from the knee to the hip (0.5 = mid-thigh).")]
    public float slitTop = 0.5f;

    [Header("Dangle (spring chains)")]
    public int panels = 6;
    public int bonesPerPanel = 3;
    [Tooltip("Pull back toward the animated rest (1/s): at the ring and at the hem.")]
    public float stiffnessRoot = 14f, stiffnessTip = 3f;
    [Tooltip("Velocity kept per frame at 60 fps.")]
    public float damping = 0.85f;
    public float gravityScale = 1f;
    [Tooltip("How strongly the air (wind minus the character's own motion) drags the panels.")]
    public float drag = 1.6f;
    [Tooltip("Furthest a segment swings from its rest direction (deg).")]
    public float maxAngle = 70f;
    [Tooltip("Keeps neighbouring panels together (0-1 per step).")]
    public float neighbour = 0.25f;
    [Tooltip("Simulated within this distance of the camera; beyond it the chains rest.")]
    public float simDistance = 40f;
}

[Serializable]
public class Outfit
{
    public List<GarmentDef> garments = new List<GarmentDef>();
    // List order is the layering (each garment rests on the ones before it): shoes, pants, then the top,
    // untucked over the pants.
    public static Outfit Casual(bool longSleeve = false) => new Outfit { garments = { GarmentDef.Shoes(), GarmentDef.Pants(), longSleeve ? GarmentDef.LongSleeve() : GarmentDef.TShirt() } };
    public static Outfit None() => new Outfit();
    public int Count => garments.Count;
}

// Who wears what: an outfit per role from the seed (shapes; colours come from BodyMaterials.Clothes).
public static class Wardrobe
{
    // From the sheet (hair is a wig now, not a garment: WigBuilder).
    public static Outfit For(CharacterFigure.Role role, CharacterSheet sheet) => For(role, sheet.seed);

    public static Outfit For(CharacterFigure.Role role, int seed)
    {
        switch (role)
        {
            case CharacterFigure.Role.Police: return Outfit.Casual(true);      // uniform: long sleeves, navy (colours)
            case CharacterFigure.Role.Player: return Outfit.Casual(false);
            default:
                var r = new PcgRandom(seed, "outfit");
                var o = Outfit.Casual(r.Value() < 0.35f);
                if (r.Value() < 0.3f) o.garments.Add(GarmentDef.Coat());   // the coat always goes over a casual outfit
                return o;
        }
    }
}
