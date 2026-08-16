using UnityEngine;
using UnityEngine.AI;

public class PlayerController : MonoBehaviour
{
    [SerializeField] float runSpeed         = 5f;
    [SerializeField] float mouseSensitivity = 200f;
    [SerializeField] float boundaryMargin   = 1f;

    [Header("Attack")]
    [SerializeField] float attackCooldown   = 1f;
    [SerializeField] float attackDelay      = 0.3f;
    [SerializeField] float attackRadius     = 2f;
    [SerializeField] float attackRange      = 2f;

    [Header("Health")]
    [SerializeField] int   maxHealth        = 100;
    [SerializeField] float invincibleTime   = 1.2f;  // seconds of immunity after a hit

    // ── Public read-only health for GameManager / HUD ─────────────────────────
    public int Health    { get; private set; }
    public int MaxHealth => maxHealth;

    CharacterController controller;
    Animator            animator;
    NavMeshObstacle     obstacle;

    float verticalVelocity = 0f;
    float boundarySize     = 37f;
    float lastAttackTime   = -99f;
    bool  isAttacking      = false;
    bool  inputDisabled    = false;

    float lastHitTime      = -99f;  // for invincibility frames

    // ── Bot control (used by TrainingPlayerBot) ───────────────────────────────
    public bool BotControlled { get; set; } = false;
    float botVerticalVelocity = -1f;

    // ── Stuck detection ───────────────────────────────────────────────────────
    Vector3 snapshotPosition;
    float   snapshotTimer;

    // ─────────────────────────────────────────────────────────────────────────
    void Start()
    {
        Health           = maxHealth;
        snapshotPosition = transform.position;

        controller = GetComponent<CharacterController>();
        // 0.1 was too low — cart wheels, plank lips and kerbs became unclimbable
        // walls, which is a common cause of "stuck running in place".
        controller.stepOffset = 0.4f;
        controller.center     = new Vector3(0f, controller.center.y, 0f);
        if (controller.skinWidth >= controller.radius)
            controller.skinWidth = controller.radius * 0.1f;

        var ground = GameObject.Find("Ground");
        if (ground != null)
            boundarySize = ground.transform.localScale.x * 5f - boundaryMargin;

        animator = GetComponentInChildren<Animator>();
        if (animator != null) animator.applyRootMotion = false;

        // NavMeshObstacle — zombies dynamically path around the hero
        obstacle = GetComponent<NavMeshObstacle>();
        if (obstacle == null) obstacle = gameObject.AddComponent<NavMeshObstacle>();
        obstacle.carving             = true;
        obstacle.shape               = NavMeshObstacleShape.Capsule;
        obstacle.radius              = 0.5f;
        obstacle.height              = 1.8f;
        obstacle.carveOnlyStationary = false;

        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible   = false;
    }

    void Update()
    {
        if (inputDisabled) return;
        HandleAttack();
        HandleMovement();
        EnforceBoundary();
        HandleStuck();
    }

    // ── Combat — called by NPCNavMesh on contact ──────────────────────────────

    public bool TakeDamage(int amount)
    {
        if (inputDisabled) return false;                          // already dead
        if (Time.time - lastHitTime < invincibleTime) return false;  // invincibility frames

        lastHitTime = Time.time;
        Health      = Mathf.Max(0, Health - amount);
        AudioManager.Instance?.PlayHeroHurt();
        Debug.Log("[Hero] Hit! HP: " + Health + "/" + maxHealth);
        return true;
    }

    /// Called by GameManager when win/lose triggers — freezes player input.
    public void DisableInput()
    {
        inputDisabled = true;
        if (animator != null) animator.SetFloat("Speed", 0f);
    }

    /// Called by GameManager in training mode — restores hero to full HP
    /// so the game never ends and zombies keep collecting training data.
    public void RestoreHealth()
    {
        Health = maxHealth;
        Debug.Log("[Hero] Training mode — health restored to full.");
    }

    // ── Attack ────────────────────────────────────────────────────────────────

    void HandleAttack()
    {
        if (!Input.GetMouseButtonDown(0)) return;
        if (Time.time - lastAttackTime < attackCooldown) return;

        lastAttackTime = Time.time;
        isAttacking    = true;

        AudioManager.Instance?.PlayHeroAttack();
        if (animator != null) animator.SetTrigger("Attack");

        Invoke(nameof(DoAttackSphere), attackDelay);
        Invoke(nameof(ClearAttack),    attackCooldown * 0.8f);
    }

    void DoAttackSphere()
    {
        Vector3 origin = transform.position
                       + transform.forward * attackRange
                       + Vector3.up * 1f;

        foreach (var col in Physics.OverlapSphere(origin, attackRadius))
        {
            var zombie = col.GetComponent<NPCNavMesh>()
                      ?? col.GetComponentInParent<NPCNavMesh>();
            zombie?.TakeHit();
        }
    }

    void ClearAttack() => isAttacking = false;

    // ── Movement ──────────────────────────────────────────────────────────────

    void HandleMovement()
    {
        if (BotControlled) return;   // TrainingPlayerBot drives movement via BotMove()

        float turn = Input.GetAxis("Mouse X") * mouseSensitivity;
        transform.Rotate(Vector3.up * turn);

        float v = isAttacking ? 0f : Input.GetAxisRaw("Vertical");

        if (controller.isGrounded) verticalVelocity = -1f;
        else verticalVelocity += Physics.gravity.y * Time.deltaTime;

        Vector3 move = transform.forward * v * runSpeed;
        move.y = verticalVelocity;
        controller.Move(move * Time.deltaTime);

        if (animator != null)
            animator.SetFloat("Speed", Mathf.Abs(v));
    }

    /// Called by TrainingPlayerBot — moves the player using CharacterController.
    public void BotMove(Vector3 worldDirection, bool facingDir = true, float speedScale = 1f)
    {
        worldDirection.y = 0f;

        if (controller.isGrounded) botVerticalVelocity = -1f;
        else botVerticalVelocity += Physics.gravity.y * Time.deltaTime;

        float speed = worldDirection.magnitude > 0.1f
            ? runSpeed * Mathf.Clamp01(speedScale)
            : 0f;
        Vector3 move = worldDirection.normalized * speed;
        move.y = botVerticalVelocity;
        controller.Move(move * Time.deltaTime);

        if (facingDir && worldDirection.sqrMagnitude > 0.01f)
            transform.rotation = Quaternion.Slerp(transform.rotation,
                                  Quaternion.LookRotation(worldDirection.normalized), 8f * Time.deltaTime);

        if (animator != null)
            animator.SetFloat("Speed", speed > 0.1f ? 1f : 0f);
    }

    // ── Stuck detection ───────────────────────────────────────────────────────

    void HandleStuck()
    {
        snapshotTimer += Time.deltaTime;
        if (snapshotTimer < 0.6f) return;

        bool hasInput = Mathf.Abs(Input.GetAxisRaw("Vertical")) > 0.1f || BotControlled;
        if (hasInput)
        {
            float moved = Vector3.Distance(transform.position, snapshotPosition);
            if (moved < 0.08f)  // barely moved despite input — wedged in geometry
            {
                // Sweep all 16 directions and move toward the most open one
                Vector3 escape = MovementUtil.FindClearDirection(
                    transform.position, transform.forward, 5f, boundarySize);
                controller.Move(escape * 0.5f);
            }
        }

        snapshotPosition = transform.position;
        snapshotTimer    = 0f;
    }

    // ── Boundary ──────────────────────────────────────────────────────────────

    void EnforceBoundary()
    {
        Vector3 pos = transform.position;
        pos.x = Mathf.Clamp(pos.x, -boundarySize, boundarySize);
        pos.z = Mathf.Clamp(pos.z, -boundarySize, boundarySize);
        transform.position = pos;
    }

    // ── Gizmos ────────────────────────────────────────────────────────────────

    void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(1f, 0.3f, 0.3f, 0.4f);
        Vector3 o = transform.position + transform.forward * attackRange + Vector3.up * 1f;
        Gizmos.DrawWireSphere(o, attackRadius);
    }
}
