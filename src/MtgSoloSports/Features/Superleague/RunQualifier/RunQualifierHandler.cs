using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Qualifiers.Engine;
using MtgSoloSports.Features.Seasons.SeasonLifecycle;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.Shared;
using MtgSoloSports.SimulationKernel.Catalog;
using MtgSoloSports.SimulationKernel.Leagues;
using MtgSoloSports.SimulationKernel.Random;
using MtgSoloSports.SimulationKernel.Rules;
using MtgSoloSports.SimulationKernel.Stages;

namespace MtgSoloSports.Features.Superleague.RunQualifier;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Runs the 32-athlete
/// Superleague qualifier for a completed Superleague season after automatic
/// movement: 8 incumbents (Superleague ranks 17-24) plus 24 challengers
/// (feeder ranks 2-4 across all eight leagues) compete in one standard
/// 16-round stage. Active career bonus at the next-season Stage 1 boundary
/// applies with normal fixed-point scoring/ranking; incumbents and challengers
/// are treated identically (role is provenance only). No new round or stage
/// career bonus is generated and no league <c>StageStanding</c>,
/// <c>SeasonStanding</c> or <c>Round</c> rows are created, so normal league
/// season championship totals are untouched. Persists 16 immutable qualifier
/// round payloads plus 32 qualifier standings (exactly 8 qualified), applies
/// the eight winners to the next-season roster (winners to the next
/// Superleague, the 24 losers to their returning-color feeders, so the final
/// next Superleague is 16 safe + 8 promoted + 8 qualifier winners) and the
/// RNG-after state in one transaction. Holds one per-save lock; read-only
/// qualifier queries never lock. No Superleague color quota.
/// Simulation, ranking, checksums, payload persistence and the completion
/// spine live in the shared <see cref="QualifierEngine"/> (MSS-150); this
/// handler owns the Superleague path's guards, field selection, roster
/// destination rules, persisted validation and first-appearance stories.
/// </summary>
public sealed class RunQualifierHandler
{
    private readonly SaveStore _store;

    public RunQualifierHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<RunQualifierResponse> HandleAsync(Guid saveId, CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        SemaphoreSlim gate = AdvanceRoundSaveLock.Acquire(saveId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RunUnderLockAsync(saveId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task<RunQualifierResponse> RunUnderLockAsync(
        Guid saveId,
        CancellationToken cancellationToken)
    {
        await _store.EnsureMigratedAsync(saveId, cancellationToken).ConfigureAwait(false);
        using SaveDbContext context = _store.OpenDbContext(saveId);
        using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        SuperleagueQualifierLoad load = await LoadStateAsync(context, saveId, cancellationToken).ConfigureAwait(false);
        QualifierEngineState state = load.Engine;
        QualifierEngineHooks hooks = CreateHooks(load);
        List<QualifierRoundPayload> payloads = new(state.Played);
        Pcg32State current = state.Rng;
        while (payloads.Count < state.TotalRounds)
        {
            QualifierRoundPayload payload = QualifierEngine.PlayRound(state, payloads, current, hooks.ValidateRound);
            payloads.Add(payload);
            current = new Pcg32State(payload.RngAfterState, payload.RngAfterStream);
        }

        QualifierEngineSimulation simulation = await QualifierEngine.CompleteAsync(
            context, state, payloads, hooks, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await BuildResponseAsync(_store, saveId, state.Source, state.Next, simulation, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Loads everything a Superleague qualifier round needs from persisted
    /// state and validates any partly played qualifier (contiguous rounds,
    /// unbroken RNG chain, RNG row untouched since the last round). Aborts on
    /// corruption.
    /// </summary>
    internal static async Task<SuperleagueQualifierLoad> LoadStateAsync(
        SaveDbContext context,
        Guid saveId,
        CancellationToken cancellationToken)
    {
        RulesV1 rules = await AdvanceRoundHandler.LoadRulesAsync(context, cancellationToken).ConfigureAwait(false);
        SaveMetadataEntity metadata = await AdvanceRoundHandler.LoadMetadataAsync(context, saveId, cancellationToken).ConfigureAwait(false);
        RngStateEntity rngRow = await AdvanceRoundHandler.LoadRngAsync(context, cancellationToken).ConfigureAwait(false);

        (SeasonEntity source, SeasonEntity next) = await LoadPendingTransitionAsync(context, cancellationToken).ConfigureAwait(false);
        await EnsureAutomaticMovementResolvedAsync(context, source, next, cancellationToken).ConfigureAwait(false);
        await QualifierEngine.EnsureChaosCompleteAsync(
            context, source, rules, static message => new RunQualifierConflictException(message), cancellationToken).ConfigureAwait(false);
        await EnsureQualifierUnresolvedAsync(context, source, next, cancellationToken).ConfigureAwait(false);

        List<LeagueEntity> sourceFeeders = await LoadSourceFeedersAsync(context, source, rules, cancellationToken).ConfigureAwait(false);
        LeagueEntity sourceSuperleague = await LoadSourceSuperleagueAsync(context, source, cancellationToken).ConfigureAwait(false);
        List<SeasonStandingEntity> superStandings = await QualifierEngine.LoadLeagueStandingsAsync(
            context, source, sourceSuperleague, rules, requireCompleteRanks: true, cancellationToken).ConfigureAwait(false);
        Dictionary<int, IReadOnlyList<SeasonStandingEntity>> feederStandings = await LoadFeederStandingsAsync(
            context, source, sourceFeeders, rules, cancellationToken).ConfigureAwait(false);
        List<SeasonMembershipEntity> sourceMemberships = await QualifierEngine.LoadSourceMembershipsAsync(
            context, source, rules, cancellationToken).ConfigureAwait(false);
        Dictionary<int, SeasonMembershipEntity> membershipByAthlete = sourceMemberships.ToDictionary(m => m.SaveAthleteId);
        Dictionary<int, string> namesByAthlete = await QualifierEngine.LoadAthleteNamesAsync(context, cancellationToken).ConfigureAwait(false);

        QualifierFieldSelection.QualifierField field = QualifierFieldSelection.Select(
            superStandings, feederStandings, sourceFeeders, sourceSuperleague, membershipByAthlete, namesByAthlete, rules);
        QualifierInvariants.ValidateField(field, superStandings, feederStandings, sourceFeeders, sourceSuperleague, rules);

        (int stageCountBefore, int seasonCountBefore, int roundCountBefore, _, _) =
            await CapturePreservationAsync(context, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<QualifierProvenance> provenance = ToProvenance(field);
        List<AdvanceRoundHandler.MemberRow> roster = QualifierEngine.BuildRoster(provenance);
        Dictionary<int, MtgSoloSports.SimulationKernel.FixedPoint.Bonus> activeBonuses = await QualifierEngine.LoadStageStartBonusesAsync(
            context, roster, next, rules, cancellationToken).ConfigureAwait(false);

        List<QualifierRoundPayload> played = await LoadPlayedRoundsAsync(context, source, next, cancellationToken).ConfigureAwait(false);
        Pcg32State rng = rngRow.ToState();
        ValidateInProgress(played, rules, rng);

        return new SuperleagueQualifierLoad(new QualifierEngineState(
            rules, metadata, source, next, QualifierBoundary.Superleague, QualifierIdentity.SuperleagueColorSentinel,
            rules.QualifierSize, rules.QualifierRounds, rules.QualifierWinners, LeagueLevel.Feeder1,
            provenance, roster, activeBonuses, played, rng),
            stageCountBefore, seasonCountBefore, roundCountBefore);
    }

    /// <summary>
    /// The Superleague path's completion behavior (MSS-150): full per-round
    /// and completed-qualifier invariants, winners to the next Superleague
    /// and losers to their returning-color feeders, persisted-state
    /// validation including league-history preservation, plus
    /// first-Superleague-appearance stories.
    /// </summary>
    internal static QualifierEngineHooks CreateHooks(SuperleagueQualifierLoad load)
    {
        int stageCountBefore = load.StageCountBefore;
        int seasonCountBefore = load.SeasonCountBefore;
        int roundCountBefore = load.RoundCountBefore;
        return new QualifierEngineHooks(
            static (payload, state, rngBefore) =>
                QualifierInvariants.ValidateRound(payload, state.Rules, rngBefore.State, rngBefore.Stream),
            static (ranked, totals, rules) => QualifierInvariants.ValidateCompletedQualifier(ranked, totals, rules),
            static (context, state, simulation, cancellationToken) =>
                ApplyQualifierToNextRosterAsync(context, state.Next, simulation, state.Rules, cancellationToken),
            (context, state, simulation, cancellationToken) => ValidatePersistedAsync(
                context, state.Source, state.Next, simulation, state.Rules,
                stageCountBefore, seasonCountBefore, roundCountBefore, cancellationToken),
            static (context, state, simulation, leaguesById, nextByAthlete, cancellationToken) =>
                EmitQualifierFirstAppearancesAsync(
                    context, state.Source, state.Next, simulation, state.Rules, leaguesById, nextByAthlete, cancellationToken));
    }

    internal static IReadOnlyList<QualifierProvenance> ToProvenance(QualifierFieldSelection.QualifierField field)
    {
        List<QualifierProvenance> provenance = new(field.All.Count);
        foreach (QualifierFieldSelection.QualifierPick pick in field.All)
        {
            provenance.Add(new QualifierProvenance(
                pick.SaveAthleteId, pick.Name, pick.SportingColor, pick.FromLeagueId, pick.FromSeasonRank, pick.Role));
        }

        return provenance;
    }

    internal sealed record SuperleagueQualifierLoad(
        QualifierEngineState Engine,
        int StageCountBefore,
        int SeasonCountBefore,
        int RoundCountBefore);

    internal static async Task<List<QualifierRoundPayload>> LoadPlayedRoundsAsync(
        SaveDbContext context, SeasonEntity source, SeasonEntity next, CancellationToken cancellationToken)
    {
        List<QualifierRoundEntity> rows = await context.QualifierRounds
            .AsNoTracking()
            .Where(e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id
                && e.QualifierBoundary == (int)QualifierBoundary.Superleague)
            .OrderBy(e => e.RoundNumber)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Select(r => QualifierRoundPayload.FromJson(r.PayloadJson)).ToList();
    }

    internal static void ValidateInProgress(List<QualifierRoundPayload> played, RulesV1 rules, Pcg32State rngRow)
    {
        foreach (QualifierRoundPayload payload in played)
        {
            QualifierInvariants.ValidateRound(payload, rules, payload.RngBeforeState, payload.RngBeforeStream);
        }

        List<PostseasonEvents.PlayedRoundLink> links = played
            .Select(p => new PostseasonEvents.PlayedRoundLink(
                1,
                p.RoundNumber,
                new Pcg32State(p.RngBeforeState, p.RngBeforeStream),
                new Pcg32State(p.RngAfterState, p.RngAfterStream)))
            .ToList();
        PostseasonEvents.ValidateInProgress(
            PostseasonEvents.Qualifier,
            links,
            PostseasonEvents.Shape(PostseasonEvents.Qualifier, rules),
            _ => throw new SaveCorruptionException("The qualifier has a single stage."),
            rngRow);
    }

    /// <summary>
    /// Emits first-Superleague-appearance story events transactionally with
    /// the qualifier result for every qualifier winner who has never appeared
    /// in a Superleague before. Idempotent via dedup keys.
    /// </summary>
    internal static async Task<bool> EmitQualifierFirstAppearancesAsync(
        SaveDbContext context,
        SeasonEntity source,
        SeasonEntity next,
        QualifierEngineSimulation simulation,
        RulesV1 rules,
        Dictionary<int, LeagueEntity> leaguesById,
        Dictionary<int, SeasonMembershipEntity> nextByAthlete,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(simulation);
        ArgumentNullException.ThrowIfNull(rules);
        bool emitted = false;
        foreach (StageRankedAthlete entry in simulation.Ranked.Where(r => r.StageRank <= rules.QualifierWinners).OrderBy(r => r.AthleteId))
        {
            bool hadPrior = await Features.Stories.StoryEventEmitter.HasPriorSuperleagueAppearanceAsync(
                context, entry.AthleteId, next.Id, cancellationToken).ConfigureAwait(false);
            if (hadPrior)
            {
                continue;
            }

            nextByAthlete.TryGetValue(entry.AthleteId, out SeasonMembershipEntity? nextMembership);
            string toName = ResolveQualifierLeagueName(nextMembership, leaguesById, "Superleague");
            emitted |= await Features.Stories.StoryEventEmitter.TryEmitAsync(
                context,
                entry.AthleteId,
                Features.Stories.StoryEventType.FirstSuperleagueAppearance,
                Features.Stories.StoryEventEmitter.FirstDedup,
                next.SeasonNumber,
                null,
                new Features.Stories.StoryEventPayload(
                    entry.Name,
                    next.SeasonNumber,
                    ToLeagueName: toName,
                    FromSeasonNumber: source.SeasonNumber,
                    ToSeasonNumber: next.SeasonNumber),
                cancellationToken).ConfigureAwait(false);
        }

        return emitted;
    }

    internal static string ResolveQualifierLeagueName(
        SeasonMembershipEntity? membership,
        Dictionary<int, LeagueEntity> leaguesById,
        string fallback)
    {
        if (membership?.LeagueId is not null && leaguesById.TryGetValue(membership.LeagueId.Value, out LeagueEntity? league))
        {
            return league.Name;
        }

        return fallback;
    }

    /// <summary>
    /// Applies the eight qualifier winners to the next-season roster inside the
    /// caller's transaction (no save here). Winners move to (or remain in) the
    /// next Superleague; the 24 losers move to (or remain in) their
    /// returning-color feeder. Automatic-movement <c>Movement</c> rows are
    /// provisional markers and are never rewritten; the final occupancy is
    /// determined by these membership rows plus the qualifier standings.
    /// Sporting color and pool membership are preserved; only the 32 qualifier
    /// participants can change leagues.
    /// </summary>
    internal static async Task ApplyQualifierToNextRosterAsync(
        SaveDbContext context,
        SeasonEntity next,
        QualifierEngineSimulation simulation,
        RulesV1 rules,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(simulation);
        ArgumentNullException.ThrowIfNull(rules);

        (LeagueEntity superleague, Dictionary<int, int> feederByColor) =
            await LoadNextLeagueTargetsAsync(context, next, rules, cancellationToken).ConfigureAwait(false);
        HashSet<int> fieldIds = ResolveFieldIds(simulation, rules);
        HashSet<int> qualifiedIds = ResolveQualifiedIds(simulation, fieldIds, rules);
        List<SeasonMembershipEntity> nextMemberships = await LoadNextQualifierMembershipsAsync(
            context, next, fieldIds, rules, cancellationToken).ConfigureAwait(false);
        AssignQualifierLeagues(nextMemberships, qualifiedIds, superleague.Id, feederByColor);
    }

    internal static async Task<(LeagueEntity Superleague, Dictionary<int, int> FeederByColor)> LoadNextLeagueTargetsAsync(
        SaveDbContext context,
        SeasonEntity next,
        RulesV1 rules,
        CancellationToken cancellationToken)
    {
        LeagueEntity superleague = await context.Leagues.AsNoTracking().SingleOrDefaultAsync(
            e => e.SeasonId == next.Id && e.Kind == (int)LeagueKind.Superleague, cancellationToken).ConfigureAwait(false)
            ?? throw new SaveCorruptionException($"Season {next.SeasonNumber} has no next Superleague.");
        List<LeagueEntity> feeders = await context.Leagues.AsNoTracking()
            .Where(e => e.SeasonId == next.Id && e.Kind == (int)LeagueKind.Feeder)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (feeders.Count != rules.TieredFeederLeagueCount)
        {
            throw new SaveCorruptionException(
                $"Season {next.SeasonNumber} must have exactly {rules.TieredFeederLeagueCount} feeder leagues, was {feeders.Count}.");
        }

        // Tiered saves carry 24 feeders; qualifier mapping only needs F1.
        // F2/F3 never enter the qualifier, so resolve via F1-only map.
        List<LeagueEntity> f1 = feeders
            .Where(l => rules.FeederDivisionsPerColor != 3 || l.FeederDivision == (int)FeederDivision.First)
            .ToList();
        if (f1.Count != rules.RegularLeagueCount)
        {
            throw new SaveCorruptionException(
                $"Season {next.SeasonNumber} must have exactly {rules.RegularLeagueCount} F1 leagues, was {f1.Count}.");
        }

        return (superleague, f1.ToDictionary(l => l.SportingColor, l => l.Id));
    }

    internal static HashSet<int> ResolveFieldIds(QualifierEngineSimulation simulation, RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(simulation);
        ArgumentNullException.ThrowIfNull(rules);
        HashSet<int> fieldIds = simulation.Field.Select(p => p.SaveAthleteId).ToHashSet();
        if (fieldIds.Count != rules.QualifierSize)
        {
            throw new SaveCorruptionException(
                $"Qualifier field must hold exactly {rules.QualifierSize} athletes, was {fieldIds.Count}.");
        }

        return fieldIds;
    }

    internal static HashSet<int> ResolveQualifiedIds(
        QualifierEngineSimulation simulation,
        HashSet<int> fieldIds,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(simulation);
        ArgumentNullException.ThrowIfNull(fieldIds);
        ArgumentNullException.ThrowIfNull(rules);
        HashSet<int> qualifiedIds = simulation.Ranked
            .Where(r => r.StageRank <= rules.QualifierWinners)
            .Select(r => r.AthleteId)
            .ToHashSet();
        if (qualifiedIds.Count != rules.QualifierWinners)
        {
            throw new SaveCorruptionException(
                $"Qualifier must hold exactly {rules.QualifierWinners} successful qualifiers, was {qualifiedIds.Count}.");
        }

        foreach (int athleteId in qualifiedIds)
        {
            if (!fieldIds.Contains(athleteId))
            {
                throw new SaveCorruptionException($"Qualifier winner {athleteId} is outside the qualifier field.");
            }
        }

        return qualifiedIds;
    }

    internal static async Task<List<SeasonMembershipEntity>> LoadNextQualifierMembershipsAsync(
        SaveDbContext context,
        SeasonEntity next,
        HashSet<int> fieldIds,
        RulesV1 rules,
        CancellationToken cancellationToken)
    {
        List<SeasonMembershipEntity> memberships = await context.SeasonMemberships
            .Where(e => e.SeasonId == next.Id && fieldIds.Contains(e.SaveAthleteId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (memberships.Count != rules.QualifierSize)
        {
            throw new SaveCorruptionException(
                $"Next season must hold exactly {rules.QualifierSize} qualifier memberships, was {memberships.Count}.");
        }

        return memberships;
    }

    internal static void AssignQualifierLeagues(
        List<SeasonMembershipEntity> memberships,
        HashSet<int> qualifiedIds,
        int superleagueId,
        Dictionary<int, int> feederByColor)
    {
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(qualifiedIds);
        ArgumentNullException.ThrowIfNull(feederByColor);
        foreach (SeasonMembershipEntity membership in memberships)
        {
            AssignSingleQualifierLeague(membership, qualifiedIds, superleagueId, feederByColor);
        }
    }

    internal static void AssignSingleQualifierLeague(
        SeasonMembershipEntity membership,
        HashSet<int> qualifiedIds,
        int superleagueId,
        Dictionary<int, int> feederByColor)
    {
        ArgumentNullException.ThrowIfNull(membership);
        ArgumentNullException.ThrowIfNull(qualifiedIds);
        ArgumentNullException.ThrowIfNull(feederByColor);
        if (membership.LeagueId is null)
        {
            throw new SaveCorruptionException(
                $"Qualifier athlete {membership.SaveAthleteId} has no provisional next-season league.");
        }

        if (qualifiedIds.Contains(membership.SaveAthleteId))
        {
            membership.LeagueId = superleagueId;
            return;
        }

        if (!feederByColor.TryGetValue(membership.SportingColor, out int feederId))
        {
            throw new SaveCorruptionException($"Unknown sporting color {membership.SportingColor}.");
        }

        membership.LeagueId = feederId;
    }

    internal static async Task ValidatePersistedAsync(
        SaveDbContext context,
        SeasonEntity source,
        SeasonEntity next,
        QualifierEngineSimulation simulation,
        RulesV1 rules,
        int stageCountBefore,
        int seasonCountBefore,
        int roundCountBefore,
        CancellationToken cancellationToken)
    {
        List<QualifierRoundEntity> rounds = await context.QualifierRounds
            .Where(e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id
                && e.QualifierBoundary == (int)QualifierBoundary.Superleague)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<QualifierStandingEntity> standings = await context.QualifierStandings
            .Where(e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id
                && e.QualifierBoundary == (int)QualifierBoundary.Superleague)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        QualifierInvariants.ValidatePersisted(source, next, rounds, standings, rules);
        await ValidateNextRosterAsync(context, next, simulation, standings, rules, cancellationToken).ConfigureAwait(false);
        await VerifyPreservationAsync(context, stageCountBefore, seasonCountBefore, roundCountBefore, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Validates that the persisted next-season roster reflects the qualifier
    /// result: the next Superleague holds exactly 32 athletes, every qualified
    /// athlete occupies the next Superleague and every failed qualifier
    /// occupies its returning-color feeder. Pool athletes and non-participants
    /// are untouched here; feeder rebalancing (MSS-017) restores 32 per feeder.
    /// </summary>
    internal static async Task ValidateNextRosterAsync(
        SaveDbContext context,
        SeasonEntity next,
        QualifierEngineSimulation simulation,
        List<QualifierStandingEntity> standings,
        RulesV1 rules,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(simulation);
        ArgumentNullException.ThrowIfNull(standings);
        ArgumentNullException.ThrowIfNull(rules);

        (LeagueEntity superleague, Dictionary<int, int> feederByColor) =
            await LoadNextLeagueTargetsAsync(context, next, rules, cancellationToken).ConfigureAwait(false);
        Dictionary<int, QualifierStandingEntity> standingByAthlete = ResolveStandingMap(standings, rules);
        HashSet<int> fieldIds = ResolveFieldIds(simulation, rules);
        if (!fieldIds.SetEquals(standingByAthlete.Keys))
        {
            throw new SaveCorruptionException("Qualifier standings do not match the qualifier field.");
        }

        List<SeasonMembershipEntity> qualifierMemberships = await context.SeasonMemberships.AsNoTracking()
            .Where(e => e.SeasonId == next.Id && fieldIds.Contains(e.SaveAthleteId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (qualifierMemberships.Count != rules.QualifierSize)
        {
            throw new SaveCorruptionException(
                $"Next season must hold exactly {rules.QualifierSize} qualifier memberships, was {qualifierMemberships.Count}.");
        }

        CheckQualifierPlacements(qualifierMemberships, standingByAthlete, superleague.Id, feederByColor);
        await CheckFinalSuperleagueCountAsync(context, next, superleague, rules, cancellationToken).ConfigureAwait(false);
    }

    internal static Dictionary<int, QualifierStandingEntity> ResolveStandingMap(
        List<QualifierStandingEntity> standings,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(standings);
        ArgumentNullException.ThrowIfNull(rules);
        Dictionary<int, QualifierStandingEntity> map = standings.ToDictionary(s => s.SaveAthleteId);
        if (map.Count != rules.QualifierSize)
        {
            throw new SaveCorruptionException(
                $"Qualifier must hold exactly {rules.QualifierSize} standings, was {map.Count}.");
        }

        return map;
    }

    internal static void CheckQualifierPlacements(
        List<SeasonMembershipEntity> memberships,
        Dictionary<int, QualifierStandingEntity> standingByAthlete,
        int superleagueId,
        Dictionary<int, int> feederByColor)
    {
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(standingByAthlete);
        ArgumentNullException.ThrowIfNull(feederByColor);
        foreach (SeasonMembershipEntity membership in memberships)
        {
            CheckSingleQualifierPlacement(membership, standingByAthlete, superleagueId, feederByColor);
        }
    }

    internal static void CheckSingleQualifierPlacement(
        SeasonMembershipEntity membership,
        Dictionary<int, QualifierStandingEntity> standingByAthlete,
        int superleagueId,
        Dictionary<int, int> feederByColor)
    {
        ArgumentNullException.ThrowIfNull(membership);
        ArgumentNullException.ThrowIfNull(standingByAthlete);
        ArgumentNullException.ThrowIfNull(feederByColor);
        if (!standingByAthlete.TryGetValue(membership.SaveAthleteId, out QualifierStandingEntity? standing))
        {
            throw new SaveCorruptionException($"Qualifier athlete {membership.SaveAthleteId} has no standing.");
        }

        if (membership.LeagueId is null)
        {
            throw new SaveCorruptionException($"Qualifier athlete {membership.SaveAthleteId} is in the common pool.");
        }

        if (standing.IsQualified)
        {
            if (membership.LeagueId != superleagueId)
            {
                throw new SaveCorruptionException(
                    $"Qualified athlete {membership.SaveAthleteId} must occupy the next Superleague.");
            }

            return;
        }

        if (!feederByColor.TryGetValue(membership.SportingColor, out int expectedFeeder))
        {
            throw new SaveCorruptionException($"Unknown sporting color {membership.SportingColor}.");
        }

        if (membership.LeagueId != expectedFeeder)
        {
            throw new SaveCorruptionException(
                $"Failed qualifier {membership.SaveAthleteId} must occupy its returning-color feeder.");
        }
    }

    internal static async Task CheckFinalSuperleagueCountAsync(
        SaveDbContext context,
        SeasonEntity next,
        LeagueEntity superleague,
        RulesV1 rules,
        CancellationToken cancellationToken)
    {
        int superCount = await context.SeasonMemberships.CountAsync(
            e => e.SeasonId == next.Id && e.LeagueId == superleague.Id, cancellationToken).ConfigureAwait(false);
        if (superCount != rules.SuperleagueSize)
        {
            throw new SaveCorruptionException(
                $"Next Superleague must contain exactly {rules.SuperleagueSize} athletes (16 safe + 8 promoted + 8 qualifier winners), was {superCount}.");
        }
    }

    internal static async Task<(int StageCount, int SeasonCount, int RoundCount, int QualifierRounds, int QualifierStandings)> CapturePreservationAsync(
        SaveDbContext context, CancellationToken cancellationToken)
    {
        int stages = await context.StageStandings.CountAsync(cancellationToken).ConfigureAwait(false);
        int seasons = await context.SeasonStandings.CountAsync(cancellationToken).ConfigureAwait(false);
        int rounds = await context.Rounds.CountAsync(cancellationToken).ConfigureAwait(false);
        int qualifierRounds = await context.QualifierRounds.CountAsync(cancellationToken).ConfigureAwait(false);
        int qualifierStandings = await context.QualifierStandings.CountAsync(cancellationToken).ConfigureAwait(false);
        return (stages, seasons, rounds, qualifierRounds, qualifierStandings);
    }

    internal static async Task VerifyPreservationAsync(
        SaveDbContext context,
        int stageCountBefore,
        int seasonCountBefore,
        int roundCountBefore,
        CancellationToken cancellationToken)
    {
        int stages = await context.StageStandings.CountAsync(cancellationToken).ConfigureAwait(false);
        int seasons = await context.SeasonStandings.CountAsync(cancellationToken).ConfigureAwait(false);
        int rounds = await context.Rounds.CountAsync(cancellationToken).ConfigureAwait(false);
        if (stages != stageCountBefore)
        {
            throw new SaveCorruptionException("Qualifier must not create league stage standings; championship totals are preserved.");
        }

        if (seasons != seasonCountBefore)
        {
            throw new SaveCorruptionException("Qualifier must not create league season standings; championship totals are preserved.");
        }

        if (rounds != roundCountBefore)
        {
            throw new SaveCorruptionException("Qualifier must not create league rounds; qualifier history lives in qualifier tables only.");
        }
    }

    internal static async Task<(SeasonEntity Source, SeasonEntity Next)> LoadPendingTransitionAsync(
        SaveDbContext context, CancellationToken cancellationToken)
    {
        List<SeasonEntity> candidates = await context.Seasons
            .Where(e => e.IsComplete && e.HasSuperleague)
            .OrderByDescending(e => e.SeasonNumber)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            throw new RunQualifierConflictException(
                "No completed Superleague season is ready for the qualifier.");
        }

        foreach (SeasonEntity candidate in candidates)
        {
            SeasonEntity? successor = await context.Seasons.SingleOrDefaultAsync(
                e => e.SeasonNumber == candidate.SeasonNumber + 1, cancellationToken).ConfigureAwait(false);
            if (successor is null || !successor.HasSuperleague || successor.IsComplete)
            {
                continue;
            }

            bool hasQualifier = await context.QualifierStandings.AnyAsync(
                e => e.FromSeasonId == candidate.Id && e.ToSeasonId == successor.Id
                    && e.QualifierBoundary == (int)QualifierBoundary.Superleague,
                cancellationToken).ConfigureAwait(false);
            if (hasQualifier)
            {
                continue;
            }

            return (candidate, successor);
        }

        bool anyQualifier = await context.QualifierStandings.AnyAsync(
            e => e.QualifierBoundary == (int)QualifierBoundary.Superleague,
            cancellationToken).ConfigureAwait(false);
        if (anyQualifier)
        {
            throw new RunQualifierConflictException(
                "The Superleague qualifier has already been resolved for the pending transition.");
        }

        throw new RunQualifierConflictException(
            "Automatic movement must be resolved before the Superleague qualifier can run.");
    }

    internal static async Task EnsureAutomaticMovementResolvedAsync(
        SaveDbContext context, SeasonEntity source, SeasonEntity next, CancellationToken cancellationToken)
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
        if (promotions != 8 || relegations != 8 || incumbents != 8 || challengers != 24)
        {
            throw new RunQualifierConflictException(
                "Automatic movement must be resolved before the Superleague qualifier can run.");
        }
    }

    internal static async Task EnsureQualifierUnresolvedAsync(
        SaveDbContext context, SeasonEntity source, SeasonEntity next, CancellationToken cancellationToken)
    {
        bool hasStandings = await context.QualifierStandings.AnyAsync(
            e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id
                && e.QualifierBoundary == (int)QualifierBoundary.Superleague,
            cancellationToken).ConfigureAwait(false);
        if (hasStandings)
        {
            throw new RunQualifierConflictException(
                $"Qualifier for Season {source.SeasonNumber} has already been resolved.");
        }

        string? other = await PostseasonEvents.FindOtherInProgressAsync(context, PostseasonEvents.Qualifier, source.Id, cancellationToken).ConfigureAwait(false);
        if (other is not null)
        {
            throw new RunQualifierConflictException(
                $"{other} is partly played; finish it before playing the {PostseasonEvents.Title(PostseasonEvents.Qualifier)}.");
        }
    }

    internal static async Task<List<LeagueEntity>> LoadSourceFeedersAsync(
        SaveDbContext context, SeasonEntity source, RulesV1 rules, CancellationToken cancellationToken)
    {
        List<LeagueEntity> leagues = await context.Leagues
            .Where(e => e.SeasonId == source.Id && e.Kind == (int)LeagueKind.Feeder)
            .OrderBy(e => e.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (leagues.Count != rules.TieredFeederLeagueCount)
        {
            throw new SaveCorruptionException(
                $"Season {source.SeasonNumber} must have exactly {rules.TieredFeederLeagueCount} feeder leagues, was {leagues.Count}.");
        }

        return leagues;
    }

    internal static async Task<LeagueEntity> LoadSourceSuperleagueAsync(
        SaveDbContext context, SeasonEntity source, CancellationToken cancellationToken)
    {
        LeagueEntity? league = await context.Leagues.SingleOrDefaultAsync(
            e => e.SeasonId == source.Id && e.Kind == (int)LeagueKind.Superleague, cancellationToken).ConfigureAwait(false);
        if (league is null)
        {
            throw new SaveCorruptionException($"Season {source.SeasonNumber} has no Superleague.");
        }

        return league;
    }

    internal static async Task<Dictionary<int, IReadOnlyList<SeasonStandingEntity>>> LoadFeederStandingsAsync(
        SaveDbContext context,
        SeasonEntity source,
        List<LeagueEntity> feeders,
        RulesV1 rules,
        CancellationToken cancellationToken)
    {
        Dictionary<int, IReadOnlyList<SeasonStandingEntity>> map = new(feeders.Count);
        foreach (LeagueEntity feeder in feeders)
        {
            List<SeasonStandingEntity> rows = await QualifierEngine.LoadLeagueStandingsAsync(
                context, source, feeder, rules, requireCompleteRanks: true, cancellationToken).ConfigureAwait(false);
            map[feeder.Id] = rows;
        }

        return map;
    }

    internal static async Task<RunQualifierResponse> BuildResponseAsync(
        SaveStore store,
        Guid saveId,
        SeasonEntity source,
        SeasonEntity next,
        QualifierEngineSimulation simulation,
        CancellationToken cancellationToken)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        List<QualifierStandingEntity> standings = await LoadPersistedStandingsAsync(context, source, next, cancellationToken).ConfigureAwait(false);
        Dictionary<int, string> names = await QualifierEngine.LoadAthleteNamesAsync(context, cancellationToken).ConfigureAwait(false);
        Dictionary<int, LeagueEntity> leaguesById = await LoadLeaguesByIdAsync(context, cancellationToken).ConfigureAwait(false);
        List<QualifierStandingMember> members = MapResponseMembers(standings, names, leaguesById, simulation);
        return await MapResponseAsync(context, saveId, source, next, simulation, standings, members, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<List<QualifierStandingEntity>> LoadPersistedStandingsAsync(
        SaveDbContext context, SeasonEntity source, SeasonEntity next, CancellationToken cancellationToken)
    {
        return await context.QualifierStandings
            .AsNoTracking()
            .Where(e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id)
            .OrderBy(e => e.QualifierRank)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<Dictionary<int, LeagueEntity>> LoadLeaguesByIdAsync(
        SaveDbContext context, CancellationToken cancellationToken)
    {
        return await context.Leagues
            .AsNoTracking()
            .ToDictionaryAsync(e => e.Id, cancellationToken)
            .ConfigureAwait(false);
    }

    internal static List<QualifierStandingMember> MapResponseMembers(
        List<QualifierStandingEntity> standings,
        Dictionary<int, string> names,
        Dictionary<int, LeagueEntity> leaguesById,
        QualifierEngineSimulation simulation)
    {
        Dictionary<int, QualifierProvenance> picksByAthlete =
            simulation.Field.ToDictionary(p => p.SaveAthleteId);
        List<QualifierStandingMember> members = new(standings.Count);
        foreach (QualifierStandingEntity standing in standings)
        {
            members.Add(MapSingleResponseMember(standing, names, leaguesById, picksByAthlete));
        }

        return members;
    }

    internal static QualifierStandingMember MapSingleResponseMember(
        QualifierStandingEntity standing,
        Dictionary<int, string> names,
        Dictionary<int, LeagueEntity> leaguesById,
        Dictionary<int, QualifierProvenance> picksByAthlete)
    {
        names.TryGetValue(standing.SaveAthleteId, out string? name);
        leaguesById.TryGetValue(standing.FromLeagueId, out LeagueEntity? from);
        picksByAthlete.TryGetValue(standing.SaveAthleteId, out QualifierProvenance? pick);
        string role = pick?.Role.ToString() ?? ((QualifierRole)standing.Role).ToString();
        string color = ((SportingColor)standing.SportingColor).ToString();
        return new QualifierStandingMember(
            standing.SaveAthleteId,
            name ?? $"Athlete {standing.SaveAthleteId}",
            color,
            role,
            standing.FromLeagueId,
            from?.Name ?? $"League {standing.FromLeagueId}",
            standing.FromSeasonRank,
            standing.QualifierRank,
            standing.QualifierScoreThousandths,
            standing.BaseScoreThousandths,
            standing.RoundWins,
            standing.IsQualified);
    }

    internal static async Task<RunQualifierResponse> MapResponseAsync(
        SaveDbContext context,
        Guid saveId,
        SeasonEntity source,
        SeasonEntity next,
        QualifierEngineSimulation simulation,
        List<QualifierStandingEntity> standings,
        List<QualifierStandingMember> members,
        CancellationToken cancellationToken)
    {
        RngStateEntity rng = await context.RngStates.AsNoTracking().SingleAsync(e => e.Id == 1, cancellationToken).ConfigureAwait(false);
        Pcg32State rngAfter = rng.ToState();
        QualifierRoundPayload first = simulation.Payloads[0];
        int incumbents = standings.Count(s => s.Role == (int)QualifierRole.Incumbent && s.IsQualified);
        int challengers = standings.Count(s => s.Role == (int)QualifierRole.Challenger && s.IsQualified);
        return new RunQualifierResponse(
            saveId,
            source.SeasonNumber,
            next.SeasonNumber,
            standings.Count,
            simulation.Payloads.Count,
            standings.Count(s => s.IsQualified),
            simulation.Checksum,
            first.RngBeforeState,
            first.RngBeforeStream,
            rngAfter.State,
            rngAfter.Stream,
            members,
            incumbents,
            challengers);
    }
}
