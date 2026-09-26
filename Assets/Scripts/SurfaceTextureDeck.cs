using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Milestone M4 texture juggling - the SOURCE library for <see cref="SurfaceTextureGenerator"/>, which composes
/// and U-Net-corrupts these into new textures (nothing here is shown as-is). Iteration 2's per-stress-level
/// library of real CC0 PBR textures (Poly Haven / ambientCG, downloaded by iteration 2's
/// <c>download_polyhaven.py</c> / <c>download_assets.py</c> into <c>assets/textures/0..5</c>), curated per house
/// surface so walls draw wall materials, floors draw floors and so on. Levels climb from homely (parquet,
/// plaster, tiles) through worn (concrete, cobble, peeling paint) to ruin at 4-5 (broken brick, damaged plaster,
/// rust, rotten planks), with stone making up about a third of level 5.
///
/// Data: <c>Resources/TextureDeck/deck.txt</c> holds one <c>Surface|Level|id,id,...</c> line per slot
/// (5-6 textures each, gaps filled from the neighbouring level); textures are
/// <c>Resources/TextureDeck/&lt;id&gt;</c> (512 px) with an optional <c>&lt;id&gt;_n</c> normal map.
/// Picks never repeat the last <see cref="HistoryDepth"/> draws of the same (surface, level), so
/// L3 -> L4 -> L3 shows a different L3 base, and one pass never hands the same texture to two surfaces.
/// </summary>
public sealed class SurfaceTextureDeck
{
    public const int HistoryDepth = 3;
    const string Root = "TextureDeck/";

    public struct Pick
    {
        public string id;
        public Texture2D albedo;
        public Texture2D normal;   // null when the source had no normal map
    }

    readonly Dictionary<(string, int), string[]> _slots = new Dictionary<(string, int), string[]>();
    readonly Dictionary<(string, int), List<string>> _history = new Dictionary<(string, int), List<string>>();
    readonly Dictionary<string, Texture2D> _loaded = new Dictionary<string, Texture2D>();
    readonly System.Random _rng;

    public int SlotCount => _slots.Count;

    SurfaceTextureDeck(int seed) => _rng = new System.Random(seed);

    /// <summary>Loads the deck manifest; null when it is missing or empty.</summary>
    public static SurfaceTextureDeck Load(int seed)
    {
        var manifest = Resources.Load<TextAsset>(Root + "deck");
        if (manifest == null) return null;
        var deck = new SurfaceTextureDeck(seed);
        foreach (var raw in manifest.text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            var parts = line.Split('|');
            if (parts.Length != 3 || !int.TryParse(parts[1], out int level)) continue;
            var ids = parts[2].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (ids.Length > 0) deck._slots[(parts[0], level)] = ids;
        }
        return deck._slots.Count > 0 ? deck : null;
    }

    public bool Has(string surface, int level) => surface != null && _slots.ContainsKey((surface, level));

    /// <summary>
    /// Draw the next base for <paramref name="surface"/> at <paramref name="level"/>, avoiding its recent
    /// history and anything in <paramref name="usedThisPass"/> (which the pick is added to).
    /// </summary>
    public bool TryNext(string surface, int level, HashSet<string> usedThisPass, out Pick pick)
    {
        pick = default;
        if (!_slots.TryGetValue((surface, level), out var ids)) return false;
        if (!_history.TryGetValue((surface, level), out var history))
            _history[(surface, level)] = history = new List<string>();

        int depth = Mathf.Min(HistoryDepth, ids.Length - 1);
        int recentFrom = Mathf.Max(0, history.Count - depth);
        var candidates = new List<string>(ids.Length);
        // Strictest first, then relax: fresh and unused this pass, fresh only, anything.
        for (int pass = 0; pass < 3 && candidates.Count == 0; pass++)
            foreach (var id in ids)
            {
                bool recent = history.IndexOf(id, recentFrom) >= 0;
                bool taken = usedThisPass != null && usedThisPass.Contains(id);
                if (pass == 0 && (recent || taken)) continue;
                if (pass == 1 && recent) continue;
                candidates.Add(id);
            }

        // Try candidates in random order until one loads.
        while (candidates.Count > 0)
        {
            int i = _rng.Next(candidates.Count);
            var id = candidates[i];
            candidates.RemoveAt(i);
            var albedo = LoadTex(id);
            if (albedo == null) continue;
            history.Add(id);
            if (history.Count > HistoryDepth * 2) history.RemoveAt(0);
            usedThisPass?.Add(id);
            pick = new Pick { id = id, albedo = albedo, normal = LoadTex(id + "_n") };
            return true;
        }
        return false;
    }

    /// <summary>
    /// A random texture from the slot, skipping ids in <paramref name="exclude"/>. Does not touch the
    /// anti-repeat history - used for the generator's secondary composition layers.
    /// </summary>
    public bool TryRandom(string surface, int level, ICollection<string> exclude, Func<string, bool> accept, out Pick pick)
    {
        pick = default;
        if (!_slots.TryGetValue((surface, level), out var ids)) return false;
        var candidates = new List<string>(ids.Length);
        foreach (var id in ids)
            if ((exclude == null || !exclude.Contains(id)) && (accept == null || accept(id))) candidates.Add(id);
        while (candidates.Count > 0)
        {
            int i = _rng.Next(candidates.Count);
            var id = candidates[i];
            candidates.RemoveAt(i);
            var albedo = LoadTex(id);
            if (albedo == null) continue;
            pick = new Pick { id = id, albedo = albedo, normal = LoadTex(id + "_n") };
            return true;
        }
        return false;
    }

    /// <summary>Material family of a library texture, from its name (same rules as the curation script).</summary>
    public static string Family(string id)
    {
        var s = id.ToLowerInvariant();
        if (Has(s, "metal", "rust", "corrugat", "iron", "steel", "shutter", "container")) return "metal";
        if (Has(s, "tile", "mosaic", "terrazzo")) return "tile";
        if (Has(s, "brick")) return "brick";
        if (Has(s, "rock", "stone", "cliff", "quarry", "boulder", "slate", "granite", "marble", "sandstone", "cobble", "block",
                "defense_wall", "castle", "medieval", "monastery", "ruins")) return "stone";
        if (Has(s, "wood", "plank", "parquet", "laminate", "veneer", "plywood", "strand", "pine", "oak", "deck", "floorboard")) return "wood";
        if (Has(s, "concrete", "cement", "precast", "slab_wall", "pavement", "paver", "paving", "garage", "hangar")) return "concrete";
        return "plaster";
    }

    /// <summary>Plaster and concrete have no strong pattern, so they can overlay anything.</summary>
    public static bool IsSoft(string family) => family == "plaster" || family == "concrete";

    static bool Has(string s, params string[] keys)
    {
        foreach (var k in keys) if (s.Contains(k)) return true;
        return false;
    }

    Texture2D LoadTex(string name)
    {
        if (_loaded.TryGetValue(name, out var t)) return t;
        t = Resources.Load<Texture2D>(Root + name);
        _loaded[name] = t;   // cache misses too, so a missing normal map is looked up once
        return t;
    }
}
