using MtgSoloSports.Features.Cups.RunColorCupTeam;
using MtgSoloSports.Features.History.EventRounds;
using MtgSoloSports.Features.Reports.GoogleDrivePublishing;
using MtgSoloSports.Features.Seasons.SeasonLifecycle;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Random;

namespace MtgSoloSports.Features.Cups.PlayColorCupTeamRound;

/// <summary>
/// Endpoint -&gt; Handler direct call. Plays exactly one Color Cup team group
/// round from the persisted state under the per-save lock and commits it with
/// the RNG state in one transaction (including the group's leg tie-break when
/// a group finishes). The final round completes the event exactly as the
/// one-shot run does.
/// </summary>
public sealed class PlayColorCupTeamRoundHandler
{
    private readonly SaveStore _store;
    private readonly DriveAutoPublishNotifier? _autoPublish;

    public PlayColorCupTeamRoundHandler(SaveStore store, DriveAutoPublishNotifier? autoPublish = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _autoPublish = autoPublish;
    }

    public async Task<PlayColorCupTeamRoundResponse> HandleAsync(
        Guid saveId,
        int? sourceSeasonNumber = null,
        CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        SemaphoreSlim gate = AdvanceRoundSaveLock.Acquire(saveId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        PlayColorCupTeamRoundResponse response;
        try
        {
            response = await PlayUnderLockAsync(saveId, sourceSeasonNumber, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }

        // MSS-091: Live round-by-round finalization changes the same Cup
        // edition/team-history reports as the one-shot run. Post-commit note
        // only; the notifier decides whether the checkpoint advanced.
        if (_autoPublish is not null)
        {
            await _autoPublish.NotifyStageCheckpointAsync(saveId, CancellationToken.None).ConfigureAwait(false);
        }

        return response;
    }

    internal async Task<PlayColorCupTeamRoundResponse> PlayUnderLockAsync(
        Guid saveId,
        int? sourceSeasonNumber,
        CancellationToken cancellationToken)
    {
        await _store.EnsureMigratedAsync(saveId, cancellationToken).ConfigureAwait(false);
        using SaveDbContext context = _store.OpenDbContext(saveId);
        using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        RunColorCupTeamHandler.TeamState state = await RunColorCupTeamHandler
            .LoadStateAsync(context, saveId, sourceSeasonNumber, cancellationToken).ConfigureAwait(false);
        (ColorCupTeamRoundPayloadDocument payload, Pcg32State after) = RunColorCupTeamHandler.PlayRound(state, state.Played, state.Rng);
        List<ColorCupTeamRoundPayloadDocument> payloads = [.. state.Played, payload];
        int total = state.Shape.TotalRounds;
        bool complete = payloads.Count == total;

        if (complete)
        {
            await RunColorCupTeamHandler.CompleteAsync(context, state, payloads, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            context.ColorCupTeamRounds.Add(new ColorCupTeamRoundEntity
            {
                SourceSeasonId = state.Source.Id,
                SourceSeasonNumber = state.Source.SeasonNumber,
                GroupNumber = payload.GroupNumber,
                RoundNumber = payload.RoundNumber,
                RulesVersion = payload.RulesVersion,
                RngBeforeState = unchecked((long)payload.RngBeforeState),
                RngBeforeStream = unchecked((long)payload.RngBeforeStream),
                RngAfterState = unchecked((long)payload.RngAfterState),
                RngAfterStream = unchecked((long)payload.RngAfterStream),
                PayloadJson = payload.ToStored(),
                PayloadChecksum = payload.Checksum,
            });
            context.ApplyRngState(after);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        EventRoundView view = await EventRoundViews.BuildAsync(
            context,
            state.Source.SeasonNumber,
            PostseasonEvents.ColorCupTeam,
            payload.GroupNumber,
            payload.RoundNumber,
            payload.RulesVersion,
            payload.Checksum,
            new Pcg32State(payload.RngBeforeState, payload.RngBeforeStream),
            new Pcg32State(payload.RngAfterState, payload.RngAfterStream),
            payload.Placements,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new PlayColorCupTeamRoundResponse(view, payloads.Count, total, complete);
    }
}
