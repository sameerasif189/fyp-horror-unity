using UnityEngine;

/// <summary>
/// Stress-driven TV-static distortion on haunted-house albedos (scanlines, snow, tear).
/// No blood or crack overlays.
/// </summary>
public static class HorrorTextureCorruptor
{
    public static Texture2D Corrupt(Texture2D source, int stressLevel, int size = 256)
    {
        return Corrupt(source, stressLevel, size, seedOffset: 0);
    }

    public static Texture2D Corrupt(Texture2D source, int stressLevel, int size, int seedOffset)
    {
        stressLevel = Mathf.Clamp(stressLevel, 0, 5);
        var src = ResizeReadable(source, size);
        var pixels = src.GetPixels();
        var rng = new System.Random(1357 + stressLevel * 997 + seedOffset * 7919);

        ApplyTvStatic(pixels, size, stressLevel, rng);

        var outTex = new Texture2D(size, size, TextureFormat.RGBA32, true, false)
        {
            wrapMode = TextureWrapMode.Repeat,
            filterMode = FilterMode.Bilinear,
            name = source.name + "_Corrupted_" + stressLevel + "_" + seedOffset
        };
        outTex.SetPixels(pixels);
        outTex.Apply(true);
        if (src != source)
            Object.Destroy(src);
        return outTex;
    }

    /// <summary>
    /// Mild wrap-shift so the next generation is not identical. No flip/rotate —
    /// those made walls look like they warped with the yard.
    /// </summary>
    public static Texture2D RemapSource(Texture2D source, int size, int seed)
    {
        var src = ResizeReadable(source, size);
        var px = src.GetPixels();
        var rng = new System.Random(seed);
        int ox = rng.Next(size / 8);
        int oy = rng.Next(size / 8);

        var outPx = new Color[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int sx = (x + ox) % size;
                int sy = (y + oy) % size;
                outPx[y * size + x] = px[sy * size + sx];
            }
        }

        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false, false)
        {
            name = source.name + "_remap_" + seed,
            wrapMode = TextureWrapMode.Repeat,
            filterMode = FilterMode.Bilinear
        };
        tex.SetPixels(outPx);
        tex.Apply(false);
        if (src != source)
            Object.Destroy(src);
        return tex;
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

    static void ApplyTvStatic(Color[] px, int size, int level, System.Random rng)
    {
        if (level <= 0) return;
        float t = Mathf.Clamp01(level / 5f);

        int scanStep = t < 0.45f ? 2 : 1;
        float scan = 0.18f + 0.72f * t;
        for (int y = 0; y < size; y += scanStep)
        {
            float a = scan * (0.45f + 0.55f * Hash01(y, level, rng));
            for (int x = 0; x < size; x++)
            {
                int i = y * size + x;
                var c = px[i];
                c.r *= 1f - a;
                c.g *= 1f - a;
                c.b *= 1f - a;
                px[i] = c;
            }
        }

        int snow = (int)(size * size * (0.06f + 0.42f * t));
        for (int s = 0; s < snow; s++)
        {
            int i = rng.Next(px.Length);
            float n = (float)rng.NextDouble();
            float a = 0.45f + 0.55f * t;
            var c = px[i];
            px[i] = Color.Lerp(c, new Color(n, n, n, c.a), a);
        }

        int bars = 1 + level;
        for (int b = 0; b < bars; b++)
        {
            int barY = rng.Next(size);
            int barH = 2 + (int)(22 * t);
            float barA = 0.22f + 0.58f * t;
            for (int y = barY; y < barY + barH && y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int i = y * size + x;
                var c = px[i];
                float n = 0.55f + 0.45f * (float)rng.NextDouble();
                px[i] = Color.Lerp(c, new Color(n, n, n), barA);
            }
        }

        if (level >= 1)
        {
            var copy = (Color[])px.Clone();
            int tears = 3 + level * 6;
            for (int k = 0; k < tears; k++)
            {
                int y0 = rng.Next(size);
                int h = 1 + rng.Next(3 + level * 2);
                int shift = (rng.Next(2) * 2 - 1) * (4 + rng.Next(8 + level * 6));
                for (int y = y0; y < y0 + h && y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        int sx = (x + shift) % size;
                        if (sx < 0) sx += size;
                        px[y * size + x] = copy[y * size + sx];
                    }
                }
            }
        }

        if (level >= 2)
        {
            var copy = (Color[])px.Clone();
            int split = 2 + level;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int i = y * size + x;
                int xr = Mathf.Clamp(x + split, 0, size - 1);
                int xb = Mathf.Clamp(x - split, 0, size - 1);
                var c = copy[i];
                c.r = copy[y * size + xr].r;
                c.b = copy[y * size + xb].b;
                px[i] = c;
            }
        }
    }

    static float Hash01(int y, int level, System.Random rng)
    {
        return (float)((y * 13 + level * 7 + rng.Next(8)) % 100) / 100f;
    }
}
