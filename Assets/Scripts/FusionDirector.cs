using System;
using UnityEngine;
using Unity.InferenceEngine;

/// <summary>
/// Runs iteration-2 fusion_generator.onnx: stress + noise -> 30 horror params.
/// Other systems subscribe to OnParamsChanged.
/// </summary>
public class FusionDirector : MonoBehaviour
{
    public static FusionDirector Instance { get; private set; }

    public const int NoiseDim = 32;
    public const int ParamCount = 30;

    public static readonly string[] ParamNames =
    {
        "audio_intensity", "audio_dissonance", "audio_pitch_drift", "audio_reverb_depth", "audio_transient_rate",
        "dsp_pitch_shift", "dsp_distortion", "dsp_filter_freq", "dsp_reverb_mix", "dsp_layer_blend", "dsp_pan",
        "visual_corruption", "visual_fog_density", "visual_light_temp", "visual_flicker_rate", "visual_grime_overlay",
        "camera_magnitude", "camera_aberration", "camera_noise", "camera_vignette", "camera_warp",
        "camera_feed_face_distort", "camera_feed_shadow", "camera_feed_darken", "camera_feed_figure",
        "entity_probability", "entity_opacity", "entity_aggression", "entity_morphology", "entity_aura"
    };

    [Serializable]
    public class FusionParams
    {
        public int stressLevel;
        public float[] values = new float[ParamCount];

        public float Get(string name)
        {
            int i = Array.IndexOf(ParamNames, name);
            return i >= 0 && i < values.Length ? values[i] : 0f;
        }

        public float audio_intensity => Get("audio_intensity");
        public float audio_dissonance => Get("audio_dissonance");
        public float audio_pitch_drift => Get("audio_pitch_drift");
        public float dsp_pitch_shift => Get("dsp_pitch_shift");
        public float dsp_distortion => Get("dsp_distortion");
        public float dsp_reverb_mix => Get("dsp_reverb_mix");
        public float dsp_layer_blend => Get("dsp_layer_blend");
        public float visual_corruption => Get("visual_corruption");
        public float visual_fog_density => Get("visual_fog_density");
        public float visual_grime_overlay => Get("visual_grime_overlay");
        public float visual_flicker_rate => Get("visual_flicker_rate");
        public float entity_opacity => Get("entity_opacity");
        public float entity_probability => Get("entity_probability");
    }

    [SerializeField] ModelAsset fusionModelAsset;
    [SerializeField] BackendType backend = BackendType.GPUCompute;
    [SerializeField] bool applyFog = true;
    [SerializeField] bool applyLight = true;

    public event Action<FusionParams> OnParamsChanged;
    public FusionParams Latest { get; private set; } = new FusionParams();

    Worker _worker;
    bool _ready;
    Light _dirLight;
    Color _baseLightColor;
    float _baseLightIntensity;

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
        Dispose();
    }

    void OnEnable()
    {
        TryInit();
        TrySubscribe();
        Generate(StressController.Instance != null ? StressController.Instance.StressLevel : 0);
    }

    void Start()
    {
        TrySubscribe();
        CacheLighting();
        Generate(StressController.Instance != null ? StressController.Instance.StressLevel : 0);
    }

    void OnDisable()
    {
        if (StressController.Instance != null)
            StressController.Instance.OnStressChanged -= OnStress;
        Dispose();
    }

    void TrySubscribe()
    {
        if (StressController.Instance == null) return;
        StressController.Instance.OnStressChanged -= OnStress;
        StressController.Instance.OnStressChanged += OnStress;
    }

    void OnStress(int level) => Generate(level);

    void CacheLighting()
    {
        var go = GameObject.Find("Directional Light");
        if (go != null)
        {
            _dirLight = go.GetComponent<Light>();
            if (_dirLight != null)
            {
                _baseLightColor = _dirLight.color;
                _baseLightIntensity = _dirLight.intensity;
            }
        }
        var point = GameObject.Find("HorrorRoom/RoomPointLight");
        // fog baseline already set in scene
    }

    void TryInit()
    {
        _ready = false;
        if (fusionModelAsset == null)
        {
            Debug.LogWarning("[Fusion] No model assigned - using heuristic params.");
            return;
        }
        try
        {
            var model = ModelLoader.Load(fusionModelAsset);
            _worker = new Worker(model, backend);
            _ready = true;
            Debug.Log("[Fusion] fusion_generator ready.");
        }
        catch (Exception e)
        {
            Debug.LogWarning("[Fusion] init failed: " + e.Message);
            Dispose();
        }
    }

    void Dispose()
    {
        _worker?.Dispose();
        _worker = null;
        _ready = false;
    }

    public void Generate(int stressLevel)
    {
        stressLevel = Mathf.Clamp(stressLevel, 0, 5);
        var p = new FusionParams { stressLevel = stressLevel, values = new float[ParamCount] };

        if (_ready)
        {
            try
            {
                float[] noise = new float[NoiseDim];
                for (int i = 0; i < NoiseDim; i++)
                    noise[i] = UnityEngine.Random.Range(-1f, 1f);

                using var stress = new Tensor<int>(new TensorShape(1), new[] { stressLevel });
                using var noiseT = new Tensor<float>(new TensorShape(1, NoiseDim), noise);
                _worker.Schedule(stress, noiseT);
                var output = _worker.PeekOutput("parameters") as Tensor<float>
                             ?? _worker.PeekOutput() as Tensor<float>;
                if (output == null) throw new Exception("No parameters output");

                using var cpu = output.ReadbackAndClone();
                for (int i = 0; i < ParamCount && i < cpu.count; i++)
                    p.values[i] = cpu[i];
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Fusion] inference failed, heuristic fallback: " + e.Message);
                FillHeuristic(p, stressLevel);
            }
        }
        else
        {
            FillHeuristic(p, stressLevel);
        }

        Latest = p;
        ApplyEnvironment(p);
        OnParamsChanged?.Invoke(p);
        Debug.Log($"[Fusion] stress={stressLevel} corruption={p.visual_corruption:F2} audio={p.audio_intensity:F2} fog={p.visual_fog_density:F2}");
    }

    static void FillHeuristic(FusionParams p, int level)
    {
        float t = level / 5f;
        // Match OUTPUT_NAMES order roughly
        p.values[0] = Mathf.Lerp(0.1f, 1f, t); // audio_intensity
        p.values[1] = t * 0.8f;
        p.values[2] = (t - 0.5f) * 1.2f;
        p.values[3] = t * 0.7f;
        p.values[4] = t;
        p.values[5] = t * 0.5f; // dsp_pitch_shift
        p.values[6] = t * 0.7f;
        p.values[7] = 1f - t * 0.6f;
        p.values[8] = t * 0.6f;
        p.values[9] = t * 0.5f;
        p.values[10] = 0.5f;
        p.values[11] = Mathf.Lerp(0.05f, 1f, t); // visual_corruption
        p.values[12] = Mathf.Lerp(0.02f, 0.12f, t); // fog as density-ish
        p.values[13] = 1f - t * 0.4f;
        p.values[14] = t * 4f;
        p.values[15] = t;
        p.values[25] = level >= 4 ? 0.8f : 0.1f;
        p.values[26] = level >= 4 ? Mathf.Lerp(0.4f, 1f, (level - 4) / 1f) : 0f;
    }

    void ApplyEnvironment(FusionParams p)
    {
        if (applyFog)
        {
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            float dens = Mathf.Lerp(0.01f, 0.08f, Mathf.Clamp01(p.visual_fog_density * 4f + p.stressLevel / 5f * 0.5f));
            RenderSettings.fogDensity = dens;
            RenderSettings.fogColor = Color.Lerp(new Color(0.08f, 0.08f, 0.1f), new Color(0.02f, 0.01f, 0.01f), p.stressLevel / 5f);
        }

        if (applyLight && _dirLight != null)
        {
            float temp = p.Get("visual_light_temp");
            _dirLight.color = Color.Lerp(_baseLightColor, new Color(1f, 0.55f, 0.4f), (1f - temp) * 0.7f);
            _dirLight.intensity = Mathf.Lerp(_baseLightIntensity, _baseLightIntensity * 0.35f, p.stressLevel / 5f);
        }
    }
}