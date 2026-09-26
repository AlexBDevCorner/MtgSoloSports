using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Features.Superleague.ResolveAutomaticMovement;

/// <summary>
/// Pure automatic-movement selection for a completed Superleague season.
/// Superleague ranks 1-16 are safe (no movement row), 17-24 enter the
/// qualifier as incumbents, 25-32 are automatically relegated, every feeder
/// champion is automatically promoted, and feeder ranks 2-4 enter the
/// qualifier as challengers. No color quota is enforced: the Superleague may
/// hold any color composition and several relegated athletes may share one
/// returning color. No RNG is consumed; final ranks are already deterministic.
/// </summary>
public static class AutomaticMovementSelection
{
    public sealed record AutomaticPick(int SaveAthleteId, int FromLeagueId, int FromSeasonRank, MovementKind Kind);

    public sealed record AutomaticPlan(
        IReadOnlyList<AutomaticPick> Promotions,
        IReadOnlyList<AutomaticPick> Relegations,
        IReadOnlyList<AutomaticPick> QualifierIncumbents,
        IReadOnlyList<AutomaticPick> QualifierChallengers,
        IReadOnlyList<AutomaticPick> All);

    /// <summary>
    /// Selects the 48 automatic/qualifier-candidate picks from final standings.
    /// Returns promotions (8), relegations (8), incumbents (8) and challengers
    /// (24) ordered deterministically by kind, source league then rank.
    /// </summary>
    public static AutomaticPlan Select(
        IReadOnlyList<SeasonStandingEntity> superleagueStandings,
        IReadOnlyDictionary<int, IReadOnlyList<SeasonStandingEntity>> feederStandingsByLeague,
        IReadOnlyList<LeagueEntity> feederLeagues,
        LeagueEntity superleague,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(superleagueStandings);
        ArgumentNullException.ThrowIfNull(feederStandingsByLeague);
        ArgumentNullException.ThrowIfNull(feederLeagues);
        ArgumentNullException.ThrowIfNull(superleague);
        ArgumentNullException.ThrowIfNull(rules);

        if (feederLeagues.Count != rules.RegularLeagueCount)
        {
            throw new InvalidOperationException(
                $"Season must have exactly {rules.RegularLeagueCount} feeder leagues, was {feederLeagues.Count}.");
        }

        List<AutomaticPick> promotions = SelectPromotions(feederStandingsByLeague, feederLeagues, rules);
        List<AutomaticPick> relegations = SelectRelegations(superleagueStandings, superleague, rules);
        List<AutomaticPick> incumbents = SelectIncumbents(superleagueStandings, superleague, rules);
        List<AutomaticPick> challengers = SelectChallengers(feederStandingsByLeague, feederLeagues, rules);

        List<AutomaticPick> all = new(promotions.Count + relegations.Count + incumbents.Count + challengers.Count);
        all.AddRange(promotions);
        all.AddRange(relegations);
        all.AddRange(incumbents);
        all.AddRange(challengers);
        all.Sort(static (left, right) =>
        {
            int kind = left.Kind.CompareTo(right.Kind);
            if (kind != 0)
            {
                return kind;
            }

            int league = left.FromLeagueId.CompareTo(right.FromLeagueId);
            return league != 0 ? league : left.FromSeasonRank.CompareTo(right.FromSeasonRank);
        });

        return new AutomaticPlan(promotions, relegations, incumbents, challengers, all);
    }

    private static List<AutomaticPick> SelectPromotions(
        IReadOnlyDictionary<int, IReadOnlyList<SeasonStandingEntity>> feederStandingsByLeague,
        IReadOnlyList<LeagueEntity> feederLeagues,
        RulesV1 rules)
    {
        List<AutomaticPick> picks = new(rules.FeederAutoPromotedCount);
        foreach (LeagueEntity league in feederLeagues.OrderBy(l => l.Id))
        {
            if (!feederStandingsByLeague.TryGetValue(league.Id, out IReadOnlyList<SeasonStandingEntity>? rows))
            {
                throw new InvalidOperationException($"League '{league.Name}' has no final standings.");
            }

            if (rows.Count != rules.LeagueSize)
            {
                throw new InvalidOperationException(
                    $"League '{league.Name}' must have exactly {rules.LeagueSize} final standings, was {rows.Count}.");
            }

            SeasonStandingEntity champion = rows.SingleOrDefault(r => r.SeasonRank == 1)
                ?? throw new InvalidOperationException($"League '{league.Name}' is missing final season rank 1.");
            picks.Add(new AutomaticPick(champion.SaveAthleteId, league.Id, champion.SeasonRank, MovementKind.AutomaticPromotion));
        }

        return picks;
    }

    private static List<AutomaticPick> SelectRelegations(
        IReadOnlyList<SeasonStandingEntity> superleagueStandings,
        LeagueEntity superleague,
        RulesV1 rules)
    {
        return SelectSuperleagueBand(
            superleagueStandings, superleague, rules, FirstRelegatedRank(rules), rules.LeagueSize, MovementKind.AutomaticRelegation);
    }

    private static List<AutomaticPick> SelectIncumbents(
        IReadOnlyList<SeasonStandingEntity> superleagueStandings,
        LeagueEntity superleague,
        RulesV1 rules)
    {
        int first = rules.SuperleagueSafeCount + 1;
        int last = rules.SuperleagueSafeCount + rules.SuperleagueQualifierIncumbentCount;
        return SelectSuperleagueBand(superleagueStandings, superleague, rules, first, last, MovementKind.QualifierIncumbent);
    }

    private static List<AutomaticPick> SelectSuperleagueBand(
        IReadOnlyList<SeasonStandingEntity> superleagueStandings,
        LeagueEntity superleague,
        RulesV1 rules,
        int firstRank,
        int lastRank,
        MovementKind kind)
    {
        if (superleagueStandings.Count != rules.SuperleagueSize)
        {
            throw new InvalidOperationException(
                $"Superleague must have exactly {rules.SuperleagueSize} final standings, was {superleagueStandings.Count}.");
        }

        List<AutomaticPick> picks = new(lastRank - firstRank + 1);
        for (int rank = firstRank; rank <= lastRank; rank++)
        {
            SeasonStandingEntity row = superleagueStandings.SingleOrDefault(r => r.SeasonRank == rank)
                ?? throw new InvalidOperationException($"Superleague is missing final season rank {rank}.");
            if (row.LeagueId != superleague.Id)
            {
                throw new InvalidOperationException($"Superleague standing for rank {rank} references league {row.LeagueId}.");
            }

            picks.Add(new AutomaticPick(row.SaveAthleteId, row.LeagueId, row.SeasonRank, kind));
        }

        return picks;
    }

    private static List<AutomaticPick> SelectChallengers(
        IReadOnlyDictionary<int, IReadOnlyList<SeasonStandingEntity>> feederStandingsByLeague,
        IReadOnlyList<LeagueEntity> feederLeagues,
        RulesV1 rules)
    {
        List<AutomaticPick> picks = new(rules.FeederQualifierCount);
        foreach (LeagueEntity league in feederLeagues.OrderBy(l => l.Id))
        {
            if (!feederStandingsByLeague.TryGetValue(league.Id, out IReadOnlyList<SeasonStandingEntity>? rows))
            {
                throw new InvalidOperationException($"League '{league.Name}' has no final standings.");
            }

            for (int rank = 2; rank <= 4; rank++)
            {
                SeasonStandingEntity row = rows.SingleOrDefault(r => r.SeasonRank == rank)
                    ?? throw new InvalidOperationException($"League '{league.Name}' is missing final season rank {rank}.");
                picks.Add(new AutomaticPick(row.SaveAthleteId, league.Id, row.SeasonRank, MovementKind.QualifierChallenger));
            }
        }

        return picks;
    }

    private static int FirstRelegatedRank(RulesV1 rules)
    {
        return rules.SuperleagueSafeCount + rules.SuperleagueQualifierIncumbentCount + 1;
    }
}
