using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Milestone M3 hub, added by <see cref="AudioGenerationRunner"/> to its own GameObject (toggle
/// <c>monsterAudio</c>). Owns the <see cref="EntityAudioBank"/>, gives every <see cref="MonsterAgent"/> in the
/// scene a <see cref="MonsterVoice"/> (checked every second, inactive monsters included, so spawns later are
/// covered), and keeps the monsters audible over MusicGen: while a voice is loud and close the music and bed
/// stingers duck by up to <see cref="duckDepth"/> (<see cref="AudioGenerationRunner.EntityDuck"/>) - fast in,
/// slow out, so the music swells back after a chase instead of snapping.
/// </summary>
public class EntityAudioDirector : MonoBehaviour
{
    public static EntityAudioDirector Instance { get; private set; }

    [Tooltip("How far the music dips under a loud, close monster (0.5 = half volume, about -6 dB).")]
    [SerializeField, Range(0f, 0.9f)] float duckDepth = 0.5f;
    [SerializeField] float scanInterval = 1f;

    readonly List<MonsterVoice> _voices = new List<MonsterVoice>();
    AudioGenerationRunner _runner;
    AudioListener _listener;
    float _nextScan, _duck = 1f;

    public EntityAudioBank Bank { get; private set; }
    public Vector3 ListenerPosition { get; private set; }
    public IReadOnlyList<MonsterVoice> Voices => _voices;
    public float Duck => _duck;

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }
        Instance = this;
        _runner = GetComponent<AudioGenerationRunner>();
        Bank = new EntityAudioBank(Random.Range(1, int.MaxValue)) { InUse = InUse };
        Debug.Log($"[EntityAudio] indexed {Bank.IndexedCount} monster clips ({Bank.ExcludedCount} left out) from {Bank.Root}: " +
                  $"breath {Bank.IndexedFor(EntityCue.Breath)}, movement {Bank.IndexedFor(EntityCue.Movement)}, " +
                  $"alert {Bank.IndexedFor(EntityCue.Alert)}, chase {Bank.IndexedFor(EntityCue.Chase)}, stinger {Bank.IndexedFor(EntityCue.Stinger)}");
    }

    void OnDestroy()
    {
        if (Instance != this) return;
        Instance = null;
        if (_runner != null) _runner.EntityDuck = 1f;
        Bank?.Dispose();
        Bank = null;
    }

    void Update()
    {
        Bank.Tick();
        if (Time.unscaledTime >= _nextScan)
        {
            _nextScan = Time.unscaledTime + scanInterval;
            Scan();
        }

        if (_listener == null || !_listener.isActiveAndEnabled) _listener = FindAnyObjectByType<AudioListener>();
        if (_listener != null) ListenerPosition = _listener.transform.position;

        float presence = 0f;
        foreach (var v in _voices)
            if (v != null && v.isActiveAndEnabled) presence = Mathf.Max(presence, v.Presence);
        float target = 1f - duckDepth * presence;
        _duck = Mathf.MoveTowards(_duck, target, Time.unscaledDeltaTime * (target < _duck ? 4f : 0.6f));
        if (_runner != null) _runner.EntityDuck = _duck;
    }

    bool InUse(AudioClip clip)
    {
        foreach (var v in _voices)
            if (v != null && v.IsPlaying(clip)) return true;
        return false;
    }

    void Scan()
    {
        _voices.RemoveAll(v => v == null);
        foreach (var m in FindObjectsByType<MonsterAgent>(FindObjectsInactive.Include))
        {
            if (!m.TryGetComponent<MonsterVoice>(out var voice)) voice = m.gameObject.AddComponent<MonsterVoice>();
            if (!_voices.Contains(voice)) _voices.Add(voice);
        }
    }
}
