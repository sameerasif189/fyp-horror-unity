using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Stress-5-only corruption of a single monster (Milestone M4). Below stress 5 the monster renders
/// exactly as authored. At stress 5 (approved live 24 Sep 2026, "you KNOW you're done"):
///  - the dread paint from <see cref="MonsterDreadPainter"/>: crisp red fissures whose cores pulse on a
///    heartbeat, and pure-red wall-style blood with its own steady glow;
///  - an aura light, morphology (non-uniform proportions of the Visual) and per-vertex deformation.
/// The pattern is painted on worker threads AHEAD of the spawn - for every monster at scene start
/// (<see cref="PrewarmAll"/>, called by TextureCorruptionRunner), and a fresh one each time a monster despawns - so a
/// monster that spawns at stress 5 is corrupted from its first frame. (Painting on spawn took 3-5 s, during which a
/// stress-5 monster showed its clean skin.)
///
/// Nothing here mutates a shared asset: textures go through per-material MaterialPropertyBlocks, the
/// emission keyword is toggled on per-renderer material instances only while stress 5 lasts (enabling it
/// with no emissive map is what turned monsters into flat red silhouettes), the blood tint/glow passes are
/// runtime material copies appended to the renderer only at stress 5, and deformation works on cloned meshes.
/// Handles glTFast materials (baseColorTexture / emissiveTexture / emissiveFactor / _EMISSIVE) and URP Lit
/// (_BaseMap / _EmissionMap / _EmissionColor / _EMISSION).
/// </summary>
[DisallowMultipleComponent]
public class MonsterCorruptionController : MonoBehaviour
{
    [Header("Scope")]
    [Tooltip("Root to corrupt. Defaults to this transform.")]
    [SerializeField] Transform monsterRoot;

    [Header("Stress-5 paint")]
    [SerializeField] int fissureCount = 19;
    [Tooltip("Paint resolution per material; halved when the monster has more than two painted materials. " +
             "Deliberately above the 512 source textures so crack outlines stay crisp on the 1.4x monsters.")]
    [SerializeField] int paintResolution = 2048;
    [SerializeField] Color fissureGlow = new Color(0.60f, 0.020f, 0.015f);
    [Tooltip("Fissure glow between beats (x fissureGlow). The approved dim level reads just above the skin.")]
    [SerializeField] float fissureGlowBase = 0.22f;
    [Tooltip("Added at the heartbeat peak (x fissureGlow); base + beat peaks around 1.0.")]
    [SerializeField] float fissureGlowBeat = 1.46f;
    [SerializeField] float heartbeatHz = 1.1f;
    [Tooltip("Steady (non-pulsing) blood glow, drawn by an extra additive pass. Kept low: brighter reads as flat pink.")]
    [SerializeField] Color bloodGlow = new Color(0.12f, 0f, 0f);
    [Tooltip("Green/blue kept inside the blood by the multiply tint pass (0 = pure red).")]
    [SerializeField, Range(0f, 1f)] float bloodKeepGreenBlue = 0.06f;

    [Header("Aura")]
    [SerializeField] bool spawnAuraLight = true;
    [SerializeField] Color auraColor = new Color(0.60f, 0.02f, 0.02f);
    [SerializeField] float auraReach = 6f;
    [SerializeField] float auraPeak = 3f;
    [SerializeField] float auraFlickerHz = 7f;

    [Header("Morphology")]
    [Tooltip("Peak non-uniform stretch of the VISUAL at stress 5, as a fraction of its scale. " +
             "The root is never scaled, so the hitbox and NavMeshAgent stay fixed.")]
    [SerializeField] float maxStretch = 0.22f;
    [SerializeField] float morphSpeed = 0.7f;

    [Header("Deformation")]
    [Tooltip("Per-vertex jitter on cloned meshes (MeshFilter and SkinnedMeshRenderer).")]
    [SerializeField] bool deformMeshes = true;
    [Tooltip("Peak jitter as a fraction of each mesh's own size, so it reads the same whatever units the import used.")]
    [SerializeField] float maxVertexJitterFraction = 0.02f;
    [SerializeField] float deformIntervalSeconds = 0.25f;
    [Tooltip("Meshes above this vertex count are left undeformed to keep the cost bounded.")]
    [SerializeField] int maxDeformableVertices = 6000;

    const string GlowPassName = "DreadBloodGlow";
    const string TintPassName = "DreadBloodTint";

    class DeformTarget
    {
        public MeshFilter filter;
        public SkinnedMeshRenderer skinned;
        public Mesh original;
        public Mesh clone;
        public Vector3[] baseVerts;
        public Vector3[] workVerts;
        public Vector3 center;
        public float invSize;
        public float amp;
    }

    /// <summary>One painted material slot of one renderer.</summary>
    sealed class Slot
    {
        public Renderer renderer;
        public int index;
        public int baseTexId, emisTexId, emisColorId;
        public string keyword;
        public Texture2D source;
        public Color32[] clean;
        public Vector2[] uv;
        public int[] triangles;
        public MonsterDreadPainter.Result result;
        public Texture2D albedo, fissureMask, bloodGlow, tint;
        public Material keywordMaterial;
        public bool keywordWasOn;
    }

    readonly List<Renderer> _renderers = new List<Renderer>();
    readonly List<DeformTarget> _deformTargets = new List<DeformTarget>();
    readonly List<Slot> _slots = new List<Slot>();
    readonly List<Material> _passMaterials = new List<Material>();
    MaterialPropertyBlock _mpb;
    Light _aura;
    Transform _morphTarget;
    Vector3 _baseScale;
    float _threat;
    int _stress;
    float _nextDeformTime;
    bool _bound;
    bool _active;
    bool _painted;
    bool _paintFailed;
    int _paintGeneration;
    Task _paintTask;

    MaterialPropertyBlock Mpb => _mpb ??= new MaterialPropertyBlock();

    /// <summary>Controllers painting while their monster is parked inactive (no Update to collect the result).</summary>
    static readonly List<MonsterCorruptionController> Prewarming = new List<MonsterCorruptionController>();
    static bool _quitting;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        Prewarming.Clear();
        _quitting = false;
        Application.quitting -= OnQuitting;
        Application.quitting += OnQuitting;
    }

    static void OnQuitting() => _quitting = true;

    /// <summary>Start painting every monster's stress-5 pattern now, including parked (inactive) ones.</summary>
    public static void PrewarmAll()
    {
        foreach (var c in FindObjectsByType<MonsterCorruptionController>(FindObjectsInactive.Include))
            c.Prewarm();
    }

    /// <summary>Main thread, every frame: turn finished background paint into textures (parked monsters have no Update).</summary>
    public static void CollectPrewarmed()
    {
        for (int i = Prewarming.Count - 1; i >= 0; i--)
        {
            var c = Prewarming[i];
            if (c == null) { Prewarming.RemoveAt(i); continue; }
            if (c._paintTask != null && !c._paintTask.IsCompleted) continue;
            c.CollectPaint();
            Prewarming.RemoveAt(i);
        }
    }

    void Prewarm()
    {
        if (!_bound) Bind(monsterRoot != null ? monsterRoot : transform);
        if (_painted || _paintTask != null) return;
        StartPaint();
        if (_paintTask != null && !Prewarming.Contains(this)) Prewarming.Add(this);
    }

    /// <summary>
    /// Whether the stress-5 skin can be on this monster the moment it spawns: its paint is done (applied, or finished and
    /// waiting for OnEnable to collect it), it has nothing to paint, or painting failed (nothing to wait for).
    /// False while a pattern is still being painted - e.g. for 3-5 s after it despawned. MonsterDirector skips such a
    /// monster at stress 5 so a fast demo never spawns one with a clean skin.
    /// </summary>
    public bool SkinReady
    {
        get
        {
            if (_painted || _paintFailed) return true;
            if (_paintTask != null) return _paintTask.IsCompleted;
            return !_bound || _slots.Count == 0;
        }
    }

    /// <summary>0..1 chase/threat weight. M2 can drive this; it layers on top of stress 5.</summary>
    public void SetThreat(float threat01) => _threat = Mathf.Clamp01(threat01);

    void Awake()
    {
        if (!_bound) Bind(monsterRoot != null ? monsterRoot : transform);   // PrewarmAll may have bound it already
    }

    void OnEnable()
    {
        if (!_bound) Bind(monsterRoot != null ? monsterRoot : transform);
        if (StressController.Instance != null)
        {
            StressController.Instance.OnStressChanged -= OnStressChanged;
            StressController.Instance.OnStressChanged += OnStressChanged;
            _stress = StressController.Instance.StressLevel;
        }
        // Normally the pattern is already painted (prewarm, or the repaint after the last despawn).
        if (_paintTask != null && _paintTask.IsCompleted) CollectPaint();
        if (!_painted && _paintTask == null) StartPaint();
        // Spawned at stress 5: put the skin on now, not in the first Update - the spawn frame itself renders first.
        if (_stress >= 5 && _painted && !_active)
        {
            Enter();
            ApplyPaint();
        }
    }

    void OnDisable()
    {
        if (StressController.Instance != null)
            StressController.Instance.OnStressChanged -= OnStressChanged;
        Exit();
        // Despawned: paint a fresh pattern for the next spawn while the monster is parked. Not while the scene or the
        // game is shutting down.
        if (_quitting || !Application.isPlaying || !gameObject.scene.isLoaded)
        {
            ReleasePaint();
            return;
        }
        // One repaint at a time: a pattern still being painted has not been shown yet, so it will do. Starting another
        // on every despawn let rapid spawn / despawn cycles stack up paint jobs (16 MB+ per material each) until the
        // editor ran out of memory.
        if (_paintTask == null) StartPaint();
        if (_paintTask != null && !Prewarming.Contains(this)) Prewarming.Add(this);
    }

    void OnDestroy()
    {
        Prewarming.Remove(this);
        ReleasePaint();
        ReleaseDeformTargets();
    }

    /// <summary>Point the controller at a monster root and cache its renderers and meshes.</summary>
    public void Bind(Transform root)
    {
        Exit();
        ReleaseDeformTargets();
        monsterRoot = root != null ? root : transform;

        // Morph the Visual, never the root: the root carries the CapsuleCollider and NavMeshAgent,
        // and scaling it would stretch the hitbox along with the model.
        _morphTarget = monsterRoot.Find("Visual");
        if (_morphTarget == null) _morphTarget = monsterRoot;
        _baseScale = _morphTarget.localScale;

        _renderers.Clear();
        monsterRoot.GetComponentsInChildren(true, _renderers);

        if (deformMeshes)
        {
            foreach (var f in monsterRoot.GetComponentsInChildren<MeshFilter>(true))
                TryAddDeformTarget(f != null ? f.sharedMesh : null, f, null);
            // Skinned monsters: deforming the bind-pose vertices of a cloned mesh wobbles the skinned result too.
            foreach (var s in monsterRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                TryAddDeformTarget(s != null ? s.sharedMesh : null, null, s);
        }

        if (spawnAuraLight && _aura == null)
        {
            var go = new GameObject("MonsterAura");
            go.transform.SetParent(monsterRoot, false);
            go.transform.localPosition = Vector3.up * 1.2f;
            _aura = go.AddComponent<Light>();
            _aura.type = LightType.Point;
            _aura.range = auraReach;
            _aura.color = auraColor;
            _aura.intensity = 0f;
            _aura.shadows = LightShadows.None;
            _aura.enabled = false;
        }

        _bound = true;
    }

    void TryAddDeformTarget(Mesh shared, MeshFilter filter, SkinnedMeshRenderer skinned)
    {
        if (shared == null || !shared.isReadable) return;
        if (shared.vertexCount > maxDeformableVertices) return;

        // Clone so the project's mesh asset is never written to.
        var clone = Instantiate(shared);
        clone.name = shared.name + " (deform)";
        clone.MarkDynamic();
        var baseVerts = shared.vertices;
        var b = shared.bounds;
        float size = Mathf.Max(1e-4f, b.size.magnitude);
        _deformTargets.Add(new DeformTarget
        {
            filter = filter,
            skinned = skinned,
            original = shared,
            clone = clone,
            baseVerts = baseVerts,
            workVerts = new Vector3[baseVerts.Length],
            center = b.center,
            invSize = 1f / size,
            amp = size * maxVertexJitterFraction,
        });
        if (filter != null) filter.sharedMesh = clone;
        if (skinned != null) skinned.sharedMesh = clone;
    }

    void OnStressChanged(int level) => _stress = level;

    void Update()
    {
        if (!_bound) return;
        CollectPaint();

        bool want = _stress >= 5;
        if (want != _active)
        {
            if (want) Enter();
            else Exit();
        }
        if (!_active) return;

        float intensity = Mathf.Clamp01(0.75f + _threat * 0.45f);
        ApplyPaint();
        ApplyAura(intensity);
        ApplyMorphology(intensity);
        if (deformMeshes && Time.time >= _nextDeformTime)
        {
            ApplyDeformation(intensity);
            // Deform faster as things get worse, but never every frame.
            _nextDeformTime = Time.time + Mathf.Lerp(deformIntervalSeconds * 2f, deformIntervalSeconds, intensity);
        }
    }

    // ------------------------------------------------------------------ paint

    static bool IsPass(Material m) => m != null && (m.name.StartsWith(GlowPassName) || m.name.StartsWith(TintPassName));

    /// <summary>Snapshot every textured material slot on the main thread, then paint all of them on workers.</summary>
    void StartPaint()
    {
        ReleasePaint();
        _paintFailed = false;
        int generation = ++_paintGeneration;
        for (int ri = 0; ri < _renderers.Count; ri++)
        {
            var r = _renderers[ri];
            if (r == null) continue;
            Mesh mesh = r is SkinnedMeshRenderer smr ? smr.sharedMesh : r.GetComponent<MeshFilter>()?.sharedMesh;
            var mats = r.sharedMaterials;
            for (int m = 0; m < mats.Length; m++)
            {
                var mat = mats[m];
                if (mat == null || IsPass(mat)) continue;
                var slot = new Slot { renderer = r, index = m };
                if (mat.HasProperty("baseColorTexture"))
                {
                    slot.baseTexId = Shader.PropertyToID("baseColorTexture");
                    slot.emisTexId = Shader.PropertyToID("emissiveTexture");
                    slot.emisColorId = Shader.PropertyToID("emissiveFactor");
                    slot.keyword = "_EMISSIVE";
                }
                else if (mat.HasProperty("_BaseMap") && mat.HasProperty("_EmissionMap"))
                {
                    slot.baseTexId = Shader.PropertyToID("_BaseMap");
                    slot.emisTexId = Shader.PropertyToID("_EmissionMap");
                    slot.emisColorId = Shader.PropertyToID("_EmissionColor");
                    slot.keyword = "_EMISSION";
                }
                else continue;
                slot.source = mat.GetTexture(slot.baseTexId) as Texture2D;
                if (slot.source == null) continue;
                if (mesh != null && mesh.isReadable)
                {
                    slot.uv = mesh.uv;
                    slot.triangles = mesh.GetTriangles(Mathf.Min(m, mesh.subMeshCount - 1));
                }
                _slots.Add(slot);
            }
        }
        if (_slots.Count == 0) return;

        int n = Mathf.ClosestPowerOfTwo(Mathf.Clamp(paintResolution, 256, 4096));
        if (_slots.Count > 2) n = Mathf.Max(512, n / 2);   // keep memory sane on multi-material monsters
        foreach (var s in _slots)
        {
            s.clean = new Color32[n * n];
            HorrorTextureCorruptor.ReadClean(s.source, n, s.clean);
        }

        var slots = _slots.ToArray();
        var settings = new MonsterDreadPainter.Settings { fissures = fissureCount, keepGreenBlue = bloodKeepGreenBlue };
        int seed = Random.Range(1, int.MaxValue);
        _paintTask = Task.Run(() => Parallel.For(0, slots.Length, i =>
        {
            if (generation != _paintGeneration) return;   // despawned before we got here
            var s = slots[i];
            var cov = s.uv != null ? MonsterDreadPainter.Coverage(s.uv, s.triangles, n) : null;
            s.result = MonsterDreadPainter.Paint(s.clean, cov, n, seed ^ (i * 7919), settings);
        }));
    }

    /// <summary>Main thread: turn finished worker output into textures.</summary>
    void CollectPaint()
    {
        if (_paintTask == null || !_paintTask.IsCompleted) return;
        var task = _paintTask;
        _paintTask = null;
        if (task.IsFaulted)
        {
            _paintFailed = true;
            Debug.LogWarning($"[MonsterCorruption] {name}: paint failed: {task.Exception?.GetBaseException().Message}");
            return;
        }
        foreach (var s in _slots)
        {
            var res = s.result;
            if (res == null) continue;
            var wrap = s.source != null ? s.source.wrapMode : TextureWrapMode.Repeat;
            s.albedo = new Texture2D(res.size, res.size, TextureFormat.RGBA32, true, false) { name = s.source.name + "_dread", wrapMode = wrap, anisoLevel = 4 };
            s.albedo.SetPixels32(res.albedo);
            s.albedo.Apply(true, true);
            s.fissureMask = MaskTexture(res.fissureMask, res.size, s.source.name + "_dreadFissures", wrap);
            s.bloodGlow = MaskTexture(res.bloodGlow, res.size, s.source.name + "_dreadBloodGlow", wrap);
            s.tint = new Texture2D(res.tintSize, res.tintSize, TextureFormat.RGBA32, true, true) { name = s.source.name + "_dreadTint", wrapMode = wrap };
            s.tint.SetPixels32(res.tint);
            s.tint.Apply(true, true);
            s.result = null;
            s.clean = null;
            s.uv = null;
            s.triangles = null;
        }
        _painted = true;
        if (_active) EnsurePasses();
    }

    static Texture2D MaskTexture(byte[] px, int n, string name, TextureWrapMode wrap)
    {
        // R8 samples as (r, 0, 0): fine, both glow colours are (near) pure red.
        var t = new Texture2D(n, n, TextureFormat.R8, true, true) { name = name, wrapMode = wrap };
        t.SetPixelData(px, 0);
        t.Apply(true, true);
        return t;
    }

    void ReleasePaint()
    {
        _paintGeneration++;
        _paintTask = null;
        foreach (var s in _slots)
        {
            if (s.albedo != null) HorrorTextureCorruptor.SafeDestroy(s.albedo);
            if (s.fissureMask != null) HorrorTextureCorruptor.SafeDestroy(s.fissureMask);
            if (s.bloodGlow != null) HorrorTextureCorruptor.SafeDestroy(s.bloodGlow);
            if (s.tint != null) HorrorTextureCorruptor.SafeDestroy(s.tint);
        }
        _slots.Clear();
        _painted = false;
    }

    void Enter()
    {
        _active = true;
        if (_painted) EnsurePasses();
        _nextDeformTime = 0f;
    }

    /// <summary>
    /// Emission keyword on the per-renderer instances and the two blood passes. Extra materials on a
    /// renderer redraw its last submesh, so the passes use the last painted slot's maps.
    /// </summary>
    void EnsurePasses()
    {
        var lastByRenderer = new Dictionary<Renderer, Slot>();
        foreach (var s in _slots)
        {
            if (s.renderer == null || s.albedo == null) continue;
            if (s.keywordMaterial == null)
            {
                var inst = s.renderer.materials;   // per-renderer instances; never the imported asset
                if (s.index < inst.Length && inst[s.index] != null)
                {
                    s.keywordMaterial = inst[s.index];
                    s.keywordWasOn = s.keywordMaterial.IsKeywordEnabled(s.keyword);
                    s.keywordMaterial.EnableKeyword(s.keyword);
                }
            }
            if (!lastByRenderer.TryGetValue(s.renderer, out var prev) || s.index > prev.index) lastByRenderer[s.renderer] = s;
        }

        foreach (var kv in lastByRenderer)
        {
            var r = kv.Key;
            var s = kv.Value;
            var mesh = r is SkinnedMeshRenderer smr ? smr.sharedMesh : r.GetComponent<MeshFilter>()?.sharedMesh;
            if (mesh == null || s.index != mesh.subMeshCount - 1) continue;   // passes would land on a different submesh
            var mats = r.sharedMaterials;
            bool hasGlow = false, hasTint = false;
            foreach (var m in mats)
            {
                if (m == null) continue;
                hasGlow |= m.name.StartsWith(GlowPassName);
                hasTint |= m.name.StartsWith(TintPassName);
            }
            if (hasGlow && hasTint) continue;
            var list = new List<Material>(mats);
            if (!hasTint) list.Add(MakePass(TintPassName, s.tint, true));
            if (!hasGlow) list.Add(MakePass(GlowPassName, s.bloodGlow, false));
            r.sharedMaterials = list.ToArray();
        }
    }

    Material MakePass(string passName, Texture tex, bool multiply)
    {
        var template = Resources.Load<Material>("Dread/" + passName);
        var m = template != null ? new Material(template) : CreateUnlitPass(multiply);
        m.name = passName;
        m.SetTexture("_BaseMap", tex);
        m.SetColor("_BaseColor", multiply ? Color.white : Color.black);
        _passMaterials.Add(m);
        return m;
    }

    /// <summary>Fallback when the Resources template is missing (the editor always has these shaders).</summary>
    static Material CreateUnlitPass(bool multiply)
    {
        var m = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
        m.SetFloat("_Surface", 1f);
        m.SetFloat("_Blend", multiply ? 3f : 2f);
        m.SetFloat("_SrcBlend", (float)(multiply ? BlendMode.DstColor : BlendMode.SrcAlpha));
        m.SetFloat("_DstBlend", (float)(multiply ? BlendMode.Zero : BlendMode.One));
        m.SetFloat("_SrcBlendAlpha", (float)(multiply ? BlendMode.Zero : BlendMode.One));
        m.SetFloat("_DstBlendAlpha", (float)BlendMode.One);
        m.SetFloat("_ZWrite", 0f);
        m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        m.SetOverrideTag("RenderType", "Transparent");
        m.renderQueue = (int)RenderQueue.Transparent + (multiply ? 1 : 2);   // tint first, then glow
        return m;
    }

    void ApplyPaint()
    {
        if (!_painted) return;
        float ph = Time.time * heartbeatHz % 1f;
        float beat = Mathf.Clamp01(Mathf.Exp(-ph * 14f) + 0.6f * Mathf.Exp(-Mathf.Abs(ph - 0.22f) * 18f));
        var emit = fissureGlow * (fissureGlowBase + fissureGlowBeat * beat);
        emit.a = 1f;
        foreach (var s in _slots)
        {
            if (s.renderer == null || s.albedo == null) continue;
            // Re-asserted every frame: the house runner clears non-house property blocks on stress changes.
            Mpb.Clear();
            s.renderer.GetPropertyBlock(Mpb, s.index);
            Mpb.SetTexture(s.baseTexId, s.albedo);
            Mpb.SetTexture(s.emisTexId, s.fissureMask);
            Mpb.SetColor(s.emisColorId, emit);
            s.renderer.SetPropertyBlock(Mpb, s.index);
        }
        for (int i = 0; i < _renderers.Count; i++)
        {
            var r = _renderers[i];
            if (r == null) continue;
            // Found by name: MonsterAgent's fade reads Renderer.materials, which may have cloned the passes.
            foreach (var m in r.sharedMaterials)
            {
                if (m == null || !m.name.StartsWith(GlowPassName)) continue;
                var c = bloodGlow;
                c.a = m.GetColor("_BaseColor").a;   // keep the fade alpha
                if (c.a <= 0f) c.a = 1f;
                m.SetColor("_BaseColor", c);
            }
        }
    }

    void Exit()
    {
        if (!_active) return;
        _active = false;
        foreach (var s in _slots)
        {
            if (s.renderer != null) s.renderer.SetPropertyBlock(null, s.index);
            if (s.keywordMaterial != null && !s.keywordWasOn) s.keywordMaterial.DisableKeyword(s.keyword);
            s.keywordMaterial = null;
        }
        for (int i = 0; i < _renderers.Count; i++)
        {
            var r = _renderers[i];
            if (r == null) continue;
            var mats = r.sharedMaterials;
            var keep = new List<Material>(mats.Length);
            foreach (var m in mats)
            {
                if (IsPass(m)) HorrorTextureCorruptor.SafeDestroy(m);   // runtime copies or their instances, never assets
                else keep.Add(m);
            }
            if (keep.Count != mats.Length) r.sharedMaterials = keep.ToArray();
        }
        foreach (var m in _passMaterials) if (m != null) HorrorTextureCorruptor.SafeDestroy(m);
        _passMaterials.Clear();

        if (_aura != null) _aura.enabled = false;
        if (_morphTarget != null && _baseScale != Vector3.zero) _morphTarget.localScale = _baseScale;
        for (int i = 0; i < _deformTargets.Count; i++)
        {
            var d = _deformTargets[i];
            if (d?.clone != null && d.baseVerts != null)
            {
                d.clone.SetVertices(d.baseVerts);
                d.clone.RecalculateNormals();
                d.clone.RecalculateBounds();
            }
        }
    }

    // ------------------------------------------------------------------ aura / morph / deform

    void ApplyAura(float intensity)
    {
        if (_aura == null) return;
        float flicker = 0.75f + 0.25f * Mathf.PerlinNoise(Time.time * auraFlickerHz, 0.37f);
        _aura.color = auraColor;
        _aura.intensity = auraPeak * intensity * intensity * flicker;
        _aura.range = auraReach * (0.6f + 0.4f * intensity);
        _aura.enabled = _aura.intensity > 0.01f;
    }

    void ApplyMorphology(float intensity)
    {
        if (_morphTarget == null) return;
        float t = Time.time * morphSpeed;
        // Elongate vertically while pinching horizontally, and let the two axes drift apart.
        float stretch = maxStretch * intensity;
        float sy = 1f + stretch * Mathf.Sin(t);
        float sx = 1f - stretch * 0.55f * Mathf.Sin(t + 1.1f);
        float sz = 1f - stretch * 0.55f * Mathf.Sin(t + 2.3f);
        _morphTarget.localScale = new Vector3(_baseScale.x * sx, _baseScale.y * sy, _baseScale.z * sz);
    }

    void ApplyDeformation(float intensity)
    {
        if (intensity <= 0.01f) return;
        float t = Time.time * 0.6f;
        for (int i = 0; i < _deformTargets.Count; i++)
        {
            var d = _deformTargets[i];
            if (d?.clone == null || d.baseVerts == null) continue;
            float amp = d.amp * intensity;
            for (int v = 0; v < d.baseVerts.Length; v++)
            {
                var p = d.baseVerts[v];
                // Noise in mesh-normalised space so the wobble travels over the body coherently.
                var q = (p - d.center) * d.invSize * 6f;
                float nx = Mathf.PerlinNoise(q.x + t, q.y) - 0.5f;
                float ny = Mathf.PerlinNoise(q.y + t, q.z) - 0.5f;
                float nz = Mathf.PerlinNoise(q.z + t, q.x) - 0.5f;
                d.workVerts[v] = p + new Vector3(nx, ny, nz) * (amp * 2f);
            }
            d.clone.SetVertices(d.workVerts);
            d.clone.RecalculateNormals();
            d.clone.RecalculateBounds();
        }
    }

    void ReleaseDeformTargets()
    {
        for (int i = 0; i < _deformTargets.Count; i++)
        {
            var d = _deformTargets[i];
            if (d == null) continue;
            // Put the original mesh back before destroying the clone, so nothing points at a destroyed mesh.
            if (d.filter != null && d.original != null) d.filter.sharedMesh = d.original;
            if (d.skinned != null && d.original != null) d.skinned.sharedMesh = d.original;
            if (d.clone != null) HorrorTextureCorruptor.SafeDestroy(d.clone);
        }
        _deformTargets.Clear();
    }
}
