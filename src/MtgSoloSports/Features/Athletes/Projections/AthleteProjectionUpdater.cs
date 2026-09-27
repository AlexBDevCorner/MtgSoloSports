using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Rules;
using MtgSoloSports.SimulationKernel.Scoring;

namespace MtgSoloSports.Features.Athletes.Projections;

/// <summary>
/// Transactional maintenance of athlete career and season summary projections.
/// Every method runs inside the caller's SQLite transaction (stage completion,
/// season finalization, save creation) so RNG state, sporting results and
/// projections commit atomically. Reads only normalized standings/memberships;
/// never decompresses round payloads.
/// Rebuild path recomputes from the same authoritative tables, so stored
/// projections stay auditable and UI counters never become the source of truth.
/// </summary>
public static class AthleteProjectionUpdater
{
    /// <summary>
    /// Refreshes projections for one league's 32 athletes after its stage
    /// completes. Call after the stage standings <c>SaveChanges</c> so queries
    /// observe them, still inside the caller's transaction.
    /// </summary>
    public static async Task RefreshAfterStageAsync(
        SaveDbContext context,
        SeasonEntity season,
        LeagueEntity league,
        RulesV1 rules,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(season);
        ArgumentNullException.ThrowIfNull(league);
        ArgumentNullException.ThrowIfNull(rules);

        List<int> athleteIds = await LoadLeagueAthleteIdsAsync(context, season, league, rules, cancellationToken).ConfigureAwait(false);
        await RebuildBatchAsync(context, athleteIds, rules, cancellationToken).ConfigureAwait(false);
    }

    internal const int RebuildBatchSize = 64;

    /// <summary>
    /// Rebuilds one batch of athletes from bulk-loaded history. Reduces the
    /// per-stage query storm (seasons/leagues/memberships/standings loaded once
    /// per batch instead of once per athlete) while computing identical rows
    /// with the same pure <c>BuildSeasonSummary</c>/<c>BuildCareer</c> functions.
    /// </summary>
    internal static async Task RebuildBatchAsync(
        SaveDbContext context,
        IReadOnlyList<int> athleteIds,
        RulesV1 rules,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(athleteIds);
        ArgumentNullException.ThrowIfNull(rules);
        if (athleteIds.Count == 0)
        {
            return;
        }

        List<SeasonEntity> seasons = await context.Seasons
            .OrderBy(e => e.SeasonNumber)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (seasons.Count == 0)
        {
            throw new InvalidOperationException("Save has no seasons for projection rebuild.");
        }

        Dictionary<int, LeagueEntity> leaguesById = await context.Leagues
            .ToDictionaryAsync(e => e.Id, cancellationToken)
            .ConfigureAwait(false);
        Dictionary<int, int> seasonNumbers = seasons.ToDictionary(e => e.Id, e => e.SeasonNumber);

        HashSet<int> idSet = athleteIds.ToHashSet();
        List<SeasonMembershipEntity> memberships = await context.SeasonMemberships
            .Where(e => idSet.Contains(e.SaveAthleteId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<StageStandingEntity> stageRows = await context.StageStandings
            .Where(e => idSet.Contains(e.SaveAthleteId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<SeasonStandingEntity> seasonRows = await context.SeasonStandings
            .Where(e => idSet.Contains(e.SaveAthleteId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        Dictionary<(int SeasonId, int AthleteId), AthleteSeasonSummaryEntity> existingSummaries =
            await LoadSummariesForBatchAsync(context, idSet, cancellationToken).ConfigureAwait(false);
        Dictionary<int, AthleteCareerEntity> existingCareers =
            await LoadCareersForBatchAsync(context, idSet, cancellationToken).ConfigureAwait(false);

        foreach (int athleteId in athleteIds)
        {
            AthleteHistory history = SliceHistory(
                athleteId, seasons, seasonNumbers, leaguesById, memberships, stageRows, seasonRows);
            ApplyHistoryToTracked(
                context, athleteId, history, rules, existingSummaries, existingCareers);
            cancellationToken.ThrowIfCancellationRequested();
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static AthleteHistory SliceHistory(
        int saveAthleteId,
        List<SeasonEntity> seasons,
        Dictionary<int, int> seasonNumbers,
        Dictionary<int, LeagueEntity> leaguesById,
        List<SeasonMembershipEntity> memberships,
        List<StageStandingEntity> stageRows,
        List<SeasonStandingEntity> seasonRows)
    {
        List<SeasonMembershipEntity> mine = memberships.Where(e => e.SaveAthleteId == saveAthleteId).ToList();
        List<StageStandingEntity> myStages = stageRows.Where(e => e.SaveAthleteId == saveAthleteId).ToList();
        List<SeasonStandingEntity> mySeasons = seasonRows.Where(e => e.SaveAthleteId == saveAthleteId).ToList();
        return new AthleteHistory(seasons, seasonNumbers, leaguesById, mine, myStages, mySeasons);
    }

    internal static void ApplyHistoryToTracked(
        SaveDbContext context,
        int saveAthleteId,
        AthleteHistory history,
        RulesV1 rules,
        Dictionary<(int SeasonId, int AthleteId), AthleteSeasonSummaryEntity> existingSummaries,
        Dictionary<int, AthleteCareerEntity> existingCareers)
    {
        Dictionary<int, SeasonMembershipEntity> membershipBySeason = history.Memberships.ToDictionary(e => e.SeasonId);
        Dictionary<int, SeasonStandingEntity> seasonStandingBySeason = history.SeasonRows.ToDictionary(e => e.SeasonId);
        foreach (SeasonEntity season in history.Seasons)
        {
            membershipBySeason.TryGetValue(season.Id, out SeasonMembershipEntity? membership);
            seasonStandingBySeason.TryGetValue(season.Id, out SeasonStandingEntity? final);
            List<StageStandingEntity> seasonStages = history.StageRows.Where(r => r.SeasonId == season.Id).ToList();
            AthleteSeasonSummaryEntity summary = BuildSeasonSummary(
                season, membership, final, seasonStages, history.LeaguesById);
            UpsertTrackedSummary(context, summary, existingSummaries);
        }

        AthleteCareerEntity career = BuildCareer(saveAthleteId, history, rules);
        UpsertTrackedCareer(context, career, existingCareers);
    }

    internal static void UpsertTrackedSummary(
        SaveDbContext context,
        AthleteSeasonSummaryEntity summary,
        Dictionary<(int SeasonId, int AthleteId), AthleteSeasonSummaryEntity> existing)
    {
        if (existing.TryGetValue((summary.SeasonId, summary.SaveAthleteId), out AthleteSeasonSummaryEntity? row))
        {
            CopySummary(summary, row);
            return;
        }

        context.AthleteSeasonSummaries.Add(summary);
        existing[(summary.SeasonId, summary.SaveAthleteId)] = summary;
    }

    internal static void UpsertTrackedCareer(
        SaveDbContext context,
        AthleteCareerEntity career,
        Dictionary<int, AthleteCareerEntity> existing)
    {
        if (existing.TryGetValue(career.SaveAthleteId, out AthleteCareerEntity? row))
        {
            CopyCareer(career, row);
            return;
        }

        context.AthleteCareers.Add(career);
        existing[career.SaveAthleteId] = career;
    }

    internal static void CopySummary(AthleteSeasonSummaryEntity source, AthleteSeasonSummaryEntity target)
    {
        target.SeasonNumber = source.SeasonNumber;
        target.LeagueId = source.LeagueId;
        target.LeagueName = source.LeagueName;
        target.LeagueKind = source.LeagueKind;
        target.WasActive = source.WasActive;
        target.RoundWins = source.RoundWins;
        target.StageWins = source.StageWins;
        target.StageSeconds = source.StageSeconds;
        target.StageThirds = source.StageThirds;
        target.SeasonRank = source.SeasonRank;
        target.IsChampion = source.IsChampion;
        target.EarnedBonusThousandths = source.EarnedBonusThousandths;
        target.TotalChampionshipPointsThousandths = source.TotalChampionshipPointsThousandths;
        target.TotalStageScoreThousandths = source.TotalStageScoreThousandths;
        target.TotalBaseScoreThousandths = source.TotalBaseScoreThousandths;
    }

    internal static void CopyCareer(AthleteCareerEntity source, AthleteCareerEntity target)
    {
        target.SeasonsActive = source.SeasonsActive;
        target.CurrentLeagueId = source.CurrentLeagueId;
        target.CurrentLeagueName = source.CurrentLeagueName;
        target.CurrentLeagueKind = source.CurrentLeagueKind;
        target.IsActive = source.IsActive;
        target.RoundWins = source.RoundWins;
        target.StageWins = source.StageWins;
        target.StageSeconds = source.StageSeconds;
        target.StageThirds = source.StageThirds;
        target.BestSeasonFinish = source.BestSeasonFinish;
        target.BestSeasonNumber = source.BestSeasonNumber;
        target.LifetimeEarnedBonusThousandths = source.LifetimeEarnedBonusThousandths;
        target.CurrentEffectiveBonusThousandths = source.CurrentEffectiveBonusThousandths;
        target.LastSeasonNumber = source.LastSeasonNumber;
        target.LastStageNumber = source.LastStageNumber;
    }

    internal static async Task<Dictionary<(int SeasonId, int AthleteId), AthleteSeasonSummaryEntity>> LoadSummariesForBatchAsync(
        SaveDbContext context,
        HashSet<int> athleteIds,
        CancellationToken cancellationToken)
    {
        List<AthleteSeasonSummaryEntity> rows = await context.AthleteSeasonSummaries
            .Where(e => athleteIds.Contains(e.SaveAthleteId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.ToDictionary(e => (e.SeasonId, e.SaveAthleteId));
    }

    internal static async Task<Dictionary<int, AthleteCareerEntity>> LoadCareersForBatchAsync(
        SaveDbContext context,
        HashSet<int> athleteIds,
        CancellationToken cancellationToken)
    {
        List<AthleteCareerEntity> rows = await context.AthleteCareers
            .Where(e => athleteIds.Contains(e.SaveAthleteId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.ToDictionary(e => e.SaveAthleteId);
    }

    /// <summary>
    /// Refreshes every athlete's projections after season finalization commits
    /// its <c>SeasonStanding</c> rows. Ensures explicit inactive summaries for
    /// common-pool athletes and final ranks/champion flags for active athletes.
    /// </summary>
    public static async Task RefreshAfterSeasonAsync(
        SaveDbContext context,
        RulesV1 rules,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(rules);
        await RebuildAllAsync(context, rules, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Seeds initial projections at save creation: one career row plus one
    /// Season 1 summary per athlete, all sporting totals zero, activity derived
    /// from Season 1 memberships. Runs inside the creation transaction.
    /// </summary>
    public static async Task SeedForNewSaveAsync(
        SaveDbContext context,
        SeasonEntity season,
        RulesV1 rules,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(season);
        ArgumentNullException.ThrowIfNull(rules);
        await RebuildAllAsync(context, rules, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Recomputes one athlete's career row plus all of its season summaries from
    /// authoritative history. Idempotent: missing rows are inserted, existing
    /// rows are overwritten with recomputed values.
    /// </summary>
    public static async Task RebuildAthleteAsync(
        SaveDbContext context,
        int saveAthleteId,
        RulesV1 rules,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(rules);
        if (saveAthleteId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(saveAthleteId));
        }

        AthleteHistory history = await LoadHistoryAsync(context, saveAthleteId, cancellationToken).ConfigureAwait(false);
        await ApplyHistoryAsync(context, saveAthleteId, history, rules, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Recomputes every athlete's projections. Used at season finalization,
    /// save creation and explicit audits. Never touches round payloads.
    /// </summary>
    public static async Task RebuildAllAsync(
        SaveDbContext context,
        RulesV1 rules,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(rules);
        List<int> athleteIds = await context.SaveAthletes
            .OrderBy(e => e.Id)
            .Select(e => e.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        for (int offset = 0; offset < athleteIds.Count; offset += RebuildBatchSize)
        {
            int count = Math.Min(RebuildBatchSize, athleteIds.Count - offset);
            List<int> batch = athleteIds.GetRange(offset, count);
            await RebuildBatchAsync(context, batch, rules, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    internal sealed record AthleteHistory(
        List<SeasonEntity> Seasons,
        Dictionary<int, int> SeasonNumbers,
        Dictionary<int, LeagueEntity> LeaguesById,
        List<SeasonMembershipEntity> Memberships,
        List<StageStandingEntity> StageRows,
        List<SeasonStandingEntity> SeasonRows);

    internal static async Task<List<int>> LoadLeagueAthleteIdsAsync(
        SaveDbContext context,
        SeasonEntity season,
        LeagueEntity league,
        RulesV1 rules,
        CancellationToken cancellationToken)
    {
        List<int> athleteIds = await context.SeasonMemberships
            .Where(e => e.SeasonId == season.Id && e.LeagueId == league.Id)
            .Select(e => e.SaveAthleteId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (athleteIds.Count != rules.LeagueSize)
        {
            throw new InvalidOperationException(
                $"League '{league.Name}' must contain exactly {rules.LeagueSize} athletes for projection refresh, was {athleteIds.Count}.");
        }

        return athleteIds;
    }

    internal static async Task<AthleteHistory> LoadHistoryAsync(
        SaveDbContext context,
        int saveAthleteId,
        CancellationToken cancellationToken)
    {
        bool exists = await context.SaveAthletes.AnyAsync(e => e.Id == saveAthleteId, cancellationToken).ConfigureAwait(false);
        if (!exists)
        {
            throw new InvalidOperationException($"Athlete {saveAthleteId} does not exist.");
        }

        List<SeasonEntity> seasons = await context.Seasons
            .OrderBy(e => e.SeasonNumber)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (seasons.Count == 0)
        {
            throw new InvalidOperationException("Save has no seasons for projection rebuild.");
        }

        Dictionary<int, int> seasonNumbers = seasons.ToDictionary(e => e.Id, e => e.SeasonNumber);
        Dictionary<int, LeagueEntity> leaguesById = await context.Leagues
            .ToDictionaryAsync(e => e.Id, cancellationToken)
            .ConfigureAwait(false);
        List<SeasonMembershipEntity> memberships = await context.SeasonMemberships
            .Where(e => e.SaveAthleteId == saveAthleteId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<StageStandingEntity> stageRows = await context.StageStandings
            .Where(e => e.SaveAthleteId == saveAthleteId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<SeasonStandingEntity> seasonRows = await context.SeasonStandings
            .Where(e => e.SaveAthleteId == saveAthleteId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return new AthleteHistory(seasons, seasonNumbers, leaguesById, memberships, stageRows, seasonRows);
    }

    internal static async Task ApplyHistoryAsync(
        SaveDbContext context,
        int saveAthleteId,
        AthleteHistory history,
        RulesV1 rules,
        CancellationToken cancellationToken)
    {
        Dictionary<int, SeasonMembershipEntity> membershipBySeason = history.Memberships.ToDictionary(e => e.SeasonId);
        Dictionary<int, SeasonStandingEntity> seasonStandingBySeason = history.SeasonRows.ToDictionary(e => e.SeasonId);
        foreach (SeasonEntity season in history.Seasons)
        {
            membershipBySeason.TryGetValue(season.Id, out SeasonMembershipEntity? membership);
            seasonStandingBySeason.TryGetValue(season.Id, out SeasonStandingEntity? final);
            List<StageStandingEntity> seasonStages = history.StageRows.Where(r => r.SeasonId == season.Id).ToList();
            AthleteSeasonSummaryEntity summary = BuildSeasonSummary(
                season, membership, final, seasonStages, history.LeaguesById);
            await UpsertSeasonSummaryAsync(context, summary, cancellationToken).ConfigureAwait(false);
        }

        AthleteCareerEntity career = BuildCareer(saveAthleteId, history, rules);
        await UpsertCareerAsync(context, career, cancellationToken).ConfigureAwait(false);
    }

    internal static AthleteSeasonSummaryEntity BuildSeasonSummary(
        SeasonEntity season,
        SeasonMembershipEntity? membership,
        SeasonStandingEntity? final,
        List<StageStandingEntity> seasonStages,
        Dictionary<int, LeagueEntity> leaguesById)
    {
        ArgumentNullException.ThrowIfNull(season);
        ArgumentNullException.ThrowIfNull(seasonStages);
        ArgumentNullException.ThrowIfNull(leaguesById);

        (int? leagueId, string? leagueName, int? leagueKind) = ResolveLeague(membership, season, leaguesById);
        bool wasActive = leagueId is not null;
        ValidatePoolInvariants(season, membership, final, seasonStages, wasActive);
        AthleteProjectionCalculator.SeasonAggregate aggregate =
            AthleteProjectionCalculator.AggregateSeasonStages(seasonStages);
        int saveAthleteId = ResolveSummaryAthlete(membership, final, seasonStages, season);
        return MapSeasonSummary(season, saveAthleteId, leagueId, leagueName, leagueKind, wasActive, final, aggregate);
    }

    internal static (int? LeagueId, string? LeagueName, int? LeagueKind) ResolveLeague(
        SeasonMembershipEntity? membership,
        SeasonEntity season,
        Dictionary<int, LeagueEntity> leaguesById)
    {
        if (membership?.LeagueId is null)
        {
            return (null, null, null);
        }

        if (!leaguesById.TryGetValue(membership.LeagueId.Value, out LeagueEntity? league))
        {
            throw new InvalidOperationException($"Season {season.SeasonNumber} references unknown league {membership.LeagueId.Value}.");
        }

        return (membership.LeagueId, league.Name, league.Kind);
    }

    internal static void ValidatePoolInvariants(
        SeasonEntity season,
        SeasonMembershipEntity? membership,
        SeasonStandingEntity? final,
        List<StageStandingEntity> seasonStages,
        bool wasActive)
    {
        if (wasActive)
        {
            return;
        }

        if (seasonStages.Count != 0)
        {
            throw new InvalidOperationException(
                $"Season {season.SeasonNumber} pool athlete has {seasonStages.Count} stage standings.");
        }

        if (final is not null)
        {
            throw new InvalidOperationException(
                $"Season {season.SeasonNumber} pool athlete has a final season standing.");
        }
    }

    internal static int ResolveSummaryAthlete(
        SeasonMembershipEntity? membership,
        SeasonStandingEntity? final,
        List<StageStandingEntity> seasonStages,
        SeasonEntity season)
    {
        return membership?.SaveAthleteId
            ?? final?.SaveAthleteId
            ?? seasonStages.FirstOrDefault()?.SaveAthleteId
            ?? throw new InvalidOperationException($"Season {season.SeasonNumber} summary has no athlete identity.");
    }

    internal static AthleteSeasonSummaryEntity MapSeasonSummary(
        SeasonEntity season,
        int saveAthleteId,
        int? leagueId,
        string? leagueName,
        int? leagueKind,
        bool wasActive,
        SeasonStandingEntity? final,
        AthleteProjectionCalculator.SeasonAggregate aggregate)
    {
        return new AthleteSeasonSummaryEntity
        {
            SeasonId = season.Id,
            SeasonNumber = season.SeasonNumber,
            SaveAthleteId = saveAthleteId,
            LeagueId = leagueId,
            LeagueName = leagueName,
            LeagueKind = leagueKind,
            WasActive = wasActive,
            RoundWins = aggregate.RoundWins,
            StageWins = aggregate.StageWins,
            StageSeconds = aggregate.StageSeconds,
            StageThirds = aggregate.StageThirds,
            SeasonRank = final?.SeasonRank,
            IsChampion = final?.IsChampion ?? false,
            EarnedBonusThousandths = aggregate.EarnedBonusThousandths,
            TotalChampionshipPointsThousandths = final?.TotalChampionshipPointsThousandths ?? aggregate.TotalChampionshipPointsThousandths,
            TotalStageScoreThousandths = final?.TotalStageScoreThousandths ?? aggregate.TotalStageScoreThousandths,
            TotalBaseScoreThousandths = final?.TotalBaseScoreThousandths ?? aggregate.TotalBaseScoreThousandths,
        };
    }

    internal static AthleteCareerEntity BuildCareer(
        int saveAthleteId,
        AthleteHistory history,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(rules);
        AthleteProjectionCalculator.CareerAggregate totals =
            AthleteProjectionCalculator.AggregateCareer(history.StageRows);
        IReadOnlyList<BonusContribution> contributions =
            AthleteProjectionCalculator.BuildContributions(history.StageRows, history.SeasonNumbers);
        SeasonEntity latest = history.Seasons.OrderByDescending(s => s.SeasonNumber).First();
        (bool isActive, int? leagueId, string? leagueName, int? leagueKind) = ResolveCurrentLeague(history, latest);
        (int? bestFinish, int? bestSeason) = FindBestFinish(history);
        (int effectiveSeason, int effectiveStage, int lastStage) = ResolveEffectiveBoundary(history, latest, rules, isActive);
        int currentEffective = AthleteProjectionCalculator.ComputeCurrentEffectiveThousandths(
            contributions, effectiveSeason, effectiveStage, rules);
        return MapCareer(saveAthleteId, history, totals, contributions, latest, isActive, leagueId, leagueName, leagueKind, bestFinish, bestSeason, currentEffective, lastStage);
    }

    internal static (bool IsActive, int? LeagueId, string? LeagueName, int? LeagueKind) ResolveCurrentLeague(
        AthleteHistory history,
        SeasonEntity latest)
    {
        SeasonMembershipEntity? latestMembership = history.Memberships.SingleOrDefault(m => m.SeasonId == latest.Id);
        bool isActive = latestMembership?.LeagueId is not null;
        int? leagueId = latestMembership?.LeagueId;
        if (leagueId is null)
        {
            return (false, null, null, null);
        }

        if (!history.LeaguesById.TryGetValue(leagueId.Value, out LeagueEntity? league))
        {
            throw new InvalidOperationException($"Latest season references unknown league {leagueId.Value}.");
        }

        return (isActive, leagueId, league.Name, league.Kind);
    }

    internal static (int? BestFinish, int? BestSeason) FindBestFinish(AthleteHistory history)
    {
        int? bestFinish = null;
        int? bestSeason = null;
        foreach (SeasonStandingEntity row in history.SeasonRows)
        {
            if (!history.SeasonNumbers.TryGetValue(row.SeasonId, out int seasonNumber))
            {
                throw new InvalidOperationException($"Season standing {row.Id} references unknown season {row.SeasonId}.");
            }

            if (bestFinish is null || row.SeasonRank < bestFinish)
            {
                bestFinish = row.SeasonRank;
                bestSeason = seasonNumber;
            }
        }

        return (bestFinish, bestSeason);
    }

    internal static (int EffectiveSeason, int EffectiveStage, int LastStage) ResolveEffectiveBoundary(
        AthleteHistory history,
        SeasonEntity latest,
        RulesV1 rules,
        bool isActive)
    {
        int completed = 0;
        if (isActive)
        {
            completed = CountCompletedStages(history, latest);
        }

        (int effectiveSeason, int effectiveStage) = AthleteProjectionCalculator.ResolveNextBoundary(
            latest.SeasonNumber, latest.IsComplete, isActive, completed, rules);
        return (effectiveSeason, effectiveStage, isActive ? completed : 0);
    }

    internal static int CountCompletedStages(AthleteHistory history, SeasonEntity latest)
    {
        int completed = history.StageRows.Count(r => r.SeasonId == latest.Id);
        int distinct = history.StageRows.Where(r => r.SeasonId == latest.Id).Select(r => r.StageNumber).Distinct().Count();
        if (distinct != completed)
        {
            throw new InvalidOperationException($"Athlete has duplicate stage rows in season {latest.SeasonNumber}.");
        }

        return completed;
    }

    internal static AthleteCareerEntity MapCareer(
        int saveAthleteId,
        AthleteHistory history,
        AthleteProjectionCalculator.CareerAggregate totals,
        IReadOnlyList<BonusContribution> contributions,
        SeasonEntity latest,
        bool isActive,
        int? leagueId,
        string? leagueName,
        int? leagueKind,
        int? bestFinish,
        int? bestSeason,
        int currentEffective,
        int lastStage)
    {
        int seasonsActive = history.Memberships.Count(m => m.LeagueId is not null);
        return new AthleteCareerEntity
        {
            SaveAthleteId = saveAthleteId,
            SeasonsActive = seasonsActive,
            CurrentLeagueId = leagueId,
            CurrentLeagueName = leagueName,
            CurrentLeagueKind = leagueKind,
            IsActive = isActive,
            RoundWins = totals.RoundWins,
            StageWins = totals.StageWins,
            StageSeconds = totals.StageSeconds,
            StageThirds = totals.StageThirds,
            BestSeasonFinish = bestFinish,
            BestSeasonNumber = bestSeason,
            LifetimeEarnedBonusThousandths = totals.LifetimeEarnedBonusThousandths,
            CurrentEffectiveBonusThousandths = currentEffective,
            LastSeasonNumber = latest.SeasonNumber,
            LastStageNumber = lastStage,
        };
    }

    private static async Task UpsertSeasonSummaryAsync(
        SaveDbContext context,
        AthleteSeasonSummaryEntity summary,
        CancellationToken cancellationToken)
    {
        AthleteSeasonSummaryEntity? existing = await context.AthleteSeasonSummaries
            .SingleOrDefaultAsync(
                e => e.SeasonId == summary.SeasonId && e.SaveAthleteId == summary.SaveAthleteId,
                cancellationToken)
            .ConfigureAwait(false);
        if (existing is null)
        {
            context.AthleteSeasonSummaries.Add(summary);
            return;
        }

        existing.SeasonNumber = summary.SeasonNumber;
        existing.LeagueId = summary.LeagueId;
        existing.LeagueName = summary.LeagueName;
        existing.LeagueKind = summary.LeagueKind;
        existing.WasActive = summary.WasActive;
        existing.RoundWins = summary.RoundWins;
        existing.StageWins = summary.StageWins;
        existing.StageSeconds = summary.StageSeconds;
        existing.StageThirds = summary.StageThirds;
        existing.SeasonRank = summary.SeasonRank;
        existing.IsChampion = summary.IsChampion;
        existing.EarnedBonusThousandths = summary.EarnedBonusThousandths;
        existing.TotalChampionshipPointsThousandths = summary.TotalChampionshipPointsThousandths;
        existing.TotalStageScoreThousandths = summary.TotalStageScoreThousandths;
        existing.TotalBaseScoreThousandths = summary.TotalBaseScoreThousandths;
    }

    private static async Task UpsertCareerAsync(
        SaveDbContext context,
        AthleteCareerEntity career,
        CancellationToken cancellationToken)
    {
        AthleteCareerEntity? existing = await context.AthleteCareers
            .SingleOrDefaultAsync(e => e.SaveAthleteId == career.SaveAthleteId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is null)
        {
            context.AthleteCareers.Add(career);
            return;
        }

        existing.SeasonsActive = career.SeasonsActive;
        existing.CurrentLeagueId = career.CurrentLeagueId;
        existing.CurrentLeagueName = career.CurrentLeagueName;
        existing.CurrentLeagueKind = career.CurrentLeagueKind;
        existing.IsActive = career.IsActive;
        existing.RoundWins = career.RoundWins;
        existing.StageWins = career.StageWins;
        existing.StageSeconds = career.StageSeconds;
        existing.StageThirds = career.StageThirds;
        existing.BestSeasonFinish = career.BestSeasonFinish;
        existing.BestSeasonNumber = career.BestSeasonNumber;
        existing.LifetimeEarnedBonusThousandths = career.LifetimeEarnedBonusThousandths;
        existing.CurrentEffectiveBonusThousandths = career.CurrentEffectiveBonusThousandths;
        existing.LastSeasonNumber = career.LastSeasonNumber;
        existing.LastStageNumber = career.LastStageNumber;
    }
}
