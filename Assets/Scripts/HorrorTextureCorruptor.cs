using UnityEngine;

/// <summary>
/// Stress-driven physical corruption of haunted-house albedos: grime, cracks, blood,
/// scorch, growth and TV static, layered per <see cref="CorruptionRecipe"/>.
///
/// Milestone M4 reverses DEVELOPER_HANDOFF.md section 6 decision 3 ("TV static only") and
/// restores the physical overlays. Per-effect strength is chosen by
/// <see cref="CorruptionVariantSelector"/> - tune the look there, not in these painters.
///
/// Everything paints in place into a caller-owned Color[] so a regeneration allocates no
/// textures. Noise is tiled on the texture period so repeating wall/floor maps keep no seams.
/// </summary>
public static class HorrorTextureCorruptor
{
    /// <summary>Snapshot a (possibly non-readable, possibly non-square) source into dest.</summary>
    public static void ReadClean(Texture2D source, int size, Color32[] dest)
    {
        if (source == null || dest == null || dest.Length < size * size) return;
        var rt = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        Graphics.Blit(source, rt);
        var prev = RenderTexture.active;
        RenderTexture.active = rt;
        var tmp = new Texture2D(size, size, TextureFormat.RGBA32, false, false);
        tmp.ReadPixels(new Rect(0, 0, size, size), 0, 0);
        tmp.Apply(false);
        RenderTexture.active = prev;
        RenderTexture.ReleaseTemporary(rt);

        var px = tmp.GetPixels32();
        System.Array.Copy(px, dest, Mathf.Min(px.Length, dest.Length));
        SafeDestroy(tmp);
    }

    /// <summary>
    /// Copy a clean snapshot into the working buffer with a wrap-around shift, so consecutive
    /// generations are not pixel-aligned. No flip/rotate - those warped walls into the yard.
    /// </summary>
    public static void CopyShifted(Color32[] src, Color[] dest, int size, int offsetX, int offsetY)
    {
        if (src == null || dest == null) return;
        int ox = ((offsetX % size) + size) % size;
        int oy = ((offsetY % size) + size) % size;
        for (int y = 0; y < size; y++)
        {
            int sy = (y + oy) % size;
            int dRow = y * size;
            int sRow = sy * size;
            for (int x = 0; x < size; x++)
            {
                int sx = (x + ox) % size;
                var c = src[sRow + sx];
                dest[dRow + x] = new Color(c.r / 255f, c.g / 255f, c.b / 255f, c.a / 255f);
            }
        }
    }

    /// <summary>
    /// Layer every effect in the recipe into px. Order is deliberate: surface staining first,
    /// structural damage next, fluids on top, signal artefacts last.
    /// </summary>
    public static void Paint(Color[] px, int size, in CorruptionRecipe recipe)
    {
        if (px == null || recipe.IsClean) return;
        int seed = recipe.seed;
        if (recipe.grime > 0.01f) ApplyGrime(px, size, recipe.grime, seed + 11);
        if (recipe.growth > 0.01f) ApplyGrowth(px, size, recipe.growth, seed + 23);
        if (recipe.scorch > 0.01f) ApplyScorch(px, size, recipe.scorch, seed + 37);
        if (recipe.cracks > 0.01f) ApplyCracks(px, size, recipe.cracks, seed + 53);
        if (recipe.blood > 0.01f) ApplyBlood(px, size, recipe.blood, seed + 71);
        if (recipe.tvStatic > 0.01f) ApplyTvStatic(px, size, recipe.tvStatic, seed + 97);
        ApplyLevelGrade(px, size, recipe.level);
    }

    /// <summary>
    /// Per-level colour grade applied over everything else. This is what makes two passes at
    /// different levels read as different even when they happen to draw the same archetype:
    /// the house drains of colour, darkens and goes cold as stress climbs.
    /// </summary>
    static void ApplyLevelGrade(Color[] px, int size, int level)
    {
        level = Mathf.Clamp(level, 0, 5);
        // Matches the severity ramp in CorruptionVariantSelector - biggest jump is L3 -> L4.
        float[] grade = { 0f, 0.10f, 0.22f, 0.40f, 0.68f, 0.90f };
        float g = grade[level];
        if (g <= 0.001f) return;

        float desat = g * 0.55f;
        float darken = Mathf.Lerp(1f, 0.58f, g);
        for (int i = 0; i < px.Length; i++)
        {
            var c = px[i];
            float lum = c.r * 0.299f + c.g * 0.587f + c.b * 0.114f;
            c.r = Mathf.Lerp(c.r, lum, desat) * darken;
            c.g = Mathf.Lerp(c.g, lum, desat) * darken;
            c.b = Mathf.Lerp(c.b, lum, desat) * darken;
            // Cold shift: pull warmth out of the highlights as things get worse.
            c.r = Mathf.Clamp01(c.r - g * 0.030f);
            c.b = Mathf.Clamp01(c.b + g * 0.040f);
            px[i] = c;
        }
    }

    /// <summary>
    /// Mild wrap-shift copy that returns a new texture. Retained for the optional ONNX base
    /// layer, which needs a Texture2D to feed the tensor converter.
    /// </summary>
    public static Texture2D RemapSource(Texture2D source, int size, int seed)
    {
        var snapshot = new Color32[size * size];
        ReadClean(source, size, snapshot);
        var rng = new System.Random(seed);
        var work = new Color[size * size];
        CopyShifted(snapshot, work, size, rng.Next(size / 8), rng.Next(size / 8));

        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false, false)
        {
            name = (source != null ? source.name : "src") + "_remap_" + seed,
            wrapMode = TextureWrapMode.Repeat,
            filterMode = FilterMode.Bilinear
        };
        tex.SetPixels(work);
        tex.Apply(false);
        return tex;
    }

    // ---------------------------------------------------------------- painters

    /// <summary>Damp dirt: low-frequency blotches plus downward water streaking.</summary>
    static void ApplyGrime(Color[] px, int size, float amount, int seed)
    {
        int period = Mathf.Max(2, size / 32);
        int streakPeriod = Mathf.Max(2, size / 8);
        for (int y = 0; y < size; y++)
        {
            float fy = y / (float)size;
            for (int x = 0; x < size; x++)
            {
                float blotch = Fbm(x * period / (float)size, y * period / (float)size, period, seed, 4);
                // Streaks are stretched hard in Y so they read as water running down.
                float streak = Fbm(x * streakPeriod / (float)size, y * 2f / (float)size, streakPeriod, seed + 7, 3);
                float g = Mathf.Clamp01(blotch * 0.75f + streak * 0.45f - 0.28f);
                g *= 0.65f + 0.35f * fy; // pools lower down
                float d = amount * g;
                if (d <= 0.001f) continue;

                int i = y * size + x;
                var c = px[i];
                // Darken toward a cold sooty brown rather than flat grey.
                c.r = Mathf.Lerp(c.r, c.r * 0.42f + 0.06f, d);
                c.g = Mathf.Lerp(c.g, c.g * 0.38f + 0.05f, d);
                c.b = Mathf.Lerp(c.b, c.b * 0.36f + 0.06f, d);
                px[i] = c;
            }
        }
    }

    /// <summary>Branching plaster cracks drawn as slowly-turning random walks.</summary>
    static void ApplyCracks(Color[] px, int size, float amount, int seed)
    {
        var rng = new System.Random(seed);
        int count = 2 + Mathf.RoundToInt(amount * 9f);
        for (int c = 0; c < count; c++)
        {
            float x = (float)(rng.NextDouble() * size);
            float y = (float)(rng.NextDouble() * size);
            float angle = (float)(rng.NextDouble() * Mathf.PI * 2f);
            int steps = Mathf.RoundToInt(size * (0.25f + 0.75f * amount));
            float width = 0.6f + amount * 1.9f;
            WalkCrack(px, size, rng, x, y, angle, steps, width, amount, depth: 0);
        }
    }

    static void WalkCrack(Color[] px, int size, System.Random rng,
                          float x, float y, float angle, int steps, float width, float amount, int depth)
    {
        for (int s = 0; s < steps; s++)
        {
            angle += (float)(rng.NextDouble() - 0.5d) * 0.42f;
            x += Mathf.Cos(angle);
            y += Mathf.Sin(angle);
            // Wrap so cracks continue across the tile seam instead of stopping at it.
            x = Repeat(x, size);
            y = Repeat(y, size);

            float taper = width * (1f - 0.55f * s / Mathf.Max(1, steps));
            StampTint(px, size, x, y, taper, CrackColor, amount * 0.85f);

            // Occasional forks, one level deep only - deeper reads as scribble.
            if (depth == 0 && s > steps * 0.2f && rng.NextDouble() < 0.012d)
            {
                float branchAngle = angle + (rng.Next(2) == 0 ? 1f : -1f) * (0.5f + (float)rng.NextDouble() * 0.6f);
                WalkCrack(px, size, rng, x, y, branchAngle, steps / 3, taper * 0.7f, amount * 0.8f, depth + 1);
            }
        }
    }

    /// <summary>Splatter blobs with irregular edges, plus drips running down from them.</summary>
    static void ApplyBlood(Color[] px, int size, float amount, int seed)
    {
        var rng = new System.Random(seed);
        int blobs = 1 + Mathf.RoundToInt(amount * 6f);
        for (int b = 0; b < blobs; b++)
        {
            float cx = (float)(rng.NextDouble() * size);
            float cy = (float)(rng.NextDouble() * size);
            float radius = size * (0.03f + (float)rng.NextDouble() * 0.09f * amount);
            int edgeSeed = seed + b * 613;

            int r = Mathf.CeilToInt(radius * 1.6f);
            for (int dy = -r; dy <= r; dy++)
            for (int dx = -r; dx <= r; dx++)
            {
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                // Ragged rim: perturb the radius by angle-indexed noise.
                float ang = Mathf.Atan2(dy, dx);
                float wobble = 0.65f + 0.55f * ValueNoise1D(ang * 2.4f, edgeSeed);
                float rr = radius * wobble;
                if (d > rr) continue;
                float a = amount * Mathf.Clamp01(1f - Mathf.Pow(d / Mathf.Max(0.0001f, rr), 2.2f));
                if (a <= 0.004f) continue;
                int ix = (int)Repeat(cx + dx, size);
                int iy = (int)Repeat(cy + dy, size);
                int i = iy * size + ix;
                px[i] = Color.Lerp(px[i], BloodColor, a * 0.92f);
            }

            // Drips: narrow runs falling from the lower edge of the blob.
            int drips = 1 + rng.Next(1 + Mathf.RoundToInt(amount * 4f));
            for (int dp = 0; dp < drips; dp++)
            {
                float dx0 = cx + (float)(rng.NextDouble() - 0.5d) * radius * 1.3f;
                float len = radius * (1.2f + (float)rng.NextDouble() * 5f * amount);
                float w = 0.7f + (float)rng.NextDouble() * 1.5f;
                for (float t = 0; t < len; t += 0.7f)
                {
                    float fade = amount * (1f - t / Mathf.Max(0.0001f, len)) * 0.85f;
                    StampTint(px, size, dx0, cy + radius * 0.6f + t, w * (1f - 0.4f * t / len), BloodColor, fade);
                }
            }
        }
    }

    /// <summary>Charring: near-black cores with sooty, noise-broken edges.</summary>
    static void ApplyScorch(Color[] px, int size, float amount, int seed)
    {
        var rng = new System.Random(seed);
        int burns = 1 + Mathf.RoundToInt(amount * 4f);
        int period = Mathf.Max(2, size / 16);
        for (int b = 0; b < burns; b++)
        {
            float cx = (float)(rng.NextDouble() * size);
            float cy = (float)(rng.NextDouble() * size);
            float radius = size * (0.08f + (float)rng.NextDouble() * 0.22f * amount);
            int r = Mathf.CeilToInt(radius);
            for (int dy = -r; dy <= r; dy++)
            for (int dx = -r; dx <= r; dx++)
            {
                float d = Mathf.Sqrt(dx * dx + dy * dy) / Mathf.Max(0.0001f, radius);
                if (d > 1f) continue;
                int ix = (int)Repeat(cx + dx, size);
                int iy = (int)Repeat(cy + dy, size);
                float n = Fbm(ix * period / (float)size, iy * period / (float)size, period, seed + b, 3);
                // Noise eats into the falloff so the burn edge is broken, not a clean disc.
                float a = amount * Mathf.Clamp01((1f - d) * 1.35f - n * 0.55f);
                if (a <= 0.004f) continue;
                int i = iy * size + ix;
                var c = px[i];
                c.r = Mathf.Lerp(c.r, 0.055f, a);
                c.g = Mathf.Lerp(c.g, 0.045f, a);
                c.b = Mathf.Lerp(c.b, 0.048f, a);
                px[i] = c;
            }
        }
    }

    /// <summary>Mould and fungal bloom: threshold-clustered patches with speckle.</summary>
    static void ApplyGrowth(Color[] px, int size, float amount, int seed)
    {
        int period = Mathf.Max(2, size / 24);
        int specklePeriod = Mathf.Max(2, size / 3);
        float threshold = Mathf.Lerp(0.62f, 0.34f, amount);
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float n = Fbm(x * period / (float)size, y * period / (float)size, period, seed, 4);
            if (n < threshold) continue;
            float a = amount * Mathf.Clamp01((n - threshold) / Mathf.Max(0.0001f, 1f - threshold));
            float speckle = Fbm(x * specklePeriod / (float)size, y * specklePeriod / (float)size, specklePeriod, seed + 3, 2);
            a *= 0.55f + 0.65f * speckle;
            if (a <= 0.004f) continue;
            int i = y * size + x;
            px[i] = Color.Lerp(px[i], MossColor, Mathf.Clamp01(a * 0.85f));
        }
    }

    /// <summary>
    /// The established look: scanlines, snow, roll bars, horizontal tears and RGB split.
    /// Driven by a 0..1 amount rather than a stress level so it can sit under other effects.
    /// </summary>
    static void ApplyTvStatic(Color[] px, int size, float amount, int seed)
    {
        float t = Mathf.Clamp01(amount);
        if (t <= 0.01f) return;
        var rng = new System.Random(seed);

        int scanStep = t < 0.45f ? 2 : 1;
        float scan = 0.18f + 0.62f * t;
        for (int y = 0; y < size; y += scanStep)
        {
            float a = scan * (0.45f + 0.55f * Hash01(y, seed, rng));
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

        int snow = (int)(size * size * (0.04f + 0.38f * t));
        for (int s = 0; s < snow; s++)
        {
            int i = rng.Next(px.Length);
            float n = (float)rng.NextDouble();
            float a = (0.45f + 0.55f * t) * t;
            var c = px[i];
            px[i] = Color.Lerp(c, new Color(n, n, n, c.a), a);
        }

        int bars = 1 + Mathf.RoundToInt(t * 5f);
        for (int b = 0; b < bars; b++)
        {
            int barY = rng.Next(size);
            int barH = 2 + (int)(22 * t);
            float barA = (0.22f + 0.58f * t) * t;
            for (int y = barY; y < barY + barH && y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int i = y * size + x;
                var c = px[i];
                float n = 0.55f + 0.45f * (float)rng.NextDouble();
                px[i] = Color.Lerp(c, new Color(n, n, n), barA);
            }
        }

        var copy = (Color[])px.Clone();
        int tears = Mathf.RoundToInt(3 + t * 30f);
        for (int k = 0; k < tears; k++)
        {
            int y0 = rng.Next(size);
            int h = 1 + rng.Next(3 + Mathf.RoundToInt(t * 10f));
            int shift = (rng.Next(2) * 2 - 1) * (4 + rng.Next(8 + Mathf.RoundToInt(t * 30f)));
            for (int y = y0; y < y0 + h && y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int sx = (x + shift) % size;
                if (sx < 0) sx += size;
                px[y * size + x] = copy[y * size + sx];
            }
        }

        if (t >= 0.34f)
        {
            System.Array.Copy(px, copy, px.Length);
            int split = 1 + Mathf.RoundToInt(t * 5f);
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

    // ---------------------------------------------------------------- helpers

    static readonly Color CrackColor = new Color(0.05f, 0.04f, 0.04f);
    static readonly Color BloodColor = new Color(0.30f, 0.025f, 0.03f);
    static readonly Color MossColor = new Color(0.17f, 0.23f, 0.10f);

    /// <summary>Alpha-weighted round stamp that wraps at the tile edge.</summary>
    static void StampTint(Color[] px, int size, float cx, float cy, float radius, Color tint, float strength)
    {
        int r = Mathf.Max(1, Mathf.CeilToInt(radius));
        for (int dy = -r; dy <= r; dy++)
        for (int dx = -r; dx <= r; dx++)
        {
            float d = Mathf.Sqrt(dx * dx + dy * dy);
            if (d > radius) continue;
            float a = strength * (1f - d / Mathf.Max(0.0001f, radius));
            if (a <= 0.003f) continue;
            int ix = (int)Repeat(cx + dx, size);
            int iy = (int)Repeat(cy + dy, size);
            int i = iy * size + ix;
            px[i] = Color.Lerp(px[i], tint, a);
        }
    }

    static float Repeat(float v, int size)
    {
        v %= size;
        if (v < 0) v += size;
        return v;
    }

    static float Hash01(int y, int seed, System.Random rng)
    {
        return (float)((y * 13 + seed * 7 + rng.Next(8)) % 100) / 100f;
    }

    static float Hash2(int x, int y, int seed)
    {
        unchecked
        {
            // uint throughout: two of these mixing constants sit above int.MaxValue.
            uint h = (uint)seed * 374761393u + (uint)x * 668265263u;
            h = (h ^ (h >> 13)) * 1274126177u;
            h ^= h >> 16;
            h = h * 2246822519u + (uint)y * 3266489917u;
            h ^= h >> 15;
            return (h & 0x7fffffffu) / 2147483647f;
        }
    }

    /// <summary>Value noise on a wrapped lattice, so output tiles on <paramref name="period"/>.</summary>
    static float ValueNoise(float x, float y, int period, int seed)
    {
        int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y);
        float fx = x - x0, fy = y - y0;
        fx = fx * fx * (3f - 2f * fx);
        fy = fy * fy * (3f - 2f * fy);
        int xa = Wrap(x0, period), xb = Wrap(x0 + 1, period);
        int ya = Wrap(y0, period), yb = Wrap(y0 + 1, period);
        float a = Hash2(xa, ya, seed), b = Hash2(xb, ya, seed);
        float c = Hash2(xa, yb, seed), d = Hash2(xb, yb, seed);
        return Mathf.Lerp(Mathf.Lerp(a, b, fx), Mathf.Lerp(c, d, fx), fy);
    }

    static float Fbm(float x, float y, int period, int seed, int octaves)
    {
        float sum = 0f, amp = 0.5f, norm = 0f, freq = 1f;
        int p = Mathf.Max(1, period);
        for (int o = 0; o < octaves; o++)
        {
            sum += amp * ValueNoise(x * freq, y * freq, p, seed + o * 131);
            norm += amp;
            amp *= 0.5f;
            freq *= 2f;
            p *= 2;
        }
        return sum / Mathf.Max(norm, 1e-4f);
    }

    static float ValueNoise1D(float x, int seed)
    {
        int x0 = Mathf.FloorToInt(x);
        float f = x - x0;
        f = f * f * (3f - 2f * f);
        return Mathf.Lerp(Hash2(x0, 0, seed), Hash2(x0 + 1, 0, seed), f);
    }

    static int Wrap(int v, int period)
    {
        if (period <= 0) return v;
        v %= period;
        return v < 0 ? v + period : v;
    }

    /// <summary>Destroy that also works from editor code paths (the builder previews in edit mode).</summary>
    internal static void SafeDestroy(Object o)
    {
        if (o == null) return;
        if (Application.isPlaying) Object.Destroy(o);
        else Object.DestroyImmediate(o);
    }
}
