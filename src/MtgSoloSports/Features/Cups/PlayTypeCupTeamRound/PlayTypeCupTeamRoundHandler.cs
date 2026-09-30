using MtgSoloSports.Features.Cups.RunTypeCupTeam;
using MtgSoloSports.Features.History.EventRounds;
using MtgSoloSports.Features.Seasons.SeasonLifecycle;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Random;

namespace MtgSoloSports.Features.Cups.PlayTypeCupTeamRound;

/// <summary>
/// Endpoint -&gt; Handler direct call. Plays exactly one Type Cup team group
/// round from the persisted state under the per-save lock and commits it with
/// the RNG state in one transaction (including the group's leg tie-break when
/// a group finishes). The final round completes the event exactly as the
/// one-shot run does.
/// </summary>
public sealed class PlayTypeCupTeamRoundHandler
{
    private readonly SaveStore _store;

    public PlayTypeCupTeamRoundHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<PlayTypeCupTeamRoundResponse> HandleAsync(
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
        try
        {
            return await PlayUnderLockAsync(saveId, sourceSeasonNumber, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task<PlayTypeCupTeamRoundResponse> PlayUnderLockAsync(
        Guid saveId,
        int? sourceSeasonNumber,
        CancellationToken cancellationToken)
    {
        await _store.EnsureMigratedAsync(saveId, cancellationToken).ConfigureAwait(false);
        using SaveDbContext context = _store.OpenDbContext(saveId);
        using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        RunTypeCupTeamHandler.TeamState state = await RunTypeCupTeamHandler
            .LoadStateAsync(context, saveId, sourceSeasonNumber, cancellationToken).ConfigureAwait(false);
        (TypeCupTeamRoundPayloadDocument payload, Pcg32State after) = RunTypeCupTeamHandler.PlayRound(state, state.Played, state.Rng);
        List<TypeCupTeamRoundPayloadDocument> payloads = [.. state.Played, payload];
        int total = state.Shape.TotalRounds;
        bool complete = payloads.Count == total;

        if (complete)
        {
            await RunTypeCupTeamHandler.CompleteAsync(context, state, payloads, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            context.TypeCupTeamRounds.Add(new TypeCupTeamRoundEntity
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
            PostseasonEvents.TypeCupTeam,
            payload.GroupNumber,
            payload.RoundNumber,
            payload.RulesVersion,
            payload.Checksum,
            new Pcg32State(payload.RngBeforeState, payload.RngBeforeStream),
            new Pcg32State(payload.RngAfterState, payload.RngAfterStream),
            payload.Placements,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new PlayTypeCupTeamRoundResponse(view, payloads.Count, total, complete);
    }
}
