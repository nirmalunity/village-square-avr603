using UnityEngine;

// Attach to an empty GameObject. Assign prefabs in Inspector, then click
// the checkbox "Scatter Now" in Play mode, or call Scatter() from code.
// In Editor: right-click the component header → "Scatter Objects" (not available
// without editor script), so just hit Play with Scatter On Start checked.

public class ScatterObjects : MonoBehaviour
{
    [Header("Prefabs to scatter")]
    [SerializeField] GameObject[] prefabs;

    [Header("Scatter Settings")]
    [SerializeField] int count = 20;
    [SerializeField] float areaSize = 22f;       // half-extent of the field
    [SerializeField] float edgeOnly = true ? 1f : 0f; // keep to outer ring
    [SerializeField] float innerClear = 10f;     // clear zone in center (village square)
    [SerializeField] float minScale = 0.8f;
    [SerializeField] float maxScale = 1.4f;
    [SerializeField] bool scatterOnStart = true;

    void Start()
    {
        if (scatterOnStart) Scatter();
    }

    [ContextMenu("Scatter Objects")]
    public void Scatter()
    {
        if (prefabs == null || prefabs.Length == 0) return;

        for (int i = 0; i < count; i++)
        {
            Vector2 pos2D = RandomEdgePoint();
            Vector3 pos = new Vector3(pos2D.x, 0f, pos2D.y);

            GameObject prefab = prefabs[Random.Range(0, prefabs.Length)];
            GameObject obj = Instantiate(prefab, pos, Quaternion.Euler(0, Random.Range(0f, 360f), 0), transform);
            float s = Random.Range(minScale, maxScale);
            obj.transform.localScale = Vector3.one * s;
        }
    }

    Vector2 RandomEdgePoint()
    {
        // Scatter within areaSize but outside innerClear radius
        for (int attempt = 0; attempt < 100; attempt++)
        {
            float x = Random.Range(-areaSize, areaSize);
            float z = Random.Range(-areaSize, areaSize);
            float dist = Mathf.Sqrt(x * x + z * z);
            if (dist > innerClear && dist < areaSize)
                return new Vector2(x, z);
        }
        return new Vector2(areaSize - 1f, areaSize - 1f);
    }
}
