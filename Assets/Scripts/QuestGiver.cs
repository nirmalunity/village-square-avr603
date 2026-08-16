using UnityEngine;

public class QuestGiver : MonoBehaviour
{
    [Header("Dialogue")]
    [TextArea(2, 5)]
    [SerializeField] string[] dialogueLines = new string[]
    {
        "Warrior! Our village is under siege!",
        "The dead have risen and are attacking our people from every direction.",
        "You must hold them off for two minutes until help arrives.",
        "Be warned !! the horde grows stronger as the clock ticks. Protect the innocent at all costs.",
        "May the village elders watch over you. Now go!"
    };

    [Header("Victory Dialogue")]
    [TextArea(2, 5)]
    [SerializeField] string[] victoryLines = new string[]
    {
        "You did it, warrior! The horde has been vanquished!",
        "Our villagers are safe because of your courage and sacrifice.",
        "The village elders will honor your name in song and story.",
        "Rest now, hero. You have earned your glory."
    };

    [SerializeField] string speakerName     = "Village Elder";
    [SerializeField] float  typewriterSpeed = 40f;   // characters per second
    [SerializeField] float  linePause       = 1.4f;  // seconds between lines
    [SerializeField] float  saluteDuration  = 3f;    // seconds salute holds after last line completes

    [Header("Proximity")]
    [SerializeField] float  triggerRadius   = 5f;    // player must be this close to trigger dialogue
    [SerializeField] float  faceSpeed       = 4f;    // rotation speed toward player

    [Header("Animation")]
    [SerializeField] float  animBlend       = 0.25f; // crossfade blend time (seconds)

    // ── Internal state machine ────────────────────────────────────────────────
    enum DialoguePhase { Waiting, Playing, Done, Victory, VictoryDone }
    DialoguePhase phase = DialoguePhase.Waiting;

    int   lineIndex    = 0;
    float charProgress = 0f;
    float pauseTimer   = 0f;
    bool  inLinePause  = false;   // true during the gap between lines
    float saluteTimer  = 0f;

    // ── Component refs ────────────────────────────────────────────────────────
    Animator  elderAnimator;
    Transform player;
    string    currentAnim = "";

    // ── GUI styles (built lazily) ─────────────────────────────────────────────
    GUIStyle styleBox;
    GUIStyle styleSpeaker;
    GUIStyle styleBody;
    bool     stylesReady = false;

    // ─────────────────────────────────────────────────────────────────────────
    void Start()
    {
        elderAnimator = GetComponentInChildren<Animator>();
        if (elderAnimator == null)
            Debug.LogError("[QuestGiver] No Animator found — assign ElderAnimator.controller.");

        var heroObj = GameObject.FindWithTag("Player");
        if (heroObj != null) player = heroObj.transform;

        PlayAnim("Idle");
    }

    void Update()
    {
        if (GameManager.Instance == null) return;

        float dist    = player != null ? Vector3.Distance(transform.position, player.position) : 999f;
        bool  inRange = dist <= triggerRadius;

        // ── VICTORY DONE — game won, nothing left to do ───────────────────────
        if (phase == DialoguePhase.VictoryDone)
        {
            PlayAnim("Idle");
            return;
        }

        // ── WAITING / DONE — idle until player walks up ───────────────────────
        if (phase == DialoguePhase.Waiting || phase == DialoguePhase.Done)
        {
            PlayAnim("Idle");
            if (inRange)
            {
                // If all zombies are cleared, play victory dialogue instead of intro
                if (GameManager.Instance?.AllZombiesDefeated == true)
                    BeginVictoryDialogue();
                else
                    BeginDialogue();
            }
            return;
        }

        // ── VICTORY — congratulation dialogue ────────────────────────────────
        if (phase == DialoguePhase.Victory)
        {
            if (!inRange) { phase = DialoguePhase.Done; return; }

            FacePlayer();
            bool   vIsLastLine = lineIndex >= victoryLines.Length - 1;
            string vCurrent    = lineIndex < victoryLines.Length ? victoryLines[lineIndex] : "";

            if (inLinePause)
            {
                PlayAnim("Idle");
                pauseTimer += Time.deltaTime;
                if (pauseTimer >= linePause) { inLinePause = false; AdvanceVictoryLine(); }
                return;
            }

            if (charProgress < vCurrent.Length)
            {
                charProgress = Mathf.Min(charProgress + typewriterSpeed * Time.deltaTime, vCurrent.Length);
                PlayAnim(vIsLastLine ? "Salute" : "Talking");
                return;
            }

            if (vIsLastLine)
            {
                PlayAnim("Salute");
                saluteTimer += Time.deltaTime;
                if (saluteTimer >= saluteDuration)
                {
                    phase = DialoguePhase.VictoryDone;
                    currentAnim = "";
                    PlayAnim("Idle");
                    GameManager.Instance?.TriggerWin();
                }
            }
            else
            {
                inLinePause = true;
                pauseTimer  = 0f;
                PlayAnim("Idle");
            }
            return;
        }

        // ── PLAYING ───────────────────────────────────────────────────────────

        // Player walked away — reset and return to waiting
        if (!inRange)
        {
            ResetDialogue();
            return;
        }

        // Slowly face the player
        FacePlayer();

        bool isLastLine = lineIndex >= dialogueLines.Length - 1;
        string current  = lineIndex < dialogueLines.Length ? dialogueLines[lineIndex] : "";

        // ── Between-line pause ────────────────────────────────────────────────
        if (inLinePause)
        {
            PlayAnim("Idle");
            pauseTimer += Time.deltaTime;
            if (pauseTimer >= linePause)
            {
                inLinePause = false;
                AdvanceLine();
            }
            return;
        }

        // ── Typewriter ────────────────────────────────────────────────────────
        if (charProgress < current.Length)
        {
            charProgress = Mathf.Min(charProgress + typewriterSpeed * Time.deltaTime, current.Length);
            PlayAnim(isLastLine ? "Salute" : "Talking");
            return;
        }

        // ── Line complete ─────────────────────────────────────────────────────
        if (isLastLine)
        {
            // Hold salute until saluteDuration expires, then start the game
            PlayAnim("Salute");
            saluteTimer += Time.deltaTime;
            if (saluteTimer >= saluteDuration)
            {
                phase = DialoguePhase.Done;
                currentAnim = "";
                PlayAnim("Idle");
                // Only trigger once — StartGame() guards itself against double-calls
                if (GameManager.Instance?.CurrentState == GameManager.State.Dialogue)
                    GameManager.Instance.StartGame();
            }
        }
        else
        {
            // Pause before advancing to the next line
            inLinePause = true;
            pauseTimer  = 0f;
            PlayAnim("Idle");
        }
    }

    // ── OnGUI — dialogue box ──────────────────────────────────────────────────

    void OnGUI()
    {
        if (phase != DialoguePhase.Playing && phase != DialoguePhase.Victory) return;

        BuildStyles();

        float boxW = Screen.width * 0.72f;
        float boxH = 190f;
        float boxX = (Screen.width - boxW) * 0.5f;
        float boxY = Screen.height - boxH - 36f;

        // Dark panel
        GUI.color = new Color(0f, 0f, 0f, 0.84f);
        GUI.Box(new Rect(boxX, boxY, boxW, boxH), GUIContent.none, styleBox);
        GUI.color = Color.white;

        // Gold accent strip
        GUI.color = new Color(1f, 0.75f, 0.2f, 0.9f);
        GUI.Box(new Rect(boxX, boxY, boxW, 4f), GUIContent.none, styleBox);
        GUI.color = Color.white;

        // Speaker name
        GUI.color = new Color(1f, 0.82f, 0.25f);
        GUI.Label(new Rect(boxX + 18f, boxY + 14f, boxW - 36f, 28f), speakerName + ":", styleSpeaker);
        GUI.color = Color.white;

        // Typewriter text — pick the right line array for the current phase
        string[] activeLines = (phase == DialoguePhase.Victory) ? victoryLines : dialogueLines;
        if (lineIndex < activeLines.Length)
        {
            int    visible = Mathf.FloorToInt(charProgress);
            string shown   = activeLines[lineIndex].Substring(0, Mathf.Min(visible, activeLines[lineIndex].Length));
            GUI.Label(new Rect(boxX + 18f, boxY + 48f, boxW - 36f, 84f), shown, styleBody);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    void BeginDialogue()
    {
        phase        = DialoguePhase.Playing;
        lineIndex    = 0;
        charProgress = 0f;
        inLinePause  = false;
        pauseTimer   = 0f;
        saluteTimer  = 0f;
        currentAnim  = "";
        stylesReady  = false;   // rebuild styles fresh each playthrough
        Debug.Log("[QuestGiver] Player in range — dialogue started.");
    }

    void ResetDialogue()
    {
        phase       = DialoguePhase.Waiting;
        lineIndex   = 0;
        charProgress = 0f;
        inLinePause = false;
        pauseTimer  = 0f;
        saluteTimer = 0f;
        currentAnim = "";
        PlayAnim("Idle");
        Debug.Log("[QuestGiver] Player left — dialogue reset.");
    }

    void BeginVictoryDialogue()
    {
        phase        = DialoguePhase.Victory;
        lineIndex    = 0;
        charProgress = 0f;
        inLinePause  = false;
        pauseTimer   = 0f;
        saluteTimer  = 0f;
        currentAnim  = "";
        stylesReady  = false;
        Debug.Log("[QuestGiver] Victory dialogue started.");
    }

    void AdvanceVictoryLine()
    {
        lineIndex++;
        charProgress = 0f;
        saluteTimer  = 0f;
        if (lineIndex >= victoryLines.Length)
        {
            phase = DialoguePhase.VictoryDone;
            GameManager.Instance?.TriggerWin();
        }
    }

    void AdvanceLine()
    {
        lineIndex++;
        charProgress = 0f;
        saluteTimer  = 0f;

        if (lineIndex >= dialogueLines.Length)
        {
            // Shouldn't reach here (last line is handled separately), but guard anyway
            phase = DialoguePhase.Done;
            GameManager.Instance?.StartGame();
        }
    }

    void FacePlayer()
    {
        if (player == null) return;
        Vector3 dir = player.position - transform.position;
        dir.y = 0f;
        if (dir.sqrMagnitude > 0.01f)
            transform.rotation = Quaternion.Slerp(
                transform.rotation,
                Quaternion.LookRotation(dir),
                Time.deltaTime * faceSpeed);
    }

    void PlayAnim(string stateName)
    {
        if (elderAnimator == null) return;
        if (currentAnim == stateName) return;
        currentAnim = stateName;
        elderAnimator.CrossFadeInFixedTime(stateName, animBlend);
    }

    void BuildStyles()
    {
        if (stylesReady) return;

        styleBox = new GUIStyle(GUI.skin.box);

        styleSpeaker = new GUIStyle(GUI.skin.label)
        {
            fontSize  = 26,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleLeft
        };
        styleSpeaker.normal.textColor = new Color(1f, 0.82f, 0.25f);

        styleBody = new GUIStyle(GUI.skin.label)
        {
            fontSize  = 24,
            fontStyle = FontStyle.Normal,
            alignment = TextAnchor.UpperLeft,
            wordWrap  = true
        };
        styleBody.normal.textColor = Color.white;

        stylesReady = true;
    }

    // ── Gizmo — visualise trigger radius in Scene view ────────────────────────
    void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(1f, 0.85f, 0.2f, 0.4f);
        Gizmos.DrawWireSphere(transform.position, triggerRadius);
    }
}
