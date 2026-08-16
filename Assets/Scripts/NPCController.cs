using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class NPCController : MonoBehaviour
{
    [SerializeField]
    float moveSpeed = 3f;
    [SerializeField]
    float turnSpeed = 120f;
    [SerializeField]
    float waypointRadius = 1f;
    [SerializeField]
    Transform[] waypoints;

    CharacterController controller;
    int currentWaypoint = 0;

    // Start is called before the first frame update
    void Start()
    {
        controller = GetComponent<CharacterController>();
    }

    // Update is called once per frame
    void Update()
    {
        if (waypoints == null || waypoints.Length == 0) return;

        Transform target = waypoints[currentWaypoint];
        Vector3 direction = target.position - transform.position;
        direction.y = 0;

        // Move to next waypoint when close enough
        if (direction.magnitude < waypointRadius)
        {
            currentWaypoint = (currentWaypoint + 1) % waypoints.Length;
            return;
        }

        direction.Normalize();

        // Rotate toward waypoint
        Quaternion targetRotation = Quaternion.LookRotation(direction);
        transform.rotation = Quaternion.RotateTowards(transform.rotation, targetRotation, turnSpeed * Time.deltaTime);

        // Move forward
        controller.Move(transform.forward * moveSpeed * Time.deltaTime);
    }
}
