using MtgSoloSports.Features.Cups.RunColorCupIndividual;
using MtgSoloSports.Features.History.EventRounds;
using MtgSoloSports.Features.Seasons.SeasonLifecycle;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Random;

namespace MtgSoloSports.Features.Cups.PlayColorCupIndividualRound;

/// <summary>
/// Endpoint -&gt; Handler direct call. Plays exactly one Color Cup individual
/// round from the persisted state under the per-save lock and commits the
/// round with the RNG-after state in one transaction. The final round also
/// completes the event exactly as the one-shot run does.
/// </summary>
public sealed class PlayColorCupIndividualRoundHandler
{
    private readonly SaveStore _store;

    public PlayColorCupIndividualRoundHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<PlayColorCupIndividualRoundResponse> HandleAsync(
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

    internal async Task<PlayColorCupIndividualRoundResponse> PlayUnderLockAsync(
        Guid saveId,
        int? sourceSeasonNumber,
        CancellationToken cancellationToken)
    {
        await _store.EnsureMigratedAsync(saveId, cancellationToken).ConfigureAwait(false);
        using SaveDbContext context = _store.OpenDbContext(saveId);
        using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        RunColorCupIndividualHandler.CupState state = await RunColorCupIndividualHandler
            .LoadStateAsync(context, saveId, sourceSeasonNumber, cancellationToken).ConfigureAwait(false);
        ColorCupIndividualRoundPayloadDocument payload = RunColorCupIndividualHandler.PlayRound(state, state.Played, state.Rng);
        List<ColorCupIndividualRoundPayloadDocument> payloads = [.. state.Played, payload];
        int total = state.Rules.ColorCupIndividualRounds;
        bool complete = payloads.Count == total;

        if (complete)
        {
            await RunColorCupIndividualHandler.CompleteAsync(context, state, payloads, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            context.ColorCupIndividualRounds.Add(new ColorCupIndividualRoundEntity
            {
                SourceSeasonId = state.Source.Id,
                SourceSeasonNumber = state.Source.SeasonNumber,
                RoundNumber = payload.RoundNumber,
                RulesVersion = payload.RulesVersion,
                RngBeforeState = unchecked((long)payload.RngBeforeState),
                RngBeforeStream = unchecked((long)payload.RngBeforeStream),
                RngAfterState = unchecked((long)payload.RngAfterState),
                RngAfterStream = unchecked((long)payload.RngAfterStream),
                PayloadJson = payload.ToStored(),
                PayloadChecksum = payload.Checksum,
            });
            context.ApplyRngState(new Pcg32State(payload.RngAfterState, payload.RngAfterStream));
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        EventRoundView view = await EventRoundViews.BuildAsync(
            context,
            state.Source.SeasonNumber,
            PostseasonEvents.ColorCupIndividual,
            group: null,
            payload.RoundNumber,
            payload.RulesVersion,
            payload.Checksum,
            new Pcg32State(payload.RngBeforeState, payload.RngBeforeStream),
            new Pcg32State(payload.RngAfterState, payload.RngAfterStream),
            payload.Placements,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new PlayColorCupIndividualRoundResponse(view, payloads.Count, total, complete);
    }
}
