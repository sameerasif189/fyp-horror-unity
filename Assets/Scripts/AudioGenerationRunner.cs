using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using Debug = UnityEngine.Debug;

/// <summary>
/// Horror audio bed: procedural immediately, MusicGen (HTTP bridge) when ready.
/// WaveGAN ONNX is no longer the primary path.
/// </summary>
[RequireComponent(typeof(AudioSource))]
public class AudioGenerationRunner : MonoBehaviour
{
    [Header("MusicGen bridge")]
    [SerializeField] string bridgeUrl = "http://127.0.0.1:8765";
    [SerializeField] bool autoStartBridge = true;
    [SerializeField] string pythonExe = @"C:\Users\PC\miniconda3\python.exe";
    [SerializeField] string bridgeScriptRelative = @"fyp iteration 2\musicgen_unity_bridge.py";
    [SerializeField] float musicGenTimeoutSec = 180f;
    [SerializeField] int maxNewTokens = 192;
    [SerializeField] bool requireCuda = true;

    [Header("Playback")]
    [SerializeField] float bedVolume = 0.55f;
    [SerializeField] float proceduralVolumeScale = 0.75f;
    [SerializeField] float crossfadeSec = 1.25f;
    [SerializeField] float regenerateInterval = 28f;
    [SerializeField] bool showStatusHud = true;

    AudioSource _a;
    AudioSource _b;
    bool _aIsActive = true;
    int _lastLevel = -1;
    int _requestId;
    FusionDirector.FusionParams _params;
    string _status = "Audio idle";
    string _lastSource = "none";
    string _deviceLabel = "unknown";
    bool _generating;
    bool _bridgeHealthy;
    Process _bridgeProcess;
    Coroutine _genRoutine;
    Coroutine _fadeRoutine;

    AudioSource Active => _aIsActive ? _a : _b;
    AudioSource Inactive => _aIsActive ? _b : _a;

    public bool IsGenerating => _generating;
    public string StatusText => _status;
    public string LastSource => _lastSource;

    void Awake()
    {
        var sources = GetComponents<AudioSource>();
        _a = sources[0];
        _a.loop = true;
        _a.playOnAwake = false;
        _a.spatialBlend = 0f;

        _b = gameObject.AddComponent<AudioSource>();
        _b.loop = true;
        _b.playOnAwake = false;
        _b.spatialBlend = 0f;
    }

    void OnEnable()
    {
        if (autoStartBridge)
            EnsureBridgeRunning();
        TrySubscribe();
        int level = StressController.Instance != null ? StressController.Instance.StressLevel : 0;
        OnStress(level);
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
        StartCoroutine(HealthPollLoop());
    }

    void OnDisable()
    {
        if (StressController.Instance != null)
            StressController.Instance.OnStressChanged -= OnStress;
        if (FusionDirector.Instance != null)
            FusionDirector.Instance.OnParamsChanged -= OnFusion;
        if (_genRoutine != null)
            StopCoroutine(_genRoutine);
    }

    void OnDestroy()
    {
        // Leave bridge running across play-mode stop so reload is fast; kill only if we started it.
        // (Optional hard-stop:) StopBridge();
    }

    void TrySubscribe()
    {
        if (StressController.Instance == null) return;
        StressController.Instance.OnStressChanged -= OnStress;
        StressController.Instance.OnStressChanged += OnStress;
    }

    void OnStress(int level)
    {
        level = Mathf.Clamp(level, 0, 5);
        _lastLevel = level;
        RequestBed(level, force: true);
    }

    void OnFusion(FusionDirector.FusionParams p)
    {
        _params = p;
        ApplyDsp();
    }

    float _nextRegen;

    void Update()
    {
        if (_lastLevel < 0 || _generating) return;
        if (Time.time >= _nextRegen)
        {
            _nextRegen = Time.time + regenerateInterval;
            // Refresh MusicGen bed periodically at mid/high stress
            if (_lastLevel >= 2)
                RequestBed(_lastLevel, force: false);
        }
    }

    void RequestBed(int level, bool force)
    {
        if (_genRoutine != null)
            StopCoroutine(_genRoutine);
        _genRoutine = StartCoroutine(GenerateBedRoutine(level, force));
    }

    IEnumerator GenerateBedRoutine(int level, bool force)
    {
        int myId = ++_requestId;
        _generating = true;

        float intensity = _params != null ? _params.audio_intensity : level / 5f;
        float dissonance = _params != null ? _params.audio_dissonance : level / 5f;
        int seed = UnityEngine.Random.Range(1, 999999);

        // 1) Immediate procedural bed so something always plays
        _status = $"Procedural bed L{level} (waiting MusicGen...)";
        Debug.Log($"[AudioGen] procedural fallback start L{level}");
        var proc = ProceduralHorrorAudio.CreateBed(level, intensity, dissonance, seed, durationSec: 4.5f);
        CrossfadeTo(proc, bedVolume * proceduralVolumeScale);
        _lastSource = "procedural";
        ApplyDsp();

        // 2) Wait until bridge is ready (MusicGen load can take minutes on first start)
        float waitReadyUntil = Time.realtimeSinceStartup + Mathf.Max(30f, musicGenTimeoutSec);
        while (!_bridgeHealthy && Time.realtimeSinceStartup < waitReadyUntil)
        {
            if (myId != _requestId) yield break;
            _status = $"Waiting for MusicGen bridge... (procedural L{level})";
            yield return ProbeHealth();
            if (!_bridgeHealthy)
                yield return new WaitForSecondsRealtime(2f);
        }

        if (!_bridgeHealthy)
        {
            _status = $"MusicGen offline - using procedural L{level}";
            Debug.LogWarning("[AudioGen] bridge not healthy; staying on procedural.");
            _generating = false;
            _nextRegen = Time.time + regenerateInterval;
            yield break;
        }

        _status = $"Generating MusicGen L{level}...";
        string url = bridgeUrl.TrimEnd('/') + "/generate";
        string json = $"{{\"level\":{level},\"intensity\":{intensity:F4},\"dissonance\":{dissonance:F4},\"seed\":{seed},\"max_new_tokens\":{maxNewTokens}}}";

        using (var req = new UnityWebRequest(url, "POST"))
        {
            byte[] body = Encoding.UTF8.GetBytes(json);
            req.uploadHandler = new UploadHandlerRaw(body);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            req.timeout = Mathf.CeilToInt(musicGenTimeoutSec);

            var op = req.SendWebRequest();
            float start = Time.realtimeSinceStartup;
            while (!op.isDone)
            {
                if (myId != _requestId)
                    yield break; // superseded by newer stress change
                float elapsed = Time.realtimeSinceStartup - start;
                _status = $"Generating MusicGen L{level}... {elapsed:0.0}s";
                yield return null;
            }

            if (myId != _requestId)
                yield break;

            if (req.result != UnityWebRequest.Result.Success)
            {
                _status = $"MusicGen failed - procedural L{level}";
                Debug.LogWarning("[AudioGen] MusicGen request failed: " + req.error);
                _generating = false;
                _nextRegen = Time.time + regenerateInterval;
                yield break;
            }

            string source = req.GetResponseHeader("X-Audio-Source") ?? "musicgen";
            string deviceHdr = req.GetResponseHeader("X-Device");
            if (!string.IsNullOrEmpty(deviceHdr))
                _deviceLabel = deviceHdr;
            string gpuHdr = req.GetResponseHeader("X-GPU-Name");
            if (!string.IsNullOrEmpty(gpuHdr))
                _deviceLabel = deviceHdr + " (" + gpuHdr + ")";
            byte[] wav = req.downloadHandler.data;
            if (wav == null || wav.Length < 64)
            {
                _status = $"Empty MusicGen response - procedural L{level}";
                _generating = false;
                yield break;
            }

            AudioClip clip = WavUtility.ToAudioClip(wav, $"MusicGen_L{level}_{seed}");
            if (clip == null)
            {
                _status = $"WAV decode failed - procedural L{level}";
                _generating = false;
                yield break;
            }

            // Soft fusion grit on MusicGen bed
            if (_params != null && _params.dsp_distortion > 0.08f)
                clip = ApplySoftDistortion(clip, _params.dsp_distortion);

            CrossfadeTo(clip, bedVolume);
            _lastSource = source;
            _status = $"Playing {source} L{level} ({clip.length:0.0}s)";
            Debug.Log($"[AudioGen] swapped to {source} L{level} len={clip.length:0.00}s force={force}");
            ApplyDsp();
        }

        _generating = false;
        _nextRegen = Time.time + regenerateInterval;
        _genRoutine = null;
    }

    void CrossfadeTo(AudioClip clip, float targetVolume)
    {
        if (clip == null) return;
        var next = Inactive;
        var cur = Active;
        next.clip = clip;
        next.volume = 0f;
        next.loop = true;
        next.Play();
        if (_fadeRoutine != null)
            StopCoroutine(_fadeRoutine);
        _fadeRoutine = StartCoroutine(CrossfadeRoutine(cur, next, targetVolume));
        _aIsActive = !_aIsActive;
    }

    IEnumerator CrossfadeRoutine(AudioSource from, AudioSource to, float targetVol)
    {
        float t = 0f;
        float fromStart = from != null && from.isPlaying ? from.volume : 0f;
        while (t < crossfadeSec)
        {
            t += Time.deltaTime;
            float u = Mathf.Clamp01(t / Mathf.Max(0.01f, crossfadeSec));
            if (to != null) to.volume = Mathf.Lerp(0f, targetVol, u);
            if (from != null) from.volume = Mathf.Lerp(fromStart, 0f, u);
            yield return null;
        }
        if (from != null)
        {
            from.Stop();
            from.clip = null;
            from.volume = 0f;
        }
        if (to != null) to.volume = targetVol;
    }

    void ApplyDsp()
    {
        if (_params == null) return;
        float intensity = Mathf.Clamp01(_params.audio_intensity);
        float pitchShift = _params.dsp_pitch_shift;
        float pitchDrift = _params.audio_pitch_drift;
        float pitch = Mathf.Clamp(1f + (pitchShift - 0.5f) * 0.25f + pitchDrift * 0.12f, 0.75f, 1.35f);
        float vol = Mathf.Clamp01(bedVolume * (0.45f + intensity * 0.7f));
        if (_lastSource == "procedural")
            vol *= proceduralVolumeScale;

        var act = Active;
        if (act != null && act.isPlaying)
        {
            act.pitch = pitch;
            // Don't fight an in-progress crossfade hard — nudge toward target
            act.volume = Mathf.Lerp(act.volume, vol, 0.35f);
        }
    }

    static AudioClip ApplySoftDistortion(AudioClip src, float amount)
    {
        int n = src.samples * src.channels;
        float[] data = new float[n];
        src.GetData(data, 0);
        float gain = 1f + Mathf.Clamp01(amount) * 6f;
        float norm = 1f / (float)Math.Tanh(gain);
        for (int i = 0; i < data.Length; i++)
            data[i] = (float)Math.Tanh(data[i] * gain) * norm;
        var clip = AudioClip.Create(src.name + "_fx", src.samples, src.channels, src.frequency, false);
        clip.SetData(data, 0);
        return clip;
    }

    IEnumerator HealthPollLoop()
    {
        var wait = new WaitForSecondsRealtime(4f);
        while (enabled)
        {
            yield return ProbeHealth();
            yield return wait;
        }
    }

    IEnumerator ProbeHealth()
    {
        string url = bridgeUrl.TrimEnd('/') + "/health";
        using (var req = UnityWebRequest.Get(url))
        {
            req.timeout = 3;
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success)
            {
                _bridgeHealthy = false;
                yield break;
            }
            // Prefer ready=true once MusicGen finished loading
            string body = req.downloadHandler.text ?? "";
            bool ready = body.IndexOf("\"ready\": true", StringComparison.OrdinalIgnoreCase) >= 0
                         || body.IndexOf("\"ready\":true", StringComparison.OrdinalIgnoreCase) >= 0;
            bool musicgen = body.IndexOf("\"musicgen\": true", StringComparison.OrdinalIgnoreCase) >= 0
                            || body.IndexOf("\"musicgen\":true", StringComparison.OrdinalIgnoreCase) >= 0;
            bool onCuda = body.IndexOf("\"device\": \"cuda\"", StringComparison.OrdinalIgnoreCase) >= 0
                          || body.IndexOf("\"device\":\"cuda\"", StringComparison.OrdinalIgnoreCase) >= 0;
            _bridgeHealthy = ready;
            if (onCuda)
                _deviceLabel = ExtractJsonString(body, "gpu_name") ?? "cuda";
            else if (ready)
                _deviceLabel = ExtractJsonString(body, "device") ?? "cpu";

            if (_bridgeHealthy && musicgen && onCuda && !_generating
                && (_status.Contains("offline") || _status.Contains("Starting") || _status.Contains("idle")))
                _status = "MusicGen CUDA ready";
            else if (_bridgeHealthy && musicgen && !onCuda && !_generating)
                _status = "MusicGen on CPU (want CUDA)";
            else if (_bridgeHealthy && !musicgen && !_generating)
                _status = "Bridge online (procedural only)";
        }
    }

    static string ExtractJsonString(string json, string key)
    {
        string needle = "\"" + key + "\":";
        int i = json.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        i += needle.Length;
        while (i < json.Length && (json[i] == ' ' || json[i] == '\t')) i++;
        if (i >= json.Length || json[i] != '"') return null;
        i++;
        int end = json.IndexOf('"', i);
        if (end < 0) return null;
        string v = json.Substring(i, end - i);
        return string.IsNullOrEmpty(v) || v == "null" ? null : v;
    }

    void EnsureBridgeRunning()
    {
        try
        {
            // Already up?
            if (IsPortOpen("127.0.0.1", 8765))
            {
                _bridgeHealthy = true;
                _status = "MusicGen bridge online";
                return;
            }

            if (_bridgeProcess != null && !_bridgeProcess.HasExited)
                return;

            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string script = Path.Combine(projectRoot, bridgeScriptRelative);
            if (!File.Exists(script))
            {
                Debug.LogWarning("[AudioGen] bridge script missing: " + script);
                return;
            }
            if (!File.Exists(pythonExe))
            {
                Debug.LogWarning("[AudioGen] python not found: " + pythonExe);
                return;
            }

            var psi = new ProcessStartInfo
            {
                FileName = pythonExe,
                Arguments = $"\"{script}\" --port 8765 --max-new-tokens {maxNewTokens}"
                            + (requireCuda ? " --require-cuda" : " --no-require-cuda")
                            + $" --generate-timeout {musicGenTimeoutSec:0}",
                WorkingDirectory = Path.GetDirectoryName(script) ?? projectRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            // Prefer discrete GPU 0 the same way iteration-2 does when CUDA is present.
            if (!psi.Environment.ContainsKey("CUDA_VISIBLE_DEVICES"))
                psi.Environment["CUDA_VISIBLE_DEVICES"] = "0";
            _bridgeProcess = Process.Start(psi);
            if (_bridgeProcess != null)
            {
                _bridgeProcess.OutputDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) Debug.Log("[MusicGenBridge] " + e.Data); };
                _bridgeProcess.ErrorDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) Debug.Log("[MusicGenBridge] " + e.Data); };
                _bridgeProcess.BeginOutputReadLine();
                _bridgeProcess.BeginErrorReadLine();
                _status = "Starting MusicGen bridge...";
                Debug.Log("[AudioGen] started MusicGen bridge pid=" + _bridgeProcess.Id);
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning("[AudioGen] failed to start bridge: " + e.Message);
        }
    }

    static bool IsPortOpen(string host, int port)
    {
        try
        {
            using (var client = new System.Net.Sockets.TcpClient())
            {
                var ar = client.BeginConnect(host, port, null, null);
                bool ok = ar.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(250));
                if (!ok) return false;
                client.EndConnect(ar);
                return true;
            }
        }
        catch
        {
            return false;
        }
    }

    void OnGUI()
    {
        if (!showStatusHud) return;
        var style = new GUIStyle(GUI.skin.box)
        {
            fontSize = 14,
            alignment = TextAnchor.MiddleLeft,
            padding = new RectOffset(10, 10, 6, 6)
        };
        string bridge = _bridgeHealthy ? "online" : "offline";
        GUI.Box(new Rect(16, 72, 460, 48),
            $"Audio: {_status}\nBridge {bridge} · {_deviceLabel} · source={_lastSource}", style);
    }
}

/// <summary>Minimal PCM16 mono/stereo WAV decoder for MusicGen bridge responses.</summary>
static class WavUtility
{
    public static AudioClip ToAudioClip(byte[] wav, string name)
    {
        try
        {
            if (wav == null || wav.Length < 44) return null;
            if (wav[0] != 'R' || wav[1] != 'I' || wav[2] != 'F' || wav[3] != 'F') return null;

            int channels = BitConverter.ToInt16(wav, 22);
            int sampleRate = BitConverter.ToInt32(wav, 24);
            int bits = BitConverter.ToInt16(wav, 34);

            int dataOffset = 12;
            int dataSize = 0;
            while (dataOffset + 8 <= wav.Length)
            {
                string chunkId = Encoding.ASCII.GetString(wav, dataOffset, 4);
                int chunkSize = BitConverter.ToInt32(wav, dataOffset + 4);
                if (chunkId == "data")
                {
                    dataOffset += 8;
                    dataSize = chunkSize;
                    break;
                }
                dataOffset += 8 + chunkSize;
            }
            if (dataSize <= 0) return null;

            int sampleCount = dataSize / (bits / 8);
            int frames = sampleCount / Mathf.Max(1, channels);
            float[] samples = new float[frames]; // mono downmix

            if (bits == 16)
            {
                int idx = dataOffset;
                for (int f = 0; f < frames; f++)
                {
                    float sum = 0f;
                    for (int c = 0; c < channels; c++)
                    {
                        short s = BitConverter.ToInt16(wav, idx);
                        idx += 2;
                        sum += s / 32768f;
                    }
                    samples[f] = sum / channels;
                }
            }
            else if (bits == 32)
            {
                // float32
                int idx = dataOffset;
                for (int f = 0; f < frames; f++)
                {
                    float sum = 0f;
                    for (int c = 0; c < channels; c++)
                    {
                        float s = BitConverter.ToSingle(wav, idx);
                        idx += 4;
                        sum += s;
                    }
                    samples[f] = sum / channels;
                }
            }
            else return null;

            var clip = AudioClip.Create(name, frames, 1, sampleRate, false);
            clip.SetData(samples, 0);
            return clip;
        }
        catch (Exception e)
        {
            Debug.LogWarning("[WavUtility] " + e.Message);
            return null;
        }
    }
}
