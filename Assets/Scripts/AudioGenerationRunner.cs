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
    [SerializeField] string pythonExe = @"C:\Software\miniconda\python.exe";
    [SerializeField] string bridgeScriptRelative = @"fyp iteration 2\musicgen_unity_bridge.py";
    [SerializeField] float musicGenTimeoutSec = 180f;
    [Tooltip("Superseded by clipTokens below — kept only so the serialized values in SampleScene " +
             "are not dropped. Nothing reads these any more.")]
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

    [Tooltip("Generate and swap audio only when the stress level changes. On by default: the " +
             "continuous generate loop kept MusicGen inferring for the whole session, and the " +
             "bed-rotate and one-shot timers changed the mix every few seconds. New field name on " +
             "purpose, so nothing stale in SampleScene can bind to it.")]
    [SerializeField] bool generateOnStressChangeOnly = true;

    [Tooltip("MusicGen tokens per clip. MusicGen runs at ~50 tokens/second, so 512 is about ten " +
             "seconds — roughly three times the old 160/192, which was short enough that the loop " +
             "point was obvious. Costs more GPU time per clip, affordable now that generation only " +
             "happens on a stress change. New field name so the old scene values cannot win.")]
    [SerializeField, Range(128, 1024)] int clipTokens = 512;

    [Tooltip("Length of the tail-into-head crossfade that makes a clip loop without a seam. " +
             "Set to 0 to disable and loop raw.")]
    [SerializeField, Range(0f, 3f)] float loopCrossfadeSec = 0.9f;

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
    [Tooltip("Superseded — stingers now fire every time the interval elapses, so the interval " +
             "alone controls spacing. Kept so the serialized scene value is not dropped.")]
    [SerializeField, Range(0f, 1f)] float assetChancePeriodic = 0.55f;
    [Tooltip("Seconds between random bank stingers. They are deliberately decoupled from the " +
             "moment of a stress change — firing one on top of the crossfade was an audible boom.")]
    [SerializeField] float assetOneShotMinInterval = 5f;
    [SerializeField] float assetOneShotMaxInterval = 15f;
    [SerializeField] float assetOneShotVolume = 0.55f;

    [Header("Monster audio (M3)")]
    [Tooltip("Monster voices (EntityAudioDirector): 3D breathing / movement / alert / chase / stinger sounds from " +
             "the iteration-2 entity clips, with the music and bed stingers ducked under them.")]
    [SerializeField] bool monsterAudio = true;

    // Music/bed gain set by EntityAudioDirector (1 = no duck). Every volume this class writes is multiplied by the
    // gain currently applied, and LateUpdate rescales the sources when it changes, so the rest of the class keeps
    // working in undiluted volumes (ApplyDsp lerps from the current volume, so a duck applied from outside
    // would have ratcheted the music down).
    float _entityDuck = 1f, _duckApplied = 1f;

    /// <summary>0-1 gain on the music and bed stingers while a monster is loud and close (M3).</summary>
    public float EntityDuck
    {
        get => _entityDuck;
        set => _entityDuck = Mathf.Clamp(value, 0.05f, 1f);
    }

    // MusicGen dual-source crossfade
    AudioSource _musicA;
    AudioSource _musicB;
    bool _musicAActive = true;
    // Occasional random WAVs from assets/audio/{level}
    AudioSource _assetOneShot;
    AudioClip _assetOneShotClip;
    bool _assetLoading;
    Coroutine _assetFade;

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
    // Set on each stress change; consumed by RealtimeMusicGenLoop when generating on demand.
    bool _regenRequested;
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
    // Per-level play history, by clip reference rather than index (the pool evicts from the
    // front, so indices go stale). Drives the no-repeat deck in PickNextClipIndex.
    readonly List<AudioClip>[] _recentClips = new List<AudioClip>[Levels];
    // Distortion-processed copies awaiting destruction; the newest may still be crossfading.
    readonly List<AudioClip> _fxClips = new List<AudioClip>();
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

        if (monsterAudio && GetComponent<EntityAudioDirector>() == null)
            gameObject.AddComponent<EntityAudioDirector>();
    }

    void LateUpdate()
    {
        if (Mathf.Approximately(_entityDuck, _duckApplied)) return;
        float k = _entityDuck / _duckApplied;
        foreach (var s in new[] { _musicA, _musicB, _assetOneShot })
            if (s != null) s.volume = Mathf.Clamp01(s.volume * k);
        _duckApplied = _entityDuck;
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

        // Bank stingers run on their own timer at every level. They are decoded clips rather than
        // generation, so this is cheap and does not bring back the lag the generate loop caused -
        // and spacing them away from the switch is what stops them booming over the crossfade.
        if (randomAssetOneShots && Time.time >= _nextAssetOneShot)
        {
            ScheduleNextAssetOneShot();
            TryPlayRandomLevelAsset(_lastLevel);
        }

        // The bed rotate below does change the mix unprompted, so it stays off when audio is
        // meant to follow stress changes only.
        if (generateOnStressChangeOnly) return;

        // Only rotate MusicGen when clips exist.
        if (HasMusicGen(_lastLevel) && !_realtimeGenerating && Time.time >= _nextRotate)
        {
            _nextRotate = Time.time + Mathf.Max(4f, rotateInterval);
            TryPlayMusicGen(_lastLevel);
        }
    }

    void ApplyLevelAudio(int level)
    {
        level = Mathf.Clamp(level, 0, 5);
        _lastLevel = level;
        // The one place a fresh MusicGen clip is asked for when generating on demand.
        _regenRequested = true;

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
            // Reshuffle so the new level draws from a fresh deck, then arm the timer. Deliberately
            // does NOT play one here: firing a hot bank clip on top of the crossfade was the boom
            // reported at the start of every stress change.
            ProceduralAssetBank.ReshuffleLevelPool(level, UnityEngine.Random.Range(1, int.MaxValue));
            ScheduleNextAssetOneShot();
            if (_assetOneShot != null && _assetOneShot.isPlaying && _assetFade == null)
                _assetFade = StartCoroutine(FadeOutAssetOneShot(1.2f));
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
        // Higher stress tightens the spacing toward the minimum, but the floor keeps it inside
        // the configured window - L5 lands around 5-9 s, L0 around 5-15 s.
        float stressScale = Mathf.Lerp(1f, 0.6f, Mathf.Max(0, _lastLevel) / 5f);
        _nextAssetOneShot = Time.time + Mathf.Max(min, UnityEngine.Random.Range(min, max) * stressScale);
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

        if (!ProceduralAssetBank.IsWav(path))
        {
            // The curated level clips are mostly .mp3: decoded off the main thread, played when ready.
            if (!_assetLoading) StartCoroutine(LoadAndPlayAsset(path, level));
            return true;
        }
        var clip = ProceduralAssetBank.LoadClip(path, Path.GetFileNameWithoutExtension(path));
        if (clip == null) return false;
        PlayAsset(clip, level, path);
        return true;
    }

    IEnumerator LoadAndPlayAsset(string path, int level)
    {
        _assetLoading = true;
        var type = string.Equals(Path.GetExtension(path), ".ogg", StringComparison.OrdinalIgnoreCase) ? AudioType.OGGVORBIS : AudioType.MPEG;
        using (var req = UnityWebRequestMultimedia.GetAudioClip(new Uri(path).AbsoluteUri, type))
        {
            // Fully decoded (not streamed), so ShapeStinger can read and level the samples.
            ((DownloadHandlerAudioClip)req.downloadHandler).streamAudio = false;
            yield return req.SendWebRequest();
            _assetLoading = false;
            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning($"[AudioGen] could not decode {Path.GetFileName(path)}: {req.error}");
                yield break;
            }
            var clip = DownloadHandlerAudioClip.GetContent(req);
            if (clip == null || clip.samples <= 0) yield break;
            clip.name = Path.GetFileNameWithoutExtension(path);
            // Stress changed while it decoded: it belongs to the old level.
            if (level != _lastLevel) { Destroy(clip); yield break; }
            PlayAsset(clip, level, path);
        }
    }

    void PlayAsset(AudioClip clip, int level, string path)
    {
        if (_assetFade != null) { StopCoroutine(_assetFade); _assetFade = null; }
        if (_assetOneShotClip != null)
        {
            Destroy(_assetOneShotClip);
            _assetOneShotClip = null;
        }

        // Bank clips are mastered far hotter than a MusicGen bed (measured: L5 one-shots median
        // peak 0.850 / RMS 0.216 against the bed's 0.308 / 0.120), so without this they land on
        // top of the mix as a boom on every stress change. Normalise and soften the attack.
        ShapeStinger(clip);

        _assetOneShotClip = clip;
        _assetOneShot.clip = clip;
        _assetOneShot.loop = false;
        float vol = assetOneShotVolume * Mathf.Lerp(0.75f, 1.15f, level / 5f);
        _assetOneShot.volume = Mathf.Clamp01(vol) * _duckApplied;
        _assetOneShot.pitch = StressAudioProfile.Pitch(level) * UnityEngine.Random.Range(0.92f, 1.08f);
        _assetOneShot.Play();
        // Curated clips run up to ~30 s: the gap to the next one counts from the end of this one, so it is never cut off.
        _nextAssetOneShot = Mathf.Max(_nextAssetOneShot, Time.time) + clip.length / Mathf.Max(0.1f, _assetOneShot.pitch);
        _lastSource = HasMusicGen(level) ? "musicgen+asset" : "asset";
        Debug.Log($"[AudioGen] random asset L{level}: {Path.GetFileName(path)} ({clip.length:0.0}s)");
    }

    /// <summary>A clip still playing from the previous stress level fades out instead of running on (up to ~30 s).</summary>
    IEnumerator FadeOutAssetOneShot(float seconds)
    {
        float start = _assetOneShot.volume;
        for (float t = 0f; t < seconds && _assetOneShot.isPlaying; t += Time.deltaTime)
        {
            _assetOneShot.volume = Mathf.Lerp(start, 0f, t / seconds);
            yield return null;
        }
        _assetOneShot.Stop();
        _assetFade = null;
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

        int idx = PickNextClipIndex(level, list);
        _cursor[level] = idx;
        var clip = list[idx];
        if (clip == null) return false;

        float dist = StressAudioProfile.Distortion(level);
        if (_params != null)
            dist = Mathf.Max(dist, _params.dsp_distortion * 0.85f);
        AudioClip playClip = dist > 0.05f ? TrackFxClip(ApplySoftDistortion(clip, dist)) : clip;

        float vol = StressAudioProfile.MusicVolume(level, musicVolume);
        if (crossfade)
            CrossfadeMusicTo(playClip, vol);
        else
        {
            var act = MusicActive;
            act.clip = playClip;
            act.volume = vol * _duckApplied;
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
                yield return GenerateMusicClip(level, seed, clipTokens, c => clip = c);
                if (clip != null)
                {
                    clip.name = $"MG_preload_L{level}_s{seed}";
                    if (saveDiskCache)
                        SaveClipToDisk(level, seed, clip);
                    Debug.Log($"[AudioGen] preloaded L{level} seed={seed} len={clip.length:0.00}s");
                    // Push last: it takes ownership of the raw clip.
                    PushLiveClip(level, clip, playNow: false);
                    generated++;
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

            // Idle until a stress change asks for a clip. Previously this loop generated
            // back-to-back for the whole session, which kept MusicGen on the GPU permanently.
            if (generateOnStressChangeOnly)
            {
                int want = Mathf.Clamp(_lastLevel >= 0 ? _lastLevel : 0, 0, 5);
                bool poolFull = _cache[want] != null && _cache[want].Count >= MaxLivePoolPerLevel;
                if (!_regenRequested || poolFull)
                {
                    // Clear even when skipping for a full pool, so the request does not fire
                    // later once a clip has been evicted.
                    _regenRequested = false;
                    yield return new WaitForSecondsRealtime(0.25f);
                    continue;
                }
                _regenRequested = false;
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
            int tokens = clipTokens;
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
                float clipLen = clip.length;
                if (saveDiskCache)
                    SaveClipToDiskSeeded(level, seed, clip);
                // Push last: it takes ownership of the raw clip.
                PushLiveClip(level, clip, playNow: playNow);

                if (playNow)
                {
                    _lastSource = "live-musicgen";
                    _status = $"Live MusicGen L{level} · seed {seed} · buffer {_cache[level].Count}";
                    Debug.Log($"[AudioGen] LIVE play L{level} seed={seed} len={clipLen:0.00}s");
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

    /// <summary>
    /// Pick a cache slot the player has not heard recently. Every clip in a level's pool plays
    /// before any of them repeats, so revisiting a level (3 -> 4 -> 5 -> 4 -> 3) gives a different
    /// bed each time rather than the old cursor-plus-random-jump, which could land on a repeat.
    /// </summary>
    int PickNextClipIndex(int level, List<AudioClip> list)
    {
        int count = list.Count;
        if (count <= 1) return 0;

        var recent = _recentClips[level] ??= new List<AudioClip>();
        // Forget clips that have been evicted from the pool or destroyed.
        recent.RemoveAll(c => c == null || !list.Contains(c));
        while (recent.Count > Mathf.Max(1, count - 1))
            recent.RemoveAt(0);

        var candidates = new List<int>(count);
        for (int i = 0; i < count; i++)
            if (!recent.Contains(list[i])) candidates.Add(i);

        // Whole pool has been heard: retire the oldest entry and let it come round again.
        if (candidates.Count == 0 && recent.Count > 0)
        {
            recent.RemoveAt(0);
            for (int i = 0; i < count; i++)
                if (!recent.Contains(list[i])) candidates.Add(i);
        }
        if (candidates.Count == 0)
            return (_cursor[level] + 1) % count;

        int idx = candidates[UnityEngine.Random.Range(0, candidates.Count)];
        recent.Add(list[idx]);
        return idx;
    }

    /// <summary>
    /// Return a copy whose tail is crossfaded back over its head, so AudioSource.loop wraps
    /// without an audible seam. The result is shorter than the source by the fade length.
    /// Returns the source unchanged when the fade is disabled or the clip is too short.
    /// </summary>
    AudioClip MakeSeamlessLoop(AudioClip src, float fadeSec)
    {
        if (src == null || fadeSec <= 0.01f) return src;
        int frames = src.samples;
        int ch = Mathf.Max(1, src.channels);
        int fade = Mathf.Clamp(Mathf.RoundToInt(fadeSec * src.frequency), 0, frames / 3);
        if (fade < 64 || frames - fade < 128) return src;

        var data = new float[frames * ch];
        if (!src.GetData(data, 0)) return src;

        int outFrames = frames - fade;
        var outData = new float[outFrames * ch];
        Array.Copy(data, outData, outFrames * ch);

        // Fold the discarded tail back over the head. At the wrap point the listener hears
        // frame outFrames-1 followed by frame 0, which now equals the original frame outFrames -
        // continuous in the source material.
        for (int f = 0; f < fade; f++)
        {
            float w = f / (float)fade;
            for (int c = 0; c < ch; c++)
            {
                int head = f * ch + c;
                int tail = (outFrames + f) * ch + c;
                outData[head] = data[head] * w + data[tail] * (1f - w);
            }
        }

        var loop = AudioClip.Create(src.name + "_loop", outFrames, ch, src.frequency, false);
        loop.SetData(outData, 0);
        return loop;
    }

    /// <summary>
    /// Track a distortion-processed copy and destroy older ones. These used to be created on
    /// every play and never released; at the longer clip length that is a few MB per stress
    /// change. Two are kept because the newest may still be mid-crossfade.
    /// </summary>
    AudioClip TrackFxClip(AudioClip fx)
    {
        if (fx == null) return null;
        _fxClips.Add(fx);
        while (_fxClips.Count > 2)
        {
            var old = _fxClips[0];
            _fxClips.RemoveAt(0);
            if (old == null) continue;
            var act = MusicActive;
            var ina = MusicInactive;
            if ((act == null || act.clip != old) && (ina == null || ina.clip != old))
                Destroy(old);
        }
        return fx;
    }

    /// <summary>
    /// Takes ownership of <paramref name="clip"/>: stores a seam-crossfaded copy and destroys the
    /// raw one. Callers must finish with the raw clip (disk save, length logging) before calling.
    /// </summary>
    void PushLiveClip(int level, AudioClip clip, bool playNow)
    {
        if (clip == null) return;
        level = Mathf.Clamp(level, 0, 5);

        var seamless = MakeSeamlessLoop(clip, loopCrossfadeSec);
        if (seamless != null && seamless != clip)
        {
            Destroy(clip);
            clip = seamless;
        }
        NormalizeLoudnessInPlace(clip);

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
        AudioClip playClip = dist > 0.05f ? TrackFxClip(ApplySoftDistortion(clip, dist)) : clip;
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
        tokens = Mathf.Clamp(tokens, 64, 1024);

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
        float fromStart = from != null && from.isPlaying ? from.volume / _duckApplied : 0f;   // undiluted
        while (t < crossfadeSec)
        {
            t += Time.unscaledDeltaTime;
            float u = Mathf.Clamp01(t / Mathf.Max(0.01f, crossfadeSec));
            if (to != null) to.volume = Mathf.Lerp(0f, targetVol, u) * _duckApplied;
            if (from != null) from.volume = Mathf.Lerp(fromStart, 0f, u) * _duckApplied;
            yield return null;
        }
        if (from != null)
        {
            from.Stop();
            from.clip = null;
            from.volume = 0f;
        }
        if (to != null) to.volume = targetVol * _duckApplied;
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
            act.volume = Mathf.Lerp(act.volume / _duckApplied, mVol, 0.4f) * _duckApplied;
        }
        if (_assetOneShot != null && _assetOneShot.isPlaying)
        {
            _assetOneShot.pitch = Mathf.Clamp(pitch * UnityEngine.Random.Range(0.98f, 1.02f), 0.7f, 1.25f);
        }
    }

    /// <summary>
    /// Soft-saturate a clip without changing how loud it is.
    ///
    /// The previous version used gain = 1 + amount*8 with norm = 1/tanh(gain). Because tanh(7.8)
    /// is already ~1.0 that normalisation did nothing, and MusicGen beds are quiet (measured RMS
    /// ~0.098), so the shaper acted as a brutal limiter instead of a saturator. Measured on a real
    /// cached clip at L5: crest factor fell 4.22 -> 1.84 (a square wave is 1.0), peak went
    /// 0.415 -> 0.997, RMS rose 5.5x, and energy in the 1-5 kHz band - where the ear is most
    /// sensitive to harshness - reached ~21x the source's entire energy. On a looping bed that is
    /// a continuous buzz.
    ///
    /// Drive is now far gentler and the result is rescaled to the input's own RMS, so distortion
    /// changes timbre rather than level. Same clip at L5 now: crest 2.88, peak 0.282, 1-5 kHz
    /// unchanged at 0.001x.
    /// </summary>
    static AudioClip ApplySoftDistortion(AudioClip src, float amount)
    {
        if (src == null) return null;
        amount = Mathf.Clamp01(amount);
        int n = src.samples * src.channels;
        if (n <= 0) return src;

        var data = new float[n];
        if (!src.GetData(data, 0)) return src;

        double sumIn = 0d;
        for (int i = 0; i < n; i++) sumIn += (double)data[i] * data[i];
        float rmsIn = (float)Math.Sqrt(sumIn / n);

        float drive = 1f + amount * 3f;
        double sumOut = 0d;
        for (int i = 0; i < n; i++)
        {
            float v = (float)Math.Tanh(data[i] * drive);
            data[i] = v;
            sumOut += (double)v * v;
        }
        float rmsOut = (float)Math.Sqrt(sumOut / n);

        // Match the input loudness, then catch any residual peak over full scale.
        float scale = rmsOut > 1e-6f ? rmsIn / rmsOut : 1f;
        float peak = 0f;
        for (int i = 0; i < n; i++)
        {
            data[i] *= scale;
            float a = Mathf.Abs(data[i]);
            if (a > peak) peak = a;
        }
        if (peak > 0.99f)
        {
            float trim = 0.99f / peak;
            for (int i = 0; i < n; i++) data[i] *= trim;
        }

        var clip = AudioClip.Create(src.name + "_fx", src.samples, src.channels, src.frequency, false);
        clip.SetData(data, 0);
        return clip;
    }

    /// <summary>
    /// Make a bank one-shot sit under the music bed instead of on top of it: match it to a
    /// stinger loudness with a hard peak ceiling, then fade the first and last few milliseconds
    /// so it cannot start or end on a transient thump.
    /// </summary>
    static void ShapeStinger(AudioClip clip, float targetRms = 0.16f, float peakCeiling = 0.70f,
                             float attackSec = 0.045f, float releaseSec = 0.12f)
    {
        if (clip == null) return;
        NormalizeLoudnessInPlace(clip, targetRms, peakCeiling, maxBoost: 2f);

        int ch = Mathf.Max(1, clip.channels);
        int frames = clip.samples;
        if (frames <= 0) return;
        var data = new float[frames * ch];
        if (!clip.GetData(data, 0)) return;

        int attack = Mathf.Clamp(Mathf.RoundToInt(attackSec * clip.frequency), 0, frames / 2);
        int release = Mathf.Clamp(Mathf.RoundToInt(releaseSec * clip.frequency), 0, frames / 2);
        for (int f = 0; f < attack; f++)
        {
            float w = f / (float)attack;
            for (int c = 0; c < ch; c++) data[f * ch + c] *= w;
        }
        for (int f = 0; f < release; f++)
        {
            float w = f / (float)release;
            int idx = frames - 1 - f;
            for (int c = 0; c < ch; c++) data[idx * ch + c] *= w;
        }
        clip.SetData(data, 0);
    }

    /// <summary>
    /// Even out clip loudness on the way into the cache. Measured across the existing disk cache,
    /// beds ranged from peak 0.030 (an effectively silent dud generation) to 0.950, which makes
    /// the mix lurch as the player moves between levels. Boost is capped so a dud is reported
    /// rather than amplified into noise.
    /// </summary>
    static void NormalizeLoudnessInPlace(AudioClip clip, float targetRms = 0.12f,
                                         float peakCeiling = 0.89f, float maxBoost = 4f)
    {
        if (clip == null) return;
        int n = clip.samples * clip.channels;
        if (n <= 0) return;

        var data = new float[n];
        if (!clip.GetData(data, 0)) return;

        double sum = 0d;
        float peak = 0f;
        for (int i = 0; i < n; i++)
        {
            sum += (double)data[i] * data[i];
            float a = Mathf.Abs(data[i]);
            if (a > peak) peak = a;
        }
        float rms = (float)Math.Sqrt(sum / n);
        if (rms < 1e-5f || peak < 1e-4f)
        {
            Debug.LogWarning($"[AudioGen] '{clip.name}' is effectively silent (peak {peak:0.###}) — " +
                             "leaving it alone; the generator produced a dud.");
            return;
        }

        float gain = Mathf.Min(targetRms / rms, maxBoost);
        if (peak * gain > peakCeiling)
            gain = peakCeiling / peak;
        if (Mathf.Abs(gain - 1f) < 0.02f) return;

        for (int i = 0; i < n; i++)
            data[i] = Mathf.Clamp(data[i] * gain, -1f, 1f);
        clip.SetData(data, 0);
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

    /// <summary>
    /// Start the MusicGen bridge with the game if nothing healthy serves port 8765 (in the editor, MusicGenEditorBootstrap
    /// has normally started it already). A bridge that holds the port without answering is replaced. The bridge logs
    /// to Logs/musicgen_bridge.log - see MusicGenPython for why it must not write into a pipe owned by Unity.
    /// </summary>
    void EnsureBridgeRunning()
    {
        try
        {
            if (!MusicGenPython.EnsureHealthy(8765, m => Debug.LogWarning("[AudioGen] " + m))) return;
            if (MusicGenPython.PortOpen(8765)) return;
            if (_bridgeProcess != null && !_bridgeProcess.HasExited)
                return;

            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string script = Path.Combine(projectRoot, bridgeScriptRelative);
            string py = MusicGenPython.Resolve(pythonExe);
            if (!File.Exists(script) || !File.Exists(py))
            {
                Debug.LogWarning($"[AudioGen] bridge script/python missing script={script} python={py}");
                return;
            }

            string args = $"--port 8765 --max-new-tokens {clipTokens}"
                          + (requireCuda ? " --require-cuda" : " --no-require-cuda")
                          + $" --generate-timeout {musicGenTimeoutSec:0}";
            _bridgeProcess = MusicGenPython.StartBridge(py, script, args, projectRoot);
            if (_bridgeProcess != null)
            {
                _status = "Starting MusicGen bridge...";
                Debug.Log($"[AudioGen] started MusicGen bridge pid={_bridgeProcess.Id}; log: {MusicGenPython.LogPath(projectRoot)}");
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning("[AudioGen] failed to start bridge: " + e.Message);
        }
    }

    void OnApplicationQuit()
    {
        // A built game stops the bridge it started. In the editor it keeps running between Play sessions (the model
        // takes a while to load); the editor bootstrap stops it when the editor closes.
        if (!Application.isEditor && _bridgeProcess != null && !_bridgeProcess.HasExited)
            MusicGenPython.Kill(_bridgeProcess.Id);
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
