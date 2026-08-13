using UnityEngine;

/// <summary>
/// Non-linear stress → audio fright mapping so L4/L5 feel distinctly worse than L3.
/// </summary>
public static class StressAudioProfile
{
    /// <summary>0..1 fright amount with a hard step into L4+.</summary>
    public static float Fright01(int level)
    {
        level = Mathf.Clamp(level, 0, 5);
        // Calm → tense rises slowly; frightened/terrified spike.
        float[] curve = { 0.05f, 0.18f, 0.34f, 0.52f, 0.82f, 1.00f };
        return curve[level];
    }

    public static float MusicVolume(int level, float baseVol)
    {
        float[] mul = { 0.55f, 0.68f, 0.82f, 0.95f, 1.25f, 1.45f };
        return Mathf.Clamp(baseVol * mul[Mathf.Clamp(level, 0, 5)], 0.05f, 1f);
    }

    public static float ProceduralVolume(int level, float baseVol)
    {
        // Procedural becomes a real threat layer at L4+, not a quiet bed.
        float[] mul = { 0.35f, 0.50f, 0.70f, 0.95f, 1.55f, 1.85f };
        return Mathf.Clamp(baseVol * mul[Mathf.Clamp(level, 0, 5)], 0.04f, 1f);
    }

    public static float Pitch(int level)
    {
        float[] pitch = { 1.00f, 0.99f, 0.97f, 0.94f, 0.88f, 0.82f };
        return pitch[Mathf.Clamp(level, 0, 5)];
    }

    public static float Distortion(int level)
    {
        float[] d = { 0.00f, 0.04f, 0.10f, 0.18f, 0.42f, 0.62f };
        return d[Mathf.Clamp(level, 0, 5)];
    }

    public static float Intensity(int level, float fusionIntensity)
    {
        return Mathf.Max(Mathf.Clamp01(fusionIntensity), Fright01(level));
    }

    public static float Dissonance(int level, float fusionDissonance)
    {
        float floor = Fright01(level) * 0.95f;
        return Mathf.Max(Mathf.Clamp01(fusionDissonance), floor);
    }
}
