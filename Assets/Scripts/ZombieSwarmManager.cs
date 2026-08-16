using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

public class ZombieSwarmManager : MonoBehaviour
{
    public static ZombieSwarmManager Instance { get; private set; }

    [Header("Alert Swarm")]
    [Tooltip("Zombies within this range of the spotting zombie get the alert")]
    [SerializeField] float alertRadius     = 25f;
    [Tooltip("Farthest zombie takes this many seconds to respond")]
    [SerializeField] float maxStaggerDelay = 5f;

    [Header("Encirclement")]
    [Tooltip("Each zombie positions this far from the predicted hero position")]
    [SerializeField] float encirclementRadius = 4f;

    [Header("Predictive Interception")]
    [Tooltip("Multiplier on time-to-reach for how far ahead to predict")]
    [SerializeField] float predictionScale  = 1.2f;
    [Tooltip("Maximum prediction offset distance so zombies don't overshoot")]
    [SerializeField] float maxPredictOffset = 7f;

    [Header("Standdown")]
    [Tooltip("Hero must be this far from every chaser before standdown starts")]
    [SerializeField] float calmRadius   = 32f;
    [Tooltip("Seconds hero must stay far away before zombies give up")]
    [SerializeField] float calmDuration = 4f;

    // ── Runtime state ─────────────────────────────────────────────────────────
    List<NPCNavMesh>                allZombies         = new List<NPCNavMesh>();
    HashSet<NPCNavMesh>             chasers            = new HashSet<NPCNavMesh>();
    Dictionary<NPCNavMesh, float>   encirclementAngles = new Dictionary<NPCNavMesh, float>();

    Transform hero;
    Vector3   heroVelocity;
    Vector3   lastHeroPos;

    bool  isAlerted   = false;
    float calmTimer   = 0f;
    int   slotCounter = 0;

    public bool IsAlerted => isAlerted;

    // ─────────────────────────────────────────────────────────────────────────
    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    void Start()
    {
        allZombies.AddRange(FindObjectsByType<NPCNavMesh>(FindObjectsSortMode.None));

        var heroObj = GameObject.FindWithTag("Player");
        if (heroObj != null) { hero = heroObj.transform; lastHeroPos = hero.position; }
        else Debug.LogWarning("[ZombieSwarmManager] No Player tag found.");

        Debug.Log("[ZombieSwarmManager] Ready — tracking " + allZombies.Count + " zombies.");
    }

    void FixedUpdate()
    {
        if (hero == null) return;

        // Track hero velocity for prediction
        heroVelocity  = (hero.position - lastHeroPos) / Time.fixedDeltaTime;
        heroVelocity.y = 0f;
        lastHeroPos   = hero.position;

        if (!isAlerted) return;

        // ── Standdown check ───────────────────────────────────────────────
        bool heroSafe = true;
        foreach (var z in chasers)
        {
            if (z == null) continue;
            if (Vector3.Distance(z.transform.position, hero.position) < calmRadius)
            {
                heroSafe = false;
                break;
            }
        }

        if (heroSafe || chasers.Count == 0)
        {
            calmTimer += Time.fixedDeltaTime;
            if (calmTimer >= calmDuration) Standdown();
        }
        else
        {
            calmTimer = 0f;
        }
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// Called by the first zombie that spots the player.
    /// Broadcasts staggered alert to all zombies within alertRadius.
    public void RaiseAlert(Vector3 alertOrigin)
    {
        if (isAlerted) return;      // already active — don't re-broadcast
        isAlerted   = true;
        calmTimer   = 0f;
        slotCounter = 0;
        chasers.Clear();
        encirclementAngles.Clear();

        int count = 0;
        foreach (var zombie in allZombies)
        {
            if (zombie == null) continue;
            float dist = Vector3.Distance(alertOrigin, zombie.transform.position);
            if (dist > alertRadius) continue;

            // Delay scales with distance — near zombies react fast, far ones slowly
            float delay = (dist / alertRadius) * maxStaggerDelay;
            zombie.ReceiveAlert(delay);
            count++;
        }

        Debug.Log("[Swarm] Alert raised — " + count + " zombies alerted.");
    }

    /// Called by a zombie when it actually starts chasing (after its delay).
    public void RegisterChaser(NPCNavMesh zombie)
    {
        if (chasers.Contains(zombie)) return;
        chasers.Add(zombie);

        // Golden-angle distribution: 137.5° between each slot
        // gives even spread for any number of zombies
        float angle = (slotCounter * 137.508f) % 360f;
        encirclementAngles[zombie] = angle;
        slotCounter++;

        Debug.Log("[Swarm] " + zombie.name + " joined chase — angle slot: " + angle.ToString("F0") + "°");
    }

    /// Called when a zombie leaves Chase state for any reason.
    public void UnregisterChaser(NPCNavMesh zombie)
    {
        chasers.Remove(zombie);
    }

    public void UnregisterZombie(NPCNavMesh zombie)
    {
        if (zombie == null) return;
        chasers.Remove(zombie);
        encirclementAngles.Remove(zombie);
        allZombies.Remove(zombie);
    }

    /// Register a zombie that was spawned at runtime so it participates in swarm alerts.
    /// If the swarm is already alerted, the new zombie gets an immediate alert with a
    /// short random delay so it doesn't teleport straight into the fight.
    public void RegisterZombie(NPCNavMesh zombie)
    {
        if (zombie == null || allZombies.Contains(zombie)) return;
        allZombies.Add(zombie);

        if (isAlerted)
            zombie.ReceiveAlert(Random.Range(0.5f, 2.5f));

        Debug.Log("[ZombieSwarmManager] Registered spawned zombie: " + zombie.name +
                  "  (total: " + allZombies.Count + ")");
    }

    /// Returns the NavMesh destination for a chasing zombie.
    /// Combines predictive interception + unique encirclement angle.
    public Vector3 GetChaseDest(NPCNavMesh zombie, float agentSpeed)
    {
        if (hero == null) return zombie.transform.position;

        // ── Predictive interception ───────────────────────────────────────
        float   dist        = Vector3.Distance(zombie.transform.position, hero.position);
        float   timeToReach = agentSpeed > 0.01f ? dist / agentSpeed : 1f;
        Vector3 predicted   = heroVelocity * timeToReach * predictionScale;

        if (predicted.magnitude > maxPredictOffset)
            predicted = predicted.normalized * maxPredictOffset;

        Vector3 predictedHeroPos = hero.position + predicted;

        // ── Encirclement spread ───────────────────────────────────────────
        float angle = encirclementAngles.ContainsKey(zombie) ? encirclementAngles[zombie] : 0f;
        Vector3 spread = Quaternion.Euler(0f, angle, 0f) * Vector3.forward * encirclementRadius;
        Vector3 target = predictedHeroPos + spread;

        // ── Snap to valid NavMesh point ───────────────────────────────────
        NavMeshHit hit;
        if (NavMesh.SamplePosition(target, out hit, encirclementRadius + 2f, NavMesh.AllAreas))
            return hit.position;

        return predictedHeroPos;
    }

    // ── Standdown ─────────────────────────────────────────────────────────────

    void Standdown()
    {
        isAlerted   = false;
        calmTimer   = 0f;
        slotCounter = 0;
        encirclementAngles.Clear();

        foreach (var zombie in allZombies)
            if (zombie != null) zombie.Standdown();

        chasers.Clear();
        Debug.Log("[Swarm] Hero escaped — all zombies standing down.");
    }

    // ── Gizmos ────────────────────────────────────────────────────────────────

    void OnDrawGizmosSelected()
    {
        if (!Application.isPlaying || hero == null) return;

        // Calm radius around hero — zombies give up when hero is inside this clear zone
        Gizmos.color = new Color(0f, 1f, 0f, 0.2f);
        Gizmos.DrawWireSphere(hero.position, calmRadius);
    }
}
