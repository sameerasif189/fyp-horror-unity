using UnityEngine;

/// <summary>
/// Milestone M5: the one place the webcam is opened. A <see cref="WebCamTexture"/> on the first front-facing device
/// (else the first device), 1280x720 at 30 fps requested. Everything that needs the camera - the facecam now, face /
/// emotion detection later - reads <see cref="Texture"/> instead of opening its own WebCamTexture, because a device
/// generally cannot be opened twice.
/// Privacy: frames stay on the GPU. Nothing here reads them back, stores them or sends them anywhere.
/// </summary>
public class WebcamSource : MonoBehaviour
{
    public static WebcamSource Instance { get; private set; }

    public int requestedWidth = 1280, requestedHeight = 720, requestedFps = 30;

    WebCamTexture _cam;

    public Texture Texture => _cam;
    /// <summary>True once the device delivers real frames (it reports 16x16 until then).</summary>
    public bool IsReady => _cam != null && _cam.isPlaying && _cam.width > 16;
    public bool FlipY => _cam != null && _cam.videoVerticallyMirrored;
    public int Width => _cam != null ? _cam.width : 0;
    public int Height => _cam != null ? _cam.height : 0;
    public string DeviceName { get; private set; }
    public string Status { get; private set; } = "off";

    /// <summary>The running source, created (and the camera opened) on first use.</summary>
    public static WebcamSource Ensure()
    {
        if (Instance != null) return Instance;
        return new GameObject("WebcamSource (runtime)").AddComponent<WebcamSource>();
    }

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }
        Instance = this;
        Open();
    }

    void Open()
    {
        var devices = WebCamTexture.devices;
        if (devices.Length == 0)
        {
            Status = "no camera";
            Debug.LogWarning("[Webcam] no camera found - the facecam shows a no-signal screen.");
            return;
        }
        int pick = 0;
        for (int i = 0; i < devices.Length; i++)
            if (devices[i].isFrontFacing) { pick = i; break; }
        DeviceName = devices[pick].name;
        _cam = new WebCamTexture(DeviceName, requestedWidth, requestedHeight, requestedFps)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
        };
        _cam.Play();
        Status = "starting";
        Debug.Log($"[Webcam] opening '{DeviceName}' ({devices.Length} device(s) found)");
    }

    void Update()
    {
        if (Status == "starting" && IsReady)
        {
            Status = $"{_cam.width}x{_cam.height}";
            Debug.Log($"[Webcam] '{DeviceName}' running at {Status}");
        }
        else if (Status == "starting" && _cam != null && !_cam.isPlaying)
        {
            Status = "unavailable";
            Debug.LogWarning($"[Webcam] '{DeviceName}' did not start (in use by another app, or camera access is off in Windows privacy settings).");
        }
    }

    void OnDestroy()
    {
        if (_cam != null)
        {
            _cam.Stop();
            Destroy(_cam);
            _cam = null;
        }
        if (Instance == this) Instance = null;
    }
}
