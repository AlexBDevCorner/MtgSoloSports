using MtgSoloSports.Features.History.EventRounds;
using MtgSoloSports.Features.Seasons.SeasonLifecycle;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Leagues;
using MtgSoloSports.SimulationKernel.Random;

namespace MtgSoloSports.Features.Qualifiers.PlayFeederQualifierRound;

/// <summary>
/// Endpoint -&gt; Handler direct call. Plays exactly one feeder qualifier round
/// from persisted state under the per-save lock and commits the round with the
/// RNG-after state in one transaction. The final round also completes the
/// selected event exactly as the one-shot run does (standings, roster,
/// RNG tie-break, stories), so per-round and all-at-once sequences from the
/// same save/RNG state give identical persisted outcomes/checksums.
/// Requires the selected event to be the next canonical pending qualifier;
/// out-of-order, already-finished and wrong-identity mutations fail without
/// changing save state and never silently advance a different qualifier.
/// </summary>
public sealed class PlayFeederQualifierRoundHandler
{
    private readonly SaveStore _store;

    public PlayFeederQualifierRoundHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<PlayFeederQualifierRoundResponse> HandleAsync(
        Guid saveId,
        QualifierBoundary boundary,
        int sportingColor,
        CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        if (boundary != QualifierBoundary.Feeder1Feeder2 && boundary != QualifierBoundary.Feeder2Feeder3)
        {
            throw new ArgumentException($"Boundary must be F1↔F2 or F2↔F3, was {boundary}.", nameof(boundary));
        }

        if (sportingColor < 0 || sportingColor >= 8)
        {
            throw new ArgumentException($"Sporting color must be 0..7, was {sportingColor}.", nameof(sportingColor));
        }

        SemaphoreSlim gate = AdvanceRoundSaveLock.Acquire(saveId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await PlayUnderLockAsync(saveId, boundary, sportingColor, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task<PlayFeederQualifierRoundResponse> PlayUnderLockAsync(
        Guid saveId,
        QualifierBoundary boundary,
        int sportingColor,
        CancellationToken cancellationToken)
    {
        await _store.EnsureMigratedAsync(saveId, cancellationToken).ConfigureAwait(false);
        using SaveDbContext context = _store.OpenDbContext(saveId);
        using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        BoundaryQualifierRunner.QualifierState state = await BoundaryQualifierRunner.LoadStateAsync(
            context, saveId, boundary, sportingColor, cancellationToken).ConfigureAwait(false);
        BoundaryQualifierRunner.QualifierRoundPayload payload = BoundaryQualifierRunner.PlayRound(
            state, state.Played, state.Rng);
        List<BoundaryQualifierRunner.QualifierRoundPayload> payloads = [.. state.Played, payload];
        bool complete = payloads.Count == state.Rules.FeederQualifierRounds;

        await PersistSingleAsync(context, state, payloads, payload, complete, cancellationToken).ConfigureAwait(false);

        EventRoundView view = await BuildViewAsync(context, state, payload, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new PlayFeederQualifierRoundResponse(
            view,
            payloads.Count,
            state.Rules.FeederQualifierRounds,
            complete,
            state.Boundary.ToString(),
            state.SportingColor,
            ((SimulationKernel.Catalog.SportingColor)state.SportingColor).ToString(),
            state.Source.SeasonNumber,
            state.Next.SeasonNumber);
    }

    internal static async Task PersistSingleAsync(
        SaveDbContext context,
        BoundaryQualifierRunner.QualifierState state,
        List<BoundaryQualifierRunner.QualifierRoundPayload> payloads,
        BoundaryQualifierRunner.QualifierRoundPayload payload,
        bool complete,
        CancellationToken cancellationToken)
    {
        if (complete)
        {
            await BoundaryQualifierRunner.CompleteAsync(context, state, payloads, cancellationToken).ConfigureAwait(false);
            return;
        }

        context.QualifierRounds.Add(new QualifierRoundEntity
        {
            FromSeasonId = state.Source.Id,
            ToSeasonId = state.Next.Id,
            QualifierBoundary = (int)state.Boundary,
            QualifierSportingColor = state.SportingColor,
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

    internal static async Task<EventRoundView> BuildViewAsync(
        SaveDbContext context,
        BoundaryQualifierRunner.QualifierState state,
        BoundaryQualifierRunner.QualifierRoundPayload payload,
        CancellationToken cancellationToken)
    {
        EventRoundView baseView = await EventRoundViews.BuildAsync(
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
        return baseView with { Title = QualifierIdentity.Display(state.Boundary, state.SportingColor) };
    }
}
