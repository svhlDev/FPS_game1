using System;
using System.Collections.Generic;
using UnityEngine;

// Every tunable of character generation in one asset (Tools > Characters > Create Body Rules asset puts
// one at Assets/Resources/BodyRules.asset; without it the defaults below are used).
// Lengths are fractions of standing height; girths are radii as fractions of height, for a realistic
// body before the house style (houseGirth thickens, extremityScale shrinks hands and feet).
[CreateAssetMenu(menuName = "Characters/Body Rules", fileName = "BodyRules")]
public class BodyRules : ScriptableObject
{
    [Header("House style (before sex and stats)")]
    [Tooltip("All girths (torso, limbs, neck) x this. Bulk, not fat.")]
    public float houseGirth = 1.35f;
    [Tooltip("Hands and feet (length and girth) x this, relative to a realistic size for the body.")]
    public float extremityScale = 0.6f;
    [Tooltip("Forearms narrow to this fraction of their mid girth at the wrist, shins at the ankle.")]
    public float extremityTaper = 0.7f;
    [Tooltip("STR scales hands and feet this fraction of what it does to limbs (Phase 2).")]
    public float extremityStrFactor = 0.5f;

    [Header("Sex templates")]
    public SexTemplate male = SexTemplate.Male();
    public SexTemplate female = SexTemplate.Female();

    [Header("Seed variation")]
    [Tooltip("Per-bone length noise (fraction, +-).")]
    public float lengthNoise = 0.03f;
    [Tooltip("Per-bone girth noise (fraction, +-).")]
    public float girthNoise = 0.04f;
    [Tooltip("Standing height noise (fraction, +-).")]
    public float heightNoise = 0.03f;

    [Header("STR: size")]
    public float strHeight = 0.08f;
    public float strLimbLength = 0.06f;
    public float strMuscleVolume = 0.6f;
    [Tooltip("STR 1 is slight, not skeletal.")]
    public float minMuscleVolume = 0.4f;
    [Tooltip("Core (bone) girth x (1 + this x s).")]
    public float strCoreGirth = 0.08f;
    [Tooltip("Muscle layer thickness = average thickness x volume^this (a shell grows slower than its volume).")]
    public float muscleThicknessExponent = 0.7f;

    [Header("INT: head")]
    public float intHeadScale = 0.10f;
    public float intElongation = 0.15f;
    public float eyeGlowStart = 16f, eyeGlowFull = 20f;
    [Tooltip("Glowing eyes (INT): emission colour and HDR intensity at full glow.")]
    [ColorUsage(false, true)] public Color eyeGlowColor = new Color(0.25f, 0.95f, 1f);
    public float eyeGlowIntensity = 6f;
    [Tooltip("Eyeball radius as a fraction of the carved socket.")]
    public float eyeballScale = 0.92f;

    [Header("DEX: fat and jitter")]
    [Tooltip("Fat thickness multiplier by DEX (x = DEX, y = x sex base fat), linear between keys.")]
    public Vector2[] dexFatCurve = { new Vector2(1, 3.0f), new Vector2(5, 1.6f), new Vector2(10, 1.0f), new Vector2(15, 0.45f), new Vector2(20, 0.15f) };
    public float jitterStart = 15f, jitterFull = 20f;

    [Header("Layers: how an average body's girth splits into core, muscle and fat")]
    [Tooltip("Muscle share of the (house) girth for an average body, per bone kind.")]
    public float torsoMuscleShare = 0.18f;
    public float limbMuscleShare = 0.32f;
    public float neckMuscleShare = 0.22f;
    [Tooltip("The core never goes below this fraction of the average girth.")]
    public float coreMinFraction = 0.35f;

    [Header("Seed individuality")]
    public float muscleNoise = 0.1f;
    public float fatDistributionNoise = 0.2f;
    public Vector2 breastSizeRange = new Vector2(0.7f, 1.35f);
    [Tooltip("Breast radius grows by this x the breast fat thickness.")]
    public float breastFatGain = 0.35f;
    [Tooltip("Downward offset as a fraction of breast size.")]
    public float breastDroop = 0.25f;
    [Tooltip("How far a breast stands out from the chest, as a fraction of its size: x for small breasts, y for large.")]
    public Vector2 breastProjection = new Vector2(0.3f, 0.95f);
    [Tooltip("Breast radius (m) that counts as small (x) and large (y) for the projection.")]
    public Vector2 breastProjectionSizes = new Vector2(0.04f, 0.12f);
    [Tooltip("How far a buttock (glute) stands out, as a fraction of its depth: x for small (lean), y for large (heavy).")]
    public Vector2 buttProjection = new Vector2(0.4f, 1.5f);
    [Tooltip("Buttock size (glute thickness + buttock fat, m) that counts as small (x) and large (y).")]
    public Vector2 buttProjectionSizes = new Vector2(0.03f, 0.11f);
    [Tooltip("Men's pec centres move this far (m) toward the midline each (0.01 = 2 cm closer together).")]
    public float malePecInset = 0.01f;
    public float faceNoise = 0.15f;
    [Tooltip("Spine pitch range (deg, + = slouch).")]
    public Vector2 postureRange = new Vector2(-3f, 6f);

    public float FatCurve(int dex)
    {
        var k = dexFatCurve;
        if (k == null || k.Length == 0) return 1f;
        if (dex <= k[0].x) return k[0].y;
        for (int i = 1; i < k.Length; i++)
            if (dex <= k[i].x) return Mathf.Lerp(k[i - 1].y, k[i].y, (dex - k[i - 1].x) / Mathf.Max(1e-4f, k[i].x - k[i - 1].x));
        return k[k.Length - 1].y;
    }

    [Header("SDF body, mesh, skinning, motion (BodyRulesTables.cs)")]
    public SdfRules sdf = new SdfRules();
    public MeshRules mesh = new MeshRules();
    public SkinRules skinning = new SkinRules();
    public MotionRules motion = new MotionRules();
    public List<MuscleBlobRule> muscleBlobs = MuscleBlobRule.Defaults();
    public List<FaceFeatureRule> faceFeatures = FaceFeatureRule.Defaults();

    [Header("Body plan rules")]
    public int minLegs = 2;
    public int maxArms = 5;
    [Tooltip("Extra shoulder girdles step down the chest by this fraction of the chest length.")]
    public float extraGirdleStep = 0.28f;
    [Tooltip("Generate-and-test: re-rolls before nudging a plan into shape.")]
    public int maxRerolls = 8;

    public SexTemplate Template(Sex s) => s == Sex.Male ? male : female;

    static BodyRules defaults;

    // The project's rules asset, or the built-in defaults.
    public static BodyRules Default
    {
        get
        {
            if (defaults != null) return defaults;
            defaults = Resources.Load<BodyRules>("BodyRules");
            if (defaults == null) { defaults = CreateInstance<BodyRules>(); defaults.name = "BodyRules (defaults)"; }
            return defaults;
        }
    }
}

// Base body for a sex: skeleton lengths (fractions of height), joint spacing and realistic girth
// profiles (radius at bone start, middle, end; fractions of height).
[Serializable]
public class SexTemplate
{
    public float height = 1.5f;

    [Header("Lengths (fraction of height)")]
    public float ankleHeight = 0.039f;
    public float shin = 0.246f;
    public float thigh = 0.245f;
    public float pelvis = 0.05f;      // hip centre down to the crotch
    public float lumbar = 0.12f;
    public float chest = 0.17f;
    public float neck = 0.05f;
    public float head = 0.13f;
    public float upperArm = 0.186f;
    public float forearm = 0.146f;
    public float hand = 0.108f;       // realistic; x extremityScale
    public float foot = 0.152f;       // realistic; x extremityScale

    [Header("Joint spacing (fraction of height)")]
    public float shoulderHalfWidth = 0.12f;
    public float shoulderDrop = 0.02f;   // shoulder joints below the top of the chest
    public float hipHalfWidth = 0.055f;
    public float neckForward = 0.02f;

    [Header("Girth profiles: radius at start / middle / end (fraction of height)")]
    public Vector3 pelvisGirth = new Vector3(0.088f, 0.092f, 0.08f);
    public Vector3 lumbarGirth = new Vector3(0.078f, 0.072f, 0.082f);
    public Vector3 chestGirth = new Vector3(0.085f, 0.098f, 0.085f);
    public Vector3 neckGirth = new Vector3(0.03f, 0.027f, 0.03f);
    public Vector3 headGirth = new Vector3(0.048f, 0.062f, 0.05f);
    public Vector3 upperArmGirth = new Vector3(0.03f, 0.027f, 0.022f);
    public Vector3 forearmGirth = new Vector3(0.022f, 0.021f, 0.016f);   // end replaced by the taper
    public Vector3 handGirth = new Vector3(0.016f, 0.018f, 0.013f);      // realistic; x extremityScale
    public Vector3 thighGirth = new Vector3(0.052f, 0.044f, 0.032f);
    public Vector3 shinGirth = new Vector3(0.031f, 0.029f, 0.02f);       // end replaced by the taper
    public Vector3 footGirth = new Vector3(0.02f, 0.022f, 0.016f);       // realistic; x extremityScale

    [Header("Muscle and fat")]
    [Tooltip("Muscle volume per group (Pectorals, Deltoids, Biceps, Triceps, Forearm, Trapezius, Lats, Abdominals, Glutes, Quadriceps, Hamstrings, Calves).")]
    public float[] muscle = { 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 0.9f, 1f, 1f, 1f };
    [Tooltip("Average fat layer thickness (fraction of height) at DEX 10.")]
    public float baseFat = 0.0095f;
    [Tooltip("Fat weight per region (Belly, Waist, Hips, Buttocks, Thighs, Breasts, UpperArms, Neck, Face).")]
    public float[] fat = { 1.6f, 1.3f, 0.8f, 0.8f, 0.7f, 0.6f, 0.7f, 0.8f, 0.6f };
    [Tooltip("Breast radius (fraction of height); 0 = none.")]
    public float breast = 0f;

    public static SexTemplate Male() => new SexTemplate { height = 1.54f };

    // Narrower shoulders, wider pelvis, slimmer arms and chest, fuller thighs; less arm and chest
    // muscle, more fat on breasts, hips, buttocks and thighs (males carry it on the belly and waist).
    public static SexTemplate Female() => new SexTemplate
    {
        height = 1.46f,
        shoulderHalfWidth = 0.106f,
        hipHalfWidth = 0.064f,
        pelvisGirth = new Vector3(0.096f, 0.102f, 0.088f),
        lumbarGirth = new Vector3(0.074f, 0.066f, 0.076f),
        chestGirth = new Vector3(0.078f, 0.088f, 0.076f),
        neckGirth = new Vector3(0.026f, 0.024f, 0.026f),
        upperArmGirth = new Vector3(0.026f, 0.023f, 0.019f),
        forearmGirth = new Vector3(0.019f, 0.018f, 0.014f),
        thighGirth = new Vector3(0.056f, 0.046f, 0.032f),
        hand = 0.102f, foot = 0.145f,
        muscle = new[] { 0.55f, 0.65f, 0.6f, 0.65f, 0.7f, 0.65f, 0.7f, 0.85f, 1.05f, 0.9f, 0.9f, 0.9f },
        baseFat = 0.0135f,
        fat = new[] { 1.0f, 0.9f, 1.5f, 1.6f, 1.5f, 1.6f, 1.0f, 0.6f, 0.7f },
        breast = 0.032f,
    };
}
