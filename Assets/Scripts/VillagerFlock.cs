using UnityEngine;
using System.Collections.Generic;

public class VillagerFlock : MonoBehaviour
{
    [Header("Members")]
    public List<VillagerBoid> members = new List<VillagerBoid>();

    [Header("Waypoints — normal patrol route")]
    public List<Transform> waypoints = new List<Transform>();

    [Header("Zombie Detection")]
    public float fleeRadius    = 15f;   // zombie within this distance triggers flee
    public float safeRadius    = 25f;   // must get this far from zombie to feel safe
    public float fleeDistance  = 20f;   // how far away to run when fleeing

    [HideInInspector] public Vector3 target;

    // State
    enum State { Patrol, Flee }
    State state        = State.Patrol;
    int   wpIndex      = 0;
    float waitTimer    = 0f;
    float waitDuration = 2f;
    bool  waiting      = false;

    // Refreshed periodically because training waves spawn zombies dynamically.
    Transform[] zombies = new Transform[0];
    float nextZombieRefresh;
    const float ZOMBIE_REFRESH_INTERVAL = 0.5f;

    void Start()
    {
        RefreshZombies();

        if (waypoints.Count > 0)
            target = waypoints[0].position;
        else
            target = transform.position;
    }

    void RefreshZombies()
    {
        // Find zombies by their NPCNavMesh component — no tag required.
        try
        {
            var zombieComponents = FindObjectsByType<NPCNavMesh>(FindObjectsSortMode.None);
            zombies = new Transform[zombieComponents.Length];
            for (int i = 0; i < zombieComponents.Length; i++)
                zombies[i] = zombieComponents[i].transform;
        }
        catch (System.Exception e)
        {
            Debug.LogWarning(name + " could not find zombies: " + e.Message);
            zombies = new Transform[0];
        }
        nextZombieRefresh = Time.time + ZOMBIE_REFRESH_INTERVAL;
    }

    void Update()
    {
        if (Time.time >= nextZombieRefresh)
            RefreshZombies();

        Transform nearestZombie = GetNearestZombie(out float nearestDist);

        // ── Decide state ─────────────────────────────────────────────────
        if (state == State.Patrol && nearestZombie != null && nearestDist < fleeRadius)
        {
            state = State.Flee;
            PickFleeTarget(nearestZombie.position);
            waiting = false;
            Debug.Log(name + " FLEEING from zombie!");
        }
        else if (state == State.Flee && (nearestZombie == null || nearestDist > safeRadius))
        {
            state = State.Patrol;
            // Resume from nearest waypoint
            if (waypoints.Count > 0)
            {
                wpIndex = NearestWaypointIndex();
                target  = waypoints[wpIndex].position;
            }
            else target = transform.position;
            Debug.Log(name + " safe — resuming patrol.");
        }

        // ── State behaviour ───────────────────────────────────────────────
        if (state == State.Flee)
        {
            // While fleeing, keep updating flee direction in case zombie moves
            if (nearestZombie != null)
                PickFleeTarget(nearestZombie.position);
            return;
        }

        // Patrol logic
        if (waypoints.Count == 0) return;

        if (waiting)
        {
            waitTimer += Time.deltaTime;
            if (waitTimer >= waitDuration)
            {
                waiting = false;
                waitTimer = 0f;
                wpIndex = (wpIndex + 1) % waypoints.Count;
                target  = waypoints[wpIndex].position;
            }
            return;
        }

        // Check if majority of group has arrived
        int arrived = 0, total = 0;
        foreach (var m in members)
        {
            if (m == null) continue;
            total++;
            if (Vector3.Distance(m.transform.position, target) < 4f)
                arrived++;
        }
        if (total > 0 && arrived >= Mathf.CeilToInt(total * 0.6f))
            waiting = true;
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    void PickFleeTarget(Vector3 zombiePos)
    {
        // If a SafeZone exists, flee TOWARD it — gives villagers a real goal
        if (SafeZone.Instance != null)
        {
            target = SafeZone.Instance.transform.position;
            return;
        }

        // Fallback: flee AWAY from zombie
        Vector3 centre = Vector3.zero;
        int cnt = 0;
        foreach (var m in members) { if (m == null) continue; centre += m.transform.position; cnt++; }
        if (cnt > 0) centre /= cnt; else centre = transform.position;

        Vector3 away = (centre - zombiePos);
        away.y = 0f;
        if (away.sqrMagnitude < 0.01f)
            away = new Vector3(Random.Range(-1f, 1f), 0f, Random.Range(-1f, 1f));
        away.Normalize();

        target = centre + away * fleeDistance;
    }

    Transform GetNearestZombie(out float nearestDist)
    {
        nearestDist = float.MaxValue;
        Transform nearest = null;

        // Group centre for distance check
        Vector3 centre = transform.position;
        int validCount = 0;
        centre = Vector3.zero;
        foreach (var m in members)
        {
            if (m == null) continue;
            centre += m.transform.position;
            validCount++;
        }
        if (validCount > 0) centre /= validCount;
        else centre = transform.position;

        foreach (var z in zombies)
        {
            if (z == null) continue;
            float d = Vector3.Distance(centre, z.position);
            if (d < nearestDist) { nearestDist = d; nearest = z; }
        }
        return nearest;
    }

    int NearestWaypointIndex()
    {
        int best = 0;
        float bestDist = float.MaxValue;
        for (int i = 0; i < waypoints.Count; i++)
        {
            float d = Vector3.Distance(transform.position, waypoints[i].position);
            if (d < bestDist) { bestDist = d; best = i; }
        }
        return best;
    }

    // Draw flee and safe radii in Scene view
    void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.red;
        Gizmos.DrawWireSphere(transform.position, fleeRadius);
        Gizmos.color = Color.green;
        Gizmos.DrawWireSphere(transform.position, safeRadius);
        Gizmos.color = Color.yellow;
        Gizmos.DrawSphere(target, 0.5f);
    }
}
