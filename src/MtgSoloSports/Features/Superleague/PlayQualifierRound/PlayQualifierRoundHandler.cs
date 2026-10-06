using MtgSoloSports.Features.History.EventRounds;
using MtgSoloSports.Features.Seasons.SeasonLifecycle;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Features.Superleague.RunQualifier;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Random;

namespace MtgSoloSports.Features.Superleague.PlayQualifierRound;

/// <summary>
/// Endpoint -&gt; Handler direct call. Plays exactly one qualifier round from
/// the persisted state under the per-save lock and commits the round with the
/// RNG-after state in one transaction. The final round also completes the
/// qualifier exactly as the one-shot run does, so results are identical.
/// </summary>
public sealed class PlayQualifierRoundHandler
{
    private readonly SaveStore _store;

    public PlayQualifierRoundHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<PlayQualifierRoundResponse> HandleAsync(Guid saveId, CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        SemaphoreSlim gate = AdvanceRoundSaveLock.Acquire(saveId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await PlayUnderLockAsync(saveId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task<PlayQualifierRoundResponse> PlayUnderLockAsync(Guid saveId, CancellationToken cancellationToken)
    {
        await _store.EnsureMigratedAsync(saveId, cancellationToken).ConfigureAwait(false);
        using SaveDbContext context = _store.OpenDbContext(saveId);
        using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        RunQualifierHandler.QualifierState state = await RunQualifierHandler.LoadStateAsync(context, saveId, cancellationToken).ConfigureAwait(false);
        QualifierRoundPayloadDocument payload = RunQualifierHandler.PlayRound(state, state.Played, state.Rng);
        List<QualifierRoundPayloadDocument> payloads = [.. state.Played, payload];
        int total = state.Rules.QualifierRounds;
        bool complete = payloads.Count == total;

        if (complete)
        {
            await RunQualifierHandler.CompleteAsync(context, state, payloads, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            context.QualifierRounds.Add(new QualifierRoundEntity
            {
                FromSeasonId = state.Source.Id,
                ToSeasonId = state.Next.Id,
                QualifierBoundary = (int)SimulationKernel.Leagues.QualifierBoundary.Superleague,
                QualifierSportingColor = SimulationKernel.Leagues.QualifierIdentity.SuperleagueColorSentinel,
                RoundNumber = payload.RoundNumber,
                RulesVersion = payload.RulesVersion,
                RngBeforeState = unchecked((long)payload.RngBeforeState),
                RngBeforeStream = unchecked((long)payload.RngBeforeStream),
                RngAfterState = unchecked((long)payload.RngAfterState),
                RngAfterStream = unchecked((long)payload.RngAfterStream),
                PayloadJson = payload.ToJson(),
                PayloadChecksum = payload.Checksum,
            });
            context.ApplyRngState(new Pcg32State(payload.RngAfterState, payload.RngAfterStream));
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        EventRoundView view = await EventRoundViews.BuildAsync(
            context,
            state.Source.SeasonNumber,
            PostseasonEvents.Qualifier,
            group: null,
            payload.RoundNumber,
            payload.RulesVersion,
            payload.Checksum,
            new Pcg32State(payload.RngBeforeState, payload.RngBeforeStream),
            new Pcg32State(payload.RngAfterState, payload.RngAfterStream),
            payload.Placements,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new PlayQualifierRoundResponse(view, payloads.Count, total, complete);
    }
}
