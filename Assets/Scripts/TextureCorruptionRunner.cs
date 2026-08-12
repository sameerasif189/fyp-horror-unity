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
    }

    [SerializeField] ModelAsset textureModelAsset;
    [SerializeField] BackendType backend = BackendType.GPUCompute;
    [SerializeField] SurfaceTarget[] surfaces;
    [SerializeField] bool preferOnnx = true;
    [SerializeField] bool blendProceduralOverlay = true;

    Worker _worker;
    Tensor<float> _inputTexture;
    bool _modelReady;
    int _lastAppliedLevel = -1;
    float _lastCorruption = -1f;
    FusionDirector.FusionParams _fusion;

    void OnEnable()
    {
        TrySubscribe();
        InitSurfaces();
        TryInitModel();
        ForceApply(StressController.Instance != null ? StressController.Instance.StressLevel : 0);
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
        _lastAppliedLevel = -1;
        ApplyLevel(level);
    }

    public void ApplyLevel(int level)
    {
        level = Mathf.Clamp(level, 0, 5);
        float corruption = _fusion != null ? _fusion.visual_corruption : level / 5f;
        float grime = _fusion != null ? _fusion.visual_grime_overlay : corruption;

        if (level == _lastAppliedLevel && Mathf.Abs(corruption - _lastCorruption) < 0.05f)
            return;
        _lastAppliedLevel = level;
        _lastCorruption = corruption;

        // Effective procedural stress based on fusion corruption strength
        int procLevel = Mathf.Clamp(Mathf.RoundToInt(Mathf.Max(level, corruption * 5f)), 0, 5);
        if (grime > 0.6f) procLevel = Mathf.Max(procLevel, 2);

        bool usedOnnx = false;
        if (_modelReady && (level > 0 || corruption > 0.15f))
        {
            try { usedOnnx = CorruptWithModel(level); }
            catch (Exception e) { Debug.LogWarning("[TextureCorruption] ONNX failed: " + e.Message); }
        }

        // If ONNX worked and corruption is mild, keep ONNX; otherwise overlay/replace with procedural deform
        bool needProc = !usedOnnx || blendProceduralOverlay || corruption > 0.35f || level >= 3;
        if (needProc)
            ApplyProcedural(procLevel, usedOnnx ? 0.65f + corruption * 0.35f : 1f);

        if (level == 0 && corruption < 0.1f && !needProc)
            RestoreClean();

        Debug.Log($"[TextureCorruption] level={level} onnx={usedOnnx} proc={needProc} corr={corruption:F2}");
    }

    void RestoreClean()
    {
        if (surfaces == null) return;
        foreach (var s in surfaces)
        {
            if (s?.material == null || s.cleanAlbedo == null) continue;
            s.material.SetTexture(BaseMapProp, s.cleanAlbedo);
        }
    }

    bool CorruptWithModel(int level)
    {
        if (surfaces == null) return false;
        using var stress = new Tensor<int>(new TensorShape(1), new[] { level });
        bool any = false;
        foreach (var s in surfaces)
        {
            if (s?.material == null || s.cleanAlbedo == null) continue;
            TextureConverter.ToTensor(s.cleanAlbedo, _inputTexture);
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
        foreach (var s in surfaces)
        {
            if (s?.material == null || s.cleanAlbedo == null) continue;

            Texture2D baseTex = s.runtimeTex != null ? s.runtimeTex : s.cleanAlbedo;
            if (level <= 0 || strength < 0.05f)
            {
                s.material.SetTexture(BaseMapProp, s.cleanAlbedo);
                continue;
            }

            var corrupted = HorrorTextureCorruptor.Corrupt(s.cleanAlbedo, level, ImgSize);
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
}