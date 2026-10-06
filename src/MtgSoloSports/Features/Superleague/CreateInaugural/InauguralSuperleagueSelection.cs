using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Features.Superleague.CreateInaugural;

/// <summary>
/// Pure inaugural selection: places 1-4 from each Season 1 F1 feeder league enter
/// the first 32-athlete Superleague (8 x 4 = 32). For tiered saves only F1 is a
/// source; F2/F3 and pool athletes never skip tiers directly into the inaugural
/// Superleague. Input is the persisted final Season 1 standings; no RNG is
/// consumed because final ranks are already deterministic (seeded draws resolved
/// at season finalization).
/// </summary>
public static class InauguralSuperleagueSelection
{
    public sealed record InauguralPick(int SaveAthleteId, int FromLeagueId, int FromSeasonRank);

    /// <summary>
    /// Selects the inaugural Superleague field from final Season 1 standings.
    /// Returns 32 picks ordered by source league id then season rank so
    /// persistence order is deterministic without relying on database order.
    /// v1 saves supply 8 feeders; tiered saves supply 24 (F1/F2/F3) and only
    /// the 8 F1 leagues contribute.
    /// </summary>
    public static IReadOnlyList<InauguralPick> Select(
        IReadOnlyList<SeasonStandingEntity> seasonStandings,
        IReadOnlyList<LeagueEntity> feederLeagues,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(seasonStandings);
        ArgumentNullException.ThrowIfNull(feederLeagues);
        ArgumentNullException.ThrowIfNull(rules);

        List<LeagueEntity> sources = ResolveSources(feederLeagues, rules);
        Dictionary<int, LeagueEntity> leaguesById = sources.ToDictionary(l => l.Id);
        List<InauguralPick> picks = new(rules.SuperleagueSize);
        foreach (LeagueEntity league in sources.OrderBy(l => l.Id))
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
                    $"Season 1 must have exactly {rules.RegularLeagueCount} feeder leagues, was {feederLeagues.Count}.");
            }

            return [.. feederLeagues];
        }

        if (feederLeagues.Count != rules.Season1FeederLeagueCount)
        {
            throw new InvalidOperationException(
                $"Tiered Season 1 must have exactly {rules.Season1FeederLeagueCount} feeder leagues (F1/F2/F3), was {feederLeagues.Count}.");
        }

        List<LeagueEntity> first = feederLeagues
            .Where(l => l.FeederDivision == (int)SimulationKernel.Leagues.FeederDivision.First)
            .ToList();
        if (first.Count != rules.RegularLeagueCount)
        {
            throw new InvalidOperationException(
                $"Tiered Season 1 must have exactly {rules.RegularLeagueCount} F1 leagues, was {first.Count}.");
        }

        return first;
    }
}
