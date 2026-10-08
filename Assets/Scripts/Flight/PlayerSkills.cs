using UnityEngine;

// Player skills, persisted with PlayerPrefs for now. Bypass: earned by hijacking cars, used to restart
// a disabled car and to break free from an arrest (and later to disable car alarms).
public static class PlayerSkills
{
    const string BypassKey = "bypassXP";
    const int HijacksPerLevel = 5;
    const int MaxLevel = 5;

    public static int BypassXP => PlayerPrefs.GetInt(BypassKey, 0);
    public static int BypassLevel => Mathf.Min(MaxLevel, BypassXP / HijacksPerLevel);

    public static void AddHijack()
    {
        PlayerPrefs.SetInt(BypassKey, BypassXP + 1);
        PlayerPrefs.Save();
    }

    // Restart sequence: 5 prompts minus half the level (at least 3); twice as long when shot down.
    public static int RestartLength(bool shotDown)
    {
        int n = Mathf.Max(3, 5 - BypassLevel / 2);
        return shotDown ? n * 2 : n;
    }

    // Seconds each prompt stays open.
    public static float PromptWindow => 0.6f + 0.08f * BypassLevel;
}
