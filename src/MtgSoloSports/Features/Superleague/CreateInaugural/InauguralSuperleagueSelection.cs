using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Features.Superleague.CreateInaugural;

/// <summary>
/// Pure inaugural selection: places 1-4 from each Season 1 feeder league enter
/// the first 32-athlete Superleague (8 x 4 = 32). Input is the persisted final
/// Season 1 standings; no RNG is consumed because final ranks are already
/// deterministic (seeded draws resolved at season finalization).
/// </summary>
public static class InauguralSuperleagueSelection
{
    public sealed record InauguralPick(int SaveAthleteId, int FromLeagueId, int FromSeasonRank);

    /// <summary>
    /// Selects the inaugural Superleague field from final Season 1 standings.
    /// Returns 32 picks ordered by source league id then season rank so
    /// persistence order is deterministic without relying on database order.
    /// </summary>
    public static IReadOnlyList<InauguralPick> Select(
        IReadOnlyList<SeasonStandingEntity> seasonStandings,
        IReadOnlyList<LeagueEntity> feederLeagues,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(seasonStandings);
        ArgumentNullException.ThrowIfNull(feederLeagues);
        ArgumentNullException.ThrowIfNull(rules);

        if (feederLeagues.Count != rules.RegularLeagueCount)
        {
            throw new InvalidOperationException(
                $"Season 1 must have exactly {rules.RegularLeagueCount} feeder leagues, was {feederLeagues.Count}.");
        }

        Dictionary<int, LeagueEntity> leaguesById = feederLeagues.ToDictionary(l => l.Id);
        List<InauguralPick> picks = new(rules.SuperleagueSize);
        foreach (LeagueEntity league in feederLeagues.OrderBy(l => l.Id))
        {
            List<SeasonStandingEntity> leagueRows = seasonStandings
                .Where(r => r.LeagueId == league.Id)
                .OrderBy(r => r.SeasonRank)
                .ToList();
            if (leagueRows.Count != rules.LeagueSize)
            {
                throw new InvalidOperationException(
                    $"League '{league.Name}' must have exactly {rules.LeagueSize} final standings, was {leagueRows.Count}.");
            }

            for (int rank = 1; rank <= rules.InauguralQualifiedPerLeague; rank++)
            {
                SeasonStandingEntity row = leagueRows.SingleOrDefault(r => r.SeasonRank == rank)
                    ?? throw new InvalidOperationException(
                        $"League '{league.Name}' is missing final season rank {rank}.");
                if (!leaguesById.ContainsKey(row.LeagueId))
                {
                    throw new InvalidOperationException(
                        $"Standing for athlete {row.SaveAthleteId} references unknown league {row.LeagueId}.");
                }

                picks.Add(new InauguralPick(row.SaveAthleteId, row.LeagueId, row.SeasonRank));
            }
        }

        if (picks.Count != rules.SuperleagueSize)
        {
            throw new InvalidOperationException(
                $"Inaugural Superleague must select exactly {rules.SuperleagueSize} athletes, was {picks.Count}.");
        }

        return picks;
    }
}
