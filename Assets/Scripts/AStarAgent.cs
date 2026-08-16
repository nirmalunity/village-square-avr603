using UnityEngine;
using System.Collections;
using System.Collections.Generic;

/// Moves the fish agent along the A* path once it's found.
public class AStarAgent : MonoBehaviour
{
    [SerializeField] float moveSpeed = 4f;
    [SerializeField] float nodeReachDistance = 0.2f;
    [SerializeField] float heightOffset = 0.5f; // float above the grid

    AStarPathfinder pathfinder;

    void Start()
    {
        pathfinder = FindObjectOfType<AStarPathfinder>();
        pathfinder.OnPathFound += OnPathFound;
    }

    void OnPathFound(List<AStarNode> path)
    {
        StopAllCoroutines();
        StartCoroutine(FollowPath(path));
    }

    IEnumerator FollowPath(List<AStarNode> path)
    {
        foreach (var node in path)
        {
            Vector3 target = node.worldPosition + Vector3.up * heightOffset;

            while (Vector3.Distance(transform.position, target) > nodeReachDistance)
            {
                transform.position = Vector3.MoveTowards(transform.position, target, moveSpeed * Time.deltaTime);

                // Face movement direction
                Vector3 dir = (target - transform.position);
                if (dir.sqrMagnitude > 0.001f)
                    transform.rotation = Quaternion.LookRotation(dir);

                yield return null;
            }
        }
    }
}
