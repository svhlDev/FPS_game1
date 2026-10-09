using System;
using UnityEngine;

public enum Sex { Male, Female }

// The character sheet every character has. Stats 1..20 (10 = average); the seed decides everything the
// sheet doesn't. Normalized stat: s = (stat - 10) / 10.
[Serializable]
public struct CharacterSheet
{
    public Sex sex;
    [Range(1, 20)] public int STR;
    [Range(1, 20)] public int INT;
    [Range(1, 20)] public int DEX;
    public int seed;

    public CharacterSheet(Sex sex, int str, int intel, int dex, int seed)
    {
        this.sex = sex; STR = Mathf.Clamp(str, 1, 20); INT = Mathf.Clamp(intel, 1, 20); DEX = Mathf.Clamp(dex, 1, 20); this.seed = seed;
    }

    public float S => Norm(STR);
    public float I => Norm(INT);
    public float D => Norm(DEX);
    public static float Norm(int stat) => (stat - 10) / 10f;

    // NPC sheet from a seed: sex 50/50, stats normal around 10 (+ a role bias), clamped to 1..20.
    public static CharacterSheet Roll(int seed, float strBias = 0f, float intBias = 0f, float dexBias = 0f, float sd = 3f)
    {
        var r = new PcgRandom(seed, "sheet");
        Sex sex = r.Value() < 0.5f ? Sex.Male : Sex.Female;
        int Stat(float bias) => Mathf.Clamp(Mathf.RoundToInt(10f + bias + r.Normal() * sd), 1, 20);
        int str = Stat(strBias), intel = Stat(intBias), dex = Stat(dexBias);
        return new CharacterSheet(sex, str, intel, dex, seed);
    }

    public override string ToString() => $"{sex} STR {STR} INT {INT} DEX {DEX} seed {seed}";
}
