using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Features.Superleague.RunQualifier;

/// <summary>
/// Pure qualifier-field selection for a completed Superleague season.
/// The field is exactly 32 athletes: 8 Superleague incumbents from ranks 17-24
/// plus 24 feeder challengers from ranks 2-4 across all eight F1 leagues
/// (F2/F3 never skip tiers into the qualifier).
/// No color quota is enforced and no RNG is consumed; final ranks are already
/// deterministic. Incumbents and challengers are selected by identical rank-band
/// rules; later simulation treats both roles identically (provenance only).
/// </summary>
public static class QualifierFieldSelection
{
    public sealed record QualifierPick(
        int SaveAthleteId,
        string Name,
        int SportingColor,
        int FromLeagueId,
        int FromSeasonRank,
        QualifierRole Role);

    public sealed record QualifierField(
        IReadOnlyList<QualifierPick> Incumbents,
        IReadOnlyList<QualifierPick> Challengers,
        IReadOnlyList<QualifierPick> All);

    /// <summary>
    /// Selects the 32-athlete qualifier field from final standings.
    /// Returns 8 incumbents plus 24 challengers ordered deterministically by
    /// role, source league then rank. Names come from the save athlete table;
    /// sporting colors come from source memberships so returning-color logic
    /// stays consistent with automatic movement. Tiered saves supply 24
    /// feeders but only F1 contributes.
    /// </summary>
    public static QualifierField Select(
        IReadOnlyList<SeasonStandingEntity> superleagueStandings,
        IReadOnlyDictionary<int, IReadOnlyList<SeasonStandingEntity>> feederStandingsByLeague,
        IReadOnlyList<LeagueEntity> feederLeagues,
        LeagueEntity superleague,
        IReadOnlyDictionary<int, SeasonMembershipEntity> membershipByAthlete,
        IReadOnlyDictionary<int, string> namesByAthlete,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(superleagueStandings);
        ArgumentNullException.ThrowIfNull(feederStandingsByLeague);
        ArgumentNullException.ThrowIfNull(feederLeagues);
        ArgumentNullException.ThrowIfNull(superleague);
        ArgumentNullException.ThrowIfNull(membershipByAthlete);
        ArgumentNullException.ThrowIfNull(namesByAthlete);
        ArgumentNullException.ThrowIfNull(rules);

        List<LeagueEntity> sources = ResolveSources(feederLeagues, rules);
        List<QualifierPick> incumbents = SelectIncumbents(
            superleagueStandings, superleague, membershipByAthlete, namesByAthlete, rules);
        List<QualifierPick> challengers = SelectChallengers(
            feederStandingsByLeague, sources, membershipByAthlete, namesByAthlete, rules);

        List<QualifierPick> all = new(incumbents.Count + challengers.Count);
        all.AddRange(incumbents);
        all.AddRange(challengers);
        all.Sort(static (left, right) =>
        {
            int role = left.Role.CompareTo(right.Role);
            if (role != 0)
            {
                return role;
            }

            int league = left.FromLeagueId.CompareTo(right.FromLeagueId);
            return league != 0 ? league : left.FromSeasonRank.CompareTo(right.FromSeasonRank);
        });

        return new QualifierField(incumbents, challengers, all);
    }

    internal static List<LeagueEntity> ResolveSources(IReadOnlyList<LeagueEntity> feederLeagues, RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(feederLeagues);
        ArgumentNullException.ThrowIfNull(rules);
        bool tiered = rules.FeederDivisionsPerColor == 3;
        if (!tiered)
        {
            if (feederLeagues.Count != rules.RegularLeagueCount)
            {
                throw new InvalidOperationException(
                    $"Season must have exactly {rules.RegularLeagueCount} feeder leagues, was {feederLeagues.Count}.");
            }

            return [.. feederLeagues];
        }

        if (feederLeagues.Count != rules.TieredFeederLeagueCount)
        {
            throw new InvalidOperationException(
                $"Tiered season must have exactly {rules.TieredFeederLeagueCount} feeder leagues, was {feederLeagues.Count}.");
        }

        List<LeagueEntity> first = feederLeagues
            .Where(l => l.FeederDivision == (int)SimulationKernel.Leagues.FeederDivision.First)
            .ToList();
        if (first.Count != rules.RegularLeagueCount)
        {
            throw new InvalidOperationException(
                $"Tiered season must have exactly {rules.RegularLeagueCount} F1 leagues, was {first.Count}.");
        }

        return first;
    }

    private static List<QualifierPick> SelectIncumbents(
        IReadOnlyList<SeasonStandingEntity> superleagueStandings,
        LeagueEntity superleague,
        IReadOnlyDictionary<int, SeasonMembershipEntity> membershipByAthlete,
        IReadOnlyDictionary<int, string> namesByAthlete,
        RulesV1 rules)
    {
        if (superleagueStandings.Count != rules.SuperleagueSize)
        {
            throw new InvalidOperationException(
                $"Superleague must have exactly {rules.SuperleagueSize} final standings, was {superleagueStandings.Count}.");
        }

        int first = rules.SuperleagueSafeCount + 1;
        int last = rules.SuperleagueSafeCount + rules.SuperleagueQualifierIncumbentCount;
        List<QualifierPick> picks = new(rules.SuperleagueQualifierIncumbentCount);
        for (int rank = first; rank <= last; rank++)
        {
            SeasonStandingEntity row = superleagueStandings.SingleOrDefault(r => r.SeasonRank == rank)
                ?? throw new InvalidOperationException($"Superleague is missing final season rank {rank}.");
            if (row.LeagueId != superleague.Id)
            {
                throw new InvalidOperationException($"Superleague standing for rank {rank} references league {row.LeagueId}.");
            }

            picks.Add(ToPick(row, superleague.Id, QualifierRole.Incumbent, membershipByAthlete, namesByAthlete));
        }

        return picks;
    }

    private static List<QualifierPick> SelectChallengers(
        IReadOnlyDictionary<int, IReadOnlyList<SeasonStandingEntity>> feederStandingsByLeague,
        IReadOnlyList<LeagueEntity> feederLeagues,
        IReadOnlyDictionary<int, SeasonMembershipEntity> membershipByAthlete,
        IReadOnlyDictionary<int, string> namesByAthlete,
        RulesV1 rules)
    {
        List<QualifierPick> picks = new(rules.FeederQualifierCount);
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

            for (int rank = 2; rank <= 4; rank++)
            {
                SeasonStandingEntity row = rows.SingleOrDefault(r => r.SeasonRank == rank)
                    ?? throw new InvalidOperationException($"League '{league.Name}' is missing final season rank {rank}.");
                picks.Add(ToPick(row, league.Id, QualifierRole.Challenger, membershipByAthlete, namesByAthlete));
            }
        }

        return picks;
    }

    private static QualifierPick ToPick(
        SeasonStandingEntity row,
        int fromLeagueId,
        QualifierRole role,
        IReadOnlyDictionary<int, SeasonMembershipEntity> membershipByAthlete,
        IReadOnlyDictionary<int, string> namesByAthlete)
    {
        if (!membershipByAthlete.TryGetValue(row.SaveAthleteId, out SeasonMembershipEntity? membership))
        {
            throw new InvalidOperationException($"Qualifier athlete {row.SaveAthleteId} has no source membership.");
        }

        if (!namesByAthlete.TryGetValue(row.SaveAthleteId, out string? name) || string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException($"Qualifier athlete {row.SaveAthleteId} has no name.");
        }

        return new QualifierPick(
            row.SaveAthleteId,
            name,
            membership.SportingColor,
            fromLeagueId,
            row.SeasonRank,
            role);
    }
}
