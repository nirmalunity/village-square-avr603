using UnityEngine;

public class CameraFollow : MonoBehaviour
{
    [SerializeField] Transform target;
    [SerializeField] float height            = 2f;
    [SerializeField] float distance          = 5f;
    [SerializeField] float lookAtHeightOffset = 1f;
    [SerializeField] float horizontalOffset  = 0f; // negative = shift left, positive = shift right

    void LateUpdate()
    {
        if (target == null) return;

        Vector3 desiredPosition = target.position
            - target.forward * distance
            + Vector3.up * height
            + target.right * horizontalOffset;
        transform.position = desiredPosition;
        transform.LookAt(target.position + Vector3.up * lookAtHeightOffset);
    }
}
