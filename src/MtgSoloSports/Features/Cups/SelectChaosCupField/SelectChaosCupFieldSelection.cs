using MtgSoloSports.SimulationKernel.Catalog;
using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.SimulationKernel.Random;

namespace MtgSoloSports.Features.Cups.SelectChaosCupField;

/// <summary>
/// Pure deterministic Chaos Cup field selection and balanced first-stage draw (MSS-100).
/// Pool invitations are distinct uniform-random per color with no strength, bonus or
/// league weighting: each color's pool is name-sorted (ordinal) then id-sorted, shuffled
/// once with the versioned save RNG in sporting-color enum order, and the required prefix
/// is invited. The first draw holds exactly 4 of each color per group: each color's 128
/// entrants are name/id-ordered, shuffled in enum order, dealt 4 per group, then each
/// group's 32 are shuffled once in group-number order for the within-group draw order.
/// All methods consume only the caller-supplied <see cref="Pcg32V1"/>; database, clock,
/// GUID and bonus ordering never affect the outcome. The caller persists
/// <c>rng.Snapshot()</c> in the same transaction as entrants and draws.
/// </summary>
public static class SelectChaosCupFieldSelection
{
    public sealed record PoolCandidate(int SaveAthleteId, string Name, SportingColor SportingColor);

    public sealed record EntrantCandidate(
        int SaveAthleteId,
        string Name,
        SportingColor SportingColor,
        int? SourceLeagueId,
        int SourceLeagueKind,
        int SourceFeederDivision,
        bool IsPoolInvite);

    /// <summary>
    /// Selects uniform-random Pool invitations per color in enum order, advancing
    /// <paramref name="rng"/>. Each color's pool is canonically ordered before shuffling
    /// so database ordering never affects the draw.
    /// </summary>
    public static IReadOnlyDictionary<SportingColor, IReadOnlyList<PoolCandidate>> SelectPoolInvites(
        IReadOnlyDictionary<SportingColor, IReadOnlyList<PoolCandidate>> poolByColor,
        IReadOnlyDictionary<SportingColor, int> requiredByColor,
        Pcg32V1 rng)
    {
        ArgumentNullException.ThrowIfNull(poolByColor);
        ArgumentNullException.ThrowIfNull(requiredByColor);
        ArgumentNullException.ThrowIfNull(rng);

        Dictionary<SportingColor, IReadOnlyList<PoolCandidate>> invited = new();
        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            IReadOnlyList<PoolCandidate> pool = RequirePool(poolByColor, color);
            int required = RequireQuota(requiredByColor, color);
            invited[color] = SelectSingleColor(pool, color, required, rng);
        }

        return invited;
    }

    internal static IReadOnlyList<PoolCandidate> SelectSingleColor(
        IReadOnlyList<PoolCandidate> pool,
        SportingColor color,
        int required,
        Pcg32V1 rng)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(rng);
        EnsureQuotaRange(color, required);
        List<PoolCandidate> ordered = OrderPool(pool, color);
        if (required == 0)
        {
            return [];
        }

        return TakeShuffled(ordered, color, required, rng);
    }

    /// <summary>
    /// Builds the balanced 32x32 first-stage draw from the frozen 128-per-color field.
    /// Each color's entrants are canonically ordered, shuffled in enum order, dealt 4 per
    /// group, then each group is shuffled once in group-number order for draw order.
    /// Returns 32 groups of 32 in group-number order with draw order preserved.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<EntrantCandidate>> BuildBalancedFirstDraw(
        IReadOnlyDictionary<SportingColor, IReadOnlyList<EntrantCandidate>> entrantsByColor,
        Pcg32V1 rng)
    {
        ArgumentNullException.ThrowIfNull(entrantsByColor);
        ArgumentNullException.ThrowIfNull(rng);

        Dictionary<SportingColor, List<EntrantCandidate>> shuffled = ShuffleByColor(entrantsByColor, rng);
        EnsureGlobalUniqueness(shuffled);
        return DealGroups(shuffled, rng);
    }

    private static IReadOnlyList<PoolCandidate> RequirePool(
        IReadOnlyDictionary<SportingColor, IReadOnlyList<PoolCandidate>> poolByColor,
        SportingColor color)
    {
        if (!poolByColor.TryGetValue(color, out IReadOnlyList<PoolCandidate>? pool))
        {
            throw new InvalidOperationException($"Chaos Pool selection misses sporting color {color}.");
        }

        return pool;
    }

    private static int RequireQuota(IReadOnlyDictionary<SportingColor, int> requiredByColor, SportingColor color)
    {
        if (!requiredByColor.TryGetValue(color, out int required))
        {
            throw new InvalidOperationException($"Chaos Pool quotas miss sporting color {color}.");
        }

        return required;
    }

    private static void EnsureQuotaRange(SportingColor color, int required)
    {
        if (required < 0 || required > ChaosCupFormat.EntrantsPerColor)
        {
            throw new InvalidOperationException($"Chaos Pool invitations for {color} must be 0..{ChaosCupFormat.EntrantsPerColor}, was {required}.");
        }
    }

    private static List<PoolCandidate> OrderPool(IReadOnlyList<PoolCandidate> pool, SportingColor color)
    {
        List<PoolCandidate> ordered = pool
            .OrderBy(c => c.Name, StringComparer.Ordinal)
            .ThenBy(c => c.SaveAthleteId)
            .ToList();
        if (ordered.Count < 0)
        {
            throw new InvalidOperationException($"Chaos pool for {color} is corrupt.");
        }

        foreach (PoolCandidate candidate in ordered)
        {
            EnsurePoolCandidate(candidate, color);
        }

        return ordered;
    }

    private static void EnsurePoolCandidate(PoolCandidate candidate, SportingColor color)
    {
        if (candidate.SaveAthleteId <= 0)
        {
            throw new InvalidOperationException($"Chaos pool for {color} contains corrupt athlete id {candidate.SaveAthleteId}.");
        }

        if (string.IsNullOrWhiteSpace(candidate.Name))
        {
            throw new InvalidOperationException($"Chaos pool athlete {candidate.SaveAthleteId} has an empty name.");
        }

        if (candidate.SportingColor != color)
        {
            throw new InvalidOperationException(
                $"Chaos pool athlete {candidate.SaveAthleteId} color {candidate.SportingColor} does not match pool {color}.");
        }
    }

    private static IReadOnlyList<PoolCandidate> TakeShuffled(
        List<PoolCandidate> ordered,
        SportingColor color,
        int required,
        Pcg32V1 rng)
    {
        if (ordered.Count < required)
        {
            throw new InvalidOperationException(
                $"Chaos pool for {color} holds {ordered.Count} athletes but needs {required} invitations.");
        }

        List<PoolCandidate> shuffled = [.. ordered];
        DeterministicShuffle.Shuffle(shuffled, rng);
        List<PoolCandidate> taken = shuffled.Take(required).ToList();
        HashSet<int> seen = new();
        foreach (PoolCandidate candidate in taken)
        {
            if (!seen.Add(candidate.SaveAthleteId))
            {
                throw new InvalidOperationException($"Chaos Pool invitations for {color} contain duplicate athlete {candidate.SaveAthleteId}.");
            }
        }

        return taken;
    }

    private static Dictionary<SportingColor, List<EntrantCandidate>> ShuffleByColor(
        IReadOnlyDictionary<SportingColor, IReadOnlyList<EntrantCandidate>> entrantsByColor,
        Pcg32V1 rng)
    {
        Dictionary<SportingColor, List<EntrantCandidate>> shuffled = new();
        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            shuffled[color] = ShuffleSingleColor(entrantsByColor, color, rng);
        }

        return shuffled;
    }

    private static List<EntrantCandidate> ShuffleSingleColor(
        IReadOnlyDictionary<SportingColor, IReadOnlyList<EntrantCandidate>> entrantsByColor,
        SportingColor color,
        Pcg32V1 rng)
    {
        if (!entrantsByColor.TryGetValue(color, out IReadOnlyList<EntrantCandidate>? entrants))
        {
            throw new InvalidOperationException($"Chaos field misses sporting color {color}.");
        }

        if (entrants.Count != ChaosCupFormat.EntrantsPerColor)
        {
            throw new InvalidOperationException(
                $"Chaos field must hold exactly {ChaosCupFormat.EntrantsPerColor} {color} entrants, was {entrants.Count}.");
        }

        List<EntrantCandidate> ordered = entrants
            .OrderBy(e => e.Name, StringComparer.Ordinal)
            .ThenBy(e => e.SaveAthleteId)
            .ToList();
        foreach (EntrantCandidate candidate in ordered)
        {
            if (candidate.SaveAthleteId <= 0)
            {
                throw new InvalidOperationException($"Chaos field for {color} contains corrupt athlete id {candidate.SaveAthleteId}.");
            }

            if (candidate.SportingColor != color)
            {
                throw new InvalidOperationException(
                    $"Chaos entrant {candidate.SaveAthleteId} color {candidate.SportingColor} does not match field color {color}.");
            }
        }

        List<EntrantCandidate> copy = [.. ordered];
        DeterministicShuffle.Shuffle(copy, rng);
        return copy;
    }

    private static void EnsureGlobalUniqueness(Dictionary<SportingColor, List<EntrantCandidate>> shuffled)
    {
        HashSet<int> seen = new(ChaosCupFormat.FieldSize);
        foreach (List<EntrantCandidate> list in shuffled.Values)
        {
            foreach (EntrantCandidate candidate in list)
            {
                if (!seen.Add(candidate.SaveAthleteId))
                {
                    throw new InvalidOperationException($"Chaos field contains duplicate athlete {candidate.SaveAthleteId}.");
                }
            }
        }
    }

    private static IReadOnlyList<IReadOnlyList<EntrantCandidate>> DealGroups(
        Dictionary<SportingColor, List<EntrantCandidate>> shuffled,
        Pcg32V1 rng)
    {
        int groupCount = ChaosCupFormat.GroupCountForStage(0);
        List<IReadOnlyList<EntrantCandidate>> groups = new(groupCount);
        for (int group = 1; group <= groupCount; group++)
        {
            groups.Add(DealSingleGroup(shuffled, group, rng));
        }

        return groups;
    }

    private static IReadOnlyList<EntrantCandidate> DealSingleGroup(
        Dictionary<SportingColor, List<EntrantCandidate>> shuffled,
        int group,
        Pcg32V1 rng)
    {
        List<EntrantCandidate> members = new(ChaosCupFormat.GroupSize);
        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            List<EntrantCandidate> list = shuffled[color];
            int offset = checked((group - 1) * ChaosCupFormat.FirstStagePerColorPerGroup);
            for (int k = 0; k < ChaosCupFormat.FirstStagePerColorPerGroup; k++)
            {
                members.Add(list[offset + k]);
            }
        }

        List<EntrantCandidate> drawOrder = [.. members];
        DeterministicShuffle.Shuffle(drawOrder, rng);
        return drawOrder;
    }
}
