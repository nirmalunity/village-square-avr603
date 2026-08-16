using System.Collections;
using UnityEngine;
using UnityEngine.AI;

public class ZombieWaveSpawner : MonoBehaviour
{
    public static ZombieWaveSpawner Instance { get; private set; }

    [Header("Prefab")]
    [Tooltip("Drag your zombie prefab here")]
    [SerializeField] GameObject zombiePrefab;

    [Header("Wave Settings")]
    [SerializeField] int   firstWaveCount   = 4;   // zombies in first wave
    [SerializeField] int   waveIncrement    = 2;   // extra zombies added each wave
    [SerializeField] int   maxWaveCount     = 14;  // cap per wave
    [SerializeField] float timeBetweenWaves = 30f; // seconds between waves
    [SerializeField] float firstWaveDelay   = 5f;  // seconds before wave 1 spawns
    [SerializeField] float spawnDelay       = 0.5f;// seconds between each zombie in a wave
    [Tooltip("Total waves before spawning stops. 0 = infinite (used in training mode).")]
    [SerializeField] int   maxWaves         = 4;   // real game: finite waves so win is possible
    [Tooltip("Hard cap on simultaneous zombies in the scene. Spawner waits until below this before adding more.")]
    [SerializeField] int   maxZombiesInScene = 12;
    [Tooltip("When enabled, remove hand-placed scene zombies so this component is the only spawn source")]
    [SerializeField] bool  soleZombieSource = true;

    [Header("Spawn Positioning")]
    [SerializeField] float spawnEdgeInset = 2f;    // how far inside the ground boundary to spawn
    [SerializeField] float navMeshSnap    = 6f;    // search radius for valid NavMesh point

    // ── Runtime ───────────────────────────────────────────────────────────────
    int  waveNumber = 0;
    bool spawning   = false;

    // ─────────────────────────────────────────────────────────────────────────
    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    /// Called by GameManager.StartGame() — safe to call from anywhere.
    public void BeginSpawning()
    {
        if (spawning) return;

        if (zombiePrefab == null)
        {
            Debug.LogWarning("[WaveSpawner] zombiePrefab is not assigned! " +
                             "Drag a zombie prefab onto the ZombieWaveSpawner component.");
            return;
        }

        spawning = true;
        StartCoroutine(WaveLoop());
    }

    /// Called once by GameManager before it counts zombies or starts the waves.
    /// This prevents old perimeter-placed NPCs from bypassing the wave system.
    public void PrepareForGame()
    {
        if (!soleZombieSource) return;

        foreach (var zombie in FindObjectsByType<NPCNavMesh>(FindObjectsSortMode.None))
        {
            if (zombie == null) continue;
            ZombieSwarmManager.Instance?.UnregisterZombie(zombie);
            zombie.gameObject.SetActive(false); // exclude immediately from scene counts
            Destroy(zombie.gameObject);
        }
    }

    /// Called by GameManager in training mode when all zombies are cleared.
    /// Resets wave state and immediately starts a fresh wave loop.
    public void RestartSpawning()
    {
        StopAllCoroutines();
        spawning    = false;
        waveNumber  = 0;
        BeginSpawning();
    }

    // ─────────────────────────────────────────────────────────────────────────

    IEnumerator WaveLoop()
    {
        // Short pause before the first wave so the player has time to react
        yield return new WaitForSeconds(firstWaveDelay);

        bool infinite = (maxWaves <= 0) || (GameManager.Instance != null && GameManager.Instance.trainingMode);

        while (GameManager.Instance?.CurrentState == GameManager.State.Playing)
        {
            // Stop spawning after maxWaves (unless infinite / training mode)
            if (!infinite && waveNumber >= maxWaves) yield break;

            waveNumber++;
            int count = Mathf.Min(firstWaveCount + (waveNumber - 1) * waveIncrement, maxWaveCount);

            Debug.Log($"[WaveSpawner] Wave {waveNumber}/{(infinite ? "∞" : maxWaves.ToString())} — spawning {count} zombies");
            yield return StartCoroutine(SpawnWave(count));

            // Wait for next wave, but stop if game ends early
            float waited = 0f;
            while (waited < timeBetweenWaves)
            {
                yield return null;
                if (GameManager.Instance?.CurrentState != GameManager.State.Playing) yield break;
                waited += Time.deltaTime;
            }
        }
    }

    IEnumerator SpawnWave(int count)
    {
        float halfExtent = GetGroundHalfExtent() - spawnEdgeInset;

        for (int i = 0; i < count; i++)
        {
            if (GameManager.Instance?.CurrentState != GameManager.State.Playing) yield break;

            // Wait until there's room under the cap
            while (FindObjectsByType<NPCNavMesh>(FindObjectsSortMode.None).Length >= maxZombiesInScene)
            {
                yield return new WaitForSeconds(1f);
                if (GameManager.Instance?.CurrentState != GameManager.State.Playing) yield break;
            }

            // Try up to 6 candidate positions and pick the first valid NavMesh one
            for (int attempt = 0; attempt < 6; attempt++)
            {
                Vector3 candidate = GetEdgePosition(halfExtent);
                if (NavMesh.SamplePosition(candidate, out NavMeshHit hit, navMeshSnap, NavMesh.AllAreas))
                {
                    SpawnZombie(hit.position);
                    break;
                }
            }

            yield return new WaitForSeconds(spawnDelay);
        }
    }

    void SpawnZombie(Vector3 pos)
    {
        var go     = Instantiate(zombiePrefab, pos, Quaternion.Euler(0f, Random.Range(0f, 360f), 0f));
        var zombie = go.GetComponent<NPCNavMesh>();
        if (zombie != null)
            ZombieSwarmManager.Instance?.RegisterZombie(zombie);
    }

    /// Returns a random point along the outer edge of the play area.
    Vector3 GetEdgePosition(float halfExtent)
    {
        // Choose one of the 4 edges
        int   edge  = Random.Range(0, 4);
        float along = Random.Range(-halfExtent, halfExtent);

        if (edge == 0) return new Vector3(along,      0f,  halfExtent);  // North edge
        if (edge == 1) return new Vector3(along,      0f, -halfExtent);  // South edge
        if (edge == 2) return new Vector3( halfExtent, 0f, along);       // East edge
                       return new Vector3(-halfExtent, 0f, along);       // West edge
    }

    float GetGroundHalfExtent()
    {
        var ground = GameObject.Find("Ground");
        return ground != null ? ground.transform.localScale.x * 5f : 30f;
    }
}
