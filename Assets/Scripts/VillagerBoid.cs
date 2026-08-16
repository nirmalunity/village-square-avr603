using UnityEngine;
using System.Collections.Generic;

/// One villager in a flocking group.
/// Separation + Cohesion + Wander noise + Seek group target with personal offset.
/// Alignment is removed — it was causing the single-file line problem.
[RequireComponent(typeof(CharacterController))]
public class VillagerBoid : MonoBehaviour
{
    [HideInInspector] public VillagerFlock flock;
    [HideInInspector] public Vector3       personalOffset;  // set by spawner

    [Header("Health")]
    [Tooltip("Villager health in the real game. Existing scene values below this are raised at runtime.")]
    public int maxHealth = 30;
    [Tooltip("Lower health keeps villager-hit and kill rewards frequent during ML training.")]
    [SerializeField] int trainingMaxHealth = 12;
    [SerializeField] int minimumPlayHealth = 30;
    public int Health { get; private set; }

    public bool TakeDamage(int dmg, out bool killed)
    {
        killed = false;
        if (Health <= 0) return false;
        Health = Mathf.Max(0, Health - dmg);
        AudioManager.Instance?.PlayVillagerScream();
        if (Health <= 0)
        {
            killed = true;

            // Keep the training world stationary: villagers remain available in
            // every zombie episode instead of disappearing from the curriculum.
            if (GameManager.Instance != null && GameManager.Instance.trainingMode)
                Health = maxHealth;
            else
                Die();
        }
        return true;
    }

    void Die()
    {
        // Remove from flock so it's no longer counted
        if (flock != null) flock.members.Remove(this);
        // Notify game manager so the HUD and lose condition update immediately
        GameManager.Instance?.OnVillagerDied();
        Destroy(gameObject);
    }

    [Header("Flocking Weights")]
    public float separationWeight = 3.0f;
    public float cohesionWeight   = 0.8f;
    public float seekWeight       = 2.0f;
    public float wanderWeight     = 1.2f;
    public float avoidWeight      = 6.0f;   // obstacle avoidance — high so it overrides other forces

    [Header("Perception")]
    public float neighborRadius   = 6f;
    public float separationRadius = 3f;

    [Header("Movement")]
    public float maxSpeed = 3.5f;
    public float maxForce = 4f;

    [Header("Obstacle Avoidance")]
    public float lookAheadDist  = 5f;
    public float avoidRayCount  = 5;
    public float avoidFanAngle  = 60f;

    [Header("Boundary")]
    public float boundaryMargin = 5f;   // start steering inward this far from the edge

    LayerMask obstacleMask;
    float     boundarySize;

    Vector3             velocity;
    Vector3             wanderTarget;
    CharacterController cc;
    Animator            animator;

    // Stuck detection — position-snapshot based (catches force-cancel cases too)
    Vector3 prevPosition;
    float   stuckTimer;
    Vector3 snapshotPosition;
    float   snapshotTimer;

    void Start()
    {
        bool training = GameManager.Instance != null && GameManager.Instance.trainingMode;
        maxHealth = training
            ? Mathf.Max(1, trainingMaxHealth)
            : Mathf.Max(maxHealth, minimumPlayHealth);
        Health   = maxHealth;
        cc       = GetComponent<CharacterController>();
        animator = GetComponentInChildren<Animator>();
        if (animator != null) animator.applyRootMotion = false;

        // Fully random initial velocity
        velocity         = new Vector3(Random.Range(-1f, 1f), 0f, Random.Range(-1f, 1f)).normalized * maxSpeed * 0.4f;
        wanderTarget     = Random.insideUnitSphere;
        wanderTarget.y   = 0f;
        prevPosition     = transform.position;
        snapshotPosition = transform.position;

        obstacleMask = ~(LayerMask.GetMask("Ignore Raycast"));

        // Match the same boundary the player uses
        var ground = GameObject.Find("Ground");
        boundarySize = ground != null
            ? ground.transform.localScale.x * 5f - boundaryMargin
            : 30f;

    }

    void Update()
    {
        if (flock == null) return;

        // ── Collect neighbours ────────────────────────────────────────────
        var neighbours = new List<VillagerBoid>();
        foreach (var b in flock.members)
        {
            if (b == null || b == this) continue;
            if (Vector3.Distance(transform.position, b.transform.position) < neighborRadius)
                neighbours.Add(b);
        }

        // ── Steering forces ───────────────────────────────────────────────
        Vector3 steering = Vector3.zero;

        // Obstacle avoidance runs first — if blocked it dominates
        Vector3 avoid = ObstacleAvoidance();
        if (avoid != Vector3.zero)
        {
            steering += avoid * avoidWeight;
        }
        else
        {
            steering += Separation(neighbours)              * separationWeight;
            steering += Cohesion(neighbours)                * cohesionWeight;
            steering += Seek(flock.target + personalOffset) * seekWeight;
            steering += Wander()                            * wanderWeight;
        }
        steering.y = 0f;

        if (steering.magnitude > maxForce)
            steering = steering.normalized * maxForce;

        // ── Update velocity ───────────────────────────────────────────────
        velocity += steering * Time.deltaTime;
        velocity.y = 0f;

        // Slow down near the personal target spot so group clusters loosely
        float distToSpot = Vector3.Distance(
            new Vector3(transform.position.x, 0, transform.position.z),
            new Vector3(flock.target.x + personalOffset.x, 0, flock.target.z + personalOffset.z));

        if (distToSpot < 2.5f)
            velocity = Vector3.Lerp(velocity, Vector3.zero, Time.deltaTime * 5f);

        // Add boundary steering — push inward when near the edge
        velocity += BoundaryForce() * Time.deltaTime;

        if (velocity.magnitude > maxSpeed)
            velocity = velocity.normalized * maxSpeed;

        // ── Move ──────────────────────────────────────────────────────────
        Vector3 move = velocity;
        move.y = -1f;
        cc.Move(move * Time.deltaTime);

        // Hard clamp as safety net so they never fall off
        Vector3 p = transform.position;
        p.x = Mathf.Clamp(p.x, -boundarySize, boundarySize);
        p.z = Mathf.Clamp(p.z, -boundarySize, boundarySize);
        if (p != transform.position)
        {
            transform.position = p;
            velocity.x = Mathf.Clamp(velocity.x, -maxSpeed, maxSpeed);
            velocity.z = Mathf.Clamp(velocity.z, -maxSpeed, maxSpeed);
        }

        // ── Face movement direction ───────────────────────────────────────
        if (velocity.sqrMagnitude > 0.05f)
            transform.rotation = Quaternion.Slerp(
                transform.rotation,
                Quaternion.LookRotation(velocity),
                Time.deltaTime * 8f);

        // ── Actual movement speed (not desired velocity) ──────────────────
        // Using actual position delta prevents the run animation playing
        // when the villager is blocked against a wall.
        Vector3 actualDelta = transform.position - prevPosition;
        actualDelta.y = 0f;
        float actualSpeed = actualDelta.magnitude / Time.deltaTime;
        prevPosition = transform.position;

        if (animator != null)
            animator.SetFloat("Speed", actualSpeed);

        // ── Stuck detection (position-snapshot — fires even when forces cancel) ──
        snapshotTimer += Time.deltaTime;
        if (snapshotTimer >= 1.0f)
        {
            float moved = Vector3.Distance(transform.position, snapshotPosition);
            if (moved < 0.15f)   // moved less than 15cm in 1 second — truly stuck
            {
                // Sweep all directions, prefer open space away from map edges
                Vector3 escape = MovementUtil.FindClearDirection(
                    transform.position, velocity, lookAheadDist, boundarySize);
                cc.Move(escape * 0.8f);          // bigger nudge to break free
                velocity     = escape * maxSpeed;
                wanderTarget = escape;
                if (escape.sqrMagnitude > 0.01f)
                    transform.rotation = Quaternion.LookRotation(escape);
            }
            snapshotPosition = transform.position;
            snapshotTimer    = 0f;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    /// Casts 8 rays evenly around the character and returns the direction
    /// with the longest clear path. Guarantees an escape route even when
    /// pressed into a corner — unlike random picking which may re-hit the wall.
    Vector3 FindEscapeDirection()
    {
        Vector3 origin   = transform.position + Vector3.up * 0.8f;
        Vector3 best     = -transform.forward;   // fallback: reverse
        float   bestDist = 0f;
        int     samples  = 8;

        for (int i = 0; i < samples; i++)
        {
            float   angle = i * (360f / samples);
            Vector3 dir   = Quaternion.Euler(0f, angle, 0f) * Vector3.forward;
            float   clear = lookAheadDist;

            if (Physics.Raycast(origin, dir, out RaycastHit h, lookAheadDist, obstacleMask))
            {
                if (h.collider.GetComponent<VillagerBoid>()        != null) { clear = lookAheadDist; }
                else if (h.collider.GetComponent<CharacterController>() != null) { clear = lookAheadDist; }
                else clear = h.distance;
            }

            if (clear > bestDist) { bestDist = clear; best = dir; }
        }

        return best;
    }

    // ── Flocking rules ────────────────────────────────────────────────────

    Vector3 Separation(List<VillagerBoid> neighbours)
    {
        Vector3 steer = Vector3.zero;
        int count = 0;
        foreach (var n in neighbours)
        {
            float d = Vector3.Distance(transform.position, n.transform.position);
            if (d < separationRadius && d > 0.001f)
            {
                steer += (transform.position - n.transform.position).normalized / d;
                count++;
            }
        }
        if (count > 0) steer /= count;
        return steer;
    }

    Vector3 Cohesion(List<VillagerBoid> neighbours)
    {
        if (neighbours.Count == 0) return Vector3.zero;
        Vector3 centre = Vector3.zero;
        foreach (var n in neighbours) centre += n.transform.position;
        centre /= neighbours.Count;
        return (centre - transform.position).normalized;
    }

    Vector3 Seek(Vector3 target)
    {
        Vector3 desired = target - transform.position;
        desired.y = 0f;
        if (desired.magnitude < 0.01f) return Vector3.zero;
        return (desired.normalized * maxSpeed - velocity).normalized;
    }

    /// Steer inward when approaching the ground boundary.
    Vector3 BoundaryForce()
    {
        Vector3 pos   = transform.position;
        Vector3 force = Vector3.zero;
        float   limit = boundarySize - boundaryMargin;

        // X axis
        if (pos.x >  limit) force.x -= (pos.x -  limit) / boundaryMargin * maxForce;
        if (pos.x < -limit) force.x += (-limit - pos.x) / boundaryMargin * maxForce;

        // Z axis
        if (pos.z >  limit) force.z -= (pos.z -  limit) / boundaryMargin * maxForce;
        if (pos.z < -limit) force.z += (-limit - pos.z) / boundaryMargin * maxForce;

        return force;
    }

    /// Cast a fan of rays forward — if any hits an obstacle, steer away.
    /// Returns the avoidance direction, or Vector3.zero if path is clear.
    Vector3 ObstacleAvoidance()
    {
        Vector3 forward = velocity.sqrMagnitude > 0.1f ? velocity.normalized : transform.forward;
        Vector3 origin  = transform.position + Vector3.up * 0.8f; // chest height

        int   rays      = Mathf.RoundToInt(avoidRayCount);
        float halfAngle = avoidFanAngle * 0.5f;

        for (int i = 0; i < rays; i++)
        {
            // Spread rays evenly across the fan angle
            float t     = rays == 1 ? 0f : (float)i / (rays - 1);
            float angle = Mathf.Lerp(-halfAngle, halfAngle, t);
            Vector3 dir = Quaternion.Euler(0f, angle, 0f) * forward;

            if (Physics.Raycast(origin, dir, out RaycastHit hit, lookAheadDist, obstacleMask))
            {
                // Skip other villagers and the player
                if (hit.collider.GetComponent<VillagerBoid>()     != null) continue;
                if (hit.collider.GetComponent<CharacterController>() != null) continue;

                // Steer along the surface normal (slide around the obstacle)
                Vector3 steer = hit.normal;
                steer.y = 0f;

                // Strengthen the steer the closer we are
                float proximity = 1f - (hit.distance / lookAheadDist);
                return steer.normalized * proximity;
            }
        }
        return Vector3.zero;
    }

    /// Random wandering — updates a wander target on a circle ahead of the boid.
    Vector3 Wander()
    {
        float wanderRadius   = 2f;
        float wanderDistance = 3f;
        float wanderJitter   = 1.5f;

        // Jitter the wander target randomly each frame
        wanderTarget += new Vector3(
            Random.Range(-1f, 1f) * wanderJitter,
            0f,
            Random.Range(-1f, 1f) * wanderJitter);
        wanderTarget = wanderTarget.normalized * wanderRadius;

        // Project onto a circle ahead of the boid
        Vector3 ahead = velocity.sqrMagnitude > 0.01f ? velocity.normalized : transform.forward;
        Vector3 circleCenter = ahead * wanderDistance;
        return (circleCenter + wanderTarget).normalized;
    }
}
