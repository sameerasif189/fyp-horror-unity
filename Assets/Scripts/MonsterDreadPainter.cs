using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Milestone M4 stress-5 monster painter (approved live 24 Sep 2026). Pure array maths, so it runs on
/// worker threads. Paints in the monster's own UV space:
///  - red fissures: #800000 core with a pure-black outline (crisp, wall-decal style); the fissure mask
///    covers only the centre of each core, so the heartbeat never blooms over the outline;
///  - blood in the wall-decal colours: clustered wounds of mixed sizes with a droplet spray, short curved
///    smears (UV "down" means nothing on a body, straight drips read as pins) and noise-broken edges;
///  - a steady blood-glow mask and a half-res multiply tint that strips green/blue inside the blood,
///    so it renders pure red instead of the pinkish grey-lit red the glTF material produces.
/// Known limit: models with mirrored UVs (DistortusRex, ~50% of triangles) show the paint on both halves.
/// </summary>
public static class MonsterDreadPainter
{
    public struct Settings
    {
        public int fissures;          // approved 19
        public float keepGreenBlue;   // approved 0.06
    }

    public sealed class Result
    {
        public int size;
        public Color32[] albedo;
        public byte[] fissureMask, bloodGlow;
        public int tintSize;
        public Color32[] tint;
    }

    static readonly Vector3 Red = new Vector3(128f, 0f, 0f), Black = Vector3.zero;
    static readonly Vector3 BloodLo = new Vector3(105f, 3f, 3f), BloodHi = new Vector3(155f, 8f, 6f), BloodSpec = new Vector3(30f, 4f, 3f);

    /// <summary>Rasterise a submesh's UV triangles into an n x n coverage mask (1 inside any triangle).</summary>
    public static float[] Coverage(Vector2[] uv, int[] tri, int n)
    {
        var cov = new float[n * n];
        if (uv == null || uv.Length == 0 || tri == null) { for (int i = 0; i < cov.Length; i++) cov[i] = 1f; return cov; }
        for (int t = 0; t + 2 < tri.Length; t += 3)
        {
            Vector2 a = uv[tri[t]] * n, b = uv[tri[t + 1]] * n, c = uv[tri[t + 2]] * n;
            int x0 = Mathf.Max(0, (int)Mathf.Min(a.x, Mathf.Min(b.x, c.x))), x1 = Mathf.Min(n - 1, (int)Mathf.Max(a.x, Mathf.Max(b.x, c.x)) + 1);
            int y0 = Mathf.Max(0, (int)Mathf.Min(a.y, Mathf.Min(b.y, c.y))), y1 = Mathf.Min(n - 1, (int)Mathf.Max(a.y, Mathf.Max(b.y, c.y)) + 1);
            float area = (b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y);
            if (Mathf.Abs(area) < 1e-6f) continue;
            for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                float px = x + 0.5f, py = y + 0.5f;
                float w0 = ((b.x - px) * (c.y - py) - (c.x - px) * (b.y - py)) / area;
                float w1 = ((c.x - px) * (a.y - py) - (a.x - px) * (c.y - py)) / area;
                if (w0 >= -0.01f && w1 >= -0.01f && 1f - w0 - w1 >= -0.01f) cov[y * n + x] = 1f;
            }
        }
        return cov;
    }

    public static Result Paint(Color32[] clean, float[] coverage, int n, int seed, Settings settings)
    {
        var rng = new System.Random(seed);
        float U(float a, float b) => a + (float)rng.NextDouble() * (b - a);
        void Pick(out int x, out int y)
        {
            for (int t = 0; t < 200; t++)
            {
                x = rng.Next(n); y = rng.Next(n);
                if (coverage == null || coverage[y * n + x] > 0.5f) return;
            }
            x = rng.Next(n); y = rng.Next(n);
        }
        float sc = n / 1024f;
        var bf = new float[n * n];
        var seeds = new List<(float x, float y, float s)>();

        // Irregular wound: a cluster of offset splats (no dominant disc) + a directional droplet spray.
        void Wound(float cx, float cy, float sig, float amp, int sats)
        {
            seeds.Add((cx, cy, sig));
            int parts = rng.Next(3, 7);
            for (int p = 0; p < parts; p++)
            {
                float a = U(0, 2 * Mathf.PI), d = sig * U(0f, 0.9f);
                Splat(bf, n, cx + Mathf.Cos(a) * d, cy + Mathf.Sin(a) * d, sig * U(0.35f, 0.7f), amp * U(0.7f, 1.1f));
            }
            float dir = U(0, 2 * Mathf.PI);
            for (int s = 0; s < sats; s++)
            {
                float a = dir + U(-0.7f, 0.7f), d = sig * U(1.3f, 4.0f), ds = Mathf.Max(0.8f, sig * U(0.06f, 0.2f));
                float px = cx + Mathf.Cos(a) * d, py = cy + Mathf.Sin(a) * d, amp2 = U(1.3f, 1.8f);
                for (int q = 0; q < 3; q++)
                    Splat(bf, n, px + Mathf.Cos(a) * ds * q * 0.9f, py + Mathf.Sin(a) * ds * q * 0.9f, ds * (1f - q * 0.25f), amp2);
            }
        }
        // Mixed sizes: mostly small, some medium, a few large.
        for (int i = 0; i < 12; i++)
        {
            Pick(out int cx, out int cy);
            double roll = rng.NextDouble();
            float sig = roll < 0.5 ? U(0.006f, 0.012f) : roll < 0.85 ? U(0.012f, 0.022f) : U(0.022f, 0.034f);
            Wound(cx, cy, sig * n, U(1.3f, 1.9f), rng.Next(5, 14) + (int)(sig * 300f));
        }

        var rOut = new float[n * n];
        var rIn = new float[n * n];
        for (int i = 0; i < settings.fissures; i++)
        {
            Pick(out int ox, out int oy);
            int br = rng.Next(3, 7);
            for (int k = 0; k < br; k++)
                Branch(rOut, rIn, n, ox, oy, U(0, 2 * Mathf.PI), rng.Next((int)(n * 0.14f), (int)(n * 0.34f)), 4, 0.5f, 1.0f, sc, rng);
            if (i % 3 == 0)   // some fissures bleed from their origin
                Wound(ox + U(-0.02f, 0.02f) * n, oy + U(-0.02f, 0.02f) * n, U(0.005f, 0.011f) * n, U(1.2f, 1.8f), rng.Next(3, 9));
        }
        bf = Blur(bf, n, 0.8f * sc);

        // Short, gently curving, tapering smears out of the wounds.
        for (int d = 0; d < 18 && seeds.Count > 0; d++)
        {
            var p = seeds[rng.Next(seeds.Count)];
            float ang = U(0, 2 * Mathf.PI), len = U(0.015f, 0.05f) * n, w0 = U(1.5f, 3.0f) * sc, bend = U(-0.06f, 0.06f);
            float x = p.x + Mathf.Cos(ang) * p.s * 0.4f, y = p.y + Mathf.Sin(ang) * p.s * 0.4f;
            int steps = Mathf.CeilToInt(len);
            for (int s = 0; s < steps; s++)
            {
                float t = s / (float)steps, w = Mathf.Max(0.5f, w0 * (1f - 0.85f * t));
                ang += bend + (float)(rng.NextDouble() - 0.5) * 0.05f;
                x += Mathf.Cos(ang); y += Mathf.Sin(ang);
                int ir = Mathf.CeilToInt(w);
                for (int dy = -ir; dy <= ir; dy++)
                for (int dx = -ir; dx <= ir; dx++)
                {
                    int xi = (int)x + dx, yi = (int)y + dy;
                    if (xi < 0 || yi < 0 || xi >= n || yi >= n || dx * dx + dy * dy > w * w) continue;
                    bf[yi * n + xi] = Mathf.Max(bf[yi * n + xi], 1f - 0.3f * t);
                }
            }
        }

        var noise = Fbm(n, rng);
        var low = Blur(bf, n, 3f * sc);
        float aa = 0.6f * sc;
        rOut = Blur(rOut, n, aa);
        rIn = Blur(rIn, n, aa * 0.85f);

        int h = n / 2;
        var res = new Result
        {
            size = n, albedo = new Color32[n * n], fissureMask = new byte[n * n], bloodGlow = new byte[n * n],
            tintSize = h, tint = new Color32[h * h],
        };
        var coverSum = new float[h * h];
        for (int i = 0; i < res.albedo.Length; i++)
        {
            var c = clean[i];
            var col = new Vector3(c.r * 1.02f + 6f, c.g * 0.80f, c.b * 0.76f);   // slight blood cast on the skin
            float bn = bf[i] * (0.45f + 1.1f * noise[i]);
            float t = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.34f, 0.42f, bn));
            float thick = Mathf.Clamp01(bn * 0.5f), spec = Mathf.Pow(Mathf.Clamp01((bf[i] - low[i]) * 2.4f), 1.6f);
            col = Vector3.Lerp(col, Vector3.Lerp(BloodLo, BloodHi, thick) + BloodSpec * spec, t);

            float ro = Mathf.Clamp01(rOut[i] * 1.2f), ri = Mathf.Clamp01(rIn[i] * 1.2f);
            col = Vector3.Lerp(col, Black, ro);
            col = Vector3.Lerp(col, Red, ri);
            res.albedo[i] = new Color32((byte)Mathf.Clamp(col.x, 0, 255), (byte)Mathf.Clamp(col.y, 0, 255), (byte)Mathf.Clamp(col.z, 0, 255), c.a);

            res.fissureMask[i] = (byte)(Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.35f, 0.8f, rIn[i])) * 255);
            float cover = t * (1f - ro);
            // Shaded glow: thin blood dim, thick pools and wet highlights brighter, broken up by noise.
            float shade = Mathf.Clamp01(0.30f + 0.55f * thick + 0.6f * spec) * (0.75f + 0.5f * (noise[i] - 0.5f));
            res.bloodGlow[i] = (byte)(Mathf.Clamp01(cover * shade) * 255);
            int x = i % n, y = i / n;
            if (x / 2 < h && y / 2 < h) coverSum[(y / 2) * h + x / 2] += cover * 0.25f;
        }
        for (int i = 0; i < res.tint.Length; i++)
        {
            byte gb = (byte)(Mathf.Lerp(1f, settings.keepGreenBlue, Mathf.Clamp01(coverSum[i])) * 255f);
            res.tint[i] = new Color32(255, gb, gb, 255);
        }
        return res;
    }

    static float[] Fbm(int n, System.Random rng)
    {
        var o = new float[n * n];
        int[] cells = { 24, 48, 96 };
        float[] wts = { 0.5f, 0.3f, 0.2f };
        for (int k = 0; k < 3; k++)
        {
            int c = cells[k];
            var g = new float[(c + 1) * (c + 1)];
            for (int i = 0; i < g.Length; i++) g[i] = (float)rng.NextDouble();
            for (int y = 0; y < n; y++)
            {
                float fy = y * c / (float)n; int y0 = (int)fy; float ty = fy - y0; ty = ty * ty * (3 - 2 * ty);
                for (int x = 0; x < n; x++)
                {
                    float fx = x * c / (float)n; int x0 = (int)fx; float tx = fx - x0; tx = tx * tx * (3 - 2 * tx);
                    float a = Mathf.Lerp(g[y0 * (c + 1) + x0], g[y0 * (c + 1) + x0 + 1], tx);
                    float b = Mathf.Lerp(g[(y0 + 1) * (c + 1) + x0], g[(y0 + 1) * (c + 1) + x0 + 1], tx);
                    o[y * n + x] += wts[k] * Mathf.Lerp(a, b, ty);
                }
            }
        }
        return o;
    }

    static void Branch(float[] outer, float[] inner, int n, float x, float y, float ang, int len, int depth, float jitter, float width, float sc, System.Random rng)
    {
        if (depth <= 0 || len <= 2) return;
        int steps = Mathf.Max(4, len / (int)Mathf.Max(3, 3 * sc));
        float seg = Mathf.Max(1f, len / (float)steps);
        float px = x, py = y, mx = x, my = y, w = (width + 0.5f * depth) * sc;
        for (int s = 1; s <= steps; s++)
        {
            ang += (float)(rng.NextDouble() * 2 - 1) * jitter;
            float nx = px + Mathf.Cos(ang) * seg, ny = py - Mathf.Sin(ang) * seg;
            Stamp(outer, n, px, py, nx, ny, w * 0.8f + 2.4f * sc);
            Stamp(inner, n, px, py, nx, ny, w * 0.8f);
            if (s == steps / 2) { mx = nx; my = ny; }
            px = nx; py = ny;
        }
        if (depth > 1)
            Branch(outer, inner, n, mx, my, ang + (float)(rng.NextDouble() * 2 - 1), Mathf.Max(4, len / 2), depth - 1, jitter, width, sc, rng);
    }

    static void Stamp(float[] f, int n, float x0, float y0, float x1, float y1, float rad)
    {
        float len = Mathf.Max(1f, Mathf.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0)));
        int st = Mathf.CeilToInt(len * 2f), ir = Mathf.CeilToInt(rad);
        for (int s = 0; s <= st; s++)
        {
            float t = s / (float)st, cx = Mathf.Lerp(x0, x1, t), cy = Mathf.Lerp(y0, y1, t);
            for (int dy = -ir; dy <= ir; dy++)
            for (int dx = -ir; dx <= ir; dx++)
            {
                if (dx * dx + dy * dy > rad * rad + 0.25f) continue;
                int xi = (int)cx + dx, yi = (int)cy + dy;
                if (xi >= 0 && yi >= 0 && xi < n && yi < n) f[yi * n + xi] = 1f;
            }
        }
    }

    static void Splat(float[] f, int n, float cxf, float cyf, float sig, float amp)
    {
        int cx = (int)cxf, cy = (int)cyf;
        int r = Mathf.CeilToInt(sig * 3f);
        float inv = 1f / (2f * sig * sig + 1e-6f);
        for (int y = Mathf.Max(0, cy - r); y < Mathf.Min(n, cy + r + 1); y++)
        for (int x = Mathf.Max(0, cx - r); x < Mathf.Min(n, cx + r + 1); x++)
            f[y * n + x] += amp * Mathf.Exp(-((x - cxf) * (x - cxf) + (y - cyf) * (y - cyf)) * inv);
    }

    static float[] Blur(float[] f, int n, float sig) => Pass(Pass(f, n, sig, true), n, sig, false);

    static float[] Pass(float[] f, int n, float sig, bool h)
    {
        int r = Mathf.CeilToInt(sig * 3f);
        var k = new float[2 * r + 1];
        float sum = 0;
        for (int i = -r; i <= r; i++) { k[i + r] = Mathf.Exp(-(i * i) / (2f * sig * sig)); sum += k[i + r]; }
        for (int i = 0; i < k.Length; i++) k[i] /= sum;
        var o = new float[f.Length];
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            float acc = 0;
            for (int i = -r; i <= r; i++)
            {
                int xx = h ? Mathf.Clamp(x + i, 0, n - 1) : x, yy = h ? y : Mathf.Clamp(y + i, 0, n - 1);
                acc += f[yy * n + xx] * k[i + r];
            }
            o[y * n + x] = acc;
        }
        return o;
    }
}
