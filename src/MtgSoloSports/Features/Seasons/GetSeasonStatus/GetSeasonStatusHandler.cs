using MtgSoloSports.Features.Seasons.SeasonLifecycle;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Seasons.GetSeasonStatus;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Reads the explicit season
/// lifecycle state without mutating or resimulating: the computed phase, the
/// postseason checklist (season complete, movement, qualifier, rebalance), the
/// post-rebalance Cup extension point and exactly the legal next actions.
/// Read-only queries never take the per-save lock.
/// </summary>
public sealed class GetSeasonStatusHandler
{
    private readonly SaveStore _store;

    public GetSeasonStatusHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<GetSeasonStatusResponse> HandleAsync(Guid saveId, CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        using SaveDbContext context = _store.OpenDbContext(saveId);
        var rules = await AdvanceRoundHandler.LoadRulesAsync(context, cancellationToken).ConfigureAwait(false);
        SeasonLifecycleSnapshot snapshot = await SeasonLifecycleEvaluator
            .EvaluateAsync(context, saveId, rules, cancellationToken)
            .ConfigureAwait(false);
        return new GetSeasonStatusResponse(
            snapshot.SaveId,
            snapshot.CurrentSeasonNumber,
            snapshot.PersistedPhase,
            snapshot.ComputedPhase,
            snapshot.SourceSeasonNumber,
            snapshot.NextSeasonNumber,
            snapshot.IsInauguralTransition,
            snapshot.GlobalStage,
            snapshot.IsCurrentSeasonComplete,
            snapshot.SeasonComplete,
            snapshot.MovementResolved,
            snapshot.QualifierResolved,
            snapshot.Rebalanced,
            snapshot.ReadyToStartNextSeason,
            snapshot.ExpectedCup,
            snapshot.LegalNextActions,
            snapshot.NextActionDetail);
    }
}
