using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Milestone M4 demo panel (user, 25 Sep 2026: "a timer UI at the side which shows which levels have a new texture
/// ready"). Right side of the screen under <see cref="GameplayHud"/>'s sound panel, one row per stress level with a
/// segment per house surface:
/// green = a freshly generated texture is waiting (the next visit to that level shows it), pulsing amber = being
/// generated now, grey = queued, dark = that surface has no library slot at that level. Each row ends with its
/// ready count and a countdown until the whole level is ready (queue position x running average); the footer shows
/// the job in progress, its pipeline stage and elapsed time. The level the player is in is highlighted - its own
/// textures were just used, so it is usually the one being refilled.
/// Added at runtime by <see cref="TextureCorruptionRunner"/> when texture generation is on; F2 toggles it.
/// </summary>
public class TextureGenerationHud : MonoBehaviour
{
    // StressController's level names (private there).
    static readonly string[] LevelNames = { "Calm", "Wary", "Nervous", "Anxious", "Frightened", "Terrified" };
    static readonly Color ReadyColor = new Color(0.36f, 0.86f, 0.47f);
    static readonly Color BusyColor = new Color(1f, 0.74f, 0.22f);
    static readonly Color QueuedColor = new Color(0.34f, 0.35f, 0.39f);
    static readonly Color NoSlotColor = new Color(0.14f, 0.14f, 0.16f);

    const float Width = 380f, Pad = 12f, RowH = 28f, Gap = 3f;

    [Tooltip("F2 toggles.")]
    public bool show = true;

    TextureCorruptionRunner _runner;
    GUIStyle _title, _label, _labelCurrent, _dim, _dimRight, _value, _valueReady;

    void Awake() => _runner = GetComponent<TextureCorruptionRunner>();

    void Update()
    {
        var kb = Keyboard.current;
        if (kb != null && kb.f2Key.wasPressedThisFrame) show = !show;
    }

    void OnGUI()
    {
        if (!show || _runner == null || Event.current.type != EventType.Repaint) return;
        if (GameStartMenu.Instance != null && !GameStartMenu.GameplayActive) return;
        var gen = _runner.Generator;
        if (gen == null || gen.Surfaces.Count == 0) return;
        InitStyles();

        // Same scaling as GameplayHud's canvas (1920x1080 reference, scaled with screen width) and 1.3x its size so
        // the 12-14 px text reads next to the canvas's 20-22 px labels; right-aligned under its sound panel, which
        // takes the top 14% of the screen on the right.
        float scale = Mathf.Max(0.1f, Screen.width / 1920f * 1.3f);
        var oldMatrix = GUI.matrix;
        GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));

        var surfaces = gen.Surfaces;
        int current = StressController.Instance != null ? StressController.Instance.StressLevel : -1;
        float height = Pad + 24f + 26f + 6f * RowH + 8f + 22f + 20f + Pad;
        var panel = new Rect(Screen.width / scale * 0.98f - Width, Screen.height / scale * 0.155f, Width, height);
        Fill(panel, new Color(0.05f, 0.06f, 0.08f, 0.82f));

        float x = panel.x + Pad, y = panel.y + Pad, w = Width - 2f * Pad;
        GUI.Label(new Rect(x, y, w, 24f), "Texture generation", _title);
        GUI.Label(new Rect(x, y + 4f, w, 20f), gen.ModelAvailable ? "compose + U-Net" : "compose only", _dimRight);
        y += 24f;
        GUI.Label(new Rect(x, y, w, 20f),
            gen.CompletedCount > 0 ? $"{gen.AverageSeconds:0.00} s per texture  ·  {gen.CompletedCount} generated" : "warming up...", _dim);
        y += 26f;

        const float nameW = 120f, valueW = 100f;
        float barX = x + nameW, barW = w - nameW - valueW - 8f;
        float seg = (barW - (surfaces.Count - 1) * Gap) / surfaces.Count;
        float pulse = 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 8f);
        for (int level = 0; level <= 5; level++)
        {
            bool isCurrent = level == current;
            if (isCurrent)
            {
                Fill(new Rect(panel.x + 4f, y, Width - 8f, RowH - 4f), new Color(1f, 1f, 1f, 0.08f));
                Fill(new Rect(panel.x + 4f, y, 3f, RowH - 4f), BusyColor);
            }
            GUI.Label(new Rect(x, y, nameW, RowH - 4f), $"L{level}  {LevelNames[level]}", isCurrent ? _labelCurrent : _label);

            int slots = 0, ready = 0;
            for (int i = 0; i < surfaces.Count; i++)
            {
                string s = surfaces[i];
                Color c;
                if (!gen.HasSlot(s, level)) c = NoSlotColor;
                else
                {
                    slots++;
                    if (gen.IsReady(s, level)) { ready++; c = ReadyColor; }
                    else if (gen.IsGenerating(s, level)) c = Color.Lerp(BusyColor, BusyColor * 0.5f, pulse);
                    else c = QueuedColor;
                }
                Fill(new Rect(barX + i * (seg + Gap), y + 6f, seg, RowH - 16f), c);
            }

            string value;
            GUIStyle style = _value;
            if (slots == 0) value = "-";
            else if (ready == slots) { value = "READY"; style = _valueReady; }
            else
            {
                float eta = gen.EtaSeconds(level);
                value = eta > 0f ? $"{ready}/{slots}  {eta:0.0}s" : $"{ready}/{slots}";
            }
            GUI.Label(new Rect(x + w - valueW, y, valueW, RowH - 4f), value, style);
            y += RowH;
        }

        y += 8f;
        // Between two jobs there is a frame with no job but a queue: only an empty queue is idle.
        string now = gen.CurrentSurface != null
            ? $"Now: {gen.CurrentSurface} L{gen.CurrentLevel}  ·  {gen.CurrentStage}  ·  {gen.CurrentElapsed:0.00} s"
            : gen.QueuedCount > 0 ? $"Starting next  ·  {gen.QueuedCount} queued"
            : "Idle: every level has fresh textures";
        GUI.Label(new Rect(x, y, w, 20f), now, _label);
        y += 22f;

        float lx = x;
        lx = Legend(lx, y, ReadyColor, "ready for next visit");
        lx = Legend(lx, y, BusyColor, "generating");
        Legend(lx, y, QueuedColor, "queued");
        GUI.Label(new Rect(x, y, w, 18f), "F2", _dimRight);

        GUI.matrix = oldMatrix;
    }

    float Legend(float x, float y, Color c, string text)
    {
        Fill(new Rect(x, y + 5f, 10f, 10f), c);
        float tw = _dim.CalcSize(new GUIContent(text)).x;
        GUI.Label(new Rect(x + 14f, y, tw + 4f, 18f), text, _dim);
        return x + 14f + tw + 14f;
    }

    static void Fill(Rect r, Color c)
    {
        var old = GUI.color;
        GUI.color = c;
        GUI.DrawTexture(r, Texture2D.whiteTexture);
        GUI.color = old;
    }

    void InitStyles()
    {
        if (_title != null) return;
        _title = Style(17, FontStyle.Bold, TextAnchor.UpperLeft, Color.white);
        _label = Style(14, FontStyle.Normal, TextAnchor.MiddleLeft, new Color(0.82f, 0.82f, 0.85f));
        _labelCurrent = Style(14, FontStyle.Bold, TextAnchor.MiddleLeft, Color.white);
        _dim = Style(12, FontStyle.Normal, TextAnchor.UpperLeft, new Color(0.6f, 0.6f, 0.65f));
        _dimRight = Style(12, FontStyle.Normal, TextAnchor.UpperRight, new Color(0.6f, 0.6f, 0.65f));
        _value = Style(14, FontStyle.Normal, TextAnchor.MiddleRight, new Color(0.82f, 0.82f, 0.85f));
        _valueReady = Style(14, FontStyle.Bold, TextAnchor.MiddleRight, ReadyColor);
    }

    static GUIStyle Style(int size, FontStyle fontStyle, TextAnchor anchor, Color color)
    {
        var s = new GUIStyle(GUI.skin.label) { fontSize = size, fontStyle = fontStyle, alignment = anchor, wordWrap = false };
        s.normal.textColor = color;
        return s;
    }
}
