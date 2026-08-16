using UnityEngine;

[RequireComponent(typeof(AudioSource))]
public class AudioManager : MonoBehaviour
{
    public static AudioManager Instance { get; private set; }

    [Header("Hero")]
    [SerializeField] AudioClip heroAttack;
    [SerializeField] AudioClip heroHurt;

    [Header("Zombies")]
    [SerializeField] AudioClip zombieHit;
    [SerializeField] AudioClip zombieDeath;
    [SerializeField] AudioClip[] zombieGroans;   // optional: random idle groans

    [Header("Villagers")]
    [SerializeField] AudioClip villagerScream;

    [Header("Game Events")]
    [SerializeField] AudioClip gameWin;
    [SerializeField] AudioClip gameLose;

    [Header("Ambience")]
    [SerializeField] AudioClip ambientLoop;
    [SerializeField] [Range(0f, 1f)] float ambientVolume = 0.3f;

    [Header("Volume")]
    [SerializeField] [Range(0f, 1f)] float sfxVolume = 1f;

    // ── Components ────────────────────────────────────────────────────────────
    AudioSource sfxSource;      // one-shot SFX
    AudioSource ambientSource;  // looping ambient

    // ── Cooldowns (prevent ear-blasting repeats) ──────────────────────────────
    float lastZombieHitTime   = -99f;
    float lastZombieDeathTime = -99f;
    float lastHeroHurtTime    = -99f;
    float lastVillagerScream  = -99f;
    const float ZOMBIE_HIT_CD   = 2f;
    const float ZOMBIE_DEATH_CD = 2.5f;
    const float HERO_HURT_CD    = 2.5f;
    const float SCREAM_CD       = 3f;

    // ── Groan — fires after a rapid hit streak ────────────────────────────────
    int   hitStreak         = 0;
    float lastHitStreakTime = -99f;
    float lastGroanTime     = -99f;
    const int   GROAN_THRESHOLD   = 4;    // hits in a row before groan triggers
    const float HIT_STREAK_WINDOW = 2.5f; // streak resets if no hit within this window
    const float GROAN_CD          = 8f;   // long gap before another groan can play

    // ─────────────────────────────────────────────────────────────────────────
    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    void Start()
    {
        sfxSource = GetComponent<AudioSource>();
        sfxSource.playOnAwake = false;
        sfxSource.loop        = false;

        // Second AudioSource for ambient loop
        ambientSource           = gameObject.AddComponent<AudioSource>();
        ambientSource.loop      = true;
        ambientSource.volume    = ambientVolume;
        ambientSource.playOnAwake = false;
        ambientSource.spatialBlend = 0f;   // 2D

        if (ambientLoop != null)
        {
            ambientSource.clip = ambientLoop;
            ambientSource.Play();
        }
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public void PlayHeroAttack() => Play(heroAttack);

    public void PlayHeroHurt()
    {
        if (Time.time - lastHeroHurtTime < HERO_HURT_CD) return;
        lastHeroHurtTime = Time.time;
        Play(heroHurt);
    }

    public void PlayZombieDeath()
    {
        if (Time.time - lastZombieDeathTime < ZOMBIE_DEATH_CD) return;
        if (Random.value > 0.35f) return;  // play only ~35% of deaths
        lastZombieDeathTime = Time.time;
        Play(zombieDeath);
    }

    public void PlayZombieHit()
    {
        // ── Streak tracking for groan ─────────────────────────────────────────
        if (Time.time - lastHitStreakTime > HIT_STREAK_WINDOW)
            hitStreak = 0;   // too much time between hits — reset streak

        hitStreak++;
        lastHitStreakTime = Time.time;

        if (hitStreak >= GROAN_THRESHOLD && Time.time - lastGroanTime >= GROAN_CD)
        {
            hitStreak     = 0;
            lastGroanTime = Time.time;
            PlayZombieGroan();
        }

        // ── Regular hit sound (sparse) ────────────────────────────────────────
        if (Time.time - lastZombieHitTime < ZOMBIE_HIT_CD) return;
        if (Random.value > 0.3f) return;
        lastZombieHitTime = Time.time;
        Play(zombieHit);
    }

    public void PlayVillagerScream()
    {
        if (Time.time - lastVillagerScream < SCREAM_CD) return;
        lastVillagerScream = Time.time;
        Play(villagerScream);
    }

    public void PlayGameWin()
    {
        ambientSource.Stop();
        Play(gameWin);
    }

    public void PlayGameLose()
    {
        ambientSource.Stop();
        Play(gameLose);
    }

    /// Play a random zombie groan (call occasionally from NPCNavMesh if desired)
    public void PlayZombieGroan()
    {
        if (zombieGroans == null || zombieGroans.Length == 0) return;
        var clip = zombieGroans[Random.Range(0, zombieGroans.Length)];
        Play(clip);
    }

    // ── Internal ──────────────────────────────────────────────────────────────

    void Play(AudioClip clip)
    {
        if (clip == null || sfxSource == null) return;
        sfxSource.PlayOneShot(clip, sfxVolume);
    }
}
