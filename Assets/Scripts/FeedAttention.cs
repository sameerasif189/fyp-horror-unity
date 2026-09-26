using UnityEngine;

/// <summary>
/// Milestone M5: is the player looking at the game rather than at the facecam? Judged from head pose only - webcam gaze
/// trackers are 4-14 degrees out, worse in a dark room and through glasses, while head pose from
/// <see cref="FeedVision"/>'s keypoints is steady (docs/research/m5-photoreal-stalker/04-perception.md, section 4).
///  - The player's usual pose (head turn, tilt, position) is a slow average, so it settles on however they sit
///    while playing; glances away barely move it.
///  - <see cref="LookingAtGame"/> once the pose has stayed near that usual pose for <see cref="SettleSeconds"/>. Effects
///    start only then, so they play in the corner of the eye. A lost face counts as not looking at the game
///    (the player may be looking straight at the facecam).
///  - <see cref="StillAndFrontal"/>: facing the camera and barely moving over the last second - when the time-slip
///    clip is recorded.
/// </summary>
public sealed class FeedAttention
{
    /// <summary>Largest head turn / tilt (eye-distances) away from the usual pose that still counts as "on the game".</summary>
    public static float Tolerance = 0.13f;
    /// <summary>How far (feed heights) the face may sit from its usual place and still count as "on the game".</summary>
    public static float PositionTolerance = 0.07f;
    public static float SettleSeconds = 1.5f;
    const float BaselineSeconds = 20f, StillWindow = 1f;

    Vector4 _usual;   // yaw, pitch, face x, face y
    bool _hasUsual;
    float _onGameSince = -1f;
    readonly Vector4[] _recent = new Vector4[24];
    readonly float[] _recentTime = new float[24];
    int _recentHead;

    public bool LookingAtGame { get; private set; }
    public bool StillAndFrontal { get; private set; }
    /// <summary>Largest of the turn / tilt deviations from the usual pose (for tuning).</summary>
    public float Deviation { get; private set; }

    /// <summary>Main thread, every frame.</summary>
    public void Tick(FeedVision v)
    {
        if (v == null || !v.FaceFound)
        {
            LookingAtGame = StillAndFrontal = false;
            _onGameSince = -1f;
            return;
        }
        var pose = new Vector4(v.Yaw, v.Pitch, v.FaceCenter.x, v.FaceCenter.y);
        if (!_hasUsual) { _usual = pose; _hasUsual = true; }
        _usual = Vector4.Lerp(_usual, pose, 1f - Mathf.Exp(-Time.deltaTime / BaselineSeconds));

        Deviation = Mathf.Max(Mathf.Abs(pose.x - _usual.x), Mathf.Abs(pose.y - _usual.y));
        float moved = Vector2.Distance(new Vector2(pose.z, pose.w), new Vector2(_usual.z, _usual.w));
        bool near = Deviation < Tolerance && moved < PositionTolerance;
        if (!near) _onGameSince = -1f;
        else if (_onGameSince < 0f) _onGameSince = Time.time;
        LookingAtGame = near && Time.time - _onGameSince >= SettleSeconds;

        // Stillness over the last second (face results arrive ~5 times a second; keep every frame's copy).
        _recentHead = (_recentHead + 1) % _recent.Length;
        _recent[_recentHead] = pose;
        _recentTime[_recentHead] = Time.time;
        float spreadPos = 0f, spreadYaw = 0f; int n = 0;
        for (int i = 0; i < _recent.Length; i++)
        {
            if (Time.time - _recentTime[i] > StillWindow) continue;
            n++;
            spreadPos = Mathf.Max(spreadPos, Vector2.Distance(new Vector2(_recent[i].z, _recent[i].w), new Vector2(pose.z, pose.w)));
            spreadYaw = Mathf.Max(spreadYaw, Mathf.Abs(_recent[i].x - pose.x));
        }
        StillAndFrontal = n >= 8 && v.FaceScore > 0.75f && Mathf.Abs(v.Yaw) < 0.16f && spreadPos < 0.015f && spreadYaw < 0.07f;
    }
}
