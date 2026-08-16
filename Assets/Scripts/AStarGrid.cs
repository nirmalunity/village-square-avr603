using UnityEngine;
using System.Collections.Generic;

public class AStarGrid : MonoBehaviour
{
    [Header("Grid Settings")]
    public int gridWidth  = 20;
    public int gridHeight = 20;
    public float nodeSize = 1f;

    [Header("Prefabs & Materials")]
    public GameObject wallPrefab;
    public Material walkableMat;
    public Material wallMat;
    public Material openMat;
    public Material closedMat;
    public Material pathMat;

    AStarNode[,] grid;
    List<GameObject> tiles = new List<GameObject>();

    void Awake()
    {
        BuildGrid();
    }

    public void BuildGrid()
    {
        // Clear old tiles
        foreach (var t in tiles) Destroy(t);
        tiles.Clear();

        grid = new AStarNode[gridWidth, gridHeight];

        for (int x = 0; x < gridWidth; x++)
        {
            for (int z = 0; z < gridHeight; z++)
            {
                Vector3 pos = new Vector3(x * nodeSize, 0, z * nodeSize);
                bool walkable = true;
                grid[x, z] = new AStarNode(x, z, walkable, pos);

                // Visual tile
                var tile = GameObject.CreatePrimitive(PrimitiveType.Cube);
                tile.transform.position  = pos;
                tile.transform.localScale = new Vector3(nodeSize * 0.95f, 0.1f, nodeSize * 0.95f);
                tile.name = $"Node_{x}_{z}";
                tile.GetComponent<Renderer>().material = walkableMat;
                // Keep collider for mouse raycasting — agent ignores tiles via layer
                tile.layer = LayerMask.NameToLayer("Default");
                tiles.Add(tile);
            }
        }
    }

    public AStarNode GetNode(int x, int z)
    {
        if (x < 0 || x >= gridWidth || z < 0 || z >= gridHeight) return null;
        return grid[x, z];
    }

    public AStarNode NodeFromWorldPos(Vector3 worldPos)
    {
        int x = Mathf.RoundToInt(worldPos.x / nodeSize);
        int z = Mathf.RoundToInt(worldPos.z / nodeSize);
        x = Mathf.Clamp(x, 0, gridWidth  - 1);
        z = Mathf.Clamp(z, 0, gridHeight - 1);
        return grid[x, z];
    }

    public List<AStarNode> GetNeighbours(AStarNode node)
    {
        var list = new List<AStarNode>();
        int[] dx = { -1, 0, 1,  0, -1, -1,  1, 1 };
        int[] dz = {  0, 1, 0, -1, -1,  1, -1, 1 };

        for (int i = 0; i < 8; i++)
        {
            var n = GetNode(node.gridX + dx[i], node.gridZ + dz[i]);
            if (n != null) list.Add(n);
        }
        return list;
    }

    public void SetWall(int x, int z, bool isWall)
    {
        if (x < 0 || x >= gridWidth || z < 0 || z >= gridHeight) return;
        grid[x, z].walkable = !isWall;
        tiles[x * gridHeight + z].GetComponent<Renderer>().material = isWall ? wallMat : walkableMat;
    }

    // Called by AStarPathfinder to colour the grid during search
    public void ColorNode(AStarNode node, string state)
    {
        var rend = tiles[node.gridX * gridHeight + node.gridZ].GetComponent<Renderer>();
        switch (state)
        {
            case "open":   rend.material = openMat;   break;
            case "closed": rend.material = closedMat; break;
            case "path":   rend.material = pathMat;   break;
            case "wall":   rend.material = wallMat;   break;
            default:       rend.material = walkableMat; break;
        }
    }

    public int GetDistance(AStarNode a, AStarNode b)
    {
        int dx = Mathf.Abs(a.gridX - b.gridX);
        int dz = Mathf.Abs(a.gridZ - b.gridZ);
        // Diagonal cost = 14, straight = 10
        return dx > dz ? 14 * dz + 10 * (dx - dz)
                       : 14 * dx + 10 * (dz - dx);
    }
}
