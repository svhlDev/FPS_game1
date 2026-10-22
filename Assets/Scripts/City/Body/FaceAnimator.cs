using System.Collections.Generic;
using UnityEngine;

// The face rig of a generated body: blinking eyelids and an opening jaw.
//   Eyelids (Face Fix Part 3): the head mesh carries the lid skin and a slightly wide opening; a lid cap
//   per lid (LidMesh) makes the exact almond (EyeShape) with a visible margin. The upper one rolls down
//   over the opening in a blink (every 2-6 s, ~0.15 s).
//   Jaw: the jaw bone (BodyBuild weights the lower face to it, hinged under the ears). Opens by MouthOpen
//   (0..1 -> 0..maxJaw degrees): bursts of talking (random syllables) now and then, slightly parted at
//   rest. Talk(seconds) from gameplay; MouthOverride / BlinkOverride (>= 0) hold a pose (tests, shouts).
// Only near the camera; far away the face stays at rest.
public class FaceAnimator : MonoBehaviour
{
    public Transform Jaw;
    public readonly List<Transform> UpperLids = new List<Transform>(), LowerLids = new List<Transform>();
    public float maxJaw = 22f;
    public float lidOpen = 0f, lidClosed = 54f;   // upper lid roll (degrees about the eye's x axis): open .. meeting the lower lid
    public float activeDistance = 30f;
    public float MouthOverride = -1f, BlinkOverride = -1f;

    float mouth, mouthTarget, nextSyllable, talkUntil, nextTalk, nextBlink, blinkT = -1f;
    static readonly Dictionary<(int, int, int, int), Mesh> lidMeshes = new Dictionary<(int, int, int, int), Mesh>();

    void Start()
    {
        nextBlink = Time.time + Random.Range(1f, 5f);
        nextTalk = Time.time + Random.Range(3f, 15f);
    }

    public void Talk(float seconds) => talkUntil = Time.time + seconds;

    void LateUpdate()
    {
        using var _alloc = AllocProbe.Begin("FaceAnimator.LateUpdate");
        var cam = Camera.main;
        if (cam != null && (cam.transform.position - transform.position).sqrMagnitude > activeDistance * activeDistance && MouthOverride < 0f && BlinkOverride < 0f) return;
        float t = Time.time, dt = Time.deltaTime;

        // Blink.
        if (blinkT < 0f && t > nextBlink) { blinkT = 0f; nextBlink = t + Random.Range(2f, 6f); }
        float close = 0f;
        if (blinkT >= 0f)
        {
            blinkT += dt;
            float u = blinkT / 0.15f;
            close = u < 0.5f ? u * 2f : Mathf.Max(0f, 2f - u * 2f);
            if (u >= 1f) blinkT = -1f;
        }
        if (BlinkOverride >= 0f) close = BlinkOverride;
        var up = Quaternion.Euler(Mathf.Lerp(lidOpen, lidClosed, close), 0f, 0f);
        foreach (var l in UpperLids) if (l != null) l.localRotation = up;

        // Talk: random syllables while talking, slightly parted otherwise.
        if (t > nextTalk) { Talk(Random.Range(1f, 3f)); nextTalk = t + Random.Range(6f, 20f); }
        if (t < talkUntil) { if (t > nextSyllable) { mouthTarget = Random.Range(0.15f, 0.7f) * (Random.value < 0.25f ? 0.2f : 1f); nextSyllable = t + Random.Range(0.08f, 0.18f); } }
        else mouthTarget = 0.04f;
        mouth = Mathf.MoveTowards(mouth, MouthOverride >= 0f ? MouthOverride : mouthTarget, dt * 6f);
        if (Jaw != null) Jaw.localRotation = Quaternion.Euler(mouth * maxJaw, 0f, 0f);
    }

    // A lid (Face Fix Part 3): a cap of a sphere (radius r, just under the head mesh's lid skin) round its
    // pole (+y upper, -y lower) whose edge follows the eye opening (EyeShape: the upper or lower curve
    // across the front, the corners' height beyond them), and the lid margin: a strip from the edge in to
    // rInner (just over the eyeball), the lid's visible thickness. The head mesh's opening is a little
    // wider than the almond; the caps make the exact one and tuck under its skin. The upper lid rolls down
    // (about x) to blink. `left`: mirrored (x is the lateral side). Shared per shape and size.
    public static Mesh LidMesh(float r, float rInner, EyeShape e, bool left, bool upper)
    {
        var key = (Mathf.RoundToInt(r * 20000f), Mathf.RoundToInt((upper ? e.upper : e.lower) * 500f) * 1000 + Mathf.RoundToInt(e.tilt * 500f),
                   Mathf.RoundToInt(rInner * 20000f), (left ? 1 : 0) + (upper ? 2 : 0));
        if (lidMeshes.TryGetValue(key, out var m) && m != null) return m;
        const int rings = 10, segs = 48;
        var v = new List<Vector3>(); var n = new List<Vector3>(); var t = new List<int>();
        float sx = left ? -1f : 1f, sy = upper ? 1f : -1f;
        v.Add(new Vector3(0f, sy * r, 0f)); n.Add(new Vector3(0f, sy, 0f));
        var edge = new Vector3[segs];
        for (int i = 1; i <= rings; i++)
            for (int j = 0; j < segs; j++)
            {
                float az = j * Mathf.PI * 2f / segs - Mathf.PI;          // azimuth from forward, + lateral
                float tt = EyeShape.T(e, az);
                float el = Mathf.Abs(tt) <= 1f ? (upper ? EyeShape.Upper(e, tt) : EyeShape.Lower(e, tt)) : e.tilt * Mathf.Sign(tt);
                // Polar angle from this lid's pole down to its edge (rings denser toward the edge).
                float edgePolar = Mathf.PI * 0.5f - sy * el;
                float k = (float)i / rings; k = 1f - (1f - k) * (1f - k);
                float polar = edgePolar * k;
                var d = new Vector3(sx * Mathf.Sin(polar) * Mathf.Sin(az), sy * Mathf.Cos(polar), Mathf.Sin(polar) * Mathf.Cos(az));
                v.Add(d * r); n.Add(d);
                if (i == rings) edge[j] = d;
            }
        bool flip = !(left ^ !upper);
        void Tri(int a, int b, int c) { if (flip) { t.Add(a); t.Add(c); t.Add(b); } else { t.Add(a); t.Add(b); t.Add(c); } }
        for (int j = 0; j < segs; j++) Tri(0, 1 + (j + 1) % segs, 1 + j);
        for (int i = 1; i < rings; i++)
            for (int j = 0; j < segs; j++)
            {
                int a = 1 + (i - 1) * segs + j, b = 1 + (i - 1) * segs + (j + 1) % segs;
                int c = a + segs, d = b + segs;
                Tri(a, b, c); Tri(b, d, c);
            }
        // The margin: from the edge (radius r) in to rInner, facing the opening.
        int o0 = v.Count;
        for (int j = 0; j < segs; j++)
        {
            Vector3 d = edge[j];
            Vector3 face = Vector3.ProjectOnPlane(new Vector3(0f, -sy, 0f), d);   // toward the opening's middle
            face = face.sqrMagnitude < 1e-6f ? -d : face.normalized;
            v.Add(d * r); n.Add(face);
            v.Add(d * rInner); n.Add(face);
        }
        for (int j = 0; j < segs; j++)
        {
            int a = o0 + 2 * j, b = o0 + 2 * ((j + 1) % segs);
            // (Both sides: the strip is thin and seen from either.)
            t.Add(a); t.Add(a + 1); t.Add(b); t.Add(b); t.Add(a + 1); t.Add(b + 1);
            t.Add(a); t.Add(b); t.Add(a + 1); t.Add(b); t.Add(b + 1); t.Add(a + 1);
        }
        m = new Mesh { name = upper ? "UpperLid" : "LowerLid" };
        m.SetVertices(v); m.SetNormals(n); m.SetTriangles(t, 0); m.RecalculateBounds();
        lidMeshes[key] = m;
        return m;
    }
}
