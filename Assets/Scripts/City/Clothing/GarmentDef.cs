using System;
using System.Collections.Generic;
using UnityEngine;

// Garments as data (Clothing spec). A garment is a solid shell between an inner surface (the body, a small
// gap off the skin) and an outer surface (the body closed over its hollows by `ease`, then hung from its
// high points by `hangLength` / `slope`), kept where the garment covers (`covers`: by nearest bone) and
// cut at its ends (`cuts`). GarmentField turns one into a distance field; adding a garment type is a
// new GarmentDef, no new mesh code.
public enum Fabric { Cotton, Denim, Wool, Leather, Vinyl }

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

    [Header("Look")]
    public Fabric fabric = Fabric.Cotton;
    public Color color = new Color(0.3f, 0.32f, 0.36f);
    [Tooltip("Outfit layer: under (0), pants (1), outer (2), shoes (3).")]
    public int layer;

    static readonly BoneKind[] Torso = { BoneKind.Pelvis, BoneKind.Lumbar, BoneKind.Chest };

    // ---------- first set (Phase 7 extends these) ----------

    public static GarmentDef TShirt() => new GarmentDef
    {
        name = "T-shirt", layer = 0, ease = 0.015f, thickness = 0.004f, hangLength = 0.15f, slope = 0.25f,
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
        name = "Pants", layer = 1, ease = 0.015f, thickness = 0.005f, hangLength = 0.1f, slope = 0.35f, splitLegs = true,
        fabric = Fabric.Denim, color = new Color(0.16f, 0.2f, 0.3f),
        covers = new[] { BoneKind.Pelvis, BoneKind.Lumbar, BoneKind.Thigh, BoneKind.Shin },
        drapeOn = new[] { BoneKind.Pelvis },
        cuts =
        {
            GarmentCut.AtHeight(1.12f, false, BoneKind.Pelvis, BoneKind.Lumbar),   // waist
            GarmentCut.AtBone(BoneKind.Shin, 0.9f),                                  // ankle
        },
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
    public static GarmentDef Coat() => new GarmentDef
    {
        name = "Coat", layer = 2, ease = 0.06f, thickness = 0.015f, hangLength = 0.3f, slope = 0.15f,
        fabric = Fabric.Wool, color = new Color(0.25f, 0.2f, 0.16f),
        covers = new[] { BoneKind.Pelvis, BoneKind.Lumbar, BoneKind.Chest, BoneKind.Neck, BoneKind.UpperArm, BoneKind.Forearm, BoneKind.Thigh },
        drapeOn = new[] { BoneKind.Pelvis, BoneKind.Lumbar, BoneKind.Chest, BoneKind.Thigh },
        cuts =
        {
            GarmentCut.AtBone(BoneKind.Neck, 0.4f),
            GarmentCut.AtBone(BoneKind.Forearm, 0.9f),
            GarmentCut.AtBone(BoneKind.Thigh, 0.95f),
        },
    };
}

[Serializable]
public class Outfit
{
    public List<GarmentDef> garments = new List<GarmentDef>();
    public static Outfit Casual() => new Outfit { garments = { GarmentDef.TShirt(), GarmentDef.Pants() } };
}
