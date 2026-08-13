using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Demo stress bus driven by keyboard keys 0-5 (Calm -> Terrified).
/// </summary>
public class StressController : MonoBehaviour
{
    public static StressController Instance { get; private set; }

    [SerializeField] int stressLevel;
    [SerializeField] bool showHud = false;

    public int StressLevel => stressLevel;
    public event System.Action<int> OnStressChanged;

    static readonly string[] LevelNames =
    {
        "Calm", "Wary", "Nervous", "Anxious", "Frightened", "Terrified"
    };

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
    }

    void Update()
    {
        if (GameStartMenu.Instance != null && !GameStartMenu.GameplayActive)
            return;

        var kb = Keyboard.current;
        if (kb == null)
            return;

        if (kb.digit0Key.wasPressedThisFrame || kb.numpad0Key.wasPressedThisFrame) SetStressLevel(0);
        else if (kb.digit1Key.wasPressedThisFrame || kb.numpad1Key.wasPressedThisFrame) SetStressLevel(1);
        else if (kb.digit2Key.wasPressedThisFrame || kb.numpad2Key.wasPressedThisFrame) SetStressLevel(2);
        else if (kb.digit3Key.wasPressedThisFrame || kb.numpad3Key.wasPressedThisFrame) SetStressLevel(3);
        else if (kb.digit4Key.wasPressedThisFrame || kb.numpad4Key.wasPressedThisFrame) SetStressLevel(4);
        else if (kb.digit5Key.wasPressedThisFrame || kb.numpad5Key.wasPressedThisFrame) SetStressLevel(5);
    }

    void OnGUI()
    {
        if (!showHud)
            return;
        if (GameStartMenu.Instance != null && !GameStartMenu.GameplayActive)
            return;

        var style = new GUIStyle(GUI.skin.box)
        {
            fontSize = 18,
            alignment = TextAnchor.MiddleLeft,
            padding = new RectOffset(12, 12, 8, 8)
        };
        string name = LevelNames[Mathf.Clamp(stressLevel, 0, 5)];
        GUI.Box(new Rect(16, 16, 320, 48), $"Stress {stressLevel}/5 — {name}\nKeys 0-5 to change", style);
    }

    public void SetStressLevel(int level)
    {
        level = Mathf.Clamp(level, 0, 5);
        if (level == stressLevel)
            return;

        stressLevel = level;
        OnStressChanged?.Invoke(stressLevel);
        Debug.Log($"[Stress] level={stressLevel} ({LevelNames[stressLevel]})");
    }
}