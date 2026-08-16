using UnityEngine;

/// A safe zone that villagers flee toward when zombies are near.
/// Place this in the scene using Tools → Setup Game Systems.
/// VillagerFlock automatically finds this via SafeZone.Instance.
public class SafeZone : MonoBehaviour
{
    public static SafeZone Instance { get; private set; }

    [Tooltip("Villagers within this radius are considered 'safe'")]
    public float radius = 10f;

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    void OnDrawGizmos()
    {
        // Always visible — green circle shows safe zone extent
        Gizmos.color = new Color(0f, 1f, 0f, 0.25f);
        Gizmos.DrawSphere(transform.position, radius);
        Gizmos.color = new Color(0f, 1f, 0f, 0.8f);
        Gizmos.DrawWireSphere(transform.position, radius);

        // Cross marker at centre
        float s = 2f;
        Gizmos.DrawLine(transform.position - Vector3.right * s, transform.position + Vector3.right * s);
        Gizmos.DrawLine(transform.position - Vector3.forward * s, transform.position + Vector3.forward * s);
    }
}
