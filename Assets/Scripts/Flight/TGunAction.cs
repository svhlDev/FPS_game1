using UnityEngine;

// The T-gun's moving parts. On each shot the Upper snaps up along the stem (local +Y) in SnapTime, the gap
// glow and the two energy beams flare in the mode colour, then it closes in CloseTime. The beams stretch with
// the gap and only show while it's open. A shot during the close opens it again from where it is; in rapid
// fire it stays open between shots. Runs only while the gun is moving.
public class TGunAction : MonoBehaviour
{
    public const float SnapTime = 0.05f, CloseTime = 0.12f, FlareTime = 0.07f;
    const float RestGap = 0.002f;     // between the bars at rest

    // How far the Upper lifts. 5.58 cm, which opens the block-out's 5.78 cm gap from the 2 mm gap at rest.
    public float lift = 0.0558f;

    public float Openness { get; private set; }
    public Transform Upper { get; private set; }
    public Transform Beams { get; private set; }
    public Renderer[] GapGlow { get; private set; }
    public bool Hold;     // test hook: stays open (for screenshots)

    Weapon weapon;
    Vector3 rest;
    bool opening;
    float firedAt = -100f, holdUntil;

    // gapGlow: everything that lights up while open, the beams included. beams: built at the full open length,
    // origin on the lower bar's top.
    public void Init(Weapon w, Transform upper, Renderer[] gapGlow, Transform beams)
    {
        weapon = w;
        Upper = upper;
        GapGlow = gapGlow;
        Beams = beams;
        rest = upper.localPosition;
        foreach (var r in gapGlow) r.enabled = false;
        enabled = false;
    }

    public void Fire()
    {
        // Another shot before the last close could finish: stay open until shortly after this one.
        float since = Time.time - firedAt;
        holdUntil = Time.time + SnapTime + (since < SnapTime + CloseTime ? since * 0.5f : 0f);
        firedAt = Time.time;
        opening = true;
        enabled = true;
    }

    // After every Update, so the frame that fires already shows the gun opening.
    void LateUpdate()
    {
        float dt = Time.deltaTime;
        if (opening)
        {
            Openness = Mathf.Min(1f, Openness + dt / SnapTime);
            if (Openness >= 1f) opening = false;
        }
        else if (!Hold && Time.time >= holdUntil)
        {
            Openness = Mathf.Max(0f, Openness - dt / CloseTime);
        }

        // Fast start and a soft stop on the way up; eases shut.
        float k = opening ? 1f - (1f - Openness) * (1f - Openness) : Openness * Openness * (3f - 2f * Openness);
        Upper.localPosition = rest + Vector3.up * lift * k;
        Beams.localScale = new Vector3(1f, (RestGap + lift * k) / (RestGap + lift), 1f);

        bool open = Openness > 0f;
        var mat = Time.time - firedAt < FlareTime || Hold ? LaserWeapon.BeamMat(weapon.CurrentMode) : LaserWeapon.ModeMat(weapon.CurrentMode);
        foreach (var r in GapGlow)
        {
            r.enabled = open;
            r.sharedMaterial = mat;
        }
        if (!open && !opening) enabled = false;
    }
}
