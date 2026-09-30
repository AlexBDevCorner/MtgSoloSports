using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Saves;
using MtgSoloSports.Features.Simulation.GlobalStage;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Features.Seasons.SeasonLifecycle;

/// <summary>
/// Pure lifecycle computation shared by the season-status read model, the
/// AdvanceToNextEvent orchestrator and the StartNextSeason gate. Derives the
/// authoritative phase from persisted seasons, standings, movements, qualifier,
/// roster and Cup rows; fundamental invariant failures throw and abort, never
/// silently repair. Season 1 uses the special inaugural chain (no qualifier);
/// Season 2+ uses automatic movement plus qualifier. Both converge on
/// Rebalanced, then run the alternating post-season Cup (odd seasons Color Cup
/// with selection, individual and team; even seasons Type Cup with selection
/// and team) before StartNextSeason. Each Cup step is an explicit inspectable
/// Next Event boundary with deterministic fast/manual equivalence.
/// </summary>
public static class SeasonLifecycleEvaluator
{
    public static async Task<SeasonLifecycleSnapshot> EvaluateAsync(
        SaveDbContext context,
        Guid saveId,
        RulesV1 rules,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(rules);
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        SaveMetadataEntity metadata = await LoadMetadataAsync(context, saveId, cancellationToken).ConfigureAwait(false);
        string persistedPhase = metadata.Phase;
        _ = SavePhaseParser.Parse(persistedPhase);

        SeasonEntity current = await LoadCurrentSeasonAsync(context, metadata, cancellationToken).ConfigureAwait(false);
        List<SeasonEntity> allSeasons = await context.Seasons
            .AsNoTracking()
            .OrderBy(e => e.SeasonNumber)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        ValidateSeasonChain(allSeasons, current);

        SeasonLifecycleSnapshot snapshot = !current.IsComplete
            ? await EvaluateInProgressAsync(context, saveId, metadata, current, allSeasons, rules, persistedPhase, cancellationToken).ConfigureAwait(false)
            : await EvaluateCompletedAsync(context, saveId, metadata, current, allSeasons, rules, persistedPhase, cancellationToken).ConfigureAwait(false);
        return await PostseasonEvents.WithEventProgressAsync(context, snapshot, rules, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<SaveMetadataEntity> LoadMetadataAsync(
        SaveDbContext context, Guid saveId, CancellationToken cancellationToken)
    {
        SaveMetadataEntity? metadata = await context.SaveMetadata
            .SingleOrDefaultAsync(e => e.Id == 1, cancellationToken)
            .ConfigureAwait(false);
        if (metadata is null)
        {
            throw new InvalidOperationException($"Save '{saveId:D}' is missing metadata.");
        }

        if (metadata.SaveId != saveId)
        {
            throw new InvalidOperationException($"Save '{saveId:D}' has mismatched identity.");
        }

        return metadata;
    }

    internal static async Task<SeasonEntity> LoadCurrentSeasonAsync(
        SaveDbContext context, SaveMetadataEntity metadata, CancellationToken cancellationToken)
    {
        SeasonEntity? season = await context.Seasons
            .SingleOrDefaultAsync(e => e.SeasonNumber == metadata.CurrentSeason, cancellationToken)
            .ConfigureAwait(false);
        if (season is null)
        {
            throw new InvalidOperationException($"Save has no season {metadata.CurrentSeason}.");
        }

        return season;
    }

    internal static void ValidateSeasonChain(List<SeasonEntity> allSeasons, SeasonEntity current)
    {
        ArgumentNullException.ThrowIfNull(allSeasons);
        ArgumentNullException.ThrowIfNull(current);
        if (allSeasons.Count == 0)
        {
            throw new InvalidOperationException("Save has no seasons.");
        }

        HashSet<int> numbers = new();
        foreach (SeasonEntity season in allSeasons)
        {
            if (!numbers.Add(season.SeasonNumber))
            {
                throw new InvalidOperationException($"Save has duplicate season {season.SeasonNumber}.");
            }
        }

        SeasonEntity max = allSeasons.OrderByDescending(s => s.SeasonNumber).First();
        if (max.SeasonNumber < current.SeasonNumber)
        {
            throw new InvalidOperationException($"Current season {current.SeasonNumber} is beyond the latest persisted season {max.SeasonNumber}.");
        }

        if (max.SeasonNumber > current.SeasonNumber + 1)
        {
            throw new InvalidOperationException(
                $"Save has seasons beyond the pending transition (current {current.SeasonNumber}, latest {max.SeasonNumber}); start the next season before creating further seasons.");
        }
    }

    internal static async Task<SeasonLifecycleSnapshot> EvaluateInProgressAsync(
        SaveDbContext context,
        Guid saveId,
        SaveMetadataEntity metadata,
        SeasonEntity current,
        List<SeasonEntity> allSeasons,
        RulesV1 rules,
        string persistedPhase,
        CancellationToken cancellationToken)
    {
        SeasonEntity? successor = allSeasons.SingleOrDefault(s => s.SeasonNumber == current.SeasonNumber + 1);
        if (successor is not null)
        {
            throw new InvalidOperationException(
                $"Season {current.SeasonNumber} is still in progress but season {successor.SeasonNumber} already exists; start the next season only from a fully valid rebalanced roster.");
        }

        GlobalStageGate.GlobalStageView global = await GlobalStageGate
            .LoadGlobalStageAsync(context, current, rules, cancellationToken)
            .ConfigureAwait(false);
        if (global.IsSeasonComplete)
        {
            throw new InvalidOperationException($"Season {current.SeasonNumber} reports a complete global stage but is not marked complete.");
        }

        string detail = $"Stage {global.CurrentStage} must complete for every active league before stage {global.CurrentStage + 1} can begin. Next backend step completes the global stage.";
        return new SeasonLifecycleSnapshot(
            saveId,
            current.SeasonNumber,
            persistedPhase,
            SavePhaseParser.ToText(SavePhase.SeasonInProgress),
            SourceSeasonNumber: null,
            NextSeasonNumber: null,
            IsInauguralTransition: false,
            global.CurrentStage,
            IsCurrentSeasonComplete: false,
            SeasonComplete: false,
            MovementResolved: false,
            QualifierResolved: false,
            Rebalanced: false,
            CupSelectionResolved: false,
            CupIndividualResolved: false,
            CupTeamResolved: false,
            CupComplete: false,
            ReadyToStartNextSeason: false,
            CupExtensionPoint.NoCup,
            [SeasonLifecycleActions.CompleteNextGlobalStage],
            detail);
    }

    internal static async Task<SeasonLifecycleSnapshot> EvaluateCompletedAsync(
        SaveDbContext context,
        Guid saveId,
        SaveMetadataEntity metadata,
        SeasonEntity current,
        List<SeasonEntity> allSeasons,
        RulesV1 rules,
        string persistedPhase,
        CancellationToken cancellationToken)
    {
        SeasonEntity? successor = allSeasons.SingleOrDefault(s => s.SeasonNumber == current.SeasonNumber + 1);
        if (successor is null)
        {
            return EvaluateSeasonCompleteNoSuccessor(context, saveId, current, rules, persistedPhase);
        }

        if (!successor.HasSuperleague || successor.IsComplete)
        {
            throw new InvalidOperationException(
                $"Pending season {successor.SeasonNumber} must be an incomplete Superleague season.");
        }

        bool isInaugural = current.SeasonNumber == 1 && !current.HasSuperleague;
        if (!isInaugural && !current.HasSuperleague)
        {
            throw new InvalidOperationException($"Season {current.SeasonNumber} has no Superleague and is not Season 1.");
        }

        if (isInaugural)
        {
            return await EvaluateInauguralPendingAsync(context, saveId, current, successor, rules, persistedPhase, cancellationToken).ConfigureAwait(false);
        }

        return await EvaluateNormalPendingAsync(context, saveId, current, successor, rules, persistedPhase, cancellationToken).ConfigureAwait(false);
    }

    internal static SeasonLifecycleSnapshot EvaluateSeasonCompleteNoSuccessor(
        SaveDbContext context,
        Guid saveId,
        SeasonEntity current,
        RulesV1 rules,
        string persistedPhase)
    {
        ArgumentNullException.ThrowIfNull(context);
        bool isInaugural = current.SeasonNumber == 1 && !current.HasSuperleague;
        string action = isInaugural
            ? SeasonLifecycleActions.ResolveInauguralMovement
            : SeasonLifecycleActions.ResolveAutomaticMovement;
        string expectedCup = CupExtensionPoint.ExpectedCupForSource(current.SeasonNumber);
        string detail = isInaugural
            ? $"Season 1 final tables are persisted. Next backend step creates the inaugural Superleague (8 x 4 = 32) for Season 2."
            : $"Season {current.SeasonNumber} final tables are persisted. Next backend step resolves automatic Superleague movement (16 safe + 8 champions + 8 qualifier incumbents provisional) for Season {current.SeasonNumber + 1}.";
        _ = rules;
        return new SeasonLifecycleSnapshot(
            saveId,
            current.SeasonNumber,
            persistedPhase,
            SavePhaseParser.ToText(SavePhase.SeasonComplete),
            current.SeasonNumber,
            current.SeasonNumber + 1,
            isInaugural,
            rules.StagesPerSeason + 1,
            IsCurrentSeasonComplete: true,
            SeasonComplete: true,
            MovementResolved: false,
            QualifierResolved: false,
            Rebalanced: false,
            CupSelectionResolved: false,
            CupIndividualResolved: false,
            CupTeamResolved: false,
            CupComplete: false,
            ReadyToStartNextSeason: false,
            expectedCup,
            [action],
            detail);
    }

    internal static async Task<SeasonLifecycleSnapshot> EvaluateInauguralPendingAsync(
        SaveDbContext context,
        Guid saveId,
        SeasonEntity source,
        SeasonEntity next,
        RulesV1 rules,
        string persistedPhase,
        CancellationToken cancellationToken)
    {
        int inaugural = await context.Movements.CountAsync(
            e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id && e.Kind == (int)MovementKind.InauguralPromotion,
            cancellationToken).ConfigureAwait(false);
        if (inaugural != rules.SuperleagueSize)
        {
            throw new InvalidOperationException(
                $"Season 2 exists but the inaugural Superleague is incomplete ({inaugural}/32 promotions); corrupted sporting state.");
        }

        int rebalance = await context.Movements.CountAsync(
            e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id
                && (e.Kind == (int)MovementKind.RebalanceDraw || e.Kind == (int)MovementKind.RebalanceDisplacement),
            cancellationToken).ConfigureAwait(false);
        string expectedCup = CupExtensionPoint.ExpectedCupForSource(source.SeasonNumber);

        if (rebalance == 0)
        {
            return new SeasonLifecycleSnapshot(
                saveId,
                source.SeasonNumber,
                persistedPhase,
                SavePhaseParser.ToText(SavePhase.InauguralMovementResolved),
                source.SeasonNumber,
                next.SeasonNumber,
                true,
                rules.StagesPerSeason + 1,
                IsCurrentSeasonComplete: true,
                SeasonComplete: true,
                MovementResolved: true,
                QualifierResolved: false,
                Rebalanced: false,
                CupSelectionResolved: false,
                CupIndividualResolved: false,
                CupTeamResolved: false,
                CupComplete: false,
                ReadyToStartNextSeason: false,
                expectedCup,
                [SeasonLifecycleActions.RebalanceFeeders],
                "Inaugural Superleague (32) is persisted with feeders at 28 each. Next backend step rebalances every feeder to 32 from its color pool.");
        }

        await EnsureRebalancedRosterAsync(context, next, rules, cancellationToken).ConfigureAwait(false);
        CupExtensionPoint.CupState cup = await CupExtensionPoint
            .LoadCupStateAsync(context, source, expectedCup, cancellationToken)
            .ConfigureAwait(false);
        return SnapshotPostRebalance(saveId, source, next, rules, persistedPhase, expectedCup, isInaugural: true, cup);
    }

    internal static async Task<SeasonLifecycleSnapshot> EvaluateNormalPendingAsync(
        SaveDbContext context,
        Guid saveId,
        SeasonEntity source,
        SeasonEntity next,
        RulesV1 rules,
        string persistedPhase,
        CancellationToken cancellationToken)
    {
        await EnsureAutomaticMovementCompleteAsync(context, source, next, rules, cancellationToken).ConfigureAwait(false);
        (int qualifierStandings, int qualifierRounds) = await LoadQualifierCountsAsync(context, source, next, cancellationToken).ConfigureAwait(false);
        string expectedCup = CupExtensionPoint.ExpectedCupForSource(source.SeasonNumber);

        if (qualifierStandings != rules.QualifierSize || qualifierRounds != rules.QualifierRounds)
        {
            return SnapshotAutomaticResolved(saveId, source, next, rules, persistedPhase, expectedCup);
        }

        int rebalance = await LoadRebalanceCountAsync(context, source, next, cancellationToken).ConfigureAwait(false);
        if (rebalance == 0)
        {
            return SnapshotQualifierResolved(saveId, source, next, rules, persistedPhase, expectedCup);
        }

        await EnsureRebalancedRosterAsync(context, next, rules, cancellationToken).ConfigureAwait(false);
        CupExtensionPoint.CupState cup = await CupExtensionPoint
            .LoadCupStateAsync(context, source, expectedCup, cancellationToken)
            .ConfigureAwait(false);
        return SnapshotPostRebalance(saveId, source, next, rules, persistedPhase, expectedCup, isInaugural: false, cup);
    }

    internal sealed record MovementCounts(int Promotions, int Relegations, int Incumbents, int Challengers);

    internal static async Task EnsureAutomaticMovementCompleteAsync(
        SaveDbContext context,
        SeasonEntity source,
        SeasonEntity next,
        RulesV1 rules,
        CancellationToken cancellationToken)
    {
        MovementCounts counts = await LoadMovementCountsAsync(context, source, next, cancellationToken).ConfigureAwait(false);
        if (counts.Promotions != rules.FeederAutoPromotedCount
            || counts.Relegations != rules.SuperleagueRelegatedCount
            || counts.Incumbents != rules.SuperleagueQualifierIncumbentCount
            || counts.Challengers != rules.FeederQualifierCount)
        {
            throw new InvalidOperationException(
                $"Season {next.SeasonNumber} exists but automatic movement is incomplete ({counts.Promotions}/8 promotions, {counts.Relegations}/8 relegations, {counts.Incumbents}/8 incumbents, {counts.Challengers}/24 challengers); corrupted sporting state.");
        }
    }

    internal static async Task<MovementCounts> LoadMovementCountsAsync(
        SaveDbContext context,
        SeasonEntity source,
        SeasonEntity next,
        CancellationToken cancellationToken)
    {
        int promotions = await context.Movements.CountAsync(
            e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id && e.Kind == (int)MovementKind.AutomaticPromotion,
            cancellationToken).ConfigureAwait(false);
        int relegations = await context.Movements.CountAsync(
            e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id && e.Kind == (int)MovementKind.AutomaticRelegation,
            cancellationToken).ConfigureAwait(false);
        int incumbents = await context.Movements.CountAsync(
            e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id && e.Kind == (int)MovementKind.QualifierIncumbent,
            cancellationToken).ConfigureAwait(false);
        int challengers = await context.Movements.CountAsync(
            e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id && e.Kind == (int)MovementKind.QualifierChallenger,
            cancellationToken).ConfigureAwait(false);
        return new MovementCounts(promotions, relegations, incumbents, challengers);
    }

    internal static async Task<(int Standings, int Rounds)> LoadQualifierCountsAsync(
        SaveDbContext context,
        SeasonEntity source,
        SeasonEntity next,
        CancellationToken cancellationToken)
    {
        int standings = await context.QualifierStandings.CountAsync(
            e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id, cancellationToken).ConfigureAwait(false);
        int rounds = await context.QualifierRounds.CountAsync(
            e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id, cancellationToken).ConfigureAwait(false);
        return (standings, rounds);
    }

    internal static async Task<int> LoadRebalanceCountAsync(
        SaveDbContext context,
        SeasonEntity source,
        SeasonEntity next,
        CancellationToken cancellationToken)
    {
        return await context.Movements.CountAsync(
            e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id
                && (e.Kind == (int)MovementKind.RebalanceDraw || e.Kind == (int)MovementKind.RebalanceDisplacement),
            cancellationToken).ConfigureAwait(false);
    }

    internal static SeasonLifecycleSnapshot SnapshotAutomaticResolved(
        Guid saveId,
        SeasonEntity source,
        SeasonEntity next,
        RulesV1 rules,
        string persistedPhase,
        string expectedCup)
    {
        return new SeasonLifecycleSnapshot(
            saveId,
            source.SeasonNumber,
            persistedPhase,
            SavePhaseParser.ToText(SavePhase.AutomaticMovementResolved),
            source.SeasonNumber,
            next.SeasonNumber,
            false,
            rules.StagesPerSeason + 1,
            IsCurrentSeasonComplete: true,
            SeasonComplete: true,
            MovementResolved: true,
            QualifierResolved: false,
            Rebalanced: false,
            CupSelectionResolved: false,
            CupIndividualResolved: false,
            CupTeamResolved: false,
            CupComplete: false,
            ReadyToStartNextSeason: false,
            expectedCup,
            [SeasonLifecycleActions.RunQualifier],
            "Automatic movement (16 safe + 8 champions + 8 incumbents provisional) is persisted. Next backend step runs the 32-athlete qualifier (8 winners) for Season " + next.SeasonNumber + ".");
    }

    internal static SeasonLifecycleSnapshot SnapshotQualifierResolved(
        Guid saveId,
        SeasonEntity source,
        SeasonEntity next,
        RulesV1 rules,
        string persistedPhase,
        string expectedCup)
    {
        return new SeasonLifecycleSnapshot(
            saveId,
            source.SeasonNumber,
            persistedPhase,
            SavePhaseParser.ToText(SavePhase.QualifierResolved),
            source.SeasonNumber,
            next.SeasonNumber,
            false,
            rules.StagesPerSeason + 1,
            IsCurrentSeasonComplete: true,
            SeasonComplete: true,
            MovementResolved: true,
            QualifierResolved: true,
            Rebalanced: false,
            CupSelectionResolved: false,
            CupIndividualResolved: false,
            CupTeamResolved: false,
            CupComplete: false,
            ReadyToStartNextSeason: false,
            expectedCup,
            [SeasonLifecycleActions.RebalanceFeeders],
            "Qualifier winners (8) are applied to the next Superleague (16 safe + 8 champions + 8 winners). Next backend step rebalances every feeder to 32 from its color pool.");
    }

    internal static SeasonLifecycleSnapshot SnapshotPostRebalance(
        Guid saveId,
        SeasonEntity source,
        SeasonEntity next,
        RulesV1 rules,
        string persistedPhase,
        string expectedCup,
        bool isInaugural,
        CupExtensionPoint.CupState cup)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(cup);
        ArgumentException.ThrowIfNullOrWhiteSpace(persistedPhase);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedCup);

        bool isColor = string.Equals(expectedCup, CupExtensionPoint.ColorCup, StringComparison.Ordinal);
        if (!cup.SelectionResolved)
        {
            return SnapshotCupSelectionPending(saveId, source, next, rules, persistedPhase, expectedCup, isInaugural, isColor);
        }

        if (isColor && !cup.IndividualResolved)
        {
            return SnapshotColorIndividualPending(saveId, source, next, rules, persistedPhase, expectedCup, isInaugural);
        }

        if (!cup.TeamResolved)
        {
            return SnapshotTeamPending(saveId, source, next, rules, persistedPhase, expectedCup, isInaugural, isColor);
        }

        return SnapshotCupComplete(saveId, source, next, rules, persistedPhase, expectedCup, isInaugural, cup);
    }

    internal static SeasonLifecycleSnapshot SnapshotCupSelectionPending(
        Guid saveId,
        SeasonEntity source,
        SeasonEntity next,
        RulesV1 rules,
        string persistedPhase,
        string expectedCup,
        bool isInaugural,
        bool isColor)
    {
        string action = isColor ? SeasonLifecycleActions.SelectColorCup : SeasonLifecycleActions.SelectTypeCup;
        string detail = isColor
            ? $"Feeders rebalanced to 32 each with a valid 32-athlete Superleague. Next backend step selects the Color Cup field for Season {source.SeasonNumber} (8 colors x 4, 35/30/25/10 formula) before any Cup event."
            : $"Feeders rebalanced to 32 each with a valid 32-athlete Superleague. Next backend step allocates Type Cup teams for Season {source.SeasonNumber} (four-athlete creature-type teams) before the team event.";
        return new SeasonLifecycleSnapshot(
            saveId,
            source.SeasonNumber,
            persistedPhase,
            SavePhaseParser.ToText(SavePhase.Rebalanced),
            source.SeasonNumber,
            next.SeasonNumber,
            isInaugural,
            rules.StagesPerSeason + 1,
            IsCurrentSeasonComplete: true,
            SeasonComplete: true,
            MovementResolved: true,
            QualifierResolved: !isInaugural,
            Rebalanced: true,
            CupSelectionResolved: false,
            CupIndividualResolved: false,
            CupTeamResolved: false,
            CupComplete: false,
            ReadyToStartNextSeason: false,
            expectedCup,
            [action],
            detail);
    }

    internal static SeasonLifecycleSnapshot SnapshotColorIndividualPending(
        Guid saveId,
        SeasonEntity source,
        SeasonEntity next,
        RulesV1 rules,
        string persistedPhase,
        string expectedCup,
        bool isInaugural)
    {
        return new SeasonLifecycleSnapshot(
            saveId,
            source.SeasonNumber,
            persistedPhase,
            SavePhaseParser.ToText(SavePhase.CupSelectionResolved),
            source.SeasonNumber,
            next.SeasonNumber,
            isInaugural,
            rules.StagesPerSeason + 1,
            IsCurrentSeasonComplete: true,
            SeasonComplete: true,
            MovementResolved: true,
            QualifierResolved: !isInaugural,
            Rebalanced: true,
            CupSelectionResolved: true,
            CupIndividualResolved: false,
            CupTeamResolved: false,
            CupComplete: false,
            ReadyToStartNextSeason: false,
            expectedCup,
            [SeasonLifecycleActions.RunColorCupIndividual],
            $"Color Cup field for Season {source.SeasonNumber} is selected (32 athletes). Next backend step runs the 16-round individual event (Gold/Silver/Bronze plus official title) before the team event.");
    }

    internal static SeasonLifecycleSnapshot SnapshotTeamPending(
        Guid saveId,
        SeasonEntity source,
        SeasonEntity next,
        RulesV1 rules,
        string persistedPhase,
        string expectedCup,
        bool isInaugural,
        bool isColor)
    {
        if (isColor)
        {
            return SnapshotColorTeamPending(saveId, source, next, rules, persistedPhase, expectedCup, isInaugural);
        }

        return SnapshotTypeTeamPending(saveId, source, next, rules, persistedPhase, expectedCup, isInaugural);
    }

    internal static SeasonLifecycleSnapshot SnapshotColorTeamPending(
        Guid saveId,
        SeasonEntity source,
        SeasonEntity next,
        RulesV1 rules,
        string persistedPhase,
        string expectedCup,
        bool isInaugural)
    {
        return new SeasonLifecycleSnapshot(
            saveId,
            source.SeasonNumber,
            persistedPhase,
            SavePhaseParser.ToText(SavePhase.CupIndividualResolved),
            source.SeasonNumber,
            next.SeasonNumber,
            isInaugural,
            rules.StagesPerSeason + 1,
            IsCurrentSeasonComplete: true,
            SeasonComplete: true,
            MovementResolved: true,
            QualifierResolved: !isInaugural,
            Rebalanced: true,
            CupSelectionResolved: true,
            CupIndividualResolved: true,
            CupTeamResolved: false,
            CupComplete: false,
            ReadyToStartNextSeason: false,
            expectedCup,
            [SeasonLifecycleActions.RunColorCupTeam],
            $"Color Cup individual for Season {source.SeasonNumber} is complete and inspectable. Next backend step runs the four 8-round rank groups for the team championship.");
    }

    internal static SeasonLifecycleSnapshot SnapshotTypeTeamPending(
        Guid saveId,
        SeasonEntity source,
        SeasonEntity next,
        RulesV1 rules,
        string persistedPhase,
        string expectedCup,
        bool isInaugural)
    {
        return new SeasonLifecycleSnapshot(
            saveId,
            source.SeasonNumber,
            persistedPhase,
            SavePhaseParser.ToText(SavePhase.CupSelectionResolved),
            source.SeasonNumber,
            next.SeasonNumber,
            isInaugural,
            rules.StagesPerSeason + 1,
            IsCurrentSeasonComplete: true,
            SeasonComplete: true,
            MovementResolved: true,
            QualifierResolved: !isInaugural,
            Rebalanced: true,
            CupSelectionResolved: true,
            CupIndividualResolved: false,
            CupTeamResolved: false,
            CupComplete: false,
            ReadyToStartNextSeason: false,
            expectedCup,
            [SeasonLifecycleActions.RunTypeCupTeam],
            $"Type Cup field for Season {source.SeasonNumber} is allocated. Next backend step runs the four 8-round rank groups for the team championship.");
    }

    internal static SeasonLifecycleSnapshot SnapshotCupComplete(
        Guid saveId,
        SeasonEntity source,
        SeasonEntity next,
        RulesV1 rules,
        string persistedPhase,
        string expectedCup,
        bool isInaugural,
        CupExtensionPoint.CupState cup)
    {
        return new SeasonLifecycleSnapshot(
            saveId,
            source.SeasonNumber,
            persistedPhase,
            SavePhaseParser.ToText(SavePhase.CupComplete),
            source.SeasonNumber,
            next.SeasonNumber,
            isInaugural,
            rules.StagesPerSeason + 1,
            IsCurrentSeasonComplete: true,
            SeasonComplete: true,
            MovementResolved: true,
            QualifierResolved: !isInaugural,
            Rebalanced: true,
            CupSelectionResolved: true,
            CupIndividualResolved: cup.IndividualResolved,
            CupTeamResolved: true,
            CupComplete: true,
            ReadyToStartNextSeason: true,
            expectedCup,
            [SeasonLifecycleActions.StartNextSeason],
            $"Post-season {expectedCup} for Season {source.SeasonNumber} is complete and inspectable (field plus all events). Next backend step finalizes bonus aging and starts Season {next.SeasonNumber}.");
    }

    internal static async Task EnsureRebalancedRosterAsync(
        SaveDbContext context, SeasonEntity next, RulesV1 rules, CancellationToken cancellationToken)
    {
        List<LeagueEntity> feeders = await context.Leagues
            .AsNoTracking()
            .Where(e => e.SeasonId == next.Id && e.Kind == (int)LeagueKind.Feeder)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (feeders.Count != rules.RegularLeagueCount)
        {
            throw new InvalidOperationException(
                $"Season {next.SeasonNumber} must have exactly {rules.RegularLeagueCount} feeder leagues, was {feeders.Count}.");
        }

        LeagueEntity? superleague = await context.Leagues
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.SeasonId == next.Id && e.Kind == (int)LeagueKind.Superleague, cancellationToken)
            .ConfigureAwait(false);
        if (superleague is null)
        {
            throw new InvalidOperationException($"Season {next.SeasonNumber} has no Superleague.");
        }

        foreach (LeagueEntity feeder in feeders)
        {
            int count = await context.SeasonMemberships
                .CountAsync(e => e.SeasonId == next.Id && e.LeagueId == feeder.Id, cancellationToken)
                .ConfigureAwait(false);
            if (count != rules.LeagueSize)
            {
                throw new InvalidOperationException(
                    $"League '{feeder.Name}' must contain exactly {rules.LeagueSize} athletes after rebalancing, was {count}.");
            }
        }

        int superCount = await context.SeasonMemberships
            .CountAsync(e => e.SeasonId == next.Id && e.LeagueId == superleague.Id, cancellationToken)
            .ConfigureAwait(false);
        if (superCount != rules.SuperleagueSize)
        {
            throw new InvalidOperationException(
                $"Next Superleague must contain exactly {rules.SuperleagueSize} athletes, was {superCount}.");
        }
    }
}
