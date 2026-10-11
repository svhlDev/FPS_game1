using UnityEngine;

// Face Fix Part 1: the classic head proportions every face is built on (and judged against), in head
// units so they scale with any head; the seed and the sex vary them a little.
//   Eyes on the horizontal midline (crown to chin). Face thirds: hairline -> brow line -> nose base ->
//   chin, equal. Mouth a third of the way from the nose base to the chin. The head is 5 eye-widths wide,
//   one eye-width between the eyes; nose width = the inner eye corners, mouth width = the pupils. Ears
//   from the brow line to the nose base, just behind the head's vertical midline in side view. Eyeball
//   diameter ~0.105 x head width, the visible opening ~0.15 x 0.045 head widths (almond).
// The frame: o = the eye line's centre (root space, rest), x right, y up, z forward; all heights (y)
// are metres from o. INT elongates the cranium (up and back), not the face: the canon uses the head's
// length without it.
public struct HeadCanon
{
    public Vector3 o, x, y, z;
    public float halfH, halfW, halfD;          // crown-to-chin / 2, head width / 2, depth / 2
    public float crown, hairline, brow, noseBase, mouth, chin;   // heights from the eye line (m)
    public float eyeWidth;                     // the head's width / 5
    public float eyeSpacing;                   // eye centre from the midline (m): 1 eye-width
    public float eyeballRadius, openingW, openingH;
    public float noseHalfWidth, mouthHalfWidth;
    public float earTop, earBottom, earZ;      // the ear band (heights) and its centre's depth (m from o, + forward)

    public Vector3 Point(float px, float py, float pz) => o + x * px + y * py + z * pz;

    // Head bone i of a plan, its joint at `joint` with axes (x, y, z).
    public static HeadCanon From(BodyPlan plan, int head, Vector3 joint, Vector3 ax, Vector3 ay, Vector3 az)
    {
        var b = plan.bones[head];
        var fd = BodyRules.Default.face;
        float el = plan.dna != null ? plan.dna.headElongation : 0f;
        bool female = plan.sheet.sex == Sex.Female;
        var rng = new PcgRandom(plan.sheet.seed, "canon");
        float V() => fd.canonVariation * rng.Signed();
        var c = new HeadCanon { x = ax, y = ay, z = az };
        c.halfH = b.length / (1f + el) * 0.5f;
        // Width and depth from the height (a head is taller than wide), not the bone's girth.
        c.halfW = c.halfH * (female ? fd.femaleWidthToHeight : fd.widthToHeight) * (1f + V());
        c.halfD = c.halfH * fd.depthToHeight * (1f + V());
        c.o = joint + ay * c.halfH + az * c.halfD * fd.faceForward;
        float H = c.halfH * 2f, W = c.halfW * 2f;
        float third = H * fd.faceThird * (1f + V());
        c.chin = -c.halfH;
        c.crown = c.halfH;
        c.noseBase = c.chin + third;
        c.brow = c.noseBase + third * (1f + V());
        c.hairline = c.brow + third * (1f + V());
        c.mouth = c.noseBase - third * fd.mouthDrop * (1f + V());
        c.eyeWidth = W / fd.eyesAcross;
        c.eyeSpacing = c.eyeWidth * (1f + V());
        float eyeScale = (female ? fd.femaleEyeScale : 1f) * (1f + V());
        c.eyeballRadius = W * fd.eyeballDiameter * 0.5f * eyeScale;
        c.openingW = W * fd.openingWidth * eyeScale;
        c.openingH = W * fd.openingHeight * eyeScale;
        c.noseHalfWidth = c.eyeWidth * 0.5f;
        c.mouthHalfWidth = c.eyeSpacing;
        c.earTop = c.brow; c.earBottom = c.noseBase;
        c.earZ = -c.halfD * fd.earBehind;
        return c;
    }

    // A line's height by name (FaceDef anchors).
    public float Line(FaceLine l)
    {
        switch (l)
        {
            case FaceLine.Crown: return crown;
            case FaceLine.Hairline: return hairline;
            case FaceLine.Brow: return brow;
            case FaceLine.NoseBase: return noseBase;
            case FaceLine.Mouth: return mouth;
            case FaceLine.Chin: return chin;
            default: return 0f;
        }
    }

    public override string ToString() =>
        $"H {halfH * 200f:0.0} W {halfW * 200f:0.0} cm (W/H {halfW / halfH:0.00}); hairline {hairline * 100f:0.0}, brow {brow * 100f:0.0}, nose {noseBase * 100f:0.0}, mouth {mouth * 100f:0.0}, chin {chin * 100f:0.0} cm; " +
        $"eye-width {eyeWidth * 100f:0.0}, eyeball r {eyeballRadius * 1000f:0.0} mm";
}

// The canon lines a face part can hang from (its position.y is then an offset from the line, in
// half-head-heights).
public enum FaceLine { EyeLine, Crown, Hairline, Brow, NoseBase, Mouth, Chin }
