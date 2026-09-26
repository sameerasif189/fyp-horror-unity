using System.Collections.Generic;
using UnityEngine;

/// <summary>Dominant look of a single corruption pass.</summary>
public enum CorruptionArchetype
{
    TvStatic = 0,
    Grime = 1,
    Cracks = 2,
    Blood = 3,
    Scorch = 4,
    Growth = 5,
}

/// <summary>
/// One corruption pass: a dominant archetype plus the ambient amount of every other
/// effect. All weights are 0..1 and are consumed by <see cref="HorrorTextureCorruptor.Paint"/>.
/// </summary>
public struct CorruptionRecipe
{
    public CorruptionArchetype dominant;
    /// <summary>Stress level this recipe was built for; drives the per-level colour grade.</summary>
    public int level;
    public float grime;
    public float cracks;
    public float blood;
    public float scorch;
    public float growth;
    public float tvStatic;
    public int seed;

    public bool IsClean => grime + cracks + blood + scorch + growth + tvStatic < 0.02f;

    public override string ToString() =>
        $"{dominant} gr{grime:F2} cr{cracks:F2} bl{blood:F2} sc{scorch:F2} gw{growth:F2} tv{tvStatic:F2}";
}

/// <summary>
/// Picks which corruption archetype dominates a given (surface, stress level) pass and
/// remembers recent picks so the same variant is not shown twice in a row.
///
/// This is what makes `L3 -> L5 -> L3` land on a different L3 look: history is keyed per
/// surface *and* per level, so returning to a level consults that level's own recent picks.
///
/// Dial the whole corruption pass back here (see <see cref="Weights"/> and
/// <see cref="DominantFloor"/>) rather than editing the painters in HorrorTextureCorruptor.
/// </summary>
public class CorruptionVariantSelector
{
    const int ArchetypeCount = 6;

    /// <summary>
    /// How many recent picks per (surface, level) are excluded from re-selection. At 3, a
    /// sequence like L3 -> L4 -> L5 -> L4 -> L3 cannot show the same archetype on either return,
    /// because each level keeps its own history.
    /// </summary>
    public int HistoryDepth { get; set; } = 3;

    /// <summary>Dominant effect strength at the very bottom of the severity ramp.</summary>
    public float DominantFloor { get; set; } = 0.22f;

    /// <summary>Strength every non-dominant effect contributes, scaled by severity and its weight.</summary>
    public float AmbientScale { get; set; } = 0.22f;

    /// <summary>
    /// Non-linear severity per level, mirroring StressAudioProfile.Fright01 so picture and sound
    /// escalate together. The gap between L3 and L4 is deliberately the largest.
    /// </summary>
    static readonly float[] Severity = { 0f, 0.18f, 0.34f, 0.55f, 0.82f, 1.00f };

    // rows = stress level 0..5, cols = CorruptionArchetype order.
    // A zero weight means the archetype cannot dominate and contributes no ambient at that level.
    static readonly float[,] Weights =
    {
        { 0.00f, 0.00f, 0.00f, 0.00f, 0.00f, 0.00f }, // L0 - clean, never reached
        { 0.15f, 1.00f, 0.35f, 0.00f, 0.00f, 0.20f }, // L1 - damp and dusty
        { 0.35f, 0.90f, 0.80f, 0.15f, 0.10f, 0.45f }, // L2 - plaster starts failing
        { 0.70f, 0.70f, 1.00f, 0.60f, 0.40f, 0.70f }, // L3 - something happened here
        { 1.00f, 0.50f, 0.90f, 1.00f, 0.80f, 0.90f }, // L4 - actively wrong
        { 1.00f, 0.40f, 0.80f, 1.00f, 1.00f, 0.80f }, // L5 - full breakdown
    };

    readonly Dictionary<int, List<CorruptionArchetype>> _history = new Dictionary<int, List<CorruptionArchetype>>();
    System.Random _rng;

    public CorruptionVariantSelector(int seed = 20260911)
    {
        _rng = new System.Random(seed);
    }

    /// <summary>Drop all variant history, e.g. when surfaces are rebound to a fresh house.</summary>
    public void Reset(int seed = 20260911)
    {
        _history.Clear();
        _rng = new System.Random(seed);
    }

    /// <summary>
    /// Build the next recipe for one surface at one stress level.
    /// <paramref name="corruption"/> is the fusion visual_corruption scalar (0..1);
    /// <paramref name="grimeOverlay"/> is fusion visual_grime_overlay and biases grime upward.
    /// </summary>
    public CorruptionRecipe Next(int surfaceIndex, int level, float corruption, float grimeOverlay, int seed)
    {
        level = Mathf.Clamp(level, 0, 5);
        var recipe = new CorruptionRecipe { seed = seed, level = level };
        if (level <= 0) return recipe;

        corruption = Mathf.Clamp01(corruption <= 0f ? level / 5f : corruption);
        float severity = Severity[level];

        var dominant = PickDominant(surfaceIndex, level);
        recipe.dominant = dominant;

        float dominantStrength = Mathf.Clamp01(Mathf.Lerp(DominantFloor, 1f, severity) * corruption);
        for (int a = 0; a < ArchetypeCount; a++)
        {
            float w = Weights[level, a];
            if (w <= 0f) continue;
            float amount = a == (int)dominant
                ? dominantStrength
                : Mathf.Clamp01(AmbientScale * severity * w * corruption);
            Set(ref recipe, (CorruptionArchetype)a, amount);
        }

        // TV static stays part of the established house look even when another archetype leads.
        recipe.tvStatic = Mathf.Max(recipe.tvStatic, 0.25f * severity * corruption);
        recipe.grime = Mathf.Clamp01(recipe.grime + 0.25f * Mathf.Clamp01(grimeOverlay));
        return recipe;
    }

    CorruptionArchetype PickDominant(int surfaceIndex, int level)
    {
        int key = surfaceIndex * 977 + level;
        if (!_history.TryGetValue(key, out var recent))
        {
            recent = new List<CorruptionArchetype>(HistoryDepth);
            _history[key] = recent;
        }

        var pick = WeightedPick(level, recent);
        recent.Add(pick);
        while (recent.Count > Mathf.Max(1, HistoryDepth))
            recent.RemoveAt(0);
        return pick;
    }

    CorruptionArchetype WeightedPick(int level, List<CorruptionArchetype> exclude)
    {
        float total = 0f;
        for (int a = 0; a < ArchetypeCount; a++)
        {
            if (Weights[level, a] <= 0f) continue;
            if (exclude != null && exclude.Contains((CorruptionArchetype)a)) continue;
            total += Weights[level, a];
        }

        // Every candidate is in history (few archetypes are legal at low levels) -
        // fall back to the unfiltered distribution rather than returning nothing.
        if (total <= 0f)
        {
            for (int a = 0; a < ArchetypeCount; a++)
                total += Weights[level, a];
            exclude = null;
        }
        if (total <= 0f) return CorruptionArchetype.TvStatic;

        double roll = _rng.NextDouble() * total;
        for (int a = 0; a < ArchetypeCount; a++)
        {
            float w = Weights[level, a];
            if (w <= 0f) continue;
            if (exclude != null && exclude.Contains((CorruptionArchetype)a)) continue;
            roll -= w;
            if (roll <= 0d) return (CorruptionArchetype)a;
        }
        return CorruptionArchetype.TvStatic;
    }

    static void Set(ref CorruptionRecipe r, CorruptionArchetype a, float v)
    {
        switch (a)
        {
            case CorruptionArchetype.TvStatic: r.tvStatic = v; break;
            case CorruptionArchetype.Grime: r.grime = v; break;
            case CorruptionArchetype.Cracks: r.cracks = v; break;
            case CorruptionArchetype.Blood: r.blood = v; break;
            case CorruptionArchetype.Scorch: r.scorch = v; break;
            case CorruptionArchetype.Growth: r.growth = v; break;
        }
    }
}
