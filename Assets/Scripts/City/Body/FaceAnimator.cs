using System.Collections.Generic;
using UnityEngine;

// The face rig of a generated body: blinking eyelids and an opening jaw.
//   Eyelids: skin caps just outside each eyeball (LidMesh), on the head joint, tilted a little (outer
//   corner up): almond-shaped openings. At rest the upper lid covers the top of the iris and the lower
//   one its bottom edge; the upper rolls down to meet the lower in a blink (every 2-6 s, ~0.15 s).
//   Jaw: the jaw bone (BodyBuild weights the lower face to it, hinged under the ears). Opens by MouthOpen
//   (0..1 -> 0..maxJaw degrees): bursts of talking (random syllables) now and then, slightly parted at
//   rest. Talk(seconds) from gameplay; MouthOverride / BlinkOverride (>= 0) hold a pose (tests, shouts).
// Only near the camera; far away the face stays at rest.
public class FaceAnimator : MonoBehaviour
{
    public Transform Jaw;
    public readonly List<Transform> UpperLids = new List<Transform>(), LowerLids = new List<Transform>();
    public float maxJaw = 22f;
    public float lidOpen = 0f, lidClosed = 36f;   // upper lid roll (degrees about the eye's x axis): open .. meeting the lower lid
    public float activeDistance = 30f;
    public float MouthOverride = -1f, BlinkOverride = -1f;

    float mouth, mouthTarget, nextSyllable, talkUntil, nextTalk, nextBlink, blinkT = -1f;
    static readonly Dictionary<(int, bool), Mesh> lidMeshes = new Dictionary<(int, bool), Mesh>();

    void Start()
    {
        nextBlink = Time.time + Random.Range(1f, 5f);
        nextTalk = Time.time + Random.Range(3f, 15f);
    }

    public void Talk(float seconds) => talkUntil = Time.time + seconds;

    void LateUpdate()
    {
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

    // A lid: a cap of a sphere (radius r) round its pole (+y upper, -y lower) whose edge isn't a circle:
    // it arches across the front (upper: highest mid-eye, lower: lowest mid-eye) and the two meet past the
    // corners, so the open eye is almond-shaped, not round. Polar angles of the edge from the lid's pole:
    // at the corners (the sides) and mid-eye (the front). Shared per size.
    public const float UpperCorner = 106f, UpperMid = 75f, LowerCorner = 100f, LowerMid = 72f;
    public static Mesh LidMesh(float r, bool upper)
    {
        int key = Mathf.RoundToInt(r * 10000f);
        if (lidMeshes.TryGetValue((key, upper), out var m) && m != null) return m;
        const int rings = 8, segs = 32;
        float sy = upper ? 1f : -1f;
        float corner = (upper ? UpperCorner : LowerCorner) * Mathf.Deg2Rad, mid = (upper ? UpperMid : LowerMid) * Mathf.Deg2Rad;
        var v = new List<Vector3>(); var n = new List<Vector3>(); var t = new List<int>();
        v.Add(new Vector3(0f, sy * r, 0f)); n.Add(new Vector3(0f, sy, 0f));
        for (int i = 1; i <= rings; i++)
            for (int j = 0; j < segs; j++)
            {
                float a = j * Mathf.PI * 2f / segs;
                float front = Mathf.Pow(Mathf.Max(0f, Mathf.Sin(a)), 1.4f);      // +z is the front
                float edge = Mathf.Lerp(corner, mid, front);
                float p = edge * i / rings;
                var d = new Vector3(Mathf.Sin(p) * Mathf.Cos(a), sy * Mathf.Cos(p), Mathf.Sin(p) * Mathf.Sin(a));
                v.Add(d * r); n.Add(d);
            }
        for (int j = 0; j < segs; j++)
        {
            int a = 1 + j, b = 1 + (j + 1) % segs;
            if (upper) { t.Add(0); t.Add(b); t.Add(a); } else { t.Add(0); t.Add(a); t.Add(b); }
        }
        for (int i = 1; i < rings; i++)
            for (int j = 0; j < segs; j++)
            {
                int a = 1 + (i - 1) * segs + j, b = 1 + (i - 1) * segs + (j + 1) % segs;
                int c = a + segs, d = b + segs;
                if (upper) { t.Add(a); t.Add(b); t.Add(c); t.Add(b); t.Add(d); t.Add(c); }
                else { t.Add(a); t.Add(c); t.Add(b); t.Add(b); t.Add(c); t.Add(d); }
            }
        m = new Mesh { name = upper ? "UpperLid" : "LowerLid" };
        m.SetVertices(v); m.SetNormals(n); m.SetTriangles(t, 0); m.RecalculateBounds();
        lidMeshes[(key, upper)] = m;
        return m;
    }
}
