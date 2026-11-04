using MtgSoloSports.Features.Seasons.SeasonLifecycle;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Seasons.GetSeasonStatus;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Reads the explicit season
/// lifecycle state without mutating or resimulating: the computed phase, the
/// postseason checklist (season complete, movement, qualifier, rebalance, Cup
/// selection/individual/team) and exactly the legal next actions.
/// Read-only queries never take the per-save lock; the schema is still
/// migrated first so saves created before newer tournament tables read
/// correctly through the same concurrency-safe <see cref="SaveStore"/>
/// mechanism as the mutation handlers.
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

        await _store.EnsureMigratedAsync(saveId, cancellationToken).ConfigureAwait(false);
        using SaveDbContext context = _store.OpenDbContext(saveId);
        var rules = await AdvanceRoundHandler.LoadRulesAsync(context, cancellationToken).ConfigureAwait(false);
        SeasonLifecycleSnapshot snapshot = await SeasonLifecycleEvaluator
            .EvaluateAsync(context, saveId, rules, cancellationToken)
            .ConfigureAwait(false);
        // MSS-132: active/scheduled selection formula and its effective source
        // season. Reading status never moves the boundary; missing tables on
        // pre-migration saves fall back to snapshot weights.
        (int effectiveFrom, int version, int bonus, int performance, int form, int chaos, int prestige) =
            await LoadSelectionFormulaAsync(context, rules, snapshot, cancellationToken).ConfigureAwait(false);
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
            snapshot.CupSelectionResolved,
            snapshot.CupIndividualResolved,
            snapshot.CupTeamResolved,
            snapshot.CupComplete,
            snapshot.ReadyToStartNextSeason,
            snapshot.ExpectedCup,
            snapshot.LegalNextActions,
            snapshot.NextActionDetail,
            snapshot.EventProgress,
            snapshot.QualifierProgress,
            snapshot.ChaosEligible,
            snapshot.ChaosSelectionResolved,
            snapshot.ChaosComplete,
            effectiveFrom,
            version,
            bonus,
            performance,
            form,
            chaos,
            prestige);
    }

    internal static async Task<(int EffectiveFrom, int Version, int Bonus, int Performance, int Form, int Chaos, int Prestige)> LoadSelectionFormulaAsync(
        SaveDbContext context,
        SimulationKernel.Rules.RulesV1 rules,
        SeasonLifecycleSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(snapshot);
        try
        {
            Persistence.Saves.CupSelectionFormulaActivationEntity activation =
                await Cups.SelectionFormula.CupSelectionFormulaLifecycle.LoadActivationAsync(context, cancellationToken).ConfigureAwait(false);
            int reference = snapshot.SourceSeasonNumber ?? snapshot.CurrentSeasonNumber;
            Cups.SelectionFormula.CupSelectionFormulaLifecycle.EffectiveWeights weights =
                Cups.SelectionFormula.CupSelectionFormulaLifecycle.ResolveWeights(rules, activation, reference);
            return (activation.EffectiveFromSeasonNumber, activation.FormulaVersion,
                weights.BonusWeightPermille, weights.PerformanceWeightPermille,
                weights.FormWeightPermille, weights.ChaosWeightPermille, weights.PrestigeWeightPermille);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            return (0, 0, rules.CupBonusWeightPermille, rules.CupPerformanceWeightPermille,
                rules.CupFormWeightPermille, rules.CupChaosWeightPermille, rules.CupPrestigeWeightPermille);
        }
    }
}
