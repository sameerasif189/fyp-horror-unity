using UnityEngine;

/// <summary>
/// Non-linear stress → audio fright mapping so L4/L5 feel distinctly worse than L3.
/// Curves widened 11 Sep 2026: the previous spread was too narrow for levels to read as
/// different. The Fright01 ramp is mirrored by CorruptionVariantSelector.Severity so picture
/// and sound escalate together, with the largest jump between L3 and L4.
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
        float[] mul = { 0.34f, 0.52f, 0.74f, 1.00f, 1.45f, 1.80f };
        return Mathf.Clamp(baseVol * mul[Mathf.Clamp(level, 0, 5)], 0.05f, 1f);
    }

    public static float ProceduralVolume(int level, float baseVol)
    {
        // Procedural becomes a real threat layer at L4+, not a quiet bed.
        float[] mul = { 0.18f, 0.36f, 0.62f, 0.98f, 1.75f, 2.20f };
        return Mathf.Clamp(baseVol * mul[Mathf.Clamp(level, 0, 5)], 0.04f, 1f);
    }

    /// <summary>
    /// L0-L4 drag downward into dread, then L5 snaps UP into panic.
    ///
    /// The monotonic slide to 0.70 at L5 was a mistake: pitching everything down reads as
    /// slow, heavy and eerie - the same emotional register as L4, only more so. Panic needs
    /// urgency, so L5 breaks the pattern and goes above unity. It also stops the bass-heavy
    /// one-shots (extreme_bass, distorted_bass) being pitched 30% down into sub-bass, which
    /// was the source of the boom on entering L5.
    /// </summary>
    public static float Pitch(int level)
    {
        float[] pitch = { 1.00f, 0.99f, 0.96f, 0.92f, 0.84f, 1.07f };
        return pitch[Mathf.Clamp(level, 0, 5)];
    }

    public static float Distortion(int level)
    {
        float[] d = { 0.00f, 0.02f, 0.08f, 0.22f, 0.58f, 0.85f };
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
