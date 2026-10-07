using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Cups.DrawTypeCupQualificationGroups;
using MtgSoloSports.Features.Cups.SelectTypeCupTeams;
using MtgSoloSports.Features.Seasons.SeasonLifecycle;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.SimulationKernel.FixedPoint;
using MtgSoloSports.SimulationKernel.Random;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Features.Cups.RunTypeCupTeam;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Runs the Type Cup team
/// tournament for a completed even source season (MSS-062).
/// Direct Finals (1-32 teams) run one 4-rank-group by 8-round competition;
/// larger fields run every persisted qualification group in draw order (each
/// 4x8 with the standard table) then a fresh 32-team Final from zero.
/// Qualification is a sporting stage, not a title: only Final standings award
/// medals/honours. Active career bonus at the next-season Stage 1 boundary
/// applies in every stage with normal fixed-point scoring; no new round or
/// stage career bonus is generated, no league championship points are awarded,
/// and no league <c>StageStanding</c>, <c>SeasonStanding</c> or <c>Round</c>
/// rows are created. Persists qualification results separately from Final
/// results plus permanent nationality for actual participants plus the
/// RNG-after state, in resumable transactions. Holds one per-save lock;
/// read-only Cup queries never lock.
/// </summary>
public sealed partial class RunTypeCupTeamHandler
{
    public const int TeamLeagueId = -3;
    public const int TeamLeagueKind = -4;
    public const string TeamLeagueName = "Type Cup Team";

    private readonly SaveStore _store;

    public RunTypeCupTeamHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<RunTypeCupTeamResponse> HandleAsync(
        Guid saveId,
        int? sourceSeasonNumber = null,
        CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        if (sourceSeasonNumber is < 1)
        {
            throw new ArgumentException("Season number must be positive.", nameof(sourceSeasonNumber));
        }

        SemaphoreSlim gate = AdvanceRoundSaveLock.Acquire(saveId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RunUnderLockAsync(saveId, sourceSeasonNumber, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task<RunTypeCupTeamResponse> RunUnderLockAsync(
        Guid saveId,
        int? sourceSeasonNumber,
        CancellationToken cancellationToken)
    {
        await _store.EnsureMigratedAsync(saveId, cancellationToken).ConfigureAwait(false);
        using SaveDbContext context = _store.OpenDbContext(saveId);
        using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await EnsureTournamentDrawAsync(context, saveId, sourceSeasonNumber, cancellationToken).ConfigureAwait(false);
        TournamentState state = await LoadStateAsync(context, saveId, sourceSeasonNumber, cancellationToken).ConfigureAwait(false);
        await EnsureFinalFieldFromPersistedAsync(context, state, cancellationToken).ConfigureAwait(false);
        List<(TypeCupTournamentPlan.StageKey Key, TypeCupTeamRoundPayloadDocument Payload)> ordered =
            new(state.PlayedOrdered);
        Pcg32State current = state.Rng;
        while (ordered.Count < state.Plan.TotalRounds)
        {
            EnsureInMemoryFinalField(state, ordered);
            (TypeCupTournamentPlan.StageKey key, TypeCupTeamRoundPayloadDocument payload, Pcg32State after) =
                PlayNextRound(state, ordered, current);
            ordered.Add((key, payload));
            current = after;
        }

        TournamentSimulation simulation = await CompleteAsync(context, state, ordered, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await BuildResponseAsync(_store, saveId, state.Source, simulation, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Ensures the qualification draw exists before tournament execution (MSS-062).
    /// For scalable &gt;32-team fields with no draw and no rounds yet, performs the
    /// same deterministic draw as the draw slice (same RNG, same math) in the
    /// caller's transaction so one-shot and step-by-step runs from identical
    /// save/RNG state produce identical draws. Explicit draws remain the
    /// inspectable path; this keeps the lifecycle a single high-level step.
    /// </summary>
    internal static async Task EnsureTournamentDrawAsync(
        SaveDbContext context,
        Guid saveId,
        int? sourceSeasonNumber,
        CancellationToken cancellationToken)
    {
        RulesV1 rules = await AdvanceRoundHandler.LoadRulesAsync(context, cancellationToken).ConfigureAwait(false);
        if (rules.TypeCupTournamentFormatVersion == RulesV1.LegacyTypeCupTournamentFormatVersion)
        {
            return;
        }

        SeasonEntity source = await LoadSourceSeasonAsync(context, sourceSeasonNumber, cancellationToken).ConfigureAwait(false);
        EnsureEvenSeason(source);
        List<TypeCupSelectionEntity> selection = await LoadSelectionAsync(context, source, cancellationToken).ConfigureAwait(false);
        TypeCupSelectionInvariants.ValidatePersisted(source, selection, rules);
        int teamCount = TypeCupTeamInvariants.ValidateField(selection, rules);
        if (TypeCupTournamentFormat.IsDirectFinal(teamCount, rules))
        {
            return;
        }

        List<TypeCupTournamentDrawEntity> existing = await context.TypeCupTournamentDraws
            .Where(e => e.SourceSeasonId == source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (existing.Count != 0)
        {
            return;
        }

        bool hasRounds = await context.TypeCupTeamRounds
            .AnyAsync(e => e.SourceSeasonId == source.Id, cancellationToken)
            .ConfigureAwait(false);
        if (hasRounds)
        {
            throw new InvalidOperationException(
                $"Type Cup tournament for Season {source.SeasonNumber} has rounds without a qualification draw; sporting state is corrupt.");
        }

        await PerformTournamentDrawAsync(context, source, selection, teamCount, rules, cancellationToken).ConfigureAwait(false);
    }

    private static async Task PerformTournamentDrawAsync(
        SaveDbContext context,
        SeasonEntity source,
        List<TypeCupSelectionEntity> selection,
        int teamCount,
        RulesV1 rules,
        CancellationToken cancellationToken)
    {
        RngStateEntity rngRow = await AdvanceRoundHandler.LoadRngAsync(context, cancellationToken).ConfigureAwait(false);
        Pcg32State rngBefore = rngRow.ToState();
        Pcg32V1 rng = Pcg32V1.Restore(rngBefore);
        List<string> types = selection
            .Select(e => e.CreatureType)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var assignments = TypeCupTournamentFormat.DrawQualificationGroups(types, rng, rules);
        Pcg32State rngAfter = rng.Snapshot();
        var groupSizes = TypeCupTournamentFormat.BalancedQualificationGroupSizes(teamCount, rules);
        var finalPlaces = TypeCupTournamentFormat.AllocateFinalPlaces(groupSizes, rules);
        foreach (int size in groupSizes)
        {
            TypeCupTournamentFormat.ValidateCompetitionFieldSize(size, rules);
        }

        TypeCupTournamentFormat.ValidateTournamentDraw(types, assignments, groupSizes, finalPlaces, rules);
        string checksum = TypeCupTournamentFormat.ComputeDrawChecksum(assignments, teamCount, groupSizes);
        AddDrawRows(context, source, rules, teamCount, assignments, groupSizes, finalPlaces, rngBefore, rngAfter, checksum);
        await StageDrawRngAsync(context, rngAfter, cancellationToken).ConfigureAwait(false);
    }

    private static void AddDrawRows(
        SaveDbContext context,
        SeasonEntity source,
        RulesV1 rules,
        int teamCount,
        IReadOnlyList<TypeCupTournamentFormat.QualificationAssignment> assignments,
        IReadOnlyList<int> groupSizes,
        IReadOnlyList<int> finalPlaces,
        Pcg32State rngBefore,
        Pcg32State rngAfter,
        string checksum)
    {
        Dictionary<string, int> groupByType = assignments.ToDictionary(a => a.CreatureType, a => a.QualificationGroup, StringComparer.Ordinal);
        foreach (var assignment in assignments.OrderBy(a => a.CreatureType, StringComparer.Ordinal))
        {
            int group = groupByType[assignment.CreatureType];
            context.TypeCupTournamentDraws.Add(new TypeCupTournamentDrawEntity
            {
                SourceSeasonId = source.Id,
                SourceSeasonNumber = source.SeasonNumber,
                CreatureType = assignment.CreatureType,
                QualificationGroup = group,
                GroupSize = groupSizes[group - 1],
                FieldTeamCount = teamCount,
                QualificationGroupCount = groupSizes.Count,
                FinalPlacesForGroup = finalPlaces[group - 1],
                RulesVersion = rules.Version,
                TournamentFormatVersion = RulesV1.DefaultTypeCupTournamentFormatVersion,
                RngBeforeState = unchecked((long)rngBefore.State),
                RngBeforeStream = unchecked((long)rngBefore.Stream),
                RngAfterState = unchecked((long)rngAfter.State),
                RngAfterStream = unchecked((long)rngAfter.Stream),
                DrawChecksum = checksum,
            });
        }
    }

    private static async Task StageDrawRngAsync(
        SaveDbContext context,
        Pcg32State rngAfter,
        CancellationToken cancellationToken)
    {
        RngStateEntity tracked = await context.RngStates.SingleAsync(e => e.Id == 1, cancellationToken).ConfigureAwait(false);
        tracked.Algorithm = Pcg32V1.AlgorithmName;
        tracked.AlgorithmVersion = Pcg32V1.AlgorithmVersion;
        tracked.State = unchecked((long)rngAfter.State);
        tracked.Stream = unchecked((long)rngAfter.Stream);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the Final field from persisted qualification standings when
    /// resuming after all quals are complete but before the Final starts.
    /// One-shot runs from scratch build it in-memory once quals are simulated.
    /// </summary>
    internal static async Task EnsureFinalFieldFromPersistedAsync(
        SaveDbContext context,
        TournamentState state,
        CancellationToken cancellationToken)
    {
        if (!state.Plan.IsTournament)
        {
            return;
        }

        var finalKey = TypeCupTournamentPlan.FinalKey();
        if (state.Fields.ContainsKey(finalKey))
        {
            return;
        }

        int perStage = state.Rules.TypeCupMinTeamSize * state.Rules.TypeCupGroupRounds;
        bool allQualsPlayed = state.Plan.QualificationStages.All(s =>
            state.PlayedOrdered.Count(o => o.Key.Phase == (int)TypeCupTournamentFormat.TournamentPhase.Qualification
                && o.Key.QualificationGroup == s.QualificationGroup) == perStage);
        if (!allQualsPlayed)
        {
            return;
        }

        List<TypeCupTeamStandingEntity> qualTeams = await context.TypeCupTeamStandings
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == state.Source.Id
                && e.TournamentPhase == (int)TypeCupTournamentFormat.TournamentPhase.Qualification)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (qualTeams.Count == 0)
        {
            return;
        }

        Dictionary<int, IReadOnlyList<(string Team, int Rank)>> byGroup = qualTeams
            .GroupBy(e => e.QualificationGroup)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<(string Team, int Rank)>)g.OrderBy(t => t.TeamRank).Select(t => (t.CreatureType, t.TeamRank)).ToList());
        IReadOnlyList<string> finalists = TypeCupTournamentPlan.SelectFinalists(state.Plan, byGroup);
        state.Fields[finalKey] = BuildFinalField(state, finalists);
    }

    /// <summary>
    /// Builds the Final field in-memory during a one-shot run once all
    /// qualification rounds are simulated but before Final simulation starts.
    /// Uses the same quota math as persisted qualifiers for identical results.
    /// </summary>
    internal static void EnsureInMemoryFinalField(
        TournamentState state,
        List<(TypeCupTournamentPlan.StageKey Key, TypeCupTeamRoundPayloadDocument Payload)> ordered)
    {
        if (!state.Plan.IsTournament)
        {
            return;
        }

        var finalKey = TypeCupTournamentPlan.FinalKey();
        if (state.Fields.ContainsKey(finalKey))
        {
            return;
        }

        int perStage = state.Rules.TypeCupMinTeamSize * state.Rules.TypeCupGroupRounds;
        int qualRounds = state.Plan.QualificationGroupCount * perStage;
        if (ordered.Count < qualRounds)
        {
            return;
        }

        Dictionary<int, IReadOnlyList<(string Team, int Rank)>> byGroup = new();
        foreach (var stage in state.Plan.QualificationStages)
        {
            var key = TypeCupTournamentPlan.QualificationKey(stage.QualificationGroup);
            var stagePayloads = ordered
                .Where(o => o.Key.Phase == key.Phase && o.Key.QualificationGroup == key.QualificationGroup)
                .Select(o => o.Payload)
                .ToList();
            var ranked = RankStage(state, key, stagePayloads);
            byGroup[stage.QualificationGroup] = ranked.Teams.Select(t => (t.TeamName, t.TeamRank)).ToList();
        }

        IReadOnlyList<string> finalists = TypeCupTournamentPlan.SelectFinalists(state.Plan, byGroup);
        state.Fields[finalKey] = BuildFinalField(state, finalists);
    }

    /// <summary>
    /// Loads tournament execution state and validates any partly played
    /// tournament (contiguous stage/round order, unbroken RNG chain including
    /// rank-group and stage tie-break boundaries, RNG row untouched since the
    /// last step). Aborts on corruption. For &gt;32-team scalable tournaments
    /// the draw must already exist (auto-created by
    /// <see cref="EnsureTournamentDrawAsync"/> when absent before play).
    /// </summary>
    internal static async Task<TournamentState> LoadStateAsync(
        SaveDbContext context,
        Guid saveId,
        int? sourceSeasonNumber,
        CancellationToken cancellationToken)
    {
        RulesV1 rules = await AdvanceRoundHandler.LoadRulesAsync(context, cancellationToken).ConfigureAwait(false);
        SaveMetadataEntity metadata = await AdvanceRoundHandler.LoadMetadataAsync(context, saveId, cancellationToken).ConfigureAwait(false);
        RngStateEntity rngRow = await AdvanceRoundHandler.LoadRngAsync(context, cancellationToken).ConfigureAwait(false);

        SeasonEntity source = await LoadSourceSeasonAsync(context, sourceSeasonNumber, cancellationToken).ConfigureAwait(false);
        EnsureEvenSeason(source);
        List<TypeCupSelectionEntity> selection = await LoadSelectionAsync(context, source, cancellationToken).ConfigureAwait(false);
        TypeCupSelectionInvariants.ValidatePersisted(source, selection, rules);
        int teamCount = TypeCupTeamInvariants.ValidateField(selection, rules);
        await EnsureTournamentUnresolvedAsync(context, source, cancellationToken).ConfigureAwait(false);

        (int stageCountBefore, int seasonCountBefore, int roundCountBefore, long lifetimeBefore, long effectiveBefore, long championshipBefore) =
            await CapturePreservationAsync(context, cancellationToken).ConfigureAwait(false);

        (Dictionary<int, Bonus> activeBonuses, List<TypeCupTournamentDrawEntity> draws, TypeCupTournamentPlan.Plan plan) =
            await LoadBonusesAndPlanAsync(context, source, selection, teamCount, rules, cancellationToken).ConfigureAwait(false);
        Dictionary<TypeCupTournamentPlan.StageKey, TypeCupStageField> fields = BuildStageFields(
            context, plan, selection, draws, rules, source, teamCount);
        await TryBuildFinalFieldAsync(context, source, selection, plan, fields, cancellationToken).ConfigureAwait(false);
        List<(TypeCupTournamentPlan.StageKey Key, TypeCupTeamRoundPayloadDocument Payload)> ordered =
            await LoadOrderedRoundsAsync(context, source, plan, rules, fields, cancellationToken).ConfigureAwait(false);

        Pcg32State rng = rngRow.ToState();
        TournamentState state = new(
            rules, metadata, source, selection, teamCount, plan, fields, ordered,
            activeBonuses, draws, rng,
            stageCountBefore, seasonCountBefore, roundCountBefore, lifetimeBefore, effectiveBefore, championshipBefore);

        ValidateRngChain(state);
        await ValidatePersistedStagesAsync(context, state, cancellationToken).ConfigureAwait(false);
        return state;
    }

    private static async Task<(Dictionary<int, Bonus> Bonuses, List<TypeCupTournamentDrawEntity> Draws, TypeCupTournamentPlan.Plan Plan)> LoadBonusesAndPlanAsync(
        SaveDbContext context,
        SeasonEntity source,
        List<TypeCupSelectionEntity> selection,
        int teamCount,
        RulesV1 rules,
        CancellationToken cancellationToken)
    {
        Dictionary<int, List<TypeCupSelectionEntity>> allGroups = PartitionGroups(selection, rules);
        TypeCupTeamInvariants.ValidateGroups(allGroups, rules, teamCount);
        Dictionary<int, List<AdvanceRoundHandler.MemberRow>> allRosters = BuildRosters(context, allGroups);
        Dictionary<int, Bonus> activeBonuses = await LoadCupActiveBonusesAsync(
            context, allRosters, source, rules, cancellationToken).ConfigureAwait(false);
        List<TypeCupTournamentDrawEntity> draws = await context.TypeCupTournamentDraws
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        TypeCupTournamentPlan.Plan plan = BuildPlan(teamCount, selection, draws, rules, source);
        return (activeBonuses, draws, plan);
    }

    private static async Task TryBuildFinalFieldAsync(
        SaveDbContext context,
        SeasonEntity source,
        List<TypeCupSelectionEntity> selection,
        TypeCupTournamentPlan.Plan plan,
        Dictionary<TypeCupTournamentPlan.StageKey, TypeCupStageField> fields,
        CancellationToken cancellationToken)
    {
        if (!plan.IsTournament)
        {
            return;
        }

        var finalKey = TypeCupTournamentPlan.FinalKey();
        if (fields.ContainsKey(finalKey))
        {
            return;
        }

        List<TypeCupTeamStandingEntity> qualTeams = await context.TypeCupTeamStandings
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id
                && e.TournamentPhase == (int)TypeCupTournamentFormat.TournamentPhase.Qualification)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (qualTeams.Count == 0)
        {
            return;
        }

        Dictionary<int, IReadOnlyList<(string Team, int Rank)>> byGroup = GroupQualStandings(qualTeams);
        if (!QualStandingsComplete(plan, byGroup))
        {
            return;
        }

        IReadOnlyList<string> finalists = TypeCupTournamentPlan.SelectFinalists(plan, byGroup);
        fields[finalKey] = await BuildPersistedFinalFieldAsync(context, selection, fields, finalists, cancellationToken).ConfigureAwait(false);
    }

    private static Dictionary<int, IReadOnlyList<(string Team, int Rank)>> GroupQualStandings(
        List<TypeCupTeamStandingEntity> qualTeams) =>
        qualTeams
            .GroupBy(e => e.QualificationGroup)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<(string Team, int Rank)>)g.OrderBy(t => t.TeamRank).Select(t => (t.CreatureType, t.TeamRank)).ToList());

    private static bool QualStandingsComplete(
        TypeCupTournamentPlan.Plan plan,
        Dictionary<int, IReadOnlyList<(string Team, int Rank)>> byGroup)
    {
        if (byGroup.Count != plan.QualificationGroupCount)
        {
            return false;
        }

        foreach (var stage in plan.QualificationStages)
        {
            if (!byGroup.TryGetValue(stage.QualificationGroup, out var standings)
                || standings.Count != stage.GroupSize)
            {
                return false;
            }
        }

        return true;
    }

    private static async Task<TypeCupStageField> BuildPersistedFinalFieldAsync(
        SaveDbContext context,
        List<TypeCupSelectionEntity> selection,
        Dictionary<TypeCupTournamentPlan.StageKey, TypeCupStageField> fields,
        IReadOnlyList<string> finalists,
        CancellationToken cancellationToken)
    {
        var dummyRules = await AdvanceRoundHandler.LoadRulesAsync(context, cancellationToken).ConfigureAwait(false);
        Dictionary<int, List<TypeCupSelectionEntity>> groups = new(dummyRules.TypeCupMinTeamSize);
        foreach (int rank in Enumerable.Range(1, dummyRules.TypeCupMinTeamSize))
        {
            groups[rank] = selection.Where(e => e.SelectionRank == rank && finalists.Contains(e.CreatureType, StringComparer.Ordinal)).ToList();
        }

        Dictionary<int, List<AdvanceRoundHandler.MemberRow>> rosters = BuildPersistedFinalRosters(fields, groups);
        List<string> orderedFinalists = finalists.OrderBy(t => t, StringComparer.Ordinal).ToList();
        Dictionary<string, int> teamIds = new(StringComparer.Ordinal);
        for (int i = 0; i < orderedFinalists.Count; i++)
        {
            teamIds[orderedFinalists[i]] = i;
        }

        return new TypeCupStageField(TypeCupTournamentPlan.FinalKey(), orderedFinalists.Count, groups, rosters, teamIds, orderedFinalists);
    }

    private static Dictionary<int, List<AdvanceRoundHandler.MemberRow>> BuildPersistedFinalRosters(
        Dictionary<TypeCupTournamentPlan.StageKey, TypeCupStageField> fields,
        Dictionary<int, List<TypeCupSelectionEntity>> groups)
    {
        Dictionary<int, List<AdvanceRoundHandler.MemberRow>> rosters = new(groups.Count);
        foreach ((int rank, List<TypeCupSelectionEntity> members) in groups)
        {
            List<AdvanceRoundHandler.MemberRow> roster = new(members.Count);
            foreach (TypeCupSelectionEntity row in members)
            {
                string name = ResolveNameFromFields(fields, row.SaveAthleteId);
                roster.Add(new AdvanceRoundHandler.MemberRow(row.SaveAthleteId, name, 0));
            }

            roster.Sort(static (left, right) => string.Compare(left.Name, right.Name, StringComparison.Ordinal));
            rosters[rank] = roster;
        }

        return rosters;
    }

    private static string ResolveNameFromFields(
        Dictionary<TypeCupTournamentPlan.StageKey, TypeCupStageField> fields,
        int athleteId)
    {
        foreach (TypeCupStageField field in fields.Values)
        {
            foreach (List<AdvanceRoundHandler.MemberRow> roster in field.Rosters.Values)
            {
                var found = roster.FirstOrDefault(r => r.AthleteId == athleteId);
                if (found is not null)
                {
                    return found.Name;
                }
            }
        }

        throw new InvalidOperationException($"Type Cup Final references unknown athlete {athleteId}.");
    }

    private static async Task<List<(TypeCupTournamentPlan.StageKey Key, TypeCupTeamRoundPayloadDocument Payload)>> LoadOrderedRoundsAsync(
        SaveDbContext context,
        SeasonEntity source,
        TypeCupTournamentPlan.Plan plan,
        RulesV1 rules,
        Dictionary<TypeCupTournamentPlan.StageKey, TypeCupStageField> fields,
        CancellationToken cancellationToken)
    {
        List<TypeCupTeamRoundEntity> rows = await context.TypeCupTeamRounds
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<(TypeCupTournamentPlan.StageKey Key, TypeCupTeamRoundPayloadDocument Payload)> ordered =
            OrderPlayedRounds(rows, plan, rules, source);
        foreach ((TypeCupTournamentPlan.StageKey key, TypeCupTeamRoundPayloadDocument payload) in ordered)
        {
            if (!fields.TryGetValue(key, out TypeCupStageField? field))
            {
                string have = string.Join(",", fields.Keys.Select(k => $"{k.Phase}/{k.QualificationGroup}"));
                throw new InvalidOperationException(
                    $"Type Cup tournament stage phase {key.Phase} qual {key.QualificationGroup} has no field (have {have}); ordered has {ordered.Count} rounds, plan total {plan.TotalRounds}.");
            }
            TypeCupTeamInvariants.ValidateRound(payload, rules, field.FieldSize, payload.RngBeforeState, payload.RngBeforeStream);
            if (!plan.IsLegacy)
            {
                TypeCupTournamentFormat.ValidateCompetitionFieldSize(field.FieldSize, rules);
            }
        }

        return ordered;
    }

    internal static TypeCupTournamentPlan.Plan BuildPlan(
        int teamCount,
        List<TypeCupSelectionEntity> selection,
        List<TypeCupTournamentDrawEntity> draws,
        RulesV1 rules,
        SeasonEntity source)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(draws);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(source);
        if (rules.TypeCupTournamentFormatVersion == RulesV1.LegacyTypeCupTournamentFormatVersion)
        {
            return TypeCupTournamentPlan.BuildLegacy(teamCount, rules);
        }

        List<string> selectedTypes = selection
            .Select(e => e.CreatureType)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();
        return TypeCupTournamentPlan.BuildFromSelection(teamCount, selectedTypes, draws, rules);
    }

    internal static Dictionary<TypeCupTournamentPlan.StageKey, TypeCupStageField> BuildStageFields(
        SaveDbContext context,
        TypeCupTournamentPlan.Plan plan,
        List<TypeCupSelectionEntity> selection,
        List<TypeCupTournamentDrawEntity> draws,
        RulesV1 rules,
        SeasonEntity source,
        int teamCount)
    {
        Dictionary<TypeCupTournamentPlan.StageKey, TypeCupStageField> fields = new();
        if (plan.IsLegacy)
        {
            Dictionary<int, List<TypeCupSelectionEntity>> groups = PartitionGroups(selection, rules);
            TypeCupTeamInvariants.ValidateGroups(groups, rules, teamCount);
            Dictionary<int, List<AdvanceRoundHandler.MemberRow>> rosters = BuildRosters(context, groups);
            fields[TypeCupTournamentPlan.LegacyKey()] = new TypeCupStageField(
                TypeCupTournamentPlan.LegacyKey(),
                teamCount,
                groups,
                rosters,
                BuildTeamIds(selection),
                selection.Select(e => e.CreatureType).Distinct(StringComparer.Ordinal).OrderBy(t => t, StringComparer.Ordinal).ToList());
            return fields;
        }

        if (plan.IsDirectFinal)
        {
            if (draws.Count != 0)
            {
                throw new InvalidOperationException(
                    $"Direct-Final Season {source.SeasonNumber} must not persist a qualification draw.");
            }

            Dictionary<int, List<TypeCupSelectionEntity>> groups = PartitionGroups(selection, rules);
            TypeCupTeamInvariants.ValidateGroups(groups, rules, teamCount);
            Dictionary<int, List<AdvanceRoundHandler.MemberRow>> rosters = BuildRosters(context, groups);
            fields[TypeCupTournamentPlan.FinalKey()] = new TypeCupStageField(
                TypeCupTournamentPlan.FinalKey(),
                teamCount,
                groups,
                rosters,
                BuildTeamIds(selection),
                selection.Select(e => e.CreatureType).Distinct(StringComparer.Ordinal).OrderBy(t => t, StringComparer.Ordinal).ToList());
            return fields;
        }

        AddQualificationFields(context, fields, plan, selection, draws, rules, source);
        return fields;
    }

    private static void AddQualificationFields(
        SaveDbContext context,
        Dictionary<TypeCupTournamentPlan.StageKey, TypeCupStageField> fields,
        TypeCupTournamentPlan.Plan plan,
        List<TypeCupSelectionEntity> selection,
        List<TypeCupTournamentDrawEntity> draws,
        RulesV1 rules,
        SeasonEntity source)
    {
        TypeCupTournamentDrawInvariants.ValidatePersisted(source, selection, draws, rules);
        Dictionary<string, int> groupByType = draws.ToDictionary(d => d.CreatureType, d => d.QualificationGroup, StringComparer.Ordinal);
        foreach (TypeCupTournamentPlan.QualificationStage stage in plan.QualificationStages)
        {
            fields[TypeCupTournamentPlan.QualificationKey(stage.QualificationGroup)] =
                BuildQualificationField(context, stage, selection, groupByType, rules);
        }
    }

    private static TypeCupStageField BuildQualificationField(
        SaveDbContext context,
        TypeCupTournamentPlan.QualificationStage stage,
        List<TypeCupSelectionEntity> selection,
        Dictionary<string, int> groupByType,
        RulesV1 rules)
    {
        List<TypeCupSelectionEntity> stageSelection = selection
            .Where(e => groupByType.TryGetValue(e.CreatureType, out int g) && g == stage.QualificationGroup)
            .ToList();
        if (stageSelection.Count != stage.GroupSize * rules.TypeCupMinTeamSize)
        {
            throw new InvalidOperationException(
                $"Type Cup qualification group {stage.QualificationGroup} must hold exactly {stage.GroupSize} teams.");
        }

        Dictionary<int, List<TypeCupSelectionEntity>> groups = PartitionGroups(stageSelection, rules);
        TypeCupTeamInvariants.ValidateGroups(groups, rules, stage.GroupSize);
        Dictionary<int, List<AdvanceRoundHandler.MemberRow>> rosters = BuildRosters(context, groups);
        return new TypeCupStageField(
            TypeCupTournamentPlan.QualificationKey(stage.QualificationGroup),
            stage.GroupSize,
            groups,
            rosters,
            BuildTeamIds(stageSelection),
            stage.TeamTypes);
    }

    /// <summary>
    /// Orders persisted round rows into global tournament order (qualification
    /// groups in draw order, then the Final; within each stage rank groups
    /// 1..4 rounds 1..8). Legacy rows order by rank group/round.
    /// </summary>
    internal static List<(TypeCupTournamentPlan.StageKey Key, TypeCupTeamRoundPayloadDocument Payload)> OrderPlayedRounds(
        List<TypeCupTeamRoundEntity> rows,
        TypeCupTournamentPlan.Plan plan,
        RulesV1 rules,
        SeasonEntity source)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(source);
        IReadOnlyList<TypeCupTournamentPlan.StageKey> sequence = TypeCupTournamentPlan.StageSequence(plan);
        Dictionary<(int Phase, int Qual, int Group, int Round), TypeCupTeamRoundEntity> byIdentity = new();
        foreach (TypeCupTeamRoundEntity row in rows)
        {
            if (row.SourceSeasonId != source.Id || row.SourceSeasonNumber != source.SeasonNumber)
            {
                throw new InvalidOperationException($"Type Cup team round {row.Id} has corrupt source linkage.");
            }

            TypeCupTeamInvariants.ValidateTournamentIdentity(row.TournamentPhase, row.QualificationGroup, "round");
            var key = (row.TournamentPhase, row.QualificationGroup, row.GroupNumber, row.RoundNumber);
            if (!byIdentity.TryAdd(key, row))
            {
                throw new InvalidOperationException(
                    $"Type Cup team contains duplicate phase {row.TournamentPhase} qual {row.QualificationGroup} group {row.GroupNumber} round {row.RoundNumber}.");
            }
        }

        List<(TypeCupTournamentPlan.StageKey Key, TypeCupTeamRoundPayloadDocument Payload)> ordered = new(rows.Count);
        if (plan.IsLegacy)
        {
            // Legacy single-field: exactly the 32 rank-group rounds with phase 0.
            foreach (TypeCupTournamentPlan.StageKey stage in sequence)
            {
                for (int group = 1; group <= rules.TypeCupMinTeamSize; group++)
                {
                    for (int round = 1; round <= rules.TypeCupGroupRounds; round++)
                    {
                        if (byIdentity.TryGetValue((stage.Phase, stage.QualificationGroup, group, round), out TypeCupTeamRoundEntity? row))
                        {
                            ordered.Add((stage, TypeCupTeamRoundPayloadDocument.FromStored(row.PayloadJson)));
                        }
                    }
                }
            }

            // Any new-format rows under a legacy plan are corruption.
            if (ordered.Count != rows.Count)
            {
                throw new InvalidOperationException("Type Cup legacy tournament has out-of-sequence rounds.");
            }

            return ordered;
        }

        CollectStageRounds(ordered, byIdentity, sequence, rules);
        if (ordered.Count != rows.Count)
        {
            throw new InvalidOperationException("Type Cup tournament has out-of-sequence rounds.");
        }

        ValidateStagePrefix(ordered, sequence, rules);
        return ordered;
    }

    private static void CollectStageRounds(
        List<(TypeCupTournamentPlan.StageKey Key, TypeCupTeamRoundPayloadDocument Payload)> ordered,
        Dictionary<(int Phase, int Qual, int Group, int Round), TypeCupTeamRoundEntity> byIdentity,
        IReadOnlyList<TypeCupTournamentPlan.StageKey> sequence,
        RulesV1 rules)
    {
        foreach (TypeCupTournamentPlan.StageKey stage in sequence)
        {
            for (int group = 1; group <= rules.TypeCupMinTeamSize; group++)
            {
                for (int round = 1; round <= rules.TypeCupGroupRounds; round++)
                {
                    if (byIdentity.TryGetValue((stage.Phase, stage.QualificationGroup, group, round), out TypeCupTeamRoundEntity? row))
                    {
                        ordered.Add((stage, TypeCupTeamRoundPayloadDocument.FromStored(row.PayloadJson)));
                    }
                }
            }
        }
    }

    private static void ValidateStagePrefix(
        List<(TypeCupTournamentPlan.StageKey Key, TypeCupTeamRoundPayloadDocument Payload)> ordered,
        IReadOnlyList<TypeCupTournamentPlan.StageKey> sequence,
        RulesV1 rules)
    {
        int perStage = rules.TypeCupMinTeamSize * rules.TypeCupGroupRounds;
        bool seenPartial = false;
        bool seenEmpty = false;
        for (int s = 0; s < sequence.Count; s++)
        {
            int countInStage = ordered.Count(o => o.Key.Phase == sequence[s].Phase && o.Key.QualificationGroup == sequence[s].QualificationGroup);
            if (countInStage == perStage)
            {
                if (seenPartial || seenEmpty)
                {
                    throw new InvalidOperationException("Type Cup tournament stage order is corrupt: complete stage after partial/empty stage.");
                }

                continue;
            }

            if (countInStage == 0)
            {
                seenEmpty = true;
                continue;
            }

            if (seenPartial || seenEmpty)
            {
                throw new InvalidOperationException("Type Cup tournament stage order is corrupt: partial stage after partial/empty stage.");
            }

            seenPartial = true;
            ValidatePartialStageRounds(ordered, sequence[s], rules);
        }
    }

    private static void ValidatePartialStageRounds(
        List<(TypeCupTournamentPlan.StageKey Key, TypeCupTeamRoundPayloadDocument Payload)> ordered,
        TypeCupTournamentPlan.StageKey stage,
        RulesV1 rules)
    {
        List<(TypeCupTournamentPlan.StageKey Key, TypeCupTeamRoundPayloadDocument Payload)> inStage =
            ordered.Where(o => o.Key.Phase == stage.Phase && o.Key.QualificationGroup == stage.QualificationGroup).ToList();
        for (int i = 0; i < inStage.Count; i++)
        {
            (int expGroup, int expRound) = PostseasonEvents.Cursor(i, new PostseasonEvents.EventShape(rules.TypeCupMinTeamSize, rules.TypeCupGroupRounds));
            if (inStage[i].Payload.GroupNumber != expGroup || inStage[i].Payload.RoundNumber != expRound)
            {
                throw new InvalidOperationException(
                    $"Type Cup tournament round sequence is corrupt: expected group {expGroup} round {expRound}.");
            }
        }
    }

    /// <summary>
    /// Validates the unbroken RNG chain across tournament stages: each round
    /// starts where the previous round ended, or at a rank-group boundary where
    /// the previous group's leg tie-break left the RNG, or at a stage boundary
    /// where the previous stage's team ranking left the RNG. The save RNG row
    /// must equal the state after the last persisted step.
    /// </summary>
    internal static void ValidateRngChain(TournamentState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.PlayedOrdered.Count == 0)
        {
            // No rounds yet: RNG must equal the draw-after state for tournaments
            // (draw consumed RNG before competition) or the untouched row for
            // direct/legacy (nothing consumed yet). The draw-after equality is
            // validated implicitly: first round simulations start from state.Rng.
            return;
        }

        if (state.PlayedOrdered.Count >= state.Plan.TotalRounds)
        {
            throw new InvalidOperationException(
                $"Type Cup tournament has all {state.Plan.TotalRounds} rounds persisted but no results; sporting state is corrupt.");
        }

        for (int index = 0; index < state.PlayedOrdered.Count; index++)
        {
            (TypeCupTournamentPlan.StageKey expStage, int expGroup, int expRound) =
                TypeCupTournamentPlan.Cursor(index, state.Plan, state.Rules);
            (TypeCupTournamentPlan.StageKey actualStage, TypeCupTeamRoundPayloadDocument payload) = state.PlayedOrdered[index];
            if (actualStage.Phase != expStage.Phase
                || actualStage.QualificationGroup != expStage.QualificationGroup
                || payload.GroupNumber != expGroup
                || payload.RoundNumber != expRound)
            {
                throw new InvalidOperationException(
                    $"Type Cup tournament round sequence is corrupt: expected phase {expStage.Phase} qual {expStage.QualificationGroup} group {expGroup} round {expRound}.");
            }

            if (index == 0)
            {
                continue;
            }

            Pcg32State expectedBefore = ExpectedRngBefore(state, index);
            Pcg32State actualBefore = new(payload.RngBeforeState, payload.RngBeforeStream);
            if (actualBefore != expectedBefore)
            {
                throw new InvalidOperationException(
                    $"Type Cup tournament RNG chain is broken before phase {actualStage.Phase} qual {actualStage.QualificationGroup} group {payload.GroupNumber} round {payload.RoundNumber}.");
            }
        }

        Pcg32State expectedRow = ExpectedRngAfterLast(state);
        if (state.Rng != expectedRow)
        {
            throw new InvalidOperationException(
                "Type Cup tournament is in progress but the save RNG moved since its last round; sporting state is corrupt.");
        }
    }

    internal static Pcg32State ExpectedRngBefore(TournamentState state, int index)
    {
        // Index 0 has no predecessor (checked by caller).
        (TypeCupTournamentPlan.StageKey prevStage, TypeCupTeamRoundPayloadDocument prev) = state.PlayedOrdered[index - 1];
        (TypeCupTournamentPlan.StageKey curStage, TypeCupTeamRoundPayloadDocument cur) = state.PlayedOrdered[index];
        Pcg32State prevAfter = new(prev.RngAfterState, prev.RngAfterStream);

        // Same rank group: next round starts where previous ended.
        if (curStage.Phase == prevStage.Phase
            && curStage.QualificationGroup == prevStage.QualificationGroup
            && cur.RoundNumber > 1)
        {
            return prevAfter;
        }

        // New rank group within the same stage: previous group's leg tie-break.
        if (curStage.Phase == prevStage.Phase
            && curStage.QualificationGroup == prevStage.QualificationGroup)
        {
            List<TypeCupTeamRoundPayloadDocument> groupPayloads = state.PlayedOrdered
                .Where(o => o.Key.Phase == prevStage.Phase
                    && o.Key.QualificationGroup == prevStage.QualificationGroup
                    && o.Payload.GroupNumber == prev.GroupNumber)
                .Select(o => o.Payload)
                .ToList();
            return RankStageGroupLeg(state, prevStage, prev.GroupNumber, groupPayloads).RngAfter;
        }

        // New stage: previous stage's team ranking after.
        return RankStageTeams(state, prevStage).RngAfter;
    }

    internal static Pcg32State ExpectedRngAfterLast(TournamentState state)
    {
        (TypeCupTournamentPlan.StageKey lastStage, TypeCupTeamRoundPayloadDocument last) = state.PlayedOrdered[^1];
        Pcg32State lastAfter = new(last.RngAfterState, last.RngAfterStream);
        int perStage = state.Rules.TypeCupMinTeamSize * state.Rules.TypeCupGroupRounds;
        int inStage = state.PlayedOrdered.Count(o => o.Key.Phase == lastStage.Phase && o.Key.QualificationGroup == lastStage.QualificationGroup);

        // Last round of a rank group 1..3: leg tie-break consumed immediately.
        if (last.RoundNumber == state.Rules.TypeCupGroupRounds && last.GroupNumber < state.Rules.TypeCupMinTeamSize)
        {
            List<TypeCupTeamRoundPayloadDocument> groupPayloads = state.PlayedOrdered
                .Where(o => o.Key.Phase == lastStage.Phase
                    && o.Key.QualificationGroup == lastStage.QualificationGroup
                    && o.Payload.GroupNumber == last.GroupNumber)
                .Select(o => o.Payload)
                .ToList();
            return RankStageGroupLeg(state, lastStage, last.GroupNumber, groupPayloads).RngAfter;
        }

        // Last round of a qualification stage (rank 4 round 8, non-final stage):
        // stage team ranking consumed immediately so the next stage starts there.
        if (last.GroupNumber == state.Rules.TypeCupMinTeamSize
            && last.RoundNumber == state.Rules.TypeCupGroupRounds
            && inStage == perStage
            && !IsTournamentFinalStage(state, lastStage))
        {
            return RankStageTeams(state, lastStage).RngAfter;
        }

        return lastAfter;
    }

    internal static bool IsTournamentFinalStage(TournamentState state, TypeCupTournamentPlan.StageKey stage)
    {
        IReadOnlyList<TypeCupTournamentPlan.StageKey> sequence = TypeCupTournamentPlan.StageSequence(state.Plan);
        TypeCupTournamentPlan.StageKey last = sequence[^1];
        return stage.Phase == last.Phase && stage.QualificationGroup == last.QualificationGroup;
    }

    /// <summary>
    /// Validates persisted stage standings against recomputed standings for
    /// completed stages and ensures the Final cannot start before every
    /// qualification group is complete with persisted qualifiers.
    /// </summary>
    internal static async Task ValidatePersistedStagesAsync(
        SaveDbContext context,
        TournamentState state,
        CancellationToken cancellationToken)
    {
        List<TypeCupTeamStandingEntity> allTeams = await context.TypeCupTeamStandings
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == state.Source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<TypeCupTeamGroupStandingEntity> allLegs = await context.TypeCupTeamGroupStandings
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == state.Source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (state.Plan.IsLegacy)
        {
            // Legacy: no standings until complete; any standings with rounds
            // still pending is corruption (handled by unresolved check).
            return;
        }

        if (state.Plan.IsDirectFinal)
        {
            // Direct Final: same as legacy single-stage.
            return;
        }

        ValidateQualificationStandings(state, allTeams, allLegs);
        ValidateFinalNotEarly(state);
    }

    private static void ValidateQualificationStandings(
        TournamentState state,
        List<TypeCupTeamStandingEntity> allTeams,
        List<TypeCupTeamGroupStandingEntity> allLegs)
    {
        IReadOnlyList<TypeCupTournamentPlan.StageKey> sequence = TypeCupTournamentPlan.StageSequence(state.Plan);
        foreach (TypeCupTournamentPlan.StageKey stage in sequence)
        {
            ValidateSingleStageStandings(state, stage, allTeams, allLegs);
        }
    }

    private static void ValidateSingleStageStandings(
        TournamentState state,
        TypeCupTournamentPlan.StageKey stage,
        List<TypeCupTeamStandingEntity> allTeams,
        List<TypeCupTeamGroupStandingEntity> allLegs)
    {
        int inStage = state.PlayedOrdered.Count(o => o.Key.Phase == stage.Phase && o.Key.QualificationGroup == stage.QualificationGroup);
        int perStage = state.Rules.TypeCupMinTeamSize * state.Rules.TypeCupGroupRounds;
        bool stageComplete = inStage == perStage;
        List<TypeCupTeamStandingEntity> stageTeams = allTeams
            .Where(e => e.TournamentPhase == stage.Phase && e.QualificationGroup == stage.QualificationGroup)
            .ToList();
        List<TypeCupTeamGroupStandingEntity> stageLegs = allLegs
            .Where(e => e.TournamentPhase == stage.Phase && e.QualificationGroup == stage.QualificationGroup)
            .ToList();

        if (IsTournamentFinalStage(state, stage))
        {
            if (stageTeams.Count != 0 || stageLegs.Count != 0)
            {
                throw new InvalidOperationException(
                    $"Type Cup Final for Season {state.Source.SeasonNumber} has partial standings without completion.");
            }

            return;
        }

        TypeCupStageField field = state.Fields[stage];
        if (stageComplete)
        {
            ValidateCompletedQualStandings(state, stage, field, stageTeams, stageLegs);
            return;
        }

        if (stageTeams.Count != 0 || stageLegs.Count != 0)
        {
            throw new InvalidOperationException(
                $"Type Cup qualification group {stage.QualificationGroup} has standings without complete rounds; sporting state is corrupt.");
        }
    }

    private static void ValidateCompletedQualStandings(
        TournamentState state,
        TypeCupTournamentPlan.StageKey stage,
        TypeCupStageField field,
        List<TypeCupTeamStandingEntity> stageTeams,
        List<TypeCupTeamGroupStandingEntity> stageLegs)
    {
        if (stageTeams.Count != field.FieldSize || stageLegs.Count != field.FieldSize * state.Rules.TypeCupMinTeamSize)
        {
            throw new InvalidOperationException(
                $"Type Cup qualification group {stage.QualificationGroup} is complete but its standings are missing; sporting state is corrupt.");
        }

        var ordered = stageTeams.OrderBy(t => t.TeamRank).ToList();
        for (int i = 0; i < ordered.Count; i++)
        {
            if (ordered[i].TeamRank != i + 1)
            {
                throw new InvalidOperationException(
                    $"Type Cup qualification group {stage.QualificationGroup} ranks are corrupt.");
            }
        }
    }

    private static void ValidateFinalNotEarly(TournamentState state)
    {
        bool anyFinalRounds = state.PlayedOrdered.Any(o => o.Key.Phase == ((int)TypeCupTournamentFormat.TournamentPhase.Final));
        if (!anyFinalRounds)
        {
            return;
        }

        IReadOnlyList<TypeCupTournamentPlan.StageKey> sequence = TypeCupTournamentPlan.StageSequence(state.Plan);
        foreach (TypeCupTournamentPlan.StageKey stage in sequence.Where(s => s.Phase == ((int)TypeCupTournamentFormat.TournamentPhase.Qualification)))
        {
            int inStage = state.PlayedOrdered.Count(o => o.Key.Phase == stage.Phase && o.Key.QualificationGroup == stage.QualificationGroup);
            int perStage = state.Rules.TypeCupMinTeamSize * state.Rules.TypeCupGroupRounds;
            if (inStage != perStage)
            {
                throw new InvalidOperationException(
                    "Type Cup Final started before every qualification group is complete; sporting state is corrupt.");
            }
        }
    }

    /// <summary>
    /// Completes the tournament from all ordered payloads (the source of truth)
    /// in the caller's transaction, in the same order as the one-shot run.
    /// </summary>
    internal static async Task<TournamentSimulation> CompleteAsync(
        SaveDbContext context,
        TournamentState state,
        List<(TypeCupTournamentPlan.StageKey Key, TypeCupTeamRoundPayloadDocument Payload)> ordered,
        CancellationToken cancellationToken)
    {
        TournamentSimulation simulation = FinishTournament(state, ordered);
        await PersistTournamentAsync(context, state, simulation, cancellationToken).ConfigureAwait(false);
        context.ApplyRngState(simulation.RngAfter);
        state.Metadata.Phase = Features.Saves.SavePhaseParser.ToText(Features.Saves.SavePhase.CupComplete);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await ValidatePersistedAsync(
            context, state, simulation, cancellationToken).ConfigureAwait(false);
        await EmitTeamStoriesAsync(context, state.Source, simulation, cancellationToken).ConfigureAwait(false);
        return simulation;
    }

    internal static async Task<SeasonEntity> LoadSourceSeasonAsync(
        SaveDbContext context,
        int? sourceSeasonNumber,
        CancellationToken cancellationToken)
    {
        if (sourceSeasonNumber.HasValue)
        {
            SeasonEntity? explicitSeason = await context.Seasons
                .SingleOrDefaultAsync(e => e.SeasonNumber == sourceSeasonNumber.Value, cancellationToken)
                .ConfigureAwait(false);
            if (explicitSeason is null || !explicitSeason.IsComplete)
            {
                throw new RunTypeCupTeamConflictException(
                    $"Season {sourceSeasonNumber.Value} is not a completed season ready for the Type Cup team event.");
            }

            return explicitSeason;
        }

        List<TypeCupSelectionEntity> any = await context.TypeCupSelections
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (any.Count == 0)
        {
            throw new RunTypeCupTeamConflictException(
                "Type Cup team selection must be resolved before the team event can run.");
        }

        int latestSourceId = any.Max(e => e.SourceSeasonId);
        SeasonEntity? latest = await context.Seasons
            .SingleOrDefaultAsync(e => e.Id == latestSourceId, cancellationToken)
            .ConfigureAwait(false);
        if (latest is null || !latest.IsComplete)
        {
            throw new InvalidOperationException("Type Cup selection references an unknown or incomplete season.");
        }

        return latest;
    }

    internal static void EnsureEvenSeason(SeasonEntity source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.SeasonNumber % 2 == 1)
        {
            throw new RunTypeCupTeamConflictException(
                $"Season {source.SeasonNumber} is odd; the Type Cup team event runs only for even seasons (odd seasons use the Color Cup).");
        }
    }

    internal static async Task<List<TypeCupSelectionEntity>> LoadSelectionAsync(
        SaveDbContext context,
        SeasonEntity source,
        CancellationToken cancellationToken)
    {
        List<TypeCupSelectionEntity> rows = await context.TypeCupSelections
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (rows.Count == 0)
        {
            throw new RunTypeCupTeamConflictException(
                $"Type Cup team selection for Season {source.SeasonNumber} must be resolved before the team event can run.");
        }

        return rows;
    }

    internal static Dictionary<int, List<TypeCupSelectionEntity>> PartitionGroups(
        List<TypeCupSelectionEntity> selection,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(rules);
        Dictionary<int, List<TypeCupSelectionEntity>> groups = new(rules.TypeCupMinTeamSize);
        foreach (int groupNumber in Enumerable.Range(1, rules.TypeCupMinTeamSize))
        {
            groups[groupNumber] = new List<TypeCupSelectionEntity>();
        }

        foreach (TypeCupSelectionEntity row in selection)
        {
            if (!groups.TryGetValue(row.SelectionRank, out List<TypeCupSelectionEntity>? members))
            {
                throw new InvalidOperationException($"Type Cup selection rank {row.SelectionRank} is out of range for team groups.");
            }

            members.Add(row);
        }

        return groups;
    }

    internal static Dictionary<string, int> BuildTeamIds(IReadOnlyList<TypeCupSelectionEntity> selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        List<string> types = selection
            .Select(e => e.CreatureType)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();
        Dictionary<string, int> ids = new(StringComparer.Ordinal);
        for (int i = 0; i < types.Count; i++)
        {
            ids[types[i]] = i;
        }

        return ids;
    }

    /// <summary>
    /// The tournament owns the save RNG until the Final can legally complete:
    /// any partly played stage blocks other random postseason events, and a
    /// completed Final blocks replay. Qualification standings alone do not
    /// complete the Cup.
    /// </summary>
    internal static async Task EnsureTournamentUnresolvedAsync(
        SaveDbContext context,
        SeasonEntity source,
        CancellationToken cancellationToken)
    {
        bool hasFinalTeams = await context.TypeCupTeamStandings.AnyAsync(
            e => e.SourceSeasonId == source.Id
                && (e.TournamentPhase == (int)TypeCupTournamentFormat.TournamentPhase.LegacySingleField
                    || e.TournamentPhase == (int)TypeCupTournamentFormat.TournamentPhase.Final),
            cancellationToken).ConfigureAwait(false);
        if (hasFinalTeams)
        {
            throw new RunTypeCupTeamConflictException(
                $"Type Cup team event for Season {source.SeasonNumber} has already been resolved.");
        }

        bool hasFinalLegs = await context.TypeCupTeamGroupStandings.AnyAsync(
            e => e.SourceSeasonId == source.Id
                && (e.TournamentPhase == (int)TypeCupTournamentFormat.TournamentPhase.LegacySingleField
                    || e.TournamentPhase == (int)TypeCupTournamentFormat.TournamentPhase.Final),
            cancellationToken).ConfigureAwait(false);
        if (hasFinalLegs)
        {
            throw new InvalidOperationException($"Type Cup team event for Season {source.SeasonNumber} has corrupt partial legs.");
        }

        bool hasHonour = await context.Honours.AnyAsync(
            e => e.SeasonId == source.Id && (e.Kind == (int)Features.Records.HonourKind.TypeCupTeamChampion
                || e.Kind == (int)Features.Records.HonourKind.TypeCupTeamRunnerUp
                || e.Kind == (int)Features.Records.HonourKind.TypeCupTeamThirdPlace),
            cancellationToken).ConfigureAwait(false);
        if (hasHonour)
        {
            throw new InvalidOperationException($"Type Cup team event for Season {source.SeasonNumber} has corrupt partial honours.");
        }

        string? other = await PostseasonEvents.FindOtherInProgressAsync(context, PostseasonEvents.TypeCupTeam, source.Id, cancellationToken).ConfigureAwait(false);
        if (other is not null)
        {
            throw new RunTypeCupTeamConflictException(
                $"{other} is partly played; finish it before playing the {PostseasonEvents.Title(PostseasonEvents.TypeCupTeam)}.");
        }
    }

    internal static Dictionary<int, List<AdvanceRoundHandler.MemberRow>> BuildRosters(
        SaveDbContext context,
        Dictionary<int, List<TypeCupSelectionEntity>> groups)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(groups);
        List<int> allIds = groups.Values.SelectMany(g => g.Select(r => r.SaveAthleteId)).ToList();
        Dictionary<int, (string Name, int Color)> athletes = context.SaveAthletes
            .AsNoTracking()
            .Where(e => allIds.Contains(e.Id))
            .ToDictionary(e => e.Id, e => (e.Name, e.SportingColor));
        if (athletes.Count != allIds.Count)
        {
            throw new InvalidOperationException("Type Cup team selection references unknown athletes.");
        }

        Dictionary<int, List<AdvanceRoundHandler.MemberRow>> rosters = new(groups.Count);
        foreach ((int groupNumber, List<TypeCupSelectionEntity> members) in groups)
        {
            List<AdvanceRoundHandler.MemberRow> roster = new(members.Count);
            foreach (TypeCupSelectionEntity row in members)
            {
                (string name, int color) = athletes[row.SaveAthleteId];
                roster.Add(new AdvanceRoundHandler.MemberRow(row.SaveAthleteId, name, color));
            }

            HashSet<string> seen = new(StringComparer.Ordinal);
            foreach (AdvanceRoundHandler.MemberRow row in roster)
            {
                if (!seen.Add(row.Name))
                {
                    throw new InvalidOperationException($"Type Cup team group {groupNumber} contains duplicate athlete '{row.Name}'.");
                }
            }

            roster.Sort(static (left, right) => string.Compare(left.Name, right.Name, StringComparison.Ordinal));
            rosters[groupNumber] = roster;
        }

        return rosters;
    }

    internal static async Task<Dictionary<int, Bonus>> LoadCupActiveBonusesAsync(
        SaveDbContext context,
        Dictionary<int, List<AdvanceRoundHandler.MemberRow>> rosters,
        SeasonEntity source,
        RulesV1 rules,
        CancellationToken cancellationToken)
    {
        List<AdvanceRoundHandler.MemberRow> combined = rosters.Values.SelectMany(r => r).ToList();
        Dictionary<int, int> seasonNumbers = await AdvanceRoundHandler.LoadSeasonNumbersAsync(context, source, cancellationToken).ConfigureAwait(false);
        if (!seasonNumbers.TryGetValue(source.Id, out int sourceSeasonNumber))
        {
            throw new InvalidOperationException($"Save has no season row for season {source.SeasonNumber}.");
        }

        int cupSeason = checked(sourceSeasonNumber + 1);
        List<StageStandingEntity> standings = await AdvanceRoundHandler.LoadBonusStandingsAsync(
            context, combined, AdvanceRoundHandler.SelectBonusSeasonIds(seasonNumbers, cupSeason), cancellationToken).ConfigureAwait(false);
        Dictionary<int, List<SimulationKernel.Scoring.BonusContribution>> contributions = AdvanceRoundHandler.GroupBonusContributions(
            combined, standings, seasonNumbers, cupSeason);

        // Cup uses the next-season Stage 1 boundary, identical to the selection
        // bonus component: source-season Stage 32 bonus enters at 80% decay.
        return AdvanceRoundHandler.ComputeStageStartBonuses(combined, contributions, cupSeason, 1, rules);
    }
}
