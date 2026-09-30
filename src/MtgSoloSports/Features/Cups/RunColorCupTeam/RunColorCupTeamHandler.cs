using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Cups.SelectColorCupTeams;
using MtgSoloSports.Features.Seasons.SeasonLifecycle;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.FixedPoint;
using MtgSoloSports.SimulationKernel.Random;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Features.Cups.RunColorCupTeam;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Runs the four ranked
/// Color Cup team groups for a completed odd source season: all team #1
/// athletes compete against each other, then all #2, #3 and #4 athletes. Each
/// group holds exactly eight athletes (one per color) over exactly eight
/// rounds. Active career bonus at the next-season Stage 1 boundary applies
/// with normal fixed-point scoring/ranking; no new round or stage career
/// bonus is generated, no league championship points are awarded, and no
/// league <c>StageStanding</c>, <c>SeasonStanding</c> or <c>Round</c> rows are
/// created. The team score is the sum of the color's four legs' group scores
/// with deterministic tie-breaking (group-rank counts, aggregated round-place
/// counts, raw base totals, seeded draw). Persists 32 Cup round payloads plus
/// 32 leg standings plus 8 team standings (Gold for rank 1, Silver for rank 2,
/// Bronze for rank 3) plus four official team championship honours (one per
/// winning-team member) plus the RNG-after state, in one transaction. Holds
/// one per-save lock; read-only Cup queries never lock.
/// </summary>
public sealed partial class RunColorCupTeamHandler
{
    public const int TeamLeagueId = -1;
    public const int TeamLeagueKind = -2;
    public const string TeamLeagueName = "Color Cup Team";

    private readonly SaveStore _store;

    public RunColorCupTeamHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<RunColorCupTeamResponse> HandleAsync(
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

    internal async Task<RunColorCupTeamResponse> RunUnderLockAsync(
        Guid saveId,
        int? sourceSeasonNumber,
        CancellationToken cancellationToken)
    {
        await _store.EnsureMigratedAsync(saveId, cancellationToken).ConfigureAwait(false);
        using SaveDbContext context = _store.OpenDbContext(saveId);
        using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        TeamState state = await LoadStateAsync(context, saveId, sourceSeasonNumber, cancellationToken).ConfigureAwait(false);
        List<ColorCupTeamRoundPayloadDocument> payloads = new(state.Played);
        Pcg32State current = state.Rng;
        while (payloads.Count < state.Shape.TotalRounds)
        {
            (ColorCupTeamRoundPayloadDocument payload, Pcg32State after) = PlayRound(state, payloads, current);
            payloads.Add(payload);
            current = after;
        }

        TeamSimulation simulation = await CompleteAsync(context, state, payloads, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await BuildResponseAsync(_store, saveId, state.Source, simulation, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Loads everything a team round needs from persisted state and validates
    /// any partly played event (contiguous group rounds, unbroken RNG chain
    /// including group tie-break boundaries, RNG row untouched since the last
    /// step). Aborts on corruption.
    /// </summary>
    internal static async Task<TeamState> LoadStateAsync(
        SaveDbContext context,
        Guid saveId,
        int? sourceSeasonNumber,
        CancellationToken cancellationToken)
    {
        RulesV1 rules = await AdvanceRoundHandler.LoadRulesAsync(context, cancellationToken).ConfigureAwait(false);
        SaveMetadataEntity metadata = await AdvanceRoundHandler.LoadMetadataAsync(context, saveId, cancellationToken).ConfigureAwait(false);
        RngStateEntity rngRow = await AdvanceRoundHandler.LoadRngAsync(context, cancellationToken).ConfigureAwait(false);

        SeasonEntity source = await LoadSourceSeasonAsync(context, sourceSeasonNumber, cancellationToken).ConfigureAwait(false);
        EnsureOddSeason(source);
        List<ColorCupSelectionEntity> selection = await LoadSelectionAsync(context, source, cancellationToken).ConfigureAwait(false);
        ColorCupSelectionInvariants.ValidatePersisted(source, selection, rules);
        ColorCupTeamInvariants.ValidateField(selection, rules);
        Dictionary<int, List<ColorCupSelectionEntity>> groups = PartitionGroups(selection, rules);
        ColorCupTeamInvariants.ValidateGroups(groups, rules);
        await EnsureTeamUnresolvedAsync(context, source, cancellationToken).ConfigureAwait(false);

        (int stageCountBefore, int seasonCountBefore, int roundCountBefore, long lifetimeBefore, long effectiveBefore, long championshipBefore) =
            await CapturePreservationAsync(context, cancellationToken).ConfigureAwait(false);

        Dictionary<int, List<AdvanceRoundHandler.MemberRow>> rosters = BuildRosters(context, groups);
        Dictionary<int, Bonus> activeBonuses = await LoadCupActiveBonusesAsync(
            context, rosters, source, rules, cancellationToken).ConfigureAwait(false);

        List<ColorCupTeamRoundEntity> rows = await context.ColorCupTeamRounds
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .OrderBy(e => e.GroupNumber).ThenBy(e => e.RoundNumber)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<ColorCupTeamRoundPayloadDocument> played = rows.Select(r => ColorCupTeamRoundPayloadDocument.FromStored(r.PayloadJson)).ToList();
        Pcg32State rng = rngRow.ToState();
        TeamState state = new(
            rules, metadata, source, groups, rosters, activeBonuses, played, rng,
            stageCountBefore, seasonCountBefore, roundCountBefore, lifetimeBefore, effectiveBefore, championshipBefore);
        foreach (ColorCupTeamRoundPayloadDocument payload in played)
        {
            ColorCupTeamInvariants.ValidateRound(payload, rules, payload.RngBeforeState, payload.RngBeforeStream);
        }

        PostseasonEvents.ValidateInProgress(
            PostseasonEvents.ColorCupTeam,
            played.Select(p => new PostseasonEvents.PlayedRoundLink(
                p.GroupNumber,
                p.RoundNumber,
                new Pcg32State(p.RngBeforeState, p.RngBeforeStream),
                new Pcg32State(p.RngAfterState, p.RngAfterStream))).ToList(),
            state.Shape,
            group => RankGroupLeg(state, group, played.Where(p => p.GroupNumber == group).ToList()).RngAfter,
            rng);
        return state;
    }

    /// <summary>
    /// Completes the team event from all payloads (the source of truth) in the
    /// caller's transaction, in the same order as the one-shot run.
    /// </summary>
    internal static async Task<TeamSimulation> CompleteAsync(
        SaveDbContext context,
        TeamState state,
        List<ColorCupTeamRoundPayloadDocument> payloads,
        CancellationToken cancellationToken)
    {
        TeamSimulation simulation = FinishTeam(state, payloads);
        await PersistTeamAsync(context, state.Source, simulation, state.Played.Count, cancellationToken).ConfigureAwait(false);
        context.ApplyRngState(simulation.RngAfter);
        state.Metadata.Phase = Features.Saves.SavePhaseParser.ToText(Features.Saves.SavePhase.CupComplete);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await ValidatePersistedAsync(
            context, state.Source, simulation, state.Rules,
            state.StageCountBefore, state.SeasonCountBefore, state.RoundCountBefore,
            state.LifetimeBefore, state.EffectiveBefore, state.ChampionshipBefore,
            cancellationToken).ConfigureAwait(false);
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
                throw new RunColorCupTeamConflictException(
                    $"Season {sourceSeasonNumber.Value} is not a completed season ready for the Color Cup team event.");
            }

            return explicitSeason;
        }

        List<ColorCupSelectionEntity> any = await context.ColorCupSelections
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (any.Count == 0)
        {
            throw new RunColorCupTeamConflictException(
                "Color Cup team selection must be resolved before the team event can run.");
        }

        int latestSourceId = any.Max(e => e.SourceSeasonId);
        SeasonEntity? latest = await context.Seasons
            .SingleOrDefaultAsync(e => e.Id == latestSourceId, cancellationToken)
            .ConfigureAwait(false);
        if (latest is null || !latest.IsComplete)
        {
            throw new InvalidOperationException("Color Cup selection references an unknown or incomplete season.");
        }

        return latest;
    }

    internal static void EnsureOddSeason(SeasonEntity source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.SeasonNumber % 2 == 0)
        {
            throw new RunColorCupTeamConflictException(
                $"Season {source.SeasonNumber} is even; the Color Cup team event runs only for odd seasons (even seasons use the Type Cup).");
        }
    }

    internal static async Task<List<ColorCupSelectionEntity>> LoadSelectionAsync(
        SaveDbContext context,
        SeasonEntity source,
        CancellationToken cancellationToken)
    {
        List<ColorCupSelectionEntity> rows = await context.ColorCupSelections
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (rows.Count == 0)
        {
            throw new RunColorCupTeamConflictException(
                $"Color Cup team selection for Season {source.SeasonNumber} must be resolved before the team event can run.");
        }

        return rows;
    }

    internal static Dictionary<int, List<ColorCupSelectionEntity>> PartitionGroups(
        List<ColorCupSelectionEntity> selection,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(rules);
        Dictionary<int, List<ColorCupSelectionEntity>> groups = new(rules.ColorCupTeamSize);
        foreach (int groupNumber in Enumerable.Range(1, rules.ColorCupTeamSize))
        {
            groups[groupNumber] = new List<ColorCupSelectionEntity>(rules.ColorCupColorCount);
        }

        foreach (ColorCupSelectionEntity row in selection)
        {
            if (!groups.TryGetValue(row.SelectionRank, out List<ColorCupSelectionEntity>? members))
            {
                throw new InvalidOperationException($"Color Cup selection rank {row.SelectionRank} is out of range for team groups.");
            }

            members.Add(row);
        }

        return groups;
    }

    internal static async Task EnsureTeamUnresolvedAsync(
        SaveDbContext context,
        SeasonEntity source,
        CancellationToken cancellationToken)
    {
        bool hasTeams = await context.ColorCupTeamStandings.AnyAsync(
            e => e.SourceSeasonId == source.Id, cancellationToken).ConfigureAwait(false);
        if (hasTeams)
        {
            throw new RunColorCupTeamConflictException(
                $"Color Cup team event for Season {source.SeasonNumber} has already been resolved.");
        }

        bool hasLegs = await context.ColorCupTeamGroupStandings.AnyAsync(
            e => e.SourceSeasonId == source.Id, cancellationToken).ConfigureAwait(false);
        if (hasLegs)
        {
            throw new InvalidOperationException($"Color Cup team event for Season {source.SeasonNumber} has corrupt partial legs.");
        }

        bool hasHonour = await context.Honours.AnyAsync(
            e => e.SeasonId == source.Id && e.Kind == (int)Features.Records.HonourKind.ColorCupTeamChampion,
            cancellationToken).ConfigureAwait(false);
        if (hasHonour)
        {
            throw new InvalidOperationException($"Color Cup team event for Season {source.SeasonNumber} has corrupt partial honours.");
        }

        string? other = await PostseasonEvents.FindOtherInProgressAsync(context, PostseasonEvents.ColorCupTeam, source.Id, cancellationToken).ConfigureAwait(false);
        if (other is not null)
        {
            throw new RunColorCupTeamConflictException(
                $"{other} is partly played; finish it before playing the {PostseasonEvents.Title(PostseasonEvents.ColorCupTeam)}.");
        }
    }

    internal static Dictionary<int, List<AdvanceRoundHandler.MemberRow>> BuildRosters(
        SaveDbContext context,
        Dictionary<int, List<ColorCupSelectionEntity>> groups)
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
            throw new InvalidOperationException("Color Cup team selection references unknown athletes.");
        }

        Dictionary<int, List<AdvanceRoundHandler.MemberRow>> rosters = new(groups.Count);
        foreach ((int groupNumber, List<ColorCupSelectionEntity> members) in groups)
        {
            List<AdvanceRoundHandler.MemberRow> roster = new(members.Count);
            foreach (ColorCupSelectionEntity row in members)
            {
                (string name, int color) = athletes[row.SaveAthleteId];
                roster.Add(new AdvanceRoundHandler.MemberRow(row.SaveAthleteId, name, color));
            }

            HashSet<string> seen = new(StringComparer.Ordinal);
            foreach (AdvanceRoundHandler.MemberRow row in roster)
            {
                if (!seen.Add(row.Name))
                {
                    throw new InvalidOperationException($"Color Cup team group {groupNumber} contains duplicate athlete '{row.Name}'.");
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
