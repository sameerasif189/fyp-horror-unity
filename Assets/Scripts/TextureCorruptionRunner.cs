using System;
using UnityEngine;
using Unity.InferenceEngine;

/// <summary>
/// Realtime texture generation: iteration-2 texture_generator.onnx + procedural
/// horror overlays, intensity driven by FusionDirector visual_* params.
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
        [NonSerialized] public Color baseColor;
        [NonSerialized] public Vector2 origScale;
        [NonSerialized] public Vector2 origOffset;
    }

    [SerializeField] ModelAsset textureModelAsset;
    [SerializeField] BackendType backend = BackendType.GPUCompute;
    [SerializeField] SurfaceTarget[] surfaces;
    [SerializeField] bool preferOnnx = true;
    [SerializeField] bool blendProceduralOverlay = true;
    [Tooltip("While playing, regenerate surface textures on an interval so corruption keeps evolving.")]
    [SerializeField] bool realtimeRegenDuringPlay = true;
    [SerializeField] float regenIntervalSeconds = 3.2f;
    [Tooltip("Each regen also randomizes UV scale/offset per renderer so mapping is never identical.")]
    [SerializeField] bool randomizeMappingEachRegen = true;

    Worker _worker;
    Tensor<float> _inputTexture;
    bool _modelReady;
    int _lastAppliedLevel = -1;
    float _lastCorruption = -1f;
    FusionDirector.FusionParams _fusion;
    float _nextRegenTime;
    int _regenSeed;
    MaterialPropertyBlock _mpb;

    MaterialPropertyBlock Mpb => _mpb ??= new MaterialPropertyBlock();

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
    }

    void Update()
    {
        if (!realtimeRegenDuringPlay || surfaces == null || surfaces.Length == 0) return;
        if (GameStartMenu.Instance != null && !GameStartMenu.GameplayActive) return;
        if (Time.time < _nextRegenTime) return;

        int level = StressController.Instance != null ? StressController.Instance.StressLevel : 0;
        _regenSeed++;
        _lastAppliedLevel = -1;
        ApplyLevel(level);
        float interval = Mathf.Lerp(regenIntervalSeconds * 1.2f, regenIntervalSeconds * 0.5f, level / 5f);
        _nextRegenTime = Time.time + Mathf.Max(1.2f, interval);
    }

    /// <summary>Replace surface targets (used when rebuilding the haunted house).</summary>
    public void BindSurfaces(SurfaceTarget[] newSurfaces)
    {
        if (surfaces != null)
        {
            foreach (var s in surfaces)
            {
                if (s?.runtimeTex == null) continue;
                if (Application.isPlaying) Destroy(s.runtimeTex);
                else DestroyImmediate(s.runtimeTex);
            }
        }
        surfaces = newSurfaces ?? System.Array.Empty<SurfaceTarget>();
        InitSurfaces();
        _lastAppliedLevel = -1;
        ForceApply(StressController.Instance != null ? StressController.Instance.StressLevel : 0);
        Debug.Log($"[TextureCorruption] bound {surfaces.Length} surfaces for realtime generation.");
    }

    void OnDisable()
    {
        if (StressController.Instance != null)
            StressController.Instance.OnStressChanged -= OnStressChanged;
        if (FusionDirector.Instance != null)
            FusionDirector.Instance.OnParamsChanged -= OnFusion;
        DisposeModel();
    }

    void OnDestroy()
    {
        DisposeModel();
        if (surfaces == null) return;
        foreach (var s in surfaces)
            if (s?.runtimeTex != null) Destroy(s.runtimeTex);
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
            s.baseColor = s.material.HasProperty("_BaseColor")
                ? s.material.GetColor("_BaseColor")
                : Color.white;
            s.origScale = s.material.GetTextureScale(BaseMapProp);
            s.origOffset = s.material.GetTextureOffset(BaseMapProp);
            if (s.origScale.sqrMagnitude < 0.01f)
                s.origScale = Vector2.one;
            if (s.material.HasProperty("_BaseColor"))
                s.material.SetColor("_BaseColor", Color.white);
        }
    }

    void TryInitModel()
    {
        _modelReady = false;
        if (!preferOnnx || textureModelAsset == null) return;
        try
        {
            var model = ModelLoader.Load(textureModelAsset);
            _worker = new Worker(model, backend);
            _inputTexture = new Tensor<float>(new TensorShape(1, 3, ImgSize, ImgSize));
            _modelReady = true;
            Debug.Log("[TextureCorruption] texture_generator ready.");
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
        _modelReady = false;
    }

    void OnStressChanged(int level) => ForceApply(level);

    void ForceApply(int level)
    {
        _regenSeed++;
        _lastAppliedLevel = -1;
        ApplyLevel(level);
    }

    public void ApplyLevel(int level)
    {
        level = Mathf.Clamp(level, 0, 5);
        float corruption = _fusion != null ? _fusion.visual_corruption : level / 5f;

        if (level == _lastAppliedLevel && Mathf.Abs(corruption - _lastCorruption) < 0.05f)
            return;
        _lastAppliedLevel = level;
        _lastCorruption = corruption;

        // TV static is the intended look — skip ONNX (it paints cracks/blood).
        int procLevel = Mathf.Clamp(Mathf.RoundToInt(Mathf.Max(level, corruption * 5f)), 0, 5);
        if (level <= 0 && corruption < 0.12f)
        {
            RestoreClean();
            if (randomizeMappingEachRegen)
                ApplyRandomMapping(0);
            Debug.Log($"[TextureCorruption] level={level} static=off corr={corruption:F2}");
            return;
        }

        ApplyProcedural(procLevel, 1f);

        if (randomizeMappingEachRegen)
            ApplyRandomMapping(level);

        Debug.Log($"[TextureCorruption] level={level} static=on corr={corruption:F2} mapSeed={_regenSeed}");
    }

    void RestoreClean()
    {
        if (surfaces == null) return;
        foreach (var s in surfaces)
        {
            if (s?.material == null || s.cleanAlbedo == null) continue;
            s.material.SetTexture(BaseMapProp, s.cleanAlbedo);
            SetMaterialST(s.material, s.origScale, s.origOffset);
        }
        ClearRendererBlocks();
    }

    bool CorruptWithModel(int level)
    {
        if (surfaces == null) return false;
        using var stress = new Tensor<int>(new TensorShape(1), new[] { level });
        bool any = false;
        for (int si = 0; si < surfaces.Length; si++)
        {
            var s = surfaces[si];
            if (s?.material == null || s.cleanAlbedo == null) continue;
            int seed = _regenSeed * 97 + si * 131 + level * 17;
            var remapped = HorrorTextureCorruptor.RemapSource(s.cleanAlbedo, ImgSize, seed);
            TextureConverter.ToTensor(remapped, _inputTexture);
            Destroy(remapped);
            _worker.Schedule(_inputTexture, stress);
            var output = _worker.PeekOutput("corrupted_texture") as Tensor<float>
                         ?? _worker.PeekOutput() as Tensor<float>;
            if (output == null) throw new Exception("No corrupted_texture output");

            var rt = RenderTexture.GetTemporary(ImgSize, ImgSize, 0, RenderTextureFormat.ARGB32);
            TextureConverter.RenderToTexture(output, rt);
            if (s.runtimeTex != null) Destroy(s.runtimeTex);
            s.runtimeTex = new Texture2D(ImgSize, ImgSize, TextureFormat.RGBA32, true, false)
            {
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Bilinear,
                name = s.name + "_onnx_" + level
            };
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            s.runtimeTex.ReadPixels(new Rect(0, 0, ImgSize, ImgSize), 0, 0);
            s.runtimeTex.Apply(true);
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            RemapIfNeeded(s.runtimeTex);
            // Keep generated deform minor so PBR floor/wall detail stays readable.
            BlendToward(s.runtimeTex, s.cleanAlbedo, 0.32f);
            s.material.SetTexture(BaseMapProp, s.runtimeTex);
            if (s.material.HasProperty("_BaseColor"))
                s.material.SetColor("_BaseColor", Color.white);
            any = true;
        }
        return any;
    }

    static void RemapIfNeeded(Texture2D tex)
    {
        var px = tex.GetPixels();
        float min = 1f;
        for (int i = 0; i < px.Length; i += 16)
            min = Mathf.Min(min, px[i].r, px[i].g, px[i].b);
        if (min >= -0.01f) return;
        for (int i = 0; i < px.Length; i++)
        {
            px[i].r = Mathf.Clamp01((px[i].r + 1f) * 0.5f);
            px[i].g = Mathf.Clamp01((px[i].g + 1f) * 0.5f);
            px[i].b = Mathf.Clamp01((px[i].b + 1f) * 0.5f);
        }
        tex.SetPixels(px);
        tex.Apply(true);
    }

    void ApplyProcedural(int level, float strength)
    {
        if (surfaces == null) return;
        strength = Mathf.Clamp01(strength);
        for (int si = 0; si < surfaces.Length; si++)
        {
            var s = surfaces[si];
            if (s?.material == null || s.cleanAlbedo == null) continue;

            Texture2D baseTex = s.runtimeTex != null ? s.runtimeTex : s.cleanAlbedo;
            if (level <= 0 || strength < 0.05f)
            {
                s.material.SetTexture(BaseMapProp, s.cleanAlbedo);
                continue;
            }

            int seed = _regenSeed * 97 + si * 131 + level * 17;
            var remapped = HorrorTextureCorruptor.RemapSource(s.cleanAlbedo, ImgSize, seed);
            var corrupted = HorrorTextureCorruptor.Corrupt(remapped, level, ImgSize, seed);
            Destroy(remapped);
            if (strength < 0.99f && baseTex != null)
            {
                // Blend ONNX/clean with procedural deform by fusion strength
                var a = GetPixelsSafe(baseTex, ImgSize);
                var b = corrupted.GetPixels();
                for (int i = 0; i < a.Length; i++)
                    a[i] = Color.Lerp(a[i], b[i], strength);
                corrupted.SetPixels(a);
                corrupted.Apply(true);
            }

            if (s.runtimeTex != null && s.runtimeTex != corrupted) Destroy(s.runtimeTex);
            s.runtimeTex = corrupted;
            s.material.SetTexture(BaseMapProp, s.runtimeTex);
            if (s.material.HasProperty("_BaseColor"))
                s.material.SetColor("_BaseColor", Color.white);
        }
    }

    static Color[] GetPixelsSafe(Texture2D tex, int size)
    {
        if (tex.width == size && tex.height == size)
        {
            try { return tex.GetPixels(); } catch { /* fall through */ }
        }
        var rt = RenderTexture.GetTemporary(size, size, 0);
        Graphics.Blit(tex, rt);
        var prev = RenderTexture.active;
        RenderTexture.active = rt;
        var tmp = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tmp.ReadPixels(new Rect(0, 0, size, size), 0, 0);
        tmp.Apply();
        RenderTexture.active = prev;
        RenderTexture.ReleaseTemporary(rt);
        var px = tmp.GetPixels();
        UnityEngine.Object.Destroy(tmp);
        return px;
    }

    static void BlendToward(Texture2D generated, Texture2D clean, float generatedWeight)
    {
        if (generated == null || clean == null) return;
        generatedWeight = Mathf.Clamp01(generatedWeight);
        var a = GetPixelsSafe(clean, generated.width);
        var b = generated.GetPixels();
        int n = Mathf.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
            a[i] = Color.Lerp(a[i], b[i], generatedWeight);
        generated.SetPixels(a);
        generated.Apply(true);
    }

    void ApplyRandomMapping(int level)
    {
        if (surfaces == null) return;
        if (!Application.isPlaying)
        {
            for (int si = 0; si < surfaces.Length; si++)
            {
                var s = surfaces[si];
                if (s?.material == null) continue;
                Vector2 baseScale = s.origScale.sqrMagnitude > 0.01f ? s.origScale : Vector2.one;
                SetMaterialST(s.material, baseScale, s.origOffset);
            }
            return;
        }

        var renderers = FindObjectsOfType<Renderer>();
        for (int si = 0; si < surfaces.Length; si++)
        {
            var s = surfaces[si];
            if (s?.material == null) continue;

            Vector2 baseScale = s.origScale.sqrMagnitude > 0.01f ? s.origScale : Vector2.one;
            float jitter = 0.03f + level * 0.015f;
            var matScale = new Vector2(
                baseScale.x * UnityEngine.Random.Range(1f - jitter, 1f + jitter),
                baseScale.y * UnityEngine.Random.Range(1f - jitter, 1f + jitter));

            // Keep the shared asset tiling stable so outdoor Lit/Standard objects
            // never inherit house UV jitter through SRP batching.
            SetMaterialST(s.material, s.origScale, s.origOffset);

            int idx = 0;
            foreach (var r in renderers)
            {
                if (r == null || r.sharedMaterial != s.material) continue;
                if (!IsHouseStructureRenderer(r)) continue;
                float sx = matScale.x * UnityEngine.Random.Range(0.97f, 1.04f);
                float sy = matScale.y * UnityEngine.Random.Range(0.97f, 1.04f);
                var st = new Vector4(sx, sy,
                    UnityEngine.Random.Range(0f, 0.08f),
                    UnityEngine.Random.Range(0f, 0.08f));
                Mpb.Clear();
                r.GetPropertyBlock(Mpb);
                Mpb.SetVector("_BaseMap_ST", st);
                Mpb.SetVector("_MainTex_ST", st);
                r.SetPropertyBlock(Mpb);
                idx++;
            }
        }

        FreezeNonHouseMapping(renderers);
    }

    static void SetMaterialST(Material mat, Vector2 scale, Vector2 offset)
    {
        if (mat == null) return;
        mat.SetTextureScale(BaseMapProp, scale);
        mat.SetTextureOffset(BaseMapProp, offset);
        if (mat.HasProperty("_MainTex"))
        {
            mat.SetTextureScale("_MainTex", scale);
            mat.SetTextureOffset("_MainTex", offset);
        }
    }

    void ClearRendererBlocks()
    {
        var renderers = FindObjectsOfType<Renderer>();
        foreach (var r in renderers)
        {
            if (r == null) continue;
            bool ours = false;
            if (surfaces != null)
            {
                foreach (var s in surfaces)
                {
                    if (s?.material != null && r.sharedMaterial == s.material)
                    {
                        ours = true;
                        break;
                    }
                }
            }
            if (ours && IsHouseStructureRenderer(r))
                r.SetPropertyBlock(null);
        }
        FreezeNonHouseMapping(renderers);
    }

    static void FreezeNonHouseMapping(Renderer[] renderers)
    {
        if (renderers == null) return;
        foreach (var r in renderers)
        {
            if (r == null || IsHouseStructureRenderer(r)) continue;
            r.SetPropertyBlock(null);
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