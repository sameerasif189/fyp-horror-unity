using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>
/// Milestone M5: the player's own webcam feed in a streamer-style facecam box (bottom-left, "LIVE" badge), quietly
/// tampered with to push the player from stress 0-3 into 4-5 - the proposal's "peripheral vision feedback
/// manipulation" (iteration 2: <c>camera_feed_fx.py</c>, strongest when calm, normal again at high stress).
///  - The feed is normal most of the time. After a random gap (mean <see cref="MeanGap"/> per stress level, first
///    one no sooner than <see cref="FirstEventDelay"/>) one event plays - but only once <see cref="FeedAttention"/>
///    says the player is looking at the game, so it happens in the corner of their eye.
///  - Camera events: dim, flicker, defocus, lag (the feed runs ~1.5 s behind), glitch tearing, freeze, VHS roll,
///    static, face warp, tunnel (drained colour + vignette), signal loss.
///  - Room events - something in the player's real room, made only from the camera's own pixels (a drawn or pasted
///    figure reads as fake; see docs/research/m5-photoreal-stalker):
///      shadow pass - a person's blurred shadow crosses the wall behind them;
///      light dip - the room light browns out and the camera's auto-exposure catches up; from stress 1, a dim corner
///        holds a darker human shape only while the light is down;
///      freeze reveal - the feed hangs for a moment and comes back with a faint human-shaped darkness in a corner
///        that fades over half a minute;
///      darkness creep - over ~15 s a corner darkens into a vague human shape, holds, and slowly lets go;
///      time-slip - while they are looking away, the facecam shows a recording of them staring still into it;
///      face tracking - the facecam's tracking box finds their face (normal, from calm) or a second "face" in an
///        empty dark spot (from stress 1).
///  - Stress 0-3 escalate in rate, weight and strength; at 4-5 the feed mostly behaves, the game is scary enough then.
///  - Everything moves on the webcam's own frame clock, never between camera frames.
///  - All pixel effects are one shader pass on the RawImage (<c>Resources/FeedbackCam/FeedDistortion.shader</c>); lag,
///    freeze and time-slip use small GPU copies of recent frames. Nothing is written to disk; only the face detector's
///    numbers and a 32x18 brightness map are read back to the CPU.
/// Created at runtime in any scene with a <see cref="StressController"/>; the camera opens only once gameplay starts.
/// Demo keys: F3 fires a random event now, F4 steps through the room events (both ignore where the player looks).
/// </summary>
public class FeedbackCam : MonoBehaviour
{
    public enum Effect
    {
        Dim, Flicker, Blur, Lag, Glitch, Freeze, Roll, Static, Warp, Tunnel, SignalLoss,
        ShadowPass, LightDip, FreezeReveal, DarknessCreep, TimeSlip, FaceTrack, SecondFace,
    }

    public static FeedbackCam Instance { get; private set; }
    public static bool Enabled = true;
    /// <summary>Mean seconds between events per stress level (randomised 0.5-1.5x).</summary>
    public static float[] MeanGap = { 40f, 28f, 20f, 14f, 45f, 70f };
    public static float FirstEventDelay = 20f;
    /// <summary>If the player's face cannot be tracked for this long past the due time, events fire anyway.</summary>
    public static float MaxAttentionWait = 25f;
    /// <summary>Test hook: shown instead of the webcam when set (keeps automated checks off the user's camera).</summary>
    public static Texture OverrideSource;
    /// <summary>Mirror the override like the webcam (true for a recorded webcam clip, false for a test card).</summary>
    public static bool OverrideMirrored;

    /// <summary>
    /// Editor-only guard for automated play tests: while the editor-session flag <c>FYP.NoWebcamForTests</c> is set the
    /// facecam stays hidden and never opens the camera. Statics like <see cref="Enabled"/> are reset by the domain
    /// reload on entering Play, too late to stop a game that starts itself; the session flag survives it.
    /// </summary>
    static bool WebcamBlockedForTests
    {
        get
        {
#if UNITY_EDITOR
            return UnityEditor.SessionState.GetBool("FYP.NoWebcamForTests", false);
#else
            return false;
#endif
        }
    }

    // Duration range and weight per stress level (0-5) of each event. Ineligible rows (no face, no free corner, no
    // time-slip clip yet) are skipped when picking.
    static readonly (Effect effect, float min, float max, float[] weight)[] Table =
    {
        (Effect.Dim,           1.5f, 4.0f, new[] { 3.0f, 2.0f, 1.5f, 1.0f, 1.0f, 1.0f }),
        (Effect.Flicker,       0.6f, 1.6f, new[] { 2.0f, 2.0f, 1.5f, 1.0f, 1.0f, 1.0f }),
        (Effect.Blur,          1.0f, 3.0f, new[] { 2.0f, 1.5f, 1.0f, 0.5f, 0.5f, 0.5f }),
        (Effect.Lag,           2.0f, 4.0f, new[] { 1.0f, 2.0f, 2.0f, 2.0f, 1.0f, 1.0f }),
        (Effect.Glitch,        0.2f, 1.2f, new[] { 0.5f, 1.5f, 2.0f, 2.5f, 1.0f, 1.0f }),
        (Effect.Freeze,        0.8f, 2.5f, new[] { 0.5f, 1.0f, 2.0f, 2.0f, 1.0f, 1.0f }),
        (Effect.Roll,          0.5f, 1.5f, new[] { 0.3f, 1.0f, 1.5f, 1.5f, 1.0f, 1.0f }),
        (Effect.Static,        0.2f, 0.8f, new[] { 0.3f, 1.0f, 1.5f, 2.0f, 1.0f, 1.0f }),
        (Effect.Warp,          2.0f, 3.5f, new[] { 0.0f, 0.5f, 1.0f, 1.5f, 0.5f, 0.5f }),
        (Effect.Tunnel,        2.0f, 4.0f, new[] { 0.5f, 1.0f, 1.5f, 1.5f, 1.0f, 1.0f }),
        (Effect.SignalLoss,    0.8f, 2.0f, new[] { 0.0f, 0.3f, 0.8f, 1.2f, 0.5f, 0.5f }),
        (Effect.ShadowPass,    0.7f, 1.2f, new[] { 1.2f, 1.5f, 1.5f, 1.5f, 0.6f, 0.4f }),
        (Effect.LightDip,      0.5f, 1.4f, new[] { 1.0f, 1.2f, 1.2f, 1.2f, 0.6f, 0.4f }),   // time the light is out; +1 s to settle
        (Effect.FreezeReveal,  0.35f, 0.8f, new[] { 0.0f, 0.6f, 1.0f, 1.2f, 0.5f, 0.3f }),
        (Effect.DarknessCreep, 30f,  48f,  new[] { 0.8f, 1.0f, 1.0f, 0.8f, 0.3f, 0.2f }),   // runs on its own; the timer carries on
        (Effect.TimeSlip,      2.0f, 3.5f, new[] { 0.0f, 0.5f, 1.0f, 1.2f, 0.5f, 0.3f }),
        (Effect.FaceTrack,     1.5f, 3.0f, new[] { 1.0f, 0.6f, 0.3f, 0.2f, 0.2f, 0.2f }),
        (Effect.SecondFace,    0.9f, 2.0f, new[] { 0.0f, 0.6f, 1.0f, 1.2f, 0.5f, 0.3f }),
    };

    static readonly Effect[] RoomEvents = { Effect.ShadowPass, Effect.LightDip, Effect.FreezeReveal, Effect.DarknessCreep, Effect.TimeSlip, Effect.FaceTrack, Effect.SecondFace };

    const float FeedWidth = 400f, Border = 4f, Margin = 24f;
    const int HistorySize = 24;            // frames kept for lag (2.4 s at 10 per second)
    const float HistoryInterval = 0.1f;
    const int HistoryWidth = 480;
    const int StareFrames = 16;            // the time-slip clip: 1.6 s of the player still and facing the camera
    const float StareInterval = 0.1f, StareRefresh = 45f;
    const float SyntheticFps = 15f;        // frame clock for test sources, which have no "new frame" signal

    static readonly int DimId = Shader.PropertyToID("_Dim"), BlurId = Shader.PropertyToID("_Blur"),
        GlitchId = Shader.PropertyToID("_Glitch"), NoiseId = Shader.PropertyToID("_Noise"), StaticId = Shader.PropertyToID("_Static"),
        DesatId = Shader.PropertyToID("_Desat"), VignetteId = Shader.PropertyToID("_Vignette"), RollId = Shader.PropertyToID("_Roll"),
        EchoMixId = Shader.PropertyToID("_EchoMix"), EchoId = Shader.PropertyToID("_Echo"), WarpId = Shader.PropertyToID("_Warp"),
        PixelateId = Shader.PropertyToID("_Pixelate"), SeedId = Shader.PropertyToID("_Seed"), MirrorId = Shader.PropertyToID("_Mirror"),
        FlipYId = Shader.PropertyToID("_FlipY"), AspectId = Shader.PropertyToID("_Aspect"), FeedTimeId = Shader.PropertyToID("_FeedTime"),
        ExposureId = Shader.PropertyToID("_Exposure"), ShadowRectId = Shader.PropertyToID("_ShadowRect"), ShadowAmtId = Shader.PropertyToID("_ShadowAmt"),
        SilAId = Shader.PropertyToID("_SilA"), SilAAmtId = Shader.PropertyToID("_SilAAmt"), SilBId = Shader.PropertyToID("_SilB"),
        SilBAmtId = Shader.PropertyToID("_SilBAmt"), SilRefId = Shader.PropertyToID("_SilRef"), SceneProbeId = Shader.PropertyToID("_SceneProbe"), PersonMaskId = Shader.PropertyToID("_PersonMask"), MaskValidId = Shader.PropertyToID("_MaskValid");

    Canvas _canvas;
    RectTransform _frame;
    RawImage _feed;
    Text _signalText, _liveText;
    Material _mat;
    bool _camRequested, _mirror, _flipY;
    FeedVision _vision;
    FeedAttention _attention;
    FeedScene _scene;
    TrackBox _boxA, _boxB;
    int _demoRoom;

    readonly RenderTexture[] _history = new RenderTexture[HistorySize];
    int _historyHead = -1, _historyCount;
    float _nextHistory;
    RenderTexture _frozen;

    // Time-slip clip, double-buffered so a refresh never mixes two recordings.
    RenderTexture[] _stare = new RenderTexture[StareFrames], _stareRec = new RenderTexture[StareFrames];
    int _stareRecCount;
    float _stareNext, _stareAt = -999f;

    // Webcam frame clock.
    float _feedTime, _feedDt, _nextSynthetic;
    bool _fresh;

    // The running event.
    Effect _effect;
    bool _active;
    float _start, _duration, _strength, _nextEvent = -1f, _warpSign, _flickerUntil, _flickerDim, _lagDelay;
    // shadow pass
    float _shFrom, _shTo, _shY, _shR, _shAmt;
    // light dip (+ figure A)
    float _dipHold, _dipDepth, _gain = 1f, _figAmt;
    Vector2 _figHead;
    float _figR, _figRef;
    // face boxes
    bool _secondOnly;
    Vector2 _boxCentre, _boxHalf, _ghostCentre, _ghostHalf;
    float _ghostScore;

    // The slow layer (figure B): darkness creep, or what a freeze reveal leaves behind. Outlives the event slot.
    bool _slowOn;
    Vector2 _slowHead;
    float _slowRef, _slowR, _slowSoft, _slowPeak, _slowStart, _slowIn, _slowHold, _slowOut;

    public bool EventActive => _active;
    public Effect CurrentEffect => _effect;
    public FeedVision Vision => _vision;
    public FeedAttention Attention => _attention;
    public FeedScene Scene => _scene;
    public bool StareReady => _stareAt > 0f;
    public bool SlowLayerActive => _slowOn;
    public int EventsFired { get; private set; }
    public float NextEventIn => _nextEvent < 0f ? -1f : _nextEvent - Time.time;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (Instance == null && FindAnyObjectByType<StressController>() != null)
            new GameObject("FeedbackCam (runtime)").AddComponent<FeedbackCam>();
    }

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        _mat = new Material(Resources.Load<Shader>("FeedbackCam/FeedDistortion")) { name = "FeedDistortion (runtime)" };
        _vision = new FeedVision();
        _attention = new FeedAttention();
        _scene = new FeedScene();
        BuildUi();
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
        _vision?.Dispose();
        _scene?.Dispose();
        foreach (var set in new[] { _history, _stare, _stareRec })
            foreach (var rt in set) if (rt != null) { rt.Release(); Destroy(rt); }
        if (_frozen != null) { _frozen.Release(); Destroy(_frozen); }
        if (_mat != null) Destroy(_mat);
    }

    void Update()
    {
        // Without a start menu, wait a moment before assuming gameplay: the menu may not exist yet on the first
        // frames, and treating that as "playing" switched the webcam on behind the menu.
        bool gameplay = GameStartMenu.GameplayActive ||
                        (GameStartMenu.Instance == null && Time.timeSinceLevelLoad > 2f && FindAnyObjectByType<GameStartMenu>() == null);
        _canvas.enabled = Enabled && gameplay && !WebcamBlockedForTests;
        if (!_canvas.enabled) return;

        if (!_camRequested && OverrideSource == null)
        {
            WebcamSource.Ensure();
            _camRequested = true;
        }

        var source = OverrideSource != null ? OverrideSource : WebcamSource.Instance != null && WebcamSource.Instance.IsReady ? WebcamSource.Instance.Texture : null;
        _feed.texture = source;
        FitToSource(source);
        _mirror = OverrideSource == null || OverrideMirrored;   // selfie view, like every streamer's facecam
        _flipY = OverrideSource == null && WebcamSource.Instance != null && WebcamSource.Instance.FlipY;
        _mat.SetFloat(MirrorId, _mirror ? 1f : 0f);
        _mat.SetFloat(FlipYId, _flipY ? 1f : 0f);

        if (source == null)
        {
            NoSignal(WebcamSource.Instance != null && WebcamSource.Instance.Status != "starting" ? "NO CAMERA" : "CONNECTING");
            return;
        }
        _liveText.text = "● LIVE";

        TickFrameClock(source);
        RecordHistory(source);
        _vision.Tick(source);
        _attention.Tick(_vision);
        _scene.Tick(source, _vision);
        RecordStare(source);
        _mat.SetFloat(AspectId, Aspect(source));
        var kb = Keyboard.current;
        if (kb != null && kb.f3Key.wasPressedThisFrame) Trigger(PickEffect(Level()));
        if (kb != null && kb.f4Key.wasPressedThisFrame) TriggerNextRoomEvent();
        Schedule();
        Apply();
    }

    static float Aspect(Texture t) => t != null ? (float)t.width / Mathf.Max(1, t.height) : 16f / 9f;

    /// <summary>Advance the effect clock only when the camera delivers a frame, so nothing moves between frames.</summary>
    void TickFrameClock(Texture source)
    {
        if (source is WebCamTexture cam) _fresh = cam.didUpdateThisFrame;
        else
        {
            _fresh = Time.time >= _nextSynthetic;
            if (_fresh) _nextSynthetic = Time.time + 1f / SyntheticFps;
        }
        if (!_fresh) return;
        _feedDt = Mathf.Clamp(Time.time - _feedTime, 0f, 0.25f);
        _feedTime = Time.time;
        _mat.SetFloat(FeedTimeId, _feedTime);
    }

    /// <summary>F4 demo: the next room event that can run right now.</summary>
    void TriggerNextRoomEvent()
    {
        for (int i = 0; i < RoomEvents.Length; i++)
            if (Trigger(RoomEvents[_demoRoom++ % RoomEvents.Length], demo: true)) return;
    }

    static int Level() => StressController.Instance != null ? Mathf.Clamp(StressController.Instance.StressLevel, 0, 5) : 0;

    void Schedule()
    {
        float now = Time.time;
        if (_nextEvent < 0f) _nextEvent = now + FirstEventDelay + Random.Range(0f, MeanGap[Level()] * 0.5f);
        if (_active && now - _start >= _duration) End();
        if (_active || now < _nextEvent) return;
        // Wait until the player is looking at the game, so it plays in the corner of their eye. If their face cannot be
        // tracked for a long while (dark room, covered camera), fall back to the timer alone.
        if (!_attention.LookingAtGame && now - _nextEvent < MaxAttentionWait) return;
        if (!Trigger(PickEffect(Level()))) _nextEvent = now + 1f;
    }

    void End()
    {
        _active = false;
        if (_effect == Effect.FreezeReveal)
            // The feed comes back with something new in the corner, fading over half a minute.
            StartSlow(_figHead, _figR, 0.9f, 0.26f * _strength, 0f, Random.Range(2f, 4f), Random.Range(25f, 40f));
        _nextEvent = Time.time + MeanGap[Level()] * Random.Range(0.5f, 1.5f);
    }

    Effect PickEffect(int level)
    {
        float total = 0f;
        foreach (var row in Table) total += CanRun(row.effect) ? row.weight[level] : 0f;
        float r = Random.value * total;
        foreach (var row in Table)
        {
            if (!CanRun(row.effect)) continue;
            r -= row.weight[level];
            if (r <= 0f) return row.effect;
        }
        return Effect.Dim;
    }

    bool CanRun(Effect e)
    {
        switch (e)
        {
            case Effect.ShadowPass: return _vision.HasMask;
            case Effect.FreezeReveal:
            case Effect.DarknessCreep: return !_slowOn && _vision.HasMask && _scene.FindSpot(_vision, _mat.GetFloat(AspectId), FigureRadius(), true, out _);
            case Effect.TimeSlip: return StareReady;
            case Effect.FaceTrack: return _vision.FaceFound;
            case Effect.SecondFace: return _vision.FaceFound && _scene.FindSpot(_vision, _mat.GetFloat(AspectId), FigureRadius(), false, out _, UnderBadge);
            default: return true;
        }
    }

    /// <summary>Head radius (feed heights) of someone across the room: well under the player's, who is at arm's length.</summary>
    float FigureRadius() => Mathf.Clamp((_vision.FaceFound ? _vision.FaceSize.y : 0.4f) * 0.5f * Random.Range(0.3f, 0.45f), 0.04f, 0.09f);

    /// <summary>Start an event now (also the demo keys and the test hook). False if it cannot run right now.</summary>
    public bool Trigger(Effect effect, bool demo = false)
    {
        if (!CanRun(effect)) return false;
        int level = Level();
        var row = System.Array.Find(Table, x => x.effect == effect);
        float aspect = _mat.GetFloat(AspectId);
        _effect = effect;
        _active = true;
        _start = Time.time;
        _duration = Random.Range(row.min, row.max);
        // Subtle when calm, full strength by stress 3; 4-5 sit in between. Demos show the stress-3 version.
        _strength = demo ? 1f : level <= 3 ? Mathf.Lerp(0.55f, 1f, level / 3f) : 0.8f;
        _warpSign = Random.value < 0.5f ? -1f : 1f;
        _lagDelay = Random.Range(1.2f, 2.2f);
        _mat.SetFloat(SeedId, Random.value * 100f);
        var face = _vision.FaceFound ? _vision.FaceCenter : new Vector2(0.5f, 0.45f);
        float faceH = _vision.FaceFound ? _vision.FaceSize.y : 0.4f;

        switch (effect)
        {
            case Effect.Freeze:
                CopyLatest(ref _frozen);
                break;
            case Effect.ShadowPass:
            {
                // Someone walking between a lamp and the wall: a big, blurred shape crossing at head height.
                bool leftToRight = Random.value < 0.5f;
                _shR = Mathf.Clamp(faceH * 0.5f * Random.Range(0.6f, 0.9f), 0.08f, 0.2f);
                float off = _shR * 3f / Mathf.Max(0.5f, aspect);
                _shFrom = leftToRight ? -off : 1f + off;
                _shTo = leftToRight ? 1f + off : -off;
                // At the height of the most lit wall behind them: a shadow on something already dark cannot be seen.
                if (!_scene.FindShadowBand(_shR, out _shY)) _shY = Mathf.Clamp(face.y + Random.Range(0.02f, 0.12f), 0.35f, 0.85f);
                _shAmt = Random.Range(0.35f, 0.5f) * _strength;
                break;
            }
            case Effect.LightDip:
            {
                _dipHold = _duration;
                _duration += 1f;   // the camera's exposure settling after the light comes back
                _dipDepth = Random.Range(0.45f, 0.7f) * Mathf.Lerp(0.7f, 1f, _strength);
                _gain = 1f;
                // From stress 1 a dim corner sometimes holds a darker human shape - only while the light is down.
                _figAmt = 0f;
                if ((level >= 1 || demo) && Random.value < (demo ? 1f : 0.7f) && _vision.HasMask)
                {
                    _figR = FigureRadius();
                    if (_scene.FindSpot(_vision, aspect, _figR, true, out _figHead))
                    {
                        _figAmt = 0.5f * _strength;
                        _figRef = Mathf.Sqrt(_scene.LumAt(_figHead, _figR, aspect));
                    }
                }
                break;
            }
            case Effect.FreezeReveal:
                CopyLatest(ref _frozen);
                _figR = FigureRadius();
                _scene.FindSpot(_vision, aspect, _figR, true, out _figHead);
                _figRef = Mathf.Sqrt(_scene.LumAt(_figHead, _figR, aspect));
                break;
            case Effect.DarknessCreep:
            {
                float r = FigureRadius();
                _scene.FindSpot(_vision, aspect, r, true, out var head);
                float peak = Random.Range(0.3f, 0.42f) * _strength;
                if (demo) StartSlow(head, r, 1.4f, peak, 3f, 4f, 3f);
                else StartSlow(head, r, 1.4f, peak, Random.Range(12f, 18f), Random.Range(6f, 12f), Random.Range(12f, 18f));
                // It runs on its own layer; the event slot and timer carry on.
                _active = false;
                _nextEvent = Time.time + MeanGap[level] * Random.Range(0.5f, 1.5f);
                break;
            }
            case Effect.TimeSlip:
                CopyLatest(ref _frozen);   // a hitch as it cuts over, like a dropped frame
                break;
            case Effect.FaceTrack:
                _boxCentre = face;
                _boxHalf = _vision.FaceSize * 0.55f;
                break;
            case Effect.SecondFace:
            {
                _boxCentre = face;
                _boxHalf = _vision.FaceSize * 0.55f;
                float r = FigureRadius();
                _scene.FindSpot(_vision, aspect, r, false, out _ghostCentre, UnderBadge);
                _ghostHalf = new Vector2(r * 1.2f / Mathf.Max(0.5f, aspect), r * 1.35f);
                _ghostScore = Random.Range(0.38f, 0.62f);
                // Sometimes the tracker loses the player and boxes only the empty spot.
                _secondOnly = Random.value < 0.35f;
                _boxB.Place(Disp(_ghostCentre), _ghostHalf);
                _boxB.Label.text = $"FACE {(_secondOnly ? 1 : 2)}  {_ghostScore:0.00}";
                break;
            }
        }
        EventsFired++;
        return true;
    }

    void StartSlow(Vector2 head, float r, float soft, float peak, float fadeIn, float hold, float fadeOut)
    {
        _slowOn = true;
        _slowRef = Mathf.Sqrt(_scene.LumAt(head, r, _mat.GetFloat(AspectId)));
        _slowHead = head;
        _slowR = r;
        _slowSoft = soft;
        _slowPeak = peak;
        _slowStart = _feedTime;
        _slowIn = fadeIn;
        _slowHold = hold;
        _slowOut = fadeOut;
    }

    void Apply()
    {
        float dim = 0, blur = 0, glitch = 0, noise = 0, snow = 0, desat = 0, vignette = 0, roll = 0, echoMix = 0, warp = 0, pixelate = 0;
        float exposure = 1f, shadow = 0f, figA = 0f;
        Texture echo = Texture2D.blackTexture;
        bool signalLost = false, showA = false, showB = false;
        float aspect = _mat.GetFloat(AspectId);
        if (_active)
        {
            float t = Mathf.Max(0f, _feedTime - _start), k = _strength;
            bool soft = _effect == Effect.Dim || _effect == Effect.Blur || _effect == Effect.Tunnel || _effect == Effect.Warp;
            float attack = soft ? 0.45f : 0.03f, release = soft ? 0.7f : 0.05f;
            float env = Mathf.Clamp01(t / attack) * Mathf.Clamp01((_duration - t) / release);
            // Bursty effects stutter on and off rather than holding.
            float burst = Mathf.PerlinNoise(t * 9f, _start) > 0.35f ? 1f : 0.25f;
            switch (_effect)
            {
                case Effect.Dim: dim = 0.6f * k * env; noise = 0.12f * env; break;
                case Effect.Flicker:
                    if (_feedTime >= _flickerUntil) { _flickerUntil = _feedTime + Random.Range(0.03f, 0.12f); _flickerDim = Random.value < 0.5f ? Random.Range(0.6f, 0.95f) : 0f; }
                    dim = _flickerDim * env; break;
                case Effect.Blur: blur = 0.7f * k * env; break;
                case Effect.Lag:
                    echo = Delayed(_lagDelay) ?? echo; echoMix = echo == Texture2D.blackTexture ? 0f : env; break;
                case Effect.Glitch: glitch = (0.5f + 0.5f * k) * env * burst; noise = 0.2f * env; pixelate = Random.value < 0.08f ? 0.4f * env : 0f; break;
                case Effect.Freeze: echo = _frozen != null ? _frozen : echo; echoMix = _frozen != null ? env : 0f; noise = 0.08f * env; break;
                case Effect.Roll: roll = k * env; noise = 0.25f * env; break;
                case Effect.Static: snow = 0.9f * env * burst; noise = 0.5f * env; break;
                case Effect.Warp: warp = 0.85f * k * env * _warpSign; break;
                case Effect.Tunnel: desat = 0.9f * env; vignette = 0.95f * k * env; dim = 0.2f * env; break;
                case Effect.SignalLoss: snow = env; signalLost = env > 0.5f; break;

                case Effect.ShadowPass:
                {
                    // Walking pace across the frame, with a slight step bob.
                    float u = Mathf.Clamp01(t / _duration);
                    float x = Mathf.Lerp(_shFrom, _shTo, u);
                    float y = _shY + Mathf.Sin(u * Mathf.PI * 6f) * _shR * 0.08f;
                    _mat.SetVector(ShadowRectId, new Vector4(x, y, _shR, 1.4f));
                    shadow = _shAmt;
                    break;
                }
                case Effect.LightDip:
                {
                    // The light drops, the camera's auto-exposure lifts the gain (and the grain) to compensate; when
                    // the light comes back the picture overshoots bright and settles.
                    float light = t < _dipHold ? Mathf.Lerp(1f, 1f - _dipDepth, Mathf.Clamp01(t / 0.06f))
                                               : Mathf.Lerp(1f - _dipDepth, 1f, Mathf.Clamp01((t - _dipHold) / 0.05f));
                    if (_fresh)
                    {
                        float target = Mathf.Clamp(1f / light, 1f, 1.9f);
                        _gain += (target - _gain) * (1f - Mathf.Exp(-_feedDt / 0.35f));
                    }
                    exposure = light * _gain;
                    noise = 0.03f + 0.3f * (_gain - 1f);
                    if (_figAmt > 0f)
                    {
                        _mat.SetVector(SilAId, new Vector4(_figHead.x, _figHead.y, _figR, 0.7f));
                        figA = _figAmt * Mathf.Clamp01((1f - light) / _dipDepth);
                    }
                    break;
                }
                case Effect.FreezeReveal:
                    echo = _frozen != null ? _frozen : echo; echoMix = _frozen != null ? 1f : 0f; noise = 0.03f;
                    break;
                case Effect.TimeSlip:
                {
                    const float hitch = 0.12f;
                    if (t < hitch || _stare[0] == null) { echo = _frozen != null ? _frozen : echo; }
                    else
                    {
                        // Ping-pong through the still, staring recording at the rate it was taken.
                        int i = Mathf.RoundToInt(Mathf.PingPong((t - hitch) / StareInterval, StareFrames - 1));
                        echo = _stare[i];
                    }
                    echoMix = echo == Texture2D.blackTexture ? 0f : 1f;
                    break;
                }
                case Effect.FaceTrack:
                    showA = TrackFace(t);
                    break;
                case Effect.SecondFace:
                    showA = !_secondOnly && TrackFace(t);
                    showB = t > (_secondOnly ? 0f : 0.3f);
                    if (showB && _fresh)
                    {
                        // A real tracker's box never sits still.
                        var jitter = new Vector2(Random.Range(-1f, 1f), Random.Range(-1f, 1f)) * 0.04f;
                        _boxB.Place(Disp(_ghostCentre + Vector2.Scale(jitter, _ghostHalf)), _ghostHalf * Random.Range(0.96f, 1.04f));
                        _boxB.Label.text = $"FACE {(_secondOnly ? 1 : 2)}  {(_ghostScore + Random.Range(-0.05f, 0.05f)):0.00}";
                    }
                    break;
            }
        }

        // Slow layer: darkness creep, or a freeze reveal's leftover, fading on its own clock.
        float figB = 0f;
        if (_slowOn)
        {
            float st = _feedTime - _slowStart;
            float a = st < _slowIn ? st / Mathf.Max(0.01f, _slowIn) : st < _slowIn + _slowHold ? 1f : 1f - (st - _slowIn - _slowHold) / Mathf.Max(0.01f, _slowOut);
            if (st > _slowIn + _slowHold + _slowOut) _slowOn = false;
            else
            {
                figB = _slowPeak * Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(a));
                _mat.SetVector(SilBId, new Vector4(_slowHead.x, _slowHead.y, _slowR, _slowSoft));
            }
        }

        _boxA.Show(showA);
        _boxB.Show(showB);
        _mat.SetFloat(DimId, dim);
        _mat.SetFloat(BlurId, blur);
        _mat.SetFloat(GlitchId, glitch);
        _mat.SetFloat(NoiseId, noise);
        _mat.SetFloat(StaticId, snow);
        _mat.SetFloat(DesatId, desat);
        _mat.SetFloat(VignetteId, vignette);
        _mat.SetFloat(RollId, roll);
        _mat.SetFloat(EchoMixId, echoMix);
        _mat.SetTexture(EchoId, echo);
        _mat.SetFloat(WarpId, warp);
        _mat.SetFloat(PixelateId, pixelate);
        _mat.SetFloat(ExposureId, exposure);
        _mat.SetFloat(ShadowAmtId, shadow);
        _mat.SetFloat(SilAAmtId, figA);
        _mat.SetFloat(SilBAmtId, figB);
        _mat.SetVector(SilRefId, new Vector4(_figRef, _slowRef, 0f, 0f));
        _mat.SetTexture(SceneProbeId, _scene.Probe);
        _mat.SetTexture(PersonMaskId, _vision.Mask);
        _mat.SetFloat(MaskValidId, _vision.HasMask ? 1f : 0f);
        _signalText.enabled = signalLost;
        _signalText.text = "NO SIGNAL";
    }

    /// <summary>The tracking box on the player's face: snaps in from a little larger, then follows (per camera frame).</summary>
    bool TrackFace(float t)
    {
        if (!_vision.FaceFound) return false;
        if (_fresh)
        {
            _boxCentre = Vector2.Lerp(_boxCentre, _vision.FaceCenter, 0.5f);
            _boxHalf = Vector2.Lerp(_boxHalf, _vision.FaceSize * 0.55f, 0.5f);
        }
        float snap = 1f + 0.3f * (1f - Mathf.Clamp01(t / 0.15f));
        _boxA.Place(Disp(_boxCentre), _boxHalf * snap);
        _boxA.Label.text = $"FACE  {_vision.FaceScore:0.00}";
        return true;
    }

    /// <summary>Would a tracking box here sit on the "LIVE" badge (top-left of the facecam)?</summary>
    bool UnderBadge(Vector2 uv) { var d = Disp(uv); return d.x < 0.3f && d.y > 0.7f; }

    /// <summary>Raw feed uv (what the vision works in) to where it shows in the mirrored / flipped facecam.</summary>
    Vector2 Disp(Vector2 uv) => new Vector2(_mirror ? 1f - uv.x : uv.x, _flipY ? 1f - uv.y : uv.y);

    void NoSignal(string label)
    {
        _active = false;
        foreach (var id in new[] { DimId, BlurId, GlitchId, DesatId, VignetteId, RollId, EchoMixId, WarpId, PixelateId, ShadowAmtId, SilAAmtId, SilBAmtId }) _mat.SetFloat(id, 0f);
        _mat.SetFloat(ExposureId, 1f);
        _mat.SetFloat(StaticId, 1f);
        _mat.SetFloat(NoiseId, 0.4f);
        _mat.SetFloat(FeedTimeId, Time.time);
        _boxA.Show(false);
        _boxB.Show(false);
        _signalText.enabled = true;
        _signalText.text = label;
        _liveText.text = "● OFFLINE";
    }

    // ---- recent frames (GPU only) ----

    void RecordHistory(Texture source)
    {
        if (Time.time < _nextHistory) return;
        _nextHistory = Time.time + HistoryInterval;
        _historyHead = (_historyHead + 1) % HistorySize;
        Copy(source, ref _history[_historyHead]);
        _historyCount = Mathf.Min(_historyCount + 1, HistorySize);
    }

    /// <summary>
    /// The time-slip clip: 1.6 s of the player still and facing the camera, re-recorded every <see cref="StareRefresh"/> s
    /// so their clothes and light match. Recording restarts whenever they move.
    /// </summary>
    void RecordStare(Texture source)
    {
        if (_active && _effect == Effect.TimeSlip) return;
        if (StareReady && Time.time - _stareAt < StareRefresh) return;
        if (!_attention.StillAndFrontal) { _stareRecCount = 0; return; }
        if (Time.time < _stareNext) return;
        _stareNext = Time.time + StareInterval;
        Copy(source, ref _stareRec[_stareRecCount++]);
        if (_stareRecCount < StareFrames) return;
        (_stare, _stareRec) = (_stareRec, _stare);
        _stareRecCount = 0;
        _stareAt = Time.time;
    }

    void Copy(Texture source, ref RenderTexture rt)
    {
        int h = Mathf.Max(1, Mathf.RoundToInt(HistoryWidth * (float)source.height / Mathf.Max(1, source.width)));
        if (rt == null || rt.height != h) { if (rt != null) { rt.Release(); Destroy(rt); } rt = new RenderTexture(HistoryWidth, h, 0, RenderTextureFormat.ARGB32); }
        Graphics.Blit(source, rt);
    }

    /// <summary>The recorded frame closest to <paramref name="seconds"/> ago, or null if there is no history yet.</summary>
    RenderTexture Delayed(float seconds)
    {
        if (_historyCount == 0) return null;
        int back = Mathf.Min(_historyCount - 1, Mathf.RoundToInt(seconds / HistoryInterval));
        return _history[(_historyHead - back + HistorySize) % HistorySize];
    }

    void CopyLatest(ref RenderTexture target)
    {
        var latest = Delayed(0f);
        if (latest == null) return;
        if (target == null || target.width != latest.width || target.height != latest.height)
        {
            if (target != null) { target.Release(); Destroy(target); }
            target = new RenderTexture(latest.width, latest.height, 0, RenderTextureFormat.ARGB32);
        }
        Graphics.Blit(latest, target);
    }

    // ---- UI ----

    void BuildUi()
    {
        var root = new GameObject("FeedbackCamCanvas");
        root.transform.SetParent(transform, false);
        _canvas = root.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.sortingOrder = 55;
        var scaler = root.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);

        var frame = Panel(root.transform, "Frame", new Color(0.05f, 0.06f, 0.08f, 0.92f));
        _frame = frame.rectTransform;
        _frame.anchorMin = _frame.anchorMax = _frame.pivot = Vector2.zero;
        _frame.anchoredPosition = new Vector2(Margin, Margin);

        var edge = Panel(_frame, "Edge", new Color(0.55f, 0.08f, 0.08f, 0.9f));
        Stretch(edge.rectTransform, 1f);

        var feedGo = new GameObject("Feed", typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
        feedGo.transform.SetParent(_frame, false);
        _feed = feedGo.GetComponent<RawImage>();
        _feed.material = _mat;
        _feed.color = Color.white;
        Stretch(_feed.rectTransform, Border);

        _liveText = Label(_feed.transform, "Live", "● LIVE", 17, TextAnchor.UpperLeft, new Color(1f, 0.25f, 0.22f));
        _liveText.fontStyle = FontStyle.Bold;
        Place(_liveText.rectTransform, new Vector2(0.03f, 0.78f), new Vector2(0.6f, 0.97f));
        var cam = Label(_feed.transform, "Cam", "CAM 01", 13, TextAnchor.LowerRight, new Color(1f, 1f, 1f, 0.75f));
        Place(cam.rectTransform, new Vector2(0.5f, 0.03f), new Vector2(0.97f, 0.2f));
        _signalText = Label(_feed.transform, "Signal", "NO SIGNAL", 26, TextAnchor.MiddleCenter, Color.white);
        _signalText.fontStyle = FontStyle.Bold;
        Place(_signalText.rectTransform, Vector2.zero, Vector2.one);
        _signalText.enabled = false;
        foreach (var t in new[] { _liveText, cam, _signalText }) t.gameObject.AddComponent<Shadow>().effectColor = new Color(0f, 0f, 0f, 0.8f);

        _boxA = new TrackBox(_feed.transform, "TrackA");
        _boxB = new TrackBox(_feed.transform, "TrackB");
        FitToSource(null);
    }

    /// <summary>A face-tracking box drawn by "the camera software": corner brackets and a small readout.</summary>
    sealed class TrackBox
    {
        static readonly Color Ink = new Color(0.62f, 1f, 0.55f, 0.85f);
        public readonly RectTransform Root;
        public readonly Text Label;

        public TrackBox(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            Root = (RectTransform)go.transform;
            const float len = 0.28f, thick = 2f;
            for (int c = 0; c < 4; c++)
            {
                float sx = c & 1, sy = c >> 1;
                var h = Panel(Root, "H", Ink).rectTransform;
                h.anchorMin = new Vector2(sx == 0 ? 0f : 1f - len, sy);
                h.anchorMax = new Vector2(sx == 0 ? len : 1f, sy);
                h.pivot = new Vector2(0.5f, sy);
                h.sizeDelta = new Vector2(0f, thick);
                h.anchoredPosition = Vector2.zero;
                var v = Panel(Root, "V", Ink).rectTransform;
                v.anchorMin = new Vector2(sx, sy == 0 ? 0f : 1f - len);
                v.anchorMax = new Vector2(sx, sy == 0 ? len : 1f);
                v.pivot = new Vector2(sx, 0.5f);
                v.sizeDelta = new Vector2(thick, 0f);
                v.anchoredPosition = Vector2.zero;
            }
            Label = FeedbackCam.Label(Root, "Readout", "FACE", 11, TextAnchor.LowerLeft, Ink);
            var lr = Label.rectTransform;
            lr.anchorMin = lr.anchorMax = new Vector2(0f, 1f);
            lr.pivot = Vector2.zero;
            lr.sizeDelta = new Vector2(120f, 14f);
            lr.anchoredPosition = new Vector2(0f, 2f);
            go.SetActive(false);
        }

        public void Show(bool on) { if (Root.gameObject.activeSelf != on) Root.gameObject.SetActive(on); }

        /// <summary>Centre and half size in facecam display uv (already mirrored).</summary>
        public void Place(Vector2 centre, Vector2 half)
        {
            Root.anchorMin = centre - half;
            Root.anchorMax = centre + half;
            Root.offsetMin = Root.offsetMax = Vector2.zero;
        }
    }

    void FitToSource(Texture source)
    {
        float aspect = source != null && source.width > 16 ? (float)source.height / source.width : 9f / 16f;
        _frame.sizeDelta = new Vector2(FeedWidth + Border * 2f, FeedWidth * aspect + Border * 2f);
    }

    static Image Panel(Transform parent, string name, Color color)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        go.transform.SetParent(parent, false);
        var img = go.GetComponent<Image>();
        img.color = color;
        img.raycastTarget = false;
        return img;
    }

    static Text Label(Transform parent, string name, string msg, int size, TextAnchor align, Color color)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
        go.transform.SetParent(parent, false);
        var t = go.GetComponent<Text>();
        t.text = msg;
        t.fontSize = size;
        t.alignment = align;
        t.color = color;
        t.raycastTarget = false;
        t.horizontalOverflow = HorizontalWrapMode.Overflow;
        t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        return t;
    }

    static void Stretch(RectTransform rt, float inset)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(inset, inset);
        rt.offsetMax = new Vector2(-inset, -inset);
    }

    static void Place(RectTransform rt, Vector2 min, Vector2 max)
    {
        rt.anchorMin = min;
        rt.anchorMax = max;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
    }
}
