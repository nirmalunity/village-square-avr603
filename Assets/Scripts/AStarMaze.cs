using UnityEngine;
using System.Collections;
using System.Collections.Generic;

/// Attach to the same GameObject as Recursive (or Maze).
/// After the maze generates, press SPACE to run A* from Start to End.
/// The fish agent then swims the path.
public class AStarMaze : MonoBehaviour
{
    [Header("Start / End (grid coords)")]
    public int startX = 1;
    public int startZ = 1;
    public int endX   = 25;
    public int endZ   = 25;

    [Header("Agent")]
    public GameObject agentPrefab;
    public float      agentSpeed = 10f;

    [Header("Visuals")]
    public Material startMat;        // Green
    public Material endMat;          // Red
    public Material pathMat;         // Yellow
    public Material openMat;         // Blue  — nodes in open set
    public Material closedMat;       // Grey  — nodes already evaluated

    [Header("Speed")]
    [Tooltip("Seconds between each A* step (lower = faster)")]
    public float stepDelay = 0.03f;

    Maze            maze;
    GameObject      agentObj;
    List<Vector3>   path = new List<Vector3>();
    Coroutine       moveCoroutine;

    // Visualisation tiles created during search — keyed by "x,z"
    Dictionary<string, GameObject> vizTiles = new Dictionary<string, GameObject>();

    // ── Internal node for A* ─────────────────────────────────────────────
    class Node
    {
        public int x, z;
        public int g, h;
        public int f => g + h;
        public Node parent;
        public Node(int x, int z) { this.x = x; this.z = z; }
    }

    void Start()
    {
        maze = GetComponent<Maze>();
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.Space))
            StartCoroutine(RunAStar());
    }

    IEnumerator RunAStar()
    {
        // Stop only the movement coroutine — not this one
        if (moveCoroutine != null) { StopCoroutine(moveCoroutine); moveCoroutine = null; }

        // Wait a frame so maze has fully generated
        yield return null;

        path.Clear();

        // Snap to nearest corridor cell if Inspector values land on a wall
        var s = FindNearestCorridor(startX, startZ);
        var e = FindNearestCorridor(endX,   endZ);
        if (s == null) { Debug.LogError("No corridor found near Start!"); yield break; }
        if (e == null) { Debug.LogError("No corridor found near End!");   yield break; }
        startX = s.Value.x; startZ = s.Value.y;
        endX   = e.Value.x; endZ   = e.Value.y;

        // Clear old viz tiles from previous run
        foreach (var t in vizTiles.Values) if (t) Destroy(t);
        vizTiles.Clear();

        PlaceMarker(startX, startZ, startMat, "Start");
        PlaceMarker(endX,   endZ,   endMat,   "End");

        // A* ──────────────────────────────────────────────────────────────
        var open   = new List<Node>();
        var closed = new HashSet<string>();

        var startNode = new Node(startX, startZ);
        startNode.h = Heuristic(startX, startZ, endX, endZ);
        open.Add(startNode);
        SetVizTile(startX, startZ, openMat);

        Node endNode = null;

        while (open.Count > 0)
        {
            // Pick lowest f (ties broken by h)
            Node current = open[0];
            foreach (var n in open)
                if (n.f < current.f || (n.f == current.f && n.h < current.h))
                    current = n;

            open.Remove(current);
            closed.Add(Key(current.x, current.z));

            // Mark current as closed (red)
            SetVizTile(current.x, current.z, closedMat);

            if (current.x == endX && current.z == endZ)
            {
                endNode = current;
                break;
            }

            foreach (var (nx, nz) in Neighbours(current.x, current.z))
            {
                if (closed.Contains(Key(nx, nz))) continue;
                if (maze.map[nx, nz] == 1) continue;  // wall

                int newG = current.g + 10;
                var existing = open.Find(n => n.x == nx && n.z == nz);
                if (existing == null)
                {
                    var newNode = new Node(nx, nz) { g = newG, h = Heuristic(nx, nz, endX, endZ), parent = current };
                    open.Add(newNode);
                    // Mark newly discovered node as open (blue)
                    SetVizTile(nx, nz, openMat);
                }
                else if (newG < existing.g)
                {
                    existing.g = newG;
                    existing.parent = current;
                }
            }

            // Pause so the user can watch the search spread
            yield return new WaitForSeconds(stepDelay);
        }

        if (endNode == null) { Debug.LogWarning("No path found!"); yield break; }

        // Retrace path ────────────────────────────────────────────────────
        var node = endNode;
        while (node != null)
        {
            path.Insert(0, GridToWorld(node.x, node.z));
            node = node.parent;
        }

        // Draw path tiles (yellow), one by one for a nice reveal ──────────
        foreach (var wp in path)
        {
            int gx = Mathf.RoundToInt(wp.x / maze.scale);
            int gz = Mathf.RoundToInt(wp.z / maze.scale);
            SetVizTile(gx, gz, pathMat);
            yield return new WaitForSeconds(stepDelay * 0.5f);
        }

        // Spawn / move agent ──────────────────────────────────────────────
        // Destroy old agent so it resets to start on each Space press
        if (agentObj != null) { Destroy(agentObj); agentObj = null; }

        // Spawn above the corridor floor (walls are scale units tall, centred at Y=0,
        // so the top of a corridor is at Y = scale*0.5)
        Vector3 spawnPos = path[0] + Vector3.up * (maze.scale * 0.5f);

        if (agentPrefab != null)
        {
            agentObj = Instantiate(agentPrefab, spawnPos, Quaternion.identity);
            agentObj.transform.localScale = new Vector3(10f, 10f, 10f);

            Debug.Log("Fish spawned at " + spawnPos);
        }
        else
        {
            // Bright visible sphere fallback
            agentObj = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            agentObj.name = "Agent";
            var mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            mat.color = Color.cyan;
            agentObj.GetComponent<Renderer>().material = mat;
            agentObj.transform.localScale = Vector3.one * (maze.scale * 0.5f);
            Destroy(agentObj.GetComponent<Collider>());
            agentObj.transform.position = spawnPos;
            Debug.Log("No fish prefab found — using cyan sphere at " + spawnPos);
        }

        moveCoroutine = StartCoroutine(MoveAgent(new List<Vector3>(path)));
    }

    IEnumerator MoveAgent(List<Vector3> waypoints)
    {
        float height = maze.scale * 0.5f;
        foreach (var wp in waypoints)
        {
            Vector3 target = wp + Vector3.up * height;
            while (Vector3.Distance(agentObj.transform.position, target) > 0.2f)
            {
                agentObj.transform.position = Vector3.MoveTowards(
                    agentObj.transform.position, target, agentSpeed * Time.deltaTime);
                Vector3 dir = target - agentObj.transform.position;
                if (dir.sqrMagnitude > 0.01f)
                    agentObj.transform.rotation = Quaternion.LookRotation(dir);
                yield return null;
            }
        }
        Debug.Log("Agent reached the End!");
    }

    void PlaceMarker(int x, int z, Material mat, string label)
    {
        // Remove old marker with same name
        var old = GameObject.Find(label + "Marker");
        if (old) Destroy(old);

        var prefabPath = label == "Start"
            ? "Assets/Models/Prefabs/Start.prefab"
            : "Assets/Models/Prefabs/End.prefab";

        var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        go.name = label + "Marker";
        go.transform.position   = GridToWorld(x, z) + Vector3.up * maze.scale * 0.5f;
        go.transform.localScale = new Vector3(maze.scale * 0.4f, maze.scale * 0.5f, maze.scale * 0.4f);
        go.GetComponent<Renderer>().material = mat;
        Destroy(go.GetComponent<Collider>());
    }

    Vector3 GridToWorld(int x, int z) => new Vector3(x * maze.scale, 0, z * maze.scale);

    string Key(int x, int z) => x + "," + z;

    int Heuristic(int x1, int z1, int x2, int z2) =>
        (Mathf.Abs(x1 - x2) + Mathf.Abs(z1 - z2)) * 10;

    IEnumerable<(int, int)> Neighbours(int x, int z)
    {
        if (x > 0)              yield return (x - 1, z);
        if (x < maze.width - 1) yield return (x + 1, z);
        if (z > 0)              yield return (x, z - 1);
        if (z < maze.depth - 1) yield return (x, z + 1);
    }

    /// Create or recolour a flat visualisation tile at grid position (x,z).
    void SetVizTile(int x, int z, Material mat)
    {
        string key = Key(x, z);
        if (!vizTiles.TryGetValue(key, out GameObject tile) || tile == null)
        {
            tile = GameObject.CreatePrimitive(PrimitiveType.Plane);
            tile.name = "Viz_" + key;
            tile.transform.position   = GridToWorld(x, z) + Vector3.up * 0.2f;
            tile.transform.localScale = Vector3.one * (maze.scale / 10f);
            Destroy(tile.GetComponent<Collider>());
            vizTiles[key] = tile;
        }
        if (mat != null) tile.GetComponent<Renderer>().material = mat;
    }

    /// BFS outward from (px, pz) to find the nearest corridor cell (map==0).
    Vector2Int? FindNearestCorridor(int px, int pz)
    {
        var visited = new HashSet<string>();
        var queue   = new Queue<Vector2Int>();
        var start   = new Vector2Int(px, pz);
        queue.Enqueue(start);
        visited.Add(px + "," + pz);

        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            if (maze.map[cur.x, cur.y] == 0) return cur;

            int[] dx = { 1, -1, 0, 0 };
            int[] dz = { 0, 0, 1, -1 };
            for (int i = 0; i < 4; i++)
            {
                int nx = cur.x + dx[i], nz = cur.y + dz[i];
                if (nx < 0 || nx >= maze.width || nz < 0 || nz >= maze.depth) continue;
                string k = nx + "," + nz;
                if (!visited.Contains(k)) { visited.Add(k); queue.Enqueue(new Vector2Int(nx, nz)); }
            }
        }
        return null;
    }
}
