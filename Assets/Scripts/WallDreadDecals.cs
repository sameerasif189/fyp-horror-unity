using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

/// <summary>
/// Milestone M4 stress-5 wall decals. Cracks and blood live in a decal layer, not in the tiling
/// wall texture - baked into a tiling texture they repeated visibly every tile. A 16-cell atlas is
/// painted on worker threads, then ~19k clipped quads are scattered at random positions,
/// sizes and flips over every visible house face and batched into one mesh per box.
/// One instance is one set from one seed; <see cref="HouseDreadLook"/> builds a fresh set for every
/// visit to stress 5 while the player is below it. The sets share one <see cref="Textures"/>, which the worker
/// writes into directly, and the main-thread part (upload, placement, meshes) is spread over frames.
///
/// Atlas: 0-5 red crack (#800000, black outline) | 6-8 black crack (#8b0000 outline) + drips |
/// 9-10 black crack + pool | 11-13 blood + drips | 14-15 blood pool.
/// Blood decals sit just behind the cracks so a crack always draws over blood.
/// </summary>
public sealed class WallDreadDecals : IDisposable
{
    public float redDensity = 2.75f, blackDensity = 1.4f, bloodDensity = 0.6f;   // decals per m2 on walls

    const int Cells = 4, C = 512, N = C * Cells, Margin = 24;
    /// <summary>Main-thread time the build may take per frame.</summary>
    const double BuildBudgetMs = 2.5;
    static readonly Vector3 Red = new Vector3(128f, 0f, 0f), RedRim = new Vector3(139f, 0f, 0f), Black = Vector3.zero;
    static readonly Vector3 BloodLo = new Vector3(105f, 3f, 3f), BloodHi = new Vector3(155f, 8f, 6f), BloodSpec = new Vector3(30f, 4f, 3f);

    /// <summary>
    /// The atlas and its gloss / emission maps, shared by every set. Allocating fresh 2048 textures cost ~40 ms on
    /// the main thread - a hitch each time the player left stress 5 - so they are made once and kept readable
    /// (~34 MB of CPU memory): each set's worker writes its pixels and mips straight into their CPU memory and its
    /// build only calls Apply. A set that was drawn with the old atlas shows the new one after that Apply, which is
    /// harmless: every set uses the same cell layout.
    /// </summary>
    public sealed class Textures : IDisposable
    {
        public readonly Texture2D atlas, gloss, emit;

        public Textures()
        {
            atlas = new Texture2D(N, N, TextureFormat.RGBA32, true)
            {
                name = "DreadDecalAtlas", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear, anisoLevel = 4,
            };
            gloss = new Texture2D(N / 2, N / 2, TextureFormat.RGBA32, true, true) { name = "DreadDecalGloss", wrapMode = TextureWrapMode.Clamp };
            emit = new Texture2D(N / 2, N / 2, TextureFormat.RGBA32, true, true) { name = "DreadDecalEmit", wrapMode = TextureWrapMode.Clamp };
        }

        public void Dispose()
        {
            HorrorTextureCorruptor.SafeDestroy(atlas);
            HorrorTextureCorruptor.SafeDestroy(gloss);
            HorrorTextureCorruptor.SafeDestroy(emit);
        }
    }

    readonly Material _template;
    readonly Textures _tex;
    readonly List<Object> _owned = new List<Object>();
    List<HouseSurfaceGeometry.Face> _faces;
    Task _paint;
    IEnumerator _build;
    GameObject _root;
    Material _mat;
    int _seed;
    bool _visible;
    Color _emission;
    double _paintMs, _buildMs, _maxSliceMs;
    int _buildFrames;

    public bool Built { get; private set; }
    /// <summary>The atlas paint threw; this set will never build.</summary>
    public bool Failed { get; private set; }
    public int DecalCount { get; private set; }

    /// <param name="template">URP Lit, alpha-clipped, emissive, gloss map, env reflections off. May be null.</param>
    /// <param name="textures">Shared with the other sets; only one set may be painting at a time.</param>
    public WallDreadDecals(Material template, Textures textures)
    {
        _template = template;
        _tex = textures;
    }

    /// <param name="threads">Worker threads for the atlas paint; 0 = all cores.</param>
    public void Begin(List<HouseSurfaceGeometry.Face> faces, int seed, int threads = 0)
    {
        _faces = faces;
        _seed = seed;
        // Views into the textures' CPU memory, taken here on the main thread and filled by the worker.
        var atlasMips = MipViews(_tex.atlas);
        var glossMips = MipViews(_tex.gloss);
        var emitMips = MipViews(_tex.emit);
        _paint = Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            PaintAtlas(seed, threads, atlasMips, glossMips, emitMips);
            _paintMs = sw.Elapsed.TotalMilliseconds;
        });
    }

    static NativeArray<Color32>[] MipViews(Texture2D t)
    {
        var v = new NativeArray<Color32>[t.mipmapCount];
        for (int m = 0; m < v.Length; m++) v[m] = t.GetPixelData<Color32>(m);
        return v;
    }

    /// <summary>
    /// Main thread, every frame: once the worker has painted the atlas, builds the textures and meshes
    /// a slice at a time (<see cref="BuildBudgetMs"/> per frame), then shows the set if it is visible.
    /// </summary>
    public void Tick()
    {
        if (Built || Failed) return;
        if (_build == null)
        {
            if (_paint == null || !_paint.IsCompleted) return;
            var task = _paint;
            _paint = null;
            if (task.IsFaulted)
            {
                Failed = true;
                Debug.LogWarning("[WallDreadDecals] atlas paint failed: " + task.Exception?.GetBaseException().Message);
                return;
            }
            _build = Build();
        }
        var sw = Stopwatch.StartNew();
        _buildFrames++;
        bool more;
        do more = _build.MoveNext();
        while (more && sw.Elapsed.TotalMilliseconds < BuildBudgetMs);
        _buildMs += sw.Elapsed.TotalMilliseconds;
        _maxSliceMs = Math.Max(_maxSliceMs, sw.Elapsed.TotalMilliseconds);
        if (more) return;
        _build = null;
        Built = true;
        SetVisible(_visible);
        SetEmission(_emission);
        Debug.Log($"[WallDreadDecals] built {DecalCount} decals (seed {_seed}): atlas {_paintMs:F0} ms off-thread, " +
                  $"{_buildMs:F0} ms main thread over {_buildFrames} frames (longest {_maxSliceMs:F1} ms).");
    }

    public void SetVisible(bool on)
    {
        _visible = on;
        if (_root != null && _root.activeSelf != on) _root.SetActive(on);
    }

    public void SetEmission(Color c)
    {
        _emission = c;
        if (_mat != null) _mat.SetColor("_EmissionColor", c);
    }

    /// <summary>
    /// Destroys the set (not the shared textures). A paint still running is waited for, because it writes into the
    /// shared textures' memory and the owner may destroy them next; a build in progress stops here.
    /// </summary>
    public void Dispose()
    {
        try { _paint?.Wait(); }
        catch (AggregateException) { }
        _build = null;
        _paint = null;
        _faces = null;
        if (_root != null) HorrorTextureCorruptor.SafeDestroy(_root);
        foreach (var o in _owned) HorrorTextureCorruptor.SafeDestroy(o);
        _owned.Clear();
        _root = null;
        _mat = null;
    }

    // ------------------------------------------------------------------ atlas (worker threads)

    static int KindOf(int cell) => cell < 6 ? 0 : cell < 9 ? 1 : cell < 11 ? 2 : cell < 14 ? 3 : 4;

    void PaintAtlas(int seed, int threads, NativeArray<Color32>[] atlasMips, NativeArray<Color32>[] glossMips,
                    NativeArray<Color32>[] emitMips)
    {
        var atlas = new Color32[N * N];
        var emit = new byte[N * N];
        var gloss = new byte[N * N];
        var opts = new ParallelOptions { MaxDegreeOfParallelism = threads > 0 ? threads : -1 };
        Parallel.For(0, Cells * Cells, opts, cell =>
        {
            var rng = new System.Random(seed * 31 + cell);
            var col = new Color32[C * C];
            var em = new float[C * C];
            var gl = new float[C * C];
            PaintCell(KindOf(cell), rng, col, em, gl);
            int cx = cell % Cells, cy = cell / Cells;
            for (int y = 0; y < C; y++)
            for (int x = 0; x < C; x++)
            {
                int d = (cy * C + y) * N + cx * C + x, s = y * C + x;
                atlas[d] = col[s];
                emit[d] = (byte)(Mathf.Clamp01(em[s]) * 255f);
                gloss[d] = (byte)(Mathf.Clamp01(gl[s]) * 255f);
            }
        });
        WriteMipChain(atlas, N, atlasMips);
        WriteMipChain(HalfPixels(gloss, true), N / 2, glossMips);
        WriteMipChain(HalfPixels(emit, false), N / 2, emitMips);
    }

    /// <summary>
    /// Writes <paramref name="px"/> (<paramref name="w"/> square) and its 2x2 box-filtered mip chain - what
    /// Texture2D.Apply(true) generated - into a texture's mip views. Worker thread.
    /// </summary>
    static void WriteMipChain(Color32[] px, int w, NativeArray<Color32>[] mips)
    {
        NativeArray<Color32>.Copy(px, mips[0]);
        for (int m = 1; m < mips.Length; m++)
        {
            int h = Math.Max(1, w / 2);
            var next = new Color32[h * h];
            for (int y = 0; y < h; y++)
            {
                int y0 = Math.Min(2 * y, w - 1) * w, y1 = Math.Min(2 * y + 1, w - 1) * w;
                for (int x = 0; x < h; x++)
                {
                    int x0 = Math.Min(2 * x, w - 1), x1 = Math.Min(2 * x + 1, w - 1);
                    Color32 a = px[y0 + x0], b = px[y0 + x1], c = px[y1 + x0], d = px[y1 + x1];
                    next[y * h + x] = new Color32((byte)((a.r + b.r + c.r + d.r + 2) >> 2), (byte)((a.g + b.g + c.g + d.g + 2) >> 2),
                                                  (byte)((a.b + b.b + c.b + d.b + 2) >> 2), (byte)((a.a + b.a + c.a + d.a + 2) >> 2));
                }
            }
            NativeArray<Color32>.Copy(next, mips[m]);
            px = next;
            w = h;
        }
    }

    static void PaintCell(int kind, System.Random rng, Color32[] col, float[] em, float[] gl)
    {
        float U(float a, float b) => a + (float)rng.NextDouble() * (b - a);
        var outer = new float[C * C];
        var inner = new float[C * C];
        var bf = new float[C * C];
        var seeds = new List<(float x, float y, float s)>();
        bool cracks = kind <= 2, hasBlood = kind != 0, drips = kind == 1 || kind == 3;

        // Lobed wound + a directional spray of elongated droplets.
        void Wound(float cx, float cy, float sig, float amp, int sats)
        {
            Splat(bf, cx, cy, sig, amp);
            seeds.Add((cx, cy, sig));
            int lobes = rng.Next(2, 5);
            for (int l = 0; l < lobes; l++)
            {
                float a = U(0, 2 * Mathf.PI), d = sig * U(0.5f, 1.2f);
                Splat(bf, cx + Mathf.Cos(a) * d, cy + Mathf.Sin(a) * d, sig * U(0.4f, 0.8f), amp * U(0.6f, 1.0f));
            }
            float dir = U(0, 2 * Mathf.PI);
            for (int s = 0; s < sats; s++)
            {
                float a = dir + U(-0.7f, 0.7f), d = sig * U(1.4f, 4.5f), ds = Mathf.Max(0.8f, sig * U(0.07f, 0.25f));
                float px = cx + Mathf.Cos(a) * d, py = cy + Mathf.Sin(a) * d, amp2 = U(1.3f, 1.8f);
                for (int q = 0; q < 3; q++)
                    Splat(bf, px + Mathf.Cos(a) * ds * q * 0.9f, py + Mathf.Sin(a) * ds * q * 0.9f, ds * (1f - q * 0.25f), amp2);
            }
        }

        float ox = C / 2 + U(-50, 50);
        float oy = drips ? U(C * 0.58f, C * 0.72f) : C / 2 + U(-50, 50);   // drip cells start high to leave room below
        if (cracks)
        {
            int br = kind == 0 ? rng.Next(3, 6) : rng.Next(2, 5);
            for (int k = 0; k < br; k++)
            {
                float ang = k * 2f * Mathf.PI / br + U(-0.5f, 0.5f);
                if (kind == 0) Branch(outer, inner, ox, oy, ang, rng.Next((int)(C * 0.22f), (int)(C * 0.42f)), 3, 0.35f, 1.0f, rng);
                else Branch(outer, inner, ox, oy, ang, rng.Next((int)(C * 0.20f), (int)(C * 0.36f)), 3, 0.55f, 1.3f, rng);
            }
            outer = Blur(outer, 0.7f);
            inner = Blur(inner, 0.6f);
        }
        if (kind == 1 || kind == 2)
        {
            int wn = rng.Next(1, 3);
            for (int w = 0; w < wn; w++) Wound(ox + U(-40, 40), oy + U(-30, 30), U(8f, kind == 2 ? 20f : 14f), U(1.2f, 1.8f), rng.Next(4, 10));
            for (int i = 0; i < bf.Length; i++) bf[i] += inner[i] * 0.35f;
        }
        else if (kind == 3 || kind == 4)
        {
            Wound(ox, oy, U(18f, kind == 4 ? 42f : 30f), U(1.4f, 2.0f), rng.Next(8, 18));
            if (rng.NextDouble() < 0.6) Wound(ox + U(-90, 90), oy + U(-60, 40), U(8f, 16f), U(1.2f, 1.7f), rng.Next(3, 9));
        }
        if (hasBlood)
        {
            bf = Blur(bf, 1.0f);
            if (drips)
            {
                int dn = kind == 3 ? rng.Next(5, 11) : rng.Next(4, 9);
                for (int d = 0; d < dn; d++)
                {
                    var p = seeds[rng.Next(seeds.Count)];
                    float x = p.x + U(-p.s, p.s) * 0.8f, y0 = p.y - p.s * 0.6f, len = U(40f, kind == 3 ? 230f : 170f), w0 = U(1.4f, 2.8f);
                    int yy = 0;
                    for (; yy < len; yy++)
                    {
                        int y = (int)(y0 - yy);
                        if (y < Margin) break;
                        float w = w0 * (1f - 0.45f * yy / len);
                        x += (float)(rng.NextDouble() - 0.5) * 0.3f;
                        for (int dx = -Mathf.CeilToInt(w); dx <= Mathf.CeilToInt(w); dx++)
                        {
                            int xi = (int)x + dx;
                            if (xi < Margin || xi >= C - Margin || Mathf.Abs(dx) > w) continue;
                            bf[y * C + xi] = Mathf.Max(bf[y * C + xi], 1f);
                        }
                    }
                    Splat(bf, x, y0 - yy, w0 * 1.1f, 1.4f);
                }
            }
        }

        var noise = hasBlood ? Fbm(rng) : null;
        var low = hasBlood ? Blur(bf, 4f) : bf;
        for (int i = 0; i < col.Length; i++)
        {
            // Noise-broken threshold: blood edges are crisp but never a clean disc.
            float bn = hasBlood ? bf[i] * (0.55f + 0.9f * noise[i]) : 0f;
            float blood = hasBlood ? Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.34f, 0.44f, bn)) : 0f;
            float thick = Mathf.Clamp01(bn * 0.5f);
            float spec = hasBlood ? Mathf.Pow(Mathf.Clamp01((bf[i] - low[i]) * 2.4f), 1.6f) : 0f;
            var c = hasBlood ? Vector3.Lerp(BloodLo, BloodHi, thick) + BloodSpec * spec : Black;
            float g = hasBlood ? Mathf.Lerp(0.65f, 0.85f, thick) : 0f;            // wet blood is glossy
            float e = hasBlood ? blood * Mathf.Lerp(0.22f, 0.34f, thick) : 0f;     // faint steady glow
            float o = cracks ? Mathf.Clamp01(outer[i] * 1.2f) : 0f;
            float n = cracks ? Mathf.Clamp01(inner[i] * 1.2f) : 0f;
            if (kind == 0)
            {
                c = Vector3.Lerp(c, Black, o); g = Mathf.Lerp(g, 0f, o);
                c = Vector3.Lerp(c, Red, n); g = Mathf.Lerp(g, 0.3f, n); e = Mathf.Max(e, n);
            }
            else if (cracks)
            {
                c = Vector3.Lerp(c, RedRim, o); g = Mathf.Lerp(g, 0.15f, o); e = Mathf.Lerp(e, 0.6f, Mathf.Clamp01(o - n));
                // Saturate the blurred core so it is pure black, never grey-brown.
                float core = Mathf.Clamp01(inner[i] * 2.2f);
                c = Vector3.Lerp(c, Black, core); g = Mathf.Lerp(g, 0f, core); e = Mathf.Lerp(e, 0f, core);
            }
            float alpha = Mathf.Max(blood, o);
            col[i] = new Color32((byte)Mathf.Clamp(c.x, 0, 255), (byte)Mathf.Clamp(c.y, 0, 255), (byte)Mathf.Clamp(c.z, 0, 255), (byte)(alpha * 255f));
            em[i] = e;
            gl[i] = g;
        }
    }

    static float[] Fbm(System.Random rng)
    {
        var o = new float[C * C];
        int[] cells = { 8, 16, 32 };
        float[] wts = { 0.5f, 0.3f, 0.2f };
        for (int k = 0; k < 3; k++)
        {
            int c = cells[k];
            var g = new float[(c + 1) * (c + 1)];
            for (int i = 0; i < g.Length; i++) g[i] = (float)rng.NextDouble();
            for (int y = 0; y < C; y++)
            {
                float fy = y * c / (float)C; int y0 = (int)fy; float ty = fy - y0; ty = ty * ty * (3 - 2 * ty);
                for (int x = 0; x < C; x++)
                {
                    float fx = x * c / (float)C; int x0 = (int)fx; float tx = fx - x0; tx = tx * tx * (3 - 2 * tx);
                    float a = Mathf.Lerp(g[y0 * (c + 1) + x0], g[y0 * (c + 1) + x0 + 1], tx);
                    float b = Mathf.Lerp(g[(y0 + 1) * (c + 1) + x0], g[(y0 + 1) * (c + 1) + x0 + 1], tx);
                    o[y * C + x] += wts[k] * Mathf.Lerp(a, b, ty);
                }
            }
        }
        return o;
    }

    static void Branch(float[] outer, float[] inner, float x, float y, float ang, int len, int depth, float jitter, float width, System.Random rng)
    {
        if (depth <= 0 || len <= 2) return;
        int steps = Mathf.Max(4, len / 3);
        float seg = Mathf.Max(1f, len / (float)steps);
        float px = x, py = y, mx = x, my = y, w = width + 0.5f * depth;
        for (int s = 1; s <= steps; s++)
        {
            ang += (float)(rng.NextDouble() * 2 - 1) * jitter;
            float nx = px + Mathf.Cos(ang) * seg, ny = py - Mathf.Sin(ang) * seg;
            Stamp(outer, px, py, nx, ny, w * 0.8f + 1.9f);
            Stamp(inner, px, py, nx, ny, w * 0.8f);
            if (s == steps / 2) { mx = nx; my = ny; }
            px = nx; py = ny;
        }
        if (depth > 1) Branch(outer, inner, mx, my, ang + (float)(rng.NextDouble() * 2 - 1), Mathf.Max(4, len / 2), depth - 1, jitter, width, rng);
    }

    static void Stamp(float[] f, float x0, float y0, float x1, float y1, float rad)
    {
        float len = Mathf.Max(1f, Mathf.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0)));
        int st = Mathf.CeilToInt(len * 2f), ir = Mathf.CeilToInt(rad);
        for (int s = 0; s <= st; s++)
        {
            float t = s / (float)st, cx = Mathf.Lerp(x0, x1, t), cy = Mathf.Lerp(y0, y1, t);
            for (int dy = -ir; dy <= ir; dy++)
            for (int dx = -ir; dx <= ir; dx++)
            {
                int xi = (int)cx + dx, yi = (int)cy + dy;
                if (xi < Margin || yi < Margin || xi >= C - Margin || yi >= C - Margin) continue;
                if (dx * dx + dy * dy <= rad * rad + 0.25f) f[yi * C + xi] = 1f;
            }
        }
    }

    static void Splat(float[] f, float cx, float cy, float sig, float amp)
    {
        int r = Mathf.CeilToInt(sig * 3f);
        float inv = 1f / (2f * sig * sig);
        for (int dy = -r; dy <= r; dy++)
        for (int dx = -r; dx <= r; dx++)
        {
            int xi = (int)cx + dx, yi = (int)cy + dy;
            if (xi < Margin || yi < Margin || xi >= C - Margin || yi >= C - Margin) continue;
            float ddx = xi - cx, ddy = yi - cy;
            f[yi * C + xi] += amp * Mathf.Exp(-(ddx * ddx + ddy * ddy) * inv);
        }
    }

    static float[] Blur(float[] f, float sig) => Pass(Pass(f, sig, true), sig, false);

    static float[] Pass(float[] f, float sig, bool h)
    {
        int r = Mathf.CeilToInt(sig * 3f);
        var k = new float[2 * r + 1];
        float sum = 0;
        for (int i = -r; i <= r; i++) { k[i + r] = Mathf.Exp(-(i * i) / (2f * sig * sig)); sum += k[i + r]; }
        for (int i = 0; i < k.Length; i++) k[i] /= sum;
        var o = new float[f.Length];
        for (int y = 0; y < C; y++)
        for (int x = 0; x < C; x++)
        {
            float acc = 0;
            for (int i = -r; i <= r; i++)
            {
                int xx = h ? Mathf.Clamp(x + i, 0, C - 1) : x, yy = h ? y : Mathf.Clamp(y + i, 0, C - 1);
                acc += f[yy * C + xx] * k[i + r];
            }
            o[y * C + x] = acc;
        }
        return o;
    }

    /// <summary>Half-resolution copy of an R8 mask as RGBA (gloss in alpha, emission in RGB). Worker thread.</summary>
    static Color32[] HalfPixels(byte[] src, bool gloss)
    {
        int H = N / 2;
        var px = new Color32[H * H];
        for (int y = 0; y < H; y++)
        for (int x = 0; x < H; x++)
        {
            int s = src[(2 * y) * N + 2 * x] + src[(2 * y) * N + 2 * x + 1] + src[(2 * y + 1) * N + 2 * x] + src[(2 * y + 1) * N + 2 * x + 1];
            byte v = (byte)(s / 4);
            px[y * H + x] = gloss ? new Color32(0, 0, 0, v) : new Color32(v, v, v, 255);
        }
        return px;
    }

    // ------------------------------------------------------------------ build + placement (main thread, sliced)

    /// <summary>Each <c>yield</c> is a point where <see cref="Tick"/> may carry on next frame.</summary>
    IEnumerator Build()
    {
        // The worker already wrote every mip; this only queues the uploads.
        _tex.atlas.Apply(false, false);
        _tex.gloss.Apply(false, false);
        _tex.emit.Apply(false, false);
        yield return null;

        _mat = _template != null ? new Material(_template) : CreateFallbackMaterial();
        _mat.name = "DreadDecals (runtime)";
        // URP's material validation drops these when a slot is empty; the runtime copy always needs them.
        _mat.EnableKeyword("_ALPHATEST_ON");
        _mat.EnableKeyword("_METALLICSPECGLOSSMAP");
        _mat.EnableKeyword("_EMISSION");
        _mat.EnableKeyword("_ENVIRONMENTREFLECTIONS_OFF");
        _mat.SetTexture("_BaseMap", _tex.atlas);
        _mat.SetTexture("_MetallicGlossMap", _tex.gloss);
        _mat.SetTexture("_EmissionMap", _tex.emit);
        _owned.Add(_mat);
        yield return null;

        _root = new GameObject("DreadDecals");
        _root.SetActive(false);
        var rng = new System.Random(_seed);
        float U(float a, float b) => a + (float)rng.NextDouble() * (b - a);
        int placed = 0;

        var byRenderer = new Dictionary<MeshRenderer, List<HouseSurfaceGeometry.Face>>();
        foreach (var f in _faces)
        {
            if (f.depth != 0 || f.renderer == null) continue;
            if (!byRenderer.TryGetValue(f.renderer, out var l)) byRenderer[f.renderer] = l = new List<HouseSurfaceGeometry.Face>();
            l.Add(f);
        }

        var V = new List<Vector3>(); var Nn = new List<Vector3>(); var UV = new List<Vector2>(); var T = new List<int>();
        foreach (var kv in byRenderer)
        {
            if (kv.Key == null) continue;   // the build spans frames
            V.Clear(); Nn.Clear(); UV.Clear(); T.Clear();
            foreach (var f in kv.Value)
            {
                int ax = f.axis, right = ax == 1 ? 0 : (ax == 0 ? 2 : 0), up = ax == 1 ? 2 : 1;
                bool rightFirst = right == (ax + 1) % 3, upFirst = up == (ax + 1) % 3;
                float r0 = rightFirst ? f.min.x : f.min.y, r1 = rightFirst ? f.max.x : f.max.y;
                float u0 = upFirst ? f.min.x : f.min.y, u1 = upFirst ? f.max.x : f.max.y;
                float area = (r1 - r0) * (u1 - u0);
                if (area < 0.01f) continue;
                bool vertical = ax != 1;
                bool floorish = !vertical && f.sign > 0 && f.surface != "Wall" && f.surface != "Trim" && f.surface != "Ceiling";
                bool ceiling = !vertical && f.sign < 0 && f.surface == "Ceiling";
                float[] dens =
                {
                    vertical ? redDensity : floorish ? redDensity * 0.55f : ceiling ? redDensity * 0.7f : redDensity * 0.35f,
                    vertical ? blackDensity : floorish ? blackDensity * 0.55f : ceiling ? blackDensity * 0.45f : blackDensity * 0.3f,
                    vertical ? bloodDensity : floorish ? bloodDensity * 0.8f : bloodDensity * 0.3f,
                };
                var n = Vector3.zero; n[ax] = f.sign;
                for (int type = 0; type < 3; type++)
                {
                    float expected = area * dens[type];
                    int count = (int)expected + (rng.NextDouble() < expected - (int)expected ? 1 : 0);
                    for (int k = 0; k < count; k++)
                    {
                        float s = type == 0 ? U(0.8f, 1.5f) : type == 1 ? U(0.9f, 1.6f) : U(0.5f, 1.1f);
                        float cx = U(r0, r1), cy = U(u0, u1);
                        // Skip spots buried inside neighbouring geometry.
                        var probe = Vector3.zero; probe[ax] = f.plane + f.sign * 0.03f; probe[right] = cx; probe[up] = cy;
                        if (Physics.CheckSphere(probe, 0.012f, ~0, QueryTriggerInteraction.Ignore)) continue;
                        float x0 = Mathf.Max(cx - s / 2, r0), x1 = Mathf.Min(cx + s / 2, r1);
                        float y0 = Mathf.Max(cy - s / 2, u0), y1 = Mathf.Min(cy + s / 2, u1);
                        if (x1 - x0 < 0.02f || y1 - y0 < 0.02f) continue;
                        int cell = type == 0 ? rng.Next(6)
                                 : type == 1 ? (vertical ? 6 + rng.Next(3) : 9 + rng.Next(2))
                                 : (vertical ? 11 + rng.Next(3) : 14 + rng.Next(2));
                        bool dripCell = (cell >= 6 && cell <= 8) || (cell >= 11 && cell <= 13);
                        int orient = dripCell && vertical ? rng.Next(2) * 4 : rng.Next(8);   // drips only ever flip, so they run down
                        float off = type == 2 ? 0.0022f + (placed % 3) * 0.0002f : 0.003f + (placed % 7) * 0.0007f;
                        int b = V.Count;
                        float[] xs = { x0, x1, x1, x0 }, ys = { y0, y0, y1, y1 };
                        for (int q = 0; q < 4; q++)
                        {
                            var p = Vector3.zero; p[ax] = f.plane + f.sign * off; p[right] = xs[q]; p[up] = ys[q];
                            float a = (xs[q] - (cx - s / 2)) / s, bb = (ys[q] - (cy - s / 2)) / s;
                            if ((orient & 4) != 0) a = 1f - a;
                            float ta = a, tb = bb;
                            switch (orient & 3)
                            {
                                case 1: ta = bb; tb = 1f - a; break;
                                case 2: ta = 1f - a; tb = 1f - bb; break;
                                case 3: ta = 1f - bb; tb = a; break;
                            }
                            int ccx = cell % Cells, ccy = cell / Cells;
                            UV.Add(new Vector2((ccx * C + Margin + ta * (C - 2 * Margin)) / N, (ccy * C + Margin + tb * (C - 2 * Margin)) / N));
                            V.Add(p);
                            Nn.Add(n);
                        }
                        bool front = Vector3.Dot(Vector3.Cross(V[b + 1] - V[b], V[b + 2] - V[b]), n) > 0;
                        if (front) { T.Add(b); T.Add(b + 1); T.Add(b + 2); T.Add(b); T.Add(b + 2); T.Add(b + 3); }
                        else { T.Add(b); T.Add(b + 2); T.Add(b + 1); T.Add(b); T.Add(b + 3); T.Add(b + 2); }
                        placed++;
                    }
                }
                yield return null;
            }
            if (V.Count == 0) continue;
            // One mesh per source box keeps URP's per-object light selection close to the wall it sits on.
            var go = new GameObject("Decals_" + kv.Key.name);
            go.transform.SetParent(_root.transform, false);
            var mesh = new Mesh { name = go.name };
            mesh.SetVertices(V); mesh.SetNormals(Nn); mesh.SetUVs(0, UV); mesh.SetTriangles(T, 0);
            mesh.RecalculateBounds();
            _owned.Add(mesh);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = _mat;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = true;
            yield return null;
        }
        DecalCount = placed;
        _faces = null;
    }

    static Material CreateFallbackMaterial()
    {
        var m = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        m.SetColor("_BaseColor", Color.white);
        m.SetFloat("_AlphaClip", 1f); m.SetFloat("_Cutoff", 0.5f); m.EnableKeyword("_ALPHATEST_ON");
        m.renderQueue = (int)RenderQueue.AlphaTest;
        m.EnableKeyword("_METALLICSPECGLOSSMAP");
        m.SetFloat("_SmoothnessTextureChannel", 0f); m.SetFloat("_Smoothness", 1f); m.SetFloat("_Metallic", 0f);
        m.SetFloat("_EnvironmentReflections", 0f); m.EnableKeyword("_ENVIRONMENTREFLECTIONS_OFF");
        m.EnableKeyword("_EMISSION");
        m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
        return m;
    }
}
