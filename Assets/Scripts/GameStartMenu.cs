using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem.UI;
#endif

/// <summary>
/// Title / start screen: Load MusicGen then Start Game.
/// </summary>
public class GameStartMenu : MonoBehaviour
{
    public static GameStartMenu Instance { get; private set; }
    public static bool GameplayActive { get; private set; }

    [SerializeField] AudioGenerationRunner audioRunner;
    [SerializeField] bool buildUiAtRuntime = true;

    Canvas _canvas;
    Text _title;
    Text _status;
    Text _detail;
    Button _loadBtn;
    Button _startBtn;
    Text _loadLabel;
    Text _startLabel;
    bool _loading;
    bool _audioReady;

    void Awake()
    {
        Instance = this;
        GameplayActive = false;
        if (audioRunner == null)
            audioRunner = FindFirstObjectByType<AudioGenerationRunner>();
    }

    void Start()
    {
        if (buildUiAtRuntime)
            BuildUi();

        // Freeze gameplay systems until Start Game
        SetGameplayEnabled(false);
        if (audioRunner != null && audioRunner.TryUseExistingPreload())
        {
            _audioReady = true;
            SetStatus("MusicGen already preloaded.",
                $"{audioRunner.CachedClipCount} clips on disk — press START GAME.");
        }
        else
        {
            SetStatus("Press LOAD AUDIO to preload MusicGen.",
                "Diverse MusicGen + random clips from assets/audio/{stress}.");
        }
        RefreshButtons();
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    void Update()
    {
        if (_loading && audioRunner != null)
        {
            SetStatus(audioRunner.StatusText, $"Bridge/device · {audioRunner.LastSource}");
            if (audioRunner.IsReady && !_audioReady)
            {
                _audioReady = true;
                _loading = false;
                SetStatus("Audio ready.", $"{audioRunner.StatusText}\nYou can Start Game.");
                RefreshButtons();
            }
        }
    }

    public void OnClickLoadAudio()
    {
        if (_loading || _audioReady) return;
        if (audioRunner == null)
        {
            audioRunner = FindFirstObjectByType<AudioGenerationRunner>();
            if (audioRunner == null)
            {
                SetStatus("No AudioGenerationRunner in scene.", "");
                return;
            }
        }

        _loading = true;
        RefreshButtons();
        SetStatus("Loading MusicGen preload…", "Builds a starter buffer, then live generation runs after Start Game.");
        audioRunner.BeginAudioLoadFromMenu();
    }

    public void OnClickStartGame()
    {
        if (!_audioReady)
        {
            SetStatus("Load audio first.", "Preload MusicGen before starting — live gen continues in-game.");
            return;
        }

        GameplayActive = true;
        SetGameplayEnabled(true);
        audioRunner?.NotifyGameStarted();
        if (_canvas != null)
            _canvas.gameObject.SetActive(false);
        if (GameplayHud.Instance != null)
            GameplayHud.Instance.Show();
        Debug.Log("[StartMenu] Game started — MusicGen only.");
    }

    void SetGameplayEnabled(bool on)
    {
        var player = FindFirstObjectByType<PlayerController>();
        if (player != null)
        {
            player.enabled = on;
            var cc = player.GetComponent<CharacterController>();
            if (cc != null) cc.enabled = on;
        }

        var stress = FindFirstObjectByType<StressController>();
        if (stress != null)
            stress.enabled = on;

        // Unlock cursor on menu, lock when playing
        Cursor.lockState = on ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible = !on;
    }

    void RefreshButtons()
    {
        if (_loadBtn != null)
        {
            _loadBtn.interactable = !_loading && !_audioReady;
            if (_loadLabel != null)
                _loadLabel.text = _audioReady ? "AUDIO LOADED" : (_loading ? "LOADING…" : "LOAD AUDIO");
        }
        if (_startBtn != null)
        {
            _startBtn.interactable = _audioReady && !_loading;
            if (_startLabel != null)
                _startLabel.text = "START GAME";
        }
    }

    void SetStatus(string main, string detail)
    {
        if (_status != null) _status.text = main ?? "";
        if (_detail != null) _detail.text = detail ?? "";
    }

    void BuildUi()
    {
        EnsureEventSystem();

        var root = new GameObject("StartMenuCanvas");
        root.transform.SetParent(transform, false);
        _canvas = root.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.sortingOrder = 100;
        var scaler = root.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        root.AddComponent<GraphicRaycaster>();

        // Dim backdrop
        var bg = CreatePanel(root.transform, "Backdrop", new Color(0.04f, 0.05f, 0.07f, 0.92f));
        StretchFull(bg.GetComponent<RectTransform>());

        var panel = CreatePanel(root.transform, "Panel", new Color(0.08f, 0.09f, 0.11f, 0.96f));
        var pr = panel.GetComponent<RectTransform>();
        pr.anchorMin = new Vector2(0.5f, 0.5f);
        pr.anchorMax = new Vector2(0.5f, 0.5f);
        pr.sizeDelta = new Vector2(640, 420);
        pr.anchoredPosition = Vector2.zero;

        _title = CreateText(panel.transform, "Title", "ADAPTIVE HORROR", 42, TextAnchor.MiddleCenter);
        var tr = _title.GetComponent<RectTransform>();
        tr.anchorMin = new Vector2(0.08f, 0.72f);
        tr.anchorMax = new Vector2(0.92f, 0.92f);
        tr.offsetMin = tr.offsetMax = Vector2.zero;
        _title.fontStyle = FontStyle.Bold;
        _title.color = new Color(0.92f, 0.86f, 0.78f);

        var sub = CreateText(panel.transform, "Subtitle", "Preload MusicGen before entering the room", 18, TextAnchor.MiddleCenter);
        var sr = sub.GetComponent<RectTransform>();
        sr.anchorMin = new Vector2(0.1f, 0.62f);
        sr.anchorMax = new Vector2(0.9f, 0.74f);
        sr.offsetMin = sr.offsetMax = Vector2.zero;
        sub.color = new Color(0.7f, 0.68f, 0.64f);

        _status = CreateText(panel.transform, "Status", "", 17, TextAnchor.MiddleCenter);
        var st = _status.GetComponent<RectTransform>();
        st.anchorMin = new Vector2(0.08f, 0.48f);
        st.anchorMax = new Vector2(0.92f, 0.62f);
        st.offsetMin = st.offsetMax = Vector2.zero;
        _status.color = new Color(0.85f, 0.82f, 0.75f);

        _detail = CreateText(panel.transform, "Detail", "", 14, TextAnchor.UpperCenter);
        var dr = _detail.GetComponent<RectTransform>();
        dr.anchorMin = new Vector2(0.08f, 0.34f);
        dr.anchorMax = new Vector2(0.92f, 0.48f);
        dr.offsetMin = dr.offsetMax = Vector2.zero;
        _detail.color = new Color(0.55f, 0.55f, 0.52f);

        _loadBtn = CreateButton(panel.transform, "LoadBtn", out _loadLabel);
        PlaceButton(_loadBtn.GetComponent<RectTransform>(), new Vector2(0.12f, 0.12f), new Vector2(0.48f, 0.28f));
        _loadBtn.onClick.AddListener(OnClickLoadAudio);
        _loadLabel.text = "LOAD AUDIO";

        _startBtn = CreateButton(panel.transform, "StartBtn", out _startLabel);
        PlaceButton(_startBtn.GetComponent<RectTransform>(), new Vector2(0.52f, 0.12f), new Vector2(0.88f, 0.28f));
        _startBtn.onClick.AddListener(OnClickStartGame);
        _startLabel.text = "START GAME";
        var startColors = _startBtn.colors;
        startColors.normalColor = new Color(0.45f, 0.18f, 0.16f);
        startColors.highlightedColor = new Color(0.6f, 0.24f, 0.2f);
        startColors.pressedColor = new Color(0.35f, 0.12f, 0.1f);
        startColors.disabledColor = new Color(0.25f, 0.25f, 0.25f, 0.6f);
        _startBtn.colors = startColors;
    }

    static void EnsureEventSystem()
    {
        if (FindFirstObjectByType<EventSystem>() != null) return;
        var es = new GameObject("EventSystem");
        es.AddComponent<EventSystem>();
#if ENABLE_INPUT_SYSTEM
        es.AddComponent<InputSystemUIInputModule>();
#else
        es.AddComponent<StandaloneInputModule>();
#endif
    }

    static GameObject CreatePanel(Transform parent, string name, Color color)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        go.transform.SetParent(parent, false);
        go.GetComponent<Image>().color = color;
        return go;
    }

    static Text CreateText(Transform parent, string name, string msg, int size, TextAnchor align)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
        go.transform.SetParent(parent, false);
        var t = go.GetComponent<Text>();
        t.text = msg;
        t.fontSize = size;
        t.alignment = align;
        t.color = Color.white;
        t.horizontalOverflow = HorizontalWrapMode.Wrap;
        t.verticalOverflow = VerticalWrapMode.Overflow;
        t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (t.font == null)
            t.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        return t;
    }

    static Button CreateButton(Transform parent, string name, out Text label)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
        go.transform.SetParent(parent, false);
        var img = go.GetComponent<Image>();
        img.color = new Color(0.22f, 0.24f, 0.28f);
        var btn = go.GetComponent<Button>();
        var colors = btn.colors;
        colors.normalColor = new Color(0.22f, 0.24f, 0.28f);
        colors.highlightedColor = new Color(0.32f, 0.34f, 0.38f);
        colors.pressedColor = new Color(0.16f, 0.17f, 0.2f);
        colors.disabledColor = new Color(0.2f, 0.2f, 0.2f, 0.55f);
        btn.colors = colors;

        label = CreateText(go.transform, "Label", "", 20, TextAnchor.MiddleCenter);
        var lr = label.GetComponent<RectTransform>();
        StretchFull(lr);
        label.fontStyle = FontStyle.Bold;
        label.color = new Color(0.95f, 0.93f, 0.9f);
        return btn;
    }

    static void PlaceButton(RectTransform rt, Vector2 min, Vector2 max)
    {
        rt.anchorMin = min;
        rt.anchorMax = max;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
    }

    static void StretchFull(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
    }
}
