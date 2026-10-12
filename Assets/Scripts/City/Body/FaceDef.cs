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
public enum FacePartShape { Capsule, Ellipsoid, Sphere, HalfEllipsoidUpper, HalfEllipsoidLower, RoundCone, Plane }
// Union: a ridge added onto the finished skin (after the fat; Face Polish Part 2). Subtract: cuts the finished skin (reads under any fat).
// Carve: a hollow in the bone layer (the fat fills it). Marker: no shape (a position other code reads: the
// eyebrow hair).
// Anatomy Rework Part 1 (the head in layers): Press - a bone landmark pressed into the base (a flattened
// ellipsoid sunk so only `press` stands out, its long axis along the surface); Carve - a hollow pressed into
// the base the same way; Plane - flattens the base where its mask reaches; Piece - an overhanging bone /
// cartilage piece (nose, brow bar, lips); Muscle - pressed onto the bone layer (the muscle layer).
// Subtract: a small skin feature cut after the fat. Marker: no shape (the eyebrow hair's position).
// Union: a skin ridge (philtrum ridges, the lip border).
public enum FacePartOp { Union, Subtract, Carve, Marker, Press, Piece, Muscle, Plane }
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
    [Tooltip("Snapped onto the skin (grooves): placed where the skin is at its x / y, position.z = metres from it (inward negative).")]
    public bool snap;
    [Tooltip("Press / Carve / Muscle: the long axis (laid along the surface). Plane: the normal. RoundCone piece: the tip's position.")]
    public Vector3 axis;
    [Tooltip("Press / Carve / Muscle: how far it stands out of / sinks into the layer below (half widths). RoundCone: the tip's metres out.")]
    public float press;
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

    [Header("Ears (Face Fix Part 5): height from the canon (brow line to nose base); metres, degrees")]
    [Tooltip("Width / height, tilt back from vertical, flare (the back edge out from the head), thickness, rim tube radius.")]
    public float earWidth = 0.58f, earTilt = 15f, earFlare = 20f, earThickness = 0.003f, earRim = 0.0034f;
    [Tooltip("Seed variation (+-): size (fraction), flare (degrees).")]
    public float earSizeVariation = 0.08f, earFlareVariation = 8f;

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
    public float neckBlend = 0.015f;
    [Header("Head layers (Anatomy Rework Part 1)")]
    [Tooltip("The egg's lower half leans forward by this x its depth per half height (the face over the jaw).")]
    public float eggShear = 0.35f;
    [Tooltip("Pressed landmarks, pieces and muscles join their layer with this one small blend (m).")]
    public float headBlend = 0.003f;
    public List<FacePart> parts = Defaults();

    static FacePart P(string name, FaceLine anchor, FacePartShape shape, Vector3 pos, Vector3 rot, Vector3 size, float blend, FacePartScale scale = FacePartScale.None,
                      bool mirrored = false, Vector3 femaleSize = default, Vector3 femaleOffset = default, FacePartOp op = FacePartOp.Union,
                      Vector3 posVar = default, Vector3 rotVar = default, Vector3 sizeVar = default, bool snap = false, Vector3 axis = default, float press = 0f) => new FacePart
    {
        snap = snap, axis = axis, press = press,
        name = name, anchor = anchor, shape = shape, op = op, position = pos, rotationEuler = rot, size = size, blend = blend, scaleBy = scale, mirrored = mirrored,
        femaleSize = femaleSize == default ? Vector3.one : femaleSize, femaleOffset = femaleOffset,
        positionVar = posVar, rotationVar = rotVar, sizeVar = sizeVar,
    };

    // Defaults: male shapes; female factors give finer brows, a smaller chin and jaw, a narrower nose and
    // fuller lips. Capsules lying along x use rotation z = -90 (local y -> x).
    // Anatomy Rework Part 1: the head as clay, in layers. Face units: x half widths, y half heights (from
    // the part's canon line), z half depths. Press / Carve / Muscle: `position` is where on the face (snapped
    // onto the base along the line from the egg's centre), `axis` the long axis's direction (laid along the
    // surface), size (along, across, thickness) and `press` (how far it stands out / sinks in) in half widths.
    // Plane: `position` a point on it, `axis` its outward normal, size the mask's radii (half widths).
    // Piece: snapped like a press, then `position.z` metres out along the surface's normal; a RoundCone (the
    // nose) runs from `position` (root, size.x radius, size.z metres out) to `axis` (tip, size.y radius,
    // `press` metres out). Subtract with snap: placed on the finished skin (Face Fix Part 4).
    public static List<FacePart> Defaults() => new List<FacePart>
    {
        // ---- Bone: planes ----
        P("ForeheadPlane", FaceLine.EyeLine, FacePartShape.Plane, new Vector3(0f, 0.5f, 0.86f), Vector3.zero, new Vector3(0.75f, 0.25f, 0.5f), 0.015f,
          op: FacePartOp.Plane, axis: new Vector3(0f, 0.3f, 1f)),
        P("FaceSidePlane", FaceLine.EyeLine, FacePartShape.Plane, new Vector3(0.76f, -0.35f, 0.3f), Vector3.zero, new Vector3(0.4f, 0.25f, 0.6f), 0.012f,
          mirrored: true, op: FacePartOp.Plane, axis: new Vector3(1f, 0f, 0.3f)),
        P("UnderJawPlane", FaceLine.Chin, FacePartShape.Plane, new Vector3(0f, 0.2f, 0.3f), Vector3.zero, new Vector3(0.9f, 0.3f, 0.75f), 0.01f,
          op: FacePartOp.Plane, axis: new Vector3(0f, -1f, 0.55f)),
        // ---- Bone: pressed landmarks ----
        P("NasalRoot", FaceLine.EyeLine, FacePartShape.Ellipsoid, new Vector3(0f, 0.03f, 1f), Vector3.zero, new Vector3(0.12f, 0.1f, 0.1f), 0.004f,
          op: FacePartOp.Press, axis: new Vector3(0f, 1f, 0f), press: 0.05f),
        P("Cheekbone", FaceLine.EyeLine, FacePartShape.Ellipsoid, new Vector3(0.72f, -0.17f, 0.5f), Vector3.zero, new Vector3(0.32f, 0.1f, 0.12f), 0.004f,
          FacePartScale.Cheekbones, mirrored: true, op: FacePartOp.Press, axis: new Vector3(0.25f, -0.05f, -1f), press: 0.07f, posVar: new Vector3(0.02f, 0.02f, 0f)),
        P("CheekboneFront", FaceLine.EyeLine, FacePartShape.Ellipsoid, new Vector3(0.5f, -0.22f, 0.85f), Vector3.zero, new Vector3(0.17f, 0.13f, 0.1f), 0.004f,
          FacePartScale.Cheekbones, mirrored: true, op: FacePartOp.Press, axis: new Vector3(1f, -0.4f, 0f), press: 0.05f),
        P("JawAngle", FaceLine.EyeLine, FacePartShape.Ellipsoid, new Vector3(0.72f, -0.66f, 0f), Vector3.zero, new Vector3(0.18f, 0.13f, 0.12f), 0.004f,
          FacePartScale.Jaw, mirrored: true, op: FacePartOp.Press, axis: new Vector3(0f, 1f, 0.3f), press: 0.07f, femaleSize: new Vector3(0.85f, 0.85f, 1f)),
        P("JawBody", FaceLine.EyeLine, FacePartShape.Ellipsoid, new Vector3(0.5f, -0.85f, 0.4f), Vector3.zero, new Vector3(0.38f, 0.1f, 0.12f), 0.004f,
          FacePartScale.Jaw, mirrored: true, op: FacePartOp.Press, axis: new Vector3(-0.35f, -0.25f, 0.9f), press: 0.05f),
        P("Chin", FaceLine.Chin, FacePartShape.Ellipsoid, new Vector3(0f, 0.1f, 0.7f), Vector3.zero, new Vector3(0.24f, 0.16f, 0.14f), 0.004f, FacePartScale.Chin,
          op: FacePartOp.Press, axis: new Vector3(1f, 0f, 0f), press: 0.09f, femaleSize: new Vector3(0.8f, 0.85f, 0.85f), sizeVar: new Vector3(0.1f, 0.08f, 0f)),
        // (Low on the muzzle: the upper lip slopes back from the lips to the nose's base.)
        P("UpperJawArc", FaceLine.Mouth, FacePartShape.Ellipsoid, new Vector3(0f, 0.01f, 0.9f), Vector3.zero, new Vector3(0.42f, 0.15f, 0.16f), 0.004f,
          op: FacePartOp.Press, axis: new Vector3(1f, 0f, 0f), press: 0.075f),
        // ---- Bone: carves ----
        P("Temple", FaceLine.EyeLine, FacePartShape.Ellipsoid, new Vector3(0.88f, 0.15f, 0.4f), Vector3.zero, new Vector3(0.22f, 0.18f, 0.12f), 0.006f,
          mirrored: true, op: FacePartOp.Carve, axis: new Vector3(0f, 1f, 0f), press: 0.05f),
        P("UnderCheekbone", FaceLine.EyeLine, FacePartShape.Ellipsoid, new Vector3(0.7f, -0.38f, 0.5f), Vector3.zero, new Vector3(0.22f, 0.13f, 0.12f), 0.006f,
          mirrored: true, op: FacePartOp.Carve, axis: new Vector3(0.3f, 0f, -1f), press: 0.05f),
        P("SideFace", FaceLine.EyeLine, FacePartShape.Ellipsoid, new Vector3(0.8f, -0.47f, 0.2f), Vector3.zero, new Vector3(0.17f, 0.14f, 0.1f), 0.006f,
          mirrored: true, op: FacePartOp.Carve, axis: new Vector3(0f, 1f, 0f), press: 0.035f),
        // ---- Bone / cartilage: overhanging pieces ----
        // Brow ridge: a bar across above the eyes (two halves meeting at the nasal root), slightly overhanging.
        P("BrowRidge", FaceLine.EyeLine, FacePartShape.Capsule, new Vector3(0.34f, 0.125f, -0.002f), new Vector3(0f, 18.3f, -88.6f), new Vector3(0.09f, 0.206f, 0f), 0.004f,
          FacePartScale.Brow, mirrored: true, op: FacePartOp.Piece, femaleSize: new Vector3(0.65f, 0.95f, 1f), sizeVar: new Vector3(0.15f, 0.05f, 0f)),
        // Nose: one tapered piece from the nasal root to the tip (its length scales the whole piece).
        P("Nose", FaceLine.NoseBase, FacePartShape.RoundCone, new Vector3(0f, 0.42f, 0f), Vector3.zero, new Vector3(0.065f, 0.11f, -0.004f), 0.004f, FacePartScale.Nose,
          op: FacePartOp.Piece, axis: new Vector3(0f, 0.06f, 0f), press: 0.017f, femaleSize: new Vector3(0.85f, 0.85f, 0.85f)),
        P("Ala", FaceLine.NoseBase, FacePartShape.Ellipsoid, new Vector3(0.15f, 0.05f, 0.005f), Vector3.zero, new Vector3(0.09f, 0.07f, 0.09f), 0.004f, FacePartScale.Nose,
          mirrored: true, op: FacePartOp.Piece, femaleSize: new Vector3(0.85f, 0.9f, 0.9f), sizeVar: new Vector3(0.12f, 0.1f, 0.1f)),
        P("NoseUnder", FaceLine.NoseBase, FacePartShape.Capsule, new Vector3(0f, 0.01f, 0.008f), new Vector3(76f, 0f, 0f), new Vector3(0.05f, 0.062f, 0f), 0.004f, FacePartScale.Nose,
          op: FacePartOp.Piece, femaleSize: new Vector3(0.85f, 0.9f, 1f)),
        // Lips: an M-shaped upper lip (two capsules wrapping back round the muzzle), a fuller lower lip behind it.
        P("LipUpper", FaceLine.Mouth, FacePartShape.Capsule, new Vector3(0.1875f, 0.02f, 0.001f), new Vector3(0f, 33.6f, -95.7f), new Vector3(0.055f, 0.145f, 0f), 0.003f,
          mirrored: true, op: FacePartOp.Piece, femaleSize: new Vector3(1.3f, 1.02f, 1f), sizeVar: new Vector3(0.15f, 0.06f, 0f)),
        P("LipLower", FaceLine.Mouth, FacePartShape.Ellipsoid, new Vector3(0f, -0.045f, -0.002f), Vector3.zero, new Vector3(0.33f, 0.055f, 0.1f), 0.003f,
          op: FacePartOp.Piece, femaleSize: new Vector3(1.05f, 1.3f, 1.15f), sizeVar: new Vector3(0.08f, 0.15f, 0.1f)),
        // ---- Muscle ----
        P("Masseter", FaceLine.EyeLine, FacePartShape.Ellipsoid, new Vector3(0.8f, -0.5f, 0.15f), Vector3.zero, new Vector3(0.26f, 0.15f, 0.1f), 0.004f,
          mirrored: true, op: FacePartOp.Muscle, axis: new Vector3(0f, 1f, 0.25f), press: 0.05f),
        P("Temporalis", FaceLine.EyeLine, FacePartShape.Ellipsoid, new Vector3(0.88f, 0.22f, 0.3f), Vector3.zero, new Vector3(0.26f, 0.24f, 0.1f), 0.004f,
          mirrored: true, op: FacePartOp.Muscle, axis: new Vector3(0f, 1f, -0.2f), press: 0.025f),
        P("OrbicularisOris", FaceLine.Mouth, FacePartShape.Ellipsoid, new Vector3(0f, 0f, 0.9f), Vector3.zero, new Vector3(0.42f, 0.24f, 0.18f), 0.004f,
          op: FacePartOp.Muscle, axis: new Vector3(1f, 0f, 0f), press: 0.08f),
        P("Zygomaticus", FaceLine.EyeLine, FacePartShape.Ellipsoid, new Vector3(0.5f, -0.42f, 0.8f), Vector3.zero, new Vector3(0.3f, 0.06f, 0.06f), 0.004f,
          mirrored: true, op: FacePartOp.Muscle, axis: new Vector3(-0.35f, -1f, 0f), press: 0.03f),
        // ---- Markers (no shape) ----
        // Brows: the eyebrow hair's position; Cheek: the cheek pad (the fat map makes it; the bake's redness zone).
        P("Brow", FaceLine.Brow, FacePartShape.Capsule, new Vector3(0.33f, 0.03f, 0.943f), new Vector3(0f, 12f, -83f), new Vector3(0.09f, 0.21f, 0f), 0.008f, FacePartScale.Brow,
          mirrored: true, femaleSize: new Vector3(0.7f, 0.92f, 1f), femaleOffset: new Vector3(0f, 0.02f, 0f), op: FacePartOp.Marker,
          posVar: new Vector3(0.02f, 0.02f, 0.01f), rotVar: new Vector3(0f, 4f, 6f), sizeVar: new Vector3(0.2f, 0.12f, 0f)),
        P("Cheek", FaceLine.EyeLine, FacePartShape.Ellipsoid, new Vector3(0.42f, -0.3f, 0.74f), new Vector3(0f, 10f, -20f), new Vector3(0.17f, 0.22f, 0.13f), 0.012f,
          mirrored: true, op: FacePartOp.Marker),
        // ---- Skin features (after the fat) ----
        // Nostrils: placed under the nose's tip (BodySDF), cut into its underside.
        P("Nostril", FaceLine.NoseBase, FacePartShape.Ellipsoid, Vector3.zero, new Vector3(0f, 20f, 0f), new Vector3(0.035f, 0.02f, 0.045f), 0.002f, FacePartScale.Nose,
          mirrored: true, op: FacePartOp.Subtract, femaleSize: new Vector3(0.85f, 0.9f, 0.9f)),
        // Grooves on the skin: the philtrum, the mouth line (deepening into the tucked-in corners), the folds
        // from the nose to the mouth corners, the groove under the lower lip.
        P("Philtrum", FaceLine.Mouth, FacePartShape.Capsule, new Vector3(0f, 0.11f, -0.0008f), Vector3.zero, new Vector3(0.025f, 0.06f, 0f), 0.002f,
          op: FacePartOp.Subtract, snap: true),
        P("MouthLine", FaceLine.Mouth, FacePartShape.Capsule, new Vector3(0f, 0f, -0.0005f), new Vector3(0f, 0f, -90f), new Vector3(0.02f, 0.12f, 0f), 0.0015f,
          op: FacePartOp.Subtract, snap: true),
        P("MouthLineSide", FaceLine.Mouth, FacePartShape.Capsule, new Vector3(0.24f, -0.003f, -0.0006f), new Vector3(0f, 33.6f, -91f), new Vector3(0.022f, 0.09f, 0f), 0.0015f,
          mirrored: true, op: FacePartOp.Subtract, snap: true),
        // (press on a skin feature: a fold - the fat along it is thinned by up to that fraction, so the fat
        // either side bulges against it and the fold deepens with the fat.)
        P("MouthCorner", FaceLine.Mouth, FacePartShape.Sphere, new Vector3(0.37f, -0.004f, -0.0022f), Vector3.zero, new Vector3(0.045f, 0f, 0f), 0.002f,
          mirrored: true, op: FacePartOp.Subtract, snap: true, press: 0.4f),
        P("Nasolabial", FaceLine.Mouth, FacePartShape.Capsule, new Vector3(0.345f, 0.04f, -0.0006f), new Vector3(0f, 0f, -153.3f), new Vector3(0.03f, 0.163f, 0f), 0.002f,
          mirrored: true, op: FacePartOp.Subtract, snap: true, press: 0.85f),
        // Face Polish Part 2: between the nose and the mouth. The crease where the nose's underside meets the
        // upper lip, the alar creases round the back of each wing, the philtrum's two ridges, a thin ridge on
        // the upper lip's border (the vermilion border), a shadowed crease under the lower lip.
        P("NoseLipCrease", FaceLine.NoseBase, FacePartShape.Capsule, new Vector3(0f, -0.04f, -0.0008f), new Vector3(0f, 0f, -90f), new Vector3(0.02f, 0.07f, 0f), 0.0015f,
          FacePartScale.Nose, op: FacePartOp.Subtract, snap: true),
        P("AlarCrease", FaceLine.NoseBase, FacePartShape.Capsule, new Vector3(0.25f, 0.07f, -0.001f), new Vector3(0f, 0f, 25f), new Vector3(0.022f, 0.075f, 0f), 0.0015f,
          FacePartScale.Nose, mirrored: true, op: FacePartOp.Subtract, snap: true),
        P("PhiltrumRidge", FaceLine.Mouth, FacePartShape.Capsule, new Vector3(0.055f, 0.1f, -0.0004f), new Vector3(0f, 0f, 8f), new Vector3(0.018f, 0.06f, 0f), 0.002f,
          mirrored: true, op: FacePartOp.Union, snap: true),
        P("VermilionBorder", FaceLine.Mouth, FacePartShape.Capsule, new Vector3(0.15f, 0.062f, -0.0003f), new Vector3(0f, 0f, -97f), new Vector3(0.012f, 0.13f, 0f), 0.0015f,
          mirrored: true, op: FacePartOp.Union, snap: true),
        P("LipLowerCrease", FaceLine.Mouth, FacePartShape.Capsule, new Vector3(0f, -0.1f, -0.0009f), new Vector3(0f, 0f, -90f), new Vector3(0.016f, 0.17f, 0f), 0.002f,
          op: FacePartOp.Subtract, snap: true),
        P("ChinGroove", FaceLine.Mouth, FacePartShape.Capsule, new Vector3(0f, -0.14f, -0.0012f), new Vector3(0f, 0f, -90f), new Vector3(0.05f, 0.12f, 0f), 0.003f,
          op: FacePartOp.Subtract, snap: true, press: 0.6f),
        // The double chin's crease: under the jaw, only with a lot of fat (it starts as a hairline).
        P("DoubleChinCrease", FaceLine.Chin, FacePartShape.Capsule, new Vector3(0f, -0.12f, -0.004f), new Vector3(0f, 0f, -90f), new Vector3(0.01f, 0.3f, 0f), 0.004f,
          op: FacePartOp.Subtract, snap: true, press: 0.35f),
    };
}
