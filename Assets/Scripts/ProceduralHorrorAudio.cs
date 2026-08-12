using UnityEngine;

/// <summary>
/// Fast CPU horror bed used while waiting for MusicGen (and if the bridge fails).
/// Approximate port of iteration-2 procedural_audio_gen layers: drone + noise + clicks.
/// </summary>
public static class ProceduralHorrorAudio
{
    const int SampleRate = 16000;

    public static AudioClip CreateBed(int stressLevel, float intensity, float dissonance, int seed, float durationSec = 4f)
    {
        stressLevel = Mathf.Clamp(stressLevel, 0, 5);
        intensity = Mathf.Clamp01(intensity);
        dissonance = Mathf.Clamp01(dissonance);
        durationSec = Mathf.Clamp(durationSec, 1f, 12f);

        int count = Mathf.Max(1, Mathf.RoundToInt(SampleRate * durationSec));
        float[] samples = new float[count];
        var rng = new System.Random(seed);

        float levelT = stressLevel / 5f;
        float droneW = Mathf.Lerp(0.55f, 0.18f, levelT);
        float noiseW = Mathf.Lerp(0.35f, 0.22f, levelT);
        float clickW = Mathf.Lerp(0.05f, 0.55f, levelT) * (0.5f + intensity);

        float baseHz = Mathf.Lerp(48f, 36f, levelT) * (1f - dissonance * 0.12f);
        float beatHz = Mathf.Lerp(0.15f, 1.8f, levelT + dissonance * 0.4f);
        float noiseCutoff = Mathf.Lerp(400f, 2800f, levelT);

        float brown = 0f;
        for (int i = 0; i < count; i++)
        {
            float t = i / (float)SampleRate;
            float env = 1f;
            // Soft edges so loops don't click when crossfading
            float edge = 0.08f;
            if (t < edge) env = t / edge;
            else if (t > durationSec - edge) env = (durationSec - t) / edge;

            float drone = Mathf.Sin(2f * Mathf.PI * baseHz * t);
            drone += 0.45f * Mathf.Sin(2f * Mathf.PI * (baseHz * 1.5f + dissonance * 7f) * t);
            drone += 0.25f * Mathf.Sin(2f * Mathf.PI * (baseHz * 2.01f) * t);
            float fm = 1f + 0.08f * Mathf.Sin(2f * Mathf.PI * beatHz * t);
            drone *= fm;
            // Slow beating between close partials
            drone *= 0.7f + 0.3f * Mathf.Sin(2f * Mathf.PI * (0.35f + dissonance) * t);

            // Brown-ish noise
            brown += ((float)rng.NextDouble() * 2f - 1f) * 0.08f;
            brown *= 0.98f;
            float noise = brown;
            // Cheap high-shelf feel via differencing at high stress
            if (levelT > 0.4f)
            {
                float white = (float)rng.NextDouble() * 2f - 1f;
                noise = Mathf.Lerp(noise, white, (levelT - 0.4f) * 0.7f);
            }
            // Gentle band emphasis via amplitude LFO (stand-in for filter sweep)
            noise *= 0.55f + 0.45f * Mathf.Sin(2f * Mathf.PI * (noiseCutoff / 8000f) * t * 0.2f + i * 0.0001f);

            float click = 0f;
            // Sparse impulsive clicks / scrapes
            float clickChance = 0.0008f + levelT * 0.0045f + intensity * 0.002f;
            if (rng.NextDouble() < clickChance)
            {
                float clickHz = Mathf.Lerp(180f, 1400f, (float)rng.NextDouble());
                float decay = Mathf.Lerp(0.04f, 0.18f, levelT);
                int len = Mathf.Min(count - i, Mathf.RoundToInt(SampleRate * decay));
                float amp = Mathf.Lerp(0.15f, 0.7f, levelT) * (0.5f + (float)rng.NextDouble());
                for (int k = 0; k < len; k++)
                {
                    float ct = k / (float)SampleRate;
                    float cenv = Mathf.Exp(-ct * (18f + levelT * 40f));
                    float ring = Mathf.Sin(2f * Mathf.PI * clickHz * ct) * Mathf.Sin(2f * Mathf.PI * clickHz * 1.37f * ct);
                    samples[i + k] += ring * cenv * amp * clickW;
                }
            }

            float s = drone * droneW + noise * noiseW + click;
            // Soft grit
            if (intensity > 0.2f)
            {
                float g = 1f + intensity * 4f;
                s = (float)System.Math.Tanh(s * g) / (float)System.Math.Tanh(g);
            }

            samples[i] += s * env * 0.55f;
        }

        // Normalize
        float peak = 1e-6f;
        for (int i = 0; i < count; i++)
            peak = Mathf.Max(peak, Mathf.Abs(samples[i]));
        float scale = 0.9f / peak;
        for (int i = 0; i < count; i++)
            samples[i] *= scale;

        var clip = AudioClip.Create($"ProcHorror_L{stressLevel}_{seed}", count, 1, SampleRate, false);
        clip.SetData(samples, 0);
        return clip;
    }
}
