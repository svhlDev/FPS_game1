using System;
using UnityEngine;

// Phase 2: BodyDNA = f(sex, STR, INT, DEX, seed). Everything the later phases need to build a body:
// overall size, limb length and girth factors, muscle volumes per group, fat thickness and how it is
// distributed, head size and shape, breasts, face proportions, posture, eye glow, jitter.
// Sex sets the base (SexTemplate), the sheet shifts it, the seed adds individual noise. Every number
// comes from BodyRules; every random draw from its own PcgRandom stream.
//   STR: size. height x (1 + strHeight s), limbs x (1 + strLimbLength s), muscle volume
//        x (1 + strMuscleVolume s) (never below minMuscleVolume), a little core girth.
//   INT: head. scale x (1 + intHeadScale s), elongation intElongation max(s, 0) (up and back), eyes
//        glow from eyeGlowStart to eyeGlowFull.
//   DEX: fat. thickness = sex base x the DEX curve (3.0 at 1 ... 0.15 at 20) x region weights; thin
//        fat shows the muscle (ripped), thick fat smooths it (soft). Jitter from jitterStart.
public enum MuscleGroup { Pectorals, Deltoids, Biceps, Triceps, Forearm, Trapezius, Lats, Abdominals, Glutes, Quadriceps, Hamstrings, Calves }
public enum FatRegion { Belly, Waist, Hips, Buttocks, Thighs, Breasts, UpperArms, Neck, Face }

[Serializable]
public class BodyDNA
{
    public CharacterSheet sheet;

    [Header("Size (STR)")]
    public float height;            // standing height target (m)
    public float limbLength = 1f;   // arms and legs x this on top of height
    public float extremity = 1f;    // hands and feet x this (STR at half strength)
    public float coreGirth = 1f;    // bone/core girth x this
    public float muscleVolume = 1f; // overall, before the per-group values
    public float[] muscle = new float[12];   // per MuscleGroup: sex default x volume x seed noise

    [Header("Fat (DEX)")]
    public float fatFactor = 1f;    // the DEX curve value
    public float fatBase;           // sex base thickness (m) x fatFactor
    public float[] fat = new float[9];       // per FatRegion thickness (m): fatBase x region weight x noise

    [Header("Head (INT)")]
    public float headScale = 1f;
    public float headElongation;    // longer up and toward the back of the skull
    public float eyeGlow;           // 0..1 emission

    [Header("Seed")]
    public float breastSize;        // radius (m), 0 for males; grows with fat
    public float breastDroop;       // downward offset (m), grows with size
    public float brow = 1f, cheekbones = 1f, jaw = 1f, nose = 1f, chin = 1f;
    public float posture;           // spine pitch (deg, + = slouch)
    public float jitter;            // 0..1 (DEX twitchiness, Phase 6)

    public float Muscle(MuscleGroup g) => muscle[(int)g];
    public float Fat(FatRegion r) => fat[(int)r];

    public static BodyDNA Compute(CharacterSheet sheet, BodyRules rules = null)
    {
        rules ??= BodyRules.Default;
        var t = rules.Template(sheet.sex);
        float s = sheet.S, i = sheet.I;
        var d = new BodyDNA { sheet = sheet };
        PcgRandom R(string stream) => new PcgRandom(sheet.seed, stream);

        // ---------- STR: size ----------
        float hNoise = 1f + rules.heightNoise * R("height").Signed();
        d.height = t.height * (1f + rules.strHeight * s) * hNoise;
        d.limbLength = 1f + rules.strLimbLength * s;
        // Hands and feet follow STR's size change only partly, so they stay comically small.
        float limbScale = (1f + rules.strHeight * s) * d.limbLength;
        d.extremity = (1f + rules.extremityStrFactor * (limbScale - 1f)) / (1f + rules.strHeight * s);
        d.coreGirth = 1f + rules.strCoreGirth * s;
        d.muscleVolume = Mathf.Max(rules.minMuscleVolume, 1f + rules.strMuscleVolume * s);
        for (int g = 0; g < d.muscle.Length; g++)
            d.muscle[g] = t.muscle[g] * d.muscleVolume * (1f + rules.muscleNoise * R("muscle." + (MuscleGroup)g).Signed());

        // ---------- DEX: fat ----------
        d.fatFactor = rules.FatCurve(sheet.DEX);
        d.fatBase = t.baseFat * d.height * d.fatFactor;
        for (int r = 0; r < d.fat.Length; r++)
            d.fat[r] = d.fatBase * t.fat[r] * Mathf.Max(0.2f, 1f + rules.fatDistributionNoise * R("fat." + (FatRegion)r).Signed());
        d.jitter = Mathf.InverseLerp(rules.jitterStart, rules.jitterFull, sheet.DEX);

        // ---------- INT: head ----------
        d.headScale = 1f + rules.intHeadScale * i;
        d.headElongation = rules.intElongation * Mathf.Max(i, 0f);
        d.eyeGlow = Mathf.InverseLerp(rules.eyeGlowStart, rules.eyeGlowFull, sheet.INT);

        // ---------- seed: individuality ----------
        if (t.breast > 0f)
        {
            var b = R("breasts");
            float size = Mathf.Lerp(rules.breastSizeRange.x, rules.breastSizeRange.y, b.Value());
            // More fat on the breasts = larger.
            d.breastSize = t.breast * d.height * size + d.Fat(FatRegion.Breasts) * rules.breastFatGain;
            d.breastDroop = d.breastSize * rules.breastDroop;
        }
        var f = R("face");
        d.brow = 1f + rules.faceNoise * f.Signed();
        d.cheekbones = 1f + rules.faceNoise * f.Signed();
        d.jaw = 1f + rules.faceNoise * f.Signed();
        d.nose = 1f + rules.faceNoise * f.Signed();
        d.chin = 1f + rules.faceNoise * f.Signed();
        d.posture = Mathf.Lerp(rules.postureRange.x, rules.postureRange.y, R("posture").Value());
        return d;
    }

    public override string ToString() =>
        $"{sheet}: h {height:0.00} m, muscle x{muscleVolume:0.00}, fat x{fatFactor:0.00} ({fatBase * 1000f:0} mm), head x{headScale:0.00} elong {headElongation:0.00}, glow {eyeGlow:0.00}, breasts {breastSize * 100f:0.0} cm, jitter {jitter:0.00}";
}
