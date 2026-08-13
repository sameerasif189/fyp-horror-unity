using UnityEngine;

/// <summary>
/// Procedural haunted-house base albedos (wallpaper, floorboards, plaster, wood).
/// Readable Texture2Ds suitable for ONNX + HorrorTextureCorruptor realtime updates.
/// </summary>
public static class HauntedHouseTextures
{
    public static Texture2D CreateWallpaper(int size = 256, int seed = 42)
    {
        var tex = NewTex("HH_Wallpaper", size);
        var px = new Color[size * size];

        // Aged plaster base
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float n = Hash(x, y, seed) * 0.08f;
            var c = new Color(0.62f + n, 0.55f + n * 0.8f, 0.45f + n * 0.5f);
            px[y * size + x] = c;
        }

        // Vertical Victorian stripe bands
        int stripeW = size / 8;
        for (int x = 0; x < size; x++)
        {
            int band = (x / stripeW) % 2;
            if (band != 0) continue;
            for (int y = 0; y < size; y++)
            {
                int i = y * size + x;
                var c = px[i];
                c.r *= 0.82f;
                c.g *= 0.78f;
                c.b *= 0.72f;
                // Damask-ish dots
                float d = Mathf.Abs(Mathf.Sin(y * 0.22f) * Mathf.Cos(x * 0.15f));
                if (d > 0.85f)
                {
                    c.r = Mathf.Lerp(c.r, 0.35f, 0.35f);
                    c.g = Mathf.Lerp(c.g, 0.22f, 0.35f);
                    c.b = Mathf.Lerp(c.b, 0.18f, 0.35f);
                }
                px[i] = c;
            }
        }

        tex.SetPixels(px);
        tex.Apply(true);
        return tex;
    }

    public static Texture2D CreateFloorboards(int size = 256, int seed = 77)
    {
        var tex = NewTex("HH_Floorboards", size);
        var px = new Color[size * size];
        var rng = new System.Random(seed);
        int plankH = size / 10;

        for (int y = 0; y < size; y++)
        {
            int plank = y / plankH;
            float tone = 0.18f + (plank % 5) * 0.025f + (float)rng.NextDouble() * 0.02f;
            for (int x = 0; x < size; x++)
            {
                float grain = Mathf.Sin(x * 0.35f + plank * 1.7f) * 0.03f
                             + Hash(x, y, seed) * 0.04f;
                // Seam between planks
                bool seam = (y % plankH) == 0 || (y % plankH) == plankH - 1;
                float v = tone + grain;
                if (seam) v *= 0.45f;
                // Occasional nail
                if ((x + plank * 13) % 47 == 0 && (y % plankH) == plankH / 2)
                    v *= 0.35f;
                px[y * size + x] = new Color(v * 1.15f, v * 0.75f, v * 0.45f);
            }
        }

        // Worn path darkening
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float path = 1f - Mathf.Abs((x / (float)size) - 0.5f) * 1.4f;
            path = Mathf.Clamp01(path) * 0.12f;
            int i = y * size + x;
            var c = px[i];
            c.r *= 1f - path;
            c.g *= 1f - path;
            c.b *= 1f - path;
            px[i] = c;
        }

        tex.SetPixels(px);
        tex.Apply(true);
        return tex;
    }

    public static Texture2D CreatePlasterCeiling(int size = 256, int seed = 91)
    {
        var tex = NewTex("HH_Plaster", size);
        var px = new Color[size * size];

        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float n = Hash(x, y, seed) * 0.1f;
            px[y * size + x] = new Color(0.72f + n, 0.70f + n, 0.66f + n * 0.8f);
        }

        tex.SetPixels(px);
        tex.Apply(true);
        return tex;
    }

    public static Texture2D CreateDarkWood(int size = 256, int seed = 55)
    {
        var tex = NewTex("HH_DarkWood", size);
        var px = new Color[size * size];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float grain = Mathf.Sin(y * 0.4f + Hash(x, y, seed) * 2f) * 0.04f;
            float v = 0.12f + Hash(x, y, seed + 3) * 0.06f + grain;
            px[y * size + x] = new Color(v * 1.1f, v * 0.7f, v * 0.4f);
        }
        tex.SetPixels(px);
        tex.Apply(true);
        return tex;
    }

    static Texture2D NewTex(string name, int size)
    {
        return new Texture2D(size, size, TextureFormat.RGBA32, true, false)
        {
            name = name,
            wrapMode = TextureWrapMode.Repeat,
            filterMode = FilterMode.Bilinear,
            anisoLevel = 4
        };
    }

    static float Hash(int x, int y, int seed)
    {
        unchecked
        {
            int n = x * 374761393 + y * 668265263 + seed * 1274126177;
            n = (n ^ (n >> 13)) * 1274126177;
            n ^= n >> 16;
            return (n & 0x7fffffff) / (float)int.MaxValue;
        }
    }
}
