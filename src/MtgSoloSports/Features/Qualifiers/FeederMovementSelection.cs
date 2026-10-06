using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Features.Qualifiers;

/// <summary>
/// Pure automatic-movement selection for feeder boundaries (MSS-058).
/// Per sporting color, from just-completed source season standings:
/// F1 ranks 17-24 incumbents, 25-32 auto-relegated to F2; F2 ranks 1-8
/// auto-promoted to F1, 9-16 challengers, 17-24 incumbents, 25-32 auto-relegated
/// to F3; F3 ranks 1-8 auto-promoted to F2, 9-16 challengers, 17-32 remain.
/// No athlete appears in two bands; every movement is one adjacent tier only;
/// no cross-color movement. No RNG consumed; final ranks already deterministic.
/// </summary>
public static class FeederMovementSelection
{
    public sealed record FeederPick(
        int SaveAthleteId,
        int FromLeagueId,
        int FromSeasonRank,
        int SportingColor,
        MovementKind Kind);

    public sealed record ColorBoundaryPlan(
        int SportingColor,
        IReadOnlyList<FeederPick> F1Incumbents,
        IReadOnlyList<FeederPick> F1Relegated,
        IReadOnlyList<FeederPick> F2PromotedToF1,
        IReadOnlyList<FeederPick> F2Challengers,
        IReadOnlyList<FeederPick> F2Incumbents,
        IReadOnlyList<FeederPick> F2Relegated,
        IReadOnlyList<FeederPick> F3PromotedToF2,
        IReadOnlyList<FeederPick> F3Challengers);

    public sealed record FeederPlan(
        IReadOnlyList<ColorBoundaryPlan> PerColor,
        IReadOnlyList<FeederPick> All);

    public static FeederPlan Select(
        IReadOnlyDictionary<int, LeagueEntity> leaguesById,
        IReadOnlyDictionary<int, IReadOnlyList<SeasonStandingEntity>> standingsByLeague,
        IReadOnlyDictionary<int, LeagueEntity> f1ByColor,
        IReadOnlyDictionary<int, LeagueEntity> f2ByColor,
        IReadOnlyDictionary<int, LeagueEntity> f3ByColor,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(leaguesById);
        ArgumentNullException.ThrowIfNull(standingsByLeague);
        ArgumentNullException.ThrowIfNull(f1ByColor);
        ArgumentNullException.ThrowIfNull(f2ByColor);
        ArgumentNullException.ThrowIfNull(f3ByColor);
        ArgumentNullException.ThrowIfNull(rules);

        List<ColorBoundaryPlan> perColor = new(8);
        List<FeederPick> all = new(512);
        foreach (int color in f1ByColor.Keys.OrderBy(c => c))
        {
            if (!f2ByColor.TryGetValue(color, out LeagueEntity? f2) || !f3ByColor.TryGetValue(color, out LeagueEntity? f3))
            {
                throw new InvalidOperationException($"Tiered transition is missing F2/F3 for color {color}.");
            }

            LeagueEntity f1 = f1ByColor[color];
            ColorBoundaryPlan plan = SelectColor(color, f1, f2, f3, standingsByLeague, rules);
            perColor.Add(plan);
            all.AddRange(plan.F1Incumbents);
            all.AddRange(plan.F1Relegated);
            all.AddRange(plan.F2PromotedToF1);
            all.AddRange(plan.F2Challengers);
            all.AddRange(plan.F2Incumbents);
            all.AddRange(plan.F2Relegated);
            all.AddRange(plan.F3PromotedToF2);
            all.AddRange(plan.F3Challengers);
        }

        return new FeederPlan(perColor, all);
    }

    internal static ColorBoundaryPlan SelectColor(
        int color,
        LeagueEntity f1,
        LeagueEntity f2,
        LeagueEntity f3,
        IReadOnlyDictionary<int, IReadOnlyList<SeasonStandingEntity>> standingsByLeague,
        RulesV1 rules)
    {
        IReadOnlyList<SeasonStandingEntity> f1Rows = RequireStandings(standingsByLeague, f1, rules);
        IReadOnlyList<SeasonStandingEntity> f2Rows = RequireStandings(standingsByLeague, f2, rules);
        IReadOnlyList<SeasonStandingEntity> f3Rows = RequireStandings(standingsByLeague, f3, rules);

        List<FeederPick> f1Incumbents = SelectBand(f1Rows, f1.Id, color, 17, 24, MovementKind.FeederQualifierIncumbent);
        List<FeederPick> f1Relegated = SelectBand(f1Rows, f1.Id, color, 25, 32, MovementKind.FeederAutomaticRelegation);
        List<FeederPick> f2Promoted = SelectBand(f2Rows, f2.Id, color, 1, 8, MovementKind.FeederAutomaticPromotion);
        List<FeederPick> f2Challengers = SelectBand(f2Rows, f2.Id, color, 9, 16, MovementKind.FeederQualifierChallenger);
        List<FeederPick> f2Incumbents = SelectBand(f2Rows, f2.Id, color, 17, 24, MovementKind.FeederQualifierIncumbent);
        List<FeederPick> f2Relegated = SelectBand(f2Rows, f2.Id, color, 25, 32, MovementKind.FeederAutomaticRelegation);
        List<FeederPick> f3Promoted = SelectBand(f3Rows, f3.Id, color, 1, 8, MovementKind.FeederAutomaticPromotion);
        List<FeederPick> f3Challengers = SelectBand(f3Rows, f3.Id, color, 9, 16, MovementKind.FeederQualifierChallenger);

        return new ColorBoundaryPlan(
            color,
            f1Incumbents,
            f1Relegated,
            f2Promoted,
            f2Challengers,
            f2Incumbents,
            f2Relegated,
            f3Promoted,
            f3Challengers);
    }

    internal static IReadOnlyList<SeasonStandingEntity> RequireStandings(
        IReadOnlyDictionary<int, IReadOnlyList<SeasonStandingEntity>> standingsByLeague,
        LeagueEntity league,
        RulesV1 rules)
    {
        if (!standingsByLeague.TryGetValue(league.Id, out IReadOnlyList<SeasonStandingEntity>? rows))
        {
            throw new InvalidOperationException($"League '{league.Name}' has no final standings.");
        }

        if (rows.Count != rules.LeagueSize)
        {
            throw new InvalidOperationException(
                $"League '{league.Name}' must have exactly {rules.LeagueSize} final standings, was {rows.Count}.");
        }

        return rows;
    }

    internal static List<FeederPick> SelectBand(
        IReadOnlyList<SeasonStandingEntity> rows,
        int leagueId,
        int color,
        int firstRank,
        int lastRank,
        MovementKind kind)
    {
        List<FeederPick> picks = new(lastRank - firstRank + 1);
        for (int rank = firstRank; rank <= lastRank; rank++)
        {
            SeasonStandingEntity row = rows.SingleOrDefault(r => r.SeasonRank == rank)
                ?? throw new InvalidOperationException($"League {leagueId} is missing final season rank {rank}.");
            if (row.LeagueId != leagueId)
            {
                throw new InvalidOperationException($"Standing for rank {rank} references league {row.LeagueId}, expected {leagueId}.");
            }

            picks.Add(new FeederPick(row.SaveAthleteId, leagueId, row.SeasonRank, color, kind));
        }

        return picks;
    }
}
