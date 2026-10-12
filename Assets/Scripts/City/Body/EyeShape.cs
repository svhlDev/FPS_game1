using System;
using UnityEngine;

// Face Fix Part 3: the eye opening, shared by the SDF lids (BodySdfKernel), the blinking lid cap
// (FaceAnimator.LidMesh) and the eye's lid shadow. Angles in radians, on the eyeball's sphere in the
// eye's frame (x lateral = toward the outer corner, y up, z forward):
//   azimuth phi from forward (+ toward the outer corner), elevation theta.
//   The opening is an almond between the inner corner (phi = -inner) and the outer one (phi = +outer):
//   t = phi / outer (outer side) or phi / inner (inner side), -1 .. 1. The corners sit at theta = tilt x t
//   (the outer corner higher); the upper curve rises `upper` above that line, peaking a little toward
//   the inner side, the lower one drops `lower` below it, peaking toward the outer side.
// The lids are shells round the eyeball (thickU / thickL thick); the crease is a groove `crease` above
// the upper lid's edge, creaseDepth deep (thinner with face fat).
[Serializable]
public struct EyeShape
{
    public float upper, lower, inner, outer, tilt;
    public float thickU, thickL, crease, creaseDepth;

    public const float Point = 0.7f;   // almond pointedness (the curves' (1 - t^2)^Point)

    public static float T(in EyeShape s, float phi) => phi >= 0f ? phi / s.outer : phi / s.inner;

    static float Bump(float t) { float b = 1f - t * t; return b > 0f ? Mathf.Pow(b, Point) : 0f; }

    public static float Upper(in EyeShape s, float t) { t = Mathf.Clamp(t, -1f, 1f); return s.tilt * t + s.upper * Bump(t) * (1f - 0.2f * t); }
    public static float Lower(in EyeShape s, float t) { t = Mathf.Clamp(t, -1f, 1f); return s.tilt * t - s.lower * Bump(t) * (1f + 0.2f * t); }

    // Signed angular distance (radians, negative inside) of direction d (eye frame, unit) to the opening.
    public static float Opening(in EyeShape s, Vector3 d)
    {
        float phi = Mathf.Atan2(d.x, d.z), theta = Mathf.Asin(Mathf.Clamp(d.y, -1f, 1f));
        float t = T(s, phi);
        float side = phi >= 0f ? phi - s.outer : -phi - s.inner;
        return Mathf.Max(side, Mathf.Max(theta - Upper(s, t), Lower(s, t) - theta));
    }

    // The canon's opening for a character (sex, seed).
    public static EyeShape For(CharacterSheet sheet, float eyeRadius)
    {
        var fd = BodyRules.Default.face;
        var r = new PcgRandom(sheet.seed, "eyeShape");
        float D = Mathf.Deg2Rad;
        bool fem = sheet.sex == Sex.Female;
        return new EyeShape
        {
            upper = (fd.lidUpper + (fem ? 1.5f : 0f) + 3f * r.Signed()) * D,
            lower = (fd.lidLower + 2f * r.Signed()) * D,
            inner = fd.lidInner * D, outer = fd.lidOuter * D,
            tilt = (fd.lidTilt + 3f * r.Signed()) * D,
            thickU = fd.lidThickness.x, thickL = fd.lidThickness.y,
            crease = fd.lidCrease * D, creaseDepth = fd.lidCreaseDepth,
        };
    }
}
