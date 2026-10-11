using System.Collections.Generic;
using UnityEngine;

// Face Fix Part 1: the proportion canon drawn over a generated head (debug: the head lineup). Two sets of
// lines on the head joint, one for the front view and one for the side view:
//   yellow  the eye line (the head's horizontal midline) and the vertical midline
//   cyan    crown, hairline, brow line, nose base, chin (the face thirds between hairline and chin)
//   magenta the mouth line
//   green   eye-width ticks along the eye line, the canon eye openings (almonds), nose and mouth widths
//   orange  the ear band (brow line to nose base), in side view at the canon ear position
public class HeadCanonOverlay : MonoBehaviour
{
    public enum View { Off, Front, Side }
    GameObject front, side;
    static Material mat;

    public static HeadCanonOverlay Attach(CharacterFigure f)
    {
        if (f == null || !f.Generated || f.HeadJoint == null) return null;
        var plan = f.Plan;
        int hi = plan.Index("Head");
        Vector3 joint = plan.JointPos(hi);
        var c = HeadCanon.From(plan, hi, joint, Vector3.right, Vector3.up, Vector3.forward);
        var o = f.HeadJoint.gameObject.AddComponent<HeadCanonOverlay>();
        o.Build(c, joint, f.HeadJoint);
        o.Show(View.Off);
        return o;
    }

    public void Show(View v) { front.SetActive(v == View.Front); side.SetActive(v == View.Side); }

    void Build(HeadCanon c, Vector3 joint, Transform head)
    {
        if (mat == null) mat = new Material(Shader.Find("Universal Render Pipeline/Unlit")) { name = "Canon lines" };
        Color yellow = new Color(1f, 0.9f, 0.1f), cyan = new Color(0.2f, 0.9f, 1f), magenta = new Color(1f, 0.3f, 0.9f),
              green = new Color(0.3f, 1f, 0.3f), orange = new Color(1f, 0.55f, 0.1f);
        front = new GameObject("CanonFront"); front.transform.SetParent(head, false);
        side = new GameObject("CanonSide"); side.transform.SetParent(head, false);
        float w = c.halfW * 1.25f, d = c.halfD * 1.25f;
        // Lines in front of the face (front set) / beside the head (side set), in the head joint's space.
        Vector3 L(float x, float y, float z) => c.Point(x, y, z) - joint;
        float zf = c.halfD * 1.5f, xs = c.halfW * 1.6f;
        void Line(GameObject set, Color col, float width, params Vector3[] pts)
        {
            var go = new GameObject("line"); go.transform.SetParent(set.transform, false);
            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = false; lr.positionCount = pts.Length; lr.SetPositions(pts);
            lr.widthMultiplier = width; lr.numCapVertices = 0;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; lr.receiveShadows = false;
            var m = new Material(mat) { color = col };
            lr.sharedMaterial = m;
            go.layer = head.gameObject.layer;
        }
        const float thin = 0.0007f, thick = 0.0011f;
        foreach (var (y, col, wd) in new[] { (c.crown, cyan, thin), (c.hairline, cyan, thin), (c.brow, cyan, thin), (0f, yellow, thick),
                                             (c.noseBase, cyan, thin), (c.mouth, magenta, thin), (c.chin, cyan, thin) })
        {
            Line(front, col, wd, L(-w, y, zf), L(w, y, zf));
            Line(side, col, wd, L(xs, y, -d), L(xs, y, d));
        }
        // Front: the vertical midline, head width, eye-width ticks, the canon eye openings, nose and mouth widths.
        Line(front, yellow, thin, L(0f, c.crown, zf), L(0f, c.chin, zf));
        Line(front, cyan, thin, L(-c.halfW, c.crown, zf), L(-c.halfW, c.chin, zf));
        Line(front, cyan, thin, L(c.halfW, c.crown, zf), L(c.halfW, c.chin, zf));
        float tick = c.eyeWidth * 0.12f;
        for (int k = -5; k <= 5; k += 2)
        {
            float x = k * c.eyeWidth * 0.5f;
            Line(front, green, thin, L(x, -tick, zf), L(x, tick, zf));
        }
        foreach (float s in new[] { -1f, 1f })
        {
            var pts = new List<Vector3>();
            for (int i = 0; i <= 24; i++)
            {
                float a = i / 24f * Mathf.PI * 2f;
                pts.Add(L(s * c.eyeSpacing + Mathf.Cos(a) * c.openingW * 0.5f, Mathf.Sin(a) * c.openingH * 0.5f, zf));
            }
            Line(front, green, thin, pts.ToArray());
            Line(front, green, thin, L(s * c.noseHalfWidth, c.noseBase - tick, zf), L(s * c.noseHalfWidth, c.noseBase + tick, zf));
            Line(front, green, thin, L(s * c.mouthHalfWidth, c.mouth - tick, zf), L(s * c.mouthHalfWidth, c.mouth + tick, zf));
            // The ear band at the sides.
            Line(front, orange, thick, L(s * c.halfW * 1.1f, c.earTop, zf), L(s * c.halfW * 1.1f, c.earBottom, zf));
        }
        // Side: the vertical midline (depth), the face's front, the ear band.
        Line(side, yellow, thin, L(xs, c.crown, 0f), L(xs, c.chin, 0f));
        Line(side, cyan, thin, L(xs, c.crown, c.halfD), L(xs, c.chin, c.halfD));
        Line(side, cyan, thin, L(xs, c.crown, -c.halfD), L(xs, c.chin, -c.halfD));
        float ew = c.halfD * 0.22f;
        Line(side, orange, thick, L(xs, c.earTop, c.earZ - ew), L(xs, c.earTop, c.earZ + ew), L(xs, c.earBottom, c.earZ + ew), L(xs, c.earBottom, c.earZ - ew), L(xs, c.earTop, c.earZ - ew));
    }
}
