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
public enum FacePartOp { Union, Subtract }
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
    public float eyeballDiameter = 0.135f, openingWidth = 0.15f, openingHeight = 0.045f;
    public float femaleEyeScale = 1.04f;
    [Tooltip("The ear's centre sits this x the half depth behind the eye line's centre.")]
    public float earBehind = 0.12f;
    [Tooltip("Seed variation (+-, fraction) of each third, the eye spacing and size.")]
    public float canonVariation = 0.03f;
    [Tooltip("How far forward the eye centres sit (x the half depth, narrowed like the parts).")]
    public float eyeDepth = 0.86f;
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
        // Jaw mass: the lower face's volume behind the mouth (the egg alone is narrow there).
        P("Jaw", FaceLine.Mouth, FacePartShape.Ellipsoid, new Vector3(0f, -0.02f, 0.3f), Vector3.zero, new Vector3(0.66f, 0.34f, 0.62f), 0.02f, FacePartScale.Jaw,
          femaleSize: new Vector3(0.88f, 0.9f, 0.9f), sizeVar: new Vector3(0.06f, 0.06f, 0.06f)),
        // Brows: above the eyes, level or rising a little toward the outer end (neutral to a slight scowl).
        P("Brow", FaceLine.Brow, FacePartShape.Capsule, new Vector3(0.34f, 0.03f, 0.97f), new Vector3(0f, 12f, -83f), new Vector3(0.09f, 0.21f, 0f), 0.008f, FacePartScale.Brow,
          mirrored: true, femaleSize: new Vector3(0.7f, 0.92f, 1f), femaleOffset: new Vector3(0f, 0.02f, 0f),
          posVar: new Vector3(0.02f, 0.02f, 0.01f), rotVar: new Vector3(0f, 4f, 6f), sizeVar: new Vector3(0.2f, 0.12f, 0f)),
        // Nose: the bridge from between the brows down and forward to the tip; the underside back into the face; nostrils.
        P("NoseBridge", FaceLine.NoseBase, FacePartShape.Capsule, new Vector3(0f, 0.24f, 1.08f), new Vector3(-30f, 0f, 0f), new Vector3(0.09f, 0.25f, 0f), 0.007f, FacePartScale.Nose,
          femaleSize: new Vector3(0.82f, 0.94f, 1f), posVar: new Vector3(0f, 0.02f, 0.02f), rotVar: new Vector3(5f, 0f, 0f), sizeVar: new Vector3(0.15f, 0.1f, 0f)),
        P("NoseUnder", FaceLine.NoseBase, FacePartShape.Capsule, new Vector3(0f, 0.01f, 1.12f), new Vector3(75f, 0f, 0f), new Vector3(0.11f, 0.1f, 0f), 0.007f, FacePartScale.Nose,
          femaleSize: new Vector3(0.82f, 0.9f, 1f), sizeVar: new Vector3(0.12f, 0.1f, 0f)),
        P("Nostril", FaceLine.NoseBase, FacePartShape.Capsule, new Vector3(0.12f, 0f, 1.04f), new Vector3(70f, 0f, -35f), new Vector3(0.08f, 0.07f, 0f), 0.005f, FacePartScale.Nose,
          mirrored: true, femaleSize: new Vector3(0.8f, 0.85f, 1f), sizeVar: new Vector3(0.12f, 0.1f, 0f)),
        // Mouth area: an oval muzzle split into upper and lower halves meeting at the mouth line.
        P("MuzzleUpper", FaceLine.Mouth, FacePartShape.HalfEllipsoidUpper, new Vector3(0f, 0.013f, 0.8f), Vector3.zero, new Vector3(0.4f, 0.2f, 0.24f), 0.012f,
          sizeVar: new Vector3(0.06f, 0.06f, 0.06f)),
        P("MuzzleLower", FaceLine.Mouth, FacePartShape.HalfEllipsoidLower, new Vector3(0f, 0.013f, 0.8f), Vector3.zero, new Vector3(0.36f, 0.22f, 0.22f), 0.012f,
          sizeVar: new Vector3(0.06f, 0.06f, 0.06f)),
        // Lips: each from two capsules meeting at the centre with a small angle (crisp blends).
        P("LipUpper", FaceLine.Mouth, FacePartShape.Capsule, new Vector3(0.085f, 0.033f, 1.08f), new Vector3(0f, 14f, -84f), new Vector3(0.05f, 0.1f, 0f), 0.003f,
          mirrored: true, femaleSize: new Vector3(1.35f, 1.05f, 1f), sizeVar: new Vector3(0.15f, 0.08f, 0f)),
        P("LipLower", FaceLine.Mouth, FacePartShape.Capsule, new Vector3(0.075f, -0.032f, 1.06f), new Vector3(0f, 14f, -96f), new Vector3(0.058f, 0.09f, 0f), 0.003f,
          mirrored: true, femaleSize: new Vector3(1.35f, 1.05f, 1f), sizeVar: new Vector3(0.15f, 0.08f, 0f)),
        // The mouth line: a thin groove between the lips, cut after the fat.
        P("MouthLine", FaceLine.Mouth, FacePartShape.Capsule, new Vector3(0f, 0f, 1.12f), new Vector3(0f, 0f, -90f), new Vector3(0.03f, 0.18f, 0f), 0.002f,
          op: FacePartOp.Subtract),
        // Cheeks: elongated ovals from below the outer corner of the eye diagonally down to the nostrils.
        P("Cheek", FaceLine.NoseBase, FacePartShape.Ellipsoid, new Vector3(0.32f, 0.25f, 0.86f), new Vector3(0f, 15f, -59f), new Vector3(0.13f, 0.24f, 0.12f), 0.012f, FacePartScale.Cheekbones,
          mirrored: true, sizeVar: new Vector3(0.12f, 0.1f, 0.12f), posVar: new Vector3(0.02f, 0.02f, 0f)),
        // Chin: forward and down from the lower mouth half.
        P("Chin", FaceLine.Chin, FacePartShape.Ellipsoid, new Vector3(0f, 0.29f, 0.9f), Vector3.zero, new Vector3(0.34f, 0.19f, 0.2f), 0.014f, FacePartScale.Chin,
          femaleSize: new Vector3(0.8f, 0.9f, 0.9f), femaleOffset: new Vector3(0f, 0.03f, -0.02f), posVar: new Vector3(0f, 0.02f, 0.02f), sizeVar: new Vector3(0.1f, 0.08f, 0.08f)),
    };
}
