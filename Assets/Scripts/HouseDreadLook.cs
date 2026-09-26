using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Milestone M4 stress-5 look for the house ("you KNOW you're done"; approved live 24 Sep 2026).
/// Owned by <see cref="TextureCorruptionRunner"/>, which switches it on only at stress 5:
///  - every surface swaps to a dark graded copy of its base - the generated level-5 texture when texture
///    generation is on (<see cref="SetSource(int, Color32[])"/>), else the authored albedo - pushed through the
///    runner's property blocks; no cracks in the tiling texture, so nothing repeats;
///  - the base glows faintly with its own texture (<see cref="BaseGlow"/>), via runtime material copies with
///    emission swapped in only while stress 5 lasts. (Before <see cref="HouseLighting"/> the house rendered ~20/255
///    and about a third of that was the skybox reflecting off every surface; the dark red grade lost to that grey
///    sheen, so stress 5 read grey.)
///  - <see cref="WallDreadDecals"/> adds the cracks and blood, with a steady (non-pulsing) red glow. Every visit to
///    stress 5 gets a new set (new shapes and positions): leaving 5 starts the next one from a new seed, built while
///    the player is below 5 (paused while at 5) and swapped in as soon as it is ready.
/// The red house lights (approved 24 Sep: near-pure red) now live in <see cref="HouseLighting"/>, which also
/// flickers them at stress 5.
/// The grade values are statics so they can be tuned live; call <see cref="Regrade"/> after changing them.
/// </summary>
public sealed class HouseDreadLook : IDisposable
{
    const int BaseSize = 512;
    /// <summary>Approved decal glow: (0.40, 0, 0) x 0.30, no heartbeat on the walls.</summary>
    public static readonly Color DecalEmission = new Color(0.12f, 0f, 0f);

    /// <summary>Luma the source is normalised to before grading (approved wallpaper: 0.53).</summary>
    public static float GradeMean = 0.62f;
    /// <summary>Luma standard deviation the source is normalised to (approved wallpaper: 0.07).</summary>
    public static float GradeContrast = 0.14f;
    /// <summary>How much of the source's own colour survives (0 = grey, 1 = full).</summary>
    public static float GradeSaturation = 0.65f;
    /// <summary>The dread grade: r*Red+RedLift, g*Green, b*Blue (approved: 0.55+6, 0.30, 0.28).</summary>
    public static float GradeRed = 0.62f, GradeRedLift = 10f, GradeGreen = 0.27f, GradeBlue = 0.25f;
    /// <summary>
    /// Emission strength of the base's own texture at stress 5; 0 keeps the materials untouched. 0.15 was approved
    /// for the unlit house (25 Sep 2026); once <see cref="HouseLighting"/> lit it, 0.15 flattened every room into one
    /// uniform red and hid the flicker, so it is now a faint 0.05 - enough to keep the texture readable during
    /// flicker outages, while the lights give the shape.
    /// </summary>
    public static float BaseGlow = 0.05f;

    readonly TextureCorruptionRunner.SurfaceTarget[] _surfaces;
    readonly IList<List<Renderer>> _renderers;
    readonly Texture2D[] _base;
    readonly Texture[] _override;
    readonly Color32[][] _source;
    readonly Material[] _glow;
    readonly List<HouseSurfaceGeometry.Face> _faces;
    readonly Material _decalTemplate;
    readonly System.Random _decalSeeds;
    readonly WallDreadDecals.Textures _decalTextures;
    WallDreadDecals _decals;       // the set stress 5 shows
    WallDreadDecals _nextDecals;   // a fresh set being built while the player is below stress 5
    bool _decalsSeen;              // _decals has been on screen, so the next visit needs a new set
    bool _active, _glowSwapped;

    public bool Active => _active;

    public HouseDreadLook(TextureCorruptionRunner.SurfaceTarget[] surfaces, IList<List<Renderer>> renderersPerSurface,
                          List<HouseSurfaceGeometry.Face> faces, Material decalTemplate, int seed)
    {
        _surfaces = surfaces;
        _renderers = renderersPerSurface;
        _base = new Texture2D[surfaces.Length];
        _override = new Texture[surfaces.Length];
        _source = new Color32[surfaces.Length][];
        _glow = new Material[surfaces.Length];
        for (int si = 0; si < surfaces.Length; si++)
        {
            var s = surfaces[si];
            if (s?.cleanAlbedo == null) continue;
            _base[si] = new Texture2D(BaseSize, BaseSize, TextureFormat.RGBA32, true, false)
            {
                name = s.name + "_dreadBase", wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Bilinear,
            };
            SetSource(si, s.cleanAlbedo);
        }

        _faces = faces;
        _decalTemplate = decalTemplate;
        _decalSeeds = new System.Random(seed);
        _decalTextures = new WallDreadDecals.Textures();
        _decals = NewDecals(0);
    }

    /// <param name="threads">Worker threads for the atlas; 0 = all cores (only the first set, built before play).</param>
    WallDreadDecals NewDecals(int threads)
    {
        var d = new WallDreadDecals(_decalTemplate, _decalTextures);
        d.SetEmission(DecalEmission);
        d.Begin(_faces, _decalSeeds.Next(1, int.MaxValue), threads);
        return d;
    }

    /// <summary>Stress-5 base texture for surface <paramref name="si"/>, or null if it has no clean albedo.</summary>
    public Texture BaseFor(int si) =>
        si < 0 || si >= _base.Length ? null : _override[si] != null ? _override[si] : _base[si];

    /// <summary>
    /// Show an already graded texture for surface <paramref name="si"/> (the generator pre-grades and uploads
    /// level-5 results in the background); null goes back to this class's own graded base.
    /// </summary>
    public void SetBaseOverride(int si, Texture graded)
    {
        if (si < 0 || si >= _override.Length) return;
        _override[si] = graded;
        if (_glow[si] != null) _glow[si].SetTexture("_EmissionMap", BaseFor(si));
    }

    /// <summary>Re-grade surface <paramref name="si"/>'s stress-5 base from a texture (the authored albedo).</summary>
    public void SetSource(int si, Texture source)
    {
        if (si < 0 || si >= _base.Length || _base[si] == null || source == null) return;
        var px = _source[si] ??= new Color32[BaseSize * BaseSize];
        HorrorTextureCorruptor.ReadClean(source as Texture2D, BaseSize, px);
        SetBaseOverride(si, null);
        Grade(si);
    }

    /// <summary>
    /// Re-grade several surfaces' stress-5 bases from generated pixels (<see cref="BaseSize"/> square; null entries
    /// are skipped). The grading runs in parallel on worker threads and only the uploads happen here - graded one
    /// by one on the main thread, the nine surfaces made every entry into stress 5 freeze for 0.6-1 s.
    /// </summary>
    public void SetSources(IList<Color32[]> pixelsPerSurface)
    {
        int n = Mathf.Min(pixelsPerSurface.Count, _base.Length);
        var graded = new Color32[n][];
        for (int si = 0; si < n; si++)
        {
            var p = pixelsPerSurface[si];
            if (p == null || _base[si] == null || p.Length != BaseSize * BaseSize) continue;
            var px = _source[si] ??= new Color32[BaseSize * BaseSize];
            Array.Copy(p, px, px.Length);
            SetBaseOverride(si, null);
        }
        System.Threading.Tasks.Parallel.For(0, n, si =>
        {
            if (pixelsPerSurface[si] != null && _base[si] != null && _source[si] != null) graded[si] = GradePixels(_source[si]);
        });
        for (int si = 0; si < n; si++) if (graded[si] != null) Upload(si, graded[si]);
    }

    /// <summary>Re-apply the grade and glow to every surface (after changing the static tunables).</summary>
    public void Regrade()
    {
        for (int si = 0; si < _base.Length; si++) if (_source[si] != null) Grade(si);
        ApplyGlow();
    }

    void Grade(int si) => Upload(si, GradePixels(_source[si]));

    void Upload(int si, Color32[] px)
    {
        _base[si].SetPixels32(px);
        _base[si].Apply(true);
    }

    /// <summary>
    /// Normalise the source's BRIGHTNESS and contrast (not per channel - that blew rust and brick out to saturated
    /// red), keep part of its colour, then apply the dread grade. Plain grading turned grey rock (luma 0.30 / std
    /// 0.046) into a uniform dark wall. Pure maths: safe on worker threads (the generator pre-grades with it).
    /// </summary>
    public static Color32[] GradePixels(Color32[] src)
    {
        var px = new Color32[src.Length];
        double sum = 0, sum2 = 0;
        for (int i = 0; i < src.Length; i++)
        {
            var c = src[i];
            double l = (0.299 * c.r + 0.587 * c.g + 0.114 * c.b) / 255.0;
            sum += l; sum2 += l * l;
        }
        float mean = (float)(sum / src.Length);
        float std = Mathf.Sqrt(Mathf.Max(0f, (float)(sum2 / src.Length) - mean * mean));
        float gain = Mathf.Clamp(GradeContrast / Mathf.Max(std, 0.01f), 0.6f, 2.5f);
        for (int i = 0; i < src.Length; i++)
        {
            var c = src[i];
            float l = (0.299f * c.r + 0.587f * c.g + 0.114f * c.b) / 255f;
            float l2 = Mathf.Clamp((l - mean) * gain + GradeMean, 0.02f, 1f);
            float k = l2 / Mathf.Max(l, 0.02f);
            float r = Mathf.Lerp(l2, c.r / 255f * k, GradeSaturation) * 255f;
            float g = Mathf.Lerp(l2, c.g / 255f * k, GradeSaturation) * 255f;
            float b = Mathf.Lerp(l2, c.b / 255f * k, GradeSaturation) * 255f;
            px[i] = new Color32((byte)Mathf.Clamp(r * GradeRed + GradeRedLift, 0f, 255f), (byte)Mathf.Clamp(g * GradeGreen, 0f, 255f),
                                (byte)Mathf.Clamp(b * GradeBlue, 0f, 255f), 255);
        }
        return px;
    }

    /// <summary>
    /// Main thread, every frame: advances the decal builds, and swaps a finished new set in while stress 5 is off
    /// (never while it is on screen).
    /// </summary>
    public void Tick()
    {
        _decals.Tick();
        // The sets share one texture set, so only one paints at a time: the first set finishes before a second starts.
        if (_decalsSeen && _nextDecals == null && !_active && (_decals.Built || _decals.Failed))
            _nextDecals = NewDecals(Mathf.Max(1, SystemInfo.processorCount / 2));   // half the cores: leave the game its threads
        if (_nextDecals == null || _active) return;   // its build (uploads, placement) waits while stress 5 is on screen
        _nextDecals.Tick();
        if (_nextDecals.Failed)
        {
            _nextDecals.Dispose();   // keep showing the old set; the next exit from stress 5 tries again
            _nextDecals = null;
            _decalsSeen = false;
        }
        else if (_nextDecals.Built)
        {
            _decals.Dispose();
            _decals = _nextDecals;
            _nextDecals = null;
            _decalsSeen = false;
        }
    }

    public void SetActive(bool on)
    {
        if (on == _active) return;
        _active = on;
        _decals.SetVisible(on);
        if (on) _decalsSeen = true;   // Tick starts the next set once the player is below stress 5 again
        ApplyGlow();
    }

    /// <summary>Swap in (or out) the emissive material copies depending on <see cref="Active"/> and <see cref="BaseGlow"/>.</summary>
    void ApplyGlow()
    {
        bool want = _active && BaseGlow > 0f;
        for (int si = 0; si < _surfaces.Length; si++)
        {
            var orig = _surfaces[si]?.material;
            if (orig == null || _base[si] == null) continue;
            if (want && _glow[si] == null)
            {
                _glow[si] = new Material(orig) { name = orig.name + " (dread glow)" };
                _glow[si].EnableKeyword("_EMISSION");
                _glow[si].globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
            }
            if (_glow[si] != null) _glow[si].SetTexture("_EmissionMap", BaseFor(si));
            if (_glow[si] != null) _glow[si].SetColor("_EmissionColor", Color.white * BaseGlow);
            if (want == _glowSwapped || _renderers == null || si >= _renderers.Count || _renderers[si] == null) continue;
            foreach (var r in _renderers[si])
            {
                if (r == null) continue;
                var mats = r.sharedMaterials;
                bool changed = false;
                for (int m = 0; m < mats.Length; m++)
                {
                    if (want && mats[m] == orig) { mats[m] = _glow[si]; changed = true; }
                    else if (!want && mats[m] == _glow[si]) { mats[m] = orig; changed = true; }
                }
                if (changed) r.sharedMaterials = mats;
            }
        }
        _glowSwapped = want;
    }

    public void Dispose()
    {
        _active = false;
        _decals.SetVisible(false);
        ApplyGlow();
        _decals.Dispose();
        _nextDecals?.Dispose();   // waits for a paint in progress: it writes into the textures destroyed below
        _nextDecals = null;
        _decalTextures.Dispose();
        foreach (var t in _base)
            if (t != null) HorrorTextureCorruptor.SafeDestroy(t);
        foreach (var m in _glow)
            if (m != null) HorrorTextureCorruptor.SafeDestroy(m);
    }
}
