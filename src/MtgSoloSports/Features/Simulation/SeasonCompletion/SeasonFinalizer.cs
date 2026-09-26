using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Random;
using MtgSoloSports.SimulationKernel.Rules;
using MtgSoloSports.SimulationKernel.Seasons;

namespace MtgSoloSports.Features.Simulation.SeasonCompletion;

/// <summary>
/// Shared season-finalization primitive. When every active league has
/// completed Stage 32, accumulates each league's 32 stage championship
/// points into deterministic final season standings (champion at rank 1)
/// with full tie-break inputs retained, marks the season complete, and
/// commits the RNG-after state in the same transaction as the standings.
/// Higher-level commands (single-league <c>CompleteStage</c> and bulk
/// <c>CompleteStageForAllLeagues</c>) share this primitive so bulk execution
/// is behaviorally equivalent to sequential single-league operations in
/// canonical league order. Leagues finalize in ascending league-id order so
/// seeded draws consume the versioned RNG deterministically.
/// </summary>
public static class SeasonFinalizer
{
    /// <summary>
    /// Attempts season finalization. Returns finalized=false when the season
    /// is not yet ready (not all leagues completed Stage 32). When ready,
    /// persists 32 <c>SeasonStanding</c> rows per active league plus the
    /// season-complete flag and RNG-after state, all in the caller's
    /// transaction. Throws on already-finalized or corrupt state.
    /// </summary>
    public static async Task<(bool Finalized, Pcg32State RngAfter)> TryFinalizeSeasonAsync(
        SaveDbContext context,
        SeasonEntity season,
        RulesV1 rules,
        Pcg32V1 rng,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(season);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(rng);

        await EnsureNotAlreadyFinalizedAsync(context, season, cancellationToken).ConfigureAwait(false);
        List<LeagueEntity> leagues = await LoadLeaguesAsync(context, season, cancellationToken).ConfigureAwait(false);
        bool ready = await IsSeasonReadyAsync(context, season, leagues, rules, cancellationToken).ConfigureAwait(false);
        if (!ready)
        {
            return (false, rng.Snapshot());
        }

        Dictionary<int, string> names = await LoadAthleteNamesAsync(context, cancellationToken).ConfigureAwait(false);
        foreach (LeagueEntity league in leagues)
        {
            await FinalizeOneLeagueAsync(context, season, league, rules, rng, names, cancellationToken).ConfigureAwait(false);
        }

        season.IsComplete = true;
        Pcg32State rngAfter = rng.Snapshot();
        context.ApplyRngState(rngAfter);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await VerifyFinalizedAsync(context, season, leagues, rules, cancellationToken).ConfigureAwait(false);
        return (true, rngAfter);
    }

    internal static async Task EnsureNotAlreadyFinalizedAsync(
        SaveDbContext context,
        SeasonEntity season,
        CancellationToken cancellationToken)
    {
        if (season.IsComplete)
        {
            throw new InvalidOperationException($"Season {season.SeasonNumber} is already finalized.");
        }

        bool hasStandings = await context.SeasonStandings
            .AnyAsync(e => e.SeasonId == season.Id, cancellationToken)
            .ConfigureAwait(false);
        if (hasStandings)
        {
            throw new InvalidOperationException($"Season {season.SeasonNumber} already has season standings.");
        }
    }

    internal static async Task<List<LeagueEntity>> LoadLeaguesAsync(
        SaveDbContext context,
        SeasonEntity season,
        CancellationToken cancellationToken)
    {
        List<LeagueEntity> leagues = await context.Leagues
            .Where(e => e.SeasonId == season.Id)
            .OrderBy(e => e.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (leagues.Count == 0)
        {
            throw new InvalidOperationException($"Season {season.SeasonNumber} has no active leagues.");
        }

        return leagues;
    }

    internal static async Task<bool> IsSeasonReadyAsync(
        SaveDbContext context,
        SeasonEntity season,
        List<LeagueEntity> leagues,
        RulesV1 rules,
        CancellationToken cancellationToken)
    {
        List<StageEntity> stages = await context.Stages
            .AsNoTracking()
            .Where(e => e.SeasonId == season.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (LeagueEntity league in leagues)
        {
            StageEntity? finalStage = stages.SingleOrDefault(e =>
                e.LeagueId == league.Id && e.StageNumber == rules.StagesPerSeason);
            if (finalStage is null || !finalStage.IsComplete)
            {
                return false;
            }
        }

        return true;
    }

    internal static async Task FinalizeOneLeagueAsync(
        SaveDbContext context,
        SeasonEntity season,
        LeagueEntity league,
        RulesV1 rules,
        Pcg32V1 rng,
        Dictionary<int, string> names,
        CancellationToken cancellationToken)
    {
        List<List<SeasonStageEntry>> stageGroups = await BuildStageGroupsAsync(
            context, season, league, rules, names, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<SeasonAthleteTotals> totals = SeasonCalculator.Accumulate(stageGroups, rules);
        IReadOnlyList<SeasonRankedAthlete> ranked = SeasonCalculator.Rank(totals, rng, rules);
        SeasonInvariants.ValidateFinalSeason(ranked, totals, rules);
        PersistLeagueStandings(context, season, league, ranked);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    internal static async Task<List<List<SeasonStageEntry>>> BuildStageGroupsAsync(
        SaveDbContext context,
        SeasonEntity season,
        LeagueEntity league,
        RulesV1 rules,
        Dictionary<int, string> names,
        CancellationToken cancellationToken)
    {
        List<StageStandingEntity> rows = await context.StageStandings
            .AsNoTracking()
            .Where(e => e.SeasonId == season.Id && e.LeagueId == league.Id)
            .OrderBy(e => e.StageNumber)
            .ThenBy(e => e.StageRank)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return GroupStageRows(rows, league, rules, names);
    }

    internal static List<List<SeasonStageEntry>> GroupStageRows(
        List<StageStandingEntity> rows,
        LeagueEntity league,
        RulesV1 rules,
        Dictionary<int, string> names)
    {
        if (rows.Count != rules.StagesPerSeason * rules.LeagueSize)
        {
            throw new InvalidOperationException(
                $"League '{league.Name}' must have {rules.StagesPerSeason * rules.LeagueSize} stage standings for finalization, was {rows.Count}.");
        }

        List<List<SeasonStageEntry>> groups = new(rules.StagesPerSeason);
        for (int stage = 1; stage <= rules.StagesPerSeason; stage++)
        {
            List<StageStandingEntity> stageRows = rows.Where(r => r.StageNumber == stage).ToList();
            groups.Add(BuildStageEntries(stageRows, league, stage, rules, names));
        }

        return groups;
    }

    internal static List<SeasonStageEntry> BuildStageEntries(
        List<StageStandingEntity> stageRows,
        LeagueEntity league,
        int stageNumber,
        RulesV1 rules,
        Dictionary<int, string> names)
    {
        if (stageRows.Count != rules.LeagueSize)
        {
            throw new InvalidOperationException(
                $"League '{league.Name}' stage {stageNumber} must have {rules.LeagueSize} standings, was {stageRows.Count}.");
        }

        List<SeasonStageEntry> entries = new(stageRows.Count);
        foreach (StageStandingEntity row in stageRows)
        {
            entries.Add(BuildSingleEntry(row, league, stageNumber, names));
        }

        return entries;
    }

    internal static SeasonStageEntry BuildSingleEntry(
        StageStandingEntity row,
        LeagueEntity league,
        int stageNumber,
        Dictionary<int, string> names)
    {
        if (row.StageNumber != stageNumber)
        {
            throw new InvalidOperationException($"League '{league.Name}' has corrupt stage identity.");
        }

        if (!names.TryGetValue(row.SaveAthleteId, out string? name))
        {
            throw new InvalidOperationException($"League '{league.Name}' references unknown athlete {row.SaveAthleteId}.");
        }

        List<int>? roundCounts = JsonSerializer.Deserialize<List<int>>(row.RoundPlaceCountsJson);
        if (roundCounts is null)
        {
            throw new InvalidOperationException($"League '{league.Name}' stage {stageNumber} has corrupt round counts.");
        }

        return new SeasonStageEntry(
            row.SaveAthleteId,
            name,
            row.StageRank,
            row.ChampionshipPointsThousandths,
            row.StageScoreThousandths,
            row.BaseScoreThousandths,
            roundCounts);
    }

    internal static void PersistLeagueStandings(
        SaveDbContext context,
        SeasonEntity season,
        LeagueEntity league,
        IReadOnlyList<SeasonRankedAthlete> ranked)
    {
        foreach (SeasonRankedAthlete entry in ranked)
        {
            context.SeasonStandings.Add(new SeasonStandingEntity
            {
                SeasonId = season.Id,
                LeagueId = league.Id,
                SaveAthleteId = entry.AthleteId,
                SeasonRank = entry.SeasonRank,
                TotalChampionshipPointsThousandths = entry.TotalChampionshipPointsThousandths,
                TotalStageScoreThousandths = entry.TotalStageScoreThousandths,
                TotalBaseScoreThousandths = entry.TotalBaseScoreThousandths,
                StageWins = entry.StageWins,
                RoundWins = entry.RoundWins,
                StagePlaceCountsJson = JsonSerializer.Serialize(entry.StagePlaceCounts),
                RoundPlaceCountsJson = JsonSerializer.Serialize(entry.RoundPlaceCounts),
                IsChampion = entry.IsChampion,
            });
        }
    }

    internal static async Task<Dictionary<int, string>> LoadAthleteNamesAsync(
        SaveDbContext context,
        CancellationToken cancellationToken)
    {
        return await context.SaveAthletes
            .AsNoTracking()
            .ToDictionaryAsync(e => e.Id, e => e.Name, cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task VerifyFinalizedAsync(
        SaveDbContext context,
        SeasonEntity season,
        List<LeagueEntity> leagues,
        RulesV1 rules,
        CancellationToken cancellationToken)
    {
        foreach (LeagueEntity league in leagues)
        {
            int count = await context.SeasonStandings.CountAsync(
                e => e.SeasonId == season.Id && e.LeagueId == league.Id,
                cancellationToken).ConfigureAwait(false);
            if (count != rules.LeagueSize)
            {
                throw new InvalidOperationException(
                    $"League '{league.Name}' must persist exactly {rules.LeagueSize} season standings, was {count}.");
            }

            int champions = await context.SeasonStandings.CountAsync(
                e => e.SeasonId == season.Id && e.LeagueId == league.Id && e.IsChampion,
                cancellationToken).ConfigureAwait(false);
            if (champions != 1)
            {
                throw new InvalidOperationException($"League '{league.Name}' must have exactly one champion, was {champions}.");
            }
        }

        if (!season.IsComplete)
        {
            throw new InvalidOperationException($"Season {season.SeasonNumber} must be marked complete after finalization.");
        }
    }
}
