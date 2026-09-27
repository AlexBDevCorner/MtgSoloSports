using MtgSoloSports.Features.Seasons.GetSeasonStatus;
using MtgSoloSports.Features.Seasons.SeasonLifecycle;
using MtgSoloSports.Features.Seasons.StartNextSeason;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Features.Simulation.CompleteStageForAllLeagues;
using MtgSoloSports.Features.Superleague.CreateInaugural;
using MtgSoloSports.Features.Superleague.RebalanceFeeders;
using MtgSoloSports.Features.Superleague.ResolveAutomaticMovement;
using MtgSoloSports.Features.Superleague.RunQualifier;
using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Seasons.AdvanceToNextEvent;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Advances exactly one legal
/// lifecycle event so users progress without knowing internal command ordering.
/// The orchestration slice calls focused feature operations and is behaviorally
/// equivalent to executing the same underlying operation directly with the
/// identical RNG and mathematics sequence. Order after Stage 32 is movement,
/// qualifier (normal seasons only), rebalancing, then next-season start; each
/// step exposes its inspectable event boundary and illegal transitions are
/// rejected rather than skipped. Holds one per-save lock for inspection plus
/// the single delegated mutation; read-only status queries never lock.
/// </summary>
public sealed class AdvanceToNextEventHandler
{
    private readonly SaveStore _store;

    public AdvanceToNextEventHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<AdvanceToNextEventResponse> HandleAsync(Guid saveId, CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        SemaphoreSlim gate = AdvanceRoundSaveLock.Acquire(saveId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await AdvanceUnderLockAsync(saveId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task<AdvanceToNextEventResponse> AdvanceUnderLockAsync(
        Guid saveId,
        CancellationToken cancellationToken)
    {
        await _store.EnsureMigratedAsync(saveId, cancellationToken).ConfigureAwait(false);
        string beforeAction = await ResolveNextActionAsync(saveId, cancellationToken).ConfigureAwait(false);
        string executedDetail = await ExecuteActionUnderLockAsync(saveId, beforeAction, cancellationToken).ConfigureAwait(false);
        GetSeasonStatusResponse after = await LoadStatusAsync(saveId, cancellationToken).ConfigureAwait(false);
        return new AdvanceToNextEventResponse(
            saveId,
            beforeAction,
            executedDetail,
            after.CurrentSeasonNumber,
            after.ComputedPhase,
            after.PersistedPhase,
            after.SourceSeasonNumber,
            after.NextSeasonNumber,
            after.IsInauguralTransition,
            after.GlobalStage,
            after.IsCurrentSeasonComplete,
            after.LegalNextActions,
            after.NextActionDetail);
    }

    internal async Task<string> ResolveNextActionAsync(Guid saveId, CancellationToken cancellationToken)
    {
        GetSeasonStatusResponse status = await LoadStatusAsync(saveId, cancellationToken).ConfigureAwait(false);
        if (status.LegalNextActions.Count != 1)
        {
            throw new AdvanceToNextEventConflictException(
                $"Save has {status.LegalNextActions.Count} legal next actions; exactly one is required to advance.");
        }

        string action = status.LegalNextActions[0];
        ValidateAction(action, status);
        return action;
    }

    internal static void ValidateAction(string action, GetSeasonStatusResponse status)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentNullException.ThrowIfNull(status);
        bool legal = action switch
        {
            SeasonLifecycleActions.CompleteNextGlobalStage => !status.IsCurrentSeasonComplete,
            SeasonLifecycleActions.ResolveInauguralMovement => status is { IsCurrentSeasonComplete: true, IsInauguralTransition: true, MovementResolved: false },
            SeasonLifecycleActions.ResolveAutomaticMovement => status is { IsCurrentSeasonComplete: true, IsInauguralTransition: false, MovementResolved: false },
            SeasonLifecycleActions.RunQualifier => status is { MovementResolved: true, QualifierResolved: false, IsInauguralTransition: false },
            SeasonLifecycleActions.RebalanceFeeders => status is { MovementResolved: true, Rebalanced: false }
                && (status.IsInauguralTransition || status.QualifierResolved),
            SeasonLifecycleActions.StartNextSeason => status.ReadyToStartNextSeason,
            _ => false,
        };
        if (!legal)
        {
            throw new AdvanceToNextEventConflictException(
                $"Action '{action}' is not legal in phase '{status.ComputedPhase}'.");
        }
    }

    internal async Task<string> ExecuteActionUnderLockAsync(
        Guid saveId, string action, CancellationToken cancellationToken)
    {
        return action switch
        {
            SeasonLifecycleActions.CompleteNextGlobalStage => await ExecuteCompleteStageAsync(saveId, cancellationToken).ConfigureAwait(false),
            SeasonLifecycleActions.ResolveInauguralMovement => await ExecuteInauguralAsync(saveId, cancellationToken).ConfigureAwait(false),
            SeasonLifecycleActions.ResolveAutomaticMovement => await ExecuteAutomaticAsync(saveId, cancellationToken).ConfigureAwait(false),
            SeasonLifecycleActions.RunQualifier => await ExecuteQualifierAsync(saveId, cancellationToken).ConfigureAwait(false),
            SeasonLifecycleActions.RebalanceFeeders => await ExecuteRebalanceAsync(saveId, cancellationToken).ConfigureAwait(false),
            SeasonLifecycleActions.StartNextSeason => await ExecuteStartNextAsync(saveId, cancellationToken).ConfigureAwait(false),
            _ => throw new AdvanceToNextEventConflictException($"Unknown lifecycle action '{action}'."),
        };
    }

    internal async Task<GetSeasonStatusResponse> LoadStatusAsync(Guid saveId, CancellationToken cancellationToken)
    {
        // Status evaluation opens its own short-lived context. The outer
        // per-save lock is already held, so evaluation observes a stable state
        // while read-only queries elsewhere never lock.
        GetSeasonStatusHandler status = new(_store);
        return await status.HandleAsync(saveId, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<string> ExecuteCompleteStageAsync(Guid saveId, CancellationToken cancellationToken)
    {
        CompleteStageForAllLeaguesHandler handler = new(_store);
        CompleteStageForAllLeaguesResponse response = await handler
            .CompleteGlobalStageUnderLockAsync(saveId, cancellationToken)
            .ConfigureAwait(false);
        return $"Completed global stage {response.CompletedStage} for season {response.SeasonNumber} ({response.Leagues.Count} leagues; season complete: {response.IsSeasonComplete}).";
    }

    internal async Task<string> ExecuteInauguralAsync(Guid saveId, CancellationToken cancellationToken)
    {
        CreateInauguralSuperleagueHandler handler = new(_store);
        CreateInauguralSuperleagueResponse response = await handler
            .CreateUnderLockAsync(saveId, cancellationToken)
            .ConfigureAwait(false);
        return $"Created inaugural Superleague for Season {response.SeasonTwoNumber} ({response.Members.Count} promotions).";
    }

    internal async Task<string> ExecuteAutomaticAsync(Guid saveId, CancellationToken cancellationToken)
    {
        ResolveAutomaticMovementHandler handler = new(_store);
        ResolveAutomaticMovementResponse response = await handler
            .ResolveUnderLockAsync(saveId, cancellationToken)
            .ConfigureAwait(false);
        return $"Resolved automatic movement Season {response.FromSeasonNumber} -> {response.ToSeasonNumber} (safe {response.Safe.Count}, promoted {response.Promoted.Count}, relegated {response.Relegated.Count}, incumbents {response.QualifierIncumbents.Count}, challengers {response.QualifierChallengers.Count}).";
    }

    internal async Task<string> ExecuteQualifierAsync(Guid saveId, CancellationToken cancellationToken)
    {
        RunQualifierHandler handler = new(_store);
        RunQualifierResponse response = await handler
            .RunUnderLockAsync(saveId, cancellationToken)
            .ConfigureAwait(false);
        return $"Ran Superleague qualifier Season {response.FromSeasonNumber} -> {response.ToSeasonNumber} ({response.Winners} winners from {response.QualifierSize} over {response.Rounds} rounds).";
    }

    internal async Task<string> ExecuteRebalanceAsync(Guid saveId, CancellationToken cancellationToken)
    {
        RebalanceFeedersHandler handler = new(_store);
        RebalanceFeedersResponse response = await handler
            .RebalanceUnderLockAsync(saveId, cancellationToken)
            .ConfigureAwait(false);
        return $"Rebalanced feeders Season {response.FromSeasonNumber} -> {response.ToSeasonNumber} (drawn {response.TotalDrawn}, displaced {response.TotalDisplaced}).";
    }

    internal async Task<string> ExecuteStartNextAsync(Guid saveId, CancellationToken cancellationToken)
    {
        StartNextSeasonHandler handler = new(_store);
        StartNextSeasonResponse response = await handler
            .StartUnderLockAsync(saveId, cancellationToken)
            .ConfigureAwait(false);
        return $"Started Season {response.ToSeasonNumber} (active {response.ActiveAthletes}, pool {response.PoolAthletes}; Cup extension point {response.ExpectedCup} validated).";
    }
}
