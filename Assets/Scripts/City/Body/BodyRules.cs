using System;
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

    public static SexTemplate Male() => new SexTemplate { height = 1.54f };

    // Narrower shoulders, wider pelvis, slimmer arms and chest, fuller thighs.
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
    };
}
