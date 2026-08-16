using UnityEngine;

public class AStarNode
{
    public int gridX, gridZ;
    public bool walkable;
    public Vector3 worldPosition;

    public int gCost; // cost from start
    public int hCost; // heuristic cost to end
    public int fCost => gCost + hCost;

    public AStarNode parent;

    public AStarNode(int x, int z, bool walkable, Vector3 worldPos)
    {
        gridX = x;
        gridZ = z;
        this.walkable = walkable;
        worldPosition = worldPos;
    }
}
