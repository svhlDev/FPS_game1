using UnityEngine;

// Marks a gameplay ledge for the future ledge grab. The ledge's top surface is on a 10 m grid line;
// `outward` points away from the wall it is attached to.
public class LedgeMarker : MonoBehaviour
{
    public Vector3 outward = Vector3.forward;
    public float width = 6f;

    public float TopY
    {
        get
        {
            var c = GetComponent<Collider>();
            return c != null ? c.bounds.max.y : transform.position.y;
        }
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.green;
        Vector3 top = new Vector3(transform.position.x, TopY, transform.position.z);
        Gizmos.DrawLine(top, top + outward * 1.5f);
    }
}
