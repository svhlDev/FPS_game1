using UnityEngine;

// A hand weapon. Barrel along local +Z; the transform's origin is the Grip (where the hand holds it),
// Muzzle is a child at the front of the barrel. Placeholder pistol: two dark grey boxes.
public class Weapon : MonoBehaviour
{
    public Transform muzzle;
    public Transform grip;
    public float fireInterval = 0.25f;
    public float damage = 25f;
    public float range = 300f;
    public float recoilPitch = 6f;     // degrees, barrel kicks up
    public float recoilBack = 0.05f;   // metres, hand pushed back

    // Grip to muzzle along the barrel (for the arm's close-range pull-back).
    public float Length => muzzle != null ? muzzle.localPosition.z : 0.2f;

    static Material gunMat;

    public static Weapon BuildPistol(Transform parent, int layer)
    {
        var root = new GameObject("Pistol").transform;
        root.SetParent(parent, false);
        root.gameObject.layer = layer;
        if (gunMat == null) gunMat = CharacterFigure.Mat(new Color(0.16f, 0.16f, 0.17f));
        Box("Grip", root, new Vector3(0f, -0.02f, 0f), new Vector3(0.035f, 0.10f, 0.05f), layer);
        Box("Slide", root, new Vector3(0f, 0.045f, 0.06f), new Vector3(0.035f, 0.04f, 0.20f), layer);
        var w = root.gameObject.AddComponent<Weapon>();
        w.grip = root;
        w.muzzle = new GameObject("Muzzle").transform;
        w.muzzle.SetParent(root, false);
        w.muzzle.localPosition = new Vector3(0f, 0.045f, 0.16f);
        return w;
    }

    static void Box(string name, Transform parent, Vector3 pos, Vector3 size, int layer)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        Destroy(go.GetComponent<Collider>());
        go.layer = layer;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = pos;
        go.transform.localScale = size;
        var r = go.GetComponent<Renderer>();
        r.sharedMaterial = gunMat;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
    }
}
