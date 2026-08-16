using UnityEngine;
using System.Text;

public class ZombieQLearner : MonoBehaviour
{
    public static ZombieQLearner Instance { get; private set; }

    [Header("Q-Learning Hyperparameters")]
    [Tooltip("How aggressively Q-values update (0 = never, 1 = instant overwrite)")]
    [SerializeField] float learningRate          = 0.6f;
    [Tooltip("How much future rewards are discounted (0 = myopic, 1 = far-sighted)")]
    [SerializeField] float discountFactor        = 0.8f;

    [Header("Exploration Schedule")]
    [Tooltip("Starting probability of picking a random action (1 = fully random)")]
    [SerializeField] float epsilonStart          = 0.85f;
    [Tooltip("Floor — will never explore less than this fraction")]
    [SerializeField] float epsilonMin            = 0.10f;
    [Tooltip("Epsilon shrinks by this amount every real-time second during play")]
    [SerializeField] float epsilonDecayPerSecond = 0.0015f;  // 2-min session → drops ~0.18
    [Tooltip("Each completed session permanently reduces starting epsilon by this amount")]
    [SerializeField] float epsilonDecayPerSession = 0.07f;   // reaches floor after ~10 sessions

    // ── State / action space ──────────────────────────────────────────────────
    // State axes
    const int HP_BUCKETS       = 3;  // player HP:      low(0)  mid(1)  high(2)
    const int DIST_BUCKETS     = 3;  // dist to player: close(0) medium(1) far(2)
    const int PRESSURE_BUCKETS = 2;  // zombie's own hit pressure: low(0)  high(1)

    public const int NUM_STATES  = HP_BUCKETS * DIST_BUCKETS * PRESSURE_BUCKETS; // 18
    public const int NUM_ACTIONS = 4;

    public enum Action
    {
        Chase          = 0,   // charge the player directly
        Flank          = 1,   // arc around and attack from the side
        RetreatVillager = 2,  // disengage hero and go for a villager instead
        GroupUp        = 3    // move toward the nearest living zombie
    }

    // ── Q-table ───────────────────────────────────────────────────────────────
    float[,] Q = new float[NUM_STATES, NUM_ACTIONS];

    // ── Runtime state ─────────────────────────────────────────────────────────
    float epsilon;
    int   sessionCount;

    // ── Public read-outs used by HUD ──────────────────────────────────────────
    public float Epsilon      => epsilon;
    public int   SessionCount => sessionCount;

    // ─────────────────────────────────────────────────────────────────────────
    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        Load();
    }

    void Update()
    {
        // Exploration decays in real time during gameplay
        epsilon = Mathf.Max(epsilonMin, epsilon - epsilonDecayPerSecond * Time.deltaTime);
    }

    void OnApplicationQuit() => Save();
    void OnDisable()         => Save();

    // ── State encoding ────────────────────────────────────────────────────────

    /// Maps continuous game values into a single integer state index.
    public int EncodeState(float playerHpRatio, float distToPlayer, float zombieHitPressure)
    {
        int h = playerHpRatio     < 0.33f ? 0 : playerHpRatio < 0.66f ? 1 : 2;
        int d = distToPlayer      < 3.5f  ? 0 : distToPlayer  < 9f    ? 1 : 2;
        int p = zombieHitPressure < 0.35f ? 0 : 1;
        return h * (DIST_BUCKETS * PRESSURE_BUCKETS) + d * PRESSURE_BUCKETS + p;
    }

    // ── Action selection ──────────────────────────────────────────────────────

    /// Epsilon-greedy: explore randomly or exploit the best known action.
    public Action SelectAction(int state)
    {
        return (Random.value < epsilon)
            ? (Action)Random.Range(0, NUM_ACTIONS)
            : BestAction(state);
    }

    public Action BestAction(int state)
    {
        int best = 0;
        for (int a = 1; a < NUM_ACTIONS; a++)
            if (Q[state, a] > Q[state, best]) best = a;
        return (Action)best;
    }

    float MaxQ(int state)
    {
        float best = Q[state, 0];
        for (int a = 1; a < NUM_ACTIONS; a++)
            if (Q[state, a] > best) best = Q[state, a];
        return best;
    }

    // ── Bellman update ────────────────────────────────────────────────────────

    /// Update Q-value after observing a reward.
    /// Call this from NPCNavMesh whenever a meaningful game event occurs.
    public void Learn(int prevState, Action action, float reward, int nextState)
    {
        int   a      = (int)action;
        float target = reward + discountFactor * MaxQ(nextState);
        Q[prevState, a] += learningRate * (target - Q[prevState, a]);
    }

    // ── Persistence ───────────────────────────────────────────────────────────

    void Save()
    {
        var sb = new StringBuilder();
        for (int s = 0; s < NUM_STATES;  s++)
        for (int a = 0; a < NUM_ACTIONS; a++)
            sb.Append(Q[s, a].ToString("F5")).Append(',');

        PlayerPrefs.SetString("VillageSquare_QTable",   sb.ToString());
        PlayerPrefs.SetInt   ("VillageSquare_Sessions", sessionCount + 1);
        PlayerPrefs.Save();
        Debug.Log($"[ZombieQLearner] Saved. Session {sessionCount + 1} complete. ε={epsilon:F2}");
    }

    void Load()
    {
        sessionCount = PlayerPrefs.GetInt("VillageSquare_Sessions", 0);

        // Each past session permanently lowers starting epsilon
        epsilon = Mathf.Max(epsilonMin,
                            epsilonStart - sessionCount * epsilonDecayPerSession);

        // ── 1. Try PlayerPrefs first (session-specific learning) ──────────────
        string raw = PlayerPrefs.GetString("VillageSquare_QTable", "");
        if (!string.IsNullOrEmpty(raw))
        {
            string[] parts = raw.Split(',');
            int idx = 0;
            for (int s = 0; s < NUM_STATES  && idx + 1 < parts.Length; s++)
            for (int a = 0; a < NUM_ACTIONS && idx + 1 < parts.Length; a++, idx++)
                if (float.TryParse(parts[idx], out float v))
                    Q[s, a] = v;

            Debug.Log($"[ZombieQLearner] Loaded session Q-table (session {sessionCount + 1}). ε={epsilon:F2}");
            return;
        }

        // ── 2. Fall back to pre-trained table from StreamingAssets ───────────
        string path = System.IO.Path.Combine(Application.streamingAssetsPath, "qtable_pretrained.txt");
        if (System.IO.File.Exists(path))
        {
            string[] lines = System.IO.File.ReadAllLines(path);
            for (int s = 0; s < NUM_STATES && s < lines.Length; s++)
            {
                string[] vals = lines[s].Trim().Split(new char[]{' ','\t'},
                                System.StringSplitOptions.RemoveEmptyEntries);
                for (int a = 0; a < NUM_ACTIONS && a < vals.Length; a++)
                    if (float.TryParse(vals[a], out float v))
                        Q[s, a] = v;
            }
            Debug.Log($"[ZombieQLearner] Loaded PRE-TRAINED Q-table from StreamingAssets. Session {sessionCount + 1}. ε={epsilon:F2}");
        }
        else
        {
            Debug.Log($"[ZombieQLearner] No Q-table found — starting from scratch. ε={epsilon:F2}");
        }
    }

    /// Wipe all saved learning — useful for resetting demos.
    [ContextMenu("Reset Q-Table (Debug)")]
    public void ResetQTable()
    {
        PlayerPrefs.DeleteKey("VillageSquare_QTable");
        PlayerPrefs.DeleteKey("VillageSquare_Sessions");
        PlayerPrefs.Save();
        Q            = new float[NUM_STATES, NUM_ACTIONS];
        sessionCount = 0;
        epsilon      = epsilonStart;
        Debug.Log("[ZombieQLearner] Q-table wiped. Starting fresh.");
    }
}
