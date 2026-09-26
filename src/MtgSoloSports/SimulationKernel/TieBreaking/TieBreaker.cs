using MtgSoloSports.SimulationKernel.Random;

namespace MtgSoloSports.SimulationKernel.TieBreaking;

/// <summary>
/// Deterministic generic ranking helpers for stage/season standings.
/// Pure integer comparisons only: placement-count vectors, then raw totals,
/// then a supplied seeded final draw. No clock, GUID ordering or
/// <see cref="System.Random"/> is used anywhere; seeded draws come from
/// <see cref="Pcg32V1"/> supplied by the caller.
/// </summary>
public static class TieBreaker
{
    /// <summary>
    /// Compares deterministic keys best-first. Returns negative when
    /// <paramref name="left"/> ranks ahead, positive when it ranks behind,
    /// zero when deterministically tied.
    /// </summary>
    public static int CompareDeterministic(DeterministicTieBreakKey left, DeterministicTieBreakKey right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        ValidateCountVector(left.StagePlaceCounts, nameof(left));
        ValidateCountVector(right.StagePlaceCounts, nameof(right));
        ValidateCountVector(left.RoundPlaceCounts, nameof(left));
        ValidateCountVector(right.RoundPlaceCounts, nameof(right));

        if (left.StagePlaceCounts.Count != right.StagePlaceCounts.Count)
        {
            throw new InvalidOperationException(
                $"Stage place-count vectors must have equal length ({left.StagePlaceCounts.Count} vs {right.StagePlaceCounts.Count}).");
        }

        if (left.RoundPlaceCounts.Count != right.RoundPlaceCounts.Count)
        {
            throw new InvalidOperationException(
                $"Round place-count vectors must have equal length ({left.RoundPlaceCounts.Count} vs {right.RoundPlaceCounts.Count}).");
        }

        for (int i = 0; i < left.StagePlaceCounts.Count; i++)
        {
            if (left.StagePlaceCounts[i] != right.StagePlaceCounts[i])
            {
                // More top finishes ranks ahead.
                return left.StagePlaceCounts[i] > right.StagePlaceCounts[i] ? -1 : 1;
            }
        }

        for (int i = 0; i < left.RoundPlaceCounts.Count; i++)
        {
            if (left.RoundPlaceCounts[i] != right.RoundPlaceCounts[i])
            {
                return left.RoundPlaceCounts[i] > right.RoundPlaceCounts[i] ? -1 : 1;
            }
        }

        if (left.RawTotalThousandths != right.RawTotalThousandths)
        {
            return left.RawTotalThousandths > right.RawTotalThousandths ? -1 : 1;
        }

        return 0;
    }

    /// <summary>
    /// Compares full keys best-first. Deterministic fields decide first; the
    /// seeded final draw (smaller wins) decides only a full deterministic tie.
    /// </summary>
    public static int Compare(TieBreakKey left, TieBreakKey right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        int deterministic = CompareDeterministic(
            new DeterministicTieBreakKey(left.StagePlaceCounts, left.RoundPlaceCounts, left.RawTotalThousandths),
            new DeterministicTieBreakKey(right.StagePlaceCounts, right.RoundPlaceCounts, right.RawTotalThousandths));

        if (deterministic != 0)
        {
            return deterministic;
        }

        return left.FinalDraw.CompareTo(right.FinalDraw);
    }

    /// <summary>
    /// Ranks entries best-first using caller-supplied keys with unique final draws.
    /// Throws when two entries tie on every field including the draw, because the
    /// result would otherwise depend on input enumeration order.
    /// </summary>
    public static IReadOnlyList<RankedEntry<T>> Rank<T>(IReadOnlyList<T> entries, Func<T, TieBreakKey> keySelector)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(keySelector);

        if (entries.Count == 0)
        {
            return [];
        }

        var resolved = new (T Entry, TieBreakKey Key)[entries.Count];
        for (int i = 0; i < entries.Count; i++)
        {
            TieBreakKey key = keySelector(entries[i])
                ?? throw new InvalidOperationException("Tie-break key selector returned null.");
            resolved[i] = (entries[i], key);
        }

        Array.Sort(resolved, static (a, b) => Compare(a.Key, b.Key));

        var ranked = new RankedEntry<T>[resolved.Length];
        for (int i = 0; i < resolved.Length; i++)
        {
            if (i > 0 && Compare(resolved[i - 1].Key, resolved[i].Key) == 0)
            {
                throw new InvalidOperationException(
                    "Duplicate final draw within a deterministically tied group. Supply unique seeded draws.");
            }

            ranked[i] = new RankedEntry<T>(resolved[i].Entry, i + 1);
        }

        return ranked;
    }

    /// <summary>
    /// Ranks entries best-first, breaking exact deterministic ties with the
    /// versioned RNG. Only deterministically tied groups consume RNG, in
    /// best-group-first order; untied entries never advance the RNG. Tied
    /// groups are canonically ordered by identifier before shuffling so the
    /// result is independent of input enumeration order.
    /// </summary>
    public static IReadOnlyList<RankedEntry<T>> RankWithSeededDraw<T>(
        IReadOnlyList<T> entries,
        Func<T, DeterministicTieBreakKey> keySelector,
        Func<T, string> idSelector,
        Pcg32V1 rng)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(keySelector);
        ArgumentNullException.ThrowIfNull(idSelector);
        ArgumentNullException.ThrowIfNull(rng);

        if (entries.Count == 0)
        {
            return [];
        }

        var resolved = ResolveSeeded(entries, keySelector, idSelector);
        List<T> ordered = OrderSeeded(resolved, rng);
        return AssignRanks(ordered);
    }

    private static (T Entry, DeterministicTieBreakKey Key, string Id)[] ResolveSeeded<T>(
        IReadOnlyList<T> entries,
        Func<T, DeterministicTieBreakKey> keySelector,
        Func<T, string> idSelector)
    {
        var resolved = new (T Entry, DeterministicTieBreakKey Key, string Id)[entries.Count];
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < entries.Count; i++)
        {
            DeterministicTieBreakKey key = keySelector(entries[i])
                ?? throw new InvalidOperationException("Tie-break key selector returned null.");
            string id = idSelector(entries[i])
                ?? throw new InvalidOperationException("Tie-break id selector returned null.");
            if (!seenIds.Add(id))
            {
                throw new InvalidOperationException($"Duplicate tie-break identifier '{id}'. Identifiers must be unique.");
            }

            // Eager validation so malformed vectors fail before any RNG use.
            CompareDeterministic(key, key);
            resolved[i] = (entries[i], key, id);
        }

        Array.Sort(resolved, static (a, b) =>
        {
            int deterministic = CompareDeterministic(a.Key, b.Key);
            return deterministic != 0
                ? deterministic
                : StringComparer.Ordinal.Compare(a.Id, b.Id);
        });
        return resolved;
    }

    private static List<T> OrderSeeded<T>(
        (T Entry, DeterministicTieBreakKey Key, string Id)[] resolved,
        Pcg32V1 rng)
    {
        var ordered = new List<T>(resolved.Length);
        int index = 0;
        while (index < resolved.Length)
        {
            int runEnd = index + 1;
            while (runEnd < resolved.Length && CompareDeterministic(resolved[index].Key, resolved[runEnd].Key) == 0)
            {
                runEnd++;
            }

            if (runEnd - index == 1)
            {
                ordered.Add(resolved[index].Entry);
            }
            else
            {
                // Canonical id order above makes the shuffle input (and therefore
                // the RNG consumption) independent of caller enumeration order.
                var tied = new List<T>(runEnd - index);
                for (int k = index; k < runEnd; k++)
                {
                    tied.Add(resolved[k].Entry);
                }

                DeterministicShuffle.Shuffle(tied, rng);
                ordered.AddRange(tied);
            }

            index = runEnd;
        }

        return ordered;
    }

    private static IReadOnlyList<RankedEntry<T>> AssignRanks<T>(List<T> ordered)
    {
        var ranked = new RankedEntry<T>[ordered.Count];
        for (int i = 0; i < ordered.Count; i++)
        {
            ranked[i] = new RankedEntry<T>(ordered[i], i + 1);
        }

        return ranked;
    }
    private static void ValidateCountVector(IReadOnlyList<int> vector, string paramName)
    {
        if (vector is null)
        {
            throw new InvalidOperationException($"Tie-break count vector '{paramName}' must not be null.");
        }

        for (int i = 0; i < vector.Count; i++)
        {
            if (vector[i] < 0)
            {
                throw new ArgumentOutOfRangeException(paramName, "Placement counts cannot be negative.");
            }
        }
    }
}
