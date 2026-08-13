using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// Indexes iteration-2 horror audio banks and juggles the FULL per-level pool
/// (shuffle without replacement) on every stress change.
/// </summary>
public static class ProceduralAssetBank
{
    static readonly List<string>[] LevelClips = new List<string>[6];
    static readonly List<string> WildlifeClips = new List<string>();
    static readonly List<string>[] LevelDecks = new List<string>[6];
    static readonly List<string> WildlifeDeck = new List<string>();
    static int[] _levelDeckPos = new int[6];
    static int _wildlifeDeckPos;
    static bool _ready;
    static string _root;

    public static bool Ready => _ready;
    public static int TotalCount
    {
        get
        {
            EnsureIndexed();
            int n = WildlifeClips.Count;
            for (int i = 0; i < 6; i++)
                n += LevelClips[i] != null ? LevelClips[i].Count : 0;
            return n;
        }
    }

    public static int LevelCount(int level)
    {
        EnsureIndexed();
        level = Mathf.Clamp(level, 0, 5);
        return LevelClips[level]?.Count ?? 0;
    }

    public static void EnsureIndexed()
    {
        if (_ready) return;
        for (int i = 0; i < 6; i++)
        {
            LevelClips[i] = new List<string>(64);
            LevelDecks[i] = new List<string>(64);
        }
        WildlifeClips.Clear();
        WildlifeDeck.Clear();

        string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        _root = Path.Combine(projectRoot, "fyp iteration 2", "assets");
        if (!Directory.Exists(_root))
        {
            Debug.LogWarning("[ProcBank] missing assets root: " + _root);
            _ready = true;
            return;
        }

        for (int level = 0; level < 6; level++)
        {
            string dir = Path.Combine(_root, "audio", level.ToString());
            AddWavs(dir, LevelClips[level], recursive: false);
        }
        AddWavs(Path.Combine(_root, "wildlife"), WildlifeClips, recursive: true);

        string fypAudio = Path.Combine(Application.dataPath, "FYP", "Audio");
        if (Directory.Exists(fypAudio))
        {
            for (int level = 0; level < 6; level++)
                AddWavs(Path.Combine(fypAudio, level.ToString()), LevelClips[level], recursive: true);
        }

        _ready = true;
        Debug.Log($"[ProcBank] indexed level=[{Count(0)},{Count(1)},{Count(2)},{Count(3)},{Count(4)},{Count(5)}] wildlife={WildlifeClips.Count} root={_root}");
    }

    static int Count(int i) => LevelClips[i]?.Count ?? 0;

    static void AddWavs(string dir, List<string> into, bool recursive)
    {
        if (!Directory.Exists(dir) || into == null) return;
        var opt = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        try
        {
            foreach (var f in Directory.GetFiles(dir, "*.wav", opt))
                into.Add(f);
            foreach (var f in Directory.GetFiles(dir, "*.WAV", opt))
                if (!into.Contains(f)) into.Add(f);
        }
        catch (Exception e)
        {
            Debug.LogWarning("[ProcBank] scan failed " + dir + ": " + e.Message);
        }
    }

    /// <summary>Reshuffle ALL assets for this stress level (+ wildlife). Call on every stress change.</summary>
    public static void ReshuffleLevelPool(int level, int? seed = null)
    {
        EnsureIndexed();
        level = Mathf.Clamp(level, 0, 5);
        var rng = seed.HasValue ? new System.Random(seed.Value) : new System.Random();

        RefillAndShuffle(LevelDecks[level], LevelClips[level], rng);
        _levelDeckPos[level] = 0;

        RefillAndShuffle(WildlifeDeck, WildlifeClips, rng);
        _wildlifeDeckPos = 0;

        Debug.Log($"[ProcBank] reshuffled L{level} assets={LevelDecks[level].Count} wildlife={WildlifeDeck.Count}");
    }

    static void RefillAndShuffle(List<string> deck, List<string> source, System.Random rng)
    {
        deck.Clear();
        if (source == null || source.Count == 0) return;
        deck.AddRange(source);
        for (int i = deck.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (deck[i], deck[j]) = (deck[j], deck[i]);
        }
    }

    /// <summary>Next path from the level deck (reshuffles when exhausted).</summary>
    public static string TakeNextLevelAsset(int level)
    {
        EnsureIndexed();
        level = Mathf.Clamp(level, 0, 5);
        if (LevelDecks[level] == null || LevelDecks[level].Count == 0)
        {
            if ((LevelClips[level]?.Count ?? 0) == 0) return null;
            ReshuffleLevelPool(level);
        }

        var deck = LevelDecks[level];
        if (deck.Count == 0) return null;
        if (_levelDeckPos[level] >= deck.Count)
        {
            RefillAndShuffle(deck, LevelClips[level], new System.Random());
            _levelDeckPos[level] = 0;
        }
        return deck[_levelDeckPos[level]++];
    }

    public static string TakeNextWildlife()
    {
        EnsureIndexed();
        if (WildlifeDeck.Count == 0)
        {
            if (WildlifeClips.Count == 0) return null;
            RefillAndShuffle(WildlifeDeck, WildlifeClips, new System.Random());
            _wildlifeDeckPos = 0;
        }
        if (_wildlifeDeckPos >= WildlifeDeck.Count)
        {
            RefillAndShuffle(WildlifeDeck, WildlifeClips, new System.Random());
            _wildlifeDeckPos = 0;
        }
        if (WildlifeDeck.Count == 0) return null;
        return WildlifeDeck[_wildlifeDeckPos++];
    }

    /// <summary>Alternate level + wildlife draws from the shuffled decks.</summary>
    public static string TakeNextJuggled(int level, bool preferLevel)
    {
        string path = preferLevel ? TakeNextLevelAsset(level) : TakeNextWildlife();
        if (path == null)
            path = preferLevel ? TakeNextWildlife() : TakeNextLevelAsset(level);
        // Neighbor levels if this level folder is empty
        if (path == null)
        {
            for (int d = 1; d <= 5 && path == null; d++)
            {
                if (level - d >= 0) path = TakeNextLevelAsset(level - d);
                if (path == null && level + d <= 5) path = TakeNextLevelAsset(level + d);
            }
        }
        return path;
    }

    [Obsolete("Use TakeNextJuggled / ReshuffleLevelPool")]
    public static string PickPath(int level, System.Random rng, bool preferLevelFolder)
    {
        return TakeNextJuggled(level, preferLevelFolder);
    }

    public static AudioClip LoadClip(string path, string name)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try
        {
            byte[] wav = File.ReadAllBytes(path);
            return WavUtility.ToAudioClip(wav, name);
        }
        catch (Exception e)
        {
            Debug.LogWarning("[ProcBank] load failed " + path + ": " + e.Message);
            return null;
        }
    }
}
