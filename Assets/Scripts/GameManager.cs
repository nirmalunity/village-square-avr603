using UnityEngine;
using UnityEngine.SceneManagement;

public class GameManager : MonoBehaviour
{
    public static GameManager Instance { get; private set; }

    [Header("Rules")]
    [Tooltip("Seconds the hero must survive to win")]
    public float surviveDuration = 120f;

    [Header("ML-Agents Training Mode")]
    [Tooltip("Enable during PPO training: skips dialogue, disables win/lose, restores hero HP on death")]
    public bool trainingMode = false;

    // ── Game state ────────────────────────────────────────────────────────────
    public enum State { Dialogue, Playing, Won, Lost }
    public State CurrentState { get; private set; } = State.Dialogue;

    float            elapsed           = 0f;
    PlayerController hero;

    // Villager tracking
    int startingVillagers = 0;
    int villagersAlive    = 0;

    // Zombie tracking
    int  zombiesInScene    = 0;
    bool hadZombies        = false;   // prevents false win before first zombie spawns
    bool zombiesClearedWin = false;
    public bool AllZombiesDefeated { get; private set; } = false;

    // ── GUI styles (built lazily) ─────────────────────────────────────────────
    GUIStyle styleLabel;
    GUIStyle styleBig;
    GUIStyle styleBox;
    GUIStyle styleSmall;
    bool     stylesReady = false;

    // ─────────────────────────────────────────────────────────────────────────
    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    void Start()
    {
        var heroObj = GameObject.FindWithTag("Player");
        if (heroObj != null) hero = heroObj.GetComponent<PlayerController>();
        else Debug.LogWarning("[GameManager] No Player tag found.");

        // Pre-count villagers so HUD shows correct number from the start
        startingVillagers = FindObjectsOfType<VillagerBoid>().Length;
        villagersAlive    = startingVillagers;

        // Training mode or no QuestGiver → skip dialogue, start immediately
        if (trainingMode || FindObjectOfType<QuestGiver>() == null)
        {
            Debug.Log(trainingMode
                ? "[GameManager] Training mode — skipping dialogue, disabling win/lose."
                : "[GameManager] No QuestGiver found — auto-starting game.");
            StartGame();
        }
    }

    // ── Called by QuestGiver when all dialogue has been read ──────────────────

    public void StartGame()
    {
        if (CurrentState != State.Dialogue) return;
        CurrentState = State.Playing;

        // Snapshot villager count at the moment the game begins
        startingVillagers = FindObjectsOfType<VillagerBoid>().Length;
        villagersAlive    = startingVillagers;

        // The wave spawner owns population. Remove legacy perimeter-placed NPCs
        // before counting so the real game starts with an actual first wave.
        ZombieWaveSpawner.Instance?.PrepareForGame();

        // Count any zombies that intentionally remain in the scene
        zombiesInScene = FindObjectsOfType<NPCNavMesh>().Length;
        hadZombies = zombiesInScene > 0;

        // Tell the wave spawner to begin (safe if not in scene)
        ZombieWaveSpawner.Instance?.BeginSpawning();

        Debug.Log($"[GameManager] Game started — {startingVillagers} villagers to protect, {surviveDuration}s timer.");
    }

    void Update()
    {
        if (CurrentState != State.Playing) return;
        if (hero == null) return;

        elapsed += Time.deltaTime;

        // ── Training mode: no win/lose, infinite waves ───────────────────────
        if (trainingMode)
        {
            // Restore well before 0 — a permanently low-HP bot sits in emergency
            // retreat forever, which starves the model of real combat experience.
            if (hero.Health <= hero.MaxHealth * 0.4f)
                hero.RestoreHealth();

            // The moment all zombies die, immediately restart spawning
            if (hadZombies && zombiesInScene <= 0)
            {
                AllZombiesDefeated = false;
                hadZombies         = false;
                if (ZombieWaveSpawner.Instance != null)
                {
                    ZombieWaveSpawner.Instance.RestartSpawning();
                    Debug.Log("[GameManager] Training: wave cleared — RestartSpawning called.");
                }
                else
                {
                    Debug.LogError("[GameManager] Training: ZombieWaveSpawner.Instance is NULL — add it to the scene!");
                }
            }
            return;
        }

        // ── Lose conditions ───────────────────────────────────────────────────
        if (hero.Health <= 0)
        {
            EndGame(false, "hero");
            return;
        }

        if (startingVillagers > 0 && villagersAlive <= 0)
        {
            EndGame(false, "villagers");
            return;
        }

        // ── Win condition ─────────────────────────────────────────────────────
        if (elapsed >= surviveDuration)
            EndGame(true, "");
    }

    void EndGame(bool won, string reason)
    {
        if (CurrentState == State.Won || CurrentState == State.Lost) return; // guard double-trigger
        CurrentState = won ? State.Won : State.Lost;
        hero?.DisableInput();
        if (won) AudioManager.Instance?.PlayGameWin();
        else     AudioManager.Instance?.PlayGameLose();

        string msg = won
            ? $"YOU WIN — survived {surviveDuration}s, {villagersAlive}/{startingVillagers} villagers safe."
            : reason == "hero" ? "GAME OVER — hero defeated." : "GAME OVER — all villagers slain.";

        Debug.Log("[GameManager] " + msg);

        // Rebuild styles so the big headline gets the right color
        stylesReady = false;
    }

    // ── Death callbacks (called by NPCNavMesh / VillagerBoid) ─────────────────

    public void OnZombieSpawned()
    {
        zombiesInScene++;
        hadZombies = true;
    }

    public void OnZombieDied()
    {
        zombiesInScene = Mathf.Max(0, zombiesInScene - 1);
        if (CurrentState == State.Playing && hadZombies && zombiesInScene <= 0)
            AllZombiesDefeated = true;   // signal QuestGiver — win triggers after elder dialogue
    }

    /// Called by QuestGiver after the victory congratulation dialogue finishes.
    public void TriggerWin()
    {
        if (trainingMode)
        {
            // In training mode: don't end the game — reset and spawn a new wave
            AllZombiesDefeated = false;
            hadZombies         = false;
            zombiesInScene     = 0;
            ZombieWaveSpawner.Instance?.RestartSpawning();
            Debug.Log("[GameManager] Training: all zombies cleared — restarting waves.");
            return;
        }
        zombiesClearedWin = true;
        EndGame(true, "zombies");
    }

    public void OnVillagerDied()
    {
        villagersAlive = Mathf.Max(0, villagersAlive - 1);
        Debug.Log($"[GameManager] Villager died — {villagersAlive}/{startingVillagers} remaining.");

        // Immediate lose check so the screen shows the right cause
        if (CurrentState == State.Playing && villagersAlive <= 0)
            EndGame(false, "villagers");
    }

    // ── HUD ───────────────────────────────────────────────────────────────────

    void OnGUI()
    {
        BuildStyles();

        // Always draw the top bar so we can see it during all phases
        DrawTopBar();

        switch (CurrentState)
        {
            case State.Won:
            case State.Lost:
                DrawEndScreen();
                break;
        }
    }

    void DrawTopBar()
    {
        if (hero == null) return;

        float barH = 40f;
        float pad  = 8f;
        float sw   = Screen.width;

        // Full-width dark strip
        GUI.color = new Color(0f, 0f, 0f, 0.70f);
        GUI.Box(new Rect(0, 0, sw, barH), GUIContent.none, styleBox);
        GUI.color = Color.white;

        float hpW    = 220f;
        float secW   = 180f;
        float timerW = 160f;

        float hpX       = pad;
        float villagerX = hpX + hpW + pad;
        float zombieX   = villagerX + secW;
        float timerX    = sw - timerW - pad;

        // ── HP bar ────────────────────────────────────────────────────────────
        float pct = hero.MaxHealth > 0 ? Mathf.Clamp01(hero.Health / (float)hero.MaxHealth) : 0f;

        GUI.color = new Color(0.3f, 0.3f, 0.3f, 1f);
        GUI.Box(new Rect(hpX, 6f, hpW, barH - 12f), GUIContent.none, styleBox);

        GUI.color = Color.Lerp(Color.red, Color.green, pct);
        GUI.Box(new Rect(hpX + 2, 8f, (hpW - 4f) * pct, barH - 16f), GUIContent.none, styleBox);

        GUI.color = Color.white;
        GUI.Label(new Rect(hpX, 0f, hpW, barH), "HP  " + hero.Health + " / " + hero.MaxHealth, styleLabel);

        // ── Villager count ────────────────────────────────────────────────────
        float villagerRatio = startingVillagers > 0 ? (float)villagersAlive / startingVillagers : 1f;
        GUI.color = villagerRatio > 0.6f ? Color.green : villagerRatio > 0.3f ? Color.yellow : Color.red;
        GUI.Label(new Rect(villagerX, 0f, secW, barH), "Villagers  " + villagersAlive, styleLabel);

        // ── Zombie count ──────────────────────────────────────────────────────
        int zombies = FindObjectsOfType<NPCNavMesh>().Length;
        GUI.color = zombies == 0 ? Color.green : new Color(1f, 0.45f, 0.45f);
        GUI.Label(new Rect(zombieX, 0f, secW, barH), "Zombies  " + zombies, styleLabel);

        // ── Countdown timer (or Training label) ───────────────────────────────
        if (trainingMode)
        {
            GUI.color = new Color(0.4f, 1f, 1f, 0.9f);
            GUI.Label(new Rect(timerX, 0f, timerW, barH), "TRAINING", styleLabel);
        }
        else
        {
            float remaining = Mathf.Max(0f, surviveDuration - elapsed);
            int   mins      = Mathf.FloorToInt(remaining / 60f);
            int   secs      = Mathf.FloorToInt(remaining % 60f);
            GUI.color = remaining < 20f ? new Color(1f, 0.9f, 0.2f) : Color.white;
            GUI.Label(new Rect(timerX, 0f, timerW, barH),
                      string.Format("{0}:{1:00}", mins, secs), styleLabel);
        }

        GUI.color = Color.white;

        // ── "Return to Elder" banner ──────────────────────────────────────────
        if (AllZombiesDefeated)
        {
            float pulse = (Mathf.Sin(Time.time * 3f) + 1f) * 0.5f;
            GUI.color = new Color(1f, 0.95f, 0.2f, Mathf.Lerp(0.6f, 1f, pulse));
            GUI.Label(new Rect(sw * 0.5f - 240f, barH + 4f, 480f, 28f),
                      "★  All zombies defeated!  Return to the Elder!  ★", styleLabel);
            GUI.color = Color.white;
        }

        // ── Q-Learning status (centred inside the top bar) ────────────────────
        var ql = ZombieQLearner.Instance;
        if (ql != null)
        {
            int    explorePct = Mathf.RoundToInt(ql.Epsilon * 100f);
            string phase      = explorePct > 60 ? "Learning" : explorePct > 25 ? "Adapting" : "Experienced";
            GUI.color = new Color(0.6f, 1f, 0.6f, 0.9f);
            GUI.Label(new Rect(sw * 0.5f - 150f, 0f, 300f, barH),
                      $"AI Session {ql.SessionCount + 1}  |  {phase}  ({explorePct}% explore)",
                      styleSmall);
            GUI.color = Color.white;
        }
    }

    void DrawEndScreen()
    {
        bool won = CurrentState == State.Won;

        float pw = 520f, ph = 220f;
        float px = (Screen.width  - pw) * 0.5f;
        float py = (Screen.height - ph) * 0.5f;

        // Dark panel
        GUI.color = new Color(0f, 0f, 0f, 0.80f);
        GUI.Box(new Rect(px, py, pw, ph), GUIContent.none, styleBox);
        GUI.color = Color.white;

        // Headline
        string headline = won ? (zombiesClearedWin ? "CLEARED!" : "YOU SURVIVED!") : "DEFEATED";
        GUI.color = won ? new Color(0.4f, 1f, 0.4f) : new Color(1f, 0.35f, 0.35f);
        styleBig.normal.textColor = GUI.color;
        GUI.Label(new Rect(px, py + 24f, pw, 66f), headline, styleBig);
        GUI.color = Color.white;
        styleBig.normal.textColor = Color.white;

        // Sub-text
        string sub;
        if (won)
            sub = zombiesClearedWin
                ? $"All zombies eliminated!  Villagers saved: {villagersAlive} / {startingVillagers}"
                : $"Survived {surviveDuration}s  —  Villagers saved: {villagersAlive} / {startingVillagers}";
        else
            sub = villagersAlive <= 0 ? "Every villager was slain." : "The zombie horde overwhelmed the hero.";

        GUI.Label(new Rect(px, py + 110f, pw, 34f), sub, styleLabel);
        GUI.Label(new Rect(px, py + 170f, pw, 28f), "Press  R  to play again", styleLabel);

        if (Input.GetKeyDown(KeyCode.R))
            SceneManager.LoadScene(SceneManager.GetActiveScene().name);
    }

    // ── Style builder ─────────────────────────────────────────────────────────

    void BuildStyles()
    {
        if (stylesReady) return;

        styleBox = new GUIStyle(GUI.skin.box);

        styleLabel = new GUIStyle(GUI.skin.label)
        {
            fontSize  = 22,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter
        };
        styleLabel.normal.textColor = Color.white;

        styleBig = new GUIStyle(GUI.skin.label)
        {
            fontSize  = 52,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter
        };
        styleBig.normal.textColor = Color.white;

        styleSmall = new GUIStyle(GUI.skin.label)
        {
            fontSize  = 16,
            fontStyle = FontStyle.Normal,
            alignment = TextAnchor.MiddleCenter
        };
        styleSmall.normal.textColor = new Color(0.6f, 1f, 0.6f);

        stylesReady = true;
    }
}
