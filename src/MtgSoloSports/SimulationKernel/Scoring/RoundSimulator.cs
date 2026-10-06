using MtgSoloSports.SimulationKernel.FixedPoint;
using MtgSoloSports.SimulationKernel.Random;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.SimulationKernel.Scoring;

/// <summary>
/// Pure fixed-point single-round simulation. Shuffles all athletes with only
/// the supplied <see cref="Pcg32V1"/>, awards base points from the snapshot
/// scoring table by shuffled position, and applies only the stage-start active
/// bonus. All arithmetic is integer-only; no clock, database ordering or
/// ambient randomness is used.
/// </summary>
public static class RoundSimulator
{
    /// <summary>
    /// Simulates one round and advances <paramref name="rng"/>.
    /// The input roster order is the shuffle input order; callers must sort
    /// deterministically (for example by name ordinal) before calling.
    /// </summary>
    public static RoundSimulationResult Simulate(
        IReadOnlyList<RoundAthleteInput> roster,
        Pcg32V1 rng,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(rng);
        ArgumentNullException.ThrowIfNull(rules);
        rules.Validate();

        if (roster.Count != rules.LeagueSize)
        {
            throw new InvalidOperationException(
                $"Round roster must contain exactly {rules.LeagueSize} athletes, was {roster.Count}.");
        }

        return SimulateWithFieldSize(roster, rng, rules, rules.LeagueSize);
    }

    /// <summary>
    /// Simulates one round for an explicit field size (MSS-058): league rounds
    /// use <c>LeagueSize</c> (32); feeder qualifiers use 16. Base points come
    /// from the snapshot scoring-table prefix, so positions 1..16 score
    /// identically in both contexts. Never assumes
    /// <c>LeagueSize == qualifier size</c>.
    /// </summary>
    public static RoundSimulationResult SimulateWithFieldSize(
        IReadOnlyList<RoundAthleteInput> roster,
        Pcg32V1 rng,
        RulesV1 rules,
        int fieldSize)
    {
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(rng);
        ArgumentNullException.ThrowIfNull(rules);
        rules.Validate();

        if (fieldSize != rules.LeagueSize && fieldSize != rules.FeederQualifierSize && fieldSize != rules.QualifierSize)
        {
            throw new InvalidOperationException(
                $"Round field size must be {rules.LeagueSize}, {rules.FeederQualifierSize} or {rules.QualifierSize}, was {fieldSize}.");
        }

        if (roster.Count != fieldSize)
        {
            throw new InvalidOperationException(
                $"Round roster must contain exactly {fieldSize} athletes, was {roster.Count}.");
        }

        ValidateInputs(roster);
        Dictionary<int, int> rankBefore = ComputeStageRanks(roster);
        List<RoundPlacement> unranked = ShuffleAndScore(roster, rng, rules, rankBefore);
        List<RoundPlacement> ranked = ApplyRanks(unranked);
        ValidateResultWithFieldSize(ranked, rules, fieldSize);
        return new RoundSimulationResult(ranked, rng.Snapshot(), ComputeChecksum(ranked));
    }

    /// <summary>
    /// Fingerprints a finishing order: lowercase hex SHA-256 over lines of
    /// <c>Position:AthleteId:Name:Base:Active:Final</c> in finishing order.
    /// SHA-256 is a content fingerprint here, not sporting randomness.
    /// </summary>
    public static string ComputeChecksum(IReadOnlyList<RoundPlacement> placements)
    {
        ArgumentNullException.ThrowIfNull(placements);
        using System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create();
        System.Text.StringBuilder builder = new();
        foreach (RoundPlacement placement in placements)
        {
            AppendPlacement(builder, placement);
        }

        byte[] hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexStringLower(hash);
    }

    private static void AppendPlacement(System.Text.StringBuilder builder, RoundPlacement placement)
    {
        builder.Append(placement.Position.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.Append(':');
        builder.Append(placement.AthleteId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.Append(':');
        builder.Append(placement.Name);
        builder.Append(':');
        builder.Append(placement.BasePoints.Thousandths.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.Append(':');
        builder.Append(placement.ActiveBonus.Thousandths.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.Append(':');
        builder.Append(placement.FinalPoints.Thousandths.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.Append('\n');
    }

    private static List<RoundPlacement> ShuffleAndScore(
        IReadOnlyList<RoundAthleteInput> roster,
        Pcg32V1 rng,
        RulesV1 rules,
        Dictionary<int, int> rankBefore)
    {
        List<RoundAthleteInput> shuffled = new(roster);
        DeterministicShuffle.Shuffle(shuffled, rng);

        List<RoundPlacement> placements = new(shuffled.Count);
        for (int i = 0; i < shuffled.Count; i++)
        {
            placements.Add(ScoreAthlete(shuffled[i], i + 1, rules, rankBefore));
        }

        return placements;
    }

    private static RoundPlacement ScoreAthlete(
        RoundAthleteInput athlete,
        int position,
        RulesV1 rules,
        Dictionary<int, int> rankBefore)
    {
        Points basePoints = ScoringCalculator.BaseRoundPointsForPosition(position, rules);
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
        Dictionary<int, int> rankAfter = ComputePlacementRanks(placements);
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

    private static void ValidateInputs(IReadOnlyList<RoundAthleteInput> roster)
    {
        HashSet<int> ids = new();
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (RoundAthleteInput athlete in roster)
        {
            CheckInput(athlete, ids, names);
        }
    }

    private static void CheckInput(RoundAthleteInput athlete, HashSet<int> ids, HashSet<string> names)
    {
        if (athlete.AthleteId <= 0)
        {
            throw new InvalidOperationException($"Round roster contains invalid athlete id {athlete.AthleteId}.");
        }

        if (string.IsNullOrWhiteSpace(athlete.Name))
        {
            throw new InvalidOperationException("Round roster contains an athlete with an empty name.");
        }

        if (!ids.Add(athlete.AthleteId))
        {
            throw new InvalidOperationException($"Round roster contains duplicate athlete id {athlete.AthleteId}.");
        }

        if (!names.Add(athlete.Name))
        {
            throw new InvalidOperationException($"Round roster contains duplicate athlete '{athlete.Name}'.");
        }
    }

    private static Dictionary<int, int> ComputeStageRanks(IReadOnlyList<RoundAthleteInput> roster)
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

    private static Dictionary<int, int> ComputePlacementRanks(IReadOnlyList<RoundPlacement> placements)
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

    private static void ValidateResult(IReadOnlyList<RoundPlacement> placements, RulesV1 rules)
    {
        ValidateResultWithFieldSize(placements, rules, rules.LeagueSize);
    }

    private static void ValidateResultWithFieldSize(
        IReadOnlyList<RoundPlacement> placements,
        RulesV1 rules,
        int fieldSize)
    {
        HashSet<int> positions = new();
        HashSet<int> ids = new();
        foreach (RoundPlacement placement in placements)
        {
            CheckPlacement(placement, rules, positions, ids);
        }

        if (positions.Count != fieldSize || !positions.SetEquals(Enumerable.Range(1, fieldSize)))
        {
            throw new InvalidOperationException(
                $"Round result must cover positions 1..{fieldSize} exactly once.");
        }
    }

    private static void CheckPlacement(
        RoundPlacement placement,
        RulesV1 rules,
        HashSet<int> positions,
        HashSet<int> ids)
    {
        if (!positions.Add(placement.Position))
        {
            throw new InvalidOperationException($"Round result contains duplicate position {placement.Position}.");
        }

        if (!ids.Add(placement.AthleteId))
        {
            throw new InvalidOperationException($"Round result contains duplicate athlete id {placement.AthleteId}.");
        }

        Points expectedBase = ScoringCalculator.BaseRoundPointsForPosition(placement.Position, rules);
        if (expectedBase.Thousandths != placement.BasePoints.Thousandths)
        {
            throw new InvalidOperationException($"Round result base points for position {placement.Position} are corrupt.");
        }

        Points expectedFinal = ScoringCalculator.ApplyBonus(expectedBase, placement.ActiveBonus);
        if (expectedFinal.Thousandths != placement.FinalPoints.Thousandths)
        {
            throw new InvalidOperationException($"Round result final points for '{placement.Name}' are corrupt.");
        }

        Points expectedAfter = checked(placement.CumulativeBefore + placement.FinalPoints);
        if (expectedAfter.Thousandths != placement.CumulativeAfter.Thousandths)
        {
            throw new InvalidOperationException($"Round result cumulative total for '{placement.Name}' is corrupt.");
        }
    }
}
