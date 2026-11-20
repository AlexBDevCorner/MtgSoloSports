using MtgSoloSports.SimulationKernel.Random;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.SimulationKernel.Scoring;

/// <summary>
/// Pure fixed-point single-round simulation. Shuffles all athletes with only
/// the supplied <see cref="Pcg32V1"/>, awards base points from the snapshot
/// scoring table by shuffled position, and applies only the stage-start active
/// bonus. All arithmetic is integer-only; no clock, database ordering or
/// ambient randomness is used. The shuffle/score/rank/validate pipeline itself
/// lives in <see cref="RoundPipeline"/>, shared with team-event group rounds
/// (MSS-151); this class owns the league-round contract around it.
/// </summary>
public static class RoundSimulator
{
    /// <summary>
    /// Simulates one league round of <c>LeagueSize</c> athletes and advances
    /// <paramref name="rng"/>. The input roster order is the shuffle input
    /// order; callers must sort deterministically (for example by name
    /// ordinal) before calling.
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

        return RoundPipeline.Simulate(
            roster, rng, rules, fieldSize, "Round", ScoringCalculator.BaseRoundPointsForPosition);
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
}
