using System;
using UnityEngine;

/// <summary>
/// Milestone M3 fallback: one small procedural monster sound per <see cref="EntityCue"/>, so a monster is never
/// silent while the recorded bank is still loading or when every file of a cue fails to decode. Mono 22.05 kHz,
/// 1-3 s each (~0.5 MB in total), generated once and levelled like the bank (<see cref="EntityAudioBank.Normalise"/>).
///  - Breath: low-passed noise shaped into an inhale and a longer, rasping exhale.
///  - Movement: four heavy footfalls over a scraping drag, loopable.
///  - Alert: a sawtooth growl (~78 Hz) with vibrato and a rough amplitude flutter.
///  - Chase: the same growl pitched up and swept down, rougher and fuller - a roar.
///  - Stinger: a noise hit with crack transients over a falling tone.
/// </summary>
public static class EntityAudioSynth
{
    const int Rate = 22050;

    public static AudioClip Create(EntityCue cue, int seed)
    {
        var rng = new System.Random(seed);
        float[] s;
        switch (cue)
        {
            case EntityCue.Breath: s = Breath(rng); break;
            case EntityCue.Movement: s = Steps(rng); break;
            case EntityCue.Alert: s = Growl(rng, 1.8f, 80f, 72f, 0.5f); break;
            case EntityCue.Chase: s = Growl(rng, 2.2f, 118f, 66f, 1f); break;
            default: s = Stinger(rng); break;
        }
        EntityAudioBank.Normalise(s);
        var clip = AudioClip.Create("synth_" + cue, s.Length, 1, Rate, false);
        clip.SetData(s, 0);
        return clip;
    }

    static float Noise(System.Random rng) => (float)(rng.NextDouble() * 2.0 - 1.0);

    /// <summary>A half-sine bump from <paramref name="a"/> to <paramref name="b"/> seconds, 0 outside.</summary>
    static float Hump(float t, float a, float b) => t <= a || t >= b ? 0f : Mathf.Sin(Mathf.PI * (t - a) / (b - a));

    static float[] Breath(System.Random rng)
    {
        var s = new float[(int)(2.6f * Rate)];
        float lp = 0f, lp2 = 0f;
        for (int i = 0; i < s.Length; i++)
        {
            float t = (float)i / Rate, n = Noise(rng);
            lp += (n - lp) * 0.18f;
            lp2 += (lp - lp2) * 0.3f;
            float exhale = Hump(t, 1.25f, 2.5f);
            float rasp = 0.35f * Mathf.Sin(2f * Mathf.PI * 62f * t) * (0.5f + 0.5f * n) * exhale;
            s[i] = (lp2 * 2.2f + rasp) * (Hump(t, 0.1f, 1.0f) * 0.55f + exhale);
        }
        return s;
    }

    static float[] Steps(System.Random rng)
    {
        var s = new float[(int)(2.4f * Rate)];
        float drag = 0f;
        for (int i = 0; i < s.Length; i++)
        {
            float t = (float)i / Rate, n = Noise(rng);
            float v = 0f;
            for (int k = 0; k < 4; k++)
            {
                float u = t - (0.3f + 0.6f * k);
                if (u < 0f || u > 0.35f) continue;
                v += Mathf.Sin(2f * Mathf.PI * 52f * u) * Mathf.Exp(-u * 20f) + n * Mathf.Exp(-u * 60f) * 0.5f;
            }
            drag += (n - drag) * 0.08f;
            s[i] = v + drag * 0.6f;
        }
        return s;
    }

    static float[] Growl(System.Random rng, float seconds, float f0, float f1, float rough)
    {
        var s = new float[(int)(seconds * Rate)];
        double phase = 0;
        float lp = 0f, nlp = 0f;
        for (int i = 0; i < s.Length; i++)
        {
            float t = (float)i / Rate, n = Noise(rng);
            float f = Mathf.Lerp(f0, f1, t / seconds) * (1f + 0.03f * Mathf.Sin(2f * Mathf.PI * 5.5f * t));
            phase += f / Rate;
            float saw = (float)(2.0 * (phase - Math.Floor(phase + 0.5)));
            nlp += (n - nlp) * 0.25f;
            float flutter = 0.6f + 0.4f * Mathf.Sin(2f * Mathf.PI * (17f + 9f * rough) * t + nlp * 3f);
            float v = (saw * 0.8f + nlp * 0.6f * rough) * flutter;
            lp += (v - lp) * 0.3f;
            float env = Mathf.Clamp01(t / 0.15f) * Mathf.Clamp01((seconds - t) / 0.5f);
            s[i] = lp * env;
        }
        return s;
    }

    static float[] Stinger(System.Random rng)
    {
        var s = new float[(int)(1.3f * Rate)];
        var cracks = new[] { rng.Next(0, Rate / 20), rng.Next(Rate / 20, Rate / 6), rng.Next(Rate / 6, Rate / 3) };
        double phase = 0;
        for (int i = 0; i < s.Length; i++)
        {
            float t = (float)i / Rate, n = Noise(rng);
            float f = Mathf.Lerp(700f, 70f, Mathf.Clamp01(t / 0.9f));
            phase += f / Rate;
            float tone = Mathf.Sin((float)(2.0 * Math.PI * phase)) * Mathf.Exp(-t * 2.5f) * 0.6f;
            float hit = n * Mathf.Exp(-t * 18f);
            float crack = 0f;
            foreach (int c in cracks)
            {
                int d = i - c;
                if (d >= 0 && d < 200) crack += n * Mathf.Exp(-d / 30f) * 1.5f;
            }
            s[i] = tone + hit + crack;
        }
        return s;
    }
}
