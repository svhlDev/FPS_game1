using System.Collections.Generic;
using UnityEngine;

// One body for everyone (player, officers, pedestrians, ejected drivers): a jointed figure built from
// primitives in the same blocky style as the city. Every limb segment hangs from a joint at its pivot
// (shoulder, elbow, wrist, hip, knee, ankle, neck), so rotating a joint moves everything below it.
//
//   Root (ground point; the owner's CharacterController lives here)
//   └ Hips ─ Spine ─ Body (box)
//     │      ├ Neck ─ Head (sphere)
//     │      ├ ShoulderL ─ UpperArmL, ElbowL ─ ForearmL, WristL ─ HandL
//     │      └ ShoulderR ─ ...
//     ├ HipL ─ ThighL, KneeL ─ ShinL, AnkleL ─ FootL
//     └ HipR ─ ...
//
// All sizes come from one proportions table scaled by figureHeight (1.5 m = sole to top of head).
// Each part carries a trigger hit collider tagged with its location (BodyPart), under a kinematic
// rigidbody on the hips, for location-based hits (punches now, guns later). Triggers never block
// movement and are ignored by every query that ignores triggers (car sweeps, line of sight).
public class CharacterFigure : MonoBehaviour
{
    public const float DefaultHeight = 1.5f;

    // ---------- proportions (metres at figureHeight 1.5) ----------
    const float HipY = 0.71f, ShoulderY = 1.22f, BodyTop = 1.26f, HeadCentreY = 1.39f, EyeY = 1.40f;
    static readonly Vector3 BodySize = new Vector3(0.30f, 0.55f, 0.30f);
    const float HeadDiameter = 0.22f;
    static readonly Vector3 UpperArmSize = new Vector3(0.08f, 0.28f, 0.08f), ForearmSize = new Vector3(0.08f, 0.26f, 0.08f);
    const float HandSize = 0.08f;
    static readonly Vector3 ThighSize = new Vector3(0.08f, 0.33f, 0.08f), ShinSize = new Vector3(0.08f, 0.33f, 0.08f);
    static readonly Vector3 FootSize = new Vector3(0.10f, 0.05f, 0.18f);
    const float ShoulderX = 0.19f, HipX = 0.07f;
    const float NeckForward = 0.05f;
    // Eyes in front of the head's centre (camera position for the player).
    public float EyeForward => 0.12f * Scale;

    public enum Role { Player, Police, Civilian }

    public float Height { get; private set; } = DefaultHeight;
    public float Scale => Height / DefaultHeight;
    public float EyeHeight => EyeY * Scale;

    // Joints
    public Transform Hips, Spine, Neck, Head;
    public Transform ShoulderL, ShoulderR, ElbowL, ElbowR, WristL, WristR, HandL, HandR;
    public Transform HipL, HipR, KneeL, KneeR, AnkleL, AnkleR;
    public Renderer HeadRenderer { get; private set; }
    public readonly List<Renderer> Renderers = new List<Renderer>();
    public float ArmLength => (UpperArmSize.y + ForearmSize.y + HandSize) * Scale;

    // ---------- build ----------

    public static CharacterFigure Build(Transform root, Role role, System.Random rng = null, float height = DefaultHeight,
                                       bool shadows = false, bool hitColliders = true)
    {
        var f = root.gameObject.AddComponent<CharacterFigure>();
        f.Height = height;
        float s = height / DefaultHeight;
        var look = Palette(role, rng);
        int layer = root.gameObject.layer;

        f.Hips = Joint("Hips", root, new Vector3(0f, HipY, 0f) * s);
        f.Spine = Joint("Spine", f.Hips, Vector3.zero);
        // The head sits a little forward of the torso (and the eyes in front of it), so looking down
        // shows the chest and the feet beyond it rather than the top of the torso box.
        f.Neck = Joint("Neck", f.Spine, new Vector3(0f, BodyTop - HipY, NeckForward) * s);
        f.ShoulderL = Joint("ShoulderL", f.Spine, new Vector3(-ShoulderX, ShoulderY - HipY, 0f) * s);
        f.ShoulderR = Joint("ShoulderR", f.Spine, new Vector3(ShoulderX, ShoulderY - HipY, 0f) * s);
        f.ElbowL = Joint("ElbowL", f.ShoulderL, new Vector3(0f, -UpperArmSize.y, 0f) * s);
        f.ElbowR = Joint("ElbowR", f.ShoulderR, new Vector3(0f, -UpperArmSize.y, 0f) * s);
        f.WristL = Joint("WristL", f.ElbowL, new Vector3(0f, -ForearmSize.y, 0f) * s);
        f.WristR = Joint("WristR", f.ElbowR, new Vector3(0f, -ForearmSize.y, 0f) * s);
        f.HipL = Joint("HipL", f.Hips, new Vector3(-HipX, 0f, 0f) * s);
        f.HipR = Joint("HipR", f.Hips, new Vector3(HipX, 0f, 0f) * s);
        f.KneeL = Joint("KneeL", f.HipL, new Vector3(0f, -ThighSize.y, 0f) * s);
        f.KneeR = Joint("KneeR", f.HipR, new Vector3(0f, -ThighSize.y, 0f) * s);
        f.AnkleL = Joint("AnkleL", f.KneeL, new Vector3(0f, -ShinSize.y, 0f) * s);
        f.AnkleR = Joint("AnkleR", f.KneeR, new Vector3(0f, -ShinSize.y, 0f) * s);

        f.Part("Body", f.Spine, PrimitiveType.Cube, new Vector3(0f, (BodyTop - HipY) * 0.5f, 0f) * s, BodySize * s, look.shirt, BodyPart.Location.Body);
        f.HeadRenderer = f.Part("Head", f.Neck, PrimitiveType.Sphere, new Vector3(0f, HeadCentreY - BodyTop, 0f) * s,
                                Vector3.one * HeadDiameter * s, look.skin, BodyPart.Location.Head);
        f.Head = f.HeadRenderer.transform;
        foreach (var side in new[] { (f.ShoulderL, f.ElbowL, f.WristL, true), (f.ShoulderR, f.ElbowR, f.WristR, false) })
        {
            f.Part("UpperArm", side.Item1, PrimitiveType.Cube, new Vector3(0f, -UpperArmSize.y * 0.5f, 0f) * s, UpperArmSize * s, look.shirt, BodyPart.Location.Arm);
            f.Part("Forearm", side.Item2, PrimitiveType.Cube, new Vector3(0f, -ForearmSize.y * 0.5f, 0f) * s, ForearmSize * s, look.shirt, BodyPart.Location.Arm);
            var hand = f.Part("Hand", side.Item3, PrimitiveType.Cube, new Vector3(0f, -HandSize * 0.5f, 0f) * s, Vector3.one * HandSize * s, look.skin, BodyPart.Location.Hand);
            if (side.Item4) f.HandL = hand.transform; else f.HandR = hand.transform;
        }
        foreach (var side in new[] { (f.HipL, f.KneeL, f.AnkleL), (f.HipR, f.KneeR, f.AnkleR) })
        {
            f.Part("Thigh", side.Item1, PrimitiveType.Cube, new Vector3(0f, -ThighSize.y * 0.5f, 0f) * s, ThighSize * s, look.pants, BodyPart.Location.Leg);
            f.Part("Shin", side.Item2, PrimitiveType.Cube, new Vector3(0f, -ShinSize.y * 0.5f, 0f) * s, ShinSize * s, look.pants, BodyPart.Location.Leg);
            f.Part("Foot", side.Item3, PrimitiveType.Cube, new Vector3(0f, -FootSize.y * 0.5f, 0.04f) * s, FootSize * s, look.shoes, BodyPart.Location.Foot);
        }

        foreach (var r in f.Renderers)
        {
            r.shadowCastingMode = shadows ? UnityEngine.Rendering.ShadowCastingMode.On : UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = shadows;
            r.gameObject.layer = layer;
        }
        foreach (Transform t in f.Hips.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;
        if (hitColliders)
        {
            var rb = f.Hips.gameObject.AddComponent<Rigidbody>(); // moving triggers belong to a kinematic body
            rb.isKinematic = true;
            rb.useGravity = false;
        }
        else
            foreach (var c in f.Hips.GetComponentsInChildren<Collider>(true)) Destroy(c);
        return f;
    }

    static Transform Joint(string name, Transform parent, Vector3 localPos)
    {
        var t = new GameObject(name).transform;
        t.SetParent(parent, false);
        t.localPosition = localPos;
        return t;
    }

    Renderer Part(string name, Transform joint, PrimitiveType type, Vector3 localPos, Vector3 size, Material mat, BodyPart.Location where)
    {
        var go = GameObject.CreatePrimitive(type);
        go.name = name;
        go.transform.SetParent(joint, false);
        go.transform.localPosition = localPos;
        go.transform.localScale = size;
        var col = go.GetComponent<Collider>();
        col.isTrigger = true;
        go.AddComponent<BodyPart>().location = where;
        var r = go.GetComponent<Renderer>();
        r.sharedMaterial = mat;
        Renderers.Add(r);
        return r;
    }

    // ---------- colours ----------

    struct Look { public Material skin, shirt, pants, shoes; }

    static readonly Color[] Skins = { new Color(0.86f, 0.68f, 0.55f), new Color(0.62f, 0.44f, 0.32f), new Color(0.42f, 0.29f, 0.2f), new Color(0.93f, 0.78f, 0.66f) };
    static readonly Color[] Shirts = { new Color(0.32f, 0.3f, 0.34f), new Color(0.38f, 0.26f, 0.24f), new Color(0.22f, 0.3f, 0.32f), new Color(0.4f, 0.37f, 0.3f), new Color(0.26f, 0.27f, 0.36f), new Color(0.3f, 0.33f, 0.26f) };
    static readonly Color[] Pants = { new Color(0.14f, 0.14f, 0.16f), new Color(0.2f, 0.19f, 0.17f), new Color(0.16f, 0.18f, 0.22f) };
    static readonly Dictionary<Color, Material> mats = new Dictionary<Color, Material>();

    static Look Palette(Role role, System.Random rng)
    {
        int Pick(int n) => rng != null ? rng.Next(n) : Random.Range(0, n);
        switch (role)
        {
            case Role.Police:
                return new Look { skin = Mat(Skins[Pick(Skins.Length)]), shirt = Mat(new Color(0.06f, 0.1f, 0.3f)), pants = Mat(new Color(0.04f, 0.06f, 0.16f)), shoes = Mat(new Color(0.03f, 0.03f, 0.03f)) };
            case Role.Player:
                return new Look { skin = Mat(Skins[0]), shirt = Mat(new Color(0.78f, 0.78f, 0.8f)), pants = Mat(new Color(0.22f, 0.23f, 0.26f)), shoes = Mat(new Color(0.1f, 0.1f, 0.11f)) };
            default:
                return new Look { skin = Mat(Skins[Pick(Skins.Length)]), shirt = Mat(Shirts[Pick(Shirts.Length)]), pants = Mat(Pants[Pick(Pants.Length)]), shoes = Mat(new Color(0.08f, 0.08f, 0.08f)) };
        }
    }

    // One shared lit material per colour (SRP batcher / instancing friendly).
    public static Material Mat(Color c)
    {
        if (mats.TryGetValue(c, out var m) && m != null) return m;
        var shader = Shader.Find("Universal Render Pipeline/Lit");
        m = new Material(shader) { name = "Figure " + ColorUtility.ToHtmlStringRGB(c), enableInstancing = true };
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c); else m.color = c;
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0.2f);
        mats[c] = m;
        return m;
    }

    // ---------- far-tier mesh ----------

    static Mesh farMesh;

    // The far crowd: body slab, head and one combined leg block in a single mesh (root at the soles).
    public static Mesh FarMesh()
    {
        if (farMesh != null) return farMesh;
        var c = GameObject.CreatePrimitive(PrimitiveType.Cube);
        var cube = c.GetComponent<MeshFilter>().sharedMesh;
        Destroy(c);
        var sp = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        var sphere = sp.GetComponent<MeshFilter>().sharedMesh;
        Destroy(sp);
        var parts = new[]
        {
            new CombineInstance { mesh = cube, transform = Matrix4x4.TRS(new Vector3(0f, (HipY + BodyTop) * 0.5f, 0f), Quaternion.identity, BodySize) },
            new CombineInstance { mesh = sphere, transform = Matrix4x4.TRS(new Vector3(0f, HeadCentreY, 0f), Quaternion.identity, Vector3.one * HeadDiameter) },
            new CombineInstance { mesh = cube, transform = Matrix4x4.TRS(new Vector3(0f, HipY * 0.5f, 0f), Quaternion.identity, new Vector3(0.22f, HipY, 0.12f)) },
        };
        farMesh = new Mesh { name = "FarFigure" };
        farMesh.CombineMeshes(parts, true, true);
        return farMesh;
    }
}

// Hit location of a figure part (location-based damage later; punches use it now).
public class BodyPart : MonoBehaviour
{
    public enum Location { Head, Body, Arm, Leg, Hand, Foot }
    public Location location;
}
