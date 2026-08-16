using System.Collections;
using UnityEngine;
using UnityEngine.AI;

public class NPCNavMesh : MonoBehaviour
{
    enum State { Patrol, Chase, Investigate, ChaseVillager, GroupUp, Hit, Dead }
    State currentState = State.Patrol;

    // ── Inspector ─────────────────────────────────────────────────────────────
    [Header("Patrol")]
    [SerializeField] float patrolRadius = 15f;
    [SerializeField] float patrolSpeed  = 2f;

    [Header("Chase")]
    [Tooltip("Keep below PlayerController.runSpeed (5) so the hero can break away " +
             "and force zombies to pick a different target")]
    [SerializeField] float chaseSpeed   = 3.3f;
    [Tooltip("Villagers flee at 3.5, so this must be faster or a retreating zombie can never catch one")]
    [SerializeField] float villagerChaseSpeed = 5.5f;
    [Tooltip("A flank becomes a real attack inside this distance instead of orbiting forever")]
    [SerializeField] float flankCloseDistance = 2.7f;

    [Header("FOV Perception")]
    [SerializeField] float fovRange     = 12f;
    [SerializeField] float fovAngle     = 120f;
    [SerializeField] float hearingRange = 4f;
    [SerializeField] float gracePeriod  = 2f;

    [Header("Health")]
    [SerializeField] int   maxHealth        = 5;
    [SerializeField] float hitStunTime      = 0.6f;
    [SerializeField] float deathRemoveDelay = 3f;

    [Header("Contact Damage — Hero")]
    [SerializeField] int   contactDamage     = 3;
    [SerializeField] float contactRange      = 1.6f;
    [SerializeField] float damageCooldown    = 4f;

    [Header("Villager Chasing")]
    [SerializeField] float villagerDetectRange    = 10f;
    [SerializeField] int   villagerDamage         = 2;
    [SerializeField] float villagerDamageCooldown = 3f;

    [Header("Utility AI")]
    [Tooltip("Randomised at spawn — 1 = fearless, 0 = cowardly")]
    [Range(0f, 1f)]
    [SerializeField] float personality       = 0.5f;
    [Tooltip("Random noise added to each utility score — increases unpredictability")]
    [SerializeField] float utilityNoise      = 0.22f;
    [Tooltip("Seconds between utility re-evaluations")]
    [SerializeField] float evalInterval      = 0.15f;
    [Tooltip("Minimum seconds a zombie stays in a state before utility can switch it")]
    [SerializeField] float minDwellTime      = 0.3f;
    [Tooltip("Once retreating, zombie commits for this many extra seconds before utility can return it")]
    [SerializeField] float retreatCommitTime = 6f;

    [Header("Pressure Signals")]
    [Tooltip("Each hit adds this much to hitPressure (1 / hitsPerFullPressure)")]
    [SerializeField] int   hitsPerFullPressure = 2;
    [Tooltip("hitPressure lost per second — lower = pressure lingers longer")]
    [SerializeField] float hitPressureDecay    = 0.04f;
    [Tooltip("Radius within which a dying zombie triggers fear in others")]
    [SerializeField] float deathAlertRadius    = 9f;
    [Tooltip("Radius within which a zombie TAKING A HIT scares its neighbours")]
    [SerializeField] float hitAlertRadius      = 6f;
    [Tooltip("Fear gained by neighbours when they witness an ally being hit")]
    [SerializeField] float witnessHitGain      = 0.35f;
    [Tooltip("How much nearbyDeathPressure rises per witnessed death")]
    [SerializeField] float deathPressureGain   = 0.55f;
    [Tooltip("nearbyDeathPressure lost per second")]
    [SerializeField] float deathPressureDecay  = 0.07f;

    [Header("ML Strategy Execution")]
    [Tooltip("Minimum time to execute Chase or Flank before accepting a different normal strategy")]
    [SerializeField] float mlCombatCommitTime  = 0.8f;
    [Tooltip("Minimum time spent escaping toward villagers")]
    [SerializeField] float mlRetreatCommitTime = 3.5f;
    [Tooltip("Minimum time spent moving toward allies")]
    [SerializeField] float mlGroupCommitTime   = 1.5f;
    [Tooltip("Threat at or above this value forces a scatter toward villagers")]
    [Range(0f, 1f)]
    [SerializeField] float scatterThreat = 0.45f;
    [Tooltip("Threat at or below this value forces a return to the hero after scatter is complete")]
    [Range(0f, 1f)]
    [SerializeField] float returnThreat = 0.15f;
    [Tooltip("Maximum time to require reaching and hitting a villager before allowing a return")]
    [SerializeField] float maxRetreatTravelTime = 20f;
    [Tooltip("Threat recovery speed while diverted. Slightly faster than combat, but long enough to reach a villager")]
    [SerializeField] float retreatRecoveryMultiplier = 1.25f;
    [SerializeField] float groupSearchRadius = 12f;

    [Header("Training Curriculum")]
    [Tooltip("Fraction of training zombies spawned into a recent-damage scenario")]
    [Range(0f, 1f)]
    [SerializeField] float trainingPressureScenarioChance = 0.50f;
    [SerializeField] Vector2 trainingPressureRange = new Vector2(0.65f, 0.95f);

    // ── Private ───────────────────────────────────────────────────────────────
    NavMeshAgent     agent;
    Animator         animator;
    Transform        player;
    PlayerController playerCtrl;

    float lastDamageTime         = -99f;
    float lastVillagerDamageTime = -99f;

    // Utility AI signals
    float hitPressure         = 0f;   // 0..1, rises on hit, decays
    float nearbyDeathPressure = 0f;   // 0..1, spikes on neighbour death, decays
    float evalTimer           = 0f;
    float dwellTimer          = 0f;
    bool  lastCanSee          = false;

    // Villager targeting
    VillagerBoid   targetVillager;
    Transform      groupTarget;
    VillagerBoid[] cachedVillagers      = new VillagerBoid[0];
    float          lastVillagerScan     = -99f;
    const float    VILLAGER_SCAN_INTERVAL = 2f;

    // General navigation
    Vector3 patrolTarget;
    Vector3 lastKnownPos;
    float   lostSightTimer;
    int     health;

    bool alertedBySwarm = false;
    bool hasRaisedAlert = false;

    // ── Q-Learning ────────────────────────────────────────────────────────────
    int   qlState    = 0;
    ZombieQLearner.Action qlAction = ZombieQLearner.Action.Chase;
    float qlTimer    = 0f;
    float flankAngle = 0f;   // degrees offset from direct chase — set by Flank action
    const float QL_INTERVAL = 1.0f;

    // ── ML-Agents ─────────────────────────────────────────────────────────────
    ZombieAgent mlAgent;   // cached on Start(); null if ZombieAgent is not on the prefab
    int pendingMLAction = -1;   // decision made during hitstun, applied on recovery
    int activeMLAction  = -1;
    float mlActionLockedUntil = -99f;
    float retreatStartedAt = -99f;
    bool  retreatHitVillager;
    bool  retreatTimeoutReported;

    // ── Public read-outs consumed by ZombieAgent.CollectObservations() ────────
    public float HpRatio    => maxHealth > 0 ? (float)health / maxHealth : 0f;
    public float HitPressure => hitPressure;
    public bool  IsAlerted   => alertedBySwarm;
    public bool  IsDead      => currentState == State.Dead;
    public int   CurrentMLAction => activeMLAction;
    public float ScatterThreat => scatterThreat;
    public float ReturnThreat => returnThreat;
    public bool  MustCompleteRetreat => activeMLAction == 2
        && !retreatHitVillager
        && Time.time - retreatStartedAt < maxRetreatTravelTime;
    public bool  RetreatTimedOut => activeMLAction == 2
        && !retreatHitVillager
        && (retreatTimeoutReported || Time.time - retreatStartedAt >= maxRetreatTravelTime);
    public bool  HasVillagerTarget => targetVillager != null;
    public int   VillagerTargetId => targetVillager != null ? targetVillager.GetInstanceID() : -1;
    public float DistanceToVillagerTarget => targetVillager != null
        ? Vector3.Distance(transform.position, targetVillager.transform.position)
        : 25f;

    /// Combined danger signal: damage taken by ME plus fear absorbed from allies
    /// being hit or killed nearby. This is what the ML model observes, so a
    /// zombie standing next to a friend getting battered will scatter too.
    public float ThreatLevel
    {
        get
        {
            float braveryScale = Mathf.Lerp(1.25f, 0.75f, personality);
            return Mathf.Clamp01((hitPressure + nearbyDeathPressure * 0.7f) * braveryScale);
        }
    }

    // Stuck detection
    Vector3 prevPosition;
    float   stuckTimer;

    const float EYE_HEIGHT = 1.4f;

    // ─────────────────────────────────────────────────────────────────────────
    void Start()
    {
        agent    = GetComponent<NavMeshAgent>();
        animator = GetComponentInChildren<Animator>();
        health   = maxHealth;

        // Each zombie gets a unique bravery — same prefab, different behaviour
        personality = Random.Range(0.15f, 0.95f);

        // Ensure PPO regularly sees the high-threat states needed to learn the
        // scatter branch. Real hits still update pressure normally; this only
        // randomises the initial condition of some training episodes.
        if (GameManager.Instance != null && GameManager.Instance.trainingMode
            && Random.value < trainingPressureScenarioChance)
        {
            hitPressure = Random.Range(trainingPressureRange.x, trainingPressureRange.y);
            int simulatedDamage = Mathf.Clamp(
                Mathf.RoundToInt(hitPressure * hitsPerFullPressure), 1, maxHealth - 1);
            health = Mathf.Max(1, maxHealth - simulatedDamage);
        }

        var playerObj = GameObject.FindWithTag("Player");
        if (playerObj != null)
        {
            player     = playerObj.transform;
            playerCtrl = playerObj.GetComponent<PlayerController>();
        }
        else Debug.LogWarning(name + ": No Player tag found.");

        prevPosition = transform.position;
        SetNewPatrolTarget();
        GameManager.Instance?.OnZombieSpawned();

        // Cache ML-Agents component (optional — only present when training/inference)
        mlAgent = GetComponent<ZombieAgent>();
    }

    void Update()
    {
        if (currentState == State.Dead || currentState == State.Hit) return;
        if (!agent.isOnNavMesh) return;
        if (player == null) { Patrol(); return; }

        if (activeMLAction == 2 && !retreatHitVillager && !retreatTimeoutReported
            && Time.time - retreatStartedAt >= maxRetreatTravelTime)
        {
            retreatTimeoutReported = true;
            mlAgent?.RecordRetreatTimeout();
        }

        lastCanSee = CanSeePlayer();

        switch (currentState)
        {
            case State.Patrol:
                Patrol();
                if (lastCanSee || alertedBySwarm)
                {
                    // Utility decides even at first contact — cowardly zombies with
                    // nearby death pressure may skip the fight and go for a villager
                    if (ShouldEngageHero())
                    {
                        EnterChase(lastCanSee);
                    }
                    else
                    {
                        var rv = FindRetreatVillager();
                        if (rv != null)
                        {
                            targetVillager = rv;
                            currentState   = State.ChaseVillager;
                            agent.speed    = villagerChaseSpeed;
                            dwellTimer     = -retreatCommitTime;
                        }
                        else EnterChase(lastCanSee); // no villager escape, must fight
                    }
                }
                else
                {
                    var v = FindNearestVillager();
                    if (v != null)
                    {
                        targetVillager = v;
                        currentState   = State.ChaseVillager;
                        agent.speed    = patrolSpeed * 1.3f;
                        dwellTimer     = 0f;
                    }
                }
                break;

            case State.Chase:
                Chase(lastCanSee);
                break;

            case State.ChaseVillager:
                ChaseVillager();
                break;

            case State.GroupUp:
                ExecuteGroupUp();
                break;

            case State.Investigate:
                Investigate();
                if (lastCanSee || alertedBySwarm) EnterChase(lastCanSee);
                break;
        }

        // ── Actual movement speed → animator ──────────────────────────────────
        Vector3 delta = transform.position - prevPosition;
        delta.y = 0f;
        float actualSpeed = delta.magnitude / Time.deltaTime;
        prevPosition = transform.position;
        if (animator != null) animator.SetFloat("Speed", actualSpeed);

        // ── Pressure decay ────────────────────────────────────────────────────
        // Threat is the memory of why this zombie scattered. Do not erase that
        // memory while it is still trying to reach its first villager; otherwise
        // the action mask sends it back to the hero before the retreat can pay off.
        bool waitingForFirstVillagerHit = activeMLAction == 2
            && !retreatHitVillager
            && !RetreatTimedOut;
        float recovery = waitingForFirstVillagerHit
            ? 0f
            : currentState == State.ChaseVillager
                ? Mathf.Max(1f, retreatRecoveryMultiplier)
                : 1f;
        hitPressure         = Mathf.Max(0f, hitPressure - hitPressureDecay * recovery * Time.deltaTime);
        nearbyDeathPressure = Mathf.Max(0f, nearbyDeathPressure - deathPressureDecay * recovery * Time.deltaTime);

        // ── Stuck detection (patrol / investigate only) ────────────────────────
        bool canGetStuck = currentState == State.Patrol || currentState == State.Investigate;
        if (canGetStuck && agent.desiredVelocity.magnitude > 0.3f && actualSpeed < 0.05f)
        {
            stuckTimer += Time.deltaTime;
            if (stuckTimer > 2f) { agent.ResetPath(); SetNewPatrolTarget(); stuckTimer = 0f; }
        }
        else stuckTimer = 0f;

        // ── Utility evaluation (disabled when ML-Agents is driving) ─────────────
        dwellTimer += Time.deltaTime;
        evalTimer  += Time.deltaTime;
        if (mlAgent == null && evalTimer >= evalInterval && dwellTimer >= minDwellTime)
        {
            // Only fight-or-flight during active combat states
            if (currentState == State.Chase || currentState == State.ChaseVillager)
                EvaluateUtility();
            evalTimer = 0f;
        }

        // ── Q-Learning strategy (disabled when ML-Agents is driving) ────────────
        if (mlAgent == null) QLUpdate();
    }

    // ── Utility AI ────────────────────────────────────────────────────────────

    /// Quick engagement check used at Patrol→Chase transition.
    /// Brave + healthy zombies always engage; cowardly ones weigh nearby deaths
    /// and may go for a villager instead of joining the fight.
    bool ShouldEngageHero()
    {
        float healthRatio = (float)health / maxHealth;
        float cowardice   = 1f - personality;
        bool  villager    = FindNearestVillager() != null;

        float uEngage = personality * healthRatio * (1f - hitPressure)
                      + Random.Range(-utilityNoise, utilityNoise);
        float uAvoid  = cowardice * Mathf.Clamp01(nearbyDeathPressure + hitPressure * 0.5f)
                      * (villager ? 1f : 0.1f)
                      + Random.Range(-utilityNoise, utilityNoise);
        return uEngage >= uAvoid;
    }

    /// Scores Chase vs Retreat vs Patrol using continuous arithmetic — no if/else.
    /// Noise + per-zombie personality ensure the same inputs produce different outputs.
    void EvaluateUtility()
    {
        float healthRatio    = (float)health / maxHealth;           // 0..1
        float cowardice      = 1f - personality;                    // 0..1
        bool  villagerNearby = FindNearestVillager() != null;

        // ── Chase score ───────────────────────────────────────────────────────
        // Brave + healthy + low pressure → wants to fight
        float uChase = personality * healthRatio * (1f - hitPressure)
                     + Random.Range(-utilityNoise, utilityNoise);

        // ── Retreat score ─────────────────────────────────────────────────────
        // Cowardly + under hit/death pressure + villager available → run away
        float combinedPressure = Mathf.Clamp01(hitPressure + nearbyDeathPressure * 0.65f);
        float uRetreat         = cowardice * combinedPressure * (villagerNearby ? 1f : 0.08f)
                               + Random.Range(-utilityNoise, utilityNoise);

        // ── Patrol score ──────────────────────────────────────────────────────
        // Low-utility fallback — cowardly, wounded zombie disengages
        float uPatrol = cowardice * (1f - healthRatio) * 0.35f
                      + Random.Range(-utilityNoise, utilityNoise);

        // ── Argmax — the only "decision" and it contains no if/else ──────────
        float[] scores  = { uChase, uRetreat, uPatrol };
        int     best    = System.Array.IndexOf(scores, Mathf.Max(scores));
        State[] targets = { State.Chase, State.ChaseVillager, State.Patrol };
        State   chosen  = targets[best];

        if (chosen == currentState) return;

        Debug.Log($"{name}  personality={personality:F2}  " +
                  $"Chase={uChase:F2}  Retreat={uRetreat:F2}  Patrol={uPatrol:F2}  → {chosen}");

        // ── Apply the chosen state ─────────────────────────────────────────────
        // (switch is for DISPATCH, not for decisions — the decision was the argmax above)
        switch (chosen)
        {
            case State.Chase:
                EnterChase(lastCanSee);
                break;

            case State.ChaseVillager:
                // Use a villager that is away from the player, not just the nearest one
                var v = FindRetreatVillager();
                if (v != null)
                {
                    targetVillager = v;
                    currentState   = State.ChaseVillager;
                    agent.speed    = villagerChaseSpeed;
                    // Negative dwell: zombie must stay in retreat for retreatCommitTime
                    // before utility is even allowed to evaluate switching back
                    dwellTimer     = -retreatCommitTime;
                }
                break;

            case State.Patrol:
                currentState = State.Patrol;
                agent.speed  = patrolSpeed;
                SetNewPatrolTarget();
                dwellTimer   = 0f;
                break;
        }
    }

    /// Spikes this zombie's death-fear pressure — called by dying neighbours.
    public void MortalityAlert()
    {
        nearbyDeathPressure = Mathf.Min(1f, nearbyDeathPressure + deathPressureGain);
        Debug.Log($"{name} mortality alert — deathPressure now {nearbyDeathPressure:F2}");
    }

    /// Smaller fear spike — called when a NEARBY ally takes a hit (not a death).
    /// Lets a group scatter as soon as the hero starts swinging, rather than
    /// waiting until someone actually dies.
    public void WitnessHitAlert(float gain)
    {
        nearbyDeathPressure = Mathf.Min(1f, nearbyDeathPressure + gain);
    }

    // ── Q-Learning ────────────────────────────────────────────────────────────

    void QLUpdate()
    {
        var ql = ZombieQLearner.Instance;
        if (ql == null) return;

        qlTimer += Time.deltaTime;
        if (qlTimer < QL_INTERVAL) return;
        qlTimer = 0f;

        float playerHp = (playerCtrl != null && playerCtrl.MaxHealth > 0)
                         ? (float)playerCtrl.Health / playerCtrl.MaxHealth : 1f;
        float dist     = player != null ? Vector3.Distance(transform.position, player.position) : 99f;

        int nextState = ql.EncodeState(playerHp, dist, hitPressure);

        // Small survival reward — staying alive is slightly good
        ql.Learn(qlState, qlAction, 0.02f, nextState);

        qlState  = nextState;
        qlAction = ql.SelectAction(qlState);
        ApplyQLAction(qlAction);
    }

    void ApplyQLAction(ZombieQLearner.Action action)
    {
        if (currentState == State.Dead || currentState == State.Hit) return;

        switch (action)
        {
            case ZombieQLearner.Action.Chase:
                flankAngle = 0f;
                if (currentState != State.Chase && (lastCanSee || alertedBySwarm))
                    EnterChase(lastCanSee);
                break;

            case ZombieQLearner.Action.Flank:
                // Pick a side only when flankAngle is currently 0 to avoid jitter
                if (Mathf.Abs(flankAngle) < 0.1f)
                    flankAngle = Random.value > 0.5f ? 75f : -75f;
                if (currentState != State.Chase && (lastCanSee || alertedBySwarm))
                    EnterChase(lastCanSee);
                break;

            case ZombieQLearner.Action.RetreatVillager:
                flankAngle = 0f;
                if (currentState != State.ChaseVillager)
                {
                    var v = FindNearestVillager();
                    if (v != null)
                    {
                        targetVillager = v;
                        currentState   = State.ChaseVillager;
                        agent.speed    = villagerChaseSpeed;
                        dwellTimer     = 0f;
                    }
                }
                break;

            case ZombieQLearner.Action.GroupUp:
                flankAngle = 0f;
                GroupWithNearbyZombies();
                break;
        }
    }

    /// Calculates chase destination, offsetting by flankAngle when flanking.
    Vector3 GetChaseTarget()
    {
        if (player == null) return transform.position;
        if (Mathf.Abs(flankAngle) < 0.1f) return player.position;
        if (Vector3.Distance(transform.position, player.position) <= flankCloseDistance)
            return player.position;

        Vector3 toZombie = (transform.position - player.position).normalized;
        if (toZombie.sqrMagnitude < 0.01f) toZombie = transform.forward;

        Vector3 flanked = Quaternion.Euler(0f, flankAngle, 0f) * toZombie;
        Vector3 target  = player.position + flanked * 3f;

        // Snap to NavMesh so the agent always gets a reachable destination
        if (UnityEngine.AI.NavMesh.SamplePosition(target, out UnityEngine.AI.NavMeshHit hit, 3f,
                                                   UnityEngine.AI.NavMesh.AllAreas))
            return hit.position;
        return player.position;
    }

    /// Move toward the nearest living zombie — safety-in-numbers behaviour.
    bool GroupWithNearbyZombies()
    {
        if (currentState == State.Dead || currentState == State.Hit) return false;
        NPCNavMesh nearest = FindNearbyAlly(groupSearchRadius);
        if (nearest == null) return false;
        groupTarget = nearest.transform;
        agent.SetDestination(groupTarget.position);
        return true;
    }

    NPCNavMesh FindNearbyAlly(float radius)
    {
        NPCNavMesh nearest = null;
        float bestDist = radius;
        foreach (var zombie in FindObjectsByType<NPCNavMesh>(FindObjectsSortMode.None))
        {
            if (zombie == null || zombie == this || zombie.IsDead) continue;
            float d = Vector3.Distance(transform.position, zombie.transform.position);
            if (d < bestDist) { bestDist = d; nearest = zombie; }
        }
        return nearest;
    }

    /// Centralised reward reporter — updates both Q-Learning and ML-Agents.
    void ReportQLReward(float reward)
    {
        // ── Q-Learning update ─────────────────────────────────────────────────
        var ql = ZombieQLearner.Instance;
        if (ql != null)
        {
            float playerHp  = (playerCtrl != null && playerCtrl.MaxHealth > 0)
                              ? (float)playerCtrl.Health / playerCtrl.MaxHealth : 1f;
            float dist      = player != null ? Vector3.Distance(transform.position, player.position) : 99f;
            int   nextState = ql.EncodeState(playerHp, dist, hitPressure);
            ql.Learn(qlState, qlAction, reward, nextState);
            qlState = nextState;
        }

        // ── ML-Agents reward — passed directly to the PPO trainer / NN ───────
        mlAgent?.AddReward(reward);
    }

    /// Called by ZombieAgent.OnActionReceived() — ML model drives strategy directly.
    public bool SetMLAction(int action)
    {
        if (currentState == State.Dead) return false;

        // Mid-hitstun we can't change state yet, but the decision made right
        // after being hit is the important one — remember it and apply on recovery.
        if (currentState == State.Hit)
        {
            pendingMLAction = action;
            return action == activeMLAction;
        }

        return ApplyMLAction(action, false);
    }

    bool ApplyMLAction(int action, bool ignoreLock)
    {
        if (action < 0 || action > 3 || currentState == State.Dead) return false;

        bool emergencyRetreat = action == 2 && ThreatLevel >= scatterThreat;
        bool recoveredReturn  = activeMLAction == 2 && (action == 0 || action == 1)
                              && ThreatLevel <= returnThreat && !MustCompleteRetreat;

        if (!ignoreLock && action != activeMLAction && Time.time < mlActionLockedUntil
            && !emergencyRetreat && !recoveredReturn)
            return false;

        if (action == activeMLAction)
        {
            bool strategyStillExecuting =
                ((action == 0 || action == 1) && currentState == State.Chase)
                || (action == 2 && currentState == State.ChaseVillager && targetVillager != null)
                || (action == 3 && currentState == State.GroupUp && groupTarget != null);
            if (strategyStillExecuting) return true;
        }

        switch (action)
        {
            case 0: // Chase
                flankAngle     = 0f;
                targetVillager = null;
                groupTarget    = null;
                alertedBySwarm = true;
                currentState   = State.Chase;
                agent.speed    = chaseSpeed;
                ZombieSwarmManager.Instance?.RegisterChaser(this);
                CommitMLAction(0, mlCombatCommitTime);
                break;

            case 1: // Flank
                if (Mathf.Abs(flankAngle) < 0.1f)
                    flankAngle = Random.value > 0.5f ? 75f : -75f;
                targetVillager = null;
                groupTarget    = null;
                alertedBySwarm = true;
                currentState   = State.Chase;
                agent.speed    = chaseSpeed;
                ZombieSwarmManager.Instance?.RegisterChaser(this);
                CommitMLAction(1, mlCombatCommitTime);
                break;

            case 2: // Retreat to villager
                flankAngle = 0f;
                VillagerBoid retreat = FindRetreatVillager();
                if (retreat != null)
                {
                    bool startingRetreat = activeMLAction != 2;
                    UnregisterFromSwarm();
                    targetVillager = retreat;
                    groupTarget    = null;
                    currentState   = State.ChaseVillager;
                    agent.speed    = villagerChaseSpeed;
                    if (startingRetreat)
                    {
                        retreatStartedAt        = Time.time;
                        retreatHitVillager      = false;
                        retreatTimeoutReported  = false;
                        mlAgent?.RecordScatterStarted();
                    }
                    CommitMLAction(2, mlRetreatCommitTime);
                }
                else return false;
                break;

            case 3: // Group up persistently; do not overwrite the destination with Chase
                flankAngle = 0f;
                targetVillager = null;
                if (!GroupWithNearbyZombies()) return false;
                UnregisterFromSwarm();
                currentState   = State.GroupUp;
                agent.speed    = chaseSpeed;
                CommitMLAction(3, mlGroupCommitTime);
                break;
        }
        return true;
    }

    void CommitMLAction(int action, float duration)
    {
        activeMLAction = action;
        mlActionLockedUntil = Time.time + Mathf.Max(0f, duration);
    }

    void ExecuteGroupUp()
    {
        if (groupTarget == null)
        {
            if (!GroupWithNearbyZombies())
            {
                activeMLAction = -1;
                EnterChase(lastCanSee);
            }
            return;
        }

        agent.speed = chaseSpeed;
        agent.SetDestination(groupTarget.position);
    }

    // ── States ────────────────────────────────────────────────────────────────

    void Patrol()
    {
        agent.speed = patrolSpeed;
        if (!agent.pathPending && agent.remainingDistance < 1f)
            SetNewPatrolTarget();
    }

    void TryDamageHero()
    {
        if (playerCtrl == null) return;
        if (Time.time - lastDamageTime < damageCooldown) return;
        if (Vector3.Distance(transform.position, player.position) > contactRange) return;
        if (playerCtrl.TakeDamage(contactDamage))
        {
            lastDamageTime = Time.time;
            ReportQLReward(1.0f);   // only reward damage that actually landed
            mlAgent?.RecordHeroHit();
        }
    }

    void Chase(bool canSee)
    {
        TryDamageHero();
        agent.speed = chaseSpeed;

        if (canSee)
        {
            lastKnownPos   = player.position;
            lostSightTimer = 0f;
            if (!hasRaisedAlert)
            {
                hasRaisedAlert = true;
                ZombieSwarmManager.Instance?.RaiseAlert(transform.position);
            }
        }
        else if (!alertedBySwarm)
        {
            lostSightTimer += Time.deltaTime;
            if (lostSightTimer >= gracePeriod)
            {
                UnregisterFromSwarm();
                EnterInvestigate();
                return;
            }
        }

        var   mgr          = ZombieSwarmManager.Instance;
        float distToPlayer = Vector3.Distance(transform.position, player.position);
        // ML actions must control the attack geometry. The old swarm destination
        // forced both Chase and Flank into the same four-metre perimeter ring.
        bool  useSwarm     = mlAgent == null && mgr != null && mgr.IsAlerted
                           && distToPlayer > 5f;
        agent.destination  = useSwarm ? mgr.GetChaseDest(this, chaseSpeed) : GetChaseTarget();
    }

    void Investigate()
    {
        if (!agent.pathPending && agent.remainingDistance < 1.5f)
        {
            currentState = State.Patrol;
            SetNewPatrolTarget();
        }
    }

    void ChaseVillager()
    {
        if (targetVillager == null)
        {
            // Pick a distributed retreat target before giving up.
            var next = FindRetreatVillager();
            if (next != null)
            {
                targetVillager = next;   // pivot to new target, stay in state
            }
            else
            {
                targetVillager = null;
                currentState   = State.Patrol;
                agent.speed    = patrolSpeed;
                SetNewPatrolTarget();
                return;
            }
        }

        agent.speed = villagerChaseSpeed;

        // Villagers can stand just off a walkable polygon while steering around
        // buildings. Project the target onto the NavMesh so SetDestination does
        // not leave the zombie stuck at the perimeter.
        Vector3 villagerDestination = targetVillager.transform.position;
        if (NavMesh.SamplePosition(villagerDestination, out NavMeshHit navHit, 3f, NavMesh.AllAreas))
            villagerDestination = navHit.position;
        agent.destination = villagerDestination;

        float distToVillager = Vector3.Distance(transform.position, targetVillager.transform.position);

        if (distToVillager < contactRange)
        {
            if (Time.time - lastVillagerDamageTime >= villagerDamageCooldown)
            {
                bool killed;
                if (targetVillager.TakeDamage(villagerDamage, out killed))
                {
                    lastVillagerDamageTime = Time.time;

                    // Villagers are briefly better than the hero only while afraid.
                    // As observable threat recovers, this drops from 1.4 to 0.2,
                    // below the hero's +1.0 reward, teaching a return to combat.
                    float villagerReward = 0.2f + 1.2f * ThreatLevel;
                    ReportQLReward(villagerReward);
                    mlAgent?.RecordVillagerHit();

                    if (activeMLAction == 2 && !retreatHitVillager)
                    {
                        retreatHitVillager = true;
                        mlAgent?.RecordScatterCompleted();
                    }

                    if (killed)
                    {
                        ReportQLReward(0.4f + 0.8f * ThreatLevel);
                        mlAgent?.RecordVillagerKill();
                    }
                }
            }
        }
    }

    // ── Transitions ───────────────────────────────────────────────────────────

    void EnterChase(bool hadFOV)
    {
        currentState   = State.Chase;
        agent.speed    = chaseSpeed;
        lostSightTimer = 0f;
        alertedBySwarm = false;
        dwellTimer     = 0f;
        ZombieSwarmManager.Instance?.RegisterChaser(this);
    }

    void EnterInvestigate()
    {
        currentState      = State.Investigate;
        agent.speed       = patrolSpeed * 1.3f;
        agent.destination = lastKnownPos;
        dwellTimer        = 0f;
    }

    void UnregisterFromSwarm() => ZombieSwarmManager.Instance?.UnregisterChaser(this);

    // ── Swarm API ─────────────────────────────────────────────────────────────

    public void ReceiveAlert(float delay)
    {
        if (currentState == State.Dead || currentState == State.Hit) return;
        StartCoroutine(AlertAfterDelay(delay));
    }

    IEnumerator AlertAfterDelay(float delay)
    {
        yield return new WaitForSeconds(delay);
        if (currentState == State.Dead || currentState == State.Hit) yield break;
        alertedBySwarm = true;
        lastKnownPos   = player != null ? player.position : transform.position;
    }

    public void Standdown()
    {
        if (currentState == State.Dead || currentState == State.Hit) return;
        StopAllCoroutines();
        alertedBySwarm  = false;
        hasRaisedAlert  = false;
        UnregisterFromSwarm();
        currentState    = State.Patrol;
        agent.isStopped = false;
        SetNewPatrolTarget();
    }

    // ── Combat ────────────────────────────────────────────────────────────────

    public void TakeHit()
    {
        if (currentState == State.Dead) return;
        health--;

        // Accumulate smooth hit pressure — utility AI reads this each eval cycle
        hitPressure = Mathf.Min(1f, hitPressure + 1f / Mathf.Max(1, hitsPerFullPressure));
        ReportQLReward(-0.8f);  // taking a hit is bad — this is the core signal that drives retreat

        // ── Broadcast fear — neighbours think "I could be next" and scatter ───
        foreach (var c in Physics.OverlapSphere(transform.position, hitAlertRadius))
        {
            var z = c.GetComponent<NPCNavMesh>();
            if (z != null && z != this && !z.IsDead) z.WitnessHitAlert(witnessHitGain);
        }

        Debug.Log($"{name} hit  hp={health}  hitPressure={hitPressure:F2}  threat={ThreatLevel:F2}");

        AudioManager.Instance?.PlayZombieHit();
        if (health <= 0) { Die(); return; }
        StartCoroutine(HitStun());
    }

    IEnumerator HitStun()
    {
        State stateBeforeHit = currentState;   // remember what we were doing

        currentState    = State.Hit;
        agent.isStopped = true;
        if (animator != null) animator.SetTrigger("Hit");

        yield return new WaitForSeconds(hitStunTime);

        if (currentState == State.Dead) yield break;
        agent.isStopped = false;

        if (mlAgent != null)
        {
            // ML-Agents is driving. Forcing Chase here would override the model's
            // decision exactly when it matters most — right after taking damage.
            if (pendingMLAction >= 0)
            {
                // Apply the decision the model made while we were stunned
                int queued      = pendingMLAction;
                pendingMLAction = -1;
                currentState    = State.Patrol;   // neutral so SetMLAction can set freely
                ApplyMLAction(queued, true);
            }
            else
            {
                currentState = (stateBeforeHit == State.Hit || stateBeforeHit == State.Dead)
                               ? State.Chase
                               : stateBeforeHit;

                if (currentState == State.ChaseVillager && targetVillager == null)
                    currentState = State.Chase;
            }
        }
        else
        {
            // Utility-AI path (no ML) — original behaviour
            var mgr = ZombieSwarmManager.Instance;
            currentState = (mgr != null && mgr.IsAlerted) ? State.Chase : State.Patrol;
            if (currentState == State.Chase)
                ZombieSwarmManager.Instance?.RegisterChaser(this);
            else
                SetNewPatrolTarget();
        }

        dwellTimer = 0f;
    }

    void Die()
    {
        currentState    = State.Dead;
        agent.isStopped = true;
        agent.enabled   = false;

        UnregisterFromSwarm();
        ZombieSwarmManager.Instance?.UnregisterZombie(this);
        hasRaisedAlert = false;

        if (animator != null) { animator.SetBool("IsDead", true); animator.ResetTrigger("Hit"); }

        var col = GetComponent<Collider>();
        if (col != null) col.enabled = false;

        // Broadcast mortality alert — raise nearby zombies' fear pressure
        foreach (var c in Physics.OverlapSphere(transform.position, deathAlertRadius))
        {
            var z = c.GetComponent<NPCNavMesh>();
            if (z != null && z != this) z.MortalityAlert();
        }

        ReportQLReward(-2f);         // Q-Learning + ML-Agents: dying is a big penalty
        mlAgent?.NotifyDied();       // signals ML-Agents trainer: episode ended
        AudioManager.Instance?.PlayZombieDeath();
        GameManager.Instance?.OnZombieDied();

        Destroy(gameObject, deathRemoveDelay);
    }

    // ── FOV Perception ────────────────────────────────────────────────────────

    bool CanSeePlayer()
    {
        Vector3 toPlayer = player.position - transform.position;
        float   dist     = toPlayer.magnitude;

        if (dist < hearingRange) return true;
        if (dist > fovRange)     return false;
        if (Vector3.Angle(transform.forward, toPlayer) > fovAngle * 0.5f) return false;

        Vector3 origin    = transform.position + Vector3.up * EYE_HEIGHT;
        Vector3 targetPos = player.position    + Vector3.up * 1f;
        Vector3 dir       = (targetPos - origin).normalized;
        float   checkDist = Vector3.Distance(origin, targetPos) + 0.5f;

        if (Physics.Raycast(origin, dir, out RaycastHit hit, checkDist,
                            Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            return hit.transform == player || hit.transform.IsChildOf(player);

        return true; // nothing blocking — clear LOS
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    VillagerBoid FindNearestVillager()
    {
        if (Time.time - lastVillagerScan > VILLAGER_SCAN_INTERVAL)
        {
            cachedVillagers  = FindObjectsByType<VillagerBoid>(FindObjectsSortMode.None);
            lastVillagerScan = Time.time;
        }

        VillagerBoid nearest  = null;
        float        bestDist = villagerDetectRange;
        foreach (var v in cachedVillagers)
        {
            if (v == null) continue;
            float d = Vector3.Distance(transform.position, v.transform.position);
            if (d < bestDist) { bestDist = d; nearest = v; }
        }
        return nearest;
    }

    /// Picks a villager to retreat toward — prefers ones that are far from the player
    /// so the zombie actually gets away from the fight, not just sidesteps.
    VillagerBoid FindRetreatVillager(float maxRange = -1f)
    {
        if (Time.time - lastVillagerScan > VILLAGER_SCAN_INTERVAL)
        {
            cachedVillagers  = FindObjectsByType<VillagerBoid>(FindObjectsSortMode.None);
            lastVillagerScan = Time.time;
        }

        VillagerBoid best      = null;
        float        bestScore = float.MinValue;
        NPCNavMesh[] allZombies = FindObjectsByType<NPCNavMesh>(FindObjectsSortMode.None);

        foreach (var v in cachedVillagers)
        {
            if (v == null) continue;
            float distFromMe     = Vector3.Distance(transform.position, v.transform.position);
            float distFromPlayer = player != null
                ? Vector3.Distance(player.position, v.transform.position) : 99f;

            if (maxRange > 0f && distFromMe > maxRange) continue;

            int assigned = 0;
            foreach (var zombie in allZombies)
                if (zombie != null && zombie != this && !zombie.IsDead
                    && zombie.targetVillager == v)
                    assigned++;

            // Prefer safety and short paths, strongly avoid villagers already being
            // chased, and add stable per-zombie variation so the horde scatters.
            float variation = Mathf.PerlinNoise(
                Mathf.Abs(GetInstanceID()) * 0.013f,
                Mathf.Abs(v.GetInstanceID()) * 0.017f) * 0.75f;
            float score = distFromPlayer * 0.12f
                        - distFromMe * 0.08f
                        - assigned * 4f
                        + variation;
            if (score > bestScore) { bestScore = score; best = v; }
        }

        // Fallback to nearest if no safe target found
        return best != null ? best : FindNearestVillager();
    }

    void SetNewPatrolTarget()
    {
        Vector3    randomDir = Random.insideUnitSphere * patrolRadius + transform.position;
        NavMeshHit hit;
        if (NavMesh.SamplePosition(randomDir, out hit, patrolRadius, NavMesh.AllAreas))
        {
            patrolTarget      = hit.position;
            agent.destination = patrolTarget;
        }
    }

    // ── Gizmos ────────────────────────────────────────────────────────────────

    void OnDrawGizmosSelected()
    {
        Vector3 eye = transform.position + Vector3.up * EYE_HEIGHT;

        Gizmos.color = Color.yellow;
        Gizmos.DrawLine(eye, eye + Quaternion.Euler(0, -fovAngle * 0.5f, 0) * transform.forward * fovRange);
        Gizmos.DrawLine(eye, eye + Quaternion.Euler(0,  fovAngle * 0.5f, 0) * transform.forward * fovRange);
        Gizmos.DrawWireSphere(eye, fovRange);

        Gizmos.color = Color.cyan;
        Gizmos.DrawWireSphere(transform.position, hearingRange);

        // Death alert radius — orange
        Gizmos.color = new Color(1f, 0.5f, 0f, 0.5f);
        Gizmos.DrawWireSphere(transform.position, deathAlertRadius);

        if (currentState == State.Investigate)
        {
            Gizmos.color = Color.magenta;
            Gizmos.DrawSphere(lastKnownPos, 0.4f);
            Gizmos.DrawLine(transform.position, lastKnownPos);
        }
    }
}
