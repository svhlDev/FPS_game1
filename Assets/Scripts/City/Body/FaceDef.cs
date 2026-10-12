using System;
using System.Collections.Generic;
using UnityEngine;

// Head spec Part D: the face as data. Every feature is an SDF primitive placed in FACE SPACE: origin on
// the eye line (HeadCanon, Face Fix Part 1), x right, y up, z forward; positions and sizes are fractions
// of the head's half width (x), half height (y, crown to chin) and half depth (z), so a face scales with
// its head (x / z positions are also narrowed by the egg's width at their height, so a part at z = 1 sits
// on the surface at any height). A part's height hangs from a canon line (anchor: the brow line, the
// nose base, the mouth...), so the canon's per-seed proportions move every part with it. The parts are the face's MUSCLE
// layer: they smooth-union onto the egg, then the face fat (DEX) blends over them exactly like the body's
// muscle blobs (a fat face softens every feature, a lean one shows them sharply), then the skin. Subtract
// parts (the mouth line) cut the finished skin, so they read even under fat.
// Every part can be moved, rotated and scaled by data: per sex (female factors), by the seed (variation
// ranges), by the DNA's face scales, and per character (CharacterSheet.face overrides, for the face
// customizer later).
public enum FacePartShape { Capsule, Ellipsoid, Sphere, HalfEllipsoidUpper, HalfEllipsoidLower }
// Union: face muscle / bone (under the fat). Subtract: cuts the finished skin (reads under any fat).
// Carve: a hollow in the bone layer (the fat fills it). Marker: no shape (a position other code reads: the
// eyebrow hair).
public enum FacePartOp { Union, Subtract, Carve, Marker }
public enum FacePartScale { None, Brow, Cheekbones, Jaw, Nose, Chin }

[Serializable]
public class FacePart
{
    public string name;
    public FacePartShape shape;
    public FacePartOp op;
    [Tooltip("Face space (fractions of the egg radii). Mirrored parts: the right-hand (+x) copy.")]
    public Vector3 position;
    [Tooltip("Degrees, in face space. A capsule runs along its local y.")]
    public Vector3 rotationEuler;
    [Tooltip("Ellipsoid / half: radii (x rx, y ry, z rz). Capsule: (radius x rx, half length x ry, -). Sphere: radius x rx.")]
    public Vector3 size;
    [Tooltip("Seed variation (+-): position, degrees, size (fraction of the size).")]
    public Vector3 positionVar, rotationVar, sizeVar;
    [Tooltip("Female: size multiplier and position offset.")]
    public Vector3 femaleSize = Vector3.one, femaleOffset;
    public bool mirrored;
    [Tooltip("The canon line position.y is measured from (half-head-heights).")]
    public FaceLine anchor;
    [Tooltip("Smooth-union (or subtract) radius, m.")]
    public float blend = 0.008f;
    [Tooltip("Which DNA face scale multiplies the size.")]
    public FacePartScale scaleBy;
}

// One per-character edit of a part (the customizer's data): move (face space), rotate (degrees), scale
// (multiplier; 0 = unchanged). `part` matches a part's name, or one side of a mirrored part ("Brow.L").
[Serializable]
public struct FacePartOverride
{
    public string part;
    public Vector3 move, rotate, scale;
}

[Serializable]
public class FaceDef
{
    [Tooltip("Mirrored parts are generated symmetric; the seed then adds this fraction of the variation per side.")]
    public float asymmetry = 0.15f;
    [Tooltip("Eye sockets also cut through the face fat in front of the eye, this far past the fat (m).")]
    public float socketClearance = 0.004f;

    [Header("Canon (Face Fix Part 1): fractions of the head's height (H) and width (W)")]
    [Tooltip("Head width / height (crown to chin), male and female; depth (front to back) / height.")]
    public float widthToHeight = 0.7f, femaleWidthToHeight = 0.68f, depthToHeight = 0.86f;
    [Tooltip("Each face third (hairline-brow, brow-nose base, nose base-chin), x H.")]
    public float faceThird = 0.29f;
    [Tooltip("The mouth sits this fraction of the lower third below the nose base.")]
    public float mouthDrop = 1f / 3f;
    [Tooltip("The head is this many eye-widths wide.")]
    public float eyesAcross = 5f;
    [Tooltip("Eyeball diameter, visible opening width and height, x W.")]
    public float eyeballDiameter = 0.15f, openingWidth = 0.15f, openingHeight = 0.06f;
    public float femaleEyeScale = 1.04f;
    [Tooltip("The ear's centre sits this x the half depth behind the eye line's centre.")]
    public float earBehind = 0.12f;
    [Tooltip("Seed variation (+-, fraction) of each third, the eye spacing and size.")]
    public float canonVariation = 0.03f;
    [Tooltip("How far forward the eye centres sit (x the half depth).")]
    public float eyeDepth = 0.74f;

    [Header("Lids (Face Fix Part 3): degrees on the eyeball; thickness, crease depth in metres")]
    [Tooltip("The upper curve's rise and the lower one's drop at mid-eye (relaxed: the upper lid over the iris's top, the lower touching its bottom).")]
    public float lidUpper = 24f, lidLower = 27f;
    [Tooltip("The inner and outer corners' azimuth from forward.")]
    public float lidInner = 64f, lidOuter = 78f;
    [Tooltip("The outer corner's lift (almond tilt).")]
    public float lidTilt = 4f;
    public Vector2 lidThickness = new Vector2(0.0025f, 0.0016f);
    [Tooltip("The crease: degrees above the upper lid's edge; depth on a lean face (none on a fat one).")]
    public float lidCrease = 14f, lidCreaseDepth = 0.0012f;
    [Tooltip("The face sits this x the half depth in front of the head joint (the neck attaches behind the jaw).")]
    public float faceForward = 0.25f;

    [Header("Skull (Face Fix Part 2), face units: x half widths, y half heights (from the eye line), z half depths")]
    [Tooltip("The cranium (braincase): an egg, big end up and back.")]
    public Vector3 craniumCentre = new Vector3(0f, 0.17f, -0.08f), craniumRadii = new Vector3(1.08f, 0.83f, 0.98f);
    public float craniumBottom = 0.8f, craniumTop = 1.04f;
    [Tooltip("The lower jaw: the menton (chin bottom; y from the chin line), the jaw angles (gonion, the right one) and the chin's half width.")]
    public Vector3 menton = new Vector3(0f, 0f, 0.7f), gonion = new Vector3(0.74f, -0.66f, 0.05f), femaleGonion = new Vector3(0.66f, -0.64f, 0.05f);
    public float chinHalfWidth = 0.24f, femaleChinHalfWidth = 0.16f;
    [Tooltip("Gonion half width + this x STR (normalized), seed variation (+-) of it.")]
    public float jawStr = 0.05f, jawVariation = 0.04f;
    [Tooltip("Half head heights: the jaw's top above the menton, its edge rounding, the jaw angle's rounding (male / female), its blend onto the cranium.")]
    public float jawTop = 0.55f, jawRound = 0.06f, jawAngle = 0.05f, femaleJawAngle = 0.1f, jawBlend = 0.15f;
    [Tooltip("The face's fat blend radius cap (m): the fat rounds over the face parts with at most this (Face Fix Part 0).")]
    public float faceFatBlendMax = 0.02f;
    [Tooltip("The head's blend with the neck (m): small, so the under-jaw plane turns down into the neck.")]
    public float neckBlend = 0.012f;
    public List<FacePart> parts = Defaults();

    static FacePart P(string name, FaceLine anchor, FacePartShape shape, Vector3 pos, Vector3 rot, Vector3 size, float blend, FacePartScale scale = FacePartScale.None,
                      bool mirrored = false, Vector3 femaleSize = default, Vector3 femaleOffset = default, FacePartOp op = FacePartOp.Union,
                      Vector3 posVar = default, Vector3 rotVar = default, Vector3 sizeVar = default) => new FacePart
    {
        name = name, anchor = anchor, shape = shape, op = op, position = pos, rotationEuler = rot, size = size, blend = blend, scaleBy = scale, mirrored = mirrored,
        femaleSize = femaleSize == default ? Vector3.one : femaleSize, femaleOffset = femaleOffset,
        positionVar = posVar, rotationVar = rotVar, sizeVar = sizeVar,
    };

    // Defaults: male shapes; female factors give finer brows, a smaller chin and jaw, a narrower nose and
    // fuller lips. Capsules lying along x use rotation z = -90 (local y -> x).
    public static List<FacePart> Defaults() => new List<FacePart>
    {
        // ---- Bone (Face Fix Part 2) ----
        // Maxilla: the midface between the cheekbones, from under the eyes to the mouth (the nose and the
        // muzzle sit on it; the cranium alone recedes there).
        P("Maxilla", FaceLine.EyeLine, FacePartShape.Ellipsoid, new Vector3(0f, -0.33f, 0.45f), Vector3.zero, new Vector3(0.6f, 0.38f, 0.42f), 0.015f,
          sizeVar: new Vector3(0.05f, 0.05f, 0.05f)),
        // Cheekbone (zygomatic): its high point below and outside the outer eye corner, the arch running back
        // to just in front of the ear (its top edge is the socket's lower-outer rim).
        P("Cheekbone", FaceLine.EyeLine, FacePartShape.Capsule, new Vector3(0.74f, -0.185f, 0.355f), new Vector3(-93.1f, -17.7f, 0f), new Vector3(0.08f, 0.276f, 0f), 0.01f,
          FacePartScale.Cheekbones, mirrored: true, femaleSize: new Vector3(0.85f, 1f, 1f), posVar: new Vector3(0.02f, 0.02f, 0.02f), sizeVar: new Vector3(0.15f, 0.05f, 0f)),
        // Brow ridge: the bony bar above each eye (the top of the socket), overhanging it a little.
        P("BrowRidge", FaceLine.EyeLine, FacePartShape.Capsule, new Vector3(0.34f, 0.125f, 0.795f), new Vector3(0f, 18.3f, -88.6f), new Vector3(0.09f, 0.206f, 0f), 0.012f,
          FacePartScale.Brow, mirrored: true, femaleSize: new Vector3(0.65f, 0.95f, 1f), posVar: new Vector3(0.01f, 0.015f, 0.01f), sizeVar: new Vector3(0.15f, 0.05f, 0f)),
        // Temples: a slight hollow behind the outer eye corner, above the cheekbone.
        P("Temple", FaceLine.EyeLine, FacePartShape.Ellipsoid, new Vector3(0.9f, 0.12f, 0.42f), Vector3.zero, new Vector3(0.12f, 0.2f, 0.22f), 0.01f,
          mirrored: true, op: FacePartOp.Carve),
        // ---- Soft parts ----
        // Brows: the eyebrow hair's position (no shape: the brow ridge is the bone under it), level or rising
        // a little toward the outer end (neutral to a slight scowl).
        P("Brow", FaceLine.Brow, FacePartShape.Capsule, new Vector3(0.33f, 0.03f, 0.943f), new Vector3(0f, 12f, -83f), new Vector3(0.09f, 0.21f, 0f), 0.008f, FacePartScale.Brow,
          mirrored: true, femaleSize: new Vector3(0.7f, 0.92f, 1f), femaleOffset: new Vector3(0f, 0.02f, 0f), op: FacePartOp.Marker,
          posVar: new Vector3(0.02f, 0.02f, 0.01f), rotVar: new Vector3(0f, 4f, 6f), sizeVar: new Vector3(0.2f, 0.12f, 0f)),
        // Nose: the bridge from between the eyes down and forward to the tip; the underside back into the face; nostrils.
        P("NoseBridge", FaceLine.NoseBase, FacePartShape.Capsule, new Vector3(0f, 0.24f, 0.962f), new Vector3(-30f, 0f, 0f), new Vector3(0.09f, 0.25f, 0f), 0.007f, FacePartScale.Nose,
          femaleSize: new Vector3(0.82f, 0.94f, 1f), posVar: new Vector3(0f, 0.02f, 0.02f), rotVar: new Vector3(5f, 0f, 0f), sizeVar: new Vector3(0.15f, 0.1f, 0f)),
        P("NoseUnder", FaceLine.NoseBase, FacePartShape.Capsule, new Vector3(0f, 0.01f, 0.944f), new Vector3(75f, 0f, 0f), new Vector3(0.11f, 0.1f, 0f), 0.007f, FacePartScale.Nose,
          femaleSize: new Vector3(0.82f, 0.9f, 1f), sizeVar: new Vector3(0.12f, 0.1f, 0f)),
        P("Nostril", FaceLine.NoseBase, FacePartShape.Capsule, new Vector3(0.101f, 0f, 0.877f), new Vector3(70f, 0f, -35f), new Vector3(0.08f, 0.07f, 0f), 0.005f, FacePartScale.Nose,
          mirrored: true, femaleSize: new Vector3(0.8f, 0.85f, 1f), sizeVar: new Vector3(0.12f, 0.1f, 0f)),
        // Mouth area: an oval muzzle split into upper and lower halves meeting at the mouth line.
        P("MuzzleUpper", FaceLine.Mouth, FacePartShape.HalfEllipsoidUpper, new Vector3(0f, 0.013f, 0.649f), Vector3.zero, new Vector3(0.4f, 0.2f, 0.24f), 0.012f,
          sizeVar: new Vector3(0.06f, 0.06f, 0.06f)),
        P("MuzzleLower", FaceLine.Mouth, FacePartShape.HalfEllipsoidLower, new Vector3(0f, 0.013f, 0.649f), Vector3.zero, new Vector3(0.36f, 0.22f, 0.22f), 0.012f,
          sizeVar: new Vector3(0.06f, 0.06f, 0.06f)),
        // Lips: each from two capsules meeting at the centre with a small angle (crisp blends).
        P("LipUpper", FaceLine.Mouth, FacePartShape.Capsule, new Vector3(0.069f, 0.033f, 0.88f), new Vector3(0f, 14f, -84f), new Vector3(0.05f, 0.1f, 0f), 0.003f,
          mirrored: true, femaleSize: new Vector3(1.35f, 1.05f, 1f), sizeVar: new Vector3(0.15f, 0.08f, 0f)),
        P("LipLower", FaceLine.Mouth, FacePartShape.Capsule, new Vector3(0.06f, -0.032f, 0.853f), new Vector3(0f, 14f, -96f), new Vector3(0.058f, 0.09f, 0f), 0.003f,
          mirrored: true, femaleSize: new Vector3(1.35f, 1.05f, 1f), sizeVar: new Vector3(0.15f, 0.08f, 0f)),
        // The mouth line: a thin groove between the lips, cut after the fat.
        P("MouthLine", FaceLine.Mouth, FacePartShape.Capsule, new Vector3(0f, 0f, 0.906f), new Vector3(0f, 0f, -90f), new Vector3(0.03f, 0.18f, 0f), 0.002f,
          op: FacePartOp.Subtract),
        // Cheek fat pad: a soft oval below the eye on the front of the cheekbone, sloping down and in toward
        // the mouth corner (its lower inner edge is the nasolabial fold).
        P("Cheek", FaceLine.EyeLine, FacePartShape.Ellipsoid, new Vector3(0.42f, -0.3f, 0.74f), new Vector3(0f, 10f, -20f), new Vector3(0.17f, 0.22f, 0.13f), 0.012f,
          mirrored: true, sizeVar: new Vector3(0.12f, 0.1f, 0.12f), posVar: new Vector3(0.02f, 0.02f, 0f)),
        // Chin: on the front of the jaw's U.
        P("Chin", FaceLine.Chin, FacePartShape.Ellipsoid, new Vector3(0f, 0.29f, 0.717f), Vector3.zero, new Vector3(0.34f, 0.19f, 0.2f), 0.014f, FacePartScale.Chin,
          femaleSize: new Vector3(0.8f, 0.9f, 0.9f), femaleOffset: new Vector3(0f, 0.03f, -0.02f), posVar: new Vector3(0f, 0.02f, 0.02f), sizeVar: new Vector3(0.1f, 0.08f, 0.08f)),
    };
}
