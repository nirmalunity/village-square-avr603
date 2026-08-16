using UnityEngine;

public class FlockManager : MonoBehaviour
{
    public static FlockManager instance;

    [Header("Fish Prefab & Count")]
    public GameObject fishPrefab;
    public int        numFish = 20;

    [Header("Spawn Area")]
    public Vector3 swimLimits = new Vector3(10, 5, 10);

    [Header("Fish Size")]
    [Range(0.5f, 5f)] public float minScale = 1.5f;
    [Range(0.5f, 5f)] public float maxScale = 3.5f;

    [Header("Flock Settings")]
    [Range(1f, 10f)]  public float minSpeed      = 1f;
    [Range(1f, 10f)]  public float maxSpeed      = 6f;
    [Range(1f, 10f)]  public float neighbourDist = 3f;
    [Range(0.1f, 5f)] public float rotationSpeed = 4f;

    [Header("Goal Point")]
    [Range(5f, 30f)]  public float goalMoveInterval = 8f;   // seconds before goal relocates
    [Range(1f, 5f)]   public float goalReachedDist  = 3f;   // how close counts as "reached"

    [HideInInspector] public GameObject[] allFish;
    [HideInInspector] public Vector3      goalPosition;

    float timer;

    void Awake()
    {
        instance = this;
    }

    void Start()
    {
        allFish = new GameObject[numFish];

        for (int i = 0; i < numFish; i++)
        {
            Vector3 spawnPos = transform.position + new Vector3(
                Random.Range(-swimLimits.x, swimLimits.x),
                Random.Range(-swimLimits.y, swimLimits.y),
                Random.Range(-swimLimits.z, swimLimits.z)
            );

            allFish[i] = Instantiate(fishPrefab, spawnPos, Quaternion.identity);
            float scale = Random.Range(minScale, maxScale);
            allFish[i].transform.localScale = Vector3.one * scale;
        }

        SetNewGoal();
    }

    void Update()
    {
        timer += Time.deltaTime;

        // Move goal after interval, or if all fish are close enough
        if (timer >= goalMoveInterval || AllFishReachedGoal())
        {
            SetNewGoal();
            timer = 0f;
        }
    }

    void SetNewGoal()
    {
        goalPosition = transform.position + new Vector3(
            Random.Range(-swimLimits.x, swimLimits.x),
            Random.Range(-swimLimits.y, swimLimits.y),
            Random.Range(-swimLimits.z, swimLimits.z)
        );
    }

    bool AllFishReachedGoal()
    {
        foreach (var fish in allFish)
        {
            if (fish == null) continue;
            if (Vector3.Distance(fish.transform.position, goalPosition) > goalReachedDist)
                return false;
        }
        return true;
    }
}
