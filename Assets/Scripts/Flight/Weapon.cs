using System.Collections.Generic;
using UnityEngine;

// A hand weapon. Barrel along local +Z; the transform's origin is the Grip (where the hand holds it),
// Muzzle is a child at the emitter on the barrel's nose.
// The T-gun (player and officers): a slim gunmetal barrel bar sitting just above the hand, a matte
// black grip stem under the barrel's centre raked back 12 degrees, two mode strips along the barrel
// sides and an emitter square on the nose in the mode colour (blue stun, red lethal).
// Two modes with their own stats: Stun (people down 2 s, cars hiccup, no damage) and Lethal.
public class Weapon : MonoBehaviour
{
    public enum Mode { Stun, Lethal }

    public Transform muzzle;
    public Transform grip;

    [Header("Stun")]
    public float stunInterval = 0.6f;
    public float stunRange = 60f;
    public float stunTime = 2f;
    public float carHiccup = 0.5f;

    [Header("Lethal")]
    public float lethalInterval = 0.25f;
    public float lethalRange = 300f;
    public float damage = 25f;          // people (head x2, body x1, limbs x0.6)
    public float carDamage = 15f;       // through VehicleHealth

    [Header("Recoil")]
    public float recoilPitch = 6f;      // degrees, barrel kicks up
    public float recoilBack = 0.05f;    // metres, hand pushed back

    public Mode CurrentMode { get; private set; } = Mode.Stun;
    public float FireInterval => CurrentMode == Mode.Stun ? stunInterval : lethalInterval;
    public float Range => CurrentMode == Mode.Stun ? stunRange : lethalRange;

    // Grip to muzzle along the barrel (for the arm's close-range pull-back).
    public float Length => muzzle != null ? muzzle.localPosition.z : 0.2f;

    readonly List<Renderer> modeParts = new List<Renderer>();
    static Material gunmetal, stemBlack;

    public void SetMode(Mode m)
    {
        CurrentMode = m;
        var mat = LaserWeapon.ModeMat(m);
        foreach (var r in modeParts) if (r != null) r.sharedMaterial = mat;
    }

    public static Weapon BuildTGun(Transform parent, int layer, Mode mode = Mode.Stun)
    {
        var root = new GameObject("TGun").transform;
        root.SetParent(parent, false);
        root.gameObject.layer = layer;
        if (gunmetal == null)
        {
            gunmetal = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = "Gunmetal", enableInstancing = true };
            gunmetal.SetColor("_BaseColor", new Color(0.2f, 0.21f, 0.23f));
            gunmetal.SetFloat("_Metallic", 0.6f);
            gunmetal.SetFloat("_Smoothness", 0.55f);
            stemBlack = CharacterFigure.Mat(new Color(0.03f, 0.03f, 0.035f));
        }
        var w = root.gameObject.AddComponent<Weapon>();
        w.grip = root;

        // Barrel bar 0.032 x 0.036 x 0.20 (z -0.10 .. 0.10), its underside just inside the top of the
        // 0.08 hand (hand centre = origin). The last 0.015 is a lower nose with 45-degree chamfers.
        const float by = 0.045f, hw = 0.032f, hh = 0.036f;
        Box(root, new Vector3(0f, by, -0.0075f), new Vector3(hw, hh, 0.185f), Quaternion.identity, gunmetal, layer);
        Box(root, new Vector3(0f, by, 0.0925f), new Vector3(hw, 0.024f, 0.015f), Quaternion.identity, gunmetal, layer);
        foreach (float sy in new[] { 1f, -1f })
            Box(root, new Vector3(0f, by + sy * 0.012f, 0.085f), new Vector3(hw, 0.0085f, 0.0085f), Quaternion.Euler(45f, 0f, 0f), gunmetal, layer);

        // Grip stem 0.026 x 0.085 x 0.036, raked back 12 degrees; the hand covers its upper half.
        Box(root, new Vector3(0f, -0.021f, -0.004f), new Vector3(0.026f, 0.085f, 0.036f), Quaternion.Euler(12f, 0f, 0f), stemBlack, layer);

        // Mode strips along the barrel sides and the emitter square on the nose.
        foreach (float sx in new[] { 1f, -1f })
            w.modeParts.Add(Box(root, new Vector3(sx * (hw * 0.5f + 0.002f), by, -0.01f), new Vector3(0.004f, 0.006f, 0.15f), Quaternion.identity, null, layer));
        w.modeParts.Add(Box(root, new Vector3(0f, by, 0.1015f), new Vector3(0.018f, 0.018f, 0.003f), Quaternion.identity, null, layer));

        w.muzzle = new GameObject("Muzzle").transform;
        w.muzzle.SetParent(root, false);
        w.muzzle.localPosition = new Vector3(0f, by, 0.104f);
        w.SetMode(mode);
        return w;
    }

    static Renderer Box(Transform parent, Vector3 pos, Vector3 size, Quaternion rot, Material mat, int layer)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Destroy(go.GetComponent<Collider>());
        go.layer = layer;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = pos;
        go.transform.localRotation = rot;
        go.transform.localScale = size;
        var r = go.GetComponent<Renderer>();
        r.sharedMaterial = mat;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
        return r;
    }
}
