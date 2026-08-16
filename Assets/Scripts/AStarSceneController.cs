using UnityEngine;

/// Master controller for the A* scene.
/// Press SPACE to run A* and move the fish.
/// Click any tile to toggle wall on/off, then press SPACE again.
public class AStarSceneController : MonoBehaviour
{
    public AStarGrid       grid;
    public AStarPathfinder pathfinder;
    public Camera          cam;

    void Update()
    {
        // SPACE = run pathfinding
        if (Input.GetKeyDown(KeyCode.Space))
            pathfinder.FindPath();

        // Left-click = toggle wall on tile
        if (Input.GetMouseButtonDown(0))
        {
            Ray ray = cam.ScreenPointToRay(Input.mousePosition);
            if (Physics.Raycast(ray, out RaycastHit hit))
            {
                string[] parts = hit.collider.name.Split('_');
                if (parts.Length == 3 && parts[0] == "Node")
                {
                    int x = int.Parse(parts[1]);
                    int z = int.Parse(parts[2]);
                    var node = grid.GetNode(x, z);
                    grid.SetWall(x, z, node.walkable); // toggle
                }
            }
        }
    }
}
