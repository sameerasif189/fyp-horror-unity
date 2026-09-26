using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Milestone M5: a coarse map of the room behind the player, for placing the facecam's room effects where they can
/// be believed. Twice a second the feed and <see cref="FeedVision.Mask"/> are averaged down to 32x18 on the GPU
/// (<c>FeedUtil</c> pass 1) and read back asynchronously: brightness per patch, and whether the player is in it.
/// <see cref="FindSpot"/> picks where a figure's head could be:
///  - empty background, clear of the player and away from their face;
///  - at or above the player's eye line, where the far side of the room usually is (no depth sensor: a figure
///    "standing" on the pillow right behind the player's head would be impossible);
///  - dim but not black - darkening only shows where there is some light to take away;
///  - with enough background like the head's around it: the shader hides a figure wherever the background is much
///    brighter than at its head (a pillow or chair nearer the camera), a stand-in for depth.
/// </summary>
public sealed class FeedScene : IDisposable
{
    public const int W = 32, H = 18;
    const float Interval = 0.5f;

    readonly Material _util;
    readonly RenderTexture _probe;
    readonly float[] _lum = new float[W * H], _person = new float[W * H];
    readonly List<(float score, Vector2 pos)> _candidates = new List<(float, Vector2)>();
    bool _pending;
    float _next;

    public bool Ready { get; private set; }
    /// <summary>The 32x18 map itself (r = brightness, g = player), for the shader.</summary>
    public Texture Probe => _probe;
    public int Probes { get; private set; }

    public FeedScene()
    {
        _util = new Material(Resources.Load<Shader>("FeedbackCam/FeedUtil"));
        // Linear, so the shader reading it (FeedDistortion's figure occlusion) and the CPU readback see the same numbers.
        _probe = new RenderTexture(W, H, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear) { name = "FeedSceneProbe", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
    }

    /// <summary>Main thread, every frame.</summary>
    public void Tick(Texture source, FeedVision vision)
    {
        if (_pending || source == null || vision == null || !vision.HasMask || Time.unscaledTime < _next) return;
        _next = Time.unscaledTime + Interval;
        _util.SetTexture("_Mask", vision.Mask);
        _util.SetVector("_Cell", new Vector4(1f / W, 1f / H, 0f, 0f));
        Graphics.Blit(source, _probe, _util, 1);
        _pending = true;
        AsyncGPUReadback.Request(_probe, 0, TextureFormat.RGBA32, OnReadback);
    }

    void OnReadback(AsyncGPUReadbackRequest r)
    {
        _pending = false;
        if (r.hasError) return;
        NativeArray<Color32> px = r.GetData<Color32>();
        for (int i = 0; i < px.Length && i < _lum.Length; i++)
        {
            _lum[i] = px[i].r / 255f;
            _person[i] = px[i].g / 255f;
        }
        Ready = true;
        Probes++;
    }

    /// <summary>
    /// A head position (feed uv) for a figure with head radius <paramref name="headR"/> (feed heights). With
    /// <paramref name="body"/> the shoulders must be clear of the player too. False if nowhere fits.
    /// </summary>
    public bool FindSpot(FeedVision v, float aspect, float headR, bool body, out Vector2 head, Predicate<Vector2> reject = null)
    {
        head = default;
        if (!Ready) return false;

        // What "dim" means in this room: a little above the darkest quarter of the empty background.
        var bg = new List<float>(W * H);
        for (int i = 0; i < _lum.Length; i++) if (_person[i] < 0.2f) bg.Add(_lum[i]);
        if (bg.Count < 20) return false;
        bg.Sort();
        float target = Mathf.Lerp(bg[bg.Count / 4], bg[bg.Count * 3 / 5], 0.35f);
        float floorLum = bg[bg.Count / 10] * 1.5f + 0.002f;   // relative: the map is linear light, so "near black" is tiny

        float rx = headR / Mathf.Max(0.1f, aspect);
        float halfW = (body ? 2.3f : 1.1f) * rx;
        Vector2 face = v.FaceFound ? v.FaceCenter : new Vector2(0.5f, 0.45f);
        float faceR = v.FaceFound ? Mathf.Max(v.FaceSize.y, v.FaceSize.x * aspect) : 0.4f;

        _candidates.Clear();
        for (int cy = 0; cy < H; cy++)
        for (int cx = 0; cx < W; cx++)
        {
            var c = new Vector2((cx + 0.5f) / W, (cy + 0.5f) / H);
            if (c.x < 0.04f || c.x > 0.96f || c.y < 0.2f || c.y > 0.93f) continue;
            if (reject != null && reject(c)) continue;
            if (v.FaceFound && c.y < face.y - 0.04f) continue;
            float fd = Vector2.Distance(new Vector2(c.x * aspect, c.y), new Vector2(face.x * aspect, face.y));
            if (fd < faceR * 1.25f) continue;

            // The head must be clear of the player and dim-but-lit.
            float headLum = Mean(c, rx, headR, out float headPerson);
            if (headPerson > 0.25f || headLum < floorLum) continue;
            // Only background like the head's (same wall or curtain) shows the figure - FeedDistortion lets brighter,
            // nearer things such as a pillow hide it - so there must be enough of it around the head and shoulders.
            float refL = Mathf.Sqrt(headLum), similar = 0f; int n = 0;
            ForCells(c.x - halfW, c.x + halfW, c.y - (body ? 2.2f : 1.1f) * headR, c.y + 1.1f * headR, i =>
            {
                n++;
                if (_person[i] < 0.3f && Mathf.Abs(Mathf.Sqrt(_lum[i]) - refL) < 0.08f) similar++;
            });
            if (n == 0 || similar / n < 0.4f) continue;
            float score = Mathf.Abs(headLum - target) + 0.1f * (1f - similar / n) + 0.15f * Mathf.Max(0f, 0.3f - fd);
            _candidates.Add((score, c));
        }
        if (_candidates.Count == 0) return false;
        _candidates.Sort((a, b) => a.score.CompareTo(b.score));
        head = _candidates[UnityEngine.Random.Range(0, Mathf.Min(4, _candidates.Count))].pos;
        return true;
    }

    /// <summary>Mean brightness of the empty background within a head-sized patch at <paramref name="c"/> (feed uv).</summary>
    public float LumAt(Vector2 c, float headR, float aspect) => Mean(c, headR / Mathf.Max(0.1f, aspect), headR, out _);

    float Mean(Vector2 c, float rx, float ry, out float maxPerson)
    {
        float sum = 0f, mp = 0f; int n = 0;
        ForCells(c.x - rx, c.x + rx, c.y - ry, c.y + ry, i => { mp = Mathf.Max(mp, _person[i]); if (_person[i] < 0.3f) { sum += _lum[i]; n++; } });
        maxPerson = mp;
        return n > 0 ? sum / n : 0f;
    }

    void ForCells(float x0, float x1, float y0, float y1, Action<int> f)
    {
        int ax = Mathf.Max(0, Mathf.FloorToInt(x0 * W)), bx = Mathf.Min(W, Mathf.CeilToInt(x1 * W));
        int ay = Mathf.Max(0, Mathf.FloorToInt(y0 * H)), by = Mathf.Min(H, Mathf.CeilToInt(y1 * H));
        for (int y = ay; y < by; y++) for (int x = ax; x < bx; x++) f(y * W + x);
    }

    /// <summary>
    /// Head height (feed uv y) for a shadow of head radius <paramref name="headR"/> crossing the frame: the band with the
    /// most lit, empty background, since a shadow only shows on something the light reaches. False before the first probe.
    /// </summary>
    public bool FindShadowBand(float headR, out float y)
    {
        y = 0.6f;
        if (!Ready) return false;
        float best = -1f;
        for (int cy = Mathf.CeilToInt(0.3f * H); cy <= Mathf.FloorToInt(0.85f * H); cy++)
        {
            float c = (cy + 0.5f) / H, sum = 0f; int n = 0, total = 0;
            int y0 = Mathf.Max(0, Mathf.FloorToInt((c - 2.5f * headR) * H)), y1 = Mathf.Min(H, Mathf.CeilToInt((c + headR) * H));
            for (int row = y0; row < y1; row++)
            for (int x = 0; x < W; x++)
            {
                total++;
                if (_person[row * W + x] > 0.3f) continue;
                sum += _lum[row * W + x];
                n++;
            }
            if (n == 0) continue;
            float score = sum / total;   // mean brightness of the empty background, weighted by how much of the band it is
            if (score > best) { best = score; y = c; }
        }
        return best >= 0f;
    }

    public void Dispose()
    {
        if (_probe != null) { _probe.Release(); UnityEngine.Object.Destroy(_probe); }
        if (_util != null) UnityEngine.Object.Destroy(_util);
    }
}
