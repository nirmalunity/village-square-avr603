using UnityEngine;

public class CrowdManager : MonoBehaviour
{
    public static CrowdManager instance;

    [Header("NPC Prefab & Count")]
    public GameObject npcPrefab;
    public int        numNPCs = 10;

    [Header("Spawn Area")]
    public float spawnRadius = 15f;

    [Header("Wander Settings")]
    [Range(1f, 8f)]  public float minSpeed      = 2f;
    [Range(1f, 8f)]  public float maxSpeed      = 5f;
    [Range(5f, 30f)] public float wanderRadius  = 20f;
    [Range(2f, 15f)] public float waypointWait  = 3f;   // how long to pause at destination

    [HideInInspector] public GameObject[] allNPCs;

    void Awake()
    {
        instance = this;
    }

    void Start()
    {
        allNPCs = new GameObject[numNPCs];

        for (int i = 0; i < numNPCs; i++)
        {
            // Random position within spawn radius
            Vector2 circle = Random.insideUnitCircle * spawnRadius;
            Vector3 spawnPos = transform.position + new Vector3(circle.x, 0f, circle.y);

            allNPCs[i] = Instantiate(npcPrefab, spawnPos, Quaternion.Euler(0, Random.Range(0f, 360f), 0));
        }
    }
}
