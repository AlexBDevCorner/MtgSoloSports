using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Features.Simulation.GlobalStage;

/// <summary>
/// Shared state-machine primitive for synchronous global stage progression.
/// Season 1 has eight active feeder leagues; all leagues progress stage by
/// stage: Stage N+1 cannot begin until Stage N is complete for every active
/// league, though leagues within the current global stage may be simulated
/// one by one in any order. The global stage is the minimum incomplete stage
/// across active leagues (stages 1..32); when every league has completed
/// Stage 32 the season is complete. This helper only reads stage cursors;
/// handlers enforce the gate with their own conflict exceptions so the
/// primitive stays free of feature-specific exception types.
/// </summary>
public static class GlobalStageGate
{
    public sealed record LeagueStageStatus(
        int LeagueId,
        string LeagueName,
        string LeagueKind,
        int? CurrentStage,
        int CompletedStages,
        bool IsLeagueComplete);

    public sealed record GlobalStageView(
        int CurrentStage,
        bool IsSeasonComplete,
        IReadOnlyList<LeagueStageStatus> Leagues);

    /// <summary>
    /// Loads all active leagues for <paramref name="season"/> plus their stage
    /// cursors and computes the current global stage. Active leagues are all
    /// persisted leagues for the season, ordered by id for determinism.
    /// </summary>
    public static async Task<GlobalStageView> LoadGlobalStageAsync(
        SaveDbContext context,
        SeasonEntity season,
        RulesV1 rules,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(season);
        ArgumentNullException.ThrowIfNull(rules);

        List<LeagueEntity> leagues = await context.Leagues
            .AsNoTracking()
            .Where(e => e.SeasonId == season.Id)
            .OrderBy(e => e.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (leagues.Count == 0)
        {
            throw new InvalidOperationException($"Season {season.SeasonNumber} has no active leagues.");
        }

        List<StageEntity> stages = await context.Stages
            .AsNoTracking()
            .Where(e => e.SeasonId == season.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return ComputeGlobalStage(leagues, stages, rules);
    }

    /// <summary>
    /// Pure computation of the global stage from already-loaded leagues and
    /// stage rows. Validates stage cursor invariants and aborts on corruption;
    /// a league ahead of the global stage is not corruption, it is simply
    /// blocked from advancing until the global stage catches up.
    /// </summary>
    public static GlobalStageView ComputeGlobalStage(
        IReadOnlyList<LeagueEntity> leagues,
        IReadOnlyList<StageEntity> stages,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(leagues);
        ArgumentNullException.ThrowIfNull(stages);
        ArgumentNullException.ThrowIfNull(rules);

        if (leagues.Count == 0)
        {
            throw new InvalidOperationException("Season has no active leagues.");
        }

        List<LeagueStageStatus> statuses = new(leagues.Count);
        foreach (LeagueEntity league in leagues)
        {
            List<StageEntity> leagueStages = stages
                .Where(e => e.LeagueId == league.Id && e.SeasonId == league.SeasonId)
                .OrderBy(e => e.StageNumber)
                .ToList();
            statuses.Add(ComputeLeagueStatus(league, leagueStages, rules));
        }

        bool allComplete = statuses.All(s => s.IsLeagueComplete);
        if (allComplete)
        {
            return new GlobalStageView(rules.StagesPerSeason + 1, true, statuses);
        }

        int global = statuses.Where(s => !s.IsLeagueComplete).Min(s => s.CurrentStage!.Value);
        return new GlobalStageView(global, false, statuses);
    }

    internal static LeagueStageStatus ComputeLeagueStatus(
        LeagueEntity league,
        List<StageEntity> leagueStages,
        RulesV1 rules)
    {
        ValidateStageRows(league, leagueStages, rules);
        if (leagueStages.Count == 0)
        {
            return new LeagueStageStatus(league.Id, league.Name, DescribeKind(league), 1, 0, false);
        }

        List<StageEntity> ordered = CheckContiguous(league, leagueStages);
        return ResolveLeagueStatus(league, ordered, rules);
    }

    private static void ValidateStageRows(LeagueEntity league, List<StageEntity> leagueStages, RulesV1 rules)
    {
        foreach (StageEntity stage in leagueStages)
        {
            if (stage.StageNumber < 1 || stage.StageNumber > rules.StagesPerSeason)
            {
                throw new InvalidOperationException($"League '{league.Name}' has corrupt stage {stage.StageNumber}.");
            }

            if (stage.CompletedRounds < 0 || stage.CompletedRounds > rules.RoundsPerStage)
            {
                throw new InvalidOperationException(
                    $"League '{league.Name}' stage {stage.StageNumber} has corrupt completed-round count {stage.CompletedRounds}.");
            }

            if (stage.IsComplete && stage.CompletedRounds != rules.RoundsPerStage)
            {
                throw new InvalidOperationException(
                    $"League '{league.Name}' stage {stage.StageNumber} is marked complete with {stage.CompletedRounds} rounds.");
            }
        }

        int distinct = leagueStages.Select(s => s.StageNumber).Distinct().Count();
        if (distinct != leagueStages.Count)
        {
            throw new InvalidOperationException($"League '{league.Name}' has duplicate stage rows.");
        }
    }

    private static List<StageEntity> CheckContiguous(LeagueEntity league, List<StageEntity> leagueStages)
    {
        List<StageEntity> ordered = leagueStages.OrderBy(s => s.StageNumber).ToList();
        for (int i = 0; i < ordered.Count; i++)
        {
            if (ordered[i].StageNumber != i + 1)
            {
                throw new InvalidOperationException(
                    $"League '{league.Name}' has non-contiguous stage rows; stages must advance in order.");
            }
        }

        return ordered;
    }

    private static LeagueStageStatus ResolveLeagueStatus(LeagueEntity league, List<StageEntity> ordered, RulesV1 rules)
    {
        StageEntity max = ordered[^1];
        if (max.IsComplete)
        {
            if (max.StageNumber == rules.StagesPerSeason)
            {
                return new LeagueStageStatus(league.Id, league.Name, DescribeKind(league), null, rules.StagesPerSeason, true);
            }

            throw new InvalidOperationException(
                $"League '{league.Name}' stage {max.StageNumber} is complete but stage {max.StageNumber + 1} is missing.");
        }

        int completed = ordered.Count(s => s.IsComplete);
        if (completed != max.StageNumber - 1)
        {
            throw new InvalidOperationException(
                $"League '{league.Name}' has non-contiguous completion; all stages before {max.StageNumber} must be complete.");
        }

        return new LeagueStageStatus(league.Id, league.Name, DescribeKind(league), max.StageNumber, completed, false);
    }

    internal static string DescribeKind(LeagueEntity league)
    {
        ArgumentNullException.ThrowIfNull(league);
        return ((LeagueKind)league.Kind).ToString();
    }

    /// <summary>
    /// Returns true when <paramref name="targetStage"/> is the current global
    /// stage and the season is not complete. Callers throw their own
    /// feature-specific conflict exception with a message naming the global stage.
    /// </summary>
    public static bool IsStageLegal(int targetStage, GlobalStageView global)
    {
        ArgumentNullException.ThrowIfNull(global);
        if (global.IsSeasonComplete)
        {
            return false;
        }

        return targetStage == global.CurrentStage;
    }

    /// <summary>
    /// Builds the standard gate message naming the blocked league, its target
    /// stage and the current global stage.
    /// </summary>
    public static string BuildBlockedMessage(string leagueName, int targetStage, GlobalStageView global)
    {
        if (global.IsSeasonComplete)
        {
            return $"League '{leagueName}' has completed all {targetStage} stages; season completion is handled by a later slice.";
        }

        return $"Stage {targetStage} for league '{leagueName}' cannot begin until stage {global.CurrentStage} is complete for every active league. Current global stage is {global.CurrentStage}.";
    }
}
