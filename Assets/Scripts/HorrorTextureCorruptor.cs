using UnityEngine;

/// <summary>
/// CPU procedural horror corruption matching iteration-2 training corruptions
/// (grime, cracks, blood, scorch) so surfaces visibly deform by stress.
/// </summary>
public static class HorrorTextureCorruptor
{
    public static Texture2D Corrupt(Texture2D source, int stressLevel, int size = 256)
    {
        stressLevel = Mathf.Clamp(stressLevel, 0, 5);
        var src = ResizeReadable(source, size);
        var pixels = src.GetPixels();
        var rng = new System.Random(1357 + stressLevel * 997);

        float intensity = stressLevel / 5f;
        if (stressLevel >= 1) ApplyGrime(pixels, size, intensity * 0.85f, rng);
        if (stressLevel >= 2) ApplyCracks(pixels, size, intensity, rng);
        if (stressLevel >= 3) ApplyBlood(pixels, size, intensity, rng);
        if (stressLevel >= 4) ApplyScorch(pixels, size, intensity, rng);
        if (stressLevel >= 5)
        {
            ApplyBlood(pixels, size, intensity * 1.15f, rng);
            ApplyCracks(pixels, size, intensity * 1.2f, rng);
            ApplyDecayNoise(pixels, size, 0.35f, rng);
        }

        var outTex = new Texture2D(size, size, TextureFormat.RGBA32, true, false)
        {
            wrapMode = TextureWrapMode.Repeat,
            filterMode = FilterMode.Bilinear,
            name = source.name + "_Corrupted_" + stressLevel
        };
        outTex.SetPixels(pixels);
        outTex.Apply(true);
        if (src != source)
            Object.Destroy(src);
        return outTex;
    }

    static Texture2D ResizeReadable(Texture2D source, int size)
    {
        var rt = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        Graphics.Blit(source, rt);
        var prev = RenderTexture.active;
        RenderTexture.active = rt;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false, false);
        tex.ReadPixels(new Rect(0, 0, size, size), 0, 0);
        tex.Apply();
        RenderTexture.active = prev;
        RenderTexture.ReleaseTemporary(rt);
        return tex;
    }

    static void ApplyGrime(Color[] px, int size, float intensity, System.Random rng)
    {
        int patches = 8 + (int)(40 * intensity);
        for (int p = 0; p < patches; p++)
        {
            int cx = rng.Next(size);
            int cy = rng.Next(size);
            int rad = 10 + rng.Next(10 + (int)(60 * intensity));
            float darken = 0.25f + 0.55f * intensity;
            for (int y = cy - rad; y <= cy + rad; y++)
            for (int x = cx - rad; x <= cx + rad; x++)
            {
                if ((uint)x >= (uint)size || (uint)y >= (uint)size) continue;
                float dx = (x - cx) / (float)rad;
                float dy = (y - cy) / (float)rad;
                float d = Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dy));
                if (d >= 1f || rng.NextDouble() > 0.65) continue;
                float a = (1f - d) * darken;
                int i = y * size + x;
                var c = px[i];
                c.r *= 1f - a;
                c.g *= 1f - a * 1.05f;
                c.b *= 1f - a * 1.1f;
                px[i] = c;
            }
        }
    }

    static void ApplyCracks(Color[] px, int size, float intensity, System.Random rng)
    {
        int cracks = 4 + (int)(18 * intensity);
        for (int c = 0; c < cracks; c++)
        {
            float x = rng.Next(size);
            float y = rng.Next(size);
            float angle = (float)(rng.NextDouble() * Mathf.PI * 2);
            int length = 20 + rng.Next(20 + (int)(90 * intensity));
            for (int s = 0; s < length; s++)
            {
                angle += (float)(rng.NextDouble() - 0.5) * 0.45f;
                x += Mathf.Cos(angle);
                y += Mathf.Sin(angle);
                int w = 1 + (int)((1f - s / (float)length) * (1 + 2 * intensity));
                for (int oy = -w; oy <= w; oy++)
                for (int ox = -w; ox <= w; ox++)
                {
                    int ix = Mathf.RoundToInt(x) + ox;
                    int iy = Mathf.RoundToInt(y) + oy;
                    if ((uint)ix >= (uint)size || (uint)iy >= (uint)size) continue;
                    int i = iy * size + ix;
                    var col = px[i];
                    col.r *= 0.15f;
                    col.g *= 0.12f;
                    col.b *= 0.1f;
                    px[i] = col;
                }
            }
        }
    }

    static void ApplyBlood(Color[] px, int size, float intensity, System.Random rng)
    {
        int stains = 5 + (int)(28 * intensity);
        for (int s = 0; s < stains; s++)
        {
            int cx = rng.Next(size);
            int cy = rng.Next(size);
            int rad = 5 + rng.Next(8 + (int)(35 * intensity));
            for (int y = cy - rad; y <= cy + rad; y++)
            for (int x = cx - rad; x <= cx + rad; x++)
            {
                if ((uint)x >= (uint)size || (uint)y >= (uint)size) continue;
                float dx = x - cx;
                float dy = y - cy;
                float dist = Mathf.Sqrt(dx * dx + dy * dy) / rad;
                if (dist >= 1f) continue;
                float a = Mathf.Pow(1f - dist, 1.4f) * intensity * 0.95f;
                int i = y * size + x;
                var c = px[i];
                float br = 0.55f + (float)rng.NextDouble() * 0.28f;
                float bg = 0.02f;
                float bb = 0.02f;
                c.r = c.r * (1f - a) + br * a;
                c.g = c.g * (1f - a) + bg * a;
                c.b = c.b * (1f - a) + bb * a;
                px[i] = c;
            }
        }
    }

    static void ApplyScorch(Color[] px, int size, float intensity, System.Random rng)
    {
        int burns = 3 + (int)(10 * intensity);
        for (int b = 0; b < burns; b++)
        {
            int cx = rng.Next(size);
            int cy = rng.Next(size);
            int rad = 15 + rng.Next(20 + (int)(50 * intensity));
            for (int y = cy - rad; y <= cy + rad; y++)
            for (int x = cx - rad; x <= cx + rad; x++)
            {
                if ((uint)x >= (uint)size || (uint)y >= (uint)size) continue;
                float dx = (x - cx) / (float)rad;
                float dy = (y - cy) / (float)rad;
                float dist = Mathf.Sqrt(dx * dx + dy * dy);
                if (dist >= 1f) continue;
                float a = (1f - dist) * intensity * 0.7f;
                int i = y * size + x;
                var c = px[i];
                c.r = Mathf.Lerp(c.r, c.r * 0.2f + 0.15f, a);
                c.g = Mathf.Lerp(c.g, c.g * 0.12f, a);
                c.b = Mathf.Lerp(c.b, c.b * 0.08f, a);
                px[i] = c;
            }
        }
    }

    static void ApplyDecayNoise(Color[] px, int size, float amount, System.Random rng)
    {
        for (int i = 0; i < px.Length; i++)
        {
            if (rng.NextDouble() > 0.08) continue;
            float n = (float)rng.NextDouble();
            var c = px[i];
            c.r = Mathf.Clamp01(c.r * (1f - amount) + n * 0.25f * amount);
            c.g = Mathf.Clamp01(c.g * (1f - amount * 1.2f));
            c.b = Mathf.Clamp01(c.b * (1f - amount * 1.3f));
            px[i] = c;
        }
    }
}