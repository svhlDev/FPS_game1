using UnityEngine;

// Anatomy Rework R8: a debug slider per fill level on a generated head (built with BodyBuild.FillLevels):
// 1 = that level (bone only / + muscle / + fat) shown, 0 = the finished head. Only one should be up at a time.
public class FillLevelDebug : MonoBehaviour
{
    [Range(0, 1)] public float bone, muscle, fat;
    SkinnedMeshRenderer head;
    int[] idx;

    public void Show(int level) { bone = level == 0 ? 1f : 0f; muscle = level == 1 ? 1f : 0f; fat = level == 2 ? 1f : 0f; Apply(); }

    void LateUpdate() => Apply();

    void Apply()
    {
        if (head == null)
        {
            var f = GetComponent<CharacterFigure>();
            head = f != null ? f.HeadRenderer as SkinnedMeshRenderer : null;
            if (head == null || head.sharedMesh == null) return;
            idx = new int[BodyBuild.FillShapes.Length];
            for (int i = 0; i < idx.Length; i++) idx[i] = head.sharedMesh.GetBlendShapeIndex(BodyBuild.FillShapes[i]);
        }
        float[] w = { bone, muscle, fat };
        for (int i = 0; i < idx.Length; i++) if (idx[i] >= 0) head.SetBlendShapeWeight(idx[i], w[i] * 100f);
    }
}
