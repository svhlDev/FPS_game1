using UnityEngine;

// Eyeballs that look at a target (CharacterFigure.LookTarget), within maxAngle of where the head faces;
// otherwise straight ahead. Runs after the animator has posed the head.
[DefaultExecutionOrder(70)]
public class EyeLook : MonoBehaviour
{
    public Transform eyeL, eyeR;
    public float maxAngle = 30f;
    public float speed = 20f;   // saccade-ish: fast
    CharacterFigure fig;

    void Awake() => fig = GetComponent<CharacterFigure>();

    void LateUpdate()
    {
        if (fig == null || fig.HeadJoint == null) return;
        float k = 1f - Mathf.Exp(-speed * Time.deltaTime);
        Aim(eyeL, k);
        Aim(eyeR, k);
    }

    void Aim(Transform eye, float k)
    {
        if (eye == null) return;
        Quaternion want = Quaternion.identity;
        if (fig.LookTarget.HasValue)
        {
            Vector3 local = eye.parent.InverseTransformDirection(fig.LookTarget.Value - eye.position);
            if (local.sqrMagnitude > 1e-6f)
            {
                Quaternion q = Quaternion.LookRotation(local.normalized, Vector3.up);
                want = Quaternion.RotateTowards(Quaternion.identity, q, maxAngle);
            }
        }
        eye.localRotation = Quaternion.Slerp(eye.localRotation, want, k);
    }
}
