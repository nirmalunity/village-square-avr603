using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class NPCMovement : MonoBehaviour
{
    enum State { Patrol, Chase }

    State currentState = State.Patrol;

    [SerializeField]
    Vector3 targetLocation;

    [SerializeField]
    Transform[] waypoints;

    int index;

    CharacterController controller;
    Animator animator;
    Transform player;

    [SerializeField]
    float moveSpeed = 3f;
    [SerializeField]
    float turnSpeed = 240f;
    [SerializeField]
    float waypointRadius = 2f;
    [SerializeField]
    float chaseRadius = 5f;
    [SerializeField]
    float loseRadius = 6.5f;
    [SerializeField]
    float stopRadius = 1.5f;

    float verticalVelocity = 0f;

    void Start()
    {
        controller = GetComponent<CharacterController>();
        animator = GetComponentInChildren<Animator>();
        if (waypoints != null && waypoints.Length > 0)
            targetLocation = waypoints[0].position;
        var playerObj = GameObject.FindWithTag("Player");
        if (playerObj != null) player = playerObj.transform;
        else Debug.LogWarning("NPCMovement: No GameObject tagged 'Player' found!");
    }

    void Update()
    {
        // Apply gravity
        if (controller.isGrounded)
            verticalVelocity = -1f;
        else
            verticalVelocity += Physics.gravity.y * Time.deltaTime;

        if (player == null) { Patrol(); return; }
        float distanceToPlayer = Vector3.Distance(transform.position, player.position);

        // Switch states based on player distance
        if (distanceToPlayer < chaseRadius)
            currentState = State.Chase;
        else if (distanceToPlayer > loseRadius)
            currentState = State.Patrol;

        switch (currentState)
        {
            case State.Patrol: Patrol(); break;
            case State.Chase:  Chase();  break;
        }
    }

    void ApplyGravity()
    {
        controller.Move(Vector3.up * verticalVelocity * Time.deltaTime);
    }

    void SetSpeed(float speed)
    {
        if (animator != null)
            animator.SetFloat("Speed", speed);
    }

    void Patrol()
    {
        if (waypoints == null || waypoints.Length == 0) { SetSpeed(0); ApplyGravity(); return; }

        Vector3 direction = targetLocation - transform.position;
        direction.y = 0;

        // Reached waypoint - move to next
        if (direction.magnitude < waypointRadius)
        {
            index = (index + 1) % waypoints.Length;
            targetLocation = waypoints[index].position;
            SetSpeed(0);
            ApplyGravity();
            return;
        }

        direction.Normalize();
        Quaternion targetRotation = Quaternion.LookRotation(direction);
        transform.rotation = Quaternion.RotateTowards(transform.rotation, targetRotation, turnSpeed * Time.deltaTime);

        Vector3 move = direction * moveSpeed;
        move.y = verticalVelocity;
        controller.Move(move * Time.deltaTime);
        SetSpeed(moveSpeed);
    }

    void Chase()
    {
        Vector3 direction = player.position - transform.position;
        direction.y = 0;

        // Stop when close enough — prevents NPC gluing to player
        if (direction.magnitude < stopRadius)
        {
            SetSpeed(0);
            controller.Move(Vector3.up * verticalVelocity * Time.deltaTime);
            return;
        }

        direction.Normalize();
        Quaternion targetRotation = Quaternion.LookRotation(direction);
        transform.rotation = Quaternion.RotateTowards(transform.rotation, targetRotation, turnSpeed * Time.deltaTime);

        Vector3 move = transform.forward * moveSpeed * 1.5f;
        move.y = verticalVelocity;
        controller.Move(move * Time.deltaTime);
        SetSpeed(moveSpeed * 1.5f);
    }
}
