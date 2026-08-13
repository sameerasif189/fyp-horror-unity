using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using Debug = UnityEngine.Debug;

/// <summary>
/// Horror audio: MusicGen beds (diverse prompts) plus occasional random one-shots
/// from fyp iteration 2/assets/audio/{0-5} matching the current stress level.
/// </summary>
[RequireComponent(typeof(AudioSource))]
public class AudioGenerationRunner : MonoBehaviour
{
    const int Levels = 6;
    const int DefaultPreloadPerLevel = 2;
    const int MaxLivePoolPerLevel = 8;

    [Header("MusicGen bridge")]
    [SerializeField] string bridgeUrl = "http://127.0.0.1:8765";
    [SerializeField] bool autoStartBridge = true;
    [SerializeField] string pythonExe = @"C:\Users\PC\miniconda3\python.exe";
    [SerializeField] string bridgeScriptRelative = @"fyp iteration 2\musicgen_unity_bridge.py";
    [SerializeField] float musicGenTimeoutSec = 180f;
    [SerializeField] int maxNewTokens = 192;
    [SerializeField] int realtimeMaxNewTokens = 160;
    [SerializeField] bool requireCuda = true;

    [Header("Preload at start (then real-time gen)")]
    [SerializeField] bool prewarmBeforeGameplay = true;
    [SerializeField] bool loadDiskCache = true;
    [SerializeField] bool saveDiskCache = true;
    [Tooltip("Fresh MusicGen clips generated during Load Audio before Start Game is unlocked.")]
    [SerializeField] int preloadClipsPerLevel = DefaultPreloadPerLevel;
    [Tooltip("Also load this many unused disk MusicGen beds per level as a safety buffer.")]
    [SerializeField] int diskBufferPerLevel = 2;
    [Tooltip("Always generate new MusicGen during play and crossfade in as soon as each clip is ready.")]
    [SerializeField] bool realtimeGenerateDuringPlay = true;
    [SerializeField] float realtimeGapSec = 0.35f;

    [Header("Playback")]
    [SerializeField] float musicVolume = 0.65f;
    [SerializeField] float crossfadeSec = 0.85f;
    [Tooltip("Rotate buffered MusicGen beds while waiting on the next live generation.")]
    [SerializeField] float rotateInterval = 9f;
    [SerializeField] bool showStatusHud = false;
    [Tooltip("If true, Start() does not auto-load — GameStartMenu must call BeginAudioLoadFromMenu.")]
    [SerializeField] bool waitForStartMenu = true;

    [Header("Random stress-level assets (audio/0-5)")]
    [SerializeField] bool randomAssetOneShots = true;
    [Tooltip("Chance to fire a random clip from assets/audio/{level} on each stress change.")]
    [SerializeField, Range(0f, 1f)] float assetChanceOnStressChange = 0.85f;
    [Tooltip("While playing, chance each interval to fire another random level clip.")]
    [SerializeField, Range(0f, 1f)] float assetChancePeriodic = 0.55f;
    [SerializeField] float assetOneShotMinInterval = 4f;
    [SerializeField] float assetOneShotMaxInterval = 11f;
    [SerializeField] float assetOneShotVolume = 0.55f;

    // MusicGen dual-source crossfade
    AudioSource _musicA;
    AudioSource _musicB;
    bool _musicAActive = true;
    // Occasional random WAVs from assets/audio/{level}
    AudioSource _assetOneShot;
    AudioClip _assetOneShotClip;

    int _lastLevel = -1;
    int _requestId;
    int _sessionSalt;
    FusionDirector.FusionParams _params;
    string _status = "Audio idle — use Load Audio on start screen";
    string _lastSource = "none";
    string _deviceLabel = "unknown";
    bool _bridgeHealthy;
    bool _prewarmDone;
    bool _prewarming;
    bool _realtimeGenerating;
    bool _gameStarted;
    Process _bridgeProcess;
    Coroutine _fadeRoutine;
    Coroutine _bootRoutine;
    Coroutine _realtimeRoutine;
    float _nextRotate;
    float _nextAssetOneShot;

    // cache[level][slot] — preload + live ring buffer
    readonly List<AudioClip>[] _cache = new List<AudioClip>[Levels];
    readonly int[] _cursor = new int[Levels];
    readonly HashSet<string> _loadedDiskPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<int> _usedSeeds = new HashSet<int>();

    AudioSource MusicActive => _musicAActive ? _musicA : _musicB;
    AudioSource MusicInactive => _musicAActive ? _musicB : _musicA;

    public bool IsGenerating => _prewarming || _realtimeGenerating;
    public bool IsReady => _prewarmDone;
    public bool BridgeHealthy => _bridgeHealthy;
    public string StatusText => _status;
    public string LastSource => _lastSource;
    public int CachedClipCount => CacheCount();

    string CacheRoot => Path.Combine(Application.persistentDataPath, "MusicGenCache");

    void Awake()
    {
        _sessionSalt = Environment.TickCount ^ Guid.NewGuid().GetHashCode();
        UnityEngine.Random.InitState(_sessionSalt);
        ProceduralAssetBank.EnsureIndexed();
        for (int i = 0; i < Levels; i++)
            _cache[i] = new List<AudioClip>(MaxLivePoolPerLevel);
        _loadedDiskPaths.Clear();
        _usedSeeds.Clear();

        var sources = GetComponents<AudioSource>();
        _musicA = sources[0];
        ConfigureSource(_musicA, loop: true);

        _musicB = gameObject.AddComponent<AudioSource>();
        ConfigureSource(_musicB, loop: true);

        _assetOneShot = gameObject.AddComponent<AudioSource>();
        ConfigureSource(_assetOneShot, loop: false);
    }

    static void ConfigureSource(AudioSource s, bool loop)
    {
        s.loop = loop;
        s.playOnAwake = false;
        s.spatialBlend = 0f;
        s.volume = 0f;
    }

    void OnEnable()
    {
        if (autoStartBridge)
            EnsureBridgeRunning();
        TrySubscribe();
    }

    void Start()
    {
        TrySubscribe();
        if (FusionDirector.Instance != null)
        {
            FusionDirector.Instance.OnParamsChanged -= OnFusion;
            FusionDirector.Instance.OnParamsChanged += OnFusion;
            if (FusionDirector.Instance.Latest != null)
                OnFusion(FusionDirector.Instance.Latest);
        }
        StartCoroutine(HealthPollLoop());
        // Menu-driven load: do not auto-prewarm / play beds until Load Audio + Start Game.
        if (!waitForStartMenu)
        {
            if (_bootRoutine != null) StopCoroutine(_bootRoutine);
            _bootRoutine = StartCoroutine(BootRoutine());
        }
        else
        {
            _status = "Waiting for Load Audio (MusicGen)…";
            showStatusHud = false;
        }
    }

    /// <summary>Called by GameStartMenu — preloads MusicGen only.</summary>
    public void BeginAudioLoadFromMenu()
    {
        if (_prewarming) return;
        if (_prewarmDone && CacheCount() > 0) return;
        showStatusHud = true;
        if (autoStartBridge)
            EnsureBridgeRunning();
        ProceduralAssetBank.EnsureIndexed();
        if (_bootRoutine != null) StopCoroutine(_bootRoutine);
        _bootRoutine = StartCoroutine(BootRoutine());
    }

    /// <summary>
    /// If editor/prewarm already wrote MusicGen WAVs to disk, load them and
    /// unlock Start Game without another Load Audio click.
    /// </summary>
    public static bool DiskCacheHasClips()
    {
        string root = Path.Combine(Application.persistentDataPath, "MusicGenCache");
        if (!Directory.Exists(root)) return false;
        try { return Directory.GetFiles(root, "*.wav", SearchOption.AllDirectories).Length > 0; }
        catch { return false; }
    }

    public bool TryUseExistingPreload()
    {
        if (_prewarmDone && CacheCount() > 0) return true;
        if (!DiskCacheHasClips()) return false;
        LoadRandomSubsetFromDisk(Mathf.Max(2, diskBufferPerLevel));
        if (CacheCount() <= 0) return false;
        _prewarmDone = true;
        _status = $"Using preloaded MusicGen — {CacheCount()} clips";
        Debug.Log("[AudioGen] reused disk preload cache=" + CacheCount());
        return true;
    }

    /// <summary>Called when player presses Start Game after audio is ready.</summary>
    public void NotifyGameStarted()
    {
        _gameStarted = true;
        showStatusHud = true;
        int level = StressController.Instance != null ? StressController.Instance.StressLevel : 0;
        if (_prewarmDone)
            ApplyLevelAudio(level);
        else
            _status = "Game started but MusicGen still loading…";

        StartRealtimeGeneration();
    }

    void OnDisable()
    {
        if (StressController.Instance != null)
            StressController.Instance.OnStressChanged -= OnStress;
        if (FusionDirector.Instance != null)
            FusionDirector.Instance.OnParamsChanged -= OnFusion;
        if (_bootRoutine != null) StopCoroutine(_bootRoutine);
        if (_fadeRoutine != null) StopCoroutine(_fadeRoutine);
        if (_realtimeRoutine != null) StopCoroutine(_realtimeRoutine);
        _realtimeRoutine = null;
        _realtimeGenerating = false;
    }

    void TrySubscribe()
    {
        if (StressController.Instance == null) return;
        StressController.Instance.OnStressChanged -= OnStress;
        StressController.Instance.OnStressChanged += OnStress;
    }

    IEnumerator BootRoutine()
    {
        _status = "Preloading MusicGen…";
        SilenceMusicGen();

        if (loadDiskCache)
            LoadRandomSubsetFromDisk(Mathf.Max(1, diskBufferPerLevel));

        if (prewarmBeforeGameplay)
            yield return PrewarmAllRoutine();

        _prewarmDone = true;
        _status = $"MusicGen preload ready — {CacheCount()} clips (live gen after Start)";
        Debug.Log($"[AudioGen] preload ready cache={CacheCount()} salt={_sessionSalt} (MusicGen only)");
        _bootRoutine = null;

        if (_gameStarted || !waitForStartMenu)
        {
            int level = StressController.Instance != null ? StressController.Instance.StressLevel : 0;
            ApplyLevelAudio(level);
            StartRealtimeGeneration();
        }
        else
        {
            SilenceMusicGen();
        }
    }

    void StartRealtimeGeneration()
    {
        if (!realtimeGenerateDuringPlay) return;
        if (_realtimeRoutine != null) return;
        _realtimeRoutine = StartCoroutine(RealtimeMusicGenLoop());
    }

    void OnStress(int level)
    {
        if (!_gameStarted && waitForStartMenu) return;
        level = Mathf.Clamp(level, 0, 5);
        _lastLevel = level;
        if (!_prewarmDone && prewarmBeforeGameplay)
        {
            SilenceMusicGen();
            _status = $"Waiting for MusicGen preload… L{level}";
            return;
        }
        ApplyLevelAudio(level);
    }

    void OnFusion(FusionDirector.FusionParams p)
    {
        _params = p;
        ApplyDsp();
    }

    void Update()
    {
        if (!_gameStarted && waitForStartMenu) return;
        if (!_prewarmDone || _lastLevel < 0) return;

        // Only rotate MusicGen when clips exist.
        if (HasMusicGen(_lastLevel) && !_realtimeGenerating && Time.time >= _nextRotate)
        {
            _nextRotate = Time.time + Mathf.Max(4f, rotateInterval);
            TryPlayMusicGen(_lastLevel);
        }

        if (randomAssetOneShots && Time.time >= _nextAssetOneShot)
        {
            ScheduleNextAssetOneShot();
            if (UnityEngine.Random.value <= AssetPeriodicChance(_lastLevel))
                TryPlayRandomLevelAsset(_lastLevel);
        }
    }

    void ApplyLevelAudio(int level)
    {
        level = Mathf.Clamp(level, 0, 5);
        _lastLevel = level;

        if (TryPlayMusicGen(level))
        {
            _lastSource = "musicgen";
            _status = $"MusicGen L{level} · buffer {_cache[level].Count}";
        }
        else
        {
            SilenceMusicGen();
            _lastSource = "silent";
            _status = $"L{level} — waiting for MusicGen…";
        }

        // Random one-shot from assets/audio/{level} (reshuffle deck each stress change).
        if (randomAssetOneShots)
        {
            ProceduralAssetBank.ReshuffleLevelPool(level, UnityEngine.Random.Range(1, int.MaxValue));
            if (UnityEngine.Random.value <= AssetStressChance(level))
                TryPlayRandomLevelAsset(level);
            ScheduleNextAssetOneShot();
        }

        ApplyDsp();
        _nextRotate = Time.time + Mathf.Max(4f, rotateInterval);
    }

    float AssetStressChance(int level)
    {
        // Higher stress → more likely to layer a random bank clip.
        float bump = level / 5f * 0.2f;
        return Mathf.Clamp01(assetChanceOnStressChange + bump);
    }

    float AssetPeriodicChance(int level)
    {
        float bump = level / 5f * 0.25f;
        return Mathf.Clamp01(assetChancePeriodic + bump);
    }

    void ScheduleNextAssetOneShot()
    {
        float min = Mathf.Max(1.5f, assetOneShotMinInterval);
        float max = Mathf.Max(min + 0.5f, assetOneShotMaxInterval);
        // Higher stress: tighter spacing between random assets
        float stressScale = Mathf.Lerp(1f, 0.55f, _lastLevel / 5f);
        _nextAssetOneShot = Time.time + UnityEngine.Random.Range(min, max) * stressScale;
    }

    /// <summary>Play a random WAV from fyp iteration 2/assets/audio/{level}.</summary>
    bool TryPlayRandomLevelAsset(int level)
    {
        if (_assetOneShot == null) return false;
        level = Mathf.Clamp(level, 0, 5);
        ProceduralAssetBank.EnsureIndexed();
        if (ProceduralAssetBank.LevelCount(level) <= 0) return false;

        string path = ProceduralAssetBank.TakeNextLevelAsset(level);
        if (string.IsNullOrEmpty(path)) return false;

        if (_assetOneShotClip != null)
        {
            Destroy(_assetOneShotClip);
            _assetOneShotClip = null;
        }

        var clip = ProceduralAssetBank.LoadClip(path, Path.GetFileNameWithoutExtension(path));
        if (clip == null) return false;

        _assetOneShotClip = clip;
        _assetOneShot.clip = clip;
        _assetOneShot.loop = false;
        float vol = assetOneShotVolume * Mathf.Lerp(0.75f, 1.15f, level / 5f);
        _assetOneShot.volume = Mathf.Clamp01(vol);
        _assetOneShot.pitch = StressAudioProfile.Pitch(level) * UnityEngine.Random.Range(0.92f, 1.08f);
        _assetOneShot.Play();
        _lastSource = HasMusicGen(level) ? "musicgen+asset" : "asset";
        Debug.Log($"[AudioGen] random asset L{level}: {Path.GetFileName(path)}");
        return true;
    }

    bool HasMusicGen(int level)
    {
        level = Mathf.Clamp(level, 0, 5);
        var list = _cache[level];
        if (list == null) return false;
        for (int i = 0; i < list.Count; i++)
            if (list[i] != null) return true;
        return false;
    }

    void SilenceMusicGen()
    {
        if (_musicA != null)
        {
            _musicA.Stop();
            _musicA.clip = null;
            _musicA.volume = 0f;
        }
        if (_musicB != null)
        {
            _musicB.Stop();
            _musicB.clip = null;
            _musicB.volume = 0f;
        }
    }

    /// <returns>true only if a real MusicGen clip was started.</returns>
    bool TryPlayMusicGen(int level)
    {
        if (!HasMusicGen(level)) return false;
        return PlayCachedMusic(level, crossfade: true);
    }

    bool PlayCachedMusic(int level, bool crossfade)
    {
        level = Mathf.Clamp(level, 0, 5);
        var list = _cache[level];
        if (list == null || list.Count == 0) return false;

        int idx;
        if (list.Count == 1)
        {
            idx = 0;
        }
        else
        {
            idx = (_cursor[level] + 1) % list.Count;
            if (UnityEngine.Random.value < 0.35f)
                idx = (idx + UnityEngine.Random.Range(1, list.Count)) % list.Count;
            if (idx == _cursor[level])
                idx = (_cursor[level] + 1) % list.Count;
        }
        _cursor[level] = idx;
        var clip = list[idx];
        if (clip == null) return false;

        float dist = StressAudioProfile.Distortion(level);
        if (_params != null)
            dist = Mathf.Max(dist, _params.dsp_distortion * 0.85f);
        AudioClip playClip = dist > 0.05f ? ApplySoftDistortion(clip, dist) : clip;

        float vol = StressAudioProfile.MusicVolume(level, musicVolume);
        if (crossfade)
            CrossfadeMusicTo(playClip, vol);
        else
        {
            var act = MusicActive;
            act.clip = playClip;
            act.volume = vol;
            act.pitch = StressAudioProfile.Pitch(level);
            act.Play();
        }
        _lastSource = "musicgen";
        Debug.Log($"[AudioGen] MusicGen play L{level} pick={idx}/{list.Count} '{clip.name}' vol={vol:0.00}");
        return true;
    }

    int CacheCount()
    {
        int n = 0;
        for (int i = 0; i < Levels; i++)
            n += _cache[i] != null ? _cache[i].Count : 0;
        return n;
    }

    IEnumerator PrewarmAllRoutine()
    {
        _prewarming = true;
        int need = Mathf.Clamp(preloadClipsPerLevel, 1, 4);

        yield return ProbeHealth();
        float waitUntil = Time.realtimeSinceStartup + Mathf.Max(60f, musicGenTimeoutSec);
        while (!_bridgeHealthy && Time.realtimeSinceStartup < waitUntil)
        {
            _status = "Waiting for MusicGen CUDA bridge to preload…";
            yield return ProbeHealth();
            if (!_bridgeHealthy)
                yield return new WaitForSecondsRealtime(2f);
        }

        if (!_bridgeHealthy)
        {
            Debug.LogWarning("[AudioGen] bridge offline — preload uses disk/procedural only; live gen will retry in play.");
            _status = "Preload: bridge offline (disk/procedural buffer)";
            _prewarming = false;
            yield break;
        }

        for (int level = 0; level < Levels; level++)
        {
            int generated = 0;
            int attempts = 0;
            while (generated < need && attempts < need * 5)
            {
                attempts++;
                _status = $"Preloading MusicGen L{level} {generated + 1}/{need} ({CacheCount()} total)";
                int seed = DiversifiedSeed(level, generated + attempts);
                AudioClip clip = null;
                yield return GenerateMusicClip(level, seed, maxNewTokens, c => clip = c);
                if (clip != null)
                {
                    clip.name = $"MG_preload_L{level}_s{seed}";
                    PushLiveClip(level, clip, playNow: false);
                    if (saveDiskCache)
                        SaveClipToDisk(level, seed, clip);
                    generated++;
                    Debug.Log($"[AudioGen] preloaded L{level} seed={seed} len={clip.length:0.00}s");
                }
                else
                {
                    Debug.LogWarning($"[AudioGen] preload failed L{level} — retrying...");
                    yield return new WaitForSecondsRealtime(1.5f);
                    if (!_bridgeHealthy)
                        break;
                }
            }
        }

        _prewarming = false;
        _status = $"Preload done ({CacheCount()}/{Levels * need}) — live gen after Start";
        Debug.Log($"[AudioGen] preload complete count={CacheCount()}");
    }

    IEnumerator RealtimeMusicGenLoop()
    {
        Debug.Log("[AudioGen] realtime MusicGen loop started");
        yield return new WaitForSecondsRealtime(0.2f);

        while (_gameStarted && realtimeGenerateDuringPlay)
        {
            if (!_prewarmDone)
            {
                yield return new WaitForSecondsRealtime(0.5f);
                continue;
            }

            int level = Mathf.Clamp(_lastLevel >= 0 ? _lastLevel : 0, 0, 5);

            if (!_bridgeHealthy)
            {
                yield return ProbeHealth();
                if (!_bridgeHealthy)
                {
                    _status = $"Realtime paused — bridge offline (playing buffer L{level})";
                    yield return new WaitForSecondsRealtime(2f);
                    continue;
                }
            }

            _realtimeGenerating = true;
            int seed = DiversifiedSeed(level, _usedSeeds.Count + Environment.TickCount);
            int tokens = Mathf.Clamp(realtimeMaxNewTokens, 64, maxNewTokens);
            _status = $"Generating live MusicGen L{level}…";
            _lastSource = "live-gen";

            AudioClip clip = null;
            yield return GenerateMusicClip(level, seed, tokens, c => clip = c);

            if (!_gameStarted)
                break;

            // Stress may have changed while generating — still keep the clip for that level.
            if (clip != null)
            {
                clip.name = $"MG_live_L{level}_s{seed}";
                bool playNow = _lastLevel == level;
                PushLiveClip(level, clip, playNow: playNow);
                if (saveDiskCache)
                    SaveClipToDiskSeeded(level, seed, clip);

                if (playNow)
                {
                    _lastSource = "live-musicgen";
                    _status = $"Live MusicGen L{level} · seed {seed} · buffer {_cache[level].Count}";
                    Debug.Log($"[AudioGen] LIVE play L{level} seed={seed} len={clip.length:0.00}s");
                }
                else
                {
                    _status = $"Buffered live L{level} (current L{_lastLevel}) · buffer {_cache[level].Count}";
                    Debug.Log($"[AudioGen] LIVE buffer L{level} seed={seed} (player on L{_lastLevel})");
                }
            }
            else
            {
                if (HasMusicGen(level) && _lastLevel == level)
                {
                    _status = $"Live gen failed L{level} — playing buffered MusicGen";
                    TryPlayMusicGen(level);
                }
                else if (_lastLevel == level)
                {
                    SilenceMusicGen();
                    _status = $"Live gen failed L{level} — silent until MusicGen ready";
                    _lastSource = "silent";
                }
                yield return new WaitForSecondsRealtime(1.5f);
            }

            _realtimeGenerating = false;
            // Small gap then immediately start the next generation for the current level.
            yield return new WaitForSecondsRealtime(Mathf.Max(0.05f, realtimeGapSec));
        }

        _realtimeGenerating = false;
        _realtimeRoutine = null;
    }

    void PushLiveClip(int level, AudioClip clip, bool playNow)
    {
        if (clip == null) return;
        level = Mathf.Clamp(level, 0, 5);
        var list = _cache[level];
        list.Add(clip);
        while (list.Count > MaxLivePoolPerLevel)
        {
            var old = list[0];
            list.RemoveAt(0);
            var act = MusicActive;
            var ina = MusicInactive;
            if (old != null && (act == null || old != act.clip) && (ina == null || old != ina.clip))
                Destroy(old);
        }
        _cursor[level] = list.Count - 1;

        if (playNow)
            PlayMusicClip(clip, level);
    }

    void PlayMusicClip(AudioClip clip, int level)
    {
        if (clip == null) return;
        level = Mathf.Clamp(level, 0, 5);
        float dist = StressAudioProfile.Distortion(level);
        if (_params != null)
            dist = Mathf.Max(dist, _params.dsp_distortion * 0.85f);
        AudioClip playClip = dist > 0.05f ? ApplySoftDistortion(clip, dist) : clip;
        float vol = StressAudioProfile.MusicVolume(level, musicVolume);
        CrossfadeMusicTo(playClip, vol);
        _nextRotate = Time.time + Mathf.Max(4f, rotateInterval);
    }

    int DiversifiedSeed(int level, int slot)
    {
        unchecked
        {
            for (int attempt = 0; attempt < 24; attempt++)
            {
                int r = UnityEngine.Random.Range(1, int.MaxValue);
                int seed = Math.Abs(_sessionSalt ^ (level * 7919) ^ (slot * 104729) ^ r ^ (Environment.TickCount + attempt * 9973));
                if (seed == 0) seed = level * 1000 + slot + 1;
                if (_usedSeeds.Add(seed))
                    return seed;
            }
            int fallback = Math.Abs(Guid.NewGuid().GetHashCode());
            _usedSeeds.Add(fallback);
            return fallback;
        }
    }

    void ShuffleAllCaches()
    {
        for (int level = 0; level < Levels; level++)
        {
            var list = _cache[level];
            if (list == null || list.Count < 2) continue;
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = UnityEngine.Random.Range(0, i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
            _cursor[level] = UnityEngine.Random.Range(0, list.Count);
        }
    }

    IEnumerator GenerateMusicClip(int level, int seed, int tokens, Action<AudioClip> onDone)
    {
        float intensity = StressAudioProfile.Intensity(level, _params != null ? _params.audio_intensity : level / 5f);
        float dissonance = StressAudioProfile.Dissonance(level, _params != null ? _params.audio_dissonance : level / 5f);
        tokens = Mathf.Clamp(tokens, 64, 512);

        string url = bridgeUrl.TrimEnd('/') + "/generate";
        string json =
            $"{{\"level\":{level},\"intensity\":{intensity:F4},\"dissonance\":{dissonance:F4},\"seed\":{seed},\"max_new_tokens\":{tokens}}}";

        const int maxAttempts = 10;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using (var req = new UnityWebRequest(url, "POST"))
            {
                byte[] body = Encoding.UTF8.GetBytes(json);
                req.uploadHandler = new UploadHandlerRaw(body);
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                req.timeout = Mathf.CeilToInt(musicGenTimeoutSec);

                var op = req.SendWebRequest();
                float start = Time.realtimeSinceStartup;
                while (!op.isDone)
                {
                    float elapsed = Time.realtimeSinceStartup - start;
                    if (_prewarming)
                        _status = $"Preloading L{level}… {elapsed:0.0}s (try {attempt})";
                    else if (_realtimeGenerating)
                        _status = $"Live MusicGen L{level}… {elapsed:0.0}s";
                    yield return null;
                }

                bool busy = req.responseCode == 409
                            || (req.error != null && req.error.IndexOf("409", StringComparison.Ordinal) >= 0);
                if (busy)
                {
                    yield return new WaitForSecondsRealtime(1.25f);
                    continue;
                }

                if (req.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning("[AudioGen] generate failed: " + req.error);
                    onDone?.Invoke(null);
                    yield break;
                }

                string deviceHdr = req.GetResponseHeader("X-Device");
                string gpuHdr = req.GetResponseHeader("X-GPU-Name");
                if (!string.IsNullOrEmpty(deviceHdr))
                    _deviceLabel = string.IsNullOrEmpty(gpuHdr) ? deviceHdr : $"{deviceHdr} ({gpuHdr})";

                var clip = WavUtility.ToAudioClip(req.downloadHandler.data, $"MG_L{level}_s{seed}");
                onDone?.Invoke(clip);
                yield break;
            }
        }
        onDone?.Invoke(null);
    }

    void LoadRandomSubsetFromDisk()
    {
        LoadRandomSubsetFromDisk(Mathf.Max(1, diskBufferPerLevel));
    }

    void LoadRandomSubsetFromDisk(int needPerLevel)
    {
        needPerLevel = Mathf.Clamp(needPerLevel, 1, 24);
        for (int level = 0; level < Levels; level++)
            TryExpandLevelFromDisk(level, addCount: needPerLevel, clearFirst: _cache[level].Count == 0);

        for (int level = 0; level < Levels; level++)
        {
            if (_cache[level].Count == 0)
                TryExpandLevelFromDisk(level, addCount: needPerLevel, clearFirst: false);
        }

        ShuffleAllCaches();
        Debug.Log($"[AudioGen] loaded disk buffer count={CacheCount()} paths={_loadedDiskPaths.Count} from {CacheRoot}");
    }

    /// <summary>Pull unused MusicGen WAVs from disk into the live pool for a level.</summary>
    int TryExpandLevelFromDisk(int level, int addCount, bool clearFirst = false)
    {
        level = Mathf.Clamp(level, 0, 5);
        string dir = Path.Combine(CacheRoot, $"L{level}");
        if (!Directory.Exists(dir)) return 0;

        string[] files;
        try { files = Directory.GetFiles(dir, "*.wav"); }
        catch { return 0; }
        if (files.Length == 0) return 0;

        for (int i = files.Length - 1; i > 0; i--)
        {
            int j = UnityEngine.Random.Range(0, i + 1);
            (files[i], files[j]) = (files[j], files[i]);
        }

        if (clearFirst)
        {
            _cache[level].Clear();
            string prefix = Path.Combine(CacheRoot, $"L{level}").Replace('\\', '/');
            var drop = new List<string>();
            foreach (var p in _loadedDiskPaths)
            {
                string n = p.Replace('\\', '/');
                if (n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    drop.Add(p);
            }
            foreach (var p in drop)
                _loadedDiskPaths.Remove(p);
        }

        int added = 0;
        int cap = Mathf.Max(addCount, 1);
        foreach (string path in files)
        {
            if (added >= cap) break;
            if (_loadedDiskPaths.Contains(path)) continue;

            try
            {
                byte[] wav = File.ReadAllBytes(path);
                var clip = WavUtility.ToAudioClip(wav, Path.GetFileNameWithoutExtension(path));
                if (clip == null) continue;
                _cache[level].Add(clip);
                _loadedDiskPaths.Add(path);
                string name = Path.GetFileNameWithoutExtension(path);
                if (name.StartsWith("clip_", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(name.Substring(5), out int seed))
                    _usedSeeds.Add(seed);
                added++;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[AudioGen] disk cache read failed: " + e.Message);
            }
        }
        return added;
    }

    void SaveClipToDiskSeeded(int level, int seed, AudioClip clip)
    {
        try
        {
            Directory.CreateDirectory(Path.Combine(CacheRoot, $"L{level}"));
            string path = Path.Combine(CacheRoot, $"L{level}", $"clip_{seed}.wav");
            byte[] wav = WavUtility.FromAudioClip(clip);
            if (wav != null && wav.Length > 0)
                File.WriteAllBytes(path, wav);
        }
        catch (Exception e)
        {
            Debug.LogWarning("[AudioGen] disk cache write failed: " + e.Message);
        }
    }

    void SaveClipToDisk(int level, int slotOrSeed, AudioClip clip)
    {
        SaveClipToDiskSeeded(level, slotOrSeed, clip);
    }

    string ClipPath(int level, int slot) =>
        Path.Combine(CacheRoot, $"L{level}", $"clip_{slot}.wav");

    void CrossfadeMusicTo(AudioClip clip, float targetVolume)
    {
        if (clip == null) return;
        var next = MusicInactive;
        var cur = MusicActive;
        next.clip = clip;
        next.volume = 0f;
        next.loop = true;
        next.Play();
        if (_fadeRoutine != null)
            StopCoroutine(_fadeRoutine);
        _fadeRoutine = StartCoroutine(CrossfadeRoutine(cur, next, targetVolume));
        _musicAActive = !_musicAActive;
    }

    IEnumerator CrossfadeRoutine(AudioSource from, AudioSource to, float targetVol)
    {
        float t = 0f;
        float fromStart = from != null && from.isPlaying ? from.volume : 0f;
        while (t < crossfadeSec)
        {
            t += Time.unscaledDeltaTime;
            float u = Mathf.Clamp01(t / Mathf.Max(0.01f, crossfadeSec));
            if (to != null) to.volume = Mathf.Lerp(0f, targetVol, u);
            if (from != null) from.volume = Mathf.Lerp(fromStart, 0f, u);
            yield return null;
        }
        if (from != null)
        {
            from.Stop();
            from.clip = null;
            from.volume = 0f;
        }
        if (to != null) to.volume = targetVol;
    }

    void ApplyDsp()
    {
        int level = Mathf.Clamp(_lastLevel >= 0 ? _lastLevel : 0, 0, 5);
        float fusionI = _params != null ? _params.audio_intensity : level / 5f;
        float intensity = StressAudioProfile.Intensity(level, fusionI);

        float pitch = StressAudioProfile.Pitch(level);
        if (_params != null)
        {
            float pitchShift = _params.dsp_pitch_shift;
            float pitchDrift = _params.audio_pitch_drift;
            // Fusion can darken further at high stress, but profile floor stays scary
            float fusionPitch = 1f + (pitchShift - 0.5f) * 0.2f + pitchDrift * 0.14f;
            pitch = Mathf.Min(pitch, Mathf.Clamp(fusionPitch, 0.72f, 1.15f));
            if (level >= 4)
                pitch = Mathf.Min(pitch, StressAudioProfile.Pitch(level));
        }

        float mVol = StressAudioProfile.MusicVolume(level, musicVolume) * Mathf.Lerp(0.9f, 1.15f, intensity);
        mVol = Mathf.Clamp01(mVol);

        var act = MusicActive;
        if (act != null && act.isPlaying && act.clip != null)
        {
            act.pitch = pitch;
            act.volume = Mathf.Lerp(act.volume, mVol, 0.4f);
        }
        if (_assetOneShot != null && _assetOneShot.isPlaying)
        {
            _assetOneShot.pitch = Mathf.Clamp(pitch * UnityEngine.Random.Range(0.98f, 1.02f), 0.7f, 1.25f);
        }
    }

    static AudioClip ApplySoftDistortion(AudioClip src, float amount)
    {
        if (src == null) return null;
        int n = src.samples * src.channels;
        float[] data = new float[n];
        src.GetData(data, 0);
        float gain = 1f + Mathf.Clamp01(amount) * 8f;
        float norm = 1f / (float)Math.Tanh(gain);
        for (int i = 0; i < data.Length; i++)
            data[i] = (float)Math.Tanh(data[i] * gain) * norm;
        var clip = AudioClip.Create(src.name + "_fx", src.samples, src.channels, src.frequency, false);
        clip.SetData(data, 0);
        return clip;
    }

    IEnumerator HealthPollLoop()
    {
        var wait = new WaitForSecondsRealtime(4f);
        while (enabled)
        {
            yield return ProbeHealth();
            yield return wait;
        }
    }

    IEnumerator ProbeHealth()
    {
        string url = bridgeUrl.TrimEnd('/') + "/health";
        using (var req = UnityWebRequest.Get(url))
        {
            req.timeout = 3;
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success)
            {
                _bridgeHealthy = false;
                yield break;
            }
            string body = req.downloadHandler.text ?? "";
            bool ready = body.IndexOf("\"ready\": true", StringComparison.OrdinalIgnoreCase) >= 0
                         || body.IndexOf("\"ready\":true", StringComparison.OrdinalIgnoreCase) >= 0;
            bool onCuda = body.IndexOf("\"device\": \"cuda\"", StringComparison.OrdinalIgnoreCase) >= 0
                          || body.IndexOf("\"device\":\"cuda\"", StringComparison.OrdinalIgnoreCase) >= 0;
            _bridgeHealthy = ready;
            if (onCuda)
                _deviceLabel = ExtractJsonString(body, "gpu_name") ?? "cuda";
            else if (ready)
                _deviceLabel = ExtractJsonString(body, "device") ?? "cpu";
        }
    }

    static string ExtractJsonString(string json, string key)
    {
        string needle = "\"" + key + "\":";
        int i = json.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        i += needle.Length;
        while (i < json.Length && (json[i] == ' ' || json[i] == '\t')) i++;
        if (i >= json.Length || json[i] != '"') return null;
        i++;
        int end = json.IndexOf('"', i);
        if (end < 0) return null;
        string v = json.Substring(i, end - i);
        return string.IsNullOrEmpty(v) || v == "null" ? null : v;
    }

    void EnsureBridgeRunning()
    {
        try
        {
            if (IsPortOpen("127.0.0.1", 8765))
            {
                // Port open is not the same as healthy — empty replies still bind.
                return;
            }
            if (_bridgeProcess != null && !_bridgeProcess.HasExited)
                return;

            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string script = Path.Combine(projectRoot, bridgeScriptRelative);
            if (!File.Exists(script) || !File.Exists(pythonExe))
            {
                Debug.LogWarning("[AudioGen] bridge script/python missing");
                return;
            }

            var psi = new ProcessStartInfo
            {
                FileName = pythonExe,
                Arguments = $"\"{script}\" --port 8765 --max-new-tokens {maxNewTokens}"
                            + (requireCuda ? " --require-cuda" : " --no-require-cuda")
                            + $" --generate-timeout {musicGenTimeoutSec:0}",
                WorkingDirectory = Path.GetDirectoryName(script) ?? projectRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            if (!psi.Environment.ContainsKey("CUDA_VISIBLE_DEVICES"))
                psi.Environment["CUDA_VISIBLE_DEVICES"] = "0";
            _bridgeProcess = Process.Start(psi);
            if (_bridgeProcess != null)
            {
                _bridgeProcess.OutputDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) Debug.Log("[MusicGenBridge] " + e.Data); };
                _bridgeProcess.ErrorDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) Debug.Log("[MusicGenBridge] " + e.Data); };
                _bridgeProcess.BeginOutputReadLine();
                _bridgeProcess.BeginErrorReadLine();
                _status = "Starting MusicGen bridge...";
                Debug.Log("[AudioGen] started MusicGen bridge pid=" + _bridgeProcess.Id);
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning("[AudioGen] failed to start bridge: " + e.Message);
        }
    }

    static bool IsPortOpen(string host, int port)
    {
        try
        {
            using (var client = new System.Net.Sockets.TcpClient())
            {
                var ar = client.BeginConnect(host, port, null, null);
                bool ok = ar.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(250));
                if (!ok) return false;
                client.EndConnect(ar);
                return true;
            }
        }
        catch { return false; }
    }

    void OnGUI()
    {
        if (!showStatusHud) return;
        // While start menu is visible, status is shown there
        if (!_gameStarted && waitForStartMenu && !_prewarming && !_prewarmDone) return;
        var style = new GUIStyle(GUI.skin.box)
        {
            fontSize = 14,
            alignment = TextAnchor.MiddleLeft,
            padding = new RectOffset(10, 10, 6, 6)
        };
        string bridge = _bridgeHealthy ? "online" : "offline";
        string ready = _prewarmDone
            ? (_realtimeGenerating ? "live-gen" : "live")
            : (_prewarming ? "preloading" : "booting");
        GUI.Box(new Rect(16, 72, 560, 52),
            $"Audio [{ready}]: {_status}\nBridge {bridge} · {_deviceLabel} · buffer {CacheCount()} · {_lastSource}", style);
    }
}

/// <summary>Minimal PCM16 WAV encode/decode for MusicGen bridge + disk cache.</summary>
static class WavUtility
{
    public static AudioClip ToAudioClip(byte[] wav, string name)
    {
        try
        {
            if (wav == null || wav.Length < 44) return null;
            if (wav[0] != 'R' || wav[1] != 'I' || wav[2] != 'F' || wav[3] != 'F') return null;

            int channels = BitConverter.ToInt16(wav, 22);
            int sampleRate = BitConverter.ToInt32(wav, 24);
            int bits = BitConverter.ToInt16(wav, 34);

            int dataOffset = 12;
            int dataSize = 0;
            while (dataOffset + 8 <= wav.Length)
            {
                string chunkId = Encoding.ASCII.GetString(wav, dataOffset, 4);
                int chunkSize = BitConverter.ToInt32(wav, dataOffset + 4);
                if (chunkId == "data")
                {
                    dataOffset += 8;
                    dataSize = chunkSize;
                    break;
                }
                dataOffset += 8 + chunkSize;
            }
            if (dataSize <= 0) return null;

            int sampleCount = dataSize / (bits / 8);
            int frames = sampleCount / Mathf.Max(1, channels);
            float[] samples = new float[frames];

            if (bits == 16)
            {
                int idx = dataOffset;
                for (int f = 0; f < frames; f++)
                {
                    float sum = 0f;
                    for (int c = 0; c < channels; c++)
                    {
                        short s = BitConverter.ToInt16(wav, idx);
                        idx += 2;
                        sum += s / 32768f;
                    }
                    samples[f] = sum / channels;
                }
            }
            else if (bits == 32)
            {
                int idx = dataOffset;
                for (int f = 0; f < frames; f++)
                {
                    float sum = 0f;
                    for (int c = 0; c < channels; c++)
                    {
                        float s = BitConverter.ToSingle(wav, idx);
                        idx += 4;
                        sum += s;
                    }
                    samples[f] = sum / channels;
                }
            }
            else return null;

            var clip = AudioClip.Create(name, frames, 1, sampleRate, false);
            clip.SetData(samples, 0);
            return clip;
        }
        catch (Exception e)
        {
            Debug.LogWarning("[WavUtility] " + e.Message);
            return null;
        }
    }

    public static byte[] FromAudioClip(AudioClip clip)
    {
        if (clip == null) return null;
        int frames = clip.samples;
        float[] data = new float[frames * clip.channels];
        clip.GetData(data, 0);

        // Downmix to mono PCM16
        float[] mono = new float[frames];
        int ch = clip.channels;
        for (int i = 0; i < frames; i++)
        {
            float sum = 0f;
            for (int c = 0; c < ch; c++)
                sum += data[i * ch + c];
            mono[i] = sum / ch;
        }

        int dataBytes = frames * 2;
        byte[] wav = new byte[44 + dataBytes];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(wav, 0);
        BitConverter.GetBytes(36 + dataBytes).CopyTo(wav, 4);
        Encoding.ASCII.GetBytes("WAVE").CopyTo(wav, 8);
        Encoding.ASCII.GetBytes("fmt ").CopyTo(wav, 12);
        BitConverter.GetBytes(16).CopyTo(wav, 16);
        BitConverter.GetBytes((short)1).CopyTo(wav, 20);
        BitConverter.GetBytes((short)1).CopyTo(wav, 22);
        BitConverter.GetBytes(clip.frequency).CopyTo(wav, 24);
        BitConverter.GetBytes(clip.frequency * 2).CopyTo(wav, 28);
        BitConverter.GetBytes((short)2).CopyTo(wav, 32);
        BitConverter.GetBytes((short)16).CopyTo(wav, 34);
        Encoding.ASCII.GetBytes("data").CopyTo(wav, 36);
        BitConverter.GetBytes(dataBytes).CopyTo(wav, 40);
        int o = 44;
        for (int i = 0; i < frames; i++)
        {
            short s = (short)Mathf.Clamp(Mathf.RoundToInt(mono[i] * 32767f), short.MinValue, short.MaxValue);
            wav[o++] = (byte)(s & 0xff);
            wav[o++] = (byte)((s >> 8) & 0xff);
        }
        return wav;
    }
}
