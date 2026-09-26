using System;
using System.Globalization;
using Unity.InferenceEngine;
using UnityEngine;

/// <summary>
/// Milestone M5: what the facecam "sees" - the face and the player's silhouette - so effects can be placed relative to
/// the player, kept behind them, and timed for when they are looking at the game. Two small Google MediaPipe models on the GPU (Apache-2.0, in
/// <c>Resources/FeedbackCam/Models</c>):
///  - BlazeFace short range (Unity's Inference Engine port, <c>unity/inference-engine-blaze-face</c>): 128x128
///    letterboxed input, 896 anchors, NMS on the GPU (same graph as Unity's sample); the best face's box and six
///    keypoints (eyes, nose, mouth, ears) come back through async readbacks of a few hundred floats.
///  - Selfie segmentation, landscape (<c>onnx-community/mediapipe_selfie_segmentation_landscape</c>): 256x144 input,
///    person matte rendered straight into <see cref="Mask"/> - never read back to the CPU.
/// The two run alternately at <see cref="Rate"/> each. All positions are feed UVs of the raw (unmirrored) camera
/// image, origin bottom-left - the same space the distortion shader samples in.
/// </summary>
public sealed class FeedVision : IDisposable
{
    public const float Rate = 10f;
    const int FaceInput = 128, NumAnchors = 896, SegWidth = 256, SegHeight = 144;
    const float IouThreshold = 0.3f, ScoreThreshold = 0.5f, FaceTimeout = 0.6f;

    readonly Worker _face, _seg;
    readonly Tensor<float> _faceIn, _segIn;
    readonly float[] _anchors = new float[NumAnchors * 4];
    readonly Material _util;
    readonly RenderTexture _letterbox;
    Tensor<int> _pIdx;
    Tensor<float> _pScores, _pBoxes;
    Vector4 _pMap;
    bool _facePending, _segTurn;
    float _nextRun, _lastFace = -10f;
    TextureTransform _segOutTransform;
    bool _segLayoutChecked;

    public RenderTexture Mask { get; }
    public bool HasMask { get; private set; }
    public bool Available => _face != null && _seg != null;
    public bool FaceFound => Time.unscaledTime - _lastFace < FaceTimeout;
    public float FaceScore { get; private set; }
    public Vector2 FaceCenter { get; private set; }
    public Vector2 FaceSize { get; private set; }
    /// <summary>Right eye, left eye, nose tip, mouth, right ear, left ear (MediaPipe order, the subject's own left/right).</summary>
    public Vector2[] Keypoints { get; } = new Vector2[6];
    /// <summary>Rough head turn: nose offset from the eye midpoint in eye-distances (0 = facing the camera).</summary>
    public float Yaw { get; private set; }
    /// <summary>Rough head tilt: how far the nose sits below the eye line, in eye-distances (grows as the head tips back).
    /// Only meaningful against the player's own usual value (<see cref="FeedAttention"/>).</summary>
    public float Pitch { get; private set; }
    public int FaceRuns { get; private set; }
    public int SegRuns { get; private set; }

    public FeedVision()
    {
        var faceAsset = Resources.Load<ModelAsset>("FeedbackCam/Models/blaze_face_short_range");
        var segAsset = Resources.Load<ModelAsset>("FeedbackCam/Models/selfie_segmentation_landscape");
        var anchors = Resources.Load<TextAsset>("FeedbackCam/Models/blaze_face_anchors");
        _util = new Material(Resources.Load<Shader>("FeedbackCam/FeedUtil"));
        Mask = new RenderTexture(SegWidth, SegHeight, 0, RenderTextureFormat.RHalf) { name = "FeedPersonMask", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
        _letterbox = new RenderTexture(FaceInput, FaceInput, 0, RenderTextureFormat.ARGB32) { name = "FeedFaceInput" };

        if (faceAsset != null && anchors != null)
        {
            var lines = anchors.text.Split('\n');
            for (int i = 0; i < NumAnchors; i++)
            {
                var v = lines[i].Split(',');
                for (int j = 0; j < 4; j++) _anchors[i * 4 + j] = float.Parse(v[j], CultureInfo.InvariantCulture);
            }
            _face = new Worker(BuildFaceGraph(ModelLoader.Load(faceAsset)), BackendType.GPUCompute);
            _faceIn = new Tensor<float>(new TensorShape(1, FaceInput, FaceInput, 3));
        }
        if (segAsset != null)
        {
            _seg = new Worker(ModelLoader.Load(segAsset), BackendType.GPUCompute);
            _segIn = new Tensor<float>(new TensorShape(1, 3, SegHeight, SegWidth));
        }
        if (!Available) Debug.LogWarning("[FeedVision] models missing from Resources/FeedbackCam/Models - the stalker is disabled.");
    }

    /// <summary>Unity's BlazeFace sample graph: [-1,1] input, sigmoid scores, GPU NMS; outputs indices, scores, boxes.</summary>
    Model BuildFaceGraph(Model model)
    {
        var graph = new FunctionalGraph();
        var input = graph.AddInput(model, 0);
        var outputs = Functional.Forward(model, 2 * input - 1);
        var boxes = outputs[0];    // (1, 896, 16)
        var scores = outputs[1];   // (1, 896, 1)
        var anchors = Functional.Constant(new TensorShape(NumAnchors, 4), _anchors);
        var xCenter = boxes[0, .., 0] + anchors[.., 0] * FaceInput;
        var yCenter = boxes[0, .., 1] + anchors[.., 1] * FaceInput;
        var halfW = 0.5f * boxes[0, .., 2];
        var halfH = 0.5f * boxes[0, .., 3];
        var nmsBoxes = Functional.Stack(new[] { yCenter - halfH, xCenter - halfW, yCenter + halfH, xCenter + halfW }, 1);
        var nmsScores = Functional.Squeeze(Functional.Sigmoid(Functional.Clamp(scores, -100f, 100f)));
        var selected = Functional.NMS(nmsBoxes, nmsScores, IouThreshold, ScoreThreshold);
        var selectedBoxes = Functional.IndexSelect(boxes, 1, selected).Unsqueeze(0);
        var selectedScores = Functional.IndexSelect(scores, 1, selected).Unsqueeze(0);
        return graph.Compile(selected, selectedScores, selectedBoxes);
    }

    /// <summary>Main thread, every frame.</summary>
    public void Tick(Texture source)
    {
        if (!Available || source == null) return;
        CollectFace();
        if (Time.unscaledTime < _nextRun) return;
        _nextRun = Time.unscaledTime + 0.5f / Rate;
        _segTurn = !_segTurn;
        if (_segTurn) RunSegmentation(source);
        else if (!_facePending) RunFace(source);
    }

    void RunSegmentation(Texture source)
    {
        TextureConverter.ToTensor(source, _segIn);
        _seg.Schedule(_segIn);
        var alpha = _seg.PeekOutput() as Tensor<float>;
        if (!_segLayoutChecked)
        {
            // The matte comes out as (1,1,H,W) or (1,H,W,1) depending on the export.
            var s = alpha.shape;
            _segOutTransform = s.rank == 4 && s[3] == 1 && s[1] != 1 ? new TextureTransform().SetTensorLayout(TensorLayout.NHWC) : default;
            _segLayoutChecked = true;
            Debug.Log($"[FeedVision] segmentation output {s}");
        }
        TextureConverter.RenderToTexture(alpha, Mask, _segOutTransform);
        HasMask = true;
        SegRuns++;
    }

    void RunFace(Texture source)
    {
        // Letterbox to a square; _pMap maps letterbox uv -> feed uv (uv * xy + zw) for decoding.
        float aspect = (float)source.width / Mathf.Max(1, source.height);
        _pMap = aspect >= 1f ? new Vector4(1f, aspect, 0f, -(aspect - 1f) * 0.5f) : new Vector4(1f / aspect, 1f, -(1f / aspect - 1f) * 0.5f, 0f);
        _util.SetVector("_ScaleOffset", _pMap);
        Graphics.Blit(source, _letterbox, _util, 0);
        TextureConverter.ToTensor(_letterbox, _faceIn, new TextureTransform().SetTensorLayout(TensorLayout.NHWC));
        _face.Schedule(_faceIn);
        _pIdx = _face.PeekOutput(0) as Tensor<int>;
        _pScores = _face.PeekOutput(1) as Tensor<float>;
        _pBoxes = _face.PeekOutput(2) as Tensor<float>;
        _pIdx.ReadbackRequest();
        _pScores.ReadbackRequest();
        _pBoxes.ReadbackRequest();
        _facePending = true;
        FaceRuns++;
    }

    void CollectFace()
    {
        if (!_facePending || !_pIdx.IsReadbackRequestDone() || !_pScores.IsReadbackRequestDone() || !_pBoxes.IsReadbackRequestDone()) return;
        _facePending = false;
        var idx = _pIdx.DownloadToArray();
        var scores = _pScores.DownloadToArray();
        var boxes = _pBoxes.DownloadToArray();
        if (idx.Length == 0) return;
        int best = 0;
        for (int i = 1; i < idx.Length; i++) if (scores[i] > scores[best]) best = i;
        int a = idx[best], o = best * 16;
        float ax = _anchors[a * 4] * FaceInput, ay = _anchors[a * 4 + 1] * FaceInput;
        FaceCenter = ToFeed(ax + boxes[o], ay + boxes[o + 1]);
        FaceSize = new Vector2(boxes[o + 2] / FaceInput * _pMap.x, boxes[o + 3] / FaceInput * _pMap.y);
        for (int k = 0; k < 6; k++) Keypoints[k] = ToFeed(ax + boxes[o + 4 + 2 * k], ay + boxes[o + 5 + 2 * k]);
        FaceScore = 1f / (1f + Mathf.Exp(-scores[best]));
        var eyes = (Keypoints[0] + Keypoints[1]) * 0.5f;
        float eyeDist = Mathf.Max(1e-4f, Vector2.Distance(Keypoints[0], Keypoints[1]));
        Yaw = (Keypoints[2].x - eyes.x) / eyeDist;
        Pitch = (eyes.y - Keypoints[2].y) / eyeDist;
        _lastFace = Time.unscaledTime;
    }

    /// <summary>Letterbox tensor pixel (y down) -> feed uv (origin bottom-left).</summary>
    Vector2 ToFeed(float x, float y) =>
        new Vector2(x / FaceInput * _pMap.x + _pMap.z, (1f - y / FaceInput) * _pMap.y + _pMap.w);

    public void Dispose()
    {
        _face?.Dispose();
        _seg?.Dispose();
        _faceIn?.Dispose();
        _segIn?.Dispose();
        if (Mask != null) { Mask.Release(); UnityEngine.Object.Destroy(Mask); }
        if (_letterbox != null) { _letterbox.Release(); UnityEngine.Object.Destroy(_letterbox); }
        if (_util != null) UnityEngine.Object.Destroy(_util);
    }
}
