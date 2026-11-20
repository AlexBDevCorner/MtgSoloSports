using MtgSoloSports.SimulationKernel.FixedPoint;
using MtgSoloSports.SimulationKernel.Random;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.SimulationKernel.Scoring;

/// <summary>
/// Shared pure round pipeline (shuffle -> score -> rank -> validate,
/// consolidated in MSS-151) for both individual league/qualifier rounds
/// (<see cref="RoundSimulator"/>) and team-event group rounds
/// (<see cref="Cups.TeamEvent"/>). Parameterized only by the genuine
/// differences between the two consumers: the validation context label
/// ("Round" / "Team group") for error messages and the base-points accessor
/// (snapshot league table vs the Type Cup table extension). Every validation,
/// error message, RNG consumption order and checksum byte is identical to the
/// two pipelines it consolidates. Kernel purity: no HTTP, EF Core, SQLite,
/// filesystem, clock or ambient randomness; the only randomness is the
/// caller-supplied <see cref="Pcg32V1"/>, consumed only by the deterministic
/// shuffle. All arithmetic is checked fixed-point integers.
/// </summary>
internal static class RoundPipeline
{
    /// <summary>
    /// Simulates one round for an explicit field size. The input roster order
    /// is the shuffle input order; callers must sort deterministically (for
    /// example by name ordinal) before calling.
    /// </summary>
    internal static RoundSimulationResult Simulate(
        IReadOnlyList<RoundAthleteInput> roster,
        Pcg32V1 rng,
        RulesV1 rules,
        int fieldSize,
        string contextLabel,
        Func<int, RulesV1, Points> basePointsForPosition)
    {
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(rng);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(contextLabel);
        ArgumentNullException.ThrowIfNull(basePointsForPosition);
        if (roster.Count != fieldSize)
        {
            throw new InvalidOperationException(
                $"{contextLabel} roster must contain exactly {fieldSize} athletes, was {roster.Count}.");
        }

        ValidateRoster(roster, contextLabel);
        Dictionary<int, int> rankBefore = ComputeRanksBefore(roster);
        List<RoundPlacement> unranked =
            ShuffleAndScore(roster, rng, rules, rankBefore, fieldSize, contextLabel, basePointsForPosition);
        List<RoundPlacement> ranked = ApplyRanks(unranked);
        ValidateResult(ranked, rules, fieldSize, contextLabel, basePointsForPosition);
        return new RoundSimulationResult(ranked, rng.Snapshot(), RoundSimulator.ComputeChecksum(ranked));
    }

    private static void ValidateRoster(IReadOnlyList<RoundAthleteInput> roster, string contextLabel)
    {
        HashSet<int> ids = new();
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (RoundAthleteInput athlete in roster)
        {
            CheckInput(athlete, ids, names, contextLabel);
        }
    }

    private static void CheckInput(
        RoundAthleteInput athlete,
        HashSet<int> ids,
        HashSet<string> names,
        string contextLabel)
    {
        if (athlete.AthleteId <= 0)
        {
            throw new InvalidOperationException($"{contextLabel} roster contains invalid athlete id {athlete.AthleteId}.");
        }

        if (string.IsNullOrWhiteSpace(athlete.Name))
        {
            throw new InvalidOperationException($"{contextLabel} roster contains an athlete with an empty name.");
        }

        if (!ids.Add(athlete.AthleteId))
        {
            throw new InvalidOperationException($"{contextLabel} roster contains duplicate athlete id {athlete.AthleteId}.");
        }

        if (!names.Add(athlete.Name))
        {
            throw new InvalidOperationException($"{contextLabel} roster contains duplicate athlete '{athlete.Name}'.");
        }
    }

    private static Dictionary<int, int> ComputeRanksBefore(IReadOnlyList<RoundAthleteInput> roster)
    {
        List<RoundAthleteInput> ordered = new(roster);
        ordered.Sort(static (left, right) =>
        {
            int points = right.CumulativeBefore.Thousandths.CompareTo(left.CumulativeBefore.Thousandths);
            return points != 0 ? points : string.Compare(left.Name, right.Name, StringComparison.Ordinal);
        });

        Dictionary<int, int> ranks = new(ordered.Count);
        for (int i = 0; i < ordered.Count; i++)
        {
            ranks[ordered[i].AthleteId] = i + 1;
        }

        return ranks;
    }

    private static List<RoundPlacement> ShuffleAndScore(
        IReadOnlyList<RoundAthleteInput> roster,
        Pcg32V1 rng,
        RulesV1 rules,
        Dictionary<int, int> rankBefore,
        int fieldSize,
        string contextLabel,
        Func<int, RulesV1, Points> basePointsForPosition)
    {
        List<RoundAthleteInput> shuffled = new(roster);
        DeterministicShuffle.Shuffle(shuffled, rng);

        List<RoundPlacement> placements = new(shuffled.Count);
        for (int i = 0; i < shuffled.Count; i++)
        {
            int position = i + 1;
            if (position > fieldSize)
            {
                throw new InvalidOperationException($"{contextLabel} position {position} exceeds field size {fieldSize}.");
            }

            placements.Add(ScoreAthlete(shuffled[i], position, rules, rankBefore, basePointsForPosition));
        }

        return placements;
    }

    private static RoundPlacement ScoreAthlete(
        RoundAthleteInput athlete,
        int position,
        RulesV1 rules,
        Dictionary<int, int> rankBefore,
        Func<int, RulesV1, Points> basePointsForPosition)
    {
        Points basePoints = basePointsForPosition(position, rules);
        Points finalPoints = ScoringCalculator.ApplyBonus(basePoints, athlete.ActiveBonus);
        Points cumulativeAfter = checked(athlete.CumulativeBefore + finalPoints);
        return new RoundPlacement(
            athlete.AthleteId,
            athlete.Name,
            position,
            basePoints,
            athlete.ActiveBonus,
            finalPoints,
            athlete.CumulativeBefore,
            cumulativeAfter,
            rankBefore[athlete.AthleteId],
            0,
            0);
    }

    private static List<RoundPlacement> ApplyRanks(IReadOnlyList<RoundPlacement> placements)
    {
        Dictionary<int, int> rankAfter = ComputeRanksAfter(placements);
        List<RoundPlacement> ranked = new(placements.Count);
        foreach (RoundPlacement placement in placements)
        {
            int after = rankAfter[placement.AthleteId];
            ranked.Add(placement with
            {
                RankAfter = after,
                RankMovement = placement.RankBefore - after,
            });
        }

        return ranked;
    }

    private static Dictionary<int, int> ComputeRanksAfter(IReadOnlyList<RoundPlacement> placements)
    {
        List<RoundPlacement> ordered = new(placements);
        ordered.Sort(static (left, right) =>
        {
            int points = right.CumulativeAfter.Thousandths.CompareTo(left.CumulativeAfter.Thousandths);
            return points != 0 ? points : string.Compare(left.Name, right.Name, StringComparison.Ordinal);
        });

        Dictionary<int, int> ranks = new(ordered.Count);
        for (int i = 0; i < ordered.Count; i++)
        {
            ranks[ordered[i].AthleteId] = i + 1;
        }

        return ranks;
    }

    private static void ValidateResult(
        IReadOnlyList<RoundPlacement> placements,
        RulesV1 rules,
        int fieldSize,
        string contextLabel,
        Func<int, RulesV1, Points> basePointsForPosition)
    {
        HashSet<int> positions = new();
        HashSet<int> ids = new();
        foreach (RoundPlacement placement in placements)
        {
            CheckPlacement(placement, rules, positions, ids, contextLabel, basePointsForPosition);
        }

        if (positions.Count != fieldSize || !positions.SetEquals(Enumerable.Range(1, fieldSize)))
        {
            throw new InvalidOperationException(
                $"{contextLabel} result must cover positions 1..{fieldSize} exactly once.");
        }
    }

    private static void CheckPlacement(
        RoundPlacement placement,
        RulesV1 rules,
        HashSet<int> positions,
        HashSet<int> ids,
        string contextLabel,
        Func<int, RulesV1, Points> basePointsForPosition)
    {
        if (!positions.Add(placement.Position))
        {
            throw new InvalidOperationException($"{contextLabel} result contains duplicate position {placement.Position}.");
        }

        if (!ids.Add(placement.AthleteId))
        {
            throw new InvalidOperationException($"{contextLabel} result contains duplicate athlete id {placement.AthleteId}.");
        }

        Points expectedBase = basePointsForPosition(placement.Position, rules);
        if (expectedBase.Thousandths != placement.BasePoints.Thousandths)
        {
            throw new InvalidOperationException($"{contextLabel} result base points for position {placement.Position} are corrupt.");
        }

        Points expectedFinal = ScoringCalculator.ApplyBonus(expectedBase, placement.ActiveBonus);
        if (expectedFinal.Thousandths != placement.FinalPoints.Thousandths)
        {
            throw new InvalidOperationException($"{contextLabel} result final points for '{placement.Name}' are corrupt.");
        }

        Points expectedAfter = checked(placement.CumulativeBefore + placement.FinalPoints);
        if (expectedAfter.Thousandths != placement.CumulativeAfter.Thousandths)
        {
            throw new InvalidOperationException($"{contextLabel} result cumulative total for '{placement.Name}' is corrupt.");
        }
    }
}
