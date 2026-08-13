using System;
using UnityEngine;

/// <summary>
/// Mixes grains from the FULL shuffled stress-level asset pool (no replacement until exhausted).
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
        ProceduralAssetBank.EnsureIndexed();

        int count = Mathf.Max(1, Mathf.RoundToInt(SampleRate * durationSec));
        float[] samples = new float[count];
        var rng = new System.Random(seed);
        float fright = StressAudioProfile.Fright01(stressLevel);

        WriteSynthLayer(samples, stressLevel, fright, intensity, dissonance, rng);

        // Walk the reshuffled level pool — use as many distinct assets as practical.
        int poolSize = Mathf.Max(1, ProceduralAssetBank.LevelCount(stressLevel));
        int grainTarget = stressLevel switch
        {
            0 => Mathf.Clamp(poolSize, 4, Mathf.Max(4, poolSize)),
            1 => Mathf.Clamp(Mathf.Max(6, poolSize / 2), 6, poolSize),
            2 => Mathf.Clamp(Mathf.Max(8, poolSize * 2 / 3), 8, Mathf.Max(8, poolSize)),
            3 => Mathf.Clamp(poolSize, 10, Mathf.Max(10, poolSize)),
            4 => Mathf.Clamp(poolSize + 4, 12, poolSize + 8),
            _ => Mathf.Clamp(poolSize + 8, 16, poolSize + 12),
        };

        int placed = 0;
        for (int g = 0; g < grainTarget; g++)
        {
            bool preferLevel = rng.NextDouble() < (0.7 + fright * 0.2);
            string path = ProceduralAssetBank.TakeNextJuggled(stressLevel, preferLevel);
            if (path == null) break;

            var src = ProceduralAssetBank.LoadClip(path, "grain");
            if (src == null) continue;

            float[] gData = new float[src.samples * src.channels];
            src.GetData(gData, 0);
            int srcFrames = src.samples;
            int srcCh = Mathf.Max(1, src.channels);
            int srcRate = src.frequency;

            int sliceFrames = Mathf.Clamp(
                Mathf.RoundToInt(srcRate * Mathf.Lerp(0.15f, 0.65f, (float)rng.NextDouble())),
                srcRate / 16,
                Mathf.Max(64, srcFrames - 1));
            int startFrame = srcFrames > sliceFrames ? rng.Next(0, srcFrames - sliceFrames) : 0;

            float pitch = Mathf.Lerp(0.55f, 1.45f, (float)rng.NextDouble());
            if (stressLevel >= 4) pitch *= Mathf.Lerp(0.7f, 1.15f, (float)rng.NextDouble());
            bool reverse = rng.NextDouble() < (0.15 + fright * 0.25);
            float amp = Mathf.Lerp(0.2f, 0.9f, fright) * (0.5f + (float)rng.NextDouble() * 0.5f);
            int destStart = rng.Next(0, Mathf.Max(1, count - SampleRate / 8));

            OverlayGrain(samples, gData, srcFrames, srcCh, srcRate, startFrame, sliceFrames,
                destStart, pitch, reverse, amp, SampleRate);
            placed++;
            UnityEngine.Object.Destroy(src);
        }

        float peak = 1e-6f;
        for (int i = 0; i < count; i++)
            peak = Mathf.Max(peak, Mathf.Abs(samples[i]));
        float target = stressLevel >= 4 ? 0.97f : 0.88f;
        float scale = target / peak;
        for (int i = 0; i < count; i++)
            samples[i] *= scale;

        var clip = AudioClip.Create($"ProcHorror_L{stressLevel}_{seed}_g{placed}", count, 1, SampleRate, false);
        clip.SetData(samples, 0);
        return clip;
    }

    static void WriteSynthLayer(float[] samples, int stressLevel, float fright, float intensity, float dissonance, System.Random rng)
    {
        int count = samples.Length;
        float baseHz = Mathf.Lerp(52f, 26f, fright) * (1f - dissonance * 0.18f);
        float beatHz = Mathf.Lerp(0.12f, 3.4f, fright) * (0.7f + (float)rng.NextDouble() * 0.6f);
        float droneW = Mathf.Lerp(0.5f, 0.12f, fright);
        float noiseW = Mathf.Lerp(0.28f, 0.32f, fright);
        float brown = 0f;
        float durationSec = count / (float)SampleRate;

        for (int i = 0; i < count; i++)
        {
            float t = i / (float)SampleRate;
            float env = 1f;
            float edge = 0.06f;
            if (t < edge) env = t / edge;
            else if (t > durationSec - edge) env = (durationSec - t) / edge;

            float drone = Mathf.Sin(2f * Mathf.PI * baseHz * t);
            drone += 0.5f * Mathf.Sin(2f * Mathf.PI * (baseHz * 1.5f + dissonance * 9f) * t);
            float fm = 1f + (0.08f + fright * 0.2f) * Mathf.Sin(2f * Mathf.PI * beatHz * t);
            drone *= fm;

            brown += ((float)rng.NextDouble() * 2f - 1f) * (0.07f + fright * 0.05f);
            brown *= 0.97f;
            float noise = brown;
            if (fright > 0.35f)
                noise = Mathf.Lerp(noise, (float)rng.NextDouble() * 2f - 1f, (fright - 0.35f));

            float s = drone * droneW + noise * noiseW;
            float grit = Mathf.Max(intensity, fright * 0.8f);
            if (grit > 0.15f)
            {
                float g = 1f + grit * (stressLevel >= 4 ? 8f : 4.5f);
                s = (float)Math.Tanh(s * g) / (float)Math.Tanh(g);
            }
            samples[i] = s * env * Mathf.Lerp(0.35f, 0.55f, fright);
        }
    }

    static void OverlayGrain(
        float[] dest, float[] srcInterleaved, int srcFrames, int srcCh, int srcRate,
        int startFrame, int sliceFrames, int destStart, float pitch, bool reverse, float amp, int outRate)
    {
        if (sliceFrames < 8) return;
        float rateRatio = (srcRate / (float)outRate) * Mathf.Max(0.25f, pitch);
        int outLen = Mathf.Max(1, Mathf.RoundToInt(sliceFrames / rateRatio));
        for (int i = 0; i < outLen; i++)
        {
            int di = destStart + i;
            if (di < 0 || di >= dest.Length) break;
            float srcPos = reverse ? (sliceFrames - 1 - i * rateRatio) : (i * rateRatio);
            int sf = startFrame + Mathf.Clamp(Mathf.FloorToInt(srcPos), 0, sliceFrames - 1);
            if (sf >= srcFrames) break;
            float frac = srcPos - Mathf.Floor(srcPos);
            int sf2 = Mathf.Min(srcFrames - 1, sf + 1);
            float s0 = 0f, s1 = 0f;
            for (int c = 0; c < srcCh; c++)
            {
                s0 += srcInterleaved[sf * srcCh + c];
                s1 += srcInterleaved[sf2 * srcCh + c];
            }
            s0 /= srcCh;
            s1 /= srcCh;
            float sample = Mathf.Lerp(s0, s1, frac);
            float hann = 0.5f - 0.5f * Mathf.Cos(2f * Mathf.PI * (i / (float)Mathf.Max(1, outLen - 1)));
            dest[di] += sample * amp * hann;
        }
    }
}
