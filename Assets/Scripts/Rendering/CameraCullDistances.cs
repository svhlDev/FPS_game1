using UnityEngine;

// Per-layer cull distances for this camera (Camera.layerCullDistances isn't serialized, so it is set
// here when the camera is enabled). The smog hides the cutoff.
[RequireComponent(typeof(Camera))]
public class CameraCullDistances : MonoBehaviour
{
    [System.Serializable]
    public struct Entry
    {
        public string layer;
        public float distance;
    }

    public Entry[] distances =
    {
        new Entry { layer = "Detail", distance = 700f },
        new Entry { layer = "Traffic", distance = 1000f },
    };

    public float DistanceFor(string layer)
    {
        foreach (var e in distances) if (e.layer == layer) return e.distance;
        return 0f;
    }

    void OnEnable()
    {
        var d = new float[32];
        foreach (var e in distances)
        {
            int l = LayerMask.NameToLayer(e.layer);
            if (l >= 0) d[l] = e.distance;
        }
        var cam = GetComponent<Camera>();
        cam.layerCullDistances = d;
    }
}
