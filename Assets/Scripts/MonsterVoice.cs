using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Milestone M3: a monster's voice. Added at runtime by <see cref="EntityAudioDirector"/> to every
/// <see cref="MonsterAgent"/> - the M2 monster scripts are not changed; this only reads
/// <see cref="MonsterAgent.CurrentState"/> and the NavMeshAgent's speed. Three 3D sources at head height:
/// two alternating vocal sources, so a new sound does not cut the last one off, and a looping movement layer
/// (chains, scraping, footsteps) whose level and pitch follow the agent's speed.
///  - Reveal: a stinger, then an alert growl.
///  - Patrol: breathing and growls every 4-8.5 s, the odd stinger.
///  - Chase: a roar or scream at once, then chase / alert / breathing every 2.2-4.5 s, the odd stinger.
///  - Cooldown: a last growl, then sparse breathing. Dormant: silent.
/// Intervals shorten with stress, and each monster gets its own pitch so two never sound identical.
/// <see cref="Presence"/> (how loud and close it is) lets the director duck the music under it.
/// </summary>
[DisallowMultipleComponent]
public class MonsterVoice : MonoBehaviour
{
    /// <summary>Full level within this distance; logarithmic fall-off beyond it.</summary>
    public static float MinDistance = 3f, MaxDistance = 40f;
    /// <summary>Voices this far from the listener or further do not duck the music at all.</summary>
    const float DuckRange = 25f, DuckFull = 5f;
    const float ChaseSpeed = 4.4f;

    MonsterAgent _agent;
    NavMeshAgent _nav;
    Transform _head;
    AudioSource _vocalA, _vocalB, _move;
    MonsterAgent.State _state = MonsterAgent.State.Dormant;
    float _nextVocal, _pendingAlertAt = -1f, _pitch;
    bool _wasMoving;

    /// <summary>0-1: how loud and close this monster is at the listener right now.</summary>
    public float Presence { get; private set; }
    public EntityCue LastCue { get; private set; }
    public string LastClip { get; private set; }
    public int PlayCount { get; private set; }
    public MonsterAgent.State State => _state;

    static EntityAudioBank Bank => EntityAudioDirector.Instance != null ? EntityAudioDirector.Instance.Bank : null;

    void Awake()
    {
        _agent = GetComponent<MonsterAgent>();
        _nav = GetComponent<NavMeshAgent>();
        _head = new GameObject("Voice").transform;
        _head.SetParent(transform, false);
        _head.localPosition = Vector3.up * 1.4f;
        _vocalA = NewSource(false);
        _vocalB = NewSource(false);
        _move = NewSource(true);
        _pitch = Random.Range(0.88f, 1.06f);
    }

    AudioSource NewSource(bool loop)
    {
        var s = _head.gameObject.AddComponent<AudioSource>();
        s.playOnAwake = false;
        s.loop = loop;
        s.spatialBlend = 1f;
        s.rolloffMode = AudioRolloffMode.Logarithmic;
        s.minDistance = MinDistance;
        s.maxDistance = MaxDistance;
        s.dopplerLevel = 0f;
        s.spread = 60f;
        s.priority = 24;   // ahead of ambience when voices run short
        s.volume = loop ? 0f : 1f;
        return s;
    }

    void OnDisable()
    {
        if (_vocalA != null) _vocalA.Stop();
        if (_vocalB != null) _vocalB.Stop();
        if (_move != null) { _move.Stop(); _move.volume = 0f; }
        _state = MonsterAgent.State.Dormant;
        _pendingAlertAt = -1f;
        _wasMoving = false;
        Presence = 0f;
    }

    void Update()
    {
        var bank = Bank;
        if (_agent == null || bank == null) return;
        float now = Time.time;
        var state = _agent.CurrentState;
        if (state != _state) Enter(state, bank, now);

        if (_pendingAlertAt > 0f && now >= _pendingAlertAt)
        {
            _pendingAlertAt = -1f;
            Vocal(bank, EntityCue.Alert, 0.9f);
        }
        if (state != MonsterAgent.State.Dormant && state != MonsterAgent.State.Reveal && now >= _nextVocal)
        {
            Vocal(bank, PickCue(state), state == MonsterAgent.State.Chase ? 1f : 0.85f);
            _nextVocal = now + Interval(state);
        }
        UpdateMovement(bank, state);
        UpdatePresence();
    }

    void Enter(MonsterAgent.State state, EntityAudioBank bank, float now)
    {
        _state = state;
        switch (state)
        {
            case MonsterAgent.State.Reveal:
                Vocal(bank, EntityCue.Stinger, 1f);
                _pendingAlertAt = now + Random.Range(0.6f, 1.2f);
                _nextVocal = now + 3f;
                break;
            case MonsterAgent.State.Patrol:
                _nextVocal = now + Random.Range(1.5f, 3.5f);
                break;
            case MonsterAgent.State.Chase:
                Vocal(bank, EntityCue.Chase, 1f);
                _nextVocal = now + Interval(state);
                break;
            case MonsterAgent.State.Cooldown:
                Vocal(bank, EntityCue.Alert, 0.8f);
                _nextVocal = now + Interval(state);
                break;
            default:
                _pendingAlertAt = -1f;
                break;
        }
    }

    static EntityCue PickCue(MonsterAgent.State state)
    {
        float r = Random.value;
        switch (state)
        {
            case MonsterAgent.State.Chase:
                return r < 0.45f ? EntityCue.Chase : r < 0.75f ? EntityCue.Alert : r < 0.9f ? EntityCue.Breath : EntityCue.Stinger;
            case MonsterAgent.State.Cooldown:
                return r < 0.7f ? EntityCue.Breath : EntityCue.Alert;
            default:
                return r < 0.55f ? EntityCue.Breath : r < 0.9f ? EntityCue.Alert : EntityCue.Stinger;
        }
    }

    static float Interval(MonsterAgent.State state)
    {
        int stress = StressController.Instance != null ? StressController.Instance.StressLevel : 4;
        float k = Mathf.Lerp(1.25f, 0.8f, stress / 5f);
        float s = state == MonsterAgent.State.Chase ? Random.Range(2.2f, 4.5f)
                : state == MonsterAgent.State.Cooldown ? Random.Range(5f, 10f)
                : Random.Range(4f, 8.5f);
        return s * k;
    }

    void Vocal(EntityAudioBank bank, EntityCue cue, float volume)
    {
        var clip = bank.Next(cue);
        if (clip == null) return;
        // The free source, else the one furthest through its clip.
        var src = !_vocalA.isPlaying ? _vocalA : !_vocalB.isPlaying ? _vocalB
                : Progress(_vocalA) >= Progress(_vocalB) ? _vocalA : _vocalB;
        src.clip = clip;
        src.volume = volume;
        src.pitch = _pitch * Random.Range(0.95f, 1.05f);
        src.Play();
        LastCue = cue;
        LastClip = clip.name;
        PlayCount++;
    }

    /// <summary>Whether one of this voice's sources is playing <paramref name="clip"/> right now.</summary>
    public bool IsPlaying(AudioClip clip) =>
        clip != null && (Holds(_vocalA, clip) || Holds(_vocalB, clip) || Holds(_move, clip));

    static bool Holds(AudioSource s, AudioClip clip) => s != null && s.isPlaying && s.clip == clip;

    static float Progress(AudioSource s) => s.clip != null && s.clip.length > 0f ? s.time / s.clip.length : 1f;

    void UpdateMovement(EntityAudioBank bank, MonsterAgent.State state)
    {
        float speed = _nav != null && _nav.enabled ? _nav.velocity.magnitude : 0f;
        bool moving = state != MonsterAgent.State.Dormant && speed > 0.25f;
        // A fresh loop each time it starts moving, or when the bank released the one it had.
        if (moving && (!_wasMoving || _move.clip == null || !_move.isPlaying))
        {
            var clip = !_wasMoving || _move.clip == null ? bank.Next(EntityCue.Movement) : _move.clip;
            if (clip != null)
            {
                _move.clip = clip;
                _move.time = Random.Range(0f, clip.length * 0.5f);
                _move.Play();
            }
        }
        _wasMoving = moving;
        float target = moving ? Mathf.Clamp01(0.35f + 0.65f * speed / ChaseSpeed) : 0f;
        _move.volume = Mathf.MoveTowards(_move.volume, target, Time.deltaTime * 2f);
        _move.pitch = _pitch * Mathf.Lerp(0.9f, 1.12f, speed / ChaseSpeed);
        if (!moving && _move.volume <= 0.001f && _move.isPlaying) _move.Stop();
    }

    void UpdatePresence()
    {
        float level = Mathf.Max(Level(_vocalA), Level(_vocalB), Level(_move) * 0.5f);
        var director = EntityAudioDirector.Instance;
        float d = director != null ? Vector3.Distance(director.ListenerPosition, _head.position) : DuckRange;
        Presence = level * Mathf.InverseLerp(DuckRange, DuckFull, d);
    }

    static float Level(AudioSource s) => s != null && s.isPlaying ? s.volume : 0f;
}
