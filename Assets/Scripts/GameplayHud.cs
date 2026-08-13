using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Colored in-game HUD: stress meter (0-5) and sound / MusicGen status.
/// Built at runtime so it does not depend on a prefab.
/// </summary>
public class GameplayHud : MonoBehaviour
{
    public static GameplayHud Instance { get; private set; }

    static readonly string[] LevelNames =
    {
        "CALM", "WARY", "NERVOUS", "ANXIOUS", "FRIGHTENED", "TERRIFIED"
    };

    static readonly Color[] StressColors =
    {
        new Color(0.28f, 0.72f, 0.38f),
        new Color(0.62f, 0.78f, 0.22f),
        new Color(0.92f, 0.78f, 0.16f),
        new Color(0.95f, 0.52f, 0.14f),
        new Color(0.90f, 0.28f, 0.14f),
        new Color(0.72f, 0.08f, 0.12f)
    };

    [SerializeField] AudioGenerationRunner audioRunner;

    Canvas _canvas;
    Image[] _pips = new Image[6];
    Image _stressFill;
    Image _stressGlow;
    Text _stressLabel;
    Text _stressHint;
    Image _soundFill;
    Image _soundDot;
    Text _soundLabel;
    Text _soundDetail;

    void Awake()
    {
        Instance = this;
        if (audioRunner == null)
            audioRunner = FindFirstObjectByType<AudioGenerationRunner>();
    }

    void Start()
    {
        BuildUi();
        SetVisible(GameStartMenu.GameplayActive);
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    void Update()
    {
        bool play = GameStartMenu.Instance == null || GameStartMenu.GameplayActive;
        SetVisible(play);
        if (!play) return;
        RefreshStress();
        RefreshSound();
    }

    public void Show() => SetVisible(true);

    void SetVisible(bool on)
    {
        if (_canvas != null)
            _canvas.gameObject.SetActive(on);
    }

    void RefreshStress()
    {
        int level = StressController.Instance != null ? StressController.Instance.StressLevel : 0;
        level = Mathf.Clamp(level, 0, 5);
        Color c = StressColors[level];

        if (_stressFill != null)
            _stressFill.rectTransform.anchorMax = new Vector2((level + 1) / 6f, 1f);
        if (_stressGlow != null)
            _stressGlow.color = new Color(c.r, c.g, c.b, 0.35f);
        if (_stressLabel != null)
        {
            _stressLabel.text = $"STRESS  {level}/5  ·  {LevelNames[level]}";
            _stressLabel.color = Color.Lerp(c, Color.white, 0.35f);
        }

        for (int i = 0; i < _pips.Length; i++)
        {
            if (_pips[i] == null) continue;
            _pips[i].color = i <= level ? StressColors[i] : new Color(0.18f, 0.18f, 0.2f, 0.7f);
        }
    }

    void RefreshSound()
    {
        if (audioRunner == null)
            audioRunner = FindFirstObjectByType<AudioGenerationRunner>();

        bool ready = audioRunner != null && audioRunner.IsReady;
        bool gen = audioRunner != null && audioRunner.IsGenerating;
        string source = audioRunner != null ? audioRunner.LastSource : "none";
        string status = audioRunner != null ? audioRunner.StatusText : "No audio runner";

        Color c;
        float fill;
        string title;
        if (!ready)
        {
            c = new Color(0.45f, 0.48f, 0.55f);
            fill = 0.12f;
            title = "SOUND  ·  LOADING";
        }
        else if (gen)
        {
            c = new Color(0.35f, 0.72f, 0.95f);
            fill = 0.55f + 0.35f * Mathf.Abs(Mathf.Sin(Time.time * 3.2f));
            title = "SOUND  ·  GENERATING";
        }
        else if (source != null && source.IndexOf("musicgen", System.StringComparison.OrdinalIgnoreCase) >= 0)
        {
            c = new Color(0.45f, 0.85f, 0.55f);
            fill = 0.82f;
            title = "SOUND  ·  MUSICGEN";
        }
        else if (source == "silent" || source == "none")
        {
            c = new Color(0.85f, 0.55f, 0.2f);
            fill = 0.22f;
            title = "SOUND  ·  WAITING";
        }
        else
        {
            c = new Color(0.75f, 0.55f, 0.95f);
            fill = 0.7f;
            title = "SOUND  ·  " + source.ToUpperInvariant();
        }

        if (_soundFill != null)
        {
            _soundFill.color = c;
            _soundFill.rectTransform.anchorMax = new Vector2(Mathf.Clamp01(fill), 1f);
        }
        if (_soundDot != null)
            _soundDot.color = gen ? Color.Lerp(c, Color.white, Mathf.PingPong(Time.time * 2f, 1f)) : c;
        if (_soundLabel != null)
            _soundLabel.text = title;
        if (_soundDetail != null)
            _soundDetail.text = Truncate(status, 72);
    }

    static string Truncate(string s, int n)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= n ? s : s.Substring(0, n - 1) + "…";
    }

    void BuildUi()
    {
        var root = new GameObject("GameplayHudCanvas");
        root.transform.SetParent(transform, false);
        _canvas = root.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.sortingOrder = 50;
        var scaler = root.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        root.AddComponent<GraphicRaycaster>();

        var stressPanel = CreatePanel(root.transform, "StressPanel", new Color(0.05f, 0.06f, 0.08f, 0.82f));
        Place(stressPanel, new Vector2(0.02f, 0.86f), new Vector2(0.38f, 0.97f));

        _stressGlow = CreatePanel(stressPanel.transform, "Glow", new Color(0.3f, 0.7f, 0.4f, 0.3f)).GetComponent<Image>();
        Place(_stressGlow.rectTransform, new Vector2(0f, 0.55f), new Vector2(1f, 1f));

        _stressLabel = CreateText(stressPanel.transform, "Label", "STRESS  0/5  ·  CALM", 22, TextAnchor.MiddleLeft);
        Place(_stressLabel.rectTransform, new Vector2(0.04f, 0.58f), new Vector2(0.96f, 0.95f));
        _stressLabel.fontStyle = FontStyle.Bold;

        var track = CreatePanel(stressPanel.transform, "Track", new Color(0.12f, 0.13f, 0.16f, 0.95f));
        Place(track.GetComponent<RectTransform>(), new Vector2(0.04f, 0.28f), new Vector2(0.96f, 0.52f));

        _stressFill = CreatePanel(track.transform, "Fill", StressColors[0]).GetComponent<Image>();
        var fr = _stressFill.rectTransform;
        fr.anchorMin = Vector2.zero;
        fr.anchorMax = new Vector2(1f / 6f, 1f);
        fr.offsetMin = fr.offsetMax = Vector2.zero;

        var pipsRow = CreatePanel(stressPanel.transform, "Pips", new Color(0, 0, 0, 0));
        Place(pipsRow.GetComponent<RectTransform>(), new Vector2(0.04f, 0.06f), new Vector2(0.96f, 0.24f));
        for (int i = 0; i < 6; i++)
        {
            var pip = CreatePanel(pipsRow.transform, "Pip" + i, StressColors[i]);
            float x0 = i / 6f + 0.01f;
            float x1 = (i + 1) / 6f - 0.01f;
            Place(pip.GetComponent<RectTransform>(), new Vector2(x0, 0.15f), new Vector2(x1, 0.85f));
            _pips[i] = pip.GetComponent<Image>();
        }

        _stressHint = CreateText(stressPanel.transform, "Hint", "KEYS  0 – 5", 12, TextAnchor.MiddleRight);
        Place(_stressHint.rectTransform, new Vector2(0.55f, 0.58f), new Vector2(0.96f, 0.95f));
        _stressHint.color = new Color(0.65f, 0.65f, 0.68f);

        var soundPanel = CreatePanel(root.transform, "SoundPanel", new Color(0.05f, 0.06f, 0.08f, 0.82f));
        Place(soundPanel, new Vector2(0.62f, 0.86f), new Vector2(0.98f, 0.97f));

        _soundLabel = CreateText(soundPanel.transform, "Label", "SOUND  ·  IDLE", 20, TextAnchor.MiddleLeft);
        Place(_soundLabel.rectTransform, new Vector2(0.06f, 0.55f), new Vector2(0.88f, 0.94f));
        _soundLabel.fontStyle = FontStyle.Bold;
        _soundLabel.color = new Color(0.75f, 0.9f, 1f);

        _soundDot = CreatePanel(soundPanel.transform, "Dot", new Color(0.4f, 0.85f, 0.55f)).GetComponent<Image>();
        Place(_soundDot.rectTransform, new Vector2(0.90f, 0.62f), new Vector2(0.96f, 0.88f));

        var sTrack = CreatePanel(soundPanel.transform, "Track", new Color(0.12f, 0.13f, 0.16f, 0.95f));
        Place(sTrack.GetComponent<RectTransform>(), new Vector2(0.06f, 0.32f), new Vector2(0.94f, 0.52f));

        _soundFill = CreatePanel(sTrack.transform, "Fill", new Color(0.35f, 0.75f, 0.95f)).GetComponent<Image>();
        var sfr = _soundFill.rectTransform;
        sfr.anchorMin = Vector2.zero;
        sfr.anchorMax = new Vector2(0.2f, 1f);
        sfr.offsetMin = sfr.offsetMax = Vector2.zero;

        _soundDetail = CreateText(soundPanel.transform, "Detail", "", 13, TextAnchor.MiddleLeft);
        Place(_soundDetail.rectTransform, new Vector2(0.06f, 0.04f), new Vector2(0.94f, 0.30f));
        _soundDetail.color = new Color(0.62f, 0.66f, 0.7f);
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

    static void Place(GameObject go, Vector2 min, Vector2 max)
    {
        Place(go.GetComponent<RectTransform>(), min, max);
    }

    static void Place(Component c, Vector2 min, Vector2 max)
    {
        var rt = c as RectTransform ?? c.GetComponent<RectTransform>();
        rt.anchorMin = min;
        rt.anchorMax = max;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
    }
}
