using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Cups.ChaosCup;
using MtgSoloSports.Features.Cups.CupHistory;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Cups;

namespace MtgSoloSports.Features.Cups.GetChaosCupEdition;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Reads one persisted Chaos Cup
/// edition without simulating, drawing or touching sporting RNG: edition status
/// plus six stage summaries built from row counts only (never payloads).
/// Throws <see cref="GetChaosCupEditionNotFoundException"/> (404) when the
/// season has no frozen field; aborts on corrupt counts. Reads never lock.
/// </summary>
public sealed class GetChaosCupEditionHandler
{
    private readonly SaveStore _store;

    public GetChaosCupEditionHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<GetChaosCupEditionResponse> HandleAsync(
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

        await _store.EnsureMigratedAsync(saveId, cancellationToken).ConfigureAwait(false);
        using SaveDbContext context = _store.OpenDbContext(saveId);
        SeasonEntity source = await LoadSourceAsync(context, sourceSeasonNumber, cancellationToken).ConfigureAwait(false);
        return await BuildResponseAsync(context, saveId, source, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<SeasonEntity> LoadSourceAsync(
        SaveDbContext context,
        int? sourceSeasonNumber,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (sourceSeasonNumber.HasValue)
        {
            SeasonEntity? explicitSeason = await context.Seasons
                .AsNoTracking()
                .SingleOrDefaultAsync(e => e.SeasonNumber == sourceSeasonNumber.Value, cancellationToken)
                .ConfigureAwait(false);
            if (explicitSeason is null)
            {
                throw new GetChaosCupEditionNotFoundException(
                    $"Chaos Cup edition for Season {sourceSeasonNumber.Value} has not been resolved yet.");
            }

            await EnsureEditionAsync(context, explicitSeason, cancellationToken).ConfigureAwait(false);
            return explicitSeason;
        }

        ChaosCupEditionEntity? latest = await context.ChaosCupEditions
            .AsNoTracking()
            .OrderByDescending(e => e.SourceSeasonNumber)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (latest is null)
        {
            throw new GetChaosCupEditionNotFoundException("Chaos Cup edition has not been resolved yet.");
        }

        SeasonEntity? source = await context.Seasons
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.Id == latest.SourceSeasonId, cancellationToken)
            .ConfigureAwait(false);
        return source ?? throw new InvalidOperationException("Chaos Cup edition references an unknown season.");
    }

    internal static async Task EnsureEditionAsync(
        SaveDbContext context,
        SeasonEntity source,
        CancellationToken cancellationToken)
    {
        bool exists = await context.ChaosCupEditions
            .AsNoTracking()
            .AnyAsync(e => e.SourceSeasonId == source.Id, cancellationToken)
            .ConfigureAwait(false);
        if (!exists)
        {
            throw new GetChaosCupEditionNotFoundException(
                $"Chaos Cup edition for Season {source.SeasonNumber} has not been resolved yet.");
        }
    }

    internal static async Task<GetChaosCupEditionResponse> BuildResponseAsync(
        SaveDbContext context,
        Guid saveId,
        SeasonEntity source,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(source);
        var rules = await AdvanceRoundHandler.LoadRulesAsync(context, cancellationToken).ConfigureAwait(false);
        ChaosCupEditionEntity edition = await context.ChaosCupEditions
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.SourceSeasonId == source.Id, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new GetChaosCupEditionNotFoundException(
                $"Chaos Cup edition for Season {source.SeasonNumber} has not been resolved yet.");
        ChaosCupInvariants.ValidateEdition(edition, rules);

        List<GetChaosCupEditionResponse.StageSummary> stages = new(ChaosCupFormat.StageCount);
        int roundsPlayed = 0;
        int groupsCompleted = 0;
        for (int stage = 0; stage < ChaosCupFormat.StageCount; stage++)
        {
            GetChaosCupEditionResponse.StageSummary summary = await BuildStageAsync(context, source, stage, cancellationToken).ConfigureAwait(false);
            stages.Add(summary);
            roundsPlayed = checked(roundsPlayed + summary.RoundsPlayed);
            groupsCompleted = checked(groupsCompleted + summary.GroupsCompleted);
        }

        bool complete = stages.All(s => s.IsComplete);
        return new GetChaosCupEditionResponse(
            saveId,
            source.SeasonNumber,
            source.Id,
            edition.RulesVersion,
            edition.ChaosFormatVersion,
            edition.FieldSize,
            edition.EditionChecksum,
            complete ? CupEditionState.Completed : CupEditionState.InProgress,
            complete,
            ChaosCupFormat.StageCount,
            ChaosCupFormat.StageGroupCounts.Sum(),
            checked(ChaosCupFormat.StageGroupCounts.Sum() * ChaosCupFormat.RoundsPerGroup),
            roundsPlayed,
            groupsCompleted,
            stages);
    }

    private static async Task<GetChaosCupEditionResponse.StageSummary> BuildStageAsync(
        SaveDbContext context,
        SeasonEntity source,
        int stageIndex,
        CancellationToken cancellationToken)
    {
        int groupCount = ChaosCupFormat.GroupCountForStage(stageIndex);
        int entrantCount = ChaosCupFormat.EntrantCountForStage(stageIndex);
        int totalRounds = checked(groupCount * ChaosCupFormat.RoundsPerGroup);
        int draws = await context.ChaosCupDraws
            .AsNoTracking()
            .CountAsync(e => e.SourceSeasonId == source.Id && e.StageIndex == stageIndex, cancellationToken)
            .ConfigureAwait(false);
        int rounds = await context.ChaosCupRounds
            .AsNoTracking()
            .CountAsync(e => e.SourceSeasonId == source.Id && e.StageIndex == stageIndex, cancellationToken)
            .ConfigureAwait(false);
        int standings = await context.ChaosCupGroupStandings
            .AsNoTracking()
            .CountAsync(e => e.SourceSeasonId == source.Id && e.StageIndex == stageIndex, cancellationToken)
            .ConfigureAwait(false);
        if (draws != 0 && draws != entrantCount)
        {
            throw new InvalidOperationException($"Chaos Cup stage {stageIndex} holds {draws} draws, expected {entrantCount}.");
        }

        if (standings % ChaosCupFormat.GroupSize != 0)
        {
            throw new InvalidOperationException($"Chaos Cup stage {stageIndex} holds {standings} standings, expected multiples of {ChaosCupFormat.GroupSize}.");
        }

        string checksum = string.Empty;
        if (draws != 0)
        {
            checksum = await context.ChaosCupDraws
                .AsNoTracking()
                .Where(e => e.SourceSeasonId == source.Id && e.StageIndex == stageIndex)
                .Select(e => e.DrawChecksum)
                .FirstAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        bool isComplete = draws == entrantCount && rounds == totalRounds && standings == entrantCount;
        string state = draws == 0
            ? GetChaosCupEditionResponse.NotDrawn
            : isComplete ? CupEditionState.Completed : CupEditionState.InProgress;
        return new GetChaosCupEditionResponse.StageSummary(
            stageIndex,
            groupCount,
            entrantCount,
            ChaosCupFormat.IsFinalStage(stageIndex),
            state,
            draws != 0,
            isComplete,
            checksum,
            checked(standings / ChaosCupFormat.GroupSize),
            rounds,
            totalRounds,
            standings);
    }
}
