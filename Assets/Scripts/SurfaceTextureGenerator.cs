using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.InferenceEngine;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Milestone M4 texture generation: iteration 2's runtime environment pipeline ported to Unity
/// (<c>fyp iteration 2/gui_demo.py</c>: <c>build_varied_base_texture</c> + <c>TextureRuntime.corrupt_surface</c>).
/// Every result is a new texture; the library (<see cref="SurfaceTextureDeck"/>) only supplies raw material.
///
///  1. Compose: four library sources for the surface, drawn around the stress level (tier offsets -1/0/+1 weighted
///     1:22:1, as in iteration 2), each wrap-shifted/mirrored, layered as base + 27-39% + 15-24% + an additive
///     layer through a random soft blob mask.
///  2. Corrupt: iteration 2's conditional U-Net (<c>texture_generator.onnx</c>, 256 px, inputs in [-1,1]) at the
///     stress level. As in <c>corrupt_surface</c>, only its change is used: delta x gain (0.12 + 0.28*strength +
///     0.065*level/5, max 0.38) plus crack darkening where it changed the most (top 14%+1.55%*level). The delta is
///     upsampled onto the 512 px composite so the sources' detail survives.
///
/// Results are pre-generated in the background, one pipeline stage per frame: source reads via async GPU
/// readback, compose and finish on worker threads, U-Net inference with async readback, and - for levels shown
/// as-is (0, and 5 after the dread grade) - an upload stage, so a stress change only swaps textures and queues
/// a fresh one. Nothing is ever generated on the spot. The base layer is never shifted/mirrored so its normal
/// map stays aligned.
/// </summary>
public sealed class SurfaceTextureGenerator : IDisposable
{
    public const int Size = 512;
    const int ModelSize = 256;

    public sealed class Result
    {
        public Color32[] pixels;   // Size x Size
        public Texture2D normal;   // the base layer's normal map, or null
        public Texture2D texture;  // pre-uploaded (post-processed) texture for upload levels, else null
        public string label;
    }

    sealed class Job
    {
        public string surface;
        public int level, stage, seed;
        public float started;   // realtime, for the status panel
        public string label;
        public Texture2D normal;
        public Texture2D[] sources;
        public RenderTexture[] rts;
        public AsyncGPUReadbackRequest[] reads;
        public Color32[][] layers;
        public Color32[] composite;
        public float[] modelInput;
        public float[] modelOutput;
        public Task task;
        public Tensor<float> input, output;
        public Tensor<int> stress;
        public Color32[] uploadPixels;
        public Result result;
    }

    readonly SurfaceTextureDeck _deck;
    readonly Worker _worker;
    readonly System.Random _rng;
    readonly Predicate<int> _uploadLevel;
    readonly Func<int, Func<Color32[], Color32[]>> _postProcessFor;
    readonly Dictionary<(string, int), Result> _ready = new Dictionary<(string, int), Result>();
    readonly Dictionary<(string, int), Result> _last = new Dictionary<(string, int), Result>();
    readonly List<(string, int)> _queue = new List<(string, int)>();
    Job _job;

    public int ReadyCount => _ready.Count;
    public bool ModelAvailable => _worker != null;

    // ---- Status, for the on-screen panel (TextureGenerationHud) ----

    readonly List<string> _surfaceOrder = new List<string>();
    readonly HashSet<(string, int)> _slots = new HashSet<(string, int)>();

    /// <summary>Surfaces in the order they were first requested.</summary>
    public IReadOnlyList<string> Surfaces => _surfaceOrder;
    /// <summary>Running average of one generation, start to finish (realtime seconds).</summary>
    public float AverageSeconds { get; private set; }
    public int CompletedCount { get; private set; }
    /// <summary>Whether (surface, level) is generated at all (has a library slot and was requested).</summary>
    public bool HasSlot(string surface, int level) => _slots.Contains((surface, level));
    /// <summary>A fresh texture is waiting: the next visit to <paramref name="level"/> shows it.</summary>
    public bool IsReady(string surface, int level) => _ready.ContainsKey((surface, level));
    public bool IsGenerating(string surface, int level) => _job != null && _job.surface == surface && _job.level == level;
    public bool IsQueued(string surface, int level) => _queue.Contains((surface, level));
    public int QueuedCount => _queue.Count;
    public string CurrentSurface => _job?.surface;
    public int CurrentLevel => _job?.level ?? -1;
    public float CurrentElapsed => _job != null ? Time.realtimeSinceStartup - _job.started : 0f;
    public string CurrentStage => _job == null ? null
        : _job.stage <= 1 ? "reading sources" : _job.stage == 2 ? "composing" : _job.stage == 3 ? "U-Net"
        : _job.stage == 6 ? "uploading" : "finishing";

    /// <summary>
    /// Estimated seconds until every queued slot of <paramref name="level"/> is ready, at the running average;
    /// 0 when nothing is pending for it.
    /// </summary>
    public float EtaSeconds(int level)
    {
        float avg = AverageSeconds > 0f ? AverageSeconds : 0.5f;
        int last = -1;
        for (int i = 0; i < _queue.Count; i++) if (_queue[i].Item2 == level) last = i;
        bool current = _job != null && _job.level == level;
        if (last < 0 && !current) return 0f;
        float remaining = _job != null ? Mathf.Max(0.05f, avg - CurrentElapsed) : 0f;
        return remaining + (last + 1) * avg;
    }

    /// <param name="uploadLevel">Levels whose results are shown as-is and should arrive already on the GPU
    /// (<see cref="Result.texture"/>), so the stress change itself does no uploads.</param>
    /// <param name="postProcessFor">Optional worker-thread pixel transform per level, applied before that upload
    /// (the stress-5 dread grade).</param>
    public SurfaceTextureGenerator(SurfaceTextureDeck deck, ModelAsset model, int seed,
                                   Predicate<int> uploadLevel = null, Func<int, Func<Color32[], Color32[]>> postProcessFor = null)
    {
        _deck = deck;
        _rng = new System.Random(seed);
        _uploadLevel = uploadLevel;
        _postProcessFor = postProcessFor;
        if (model != null)
        {
            try { _worker = new Worker(ModelLoader.Load(model), BackendType.GPUCompute); }
            catch (Exception e) { Debug.LogWarning("[TextureGen] U-Net unavailable, composing only: " + e.Message); }
        }
    }

    /// <summary>Queue a background generation for this slot unless one is ready or already queued.</summary>
    public void Request(string surface, int level)
    {
        var key = (surface, level);
        if (_deck == null || !_deck.Has(surface, level)) return;
        if (_slots.Add(key) && !_surfaceOrder.Contains(surface)) _surfaceOrder.Add(surface);
        if (_ready.ContainsKey(key) || _queue.Contains(key)) return;
        if (_job != null && _job.surface == surface && _job.level == level) return;
        _queue.Add(key);
    }

    /// <summary>
    /// A generated texture for the slot, never generating on the spot (that froze stress changes for up to
    /// ~0.4 s per surface): the pre-generated one when ready, else the slot's previous one (a repeat beats a
    /// freeze), else null - only possible in the first seconds of play, before the slot was ever generated;
    /// the caller then keeps the authored texture and the level is moved to the front of the queue.
    /// A fresh texture is queued at the back - the one just taken is already on screen;
    /// <see cref="PrioritiseLevel"/> moves the levels the player can reach next to the front.
    /// </summary>
    public Result Take(string surface, int level, out bool reused)
    {
        var key = (surface, level);
        reused = false;
        if (_ready.TryGetValue(key, out var r))
        {
            _ready.Remove(key);
            if (_last.TryGetValue(key, out var old) && old != r && old.texture != null) UnityEngine.Object.Destroy(old.texture);
            _last[key] = r;
        }
        else if (_last.TryGetValue(key, out r)) reused = true;
        Request(surface, level);
        if (r == null) PrioritiseLevel(level);
        return r;
    }

    /// <summary>Move every queued slot of <paramref name="level"/> to the front of the queue.</summary>
    public void PrioritiseLevel(int level)
    {
        if (level < 0 || level > 5) return;
        var moved = new List<(string, int)>();
        for (int i = _queue.Count - 1; i >= 0; i--)
            if (_queue[i].Item2 == level) { moved.Add(_queue[i]); _queue.RemoveAt(i); }
        moved.Reverse();
        _queue.InsertRange(0, moved);
    }

    /// <summary>Advance the background pipeline by one stage. Call once per frame.</summary>
    public void Tick()
    {
        if (_job == null)
        {
            if (_queue.Count == 0) return;
            var (s, l) = _queue[0];
            _queue.RemoveAt(0);
            _job = Begin(s, l);
            if (_job == null) return;
        }
        var j = _job;
        switch (j.stage)
        {
            case 0:   // blit each source and read it back asynchronously (a sync read stalled ~10 ms per source)
                j.rts = new RenderTexture[j.sources.Length];
                j.reads = new AsyncGPUReadbackRequest[j.sources.Length];
                for (int i = 0; i < j.sources.Length; i++)
                {
                    j.rts[i] = RenderTexture.GetTemporary(Size, Size, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                    Graphics.Blit(j.sources[i], j.rts[i]);
                    j.reads[i] = AsyncGPUReadback.Request(j.rts[i], 0, TextureFormat.RGBA32);
                }
                j.stage = 1;
                break;
            case 1:
            {
                // Copy each read the frame it completes: a finished readback's data does not survive for long,
                // so waiting for all four let early ones expire ("GPU readback failed" on ~1 in 6 jobs).
                j.layers ??= new Color32[j.sources.Length][];
                bool pending = false;
                for (int i = 0; i < j.reads.Length; i++)
                {
                    if (j.layers[i] != null) continue;
                    if (!j.reads[i].done) { pending = true; continue; }
                    if (j.reads[i].hasError) { Fail(j, new Exception("GPU readback failed")); return; }
                    j.layers[i] = j.reads[i].GetData<Color32>().ToArray();   // same row order as ReadPixels (verified)
                    RenderTexture.ReleaseTemporary(j.rts[i]);
                    j.rts[i] = null;
                }
                if (pending) return;
                j.rts = null;
                j.task = Task.Run(() => Compose(j));
                j.stage = 2;
                break;
            }
            case 2:
                if (!j.task.IsCompleted) return;
                if (j.task.IsFaulted) { Fail(j, j.task.Exception); return; }
                if (_worker == null) { j.modelOutput = null; j.stage = 4; break; }
                j.input = new Tensor<float>(new TensorShape(1, 3, ModelSize, ModelSize), j.modelInput);
                j.stress = new Tensor<int>(new TensorShape(1), new[] { j.level });
                _worker.Schedule(j.input, j.stress);
                j.output = _worker.PeekOutput() as Tensor<float>;
                j.output.ReadbackRequest();
                j.stage = 3;
                break;
            case 3:
                if (!j.output.IsReadbackRequestDone()) return;
                j.modelOutput = j.output.DownloadToArray();
                DisposeTensors(j);
                j.stage = 4;
                break;
            case 4:   // apply the model's change on a worker thread
                j.task = Task.Run(() => Finish(j));
                j.stage = 5;
                break;
            case 5:
                if (!j.task.IsCompleted) return;
                if (j.task.IsFaulted) { Fail(j, j.task.Exception); return; }
                if (_uploadLevel == null || !_uploadLevel(j.level)) { Complete(j); break; }
                var post = _postProcessFor?.Invoke(j.level);
                var pixels = j.result.pixels;
                j.task = post != null ? Task.Run(() => j.uploadPixels = post(pixels)) : null;
                if (post == null) j.uploadPixels = pixels;
                j.stage = 6;
                break;
            case 6:   // upload on its own frame (~5-20 ms), so the stress change only swaps textures
                if (j.task != null && !j.task.IsCompleted) return;
                if (j.task != null && j.task.IsFaulted) { Fail(j, j.task.Exception); return; }
                var tex = new Texture2D(Size, Size, TextureFormat.RGBA32, true, false)
                {
                    name = $"{j.surface}_L{j.level}_generated", wrapMode = TextureWrapMode.Repeat, anisoLevel = 4,
                };
                tex.SetPixels32(j.uploadPixels);
                tex.Apply(true, true);
                j.result.texture = tex;
                j.uploadPixels = null;
                Complete(j);
                break;
        }
    }

    void Complete(Job j)
    {
        var key = (j.surface, j.level);
        if (_ready.TryGetValue(key, out var stale) && stale.texture != null && !IsLast(stale)) UnityEngine.Object.Destroy(stale.texture);
        _ready[key] = j.result;
        _job = null;
        float seconds = Time.realtimeSinceStartup - j.started;
        AverageSeconds = CompletedCount == 0 ? seconds : Mathf.Lerp(AverageSeconds, seconds, 0.2f);
        CompletedCount++;
    }

    bool IsLast(Result r)
    {
        foreach (var v in _last.Values) if (v == r) return true;
        return false;
    }

    /// <summary>Main thread: choose the four sources (reading them is a separate, asynchronous stage).</summary>
    Job Begin(string surface, int level)
    {
        if (_deck == null || !_deck.TryNext(surface, level, null, out var primary)) return null;
        var j = new Job
        {
            surface = surface, level = level, seed = _rng.Next(), normal = primary.normal, started = Time.realtimeSinceStartup,
        };
        var ids = new List<string> { primary.id };
        var sources = new List<Texture2D> { primary.albedo };
        // Overlays stay in the base's material family; plaster/concrete (no strong pattern) may overlay anything.
        // Mixing freely let one brick layer in four print its courses through almost every wall.
        string baseFamily = SurfaceTextureDeck.Family(primary.id);
        Func<string, bool> fits = id =>
        {
            var f = SurfaceTextureDeck.Family(id);
            return f == baseFamily || SurfaceTextureDeck.IsSoft(f);
        };
        // Iteration 2's tier offsets: mostly the same level, occasionally a neighbour.
        for (int k = 0; k < 3; k++)
        {
            int roll = _rng.Next(24);
            int tier = Mathf.Clamp(level + (roll == 0 ? -1 : roll == 23 ? 1 : 0), 0, 5);
            if (!_deck.Has(surface, tier)) tier = level;
            if (_deck.TryRandom(surface, tier, ids, fits, out var extra)
                || (tier != level && _deck.TryRandom(surface, level, ids, fits, out extra)))
            {
                ids.Add(extra.id);
                sources.Add(extra.albedo);
            }
        }
        j.sources = sources.ToArray();
        j.label = string.Join(" + ", ids) + (_worker != null ? $" -> U-Net L{level}" : " (no U-Net)");
        return j;
    }

    static void Compose(Job j)
    {
        var rng = new System.Random(j.seed);
        int n = Size;
        var acc = new float[n * n * 3];
        for (int li = 0; li < j.layers.Length; li++)
        {
            var src = j.layers[li];
            // The base layer stays put so the normal map that comes with it still lines up; the rest are
            // wrap-shifted and mirrored so each composition is different.
            int ox = li == 0 ? 0 : rng.Next(n), oy = li == 0 ? 0 : rng.Next(n);
            bool flipX = li != 0 && rng.Next(2) == 0, flipY = li != 0 && rng.Next(2) == 0;
            float alpha = li == 0 ? 1f : li == 1 ? rng.Next(68, 101) / 255f : rng.Next(38, 63) / 255f;
            float[] mask = null;
            if (li == 3)
            {
                // Additive layer through soft blobs (iteration 2: 18-28 circles, alpha 20-40).
                mask = new float[n * n];
                int blobs = 18 + rng.Next(11);
                for (int b = 0; b < blobs; b++)
                {
                    int cx = rng.Next(n), cy = rng.Next(n), r = rng.Next(50, n / 2);
                    float a = rng.Next(20, 41) / 255f;
                    for (int y = Math.Max(0, cy - r); y < Math.Min(n, cy + r); y++)
                    for (int x = Math.Max(0, cx - r); x < Math.Min(n, cx + r); x++)
                    {
                        float d = ((x - cx) * (x - cx) + (y - cy) * (y - cy)) / (float)(r * r);
                        if (d < 1f) mask[y * n + x] = Math.Max(mask[y * n + x], a * (1f - d * d));
                    }
                }
            }
            for (int y = 0; y < n; y++)
            {
                int sy = (flipY ? n - 1 - y : y) + oy; if (sy >= n) sy -= n;
                for (int x = 0; x < n; x++)
                {
                    int sx = (flipX ? n - 1 - x : x) + ox; if (sx >= n) sx -= n;
                    var c = src[sy * n + sx];
                    int i = (y * n + x) * 3;
                    if (li == 3)
                    {
                        float m = mask[y * n + x];
                        acc[i] += c.r * m; acc[i + 1] += c.g * m; acc[i + 2] += c.b * m;
                    }
                    else
                    {
                        acc[i] += (c.r - acc[i]) * alpha; acc[i + 1] += (c.g - acc[i + 1]) * alpha; acc[i + 2] += (c.b - acc[i + 2]) * alpha;
                    }
                }
            }
            j.layers[li] = null;
        }
        j.composite = new Color32[n * n];
        for (int i = 0; i < j.composite.Length; i++)
            j.composite[i] = new Color32(B(acc[i * 3]), B(acc[i * 3 + 1]), B(acc[i * 3 + 2]), 255);

        // Model input: 2x2 box-downsampled composite, NCHW, [-1,1], tensor row 0 = image top.
        int m2 = ModelSize, plane = m2 * m2;
        j.modelInput = new float[3 * plane];
        for (int y = 0; y < m2; y++)
        for (int x = 0; x < m2; x++)
        {
            int sx = x * 2, sy = y * 2;
            var a = j.composite[sy * n + sx]; var b = j.composite[sy * n + sx + 1];
            var c = j.composite[(sy + 1) * n + sx]; var d = j.composite[(sy + 1) * n + sx + 1];
            int t = (m2 - 1 - y) * m2 + x;
            j.modelInput[t] = (a.r + b.r + c.r + d.r) / 510f - 1f;
            j.modelInput[plane + t] = (a.g + b.g + c.g + d.g) / 510f - 1f;
            j.modelInput[2 * plane + t] = (a.b + b.b + c.b + d.b) / 510f - 1f;
        }
    }

    /// <summary>Iteration 2's corrupt_surface, with the change applied at the composite's full resolution.</summary>
    static void Finish(Job j)
    {
        int n = Size;
        var outPx = j.composite;
        if (j.modelOutput != null)
        {
            int m2 = ModelSize, plane = m2 * m2;
            var delta = new float[3 * plane];
            var diff = new float[plane];
            for (int t = 0; t < plane; t++)
            {
                float dr = (j.modelOutput[t] - j.modelInput[t]) * 127.5f;
                float dg = (j.modelOutput[plane + t] - j.modelInput[plane + t]) * 127.5f;
                float db = (j.modelOutput[2 * plane + t] - j.modelInput[2 * plane + t]) * 127.5f;
                delta[t] = dr; delta[plane + t] = dg; delta[2 * plane + t] = db;
                diff[t] = (Math.Abs(dr) + Math.Abs(dg) + Math.Abs(db)) / 3f;
            }
            var sorted = (float[])diff.Clone();
            Array.Sort(sorted);
            float thresh = sorted[Mathf.Clamp((int)(plane * (86f - j.level * 1.55f) / 100f), 0, plane - 1)];
            float strength = Mathf.Lerp(0.3f, 0.9f, j.level / 5f);
            float gain = Mathf.Min(0.38f, 0.12f + 0.28f * strength + 0.065f * j.level / 5f);

            outPx = new Color32[n * n];
            for (int y = 0; y < n; y++)
            {
                // composite row y (bottom-up) -> tensor row (top-down), bilinear
                float ty = (m2 - 1) - (y + 0.5f) * m2 / n + 0.5f;
                int y0 = Mathf.Clamp((int)Math.Floor(ty), 0, m2 - 1), y1 = Math.Min(m2 - 1, y0 + 1);
                float fy = Mathf.Clamp01(ty - y0);
                for (int x = 0; x < n; x++)
                {
                    float tx = (x + 0.5f) * m2 / n - 0.5f;
                    int x0 = Mathf.Clamp((int)Math.Floor(tx), 0, m2 - 1), x1 = Math.Min(m2 - 1, x0 + 1);
                    float fx = Mathf.Clamp01(tx - x0);
                    int t00 = y0 * m2 + x0, t01 = y0 * m2 + x1, t10 = y1 * m2 + x0, t11 = y1 * m2 + x1;
                    float Bl(float[] a, int off) => Mathf.Lerp(Mathf.Lerp(a[off + t00], a[off + t01], fx), Mathf.Lerp(a[off + t10], a[off + t11], fx), fy);
                    float crack = Mathf.Lerp(Mathf.Lerp(diff[t00] > thresh ? 1f : 0f, diff[t01] > thresh ? 1f : 0f, fx),
                                             Mathf.Lerp(diff[t10] > thresh ? 1f : 0f, diff[t11] > thresh ? 1f : 0f, fx), fy);
                    var c = j.composite[y * n + x];
                    float r = c.r + Bl(delta, 0) * gain, g = c.g + Bl(delta, plane) * gain, b = c.b + Bl(delta, 2 * plane) * gain;
                    r *= Mathf.Lerp(1f, 0.86f, crack); g *= Mathf.Lerp(1f, 0.84f, crack); b *= Mathf.Lerp(1f, 0.82f, crack);
                    outPx[y * n + x] = new Color32(B(0.94f * r + 0.06f * c.r), B(0.94f * g + 0.06f * c.g), B(0.94f * b + 0.06f * c.b), 255);
                }
            }
        }
        j.result = new Result { pixels = outPx, normal = j.normal, label = j.label };
        j.composite = null;
        j.modelInput = null;
        j.modelOutput = null;
    }

    void Fail(Job j, Exception e)
    {
        Debug.LogWarning($"[TextureGen] {j.surface} L{j.level} failed, retrying later: {e?.GetBaseException().Message}");
        DisposeTensors(j);
        ReleaseRts(j);
        _job = null;
        // Retry at the back of the queue; otherwise the slot is left for a slow synchronous generation.
        var key = (j.surface, j.level);
        if (!_ready.ContainsKey(key) && !_queue.Contains(key)) _queue.Add(key);
    }

    static void ReleaseRts(Job j)
    {
        if (j.rts == null) return;
        foreach (var rt in j.rts) if (rt != null) RenderTexture.ReleaseTemporary(rt);
        j.rts = null;
    }

    static void DisposeTensors(Job j)
    {
        j.input?.Dispose(); j.input = null;
        j.stress?.Dispose(); j.stress = null;
        j.output = null;   // owned by the worker
    }

    static byte B(float v) => (byte)(v < 0f ? 0f : v > 255f ? 255f : v);

    public void Dispose()
    {
        if (_job != null) { DisposeTensors(_job); ReleaseRts(_job); }
        _job = null;
        _queue.Clear();
        var textures = new HashSet<Texture2D>();
        foreach (var r in _ready.Values) if (r?.texture != null) textures.Add(r.texture);
        foreach (var r in _last.Values) if (r?.texture != null) textures.Add(r.texture);
        foreach (var t in textures) HorrorTextureCorruptor.SafeDestroy(t);
        _ready.Clear();
        _last.Clear();
        _worker?.Dispose();
    }
}
