using UnityEngine;
using UnityEngine.AI;

public class CrowdAgent : MonoBehaviour
{
    NavMeshAgent agent;
    Animator     animator;
    CrowdManager manager;

    float waitTimer;
    bool  waiting;

    void Start()
    {
        agent    = GetComponent<NavMeshAgent>();
        animator = GetComponentInChildren<Animator>();
        manager  = CrowdManager.instance;

        if (agent == null)
        {
            Debug.LogError("CrowdAgent: No NavMeshAgent on " + gameObject.name + " — add one!");
            return;
        }

        // Disable CharacterController if present — it conflicts with NavMeshAgent
        var cc = GetComponent<CharacterController>();
        if (cc != null) cc.enabled = false;

        // Each NPC gets its own random speed
        agent.speed = Random.Range(manager.minSpeed, manager.maxSpeed);
    }

    void Update()
    {
        if (agent == null) return;
        if (!agent.isOnNavMesh)
        {
            Debug.LogWarning("CrowdAgent: " + gameObject.name + " is not on NavMesh — bake it first!");
            return;
        }

        // Start moving once placed on NavMesh
        if (!waiting && !agent.hasPath)
            SetNewDestination();

        // Drive animator
        if (animator != null)
            animator.SetFloat("Speed", agent.velocity.magnitude);

        if (waiting)
        {
            waitTimer -= Time.deltaTime;
            if (waitTimer <= 0f)
            {
                waiting = false;
                SetNewDestination();
            }
            return;
        }

        // Reached destination — wait then pick a new one
        if (!agent.pathPending && agent.remainingDistance < 0.8f)
        {
            waiting   = true;
            waitTimer = Random.Range(manager.waypointWait * 0.5f, manager.waypointWait);
        }
    }

    void SetNewDestination()
    {
        // Pick a random point on the NavMesh within wander radius
        Vector3 randomDir = Random.insideUnitSphere * manager.wanderRadius;
        randomDir.y = 0;
        randomDir  += manager.transform.position;

        NavMeshHit hit;
        if (NavMesh.SamplePosition(randomDir, out hit, manager.wanderRadius, NavMesh.AllAreas))
            agent.SetDestination(hit.position);
    }
}
