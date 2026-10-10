using System.Collections.Generic;
using UnityEngine;

// Laser firing shared by everyone with a beam weapon: the player's T-gun, officers' T-guns and the
// police cars' lethal laser.
//   Fire   : one hitscan shot from a muzzle along the barrel (the first thing that isn't the shooter;
//            people's hit triggers count), with the beam, muzzle flash and impact burst in the mode
//            colour, and the gunfire report for the crowd. Returns what it hit; the caller applies it.
//   Trace  : the same ray without visuals (continuous beams).
//   Beam   : a fading beam line (pooled LineRenderers), HandBeamWidth wide for hand guns.
// Every shot that hits leaves a burn mark and feeds the target's burn chain (BurnMarks).
// Shared materials: the mode colours (strips, emitter), the bright beams and the thin aiming line.
public static class LaserWeapon
{
    public static readonly Color StunColor = new Color(0.2f, 0.6f, 1f);
    public static readonly Color LethalColor = new Color(1f, 0.12f, 0.08f);
    public const float HandBeamWidth = 0.03f, CarBeamWidth = 0.06f;

    // Test readouts.
    public static int Shots;
    public static Vector3 LastOrigin;
    public static Collider LastHit;
    public static Transform LastShooter;

    static Material stunMode, lethalMode, stunBeam, lethalBeam, aimLine;

    public static Color ColorOf(Weapon.Mode m) => m == Weapon.Mode.Stun ? StunColor : LethalColor;

    public static Material ModeMat(Weapon.Mode m)
    {
        ref Material mat = ref (m == Weapon.Mode.Stun ? ref stunMode : ref lethalMode);
        if (mat == null) mat = Unlit(ColorOf(m) * 2.5f, m + " mode");
        return mat;
    }

    public static Material BeamMat(Weapon.Mode m)
    {
        ref Material mat = ref (m == Weapon.Mode.Stun ? ref stunBeam : ref lethalBeam);
        if (mat == null) mat = Unlit(ColorOf(m) * 6f, m + " beam");
        return mat;
    }

    // The police car's thin red aiming line before the beam.
    public static Material AimLineMat => aimLine != null ? aimLine : aimLine = Unlit(new Color(1f, 0.1f, 0.08f) * 1.5f, "Laser aim line");

    static Material Unlit(Color c, string name)
    {
        var m = new Material(Shader.Find("Universal Render Pipeline/Unlit")) { name = name, enableInstancing = true };
        m.SetColor("_BaseColor", c);
        return m;
    }

    public static bool Trace(Vector3 origin, Vector3 dir, float range, Transform shooter, out RaycastHit hit) =>
        AimSolver.First(origin, dir.normalized, range, shooter, out hit);

    public static bool Fire(Vector3 muzzle, Vector3 dir, float range, Transform shooter, Weapon.Mode mode, out RaycastHit hit)
    {
        dir.Normalize();
        bool didHit = Trace(muzzle, dir, range, shooter, out hit);
        Vector3 end = didHit ? hit.point : muzzle + dir * range;
        bool stun = mode == Weapon.Mode.Stun;
        Shots++;
        LastOrigin = muzzle;
        LastHit = didHit ? hit.collider : null;
        LastShooter = shooter;

        Beam(muzzle, end, mode, HandBeamWidth, 0.08f);
        Effects.Glow(muzzle + dir * 0.02f, 0.025f, stun, 0.06f);
        if (didHit)
        {
            Effects.Glow(hit.point + hit.normal * 0.03f, 0.14f, stun, 0.15f);
            for (int i = 0; i < 3; i++)
                Effects.Debris(hit.point + hit.normal * 0.05f, (hit.normal + Random.insideUnitSphere * 0.8f) * Random.Range(2f, 5f), 0.025f);
            BurnMarks.Hit(hit, dir, mode);
        }
        PedestrianSystem.ReportDanger(muzzle, 40f);
        return didHit;
    }

    public static void Beam(Vector3 a, Vector3 b, Weapon.Mode mode, float width, float life) =>
        LaserBeams.I.Show(a, b, BeamMat(mode), width, life);
}

// Pool of fading beam lines (created at runtime only; never saved in a scene).
[DefaultExecutionOrder(210)]
public class LaserBeams : MonoBehaviour
{
    struct Live { public LineRenderer line; public float start, life, width; }
    static LaserBeams instance;
    readonly List<Live> live = new List<Live>();
    readonly Stack<LineRenderer> pool = new Stack<LineRenderer>();

    public static LaserBeams I => instance != null ? instance : instance = new GameObject("LaserBeams").AddComponent<LaserBeams>();

    public void Show(Vector3 a, Vector3 b, Material mat, float width, float life)
    {
        LineRenderer l;
        if (pool.Count > 0) l = pool.Pop();
        else
        {
            l = new GameObject("Beam").AddComponent<LineRenderer>();
            l.transform.SetParent(transform, false);
            l.positionCount = 2;
            l.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            l.receiveShadows = false;
        }
        l.sharedMaterial = mat;
        l.SetPosition(0, a);
        l.SetPosition(1, b);
        l.widthMultiplier = width;
        l.enabled = true;
        live.Add(new Live { line = l, start = Time.time, life = life, width = width });
    }

    // Fade: the beam thins away over its life.
    void LateUpdate()
    {
        for (int i = live.Count - 1; i >= 0; i--)
        {
            var b = live[i];
            float t = (Time.time - b.start) / b.life;
            if (t >= 1f || b.line == null)
            {
                if (b.line != null) { b.line.enabled = false; pool.Push(b.line); }
                live.RemoveAt(i);
                continue;
            }
            b.line.widthMultiplier = b.width * (1f - t * t);
        }
    }
}
