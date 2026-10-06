using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Leagues;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Features.Qualifiers;

/// <summary>
/// Pure qualifier-field selection for feeder boundaries (MSS-058).
/// Each event is exactly 16 athletes: 8 incumbents + 8 challengers from the
/// just-completed source season standings, same color, adjacent tiers only.
/// F1↔F2 per color: F1 ranks 17-24 incumbents + F2 ranks 9-16 challengers.
/// F2↔F3 per color: F2 ranks 17-24 incumbents + F3 ranks 9-16 challengers.
/// F2 ranks 1-8 are auto-up only, 25-32 auto-down only; no F2 athlete appears
/// in both boundaries. No RNG consumed; final ranks already deterministic.
/// </summary>
public static class FeederQualifierFieldSelection
{
    public sealed record FeederPick(
        int SaveAthleteId,
        string Name,
        int SportingColor,
        int FromLeagueId,
        int FromSeasonRank,
        QualifierRole Role);

    public sealed record FeederField(
        QualifierBoundary Boundary,
        int SportingColor,
        IReadOnlyList<FeederPick> Incumbents,
        IReadOnlyList<FeederPick> Challengers,
        IReadOnlyList<FeederPick> All);

    public static FeederField Select(
        QualifierBoundary boundary,
        int color,
        IReadOnlyList<SeasonStandingEntity> upperRows,
        LeagueEntity upperLeague,
        IReadOnlyList<SeasonStandingEntity> lowerRows,
        LeagueEntity lowerLeague,
        IReadOnlyDictionary<int, SeasonMembershipEntity> membershipByAthlete,
        IReadOnlyDictionary<int, string> namesByAthlete,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(upperRows);
        ArgumentNullException.ThrowIfNull(upperLeague);
        ArgumentNullException.ThrowIfNull(lowerRows);
        ArgumentNullException.ThrowIfNull(lowerLeague);
        ArgumentNullException.ThrowIfNull(membershipByAthlete);
        ArgumentNullException.ThrowIfNull(namesByAthlete);
        ArgumentNullException.ThrowIfNull(rules);

        if (boundary != QualifierBoundary.Feeder1Feeder2 && boundary != QualifierBoundary.Feeder2Feeder3)
        {
            throw new InvalidOperationException($"Feeder qualifier boundary must be F1↔F2 or F2↔F3, was {boundary}.");
        }

        if (upperRows.Count != rules.LeagueSize || lowerRows.Count != rules.LeagueSize)
        {
            throw new InvalidOperationException(
                $"Feeder qualifier source leagues must each hold exactly {rules.LeagueSize} final standings.");
        }

        List<FeederPick> incumbents = SelectBand(
            upperRows, upperLeague, 17, 24, QualifierRole.Incumbent, membershipByAthlete, namesByAthlete);
        List<FeederPick> challengers = SelectBand(
            lowerRows, lowerLeague, 9, 16, QualifierRole.Challenger, membershipByAthlete, namesByAthlete);

        List<FeederPick> all = new(incumbents.Count + challengers.Count);
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

        return new FeederField(boundary, color, incumbents, challengers, all);
    }

    internal static List<FeederPick> SelectBand(
        IReadOnlyList<SeasonStandingEntity> rows,
        LeagueEntity league,
        int firstRank,
        int lastRank,
        QualifierRole role,
        IReadOnlyDictionary<int, SeasonMembershipEntity> membershipByAthlete,
        IReadOnlyDictionary<int, string> namesByAthlete)
    {
        List<FeederPick> picks = new(lastRank - firstRank + 1);
        for (int rank = firstRank; rank <= lastRank; rank++)
        {
            SeasonStandingEntity row = rows.SingleOrDefault(r => r.SeasonRank == rank)
                ?? throw new InvalidOperationException($"League '{league.Name}' is missing final season rank {rank}.");
            if (row.LeagueId != league.Id)
            {
                throw new InvalidOperationException($"Standing for rank {rank} references league {row.LeagueId}.");
            }

            picks.Add(ToPick(row, league.Id, role, membershipByAthlete, namesByAthlete));
        }

        return picks;
    }

    internal static FeederPick ToPick(
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

        return new FeederPick(
            row.SaveAthleteId,
            name,
            membership.SportingColor,
            fromLeagueId,
            row.SeasonRank,
            role);
    }
}
