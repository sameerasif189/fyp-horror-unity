using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

/// <summary>
/// Finds the Python used for musicgen_unity_bridge.py (override with environment variable MUSICGEN_PYTHON), and
/// starts / checks / stops the bridge so the game brings it up itself - no terminal needed.
///
/// The bridge must not write into a pipe owned by Unity. It logs every HTTP request to stderr; when Unity redirected
/// its output and never read it (MusicGenEditorBootstrap), or lost the reader on a domain reload (every script compile
/// and every Play), the pipe filled, the bridge blocked on its next write while still holding port 8765, and every
/// request got an empty reply (curl error 52) - which is why it had to be run by hand. <see cref="StartBridge"/> sends
/// its output to <c>Logs/musicgen_bridge.log</c> through the shell instead, and <see cref="EnsureHealthy"/> replaces a
/// bridge that holds the port but no longer answers.
/// </summary>
public static class MusicGenPython
{
    public const string Host = "127.0.0.1";

    static readonly string[] Candidates =
    {
        @"C:\Software\miniconda\python.exe",
        @"C:\Users\PC\miniconda3\python.exe",
        @"C:\Users\immad\miniconda3\python.exe",
        @"C:\ProgramData\miniconda3\python.exe",
    };

    public static string Resolve(string configured)
    {
        string env = Environment.GetEnvironmentVariable("MUSICGEN_PYTHON");
        if (FileExists(env))
            return env;
        if (FileExists(configured))
            return configured;

        string homeConda = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "miniconda3",
            "python.exe");
        if (FileExists(homeConda))
            return homeConda;

        foreach (string c in Candidates)
        {
            if (FileExists(c))
                return c;
        }

        return configured ?? "";
    }

    static bool FileExists(string path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(path);

    /// <summary>Where the bridge's own output goes (next to Unity's Editor logs).</summary>
    public static string LogPath(string projectRoot) => Path.Combine(projectRoot, "Logs", "musicgen_bridge.log");

    /// <summary>
    /// Start the bridge with its output going to <see cref="LogPath"/>, not to Unity. Returns the shell process that
    /// hosts it (killing it with /T takes the bridge down too), or null.
    /// </summary>
    public static Process StartBridge(string python, string script, string args, string projectRoot)
    {
        string log = LogPath(projectRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(log));
        var psi = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            // cmd /c ""python" "script" args > "log" 2>&1" - the outer quotes are cmd's own.
            Arguments = $"/c \"\"{python}\" \"{script}\" {args} > \"{log}\" 2>&1\"",
            WorkingDirectory = Path.GetDirectoryName(script) ?? projectRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (!psi.Environment.ContainsKey("CUDA_VISIBLE_DEVICES"))
            psi.Environment["CUDA_VISIBLE_DEVICES"] = "0";
        psi.Environment["PYTHONUNBUFFERED"] = "1";
        return Process.Start(psi);
    }

    public static bool PortOpen(int port, int timeoutMs = 250)
    {
        try
        {
            using (var client = new TcpClient())
            {
                var ar = client.BeginConnect(Host, port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(timeoutMs))) return false;
                client.EndConnect(ar);
                return true;
            }
        }
        catch { return false; }
    }

    /// <summary>True if <c>/health</c> answers at all (ready or still loading) within the timeout.</summary>
    public static bool Answers(int port, int timeoutMs = 3000)
    {
        try
        {
            var req = (HttpWebRequest)WebRequest.Create($"http://{Host}:{port}/health");
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.Proxy = null;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var reader = new StreamReader(resp.GetResponseStream()))
                return reader.ReadToEnd().Contains("\"ok\"");
        }
        catch { return false; }
    }

    /// <summary>
    /// If something holds <paramref name="port"/> but does not answer <c>/health</c> (checked twice), kill it so a
    /// fresh bridge can start. Returns true when the port is free or answering afterwards.
    /// </summary>
    public static bool EnsureHealthy(int port, Action<string> log)
    {
        if (!PortOpen(port)) return true;
        if (Answers(port) || Answers(port)) return true;
        int pid = PortOwner(port);
        log?.Invoke($"port {port} is held by pid {pid} but /health does not answer - a stuck bridge; restarting it");
        if (pid > 0) Kill(pid);
        for (int i = 0; i < 20 && PortOpen(port); i++) System.Threading.Thread.Sleep(100);
        return !PortOpen(port);
    }

    /// <summary>The process listening on a local TCP port (from netstat), or -1.</summary>
    public static int PortOwner(int port)
    {
        try
        {
            var psi = new ProcessStartInfo("netstat", "-ano -p TCP")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            };
            using (var p = Process.Start(psi))
            {
                string text = p.StandardOutput.ReadToEnd();   // read to the end, so this pipe never fills
                p.WaitForExit(3000);
                var m = Regex.Match(text, $@"TCP\s+\S+:{port}\s+\S+\s+LISTENING\s+(\d+)");
                return m.Success ? int.Parse(m.Groups[1].Value) : -1;
            }
        }
        catch { return -1; }
    }

    /// <summary>Kill a process and its children (the shell and the bridge it hosts).</summary>
    public static void Kill(int pid)
    {
        try
        {
            using (var p = Process.Start(new ProcessStartInfo("taskkill", $"/PID {pid} /T /F") { UseShellExecute = false, CreateNoWindow = true }))
                p?.WaitForExit(5000);
        }
        catch { }
    }
}
