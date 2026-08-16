using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Sensors;
using Unity.MLAgents.Actuators;

[RequireComponent(typeof(NPCNavMesh))]
public class ZombieAgent : Agent
{
    NPCNavMesh       npc;
    Transform        playerTransform;
    PlayerController playerCtrl;

    const float MAX_DIST = 25f;   // normalisation range for distance

    readonly Collider[] _overlapBuffer = new Collider[16];

    bool  observedVillagerExists;
    bool  observedNearbyAlly;
    int   previousVillagerTargetId = -1;
    float previousVillagerDistance = -1f;

    // ─────────────────────────────────────────────────────────────────────────
    public override void Initialize()
    {
        npc = GetComponent<NPCNavMesh>();

        var playerObj = GameObject.FindWithTag("Player");
        if (playerObj != null)
        {
            playerTransform = playerObj.transform;
            playerCtrl      = playerObj.GetComponent<PlayerController>();
        }
    }

    // ── Observations (11 floats — must match Space Size in BehaviorParameters) ─
    public override void CollectObservations(VectorSensor sensor)
    {
        // 1. Distance to player (0 = same position, 1 = MAX_DIST away)
        float dist = playerTransform != null
                     ? Vector3.Distance(transform.position, playerTransform.position)
                     : MAX_DIST;
        sensor.AddObservation(Mathf.Clamp01(dist / MAX_DIST));

        // 2. Player current HP ratio (0 = dead, 1 = full health)
        float playerHp = (playerCtrl != null && playerCtrl.MaxHealth > 0)
                         ? (float)playerCtrl.Health / playerCtrl.MaxHealth : 1f;
        sensor.AddObservation(playerHp);

        // 3. Own HP ratio
        sensor.AddObservation(npc.HpRatio);

        // 4. Threat level — own damage PLUS fear absorbed from allies being hit
        //    or killed nearby. Group-level signal, so a whole cluster scatters
        //    when the hero starts swinging at one of them.
        sensor.AddObservation(npc.ThreatLevel);

        // 5-6. Normalised direction toward player (x, z components)
        Vector3 dir = Vector3.zero;
        if (playerTransform != null && dist > 0.01f)
            dir = (playerTransform.position - transform.position).normalized;
        sensor.AddObservation(dir.x);
        sensor.AddObservation(dir.z);

        // 7. Swarm alert — has this zombie been told the player is nearby?
        sensor.AddObservation(npc.IsAlerted ? 1f : 0f);

        // 8. Nearby ally density — safety-in-numbers signal (0 = alone, 1 = 6+ allies)
        int count = Physics.OverlapSphereNonAlloc(transform.position, 6f, _overlapBuffer);
        int allies = 0;
        for (int i = 0; i < count; i++)
        {
            if (_overlapBuffer[i] == null) continue;
            var ally = _overlapBuffer[i].GetComponent<NPCNavMesh>()
                    ?? _overlapBuffer[i].GetComponentInParent<NPCNavMesh>();
            if (ally != null && ally != npc && !ally.IsDead)
                allies++;
        }
        observedNearbyAlly = allies > 0;
        sensor.AddObservation(Mathf.Clamp01(allies / 6f));

        // ── 9-11. Villager awareness — without this the model is blind to villagers ──
        VillagerBoid nearestV = null;
        float        vDist    = float.PositiveInfinity;
        foreach (var v in FindObjectsByType<VillagerBoid>(FindObjectsSortMode.None))
        {
            if (v == null) continue;
            float d = Vector3.Distance(transform.position, v.transform.position);
            if (d < vDist) { vDist = d; nearestV = v; }
        }
        observedVillagerExists = nearestV != null;

        // 9. Distance to nearest villager (0 = touching, 1 = far/none)
        sensor.AddObservation(nearestV != null ? Mathf.Clamp01(vDist / MAX_DIST) : 1f);

        // 10-11. Direction toward nearest villager (x, z)
        Vector3 vDir = Vector3.zero;
        if (nearestV != null && vDist > 0.01f)
            vDir = (nearestV.transform.position - transform.position).normalized;
        sensor.AddObservation(vDir.x);
        sensor.AddObservation(vDir.z);
    }

    // ── Actions — neural net output mapped to zombie strategy ─────────────────
    // Discrete branch 0, size 4:
    //   0 = Chase           (charge player directly)
    //   1 = Flank           (arc around, attack from side)
    //   2 = RetreatVillager (disengage, attack a villager)
    //   3 = GroupUp         (move toward nearest ally zombie)
    public override void OnActionReceived(ActionBuffers actions)
    {
        int requestedStrategy = actions.DiscreteActions[0];
        int previousStrategy  = npc.CurrentMLAction;
        bool accepted          = npc.SetMLAction(requestedStrategy);
        int  strategy          = npc.CurrentMLAction;
        float threat           = npc.ThreatLevel;

        var stats = Academy.Instance.StatsRecorder;
        stats.Add("Zombie/Action/Chase",   strategy == 0 ? 1f : 0f);
        stats.Add("Zombie/Action/Flank",   strategy == 1 ? 1f : 0f);
        stats.Add("Zombie/Action/Retreat", strategy == 2 ? 1f : 0f);
        stats.Add("Zombie/Action/GroupUp", strategy == 3 ? 1f : 0f);
        stats.Add("Zombie/Threat", threat);
        stats.Add("Zombie/RejectedSwitch", !accepted && requestedStrategy != strategy ? 1f : 0f);

        if (accepted && strategy != previousStrategy)
        {
            if ((previousStrategy == 0 || previousStrategy == 1) && strategy == 2)
                stats.Add("Zombie/Transitions/HeroToRetreat", 1f, StatAggregationMethod.Sum);
            if (previousStrategy == 2 && (strategy == 0 || strategy == 1))
                stats.Add("Zombie/Transitions/RetreatToHero", 1f, StatAggregationMethod.Sum);
        }

        // Tiny time cost prevents passive policies from winning by doing nothing.
        AddReward(-0.0002f);

        // When a strategy is still committed, teach PPO not to request a different
        // action every decision. The controller ignores the request until the
        // commitment expires, preventing visible twitching in the game.
        if (!accepted && strategy >= 0 && requestedStrategy != strategy)
            AddReward(-0.001f);

        // ── Reward shaping ────────────────────────────────────────────────────
        // IMPORTANT: these fire every decision (~5/sec), so they must stay TINY
        // relative to event rewards. Otherwise the model
        // farms reward by merely *selecting* an action instead of succeeding at it.
        if (threat >= npc.ScatterThreat)
        {
            if (strategy == 2)
                AddReward(0.003f * threat);
            else if (strategy == 0 || strategy == 1)
                AddReward(-0.002f * threat);
        }
        else if (threat <= npc.ReturnThreat)
        {
            float dist      = playerTransform != null
                              ? Vector3.Distance(transform.position, playerTransform.position)
                              : MAX_DIST;
            float proximity = 1f - Mathf.Clamp01(dist / MAX_DIST);

            if (strategy == 0 || strategy == 1)
                AddReward(0.002f * proximity);
            else if (strategy == 2)
                AddReward(-0.006f); // recovered zombies should leave villagers
        }

        // Reward only distance PROGRESS toward the selected villager. Absolute
        // proximity rewarded standing still and could be farmed indefinitely.
        if (strategy == 2 && npc.HasVillagerTarget)
        {
            int   targetId = npc.VillagerTargetId;
            float distance = npc.DistanceToVillagerTarget;
            if (targetId == previousVillagerTargetId && previousVillagerDistance >= 0f)
            {
                float progress = Mathf.Clamp(previousVillagerDistance - distance, -1f, 1f);
                AddReward(0.004f * progress);
            }
            previousVillagerTargetId = targetId;
            previousVillagerDistance = distance;
        }
        else
        {
            previousVillagerTargetId = -1;
            previousVillagerDistance = -1f;
        }
    }

    public override void WriteDiscreteActionMask(IDiscreteActionMask actionMask)
    {
        bool canRetreat         = observedVillagerExists;
        bool retreatTimedOut    = npc.RetreatTimedOut;
        bool mustScatter        = canRetreat && !retreatTimedOut
                                && npc.ThreatLevel >= npc.ScatterThreat;
        bool mustFinishRetreat  = canRetreat && npc.MustCompleteRetreat;
        bool forceRetreat       = mustScatter || mustFinishRetreat;
        bool forceReturn        = retreatTimedOut
                                || (npc.ThreatLevel <= npc.ReturnThreat && !mustFinishRetreat);

        // Guardrails keep both training and inference out of degenerate policies:
        // healthy zombies fight, badly pressured zombies scatter, and recovered
        // zombies return. Personality is already folded into ThreatLevel, so the
        // hit count required to cross these limits differs per zombie.
        actionMask.SetActionEnabled(0, 0, !forceRetreat);
        actionMask.SetActionEnabled(0, 1, !forceRetreat);
        actionMask.SetActionEnabled(0, 2, canRetreat && !forceReturn);

        // The completed v2 policy collapsed to GroupUp (64% of decisions), which
        // produced a stationary blob. Keep the fourth output for ONNX compatibility
        // but mask it out; Chase/Flank already provide coordinated combat variety.
        actionMask.SetActionEnabled(0, 3, false);
    }

    // ── Heuristic — lets you test the agent manually without Python running ───
    /// Hand-written approximation of the policy we WANT the network to learn.
    /// Runs whenever no model is assigned and Python is not connected, so the
    /// game loop can be validated before/without training.
    public override void Heuristic(in ActionBuffers actionsOut)
    {
        float pressure = npc.ThreatLevel;   // includes fear from nearby allies being hit
        float hp       = npc.HpRatio;

        bool villagerExists = FindAnyObjectByType<VillagerBoid>() != null;

        int action;

        if (villagerExists && (pressure >= npc.ScatterThreat || npc.MustCompleteRetreat || hp < 0.5f))
            action = 2;                       // hurt → go hit villagers instead
        else if (pressure > 0.2f)
            action = 1;                       // lightly pressured → flank, don't charge
        else
            action = 0;                       // healthy → chase the hero

        actionsOut.DiscreteActions.Array[0] = action;
    }

    // ── Episode lifecycle ─────────────────────────────────────────────────────
    public override void OnEpisodeBegin()
    {
        // Zombies are destroyed on death and freshly instantiated each wave,
        // so there is nothing to reset here.
    }

    public void RecordHeroHit()
    {
        Academy.Instance.StatsRecorder.Add(
            "Zombie/Events/HeroHits", 1f, StatAggregationMethod.Sum);
    }

    public void RecordVillagerHit()
    {
        Academy.Instance.StatsRecorder.Add(
            "Zombie/Events/VillagerHits", 1f, StatAggregationMethod.Sum);
    }

    public void RecordVillagerKill()
    {
        Academy.Instance.StatsRecorder.Add(
            "Zombie/Events/VillagerKills", 1f, StatAggregationMethod.Sum);
    }

    public void RecordScatterStarted()
    {
        Academy.Instance.StatsRecorder.Add(
            "Zombie/Events/ScatterStarted", 1f, StatAggregationMethod.Sum);
    }

    public void RecordScatterCompleted()
    {
        Academy.Instance.StatsRecorder.Add(
            "Zombie/Events/ScatterCompleted", 1f, StatAggregationMethod.Sum);
    }

    public void RecordRetreatTimeout()
    {
        Academy.Instance.StatsRecorder.Add(
            "Zombie/Events/RetreatTimeout", 1f, StatAggregationMethod.Sum);
    }

    /// Called by NPCNavMesh.Die() — signals the trainer that this episode ended.
    public void NotifyDied()
    {
        EndEpisode();
    }
}
