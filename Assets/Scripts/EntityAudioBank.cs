using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>What a monster sound is for; <see cref="MonsterVoice"/> picks one per monster state.</summary>
public enum EntityCue { Breath, Movement, Alert, Chase, Stinger }

/// <summary>
/// Milestone M3 monster-sound bank. Indexes iteration 2's entity clips (<c>fyp iteration 2/assets/entity_audio</c>:
/// 122 Freesound MP3s fetched by iteration 2's download scripts - recordings, not generated) and sorts them into
/// <see cref="EntityCue"/>s by file name; heartbeats (the player's, not the monster's), room ambiences, human
/// sounds and stray music are left out. Only a small decoded working set is kept: <see cref="PerCue"/> clips per
/// cue and at most <see cref="MaxDecoded"/> alive at once. A clip that has been played <see cref="UsesBeforeRotate"/>
/// times is replaced by the next one from that cue's shuffled deck and released, so the set keeps rotating through
/// the library; repeats in between are expected.
/// Loading runs one file at a time in the background: Unity decodes the file (UnityWebRequest), a worker thread
/// keeps its loudest few seconds, downmixes to mono and levels it to <see cref="TargetRms"/>, and the full decode
/// is destroyed at once (a 26 s stereo MP3 decodes to ~4.6 MB; the kept window is a fraction of that). Until a cue
/// has a clip - or if every file of it fails - it plays a procedural stand-in (<see cref="EntityAudioSynth"/>),
/// so a monster is never silent.
/// </summary>
public sealed class EntityAudioBank : IDisposable
{
    public const int PerCue = 3;
    public const int MaxDecoded = 16;
    public const int UsesBeforeRotate = 3;
    /// <summary>Loudness every clip is levelled to (the MusicGen bed measures RMS ~0.12, bank stingers ~0.22).</summary>
    public const float TargetRms = 0.16f;
    const float PeakLimit = 0.95f, MaxGain = 10f;
    static readonly int CueCount = Enum.GetValues(typeof(EntityCue)).Length;
    /// <summary>Longest stretch kept per cue, seconds (Breath, Movement, Alert, Chase, Stinger).</summary>
    static readonly float[] WindowSeconds = { 7f, 10f, 5f, 5f, 3.5f };
    /// <summary>
    /// Lowest pitch a voice plays at (0.88 x 0.95 vocal, 0.9 x 0.88 movement). A released clip is destroyed as soon
    /// as <see cref="InUse"/> says no source is playing it, and at the latest after length / this + 1 s (a looping
    /// movement clip; its voice notices the destroyed clip and starts a fresh one). Holding released clips for a
    /// flat 12 s kept them occupying the cap and throttled rotation to ~4 swaps in 45 s of heavy use.
    /// </summary>
    const float MinPitch = 0.75f;

    /// <summary>Whether any source is playing a clip right now (set by <see cref="EntityAudioDirector"/>).</summary>
    public Func<AudioClip, bool> InUse;

    static readonly string[] Exclude =
    {
        "car passing", "phone notif", "music-", "alien alarm", "heartbeat", "eerie house", "haunted cavern",
        "tonal sweeps", "plants or ferns", "squeaky shoes", "16-bit remix", "scared", "distant monsters fight",
        "distant ambience", "distant rattle", "ghosthunt", "running on wet grass", "wet grass",
    };

    // First match wins, so the order matters ("Soft Growling Creature Laugh" is an alert, not a stinger).
    static readonly (EntityCue cue, string[] keys)[] Rules =
    {
        (EntityCue.Breath, new[] { "breath", "sniffl", "drool" }),
        (EntityCue.Chase, new[] { "roar", "scream", "screech", "shout", "angry", "boss", "death", "nazgul", "scary_monster", "flesh monster" }),
        (EntityCue.Alert, new[] { "growl", "snarl", "groan", "rumble", "chitter", "monster.aiff", "demonic voice", "zombie pain", "weird sound", "whispers" }),
        (EntityCue.Movement, new[] { "footstep", "foot-steps", "walk", "chain", "drag", "scratch", "scrap", "claw", "sliding", "puddle", "traveling" }),
        (EntityCue.Stinger, new[] { "crack", "bone", "gut rip", "flesh", "tearing", "rip", "squelch", "crunch", "gore", "knuckles", "laugh",
                                    "cackle", "whisper", "swoosh", "warp", "teeth", "chatter", "dente", "thowup", "tomb door", "knocking", "peel" }),
    };

    sealed class Slot
    {
        public AudioClip clip;
        public string path;
        public int uses;
    }

    sealed class Load
    {
        public string path;
        public EntityCue cue;
        public UnityWebRequest request;
        public Task<float[]> task;
        public int frequency;
    }

    readonly List<string>[] _paths = new List<string>[CueCount];
    readonly List<string>[] _deck = new List<string>[CueCount];
    readonly int[] _deckPos = new int[CueCount];
    readonly List<Slot>[] _set = new List<Slot>[CueCount];
    readonly AudioClip[] _fallback = new AudioClip[CueCount];
    readonly List<(AudioClip clip, float at)> _released = new List<(AudioClip, float)>();
    readonly System.Random _rng;
    Load _load;

    public string Root { get; private set; }
    public int IndexedCount { get; private set; }
    public int ExcludedCount { get; private set; }
    public int LoadedTotal { get; private set; }
    public int FailedTotal { get; private set; }
    public int ReleasedTotal { get; private set; }
    public int Plays { get; private set; }
    public int FallbackPlays { get; private set; }
    public int PeakDecoded { get; private set; }

    /// <summary>Clips alive right now: the working set, released ones not yet destroyed, and one in flight.</summary>
    public int DecodedCount
    {
        get
        {
            int n = _released.Count + (_load != null ? 1 : 0);
            foreach (var s in _set) n += s.Count;
            return n;
        }
    }

    public int IndexedFor(EntityCue cue) => _paths[(int)cue].Count;
    public int LoadedFor(EntityCue cue) => _set[(int)cue].Count;

    public EntityAudioBank(int seed)
    {
        _rng = new System.Random(seed);
        for (int c = 0; c < CueCount; c++)
        {
            _paths[c] = new List<string>();
            _deck[c] = new List<string>();
            _set[c] = new List<Slot>(PerCue);
            _fallback[c] = EntityAudioSynth.Create((EntityCue)c, seed + c);
        }
        Index();
    }

    void Index()
    {
        Root = Path.Combine(Path.GetFullPath(Path.Combine(Application.dataPath, "..")), "fyp iteration 2", "assets", "entity_audio");
        if (!Directory.Exists(Root))
        {
            Debug.LogWarning("[EntityAudio] no entity clips at " + Root + " - procedural voices only.");
            return;
        }
        var files = new List<string>();
        foreach (var pattern in new[] { "*.mp3", "*.wav", "*.ogg" }) files.AddRange(Directory.GetFiles(Root, pattern));
        files.Sort(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
        {
            var cue = Classify(Path.GetFileName(f));
            if (cue == null) { ExcludedCount++; continue; }
            _paths[(int)cue.Value].Add(f);
            IndexedCount++;
        }
        for (int c = 0; c < CueCount; c++) Shuffle(c);
    }

    /// <summary>The cue a library file belongs to, or null when it is left out.</summary>
    public static EntityCue? Classify(string fileName)
    {
        var n = fileName.ToLowerInvariant();
        foreach (var k in Exclude) if (n.Contains(k)) return null;
        foreach (var (cue, keys) in Rules)
            foreach (var k in keys)
                if (n.Contains(k)) return cue;
        return null;
    }

    void Shuffle(int c)
    {
        var d = _deck[c];
        d.Clear();
        d.AddRange(_paths[c]);
        for (int i = d.Count - 1; i > 0; i--)
        {
            int j = _rng.Next(i + 1);
            (d[i], d[j]) = (d[j], d[i]);
        }
        _deckPos[c] = 0;
    }

    /// <summary>
    /// A clip for <paramref name="cue"/>: the least-played one in its working set (ties random), else the
    /// procedural stand-in. Never null.
    /// </summary>
    public AudioClip Next(EntityCue cue)
    {
        Slot pick = null;
        int ties = 0;
        foreach (var s in _set[(int)cue])
        {
            if (s.clip == null) continue;
            if (pick == null || s.uses < pick.uses) { pick = s; ties = 1; }
            else if (s.uses == pick.uses && _rng.Next(++ties) == 0) pick = s;
        }
        if (pick == null)
        {
            FallbackPlays++;
            return _fallback[(int)cue];
        }
        pick.uses++;
        Plays++;
        return pick.clip;
    }

    /// <summary>Main thread, every frame: releases old clips and advances the background loader.</summary>
    public void Tick()
    {
        float now = Time.unscaledTime;
        for (int i = _released.Count - 1; i >= 0; i--)
        {
            var (old, at) = _released[i];
            if (now < at && old != null && (InUse == null || InUse(old))) continue;
            if (_released[i].clip != null) HorrorTextureCorruptor.SafeDestroy(_released[i].clip);
            _released.RemoveAt(i);
            ReleasedTotal++;
        }

        if (_load != null)
        {
            Advance();
            return;
        }
        if (DecodedCount >= MaxDecoded) return;

        // Fill any cue below its working-set size first, then replace a clip that is due for rotation.
        int want = -1;
        for (int c = 0; c < CueCount && want < 0; c++)
            if (_set[c].Count < Math.Min(PerCue, _paths[c].Count)) want = c;
        for (int c = 0; c < CueCount && want < 0; c++)
            if (_paths[c].Count > _set[c].Count && _set[c].Exists(s => s.uses >= UsesBeforeRotate)) want = c;
        if (want < 0) return;

        var path = NextPath(want);
        if (path == null) return;
        var request = UnityWebRequestMultimedia.GetAudioClip(new Uri(path).AbsoluteUri, TypeOf(path));
        ((DownloadHandlerAudioClip)request.downloadHandler).streamAudio = false;
        request.SendWebRequest();
        _load = new Load { path = path, cue = (EntityCue)want, request = request };
    }

    string NextPath(int c)
    {
        var deck = _deck[c];
        for (int tries = 0; tries < deck.Count; tries++)
        {
            if (_deckPos[c] >= deck.Count) Shuffle(c);
            var p = deck[_deckPos[c]++];
            if (!_set[c].Exists(s => s.path == p)) return p;
        }
        return null;
    }

    static AudioType TypeOf(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext == ".wav" ? AudioType.WAV : ext == ".ogg" ? AudioType.OGGVORBIS : AudioType.MPEG;
    }

    void Advance()
    {
        var l = _load;
        if (l.task == null)
        {
            if (!l.request.isDone) return;
            AudioClip decoded = null;
            if (l.request.result == UnityWebRequest.Result.Success)
            {
                try { decoded = DownloadHandlerAudioClip.GetContent(l.request); }
                catch (Exception e) { Debug.LogWarning($"[EntityAudio] decode failed {Path.GetFileName(l.path)}: {e.Message}"); }
            }
            string error = l.request.error;
            l.request.Dispose();
            l.request = null;
            if (decoded == null || decoded.samples == 0)
            {
                if (decoded != null) HorrorTextureCorruptor.SafeDestroy(decoded);
                Fail(l, error ?? "no audio");
                return;
            }

            var data = new float[decoded.samples * decoded.channels];
            decoded.GetData(data, 0);
            int channels = decoded.channels;
            l.frequency = decoded.frequency;
            HorrorTextureCorruptor.SafeDestroy(decoded);   // only the trimmed window is kept
            float window = WindowSeconds[(int)l.cue];
            int hz = l.frequency;
            l.task = Task.Run(() => Trim(data, channels, hz, window));
            return;
        }

        if (!l.task.IsCompleted) return;
        var samples = l.task.IsFaulted ? null : l.task.Result;
        if (samples == null || samples.Length == 0)
        {
            Fail(l, l.task.Exception?.GetBaseException().Message ?? "silent");
            return;
        }
        var clip = AudioClip.Create($"{l.cue}_{Path.GetFileNameWithoutExtension(l.path)}", samples.Length, 1, l.frequency, false);
        clip.SetData(samples, 0);
        Add(l.cue, clip, l.path);
        _load = null;
        LoadedTotal++;
        PeakDecoded = Math.Max(PeakDecoded, DecodedCount);
    }

    void Add(EntityCue cue, AudioClip clip, string path)
    {
        var set = _set[(int)cue];
        var slot = new Slot { clip = clip, path = path };
        if (set.Count < PerCue)
        {
            set.Add(slot);
            return;
        }
        int worst = 0;
        for (int i = 1; i < set.Count; i++) if (set[i].uses > set[worst].uses) worst = i;
        Release(set[worst].clip);
        set[worst] = slot;
    }

    void Release(AudioClip clip)
    {
        if (clip != null) _released.Add((clip, Time.unscaledTime + clip.length / MinPitch + 1f));
    }

    void Fail(Load l, string why)
    {
        Debug.LogWarning($"[EntityAudio] skipping {Path.GetFileName(l.path)} for this session: {why}");
        _paths[(int)l.cue].Remove(l.path);
        _deck[(int)l.cue].Remove(l.path);
        _deckPos[(int)l.cue] = Math.Min(_deckPos[(int)l.cue], _deck[(int)l.cue].Count);
        FailedTotal++;
        _load = null;
    }

    /// <summary>
    /// Worker thread: mono downmix, the loudest <paramref name="windowSec"/> (in 50 ms blocks), levelled, with
    /// 20 ms fades so a cut window neither clicks nor pops when it loops.
    /// </summary>
    static float[] Trim(float[] data, int channels, int hz, float windowSec)
    {
        int frames = data.Length / Math.Max(1, channels);
        if (frames == 0) return null;
        var mono = new float[frames];
        for (int f = 0; f < frames; f++)
        {
            float sum = 0f;
            for (int c = 0; c < channels; c++) sum += data[f * channels + c];
            mono[f] = sum / channels;
        }

        int win = Math.Min(frames, (int)(windowSec * hz));
        int start = 0;
        if (win < frames)
        {
            int block = Math.Max(1, hz / 20);
            int blocks = frames / block, winBlocks = Math.Max(1, win / block);
            var prefix = new double[blocks + 1];
            for (int b = 0; b < blocks; b++)
            {
                double e = 0;
                for (int i = b * block; i < (b + 1) * block; i++) e += mono[i] * mono[i];
                prefix[b + 1] = prefix[b] + e;
            }
            double best = -1;
            for (int b = 0; b + winBlocks <= blocks; b++)
            {
                double e = prefix[b + winBlocks] - prefix[b];
                if (e > best) { best = e; start = b * block; }
            }
            start = Math.Min(start, frames - win);
        }

        var s = new float[win];
        Array.Copy(mono, start, s, 0, win);
        Normalise(s);
        int fade = Math.Min(win / 4, hz / 50);
        for (int i = 0; i < fade; i++)
        {
            float g = (float)i / fade;
            s[i] *= g;
            s[win - 1 - i] *= g;
        }
        return s;
    }

    /// <summary>Level to <see cref="TargetRms"/> without letting the peak pass <see cref="PeakLimit"/>. Thread-safe.</summary>
    public static void Normalise(float[] s)
    {
        double sum = 0;
        float peak = 0f;
        foreach (var v in s)
        {
            sum += v * v;
            peak = Math.Max(peak, Math.Abs(v));
        }
        float rms = (float)Math.Sqrt(sum / Math.Max(1, s.Length));
        if (rms < 1e-5f || peak < 1e-5f) return;
        float gain = Math.Min(Math.Min(TargetRms / rms, PeakLimit / peak), MaxGain);
        for (int i = 0; i < s.Length; i++) s[i] *= gain;
    }

    /// <summary>One line for logs and the test evidence.</summary>
    public string Describe()
    {
        var parts = new List<string>();
        for (int c = 0; c < CueCount; c++) parts.Add($"{(EntityCue)c} {_set[c].Count}/{_paths[c].Count}");
        return $"{string.Join(", ", parts)} | decoded {DecodedCount}/{MaxDecoded} (peak {PeakDecoded}), loaded {LoadedTotal}, " +
               $"released {ReleasedTotal}, failed {FailedTotal}, plays {Plays}, procedural {FallbackPlays}";
    }

    public void Dispose()
    {
        if (_load != null)
        {
            _load.request?.Dispose();
            _load = null;
        }
        foreach (var set in _set)
        {
            foreach (var s in set) if (s.clip != null) HorrorTextureCorruptor.SafeDestroy(s.clip);
            set.Clear();
        }
        foreach (var (clip, _) in _released) if (clip != null) HorrorTextureCorruptor.SafeDestroy(clip);
        _released.Clear();
        for (int c = 0; c < CueCount; c++)
            if (_fallback[c] != null) HorrorTextureCorruptor.SafeDestroy(_fallback[c]);
    }
}
