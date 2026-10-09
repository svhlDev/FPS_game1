using System;
using System.Collections.Generic;
using UnityEngine;

// The BodyRules sections beyond the formulas: how the SDF body is shaped, meshed and skinned, the muscle
// blob and face feature layouts, and the DNA's effect on motion. Defaults reproduce the hand-tuned
// values; everything is editable on the BodyRules asset.

[Serializable]
public class SdfRules
{
    [Header("Blends (m)")]
    [Tooltip("Core: a bone with its parent (+ jointBlendGirth x the thinner core radius).")]
    public float jointBlend = 0.015f;
    public float jointBlendGirth = 0.6f;
    [Tooltip("Pelvis / lumbar / chest seams.")]
    public float torsoBlend = 0.04f;
    [Tooltip("Muscle blobs onto the core: small, so muscle shapes stay distinct.")]
    public float muscleBlend = 0.022f;
    [Tooltip("Fat blend radius = fatBlendMin + this x local fat thickness, at most fatBlendMax.")]
    public float fatBlendPerMetre = 1.6f;
    public float fatBlendMin = 0.018f;
    public float fatBlendMax = 0.14f;
    [Tooltip("Fat thickness blends across bones within about this distance (no ridges where they meet).")]
    public float fatBlendFalloff = 0.06f;
    public float breastBlend = 0.06f;
    public float featureBlend = 0.012f;
    public float eyeCarveBlend = 0.006f;
    [Tooltip("A muscle blob is skipped this far (+ the fat blend) from its bone's core.")]
    public float muscleReach = 0.12f;

    [Header("Shape")]
    [Tooltip("Torso cross-sections are flattened front-to-back: depth / width.")]
    public float pelvisDepth = 0.75f, lumbarDepth = 0.7f, chestDepth = 0.68f;
    [Tooltip("Blob width cap: edges rise off a curved body by ~ (width x r)^2 / 2r; keep that under this x the blob's thickness / 2.")]
    public float blobFit = 1.2f;
    [Tooltip("Blob centres stand this x their thickness off the core surface.")]
    public float blobLift = 0.15f;

    [Header("Bind pose (the mesh is generated in an A-pose)")]
    public float armSpread = 38f;
    public float legSpread = 4f;

    [Header("Eyes (fractions of the skull radii)")]
    public float eyeRadius = 0.17f;
    public Vector3 eyePosition = new Vector3(0.38f, 0.06f, 0.86f);
}

// Sausage hands: a rounded palm block, 4 finger capsules along its front (lower) edge and a thumb on
// its front-inner side; one bone per digit (a sausage bends at the knuckle only). The hand hangs with
// the palm facing the body's midline and the thumb forward.
[Serializable]
public class HandRules
{
    [Header("Palm (the hand's length and mid radius, after the house style)")]
    [Tooltip("Palm length x hand length (fingers make up the rest).")]
    public float palmLength = 0.53f;
    [Tooltip("Palm width (front to back) and thickness x the hand's mid outer radius.")]
    public float palmWidth = 2.8f, palmThickness = 1.3f;
    [Tooltip("Corner radius x the palm's thickness.")]
    public float palmCorner = 0.35f;

    [Header("Fingers")]
    [Tooltip("Finger length x palm length.")]
    public float fingerLength = 0.9f;
    [Tooltip("Finger radius x palm width (4 fingers at a quarter width each: 0.125 would touch).")]
    public float fingerRadius = 0.1f;
    [Tooltip("Length of the index, middle, ring and little finger (x fingerLength).")]
    public Vector4 fingerScale = new Vector4(0.95f, 1.05f, 0.97f, 0.8f);
    [Tooltip("Seed noise on each finger's length.")]
    public float fingerNoise = 0.05f;
    [Tooltip("Fat thickens the fingers by this x the hand's fat (capped so they stay apart).")]
    public float fingerFat = 0.5f;
    [Tooltip("Thumb length x the middle finger's; radius x a finger's.")]
    public float thumbLength = 0.72f, thumbRadius = 1.15f;
    [Tooltip("Thumb base along the palm (x palm length from the wrist).")]
    public float thumbBase = 0.3f;
    [Tooltip("Thumb direction (in, down, forward) in the hand's space.")]
    public Vector3 thumbDir = new Vector3(0.35f, 0.6f, 0.55f);
    [Tooltip("Knuckle pivot: this x the finger's radius toward the palm side.")]
    public float knucklePivot = 0.6f;
    [Tooltip("Fingers onto the palm (m): small, so the sausages stay readable.")]
    public float fingerBlend = 0.004f;

    [Header("Poses")]
    [Tooltip("Fist: fingers curl this many degrees toward the palm (one segment each: well past 90, or a fist reads as a flat L).")]
    public float fistCurl = 150f;
    [Tooltip("Fist: the thumb folds across the fingers, toward (in, down, back) in the hand's space.")]
    public Vector3 thumbFold = new Vector3(0.2f, 0.75f, 0.55f);
    [Tooltip("Relaxed hands (lowered, hanging): this much of the fist.")]
    public float relaxedCurl = 0.35f;
    [Tooltip("Curl blend speed (per second).")]
    public float curlSpeed = 12f;

    [Header("Mesh and skin")]
    [Tooltip("Each hand is meshed on its own finer grid (m), joined to the body just above the wrist.")]
    public float cell = 0.002f;
    [Tooltip("Triangle budget per hand.")]
    public int triangles = 1400;
    [Tooltip("The hand's mesh starts this far up the forearm (m); the body's ends a fifth of it above the wrist.")]
    public float cutOverlap = 0.015f;
    [Tooltip("Over the overlap each mesh shrinks this much (m) under the other, so neither shows through.")]
    public float cutShrink = 0.0015f;
    [Tooltip("Skin weights: a digit blends into the palm over this x its radius either side of the knuckle.")]
    public float knuckleBlend = 2f;
}

[Serializable]
public class MeshRules
{
    [Tooltip("Surface-nets grid cell (m).")]
    public float cell = 0.015f;
    [Tooltip("Near-tier triangle budget (decimated to it).")]
    public int targetTriangles = 10000;
    public int smoothIterations = 2;
    public float smoothStrength = 0.5f;
    [Tooltip("Normals: SDF gradient sampled +- this many cells (smooths over kinks in the field).")]
    public float normalStep = 1f;
    [Tooltip("Decimation keeps more detail where this is higher.")]
    public float headImportance = 6f, handImportance = 4f, footImportance = 2f, neckImportance = 2f;
}

[Serializable]
public class SkinRules
{
    [Tooltip("Weight = 1 / (distance + softness)^power.")]
    public float falloffPower = 4f;
    public float softness = 0.012f;
    [Tooltip("The vertex's own (nearest-core) bone.")]
    public float ownBoneBoost = 3f;
    [Tooltip("Neighbour bones count within this many joint radii of their shared joint.")]
    public float jointReach = 1.6f;
}

[Serializable]
public class MotionRules
{
    [Header("Size and fat (heavy: DEX 10 -> 0, DEX 1 -> 1)")]
    [Tooltip("Spine roll while walking (deg) at full heaviness; hips roll hipRoll.")]
    public float heavySway = 5f;
    public float heavyHipRoll = 3f;
    [Tooltip("Legs apart (deg each) at full heaviness.")]
    public float heavyStance = 5f;
    [Tooltip("STR: leg swing x (1 + this x s); cadence follows the stride so feet plant.")]
    public float strStrideSwing = 0.15f;

    [Header("DEX jitter (0 at jitterStart, 1 at jitterFull)")]
    public float jitterHead = 4f;
    public float jitterWrist = 10f;
    public float jitterSpeed = 9f, jitterSpeedGain = 6f;
    [Tooltip("Idle weight shifts run (1 + this x jitter) faster (not the player's).")]
    public float jitterIdle = 1.5f;
    public Vector2 glanceAngle = new Vector2(20f, 35f);
    public Vector2 glanceTime = new Vector2(0.3f, 0.6f);
    [Tooltip("Seconds between glances at jitter 0 (x) and 1 (y).")]
    public Vector2 glanceInterval = new Vector2(4f, 1.2f);
}

// A muscle blob: on bone `bone`, at t along it, `angle` degrees round it (0 = front, +90 = the body's
// right), `along` x bone length long, `width` x core radius wide, `thick` x the bone's muscle layer.
// mirror: a pair at +-angle (torso); outward: the angle's sign follows the limb's side.
[Serializable]
public class MuscleBlobRule
{
    public BoneKind bone;
    public MuscleGroup group;
    public float t, angle, along, width, thick = 1f;
    public bool mirror, outward;

    static MuscleBlobRule M(BoneKind b, MuscleGroup g, float t, float angle, float along, float width, float thick = 1f, bool mirror = false, bool outward = false) =>
        new MuscleBlobRule { bone = b, group = g, t = t, angle = angle, along = along, width = width, thick = thick, mirror = mirror, outward = outward };

    public static List<MuscleBlobRule> Defaults() => new List<MuscleBlobRule>
    {
        M(BoneKind.Chest, MuscleGroup.Pectorals, 0.72f, 36f, 0.55f, 0.52f, 0.45f, mirror: true),
        M(BoneKind.Chest, MuscleGroup.Lats, 0.45f, 125f, 0.75f, 0.6f, 0.45f, mirror: true),
        M(BoneKind.Chest, MuscleGroup.Trapezius, 0.92f, 155f, 0.35f, 0.7f, 0.5f, mirror: true),
        // Abdominals: a 2 x 3 grid of small blobs; the gaps between them are the definition.
        M(BoneKind.Lumbar, MuscleGroup.Abdominals, 0.22f, 14f, 0.26f, 0.3f, 0.5f, mirror: true),
        M(BoneKind.Lumbar, MuscleGroup.Abdominals, 0.5f, 14f, 0.26f, 0.3f, 0.5f, mirror: true),
        M(BoneKind.Lumbar, MuscleGroup.Abdominals, 0.78f, 14f, 0.26f, 0.3f, 0.5f, mirror: true),
        M(BoneKind.Pelvis, MuscleGroup.Glutes, 0.6f, 148f, 1.3f, 0.5f, 1.2f, mirror: true),
        M(BoneKind.Neck, MuscleGroup.Trapezius, 0.2f, 140f, 0.9f, 0.6f, 0.5f, mirror: true),
        M(BoneKind.UpperArm, MuscleGroup.Deltoids, 0.1f, 90f, 0.32f, 0.95f, 1.2f, outward: true),
        M(BoneKind.UpperArm, MuscleGroup.Biceps, 0.55f, 0f, 0.5f, 0.6f),
        M(BoneKind.UpperArm, MuscleGroup.Triceps, 0.45f, 180f, 0.55f, 0.65f),
        M(BoneKind.Forearm, MuscleGroup.Forearm, 0.28f, 30f, 0.5f, 0.8f, outward: true),
        M(BoneKind.Thigh, MuscleGroup.Quadriceps, 0.5f, 15f, 0.75f, 0.8f, outward: true),
        M(BoneKind.Thigh, MuscleGroup.Hamstrings, 0.5f, 180f, 0.65f, 0.7f, 0.8f),
        M(BoneKind.Shin, MuscleGroup.Calves, 0.3f, 180f, 0.45f, 0.75f, 1.1f),
    };
}

public enum FaceScale { Brow, Cheekbones, Jaw, Nose, Chin }

// A face feature: an ellipsoid at `at` (fractions of the skull radii from its centre), `size` (same
// units), scaled by the seed's value for `scale`; mirror: a pair at +-x.
[Serializable]
public class FaceFeatureRule
{
    public FaceScale scale;
    public Vector3 at, size;
    public bool mirror;

    static FaceFeatureRule F(FaceScale s, Vector3 at, Vector3 size, bool mirror = false) => new FaceFeatureRule { scale = s, at = at, size = size, mirror = mirror };

    public static List<FaceFeatureRule> Defaults() => new List<FaceFeatureRule>
    {
        F(FaceScale.Brow, new Vector3(0f, 0.18f, 0.82f), new Vector3(0.75f, 0.11f, 0.16f)),
        F(FaceScale.Cheekbones, new Vector3(0.55f, -0.08f, 0.68f), new Vector3(0.24f, 0.14f, 0.24f), mirror: true),
        F(FaceScale.Jaw, new Vector3(0f, -0.48f, 0.32f), new Vector3(0.72f, 0.36f, 0.68f)),
        F(FaceScale.Nose, new Vector3(0f, -0.12f, 0.98f), new Vector3(0.11f, 0.22f, 0.2f)),
        F(FaceScale.Chin, new Vector3(0f, -0.8f, 0.72f), new Vector3(0.28f, 0.15f, 0.2f)),
    };
}
