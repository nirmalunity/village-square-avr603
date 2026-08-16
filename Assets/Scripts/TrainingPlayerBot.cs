using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using Unity.MLAgents;

[RequireComponent(typeof(PlayerController))]
public class TrainingPlayerBot : MonoBehaviour
{
    [Header("Movement")]
    [SerializeField] float dirUpdateInterval = 2.5f;  // seconds between direction recalculations
    [SerializeField] float maxJitter         = 50f;   // random angle offset when picking direction

    [Header("Combat")]
    [SerializeField] float attackRange    = 2.2f;   // strike distance
    [SerializeField] float attackInterval = 1.5f;   // seconds between swings
    [SerializeField] float hitChance      = 0.70f;  // current skill — varies during training
    [Tooltip("Fraction of hero run speed used while circling a combat target")]
    [Range(0.1f, 1f)]
    [SerializeField] float attackStrafeSpeed = 0.55f;
    [SerializeField] float strafeSwitchInterval = 2f;

    [Header("Skill Variation (training only)")]
    [Tooltip("Cycle player skill so the model learns to handle BOTH good and bad players")]
    [SerializeField] bool  varySkill        = true;
    [SerializeField] float skillSwitchTime  = 25f;   // seconds before switching skill level
    [SerializeField] float badPlayerChance  = 0.25f; // 'bad player' hit rate
    [SerializeField] float goodPlayerChance = 0.90f; // 'good player' hit rate
    float skillTimer;

    [Header("Retreat — damage driven, not proximity driven")]
    [Tooltip("Retreat once this much HP is lost inside hpWindow seconds")]
    [SerializeField] int   hpLossTrigger    = 12;
    [SerializeField] float hpWindow         = 5f;    // rolling window for damage tracking
    [Tooltip("Always retreat below this HP fraction")]
    [SerializeField] float criticalHpRatio  = 0.35f;
    [Tooltip("Stop retreating once this few zombies remain within safeRadius")]
    [SerializeField] int   safeZombieCount  = 1;
    [SerializeField] float safeRadius       = 5f;
    [SerializeField] float maxRetreatTime   = 6f;    // hard cap so bot never retreats forever
    [Tooltip("Retreat when this many zombies are pressed up against you, even at full HP")]
    [SerializeField] int   swarmRetreatCount = 4;
    [Tooltip("Must land this many hits before retreating is allowed (unless critically low HP)")]
    [SerializeField] int   minHitsBeforeRetreat = 3;
    [Tooltip("Minimum seconds committed to a fight before retreat is allowed")]
    [SerializeField] float minFightTime         = 3f;

    [Header("Training-only stuck rescue")]
    [Tooltip("Teleport to a nearby clear point after this many seconds with almost no movement")]
    [SerializeField] float stuckRescueTime = 4f;
    [SerializeField] float rescueDistance  = 8f;

    // ── Runtime ───────────────────────────────────────────────────────────────
    PlayerController pc;
    Vector3          currentDir;
    float            dirTimer;
    float            attackTimer;
    float            retreatTimer;
    bool             retreating;

    // Damage tracking
    int   hpAtWindowStart;
    float hpWindowTimer;

    // Stuck detection
    Vector3 snapshotPos;
    float   snapshotTimer;
    float   stuckDuration;
    float   boundarySize = 35f;
    CharacterController heroController;

    // Combat movement
    float strafeTimer;
    float strafeSign = 1f;

    readonly HashSet<NPCNavMesh> uniqueNearby = new HashSet<NPCNavMesh>();

    // Fight commitment — stops the bot from fleeing before it accomplishes anything
    int   hitsSinceRetreat;
    float fightTimer;

    // Target focus — keep hitting the SAME zombie so hitPressure actually builds.
    // Spreading damage across the swarm leaves every zombie at low pressure and
    // the ML model never sees the high-pressure states it needs to learn from.
    NPCNavMesh focusTarget;

    /// Returns the zombie to attack, sticking with the current one while it is
    /// alive and still in reach.
    NPCNavMesh GetFocusTarget()
    {
        if (focusTarget != null && !focusTarget.IsDead)
        {
            float d = Vector3.Distance(transform.position, focusTarget.transform.position);
            if (d <= attackRange * 2.5f) return focusTarget;   // stay locked on
        }
        focusTarget = FindNearest();   // dead, gone, or too far — pick a new one
        return focusTarget;
    }

    // ─────────────────────────────────────────────────────────────────────────
    void Start()
    {
        pc = GetComponent<PlayerController>();
        currentDir = transform.forward;
        hpAtWindowStart = pc.Health;
        snapshotPos     = transform.position;
        heroController  = GetComponent<CharacterController>();
        strafeSign      = Random.value < 0.5f ? -1f : 1f;

        var ground = GameObject.Find("Ground");
        if (ground != null) boundarySize = ground.transform.localScale.x * 5f - 2f;
    }

    /// Detects running-on-the-spot and steers toward genuinely open space.
    /// Returns true if an escape was applied this frame.
    bool ResolveStuck()
    {
        snapshotTimer += Time.deltaTime;
        if (snapshotTimer < 0.7f) return false;

        float sampleTime = snapshotTimer;
        float moved = Vector3.Distance(transform.position, snapshotPos);
        snapshotPos   = transform.position;
        snapshotTimer = 0f;

        if (moved > 0.25f)
        {
            stuckDuration = 0f;
            return false;   // moving fine
        }

        stuckDuration += sampleTime;

        if (stuckDuration >= stuckRescueTime)
        {
            RescueFromTrap();
            return true;
        }

        // Sweep every direction, pick the most open one that isn't into a wall/corner
        Vector3 escape = MovementUtil.FindClearDirection(transform.position, Vector3.zero, 6f, boundarySize);
        currentDir = escape;
        dirTimer   = 0f;
        pc.BotMove(escape);
        Debug.Log($"[TrainingBot] Stuck for {stuckDuration:F1}s — escaping toward open space");
        return true;
    }

    void RescueFromTrap()
    {
        Vector3 rescue = FindRescuePosition();
        if (heroController != null) heroController.enabled = false;
        transform.position = rescue;
        if (heroController != null) heroController.enabled = true;

        snapshotPos     = transform.position;
        snapshotTimer   = 0f;
        stuckDuration   = 0f;
        retreatTimer    = 0f;
        currentDir      = MovementUtil.FindClearDirection(
            transform.position, Vector3.zero, 6f, boundarySize);

        Academy.Instance.StatsRecorder.Add(
            "TrainingBot/StuckRescues", 1f, StatAggregationMethod.Sum);
        Debug.Log($"[TrainingBot] Rescue reposition to {transform.position}");
    }

    Vector3 FindRescuePosition()
    {
        NPCNavMesh[] zombies = FindObjectsByType<NPCNavMesh>(FindObjectsSortMode.None);
        Vector3 best = transform.position;
        float bestScore = float.MinValue;

        for (int i = 0; i < 16; i++)
        {
            float angle = i * 22.5f;
            Vector3 dir = Quaternion.Euler(0f, angle, 0f) * Vector3.forward;
            Vector3 candidate = transform.position + dir * rescueDistance;
            candidate.x = Mathf.Clamp(candidate.x, -boundarySize, boundarySize);
            candidate.z = Mathf.Clamp(candidate.z, -boundarySize, boundarySize);

            if (!NavMesh.SamplePosition(candidate, out NavMeshHit hit, 3f, NavMesh.AllAreas))
                continue;

            float nearestZombie = rescueDistance * 2f;
            foreach (var zombie in zombies)
            {
                if (zombie == null || zombie.IsDead) continue;
                nearestZombie = Mathf.Min(nearestZombie,
                    Vector3.Distance(hit.position, zombie.transform.position));
            }

            if (nearestZombie > bestScore)
            {
                bestScore = nearestZombie;
                best = new Vector3(hit.position.x, transform.position.y, hit.position.z);
            }
        }

        if (bestScore == float.MinValue)
        {
            Vector3 clear = MovementUtil.FindClearDirection(
                transform.position, Vector3.zero, rescueDistance, boundarySize);
            best = transform.position + clear * rescueDistance;
            best.x = Mathf.Clamp(best.x, -boundarySize, boundarySize);
            best.z = Mathf.Clamp(best.z, -boundarySize, boundarySize);
        }
        return best;
    }

    void Update()
    {
        if (GameManager.Instance == null || !GameManager.Instance.trainingMode) return;
        if (GameManager.Instance.CurrentState != GameManager.State.Playing) return;

        pc.BotControlled = true;

        // ── Skill variation — alternate between good and bad player ───────────
        // Critical: the model must experience BOTH to learn a conditional policy.
        if (varySkill)
        {
            skillTimer += Time.deltaTime;
            if (skillTimer >= skillSwitchTime)
            {
                skillTimer = 0f;
                // Randomly pick a skill level, biased toward the extremes
                float r = Random.value;
                if (r < 0.40f)      hitChance = badPlayerChance;      // bad player
                else if (r < 0.80f) hitChance = goodPlayerChance;     // good player
                else                hitChance = Random.Range(0.4f, 0.7f); // average
                Debug.Log($"[TrainingBot] Skill switched — hitChance = {hitChance:F2}");
            }
        }

        // ── Stuck check runs first — nothing else matters if we can't move ────
        if (ResolveStuck()) return;

        // ── Damage tracking — rolling HP-loss window ──────────────────────────
        hpWindowTimer += Time.deltaTime;
        int hpLost = hpAtWindowStart - pc.Health;
        if (hpWindowTimer >= hpWindow)
        {
            hpAtWindowStart = pc.Health;
            hpWindowTimer   = 0f;
            hpLost          = 0;
        }

        float hpRatio = pc.MaxHealth > 0 ? (float)pc.Health / pc.MaxHealth : 1f;

        // ── Retreat phase — leave only when the area is actually clearer ──────
        if (retreating)
        {
            retreatTimer += Time.deltaTime;

            bool areaClear = CountWithin(safeRadius) <= safeZombieCount;
            bool timedOut  = retreatTimer >= maxRetreatTime;

            if (areaClear)
            {
                retreating       = false;
                retreatTimer     = 0f;
                hpAtWindowStart  = pc.Health;   // reset window so we don't instantly re-trigger
                hpWindowTimer    = 0f;
                hitsSinceRetreat = 0;           // must earn the next retreat too
                fightTimer       = 0f;
                Debug.Log("[TrainingBot] Re-engaging — area clear");
            }
            else
            {
                if (timedOut)
                {
                    // Never re-enter combat in the same pile-up. Reposition once,
                    // then remain in retreat until the unique nearby count is safe.
                    RescueFromTrap();
                }

                // Fighting retreat — keep swinging at the focus target
                NPCNavMesh adjacent = GetFocusTarget();
                if (adjacent != null &&
                    Vector3.Distance(transform.position, adjacent.transform.position) <= attackRange)
                {
                    attackTimer += Time.deltaTime;
                    if (attackTimer >= attackInterval)
                    {
                        attackTimer = 0f;
                        if (Random.value <= hitChance)
                        {
                            adjacent.TakeHit();
                            hitsSinceRetreat++;
                            AudioManager.Instance?.PlayHeroAttack();
                            Debug.Log($"[TrainingBot] HIT while retreating");
                        }
                    }
                }

                // Back away toward genuinely open space
                Vector3 away = AwayFromCluster();
                pc.BotMove(away, false);
                if (away.sqrMagnitude > 0.01f)
                    transform.rotation = Quaternion.Slerp(transform.rotation,
                        Quaternion.LookRotation(away), 8f * Time.deltaTime);
                return;
            }
        }

        // ── Decide to retreat — but only after actually fighting ──────────────
        fightTimer += Time.deltaTime;

        int  swarming    = CountWithin(attackRange * 1.6f);
        Academy.Instance.StatsRecorder.Add("TrainingBot/NearbyZombies", swarming);
        bool wantRetreat = hpLost >= hpLossTrigger || swarming >= swarmRetreatCount;
        bool emergency   = hpRatio <= criticalHpRatio;   // overrides commitment

        // Must land a few hits and stand ground a while before fleeing is allowed
        bool hasEarnedRetreat = hitsSinceRetreat >= minHitsBeforeRetreat
                             && fightTimer       >= minFightTime;

        if (emergency || (wantRetreat && hasEarnedRetreat))
        {
            retreating   = true;
            retreatTimer = 0f;
            Debug.Log($"[TrainingBot] Retreating — {hitsSinceRetreat} hits landed, " +
                      $"lost {hpLost} HP, at {hpRatio:P0}, {swarming} zombies on me");
            return;
        }

        // ── Attack if zombie is in range ──────────────────────────────────────
        NPCNavMesh nearest = GetFocusTarget();   // stay on one target so pressure builds
        if (nearest != null)
        {
            float dist = Vector3.Distance(transform.position, nearest.transform.position);

            if (dist <= attackRange)
            {
                // Face the zombie
                Vector3 faceDir = nearest.transform.position - transform.position;
                faceDir.y = 0f;
                transform.rotation = Quaternion.Slerp(transform.rotation,
                    Quaternion.LookRotation(faceDir.normalized), 10f * Time.deltaTime);

                // Circle the target while swinging. Standing still lets the horde
                // form an artificial permanent pile-up and biases PPO toward Chase.
                strafeTimer += Time.deltaTime;
                if (strafeTimer >= strafeSwitchInterval)
                {
                    strafeTimer = 0f;
                    if (Random.value < 0.65f) strafeSign *= -1f;
                }

                Vector3 tangent = Vector3.Cross(Vector3.up, faceDir.normalized) * strafeSign;
                Vector3 outward = -faceDir.normalized * 0.2f;
                Vector3 strafe = MovementUtil.FindClearDirection(
                    transform.position, tangent + outward, 3f, boundarySize);
                pc.BotMove(strafe, false, attackStrafeSpeed);
                attackTimer += Time.deltaTime;
                if (attackTimer >= attackInterval)
                {
                    attackTimer = 0f;
                    if (Random.value <= hitChance)
                    {
                        nearest.TakeHit();
                        hitsSinceRetreat++;
                        AudioManager.Instance?.PlayHeroAttack();
                        Debug.Log($"[TrainingBot] HIT {nearest.name}  ({hitsSinceRetreat} this fight)");
                    }
                }
                return;
            }
        }


        // ── Move toward nearest zombie ────────────────────────────────────────
        dirTimer += Time.deltaTime;
        if (dirTimer >= dirUpdateInterval || nearest == null)
        {
            dirTimer = 0f;
            if (nearest != null)
            {
                // Direction toward zombie + random angle jitter
                Vector3 toZombie = (nearest.transform.position - transform.position);
                toZombie.y = 0f;
                float jitter = Random.Range(-maxJitter, maxJitter);
                currentDir = Quaternion.Euler(0, jitter, 0) * toZombie.normalized;
            }
            else
            {
                // No zombies yet — wander randomly
                float angle = Random.Range(0f, 360f);
                currentDir = new Vector3(Mathf.Sin(angle * Mathf.Deg2Rad), 0f,
                                         Mathf.Cos(angle * Mathf.Deg2Rad));
            }
        }

        pc.BotMove(currentDir);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    NPCNavMesh FindNearest()
    {
        NPCNavMesh best     = null;
        float      bestDist = float.MaxValue;
        foreach (var z in FindObjectsByType<NPCNavMesh>(FindObjectsSortMode.None))
        {
            if (z == null || z.IsDead) continue;   // skip corpses — they linger 3s before destroy
            float d = Vector3.Distance(transform.position, z.transform.position);
            if (d < bestDist) { bestDist = d; best = z; }
        }
        return best;
    }

    /// Direction away from the zombie cluster that is also OPEN and not into a
    /// corner — sweeps all directions rather than blindly running "away".
    Vector3 AwayFromCluster()
    {
        Vector3 centre = Vector3.zero;
        uniqueNearby.Clear();
        foreach (var c in Physics.OverlapSphere(transform.position, safeRadius * 2f))
        {
            var z = c.GetComponent<NPCNavMesh>() ?? c.GetComponentInParent<NPCNavMesh>();
            if (z != null && !z.IsDead) uniqueNearby.Add(z);
        }
        if (uniqueNearby.Count == 0) return transform.forward;

        foreach (var zombie in uniqueNearby) centre += zombie.transform.position;
        centre /= uniqueNearby.Count;
        return MovementUtil.FleeDirection(transform.position, centre, 6f, boundarySize);
    }

    int CountWithin(float range)
    {
        uniqueNearby.Clear();
        foreach (var c in Physics.OverlapSphere(transform.position, range))
        {
            var z = c.GetComponent<NPCNavMesh>() ?? c.GetComponentInParent<NPCNavMesh>();
            if (z != null && !z.IsDead) uniqueNearby.Add(z);
        }
        return uniqueNearby.Count;
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(1f, 1f, 0f, 0.35f);
        Gizmos.DrawWireSphere(transform.position, attackRange);
        Gizmos.color = new Color(1f, 0f, 0f, 0.35f);
        Gizmos.DrawWireSphere(transform.position, safeRadius);   // retreat-exit check radius
    }
}
