using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Cups.AdvanceChaosCupStage;
using MtgSoloSports.Features.Cups.RunChaosCupGroup;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Cups;

namespace MtgSoloSports.Features.Cups.CompleteChaosCup;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Fast-forwards one Chaos Cup
/// stage or the entire tournament (MSS-103) by reusing the canonical one-group
/// complete action and the persisted stage-draw action. Each group commits in
/// its own transaction and each draw commits once with the save RNG, so there
/// is no unbounded tournament transaction: cancellation or failure after an
/// intermediate commit leaves resumable persisted progress and a retry consumes
/// no duplicate rounds, bonus or RNG. Per-group isolated RNG plus canonically
/// ordered advancers keep incremental, complete-group, complete-stage and
/// complete-cup paths byte-identical from equivalent saved state. Holds one
/// per-save lock for the whole bulk run; reads never lock.
/// </summary>
public sealed class CompleteChaosCupHandler
{
    private readonly SaveStore _store;

    public CompleteChaosCupHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<CompleteChaosCupStageResponse> CompleteStageAsync(
        Guid saveId,
        int? stageIndex = null,
        int? sourceSeasonNumber = null,
        CancellationToken cancellationToken = default)
    {
        ValidateStageRequest(saveId, stageIndex, sourceSeasonNumber);
        SemaphoreSlim gate = AdvanceRoundSaveLock.Acquire(saveId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await CompleteStageUnderLockAsync(saveId, stageIndex, sourceSeasonNumber, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<CompleteChaosCupResponse> CompleteCupAsync(
        Guid saveId,
        int? sourceSeasonNumber = null,
        CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        if (sourceSeasonNumber is < 1)
        {
            throw new ArgumentException("Season number must be positive.", nameof(sourceSeasonNumber));
        }

        SemaphoreSlim gate = AdvanceRoundSaveLock.Acquire(saveId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await CompleteCupUnderLockAsync(saveId, sourceSeasonNumber, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task<CompleteChaosCupStageResponse> CompleteStageUnderLockAsync(
        Guid saveId,
        int? stageIndex,
        int? sourceSeasonNumber,
        CancellationToken cancellationToken)
    {
        await _store.EnsureMigratedAsync(saveId, cancellationToken).ConfigureAwait(false);
        int target = await ResolveStageAsync(saveId, stageIndex, sourceSeasonNumber, cancellationToken).ConfigureAwait(false);
        int explicitSeason = await ResolveSeasonNumberAsync(saveId, sourceSeasonNumber, cancellationToken).ConfigureAwait(false);
        int roundsBefore = await CountStageRoundsAsync(saveId, explicitSeason, target, cancellationToken).ConfigureAwait(false);

        RunChaosCupGroupHandler groups = new(_store);
        int groupCount = ChaosCupFormat.GroupCountForStage(target);
        for (int group = 1; group <= groupCount; group++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await groups.FinishUnderLockAsync(saveId, target, group, explicitSeason, cancellationToken).ConfigureAwait(false);
        }

        return await BuildStageResponseAsync(saveId, explicitSeason, target, roundsBefore, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<CompleteChaosCupResponse> CompleteCupUnderLockAsync(
        Guid saveId,
        int? sourceSeasonNumber,
        CancellationToken cancellationToken)
    {
        await _store.EnsureMigratedAsync(saveId, cancellationToken).ConfigureAwait(false);
        int explicitSeason = await ResolveSeasonNumberAsync(saveId, sourceSeasonNumber, cancellationToken).ConfigureAwait(false);
        int roundsBefore = await CountTournamentRoundsAsync(saveId, explicitSeason, cancellationToken).ConfigureAwait(false);

        AdvanceChaosCupStageHandler draws = new(_store);
        for (int stage = 0; stage < ChaosCupFormat.StageCount; stage++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool hasDraws = await HasStageDrawsAsync(saveId, explicitSeason, stage, cancellationToken).ConfigureAwait(false);
            if (!hasDraws)
            {
                if (stage == 0)
                {
                    throw new CompleteChaosCupConflictException(
                        $"Chaos Cup stage 0 has no persisted draw yet for Season {explicitSeason}.");
                }

                await draws.AdvanceUnderLockAsync(saveId, stage, explicitSeason, cancellationToken).ConfigureAwait(false);
            }

            await CompleteStageUnderLockAsync(saveId, stage, explicitSeason, cancellationToken).ConfigureAwait(false);
        }

        return await BuildCupResponseAsync(saveId, explicitSeason, roundsBefore, cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateStageRequest(Guid saveId, int? stageIndex, int? sourceSeasonNumber)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        if (stageIndex is < 0 or >= 6)
        {
            throw new ArgumentException($"Chaos Cup stage {stageIndex} is out of range (0..5).", nameof(stageIndex));
        }

        if (sourceSeasonNumber is < 1)
        {
            throw new ArgumentException("Season number must be positive.", nameof(sourceSeasonNumber));
        }
    }

    private async Task<int> ResolveStageAsync(
        Guid saveId,
        int? stageIndex,
        int? sourceSeasonNumber,
        CancellationToken cancellationToken)
    {
        if (stageIndex.HasValue)
        {
            await EnsureStageDrawableAsync(saveId, stageIndex.Value, sourceSeasonNumber, cancellationToken).ConfigureAwait(false);
            return stageIndex.Value;
        }

        int explicitSeason = await ResolveSeasonNumberAsync(saveId, sourceSeasonNumber, cancellationToken).ConfigureAwait(false);
        using SaveDbContext context = _store.OpenDbContext(saveId);
        SeasonEntity source = await LoadSourceAsync(context, explicitSeason, cancellationToken).ConfigureAwait(false);
        for (int stage = 0; stage < ChaosCupFormat.StageCount; stage++)
        {
            bool hasDraws = await context.ChaosCupDraws.AsNoTracking()
                .AnyAsync(e => e.SourceSeasonId == source.Id && e.StageIndex == stage, cancellationToken)
                .ConfigureAwait(false);
            if (!hasDraws)
            {
                continue;
            }

            if (!await IsStageCompleteAsync(context, source, stage, cancellationToken).ConfigureAwait(false))
            {
                return stage;
            }
        }

        throw new CompleteChaosCupConflictException(
            $"Chaos Cup for Season {explicitSeason} has no incomplete drawn stage; draw the next stage or the tournament is complete.");
    }

    private async Task EnsureStageDrawableAsync(
        Guid saveId,
        int stageIndex,
        int? sourceSeasonNumber,
        CancellationToken cancellationToken)
    {
        int explicitSeason = await ResolveSeasonNumberAsync(saveId, sourceSeasonNumber, cancellationToken).ConfigureAwait(false);
        using SaveDbContext context = _store.OpenDbContext(saveId);
        SeasonEntity source = await LoadSourceAsync(context, explicitSeason, cancellationToken).ConfigureAwait(false);
        bool hasDraws = await context.ChaosCupDraws.AsNoTracking()
            .AnyAsync(e => e.SourceSeasonId == source.Id && e.StageIndex == stageIndex, cancellationToken)
            .ConfigureAwait(false);
        if (!hasDraws)
        {
            throw new CompleteChaosCupConflictException(
                $"Chaos Cup stage {stageIndex} has no persisted draw yet for Season {explicitSeason}.");
        }

        for (int prev = 0; prev < stageIndex; prev++)
        {
            if (!await IsStageCompleteAsync(context, source, prev, cancellationToken).ConfigureAwait(false))
            {
                throw new CompleteChaosCupConflictException(
                    $"Chaos Cup stage {stageIndex} cannot complete before stage {prev} completes.");
            }
        }
    }

    private async Task<int> ResolveSeasonNumberAsync(
        Guid saveId,
        int? sourceSeasonNumber,
        CancellationToken cancellationToken)
    {
        if (sourceSeasonNumber.HasValue)
        {
            return sourceSeasonNumber.Value;
        }

        using SaveDbContext context = _store.OpenDbContext(saveId);
        ChaosCupEditionEntity? latest = await context.ChaosCupEditions.AsNoTracking()
            .OrderByDescending(e => e.SourceSeasonNumber)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (latest is null)
        {
            throw new CompleteChaosCupConflictException("Chaos Cup field has not been selected yet for any season.");
        }

        return latest.SourceSeasonNumber;
    }

    private async Task<SeasonEntity> LoadSourceAsync(
        SaveDbContext context,
        int explicitSeason,
        CancellationToken cancellationToken)
    {
        SeasonEntity? source = await context.Seasons.AsNoTracking()
            .SingleOrDefaultAsync(e => e.SeasonNumber == explicitSeason, cancellationToken)
            .ConfigureAwait(false);
        if (source is null)
        {
            throw new CompleteChaosCupConflictException($"Season {explicitSeason} does not exist for Chaos Cup bulk completion.");
        }

        bool hasEdition = await context.ChaosCupEditions.AsNoTracking()
            .AnyAsync(e => e.SourceSeasonId == source.Id, cancellationToken)
            .ConfigureAwait(false);
        if (!hasEdition)
        {
            throw new CompleteChaosCupConflictException(
                $"Chaos Cup field for Season {explicitSeason} has not been selected yet.");
        }

        return source;
    }

    private static async Task<bool> IsStageCompleteAsync(
        SaveDbContext context,
        SeasonEntity source,
        int stageIndex,
        CancellationToken cancellationToken)
    {
        int groupCount = ChaosCupFormat.GroupCountForStage(stageIndex);
        int expectedRounds = checked(groupCount * ChaosCupFormat.RoundsPerGroup);
        int expectedStandings = ChaosCupFormat.EntrantCountForStage(stageIndex);
        int rounds = await context.ChaosCupRounds.AsNoTracking()
            .CountAsync(e => e.SourceSeasonId == source.Id && e.StageIndex == stageIndex, cancellationToken)
            .ConfigureAwait(false);
        int standings = await context.ChaosCupGroupStandings.AsNoTracking()
            .CountAsync(e => e.SourceSeasonId == source.Id && e.StageIndex == stageIndex, cancellationToken)
            .ConfigureAwait(false);
        return rounds == expectedRounds && standings == expectedStandings;
    }

    private async Task<int> CountStageRoundsAsync(
        Guid saveId,
        int explicitSeason,
        int stageIndex,
        CancellationToken cancellationToken)
    {
        using SaveDbContext context = _store.OpenDbContext(saveId);
        SeasonEntity source = await LoadSourceAsync(context, explicitSeason, cancellationToken).ConfigureAwait(false);
        return await context.ChaosCupRounds.AsNoTracking()
            .CountAsync(e => e.SourceSeasonId == source.Id && e.StageIndex == stageIndex, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<int> CountTournamentRoundsAsync(
        Guid saveId,
        int explicitSeason,
        CancellationToken cancellationToken)
    {
        using SaveDbContext context = _store.OpenDbContext(saveId);
        SeasonEntity source = await LoadSourceAsync(context, explicitSeason, cancellationToken).ConfigureAwait(false);
        return await context.ChaosCupRounds.AsNoTracking()
            .CountAsync(e => e.SourceSeasonId == source.Id, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<bool> HasStageDrawsAsync(
        Guid saveId,
        int explicitSeason,
        int stageIndex,
        CancellationToken cancellationToken)
    {
        using SaveDbContext context = _store.OpenDbContext(saveId);
        SeasonEntity source = await LoadSourceAsync(context, explicitSeason, cancellationToken).ConfigureAwait(false);
        return await context.ChaosCupDraws.AsNoTracking()
            .AnyAsync(e => e.SourceSeasonId == source.Id && e.StageIndex == stageIndex, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<CompleteChaosCupStageResponse> BuildStageResponseAsync(
        Guid saveId,
        int explicitSeason,
        int stageIndex,
        int roundsBefore,
        CancellationToken cancellationToken)
    {
        using SaveDbContext context = _store.OpenDbContext(saveId);
        SeasonEntity source = await LoadSourceAsync(context, explicitSeason, cancellationToken).ConfigureAwait(false);
        int groupCount = ChaosCupFormat.GroupCountForStage(stageIndex);
        List<CompleteChaosCupStageResponse.StageGroupProgress> groups = new(groupCount);
        for (int group = 1; group <= groupCount; group++)
        {
            int played = await context.ChaosCupRounds.AsNoTracking()
                .CountAsync(e => e.SourceSeasonId == source.Id && e.StageIndex == stageIndex && e.GroupNumber == group, cancellationToken)
                .ConfigureAwait(false);
            groups.Add(new CompleteChaosCupStageResponse.StageGroupProgress(
                group, played, played == ChaosCupFormat.RoundsPerGroup));
        }

        int total = groups.Sum(g => g.RoundsPlayed);
        int completed = groups.Count(g => g.IsComplete);
        int expected = checked(groupCount * ChaosCupFormat.RoundsPerGroup);
        return new CompleteChaosCupStageResponse(
            saveId,
            source.SeasonNumber,
            source.Id,
            stageIndex,
            groupCount,
            ChaosCupFormat.EntrantCountForStage(stageIndex),
            completed,
            checked(total - roundsBefore),
            total,
            expected,
            total == expected && completed == groupCount,
            groups);
    }

    private async Task<CompleteChaosCupResponse> BuildCupResponseAsync(
        Guid saveId,
        int explicitSeason,
        int roundsBefore,
        CancellationToken cancellationToken)
    {
        using SaveDbContext context = _store.OpenDbContext(saveId);
        SeasonEntity source = await LoadSourceAsync(context, explicitSeason, cancellationToken).ConfigureAwait(false);
        List<CompleteChaosCupResponse.CupStageProgress> stages = new(ChaosCupFormat.StageCount);
        int groupsCompleted = 0;
        int roundsTotal = 0;
        foreach (int stage in Enumerable.Range(0, ChaosCupFormat.StageCount))
        {
            int groupCount = ChaosCupFormat.GroupCountForStage(stage);
            int expected = checked(groupCount * ChaosCupFormat.RoundsPerGroup);
            int played = await context.ChaosCupRounds.AsNoTracking()
                .CountAsync(e => e.SourceSeasonId == source.Id && e.StageIndex == stage, cancellationToken)
                .ConfigureAwait(false);
            int standings = await context.ChaosCupGroupStandings.AsNoTracking()
                .CountAsync(e => e.SourceSeasonId == source.Id && e.StageIndex == stage, cancellationToken)
                .ConfigureAwait(false);
            bool complete = played == expected && standings == ChaosCupFormat.EntrantCountForStage(stage);

            groupsCompleted = checked(groupsCompleted + (complete ? groupCount : 0));
            roundsTotal = checked(roundsTotal + played);
            stages.Add(new CompleteChaosCupResponse.CupStageProgress(stage, groupCount, complete ? groupCount : 0, complete));
        }

        int totalGroups = ChaosCupFormat.StageGroupCounts.Sum();
        int totalRounds = checked(totalGroups * ChaosCupFormat.RoundsPerGroup);
        return new CompleteChaosCupResponse(
            saveId,
            source.SeasonNumber,
            source.Id,
            stages.Count(s => s.IsComplete),
            ChaosCupFormat.StageCount,
            groupsCompleted,
            totalGroups,
            checked(roundsTotal - roundsBefore),
            roundsTotal,
            totalRounds,
            stages.All(s => s.IsComplete),
            stages);
    }
}
