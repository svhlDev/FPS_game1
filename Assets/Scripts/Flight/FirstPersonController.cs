using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(CharacterController))]
public class FirstPersonController : MonoBehaviour
{
    public Camera playerCamera;
    [Tooltip("The 'Head' object the camera sits under. Defaults to the camera's parent.")]
    public Transform cameraRoot;
    public float walkSpeed = 5f;
    public float sprintSpeed = 9f;
    public float jumpHeight = 1.4f;
    public float gravity = -25f;
    public float lookSensitivity = 0.1f;
    public float interactRange = 5f;

    CharacterController cc;
    float pitch;
    float verticalVelocity;
    int blockInteractFrame = -1;

    void Awake()
    {
        cc = GetComponent<CharacterController>();
        if (cameraRoot == null) cameraRoot = playerCamera.transform.parent;
    }

    void OnEnable()
    {
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
        verticalVelocity = 0f;
    }

    void Update()
    {
        var kb = Keyboard.current;
        var mouse = Mouse.current;
        if (kb == null || mouse == null) return;

        Vector2 look = mouse.delta.ReadValue() * lookSensitivity;
        transform.Rotate(0f, look.x, 0f);
        pitch = Mathf.Clamp(pitch - look.y, -85f, 85f);
        cameraRoot.localRotation = Quaternion.Euler(pitch, 0f, 0f);

        float x = (kb.dKey.isPressed ? 1f : 0f) - (kb.aKey.isPressed ? 1f : 0f);
        float z = (kb.wKey.isPressed ? 1f : 0f) - (kb.sKey.isPressed ? 1f : 0f);
        float speed = kb.leftShiftKey.isPressed ? sprintSpeed : walkSpeed;
        Vector3 move = Vector3.ClampMagnitude(transform.right * x + transform.forward * z, 1f) * speed;

        if (cc.isGrounded && verticalVelocity < 0f) verticalVelocity = -2f;
        if (cc.isGrounded && kb.spaceKey.wasPressedThisFrame) verticalVelocity = Mathf.Sqrt(jumpHeight * -2f * gravity);
        verticalVelocity += gravity * Time.deltaTime;
        cc.Move((move + Vector3.up * verticalVelocity) * Time.deltaTime);

        if (kb.eKey.wasPressedThisFrame && Time.frameCount != blockInteractFrame) TryHijack();
    }

    void TryHijack()
    {
        var ray = new Ray(playerCamera.transform.position, playerCamera.transform.forward);
        if (!Physics.Raycast(ray, out var hit, interactRange)) return;
        var vehicle = hit.collider.GetComponentInParent<FlyingVehicle>();
        if (vehicle != null && !vehicle.IsOccupied) vehicle.Enter(this);
    }

    public void AttachCamera(Camera cam)
    {
        cam.transform.SetParent(cameraRoot, false);
        cam.transform.localPosition = Vector3.zero;
        cam.transform.localRotation = Quaternion.identity;
        pitch = 0f;
        cameraRoot.localRotation = Quaternion.identity;
    }

    public void BlockInteractThisFrame() => blockInteractFrame = Time.frameCount;
}
