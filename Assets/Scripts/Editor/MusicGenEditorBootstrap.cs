#if UNITY_EDITOR
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// Loads MusicGen (CUDA bridge) and builds a diverse disk cache BEFORE Play Mode.
/// Blocks entering Play until the bridge is healthy and each level has enough clips.
/// </summary>
[InitializeOnLoad]
public static class MusicGenEditorBootstrap
{
    const int Levels = 6;
    const int MinClipsPerLevel = 3;
    const int LibraryTarget = 9;
    const string BridgeUrl = "http://127.0.0.1:8765";
    const string PythonExe = @"C:\Software\miniconda\python.exe";

    static bool _prewarming;
    static bool _enterPlayWhenDone;
    static Process _bridge;

    static string CacheRoot => Path.Combine(Application.persistentDataPath, "MusicGenCache");
    static string ProjectRoot => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
    static string BridgeScript => Path.Combine(ProjectRoot, "fyp iteration 2", "musicgen_unity_bridge.py");

    static MusicGenEditorBootstrap()
    {
        EditorApplication.playModeStateChanged += OnPlayModeChanged;
        EditorApplication.quitting -= StopOurBridge;
        EditorApplication.quitting += StopOurBridge;
        EditorApplication.delayCall += () =>
        {
            EnsureBridgeRunning();
            Debug.Log("[MusicGenEditor] Bridge ensure on editor load. Use FYP → Prewarm MusicGen Now (Before Play).");
        };
    }

    static void OnPlayModeChanged(PlayModeStateChange state)
    {
        // In-game GameStartMenu handles Load Audio → Start Game.
        // Only ensure the CUDA bridge process is up when entering Play.
        if (state == PlayModeStateChange.ExitingEditMode)
            EnsureBridgeRunning();
    }

    [MenuItem("FYP/Prewarm MusicGen Now (Before Play)")]
    public static void MenuPrewarm()
    {
        _enterPlayWhenDone = false;
        RunPrewarmBlocking(enterPlay: false);
    }

    [MenuItem("FYP/Prewarm MusicGen Cache (Enter Play Mode)")]
    public static void MenuPrewarmAndPlay()
    {
        _enterPlayWhenDone = true;
        RunPrewarmBlocking(enterPlay: true);
    }

    [MenuItem("FYP/Clear MusicGen Disk Cache")]
    public static void ClearDiskCache()
    {
        if (Directory.Exists(CacheRoot))
        {
            Directory.Delete(CacheRoot, true);
            Debug.Log("[MusicGenEditor] Cleared " + CacheRoot);
            EditorUtility.DisplayDialog("MusicGen Cache", "Cleared:\n" + CacheRoot, "OK");
        }
        else
        {
            EditorUtility.DisplayDialog("MusicGen Cache", "No cache at:\n" + CacheRoot, "OK");
        }
    }

    static void RunPrewarmBlocking(bool enterPlay)
    {
        if (_prewarming) return;
        _prewarming = true;
        try
        {
            EnsureBridgeRunning();
            if (!WaitForBridge(timeoutSec: 180f, showProgress: true))
            {
                EditorUtility.DisplayDialog(
                    "MusicGen",
                    "CUDA bridge did not become ready.\nStart it manually:\npython musicgen_unity_bridge.py --require-cuda",
                    "OK");
                return;
            }

            var rng = new System.Random(Environment.TickCount ^ Guid.NewGuid().GetHashCode());
            int totalNeed = Levels * LibraryTarget;
            int done = CountLibraryClips();

            for (int level = 0; level < Levels; level++)
            {
                string dir = Path.Combine(CacheRoot, $"L{level}");
                Directory.CreateDirectory(dir);
                int have = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.wav").Length : 0;
                // Always mint at least MinClipsPerLevel brand-new seeds this session for diversity
                int toGenerate = Math.Max(0, MinClipsPerLevel - Math.Min(have, MinClipsPerLevel));
                if (have < LibraryTarget)
                    toGenerate = Math.Max(toGenerate, LibraryTarget - have);
                // Force at least 1 new clip per level every prewarm for variety
                toGenerate = Math.Max(toGenerate, 1);

                for (int n = 0; n < toGenerate; n++)
                {
                    int seed = Math.Abs(rng.Next() ^ (level * 9973) ^ Environment.TickCount);
                    float fright = level / 5f;
                    string label = $"L{level} new clip {n + 1}/{toGenerate} (library {have + n}/{LibraryTarget})";
                    if (EditorUtility.DisplayCancelableProgressBar(
                            "Prewarming MusicGen (before Play)",
                            label,
                            (done + 0.01f) / Math.Max(1, totalNeed)))
                    {
                        EditorUtility.ClearProgressBar();
                        Debug.LogWarning("[MusicGenEditor] Prewarm cancelled.");
                        return;
                    }

                    byte[] wav = GenerateSync(level, fright, fright, seed, timeoutSec: 180);
                    if (wav == null || wav.Length < 64)
                    {
                        Debug.LogWarning($"[MusicGenEditor] generate failed L{level} seed={seed}");
                        continue;
                    }
                    string path = Path.Combine(dir, $"clip_{seed}.wav");
                    File.WriteAllBytes(path, wav);
                    have++;
                    done++;
                    Debug.Log($"[MusicGenEditor] saved {path} ({wav.Length} bytes)");
                }
            }

            EditorUtility.ClearProgressBar();
            EditorUtility.DisplayDialog(
                "MusicGen Ready",
                $"Bridge online (CUDA).\nCache library: {CountLibraryClips()} clips under\n{CacheRoot}\n\n" +
                (enterPlay ? "Entering Play Mode." : "You can press Play."),
                "OK");

            if (enterPlay || _enterPlayWhenDone)
                EditorApplication.isPlaying = true;
        }
        finally
        {
            EditorUtility.ClearProgressBar();
            _prewarming = false;
            _enterPlayWhenDone = false;
        }
    }

    static bool CacheReady(int minPerLevel)
    {
        for (int level = 0; level < Levels; level++)
        {
            string dir = Path.Combine(CacheRoot, $"L{level}");
            if (!Directory.Exists(dir)) return false;
            if (Directory.GetFiles(dir, "*.wav").Length < minPerLevel) return false;
        }
        return true;
    }

    static int CountLibraryClips()
    {
        int n = 0;
        for (int level = 0; level < Levels; level++)
        {
            string dir = Path.Combine(CacheRoot, $"L{level}");
            if (Directory.Exists(dir))
                n += Directory.GetFiles(dir, "*.wav").Length;
        }
        return n;
    }

    const string PidKey = "FYP.MusicGenBridgePid";

    /// <summary>
    /// Start the bridge if nothing healthy is serving port 8765, replacing a stuck one (see MusicGenPython for why the
    /// old launcher left zombies behind and the bridge had to be run from a terminal). Its output goes to
    /// Logs/musicgen_bridge.log. The pid survives domain reloads in SessionState, so the editor can stop the bridge it
    /// started when it quits.
    /// </summary>
    static void EnsureBridgeRunning()
    {
        if (!MusicGenPython.EnsureHealthy(8765, m => Debug.LogWarning("[MusicGenEditor] " + m))) return;
        if (MusicGenPython.PortOpen(8765)) return;   // a healthy bridge (ours, or one started by hand)
        if (_bridge != null && !_bridge.HasExited) return;   // ours, still loading the model
        int oldPid = SessionState.GetInt(PidKey, -1);
        if (oldPid > 0 && IsAlive(oldPid)) return;   // ours from before a domain reload, still loading
        string py = MusicGenPython.Resolve(PythonExe);
        if (!File.Exists(py) || !File.Exists(BridgeScript))
        {
            Debug.LogWarning($"[MusicGenEditor] python or bridge script missing python={py} script={BridgeScript}");
            return;
        }
        try
        {
            _bridge = MusicGenPython.StartBridge(py, BridgeScript, "--port 8765 --max-new-tokens 192 --require-cuda --generate-timeout 180", ProjectRoot);
            if (_bridge != null) SessionState.SetInt(PidKey, _bridge.Id);
            Debug.Log($"[MusicGenEditor] started bridge (pid {_bridge?.Id}); log: {MusicGenPython.LogPath(ProjectRoot)}");
        }
        catch (Exception e)
        {
            Debug.LogWarning("[MusicGenEditor] failed to start bridge: " + e.Message);
        }
    }

    static bool IsAlive(int pid)
    {
        try { return !Process.GetProcessById(pid).HasExited; }
        catch { return false; }
    }

    /// <summary>The editor is closing: stop the bridge it started (it holds ~2 GB of VRAM). A hand-started one is left alone.</summary>
    static void StopOurBridge()
    {
        int pid = SessionState.GetInt(PidKey, -1);
        if (pid > 0 && IsAlive(pid)) MusicGenPython.Kill(pid);
        SessionState.EraseInt(PidKey);
    }

    static bool WaitForBridge(float timeoutSec, bool showProgress)
    {
        float start = Time.realtimeSinceStartup;
        // Editor: use DateTime
        var t0 = DateTime.UtcNow;
        while ((DateTime.UtcNow - t0).TotalSeconds < timeoutSec)
        {
            if (showProgress)
            {
                float u = (float)((DateTime.UtcNow - t0).TotalSeconds / timeoutSec);
                if (EditorUtility.DisplayCancelableProgressBar("MusicGen", "Waiting for CUDA bridge…", u))
                    return false;
            }
            if (HealthOk())
            {
                if (showProgress) EditorUtility.ClearProgressBar();
                return true;
            }
            Thread.Sleep(500);
        }
        if (showProgress) EditorUtility.ClearProgressBar();
        return HealthOk();
    }

    static bool HealthOk()
    {
        try
        {
            var req = (HttpWebRequest)WebRequest.Create(BridgeUrl + "/health");
            req.Timeout = 2500;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var stream = resp.GetResponseStream())
            using (var reader = new StreamReader(stream))
            {
                string body = reader.ReadToEnd();
                return body.IndexOf("\"ready\": true", StringComparison.OrdinalIgnoreCase) >= 0
                       || body.IndexOf("\"ready\":true", StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }
        catch { return false; }
    }

    static byte[] GenerateSync(int level, float intensity, float dissonance, int seed, int timeoutSec)
    {
        try
        {
            string json =
                $"{{\"level\":{level},\"intensity\":{intensity:F4},\"dissonance\":{dissonance:F4},\"seed\":{seed},\"max_new_tokens\":192}}";
            var req = (HttpWebRequest)WebRequest.Create(BridgeUrl + "/generate");
            req.Method = "POST";
            req.ContentType = "application/json";
            req.Timeout = timeoutSec * 1000;
            req.ReadWriteTimeout = timeoutSec * 1000;
            byte[] body = Encoding.UTF8.GetBytes(json);
            req.ContentLength = body.Length;
            using (var s = req.GetRequestStream())
                s.Write(body, 0, body.Length);
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var stream = resp.GetResponseStream())
            using (var ms = new MemoryStream())
            {
                stream.CopyTo(ms);
                return ms.ToArray();
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning("[MusicGenEditor] generate error: " + e.Message);
            return null;
        }
    }

    static bool PortOpen(string host, int port)
    {
        try
        {
            using (var c = new System.Net.Sockets.TcpClient())
            {
                var ar = c.BeginConnect(host, port, null, null);
                bool ok = ar.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(250));
                if (!ok) return false;
                c.EndConnect(ar);
                return true;
            }
        }
        catch { return false; }
    }
}
#endif
