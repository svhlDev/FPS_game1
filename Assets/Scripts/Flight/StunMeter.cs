using UnityEngine;

// Stun builds up instead of dropping someone in one hit (officers, pedestrians, the player; cars keep
// their thrust hiccup). Each hit adds a level and slows the target more; after DrainDelay s without a hit
// the levels drain at DrainRate per second. At LevelsToFreeze the target freezes mid-motion for FreezeTime
// s (hits do nothing meanwhile), then the meter is back to zero.
// No Update: everything is worked out from the clock when asked, so a crowd costs nothing.
public class StunMeter
{
    public const int LevelsToFreeze = 10;
    public const float DrainDelay = 3f, DrainRate = 1f, FreezeTime = 3f;

    // The slow-down curve: straight from full speed at level 0 to this at level 9, the last before the
    // freeze (about 9.4% per hit: 91, 81, 72, 62, 53, 43, 34, 24, 15 %).
    public const float SlowestSpeed = 0.15f;

    float stored;               // level at the last hit
    float lastHit = -100f;
    float frozenUntil = -1f;

    public bool Frozen => Time.time < frozenUntil;

    public float Level
    {
        get
        {
            if (frozenUntil >= 0f)
            {
                if (Frozen) return LevelsToFreeze;
                stored = 0f;
                frozenUntil = -1f;
                return 0f;
            }
            return Mathf.Max(0f, stored - Mathf.Max(0f, Time.time - lastHit - DrainDelay) * DrainRate);
        }
    }

    public static float SpeedAt(float level) => Mathf.Lerp(1f, SlowestSpeed, Mathf.Clamp01(level / (LevelsToFreeze - 1)));

    // Multiply the target's speed by this (0 while frozen).
    public float SpeedFactor => Frozen ? 0f : SpeedAt(Level);

    // One stun hit. True if this one froze the target.
    public bool Hit()
    {
        if (Frozen) return false;
        stored = Level + 1f;
        lastHit = Time.time;
        if (stored < LevelsToFreeze) return false;
        frozenUntil = Time.time + FreezeTime;
        return true;
    }

    public void Reset()
    {
        stored = 0f;
        lastHit = -100f;
        frozenUntil = -1f;
    }
}
