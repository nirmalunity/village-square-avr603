using UnityEngine;
using System.Collections;
using System.Collections.Generic;

public class AStarPathfinder : MonoBehaviour
{
    [Header("References")]
    public AStarGrid grid;
    public Transform startMarker;
    public Transform endMarker;

    [Header("Visualization")]
    public float stepDelay = 0.02f; // seconds between each search step (set 0 for instant)

    List<AStarNode> lastPath = new List<AStarNode>();

    public event System.Action<List<AStarNode>> OnPathFound;

    public void FindPath()
    {
        StopAllCoroutines();
        StartCoroutine(RunAStar());
    }

    IEnumerator RunAStar()
    {
        AStarNode start = grid.NodeFromWorldPos(startMarker.position);
        AStarNode end   = grid.NodeFromWorldPos(endMarker.position);

        // Reset grid colours (except walls)
        for (int x = 0; x < grid.gridWidth; x++)
            for (int z = 0; z < grid.gridHeight; z++)
            {
                var n = grid.GetNode(x, z);
                grid.ColorNode(n, n.walkable ? "none" : "wall");
                n.gCost = 0; n.hCost = 0; n.parent = null;
            }

        var openSet   = new List<AStarNode> { start };
        var closedSet = new HashSet<AStarNode>();

        while (openSet.Count > 0)
        {
            // Pick node with lowest fCost (ties broken by hCost)
            AStarNode current = openSet[0];
            foreach (var n in openSet)
                if (n.fCost < current.fCost || (n.fCost == current.fCost && n.hCost < current.hCost))
                    current = n;

            openSet.Remove(current);
            closedSet.Add(current);

            if (current != start && current != end)
                grid.ColorNode(current, "closed");

            if (current == end)
            {
                lastPath = RetracePath(start, end);
                foreach (var n in lastPath)
                    if (n != start && n != end)
                        grid.ColorNode(n, "path");

                OnPathFound?.Invoke(lastPath);
                yield break;
            }

            foreach (var neighbour in grid.GetNeighbours(current))
            {
                if (!neighbour.walkable || closedSet.Contains(neighbour)) continue;

                int newG = current.gCost + grid.GetDistance(current, neighbour);
                if (newG < neighbour.gCost || !openSet.Contains(neighbour))
                {
                    neighbour.gCost  = newG;
                    neighbour.hCost  = grid.GetDistance(neighbour, end);
                    neighbour.parent = current;

                    if (!openSet.Contains(neighbour))
                    {
                        openSet.Add(neighbour);
                        if (neighbour != end)
                            grid.ColorNode(neighbour, "open");
                    }
                }
            }

            if (stepDelay > 0) yield return new WaitForSeconds(stepDelay);
        }

        Debug.LogWarning("A*: No path found!");
    }

    List<AStarNode> RetracePath(AStarNode start, AStarNode end)
    {
        var path = new List<AStarNode>();
        AStarNode current = end;
        while (current != start)
        {
            path.Add(current);
            current = current.parent;
        }
        path.Add(start);
        path.Reverse();
        return path;
    }

    public List<AStarNode> GetLastPath() => lastPath;
}
