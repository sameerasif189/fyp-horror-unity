using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using Unity.InferenceEngine;

/// <summary>
/// Realtime stress-driven corruption of the haunted-house surfaces.
///
/// Milestone M4 reworked this to satisfy three constraints:
///  - Shared material ASSETS are never written at runtime. The builder creates house materials
///    with AssetDatabase.CreateAsset, so writing _BaseMap on them used to persist corruption into
///    the project after exiting play mode. Everything now goes through per-renderer
///    MaterialPropertyBlocks, which are transient and cannot dirty an asset.
///  - Renderers are resolved once into <see cref="_surfaceRenderers"/> instead of a
///    FindObjectsOfType sweep on every regeneration. Call <see cref="RefreshRendererCache"/>
///    after spawning or destroying house geometry.
///  - Texture memory is bounded. Each surface owns exactly one runtime Texture2D, reused in
///    place via SetPixels; clean albedos are cached once as Color32[] and painting happens in a
///    single shared Color[] scratch buffer, so a regeneration allocates nothing.
///
/// In play mode the house boxes also get world-space UVs and coplanar-overlap insets
/// (<see cref="HouseSurfaceGeometry"/>), stress 5 swaps the procedural painters for the approved
/// dread look (<see cref="HouseDreadLook"/>: graded base, crack/blood decals), and <see cref="HouseLighting"/>
/// lights the house per stress level (normal 0-3, dimmer 4, red and flickering 5).
/// </summary>
public class TextureCorruptionRunner : MonoBehaviour
{
    const int ImgSize = 256;
    const string BaseMapProp = "_BaseMap";

    [Serializable]
    public class SurfaceTarget
    {
        public string name;
        public Material material;
        public Texture2D cleanAlbedo;

        [NonSerialized] public Texture2D runtimeTex;
        [NonSerialized] public Color32[] cleanPixels;
        [NonSerialized] public Vector2 origScale;
        [NonSerialized] public Vector2 origOffset;
    }

    [SerializeField] ModelAsset textureModelAsset;
    [SerializeField] BackendType backend = BackendType.GPUCompute;
    [SerializeField] SurfaceTarget[] surfaces;

    [Tooltip("Use the iteration-2 texture_generator.onnx output as the base layer under the " +
             "procedural corruption pass. Off by default: the procedural painters are the " +
             "reference look and the model is not needed for it.")]
    [SerializeField] bool useOnnxBaseLayer;

    [Tooltip("Also regenerate on a timer, not just when stress changes. Off by default: a full " +
             "repaint of every surface runs five painters per texture and was the main source of " +
             "in-game stutter. Deliberately renamed from 'realtimeRegenDuringPlay' so the old " +
             "serialized 'true' in SampleScene cannot silently re-enable it.")]
    [SerializeField] bool continuousRegenDuringPlay;

    [Tooltip("Only used when continuousRegenDuringPlay is on.")]
    [SerializeField] float regenIntervalSeconds = 3.2f;

    [Tooltip("Each regen also randomizes UV scale/offset per renderer so mapping is never identical.")]
    [SerializeField] bool randomizeMappingEachRegen = true;

    [Tooltip("Ceiling on live runtime texture memory. Surfaces past the budget keep their clean " +
             "albedo rather than allocating.")]
    [SerializeField] int maxRuntimeTextureBytes = 16 * 1024 * 1024;

    [Tooltip("Stress 5 replaces the procedural corruption with the approved 'dread' look: dark graded " +
             "base, crack/blood decals and red house lights (HouseDreadLook). Play mode only.")]
    [SerializeField] bool stressFiveDread = true;

    [Tooltip("Rebuild the house boxes with world-space UVs and push coplanar overlapping faces apart " +
             "(HouseSurfaceGeometry) - fixes stretched textures and z-fighting. Play mode only; colliders " +
             "and the scene file are untouched.")]
    [SerializeField] bool worldSpaceUVs = true;

    [Tooltip("Texture generation (iteration 2's pipeline, SurfaceTextureGenerator): every stress change gives each " +
             "surface a NEW texture composed from the per-level source library (Resources/TextureDeck) and " +
             "corrupted by the texture_generator U-Net (textureModelAsset). Pre-generated in the background. " +
             "Procedural corruption paints on top at 1-4, level 0 shows it as generated, level 5 grades it. Play mode only.")]
    [SerializeField] bool juggleTextures = true;

    [Tooltip("Stress lighting (HouseLighting): brighter builder lights plus a fill light in every unlit room, " +
             "normal at 0-3, dimmer at 4, red and flickering at 5; a dark interior reflection replaces the sky's. " +
             "Play mode only.")]
    [SerializeField] bool stressLighting = true;

    [Tooltip("On-screen panel (TextureGenerationHud, F2 toggles) showing which stress levels have freshly generated " +
             "textures waiting and how long until the rest are ready. Play mode only, with texture generation on.")]
    [SerializeField] bool generationHud = true;

    Worker _worker;
    Tensor<float> _inputTexture;
    Texture2D _onnxReadback;
    bool _modelReady;

    int _lastAppliedLevel = -1;
    float _lastCorruption = -1f;
    FusionDirector.FusionParams _fusion;
    float _nextRegenTime;
    int _regenSeed;

    MaterialPropertyBlock _mpb;
    CorruptionVariantSelector _variants;
    Color[] _scratch;
    List<Renderer>[] _surfaceRenderers;
    List<Renderer> _foreignRenderers;
    int _runtimeTextureBytes;
    HouseDreadLook _dread;
    HouseLighting _lighting;
    readonly List<Mesh> _geometryMeshes = new List<Mesh>();
    SurfaceTextureDeck _deck;
    SurfaceTextureGenerator _gen;
    SurfaceTextureGenerator.Result[] _generated;
    Texture2D[] _rawTex;
    bool[] _normalCapable;
    bool _rejuggle = true;
    int _juggledLevel = -1;
    int _stressChangeFrame = -1;
    Color[][] _paintBuffers;
    int _paintGeneration;
    (Task task, int generation, int level, float corruption, bool[] active, string summary) _paint;
    static Texture2D _flatNormal;

    enum PushMode { Painted, Dread, Raw }

    /// <summary>The background texture generator (play mode with generation on), else null.</summary>
    public SurfaceTextureGenerator Generator => _gen;

    MaterialPropertyBlock Mpb => _mpb ??= new MaterialPropertyBlock();
    CorruptionVariantSelector Variants => _variants ??= new CorruptionVariantSelector();

    void OnEnable()
    {
        TrySubscribe();
        InitSurfaces();
        TryInitModel();
        ForceApply(StressController.Instance != null ? StressController.Instance.StressLevel : 0);
        EnvironmentTextureLock.EnsureOnExterior();
    }

    void Start()
    {
        TrySubscribe();
        if (FusionDirector.Instance != null)
        {
            FusionDirector.Instance.OnParamsChanged -= OnFusion;
            FusionDirector.Instance.OnParamsChanged += OnFusion;
            if (FusionDirector.Instance.Latest != null)
                OnFusion(FusionDirector.Instance.Latest);
        }
        ForceApply(StressController.Instance != null ? StressController.Instance.StressLevel : 0);
        _nextRegenTime = Time.time + Mathf.Max(2f, regenIntervalSeconds);
        // Monsters' stress-5 paint, ready before any of them spawns (M4).
        if (Application.isPlaying) MonsterCorruptionController.PrewarmAll();
    }

    void Update()
    {
        MonsterCorruptionController.CollectPrewarmed();
        _dread?.Tick();
        _lighting?.Tick();
        _gen?.Tick();
        if (_paint.task != null && _paint.task.IsCompleted) FinishRepaint();
        if (!continuousRegenDuringPlay || surfaces == null || surfaces.Length == 0) return;
        if (GameStartMenu.Instance != null && !GameStartMenu.GameplayActive) return;
        if (Time.time < _nextRegenTime) return;

        int level = StressController.Instance != null ? StressController.Instance.StressLevel : 0;
        ForceApply(level);
        float interval = Mathf.Lerp(regenIntervalSeconds * 1.2f, regenIntervalSeconds * 0.5f, level / 5f);
        _nextRegenTime = Time.time + Mathf.Max(1.2f, interval);
    }

    /// <summary>Replace surface targets (used when rebuilding the haunted house).</summary>
    public void BindSurfaces(SurfaceTarget[] newSurfaces)
    {
        ReleaseSurfaces();
        surfaces = newSurfaces ?? Array.Empty<SurfaceTarget>();
        Variants.Reset();
        InitSurfaces();
        _lastAppliedLevel = -1;
        ForceApply(StressController.Instance != null ? StressController.Instance.StressLevel : 0);
        Debug.Log($"[TextureCorruption] bound {surfaces.Length} surfaces for realtime generation.");
    }

    /// <summary>
    /// Rebuild the cached renderer lists. Call after house geometry is created or destroyed -
    /// nothing else in the regeneration path touches the scene graph.
    /// </summary>
    public void RefreshRendererCache()
    {
        int surfaceCount = surfaces?.Length ?? 0;
        _surfaceRenderers = new List<Renderer>[surfaceCount];
        for (int i = 0; i < surfaceCount; i++)
            _surfaceRenderers[i] = new List<Renderer>();
        _foreignRenderers = new List<Renderer>();

        var all = FindObjectsByType<Renderer>(FindObjectsInactive.Include);
        foreach (var r in all)
        {
            if (r == null) continue;
            if (!IsHouseStructureRenderer(r))
            {
                // Anything outside the house keeps a clean block so it can never inherit
                // house UV jitter or corruption through a shared material.
                _foreignRenderers.Add(r);
                continue;
            }
            for (int si = 0; si < surfaceCount; si++)
            {
                var s = surfaces[si];
                if (s?.material == null || r.sharedMaterial != s.material) continue;
                _surfaceRenderers[si].Add(r);
                break;
            }
        }

        int bound = 0;
        for (int i = 0; i < surfaceCount; i++) bound += _surfaceRenderers[i].Count;
        Debug.Log($"[TextureCorruption] renderer cache: {bound} house renderers across " +
                  $"{surfaceCount} surfaces, {_foreignRenderers.Count} frozen non-house renderers.");
    }

    void OnDisable()
    {
        if (StressController.Instance != null)
            StressController.Instance.OnStressChanged -= OnStressChanged;
        if (FusionDirector.Instance != null)
            FusionDirector.Instance.OnParamsChanged -= OnFusion;
        _dread?.SetActive(false);
        _lighting?.Dispose();   // restores the builder's lights; OnEnable -> InitSurfaces rebuilds it
        _lighting = null;
        ClearRendererBlocks();
        DisposeModel();
    }

    void OnDestroy()
    {
        DisposeModel();
        ReleaseSurfaces();
        foreach (var m in _geometryMeshes) HorrorTextureCorruptor.SafeDestroy(m);
        _geometryMeshes.Clear();
    }

    /// <summary>Drop every runtime texture and cached buffer. Safe in edit mode.</summary>
    void ReleaseSurfaces()
    {
        if (surfaces != null)
        {
            foreach (var s in surfaces)
            {
                if (s == null) continue;
                if (s.runtimeTex != null) HorrorTextureCorruptor.SafeDestroy(s.runtimeTex);
                s.runtimeTex = null;
                s.cleanPixels = null;
            }
        }
        _dread?.Dispose();
        _dread = null;
        _lighting?.Dispose();
        _lighting = null;
        _gen?.Dispose();
        _gen = null;
        if (_rawTex != null)
            foreach (var t in _rawTex) if (t != null) HorrorTextureCorruptor.SafeDestroy(t);
        _rawTex = null;
        _runtimeTextureBytes = 0;
        _scratch = null;
        _surfaceRenderers = null;
        _foreignRenderers = null;
    }

    void TrySubscribe()
    {
        if (StressController.Instance == null) return;
        StressController.Instance.OnStressChanged -= OnStressChanged;
        StressController.Instance.OnStressChanged += OnStressChanged;
    }

    void OnFusion(FusionDirector.FusionParams p)
    {
        _fusion = p;
        // FusionDirector re-generates on every stress change, right after our own OnStressChanged repaint.
        // Repainting again in the same frame doubled the texture cost of every stress change for no visible gain.
        if (Time.frameCount == _stressChangeFrame)
        {
            _lastCorruption = p.visual_corruption;
            return;
        }
        // Regen when fusion corruption changes meaningfully
        if (Mathf.Abs(p.visual_corruption - _lastCorruption) > 0.08f)
            ForceApply(p.stressLevel);
    }

    void InitSurfaces()
    {
        if (surfaces == null) return;
        foreach (var s in surfaces)
        {
            if (s?.material == null) continue;
            s.origScale = s.material.GetTextureScale(BaseMapProp);
            s.origOffset = s.material.GetTextureOffset(BaseMapProp);
            if (s.origScale.sqrMagnitude < 0.01f)
                s.origScale = Vector2.one;

            // Repair: earlier builds wrote corrupted maps straight onto the material asset.
            // Writing the clean albedo back is idempotent and leaves the asset in its authored
            // state; from here on corruption only ever travels through property blocks.
            if (s.cleanAlbedo != null && s.material.HasProperty(BaseMapProp)
                && s.material.GetTexture(BaseMapProp) != s.cleanAlbedo)
            {
                s.material.SetTexture(BaseMapProp, s.cleanAlbedo);
            }

            if (s.cleanAlbedo != null)
            {
                s.cleanPixels ??= new Color32[ImgSize * ImgSize];
                HorrorTextureCorruptor.ReadClean(s.cleanAlbedo, ImgSize, s.cleanPixels);
            }
        }
        RefreshRendererCache();
        BuildPlayModeLook();

        _gen?.Dispose();
        _gen = null;
        _deck = Application.isPlaying && juggleTextures ? SurfaceTextureDeck.Load(UnityEngine.Random.Range(1, int.MaxValue)) : null;
        if (_deck != null)
        {
            // Levels 0 and 5 show the generated texture as-is (5 after the dread grade), so those arrive pre-uploaded;
            // levels 1-4 are painted over and only need the pixels.
            bool dreadOn = _dread != null;
            _gen = new SurfaceTextureGenerator(_deck, textureModelAsset, UnityEngine.Random.Range(1, int.MaxValue),
                l => l == 0 || (l == 5 && dreadOn),
                l => l == 5 && dreadOn ? HouseDreadLook.GradePixels : (Func<Color32[], Color32[]>)null);
            // Pre-generate every slot in the background, nearest stress level first.
            int cur = StressController.Instance != null ? StressController.Instance.StressLevel : 0;
            for (int d = 0; d <= 5; d++)
                foreach (int lvl in new[] { cur - d, cur + d })
                {
                    if (lvl < 0 || lvl > 5) continue;   // Request() ignores the duplicate at d == 0
                    foreach (var s in surfaces) if (s?.material != null) _gen.Request(s.name, lvl);
                }
            if (!_gen.ModelAvailable)
                Debug.LogWarning("[TextureCorruption] textureModelAsset missing: textures are composed but not U-Net corrupted.");
            if (generationHud && GetComponent<TextureGenerationHud>() == null) gameObject.AddComponent<TextureGenerationHud>();
        }
        _generated = new SurfaceTextureGenerator.Result[surfaces.Length];
        if (_rawTex == null || _rawTex.Length != surfaces.Length) _rawTex = new Texture2D[surfaces.Length];
        _normalCapable = new bool[surfaces.Length];
        for (int i = 0; i < surfaces.Length; i++)
        {
            var m = surfaces[i]?.material;
            // Only surfaces already normal-mapped get the deck's normals; keywords are never flipped on assets.
            _normalCapable[i] = m != null && m.HasProperty("_BumpMap") && m.IsKeywordEnabled("_NORMALMAP");
        }
        _rejuggle = true;
        _juggledLevel = -1;
        if (Application.isPlaying && juggleTextures && _deck == null)
            Debug.LogWarning("[TextureCorruption] juggleTextures is on but Resources/TextureDeck/deck.txt is missing.");
    }

    /// <summary>
    /// Give every surface a newly generated base for <paramref name="level"/> (pre-generated when ready, else
    /// generated now; a fresh one is queued either way). Painters read it from cleanPixels, level 0 shows it as
    /// generated, stress 5 re-grades it. Surfaces without a library slot fall back to their authored albedo.
    /// </summary>
    void JuggleBases(int level)
    {
        _rejuggle = false;
        _juggledLevel = level;
        int readyBefore = _gen.ReadyCount, reusedCount = 0;
        var summary = new System.Text.StringBuilder();
        var dreadPixels = level >= 5 && _dread != null ? new Color32[surfaces.Length][] : null;
        for (int si = 0; si < surfaces.Length; si++)
        {
            var s = surfaces[si];
            if (s?.material == null) continue;
            bool reused = false;
            var res = _deck.Has(s.name, level) ? _gen.Take(s.name, level, out reused) : null;
            _generated[si] = res;
            if (reused) reusedCount++;
            if (res != null)
            {
                summary.Append("\n  ").Append(s.name).Append(reused ? " (previous, new one not ready): " : ": ").Append(res.label);
                if (level < 5 && s.cleanPixels != null) Downsample(res.pixels, SurfaceTextureGenerator.Size, s.cleanPixels, ImgSize);
                if (level <= 0 && res.texture == null)   // normally pre-uploaded by the generator
                {
                    var raw = _rawTex[si];
                    if (raw == null)
                        _rawTex[si] = raw = new Texture2D(SurfaceTextureGenerator.Size, SurfaceTextureGenerator.Size, TextureFormat.RGBA32, true, false)
                        {
                            name = s.name + "_generated", wrapMode = TextureWrapMode.Repeat, anisoLevel = 4,
                        };
                    raw.SetPixels32(res.pixels);
                    raw.Apply(true);
                }
                if (level >= 5 && dreadPixels != null)
                {
                    if (res.texture != null) _dread.SetBaseOverride(si, res.texture);   // pre-graded and uploaded
                    else dreadPixels[si] = res.pixels;
                }
            }
            else if (s.cleanAlbedo != null)
            {
                // Only in the first seconds of play, before this slot was ever generated.
                if (s.cleanPixels != null) HorrorTextureCorruptor.ReadClean(s.cleanAlbedo, ImgSize, s.cleanPixels);
                if (level >= 5) _dread?.SetSource(si, s.cleanAlbedo);
            }
        }
        if (dreadPixels != null && Array.Exists(dreadPixels, p => p != null))
            _dread.SetSources(dreadPixels);   // results that pre-date the pre-grade path: graded in parallel, uploaded here
        // The textures for this level are on screen now; the next ones needed are one level either side.
        _gen.PrioritiseLevel(level + 1);
        _gen.PrioritiseLevel(level - 1);
        Debug.Log($"[TextureCorruption] generated L{level} ({readyBefore} slots pre-generated, {reusedCount} reused):{summary}");
    }

    /// <summary>Box-filter a square Color32 image down to <paramref name="dstSize"/>.</summary>
    static void Downsample(Color32[] src, int srcSize, Color32[] dst, int dstSize)
    {
        int k = srcSize / dstSize;
        if (k <= 1) { Array.Copy(src, dst, Mathf.Min(src.Length, dst.Length)); return; }
        int area = k * k;
        for (int y = 0; y < dstSize; y++)
        for (int x = 0; x < dstSize; x++)
        {
            int r = 0, g = 0, b = 0;
            for (int dy = 0; dy < k; dy++)
            for (int dx = 0; dx < k; dx++)
            {
                var c = src[(y * k + dy) * srcSize + x * k + dx];
                r += c.r; g += c.g; b += c.b;
            }
            dst[y * dstSize + x] = new Color32((byte)(r / area), (byte)(g / area), (byte)(b / area), 255);
        }
    }

    static Texture2D FlatNormal
    {
        get
        {
            if (_flatNormal != null) return _flatNormal;
            // (0.5, 0.5, 1, 1) decodes flat under both RGB and DXT5nm-style unpacking.
            _flatNormal = new Texture2D(4, 4, TextureFormat.RGBA32, false, true) { name = "FlatNormal" };
            var px = new Color32[16];
            for (int i = 0; i < px.Length; i++) px[i] = new Color32(128, 128, 255, 255);
            _flatNormal.SetPixels32(px);
            _flatNormal.Apply(false, true);
            return _flatNormal;
        }
    }

    /// <summary>
    /// Play mode only (BindSurfaces also runs from the editor builder, and these meshes and textures
    /// must never be saved into the scene): world-space UVs + overlap fix, then the stress-5 look.
    /// </summary>
    void BuildPlayModeLook()
    {
        _dread?.Dispose();
        _dread = null;
        _lighting?.Dispose();
        _lighting = null;
        if (!Application.isPlaying || surfaces == null || _surfaceRenderers == null) return;

        var house = GameObject.Find("HauntedHouse");
        if (stressLighting && house != null)
        {
            _lighting = new HouseLighting(house.transform, UnityEngine.Random.Range(1, int.MaxValue));
            Debug.Log($"[TextureCorruption] house lighting: {_lighting.AddedCount} fill lights added");
        }
        if (!worldSpaceUVs && !stressFiveDread) return;

        var names = new string[surfaces.Length];
        for (int i = 0; i < surfaces.Length; i++) names[i] = surfaces[i]?.name;
        var faces = HouseSurfaceGeometry.Apply(names, _surfaceRenderers, _geometryMeshes, worldSpaceUVs);
        if (!stressFiveDread) return;

        _dread = new HouseDreadLook(surfaces, _surfaceRenderers, faces,
                                    Resources.Load<Material>("Dread/DreadDecals"), UnityEngine.Random.Range(1, int.MaxValue));
    }

    void TryInitModel()
    {
        _modelReady = false;
        if (!useOnnxBaseLayer || textureModelAsset == null) return;
        try
        {
            var model = ModelLoader.Load(textureModelAsset);
            _worker = new Worker(model, backend);
            _inputTexture = new Tensor<float>(new TensorShape(1, 3, ImgSize, ImgSize));
            _modelReady = true;
            Debug.Log("[TextureCorruption] texture_generator ready (ONNX base layer on).");
        }
        catch (Exception e)
        {
            Debug.LogWarning("[TextureCorruption] Model init failed: " + e.Message);
            DisposeModel();
        }
    }

    void DisposeModel()
    {
        _worker?.Dispose();
        _worker = null;
        _inputTexture?.Dispose();
        _inputTexture = null;
        if (_onnxReadback != null) HorrorTextureCorruptor.SafeDestroy(_onnxReadback);
        _onnxReadback = null;
        _modelReady = false;
    }

    void OnStressChanged(int level)
    {
        _rejuggle = true;   // only a real stress change swaps the base; fusion re-applies keep it
        _stressChangeFrame = Time.frameCount;
        ForceApply(level);
    }

    void ForceApply(int level)
    {
        _regenSeed++;
        _lastAppliedLevel = -1;
        ApplyLevel(level);
    }

    public void ApplyLevel(int level)
    {
        level = Mathf.Clamp(level, 0, 5);
        _lighting?.SetLevel(level);   // stress level only, not fusion corruption; repeats are no-ops
        float corruption = _fusion != null ? _fusion.visual_corruption : level / 5f;

        if (level == _lastAppliedLevel && Mathf.Abs(corruption - _lastCorruption) < 0.05f)
            return;
        _lastAppliedLevel = level;
        _lastCorruption = corruption;

        if (_deck != null && (_rejuggle || _juggledLevel != level))
            JuggleBases(level);

        _paintGeneration++;   // any repaint still running is for a level we have left

        // Stress 5 only (not fusion-driven corruption): the dread look replaces the procedural painters.
        bool dread = level >= 5 && _dread != null;
        _dread?.SetActive(dread);
        if (dread)
        {
            PushToRenderers(level, PushMode.Dread);
            Debug.Log($"[TextureCorruption] level={level} dread look on");
            return;
        }

        if (level <= 0 && corruption < 0.12f)
        {
            // Calm: the juggled base shown clean, or the authored materials when juggling is off.
            if (_deck != null) PushToRenderers(0, PushMode.Raw);
            else ClearRendererBlocks();
            Debug.Log($"[TextureCorruption] level={level} corruption=off corr={corruption:F2}");
            return;
        }

        int procLevel = Mathf.Clamp(Mathf.RoundToInt(Mathf.Max(level, corruption * 5f)), 1, 5);
        Regenerate(procLevel, corruption);   // paints on worker threads; FinishRepaint pushes the result
    }

    /// <summary>
    /// Start repainting every surface's runtime texture for <paramref name="level"/>: recipes and base copies are
    /// prepared here, the painters run in parallel on worker threads, and <see cref="FinishRepaint"/> uploads and
    /// pushes the result. On the main thread the nine painters took 0.9-1.3 s per stress change - the freeze
    /// that made levels 1-4 load so much slower than 5 (which skips them).
    /// </summary>
    void Regenerate(int level, float corruption)
    {
        if (surfaces == null || surfaces.Length == 0) return;
        _scratch ??= new Color[ImgSize * ImgSize];
        if (_paintBuffers == null || _paintBuffers.Length != surfaces.Length) _paintBuffers = new Color[surfaces.Length][];
        float grimeOverlay = _fusion != null ? _fusion.visual_grime_overlay : 0f;

        var recipes = new CorruptionRecipe[surfaces.Length];
        var active = new bool[surfaces.Length];
        var summary = new System.Text.StringBuilder();
        for (int si = 0; si < surfaces.Length; si++)
        {
            var s = surfaces[si];
            if (s?.material == null || s.cleanAlbedo == null || s.cleanPixels == null) continue;
            if (!TryEnsureRuntimeTex(s)) continue;

            int seed = _regenSeed * 97 + si * 131 + level * 17;
            recipes[si] = Variants.Next(si, level, corruption, grimeOverlay, seed);
            var buf = _paintBuffers[si] ??= new Color[ImgSize * ImgSize];

            // Base layer: ONNX output when enabled, otherwise the clean albedo, wrap-shifted so
            // successive generations are never pixel-aligned.
            var rng = new System.Random(seed);
            if (TryFillFromModel(s, level, seed)) Array.Copy(_scratch, buf, buf.Length);
            else HorrorTextureCorruptor.CopyShifted(s.cleanPixels, buf, ImgSize, rng.Next(ImgSize / 8), rng.Next(ImgSize / 8));
            active[si] = true;

            if (summary.Length > 0) summary.Append(' ');
            summary.Append(s.name).Append(':').Append(recipes[si].dominant);
        }

        int generation = _paintGeneration;
        var buffers = _paintBuffers;
        _paint = (Task.Run(() => Parallel.For(0, buffers.Length, si =>
        {
            if (active[si]) HorrorTextureCorruptor.Paint(buffers[si], ImgSize, recipes[si]);
        })), generation, level, corruption, active, summary.ToString());
    }

    /// <summary>Main thread: upload and push a finished repaint, unless the level has changed since.</summary>
    void FinishRepaint()
    {
        var (task, generation, level, corruption, active, summary) = _paint;
        _paint = default;
        if (task.IsFaulted)
        {
            Debug.LogWarning("[TextureCorruption] repaint failed: " + task.Exception?.GetBaseException().Message);
            return;
        }
        if (generation != _paintGeneration) return;
        for (int si = 0; si < surfaces.Length; si++)
        {
            if (!active[si] || surfaces[si]?.runtimeTex == null) continue;
            surfaces[si].runtimeTex.SetPixels(_paintBuffers[si]);
            surfaces[si].runtimeTex.Apply(true);
        }
        PushToRenderers(level, PushMode.Painted);
        Debug.Log($"[TextureCorruption] level={level} corr={corruption:F2} seed={_regenSeed} " +
                  $"mem={_runtimeTextureBytes / 1024}KB variants=[{summary}]");
    }

    /// <summary>
    /// Allocate this surface's single runtime texture if it does not have one yet, respecting
    /// <see cref="maxRuntimeTextureBytes"/>. Returns false when the surface must stay clean.
    /// </summary>
    bool TryEnsureRuntimeTex(SurfaceTarget s)
    {
        if (s.runtimeTex != null && s.runtimeTex.width == ImgSize && s.runtimeTex.height == ImgSize)
            return true;

        if (s.runtimeTex != null)
        {
            _runtimeTextureBytes -= TextureBytes();
            HorrorTextureCorruptor.SafeDestroy(s.runtimeTex);
            s.runtimeTex = null;
        }

        int cost = TextureBytes();
        if (_runtimeTextureBytes + cost > maxRuntimeTextureBytes)
        {
            Debug.LogWarning($"[TextureCorruption] runtime texture budget reached " +
                             $"({_runtimeTextureBytes / 1024}KB); '{s.name}' stays clean.");
            return false;
        }

        s.runtimeTex = new Texture2D(ImgSize, ImgSize, TextureFormat.RGBA32, true, false)
        {
            wrapMode = TextureWrapMode.Repeat,
            filterMode = FilterMode.Bilinear,
            name = s.name + "_corrupted"
        };
        _runtimeTextureBytes += cost;
        return true;
    }

    // RGBA32 plus the usual ~33% mip chain overhead.
    static int TextureBytes() => Mathf.CeilToInt(ImgSize * ImgSize * 4 * 1.34f);

    /// <summary>Fill the scratch buffer from the ONNX generator. False when unavailable.</summary>
    bool TryFillFromModel(SurfaceTarget s, int level, int seed)
    {
        // The generator already ran the U-Net on a composed base; this legacy path would overwrite it.
        if (_gen != null) return false;
        if (!_modelReady || _worker == null || _inputTexture == null) return false;
        try
        {
            using var stress = new Tensor<int>(new TensorShape(1), new[] { level });
            var remapped = HorrorTextureCorruptor.RemapSource(s.cleanAlbedo, ImgSize, seed);
            TextureConverter.ToTensor(remapped, _inputTexture);
            HorrorTextureCorruptor.SafeDestroy(remapped);

            _worker.Schedule(_inputTexture, stress);
            var output = _worker.PeekOutput("corrupted_texture") as Tensor<float>
                         ?? _worker.PeekOutput() as Tensor<float>;
            if (output == null) return false;

            var rt = RenderTexture.GetTemporary(ImgSize, ImgSize, 0, RenderTextureFormat.ARGB32);
            TextureConverter.RenderToTexture(output, rt);
            if (_onnxReadback == null)
                _onnxReadback = new Texture2D(ImgSize, ImgSize, TextureFormat.RGBA32, false, false);
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            _onnxReadback.ReadPixels(new Rect(0, 0, ImgSize, ImgSize), 0, 0);
            _onnxReadback.Apply(false);
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);

            var px = _onnxReadback.GetPixels();
            Array.Copy(px, _scratch, Mathf.Min(px.Length, _scratch.Length));
            return true;
        }
        catch (Exception e)
        {
            Debug.LogWarning("[TextureCorruption] ONNX base layer failed, using clean albedo: " + e.Message);
            _modelReady = false;
            return false;
        }
    }

    /// <summary>
    /// Push textures and UV jitter to the cached house renderers via property blocks: the painted runtime
    /// textures, the stress-5 graded bases (<see cref="PushMode.Dread"/>) or the juggled base shown clean
    /// (<see cref="PushMode.Raw"/>). With juggling on, normal-mapped surfaces also get the base's own normal
    /// map (flat when it has none). Nothing here writes to a material asset.
    /// </summary>
    void PushToRenderers(int level, PushMode mode)
    {
        if (surfaces == null) return;
        if (_surfaceRenderers == null || _surfaceRenderers.Length != surfaces.Length)
            RefreshRendererCache();
        if (_surfaceRenderers == null) return;

        for (int si = 0; si < surfaces.Length; si++)
        {
            var s = surfaces[si];
            if (s?.material == null) continue;
            var gen = _generated != null && si < _generated.Length ? _generated[si] : null;
            Texture tex = mode == PushMode.Dread ? _dread.BaseFor(si)
                        : mode == PushMode.Raw ? (gen?.texture != null ? gen.texture : gen != null && _rawTex[si] != null ? _rawTex[si] : (Texture)s.cleanAlbedo)
                        : s.runtimeTex;
            if (tex == null) continue;
            Texture normal = null;
            if (_deck != null && _normalCapable != null && _normalCapable[si])
                normal = gen != null ? (gen.normal != null ? gen.normal : FlatNormal) : s.material.GetTexture("_BumpMap");

            Vector2 baseScale = s.origScale.sqrMagnitude > 0.01f ? s.origScale : Vector2.one;
            float jitter = randomizeMappingEachRegen ? 0.03f + level * 0.015f : 0f;

            var list = _surfaceRenderers[si];
            for (int ri = 0; ri < list.Count; ri++)
            {
                var r = list[ri];
                if (r == null) continue;

                float sx = baseScale.x, sy = baseScale.y;
                Vector2 offset = s.origOffset;
                if (jitter > 0f)
                {
                    sx *= UnityEngine.Random.Range(1f - jitter, 1f + jitter);
                    sy *= UnityEngine.Random.Range(1f - jitter, 1f + jitter);
                    offset = new Vector2(UnityEngine.Random.Range(0f, 0.08f),
                                         UnityEngine.Random.Range(0f, 0.08f));
                }
                var st = new Vector4(sx, sy, offset.x, offset.y);

                Mpb.Clear();
                r.GetPropertyBlock(Mpb);
                Mpb.SetTexture(BaseMapProp, tex);
                Mpb.SetVector("_BaseMap_ST", st);
                if (s.material.HasProperty("_MainTex"))
                {
                    Mpb.SetTexture("_MainTex", tex);
                    Mpb.SetVector("_MainTex_ST", st);
                }
                if (s.material.HasProperty("_BaseColor"))
                    Mpb.SetColor("_BaseColor", Color.white);
                if (normal != null)
                    Mpb.SetTexture("_BumpMap", normal);
                r.SetPropertyBlock(Mpb);
            }
        }

        FreezeForeignRenderers();
    }

    /// <summary>Drop every override so the house renders from its authored materials again.</summary>
    void ClearRendererBlocks()
    {
        if (_surfaceRenderers != null)
        {
            foreach (var list in _surfaceRenderers)
            {
                if (list == null) continue;
                foreach (var r in list)
                    if (r != null) r.SetPropertyBlock(null);
            }
        }
        FreezeForeignRenderers();
    }

    void FreezeForeignRenderers()
    {
        if (_foreignRenderers == null) return;
        for (int i = 0; i < _foreignRenderers.Count; i++)
        {
            var r = _foreignRenderers[i];
            if (r != null) r.SetPropertyBlock(null);
        }
    }

    static bool IsHouseStructureRenderer(Renderer r)
    {
        if (r == null || IsExteriorEnvironment(r)) return false;
        var t = r.transform;
        while (t != null)
        {
            string n = t.name;
            if (n == "HauntedHouse" || n == "Basement" || n == "GroundFloor" || n == "UpperFloor")
                return true;
            t = t.parent;
        }
        return false;
    }

    static bool IsExteriorEnvironment(Renderer r)
    {
        if (r == null) return false;
        var t = r.transform;
        while (t != null)
        {
            string n = t.name;
            if (n == "Yard" || n == "HauntedYard")
                return true;
            t = t.parent;
        }
        string self = r.gameObject.name;
        if (self.StartsWith("Foundation") || self.StartsWith("Path_"))
            return true;
        var mats = r.sharedMaterials;
        if (mats == null) return false;
        for (int i = 0; i < mats.Length; i++)
        {
            var mat = mats[i];
            if (mat == null) continue;
            string n = mat.name;
            if (n.IndexOf("Nature", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("Yughues", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("YFFl", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("YFCM", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }
        return false;
    }
}
