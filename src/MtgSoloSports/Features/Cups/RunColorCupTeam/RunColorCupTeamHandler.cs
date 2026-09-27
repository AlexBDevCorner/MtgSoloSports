using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Cups.SelectColorCupTeams;
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

        RulesV1 rules = await AdvanceRoundHandler.LoadRulesAsync(context, cancellationToken).ConfigureAwait(false);
        await AdvanceRoundHandler.LoadMetadataAsync(context, saveId, cancellationToken).ConfigureAwait(false);
        RngStateEntity rngRow = await AdvanceRoundHandler.LoadRngAsync(context, cancellationToken).ConfigureAwait(false);
        Pcg32State rngBefore = rngRow.ToState();

        SeasonEntity source = await LoadSourceSeasonAsync(context, sourceSeasonNumber, cancellationToken).ConfigureAwait(false);
        EnsureOddSeason(source);
        List<ColorCupSelectionEntity> selection = await LoadSelectionAsync(context, source, cancellationToken).ConfigureAwait(false);
        ColorCupSelectionInvariants.ValidatePersisted(source, selection, rules);
        ColorCupTeamInvariants.ValidateField(selection, rules);
        Dictionary<int, List<ColorCupSelectionEntity>> groups = PartitionGroups(selection, rules);
        ColorCupTeamInvariants.ValidateGroups(groups, rules);
        await EnsureTeamAbsentAsync(context, source, cancellationToken).ConfigureAwait(false);

        (int stageCountBefore, int seasonCountBefore, int roundCountBefore, long lifetimeBefore, long effectiveBefore, long championshipBefore) =
            await CapturePreservationAsync(context, cancellationToken).ConfigureAwait(false);

        Dictionary<int, List<AdvanceRoundHandler.MemberRow>> rosters = BuildRosters(context, groups);
        Dictionary<int, Bonus> activeBonuses = await LoadCupActiveBonusesAsync(
            context, rosters, source, rules, cancellationToken).ConfigureAwait(false);

        TeamSimulation simulation = SimulateTeam(groups, rosters, activeBonuses, rngBefore, source, rules);

        await PersistTeamAsync(context, source, simulation, cancellationToken).ConfigureAwait(false);
        context.ApplyRngState(simulation.RngAfter);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await ValidatePersistedAsync(
            context, source, simulation, rules,
            stageCountBefore, seasonCountBefore, roundCountBefore,
            lifetimeBefore, effectiveBefore, championshipBefore,
            cancellationToken).ConfigureAwait(false);
        await EmitTeamStoriesAsync(context, source, simulation, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return await BuildResponseAsync(_store, saveId, source, simulation, cancellationToken).ConfigureAwait(false);
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

    internal static async Task EnsureTeamAbsentAsync(
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

        bool hasRounds = await context.ColorCupTeamRounds.AnyAsync(
            e => e.SourceSeasonId == source.Id, cancellationToken).ConfigureAwait(false);
        if (hasRounds)
        {
            throw new InvalidOperationException($"Color Cup team event for Season {source.SeasonNumber} has corrupt partial rounds.");
        }

        bool hasHonour = await context.Honours.AnyAsync(
            e => e.SeasonId == source.Id && e.Kind == (int)Features.Records.HonourKind.ColorCupTeamChampion,
            cancellationToken).ConfigureAwait(false);
        if (hasHonour)
        {
            throw new InvalidOperationException($"Color Cup team event for Season {source.SeasonNumber} has corrupt partial honours.");
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
        List<StageStandingEntity> standings = await AdvanceRoundHandler.LoadBonusStandingsAsync(context, combined, cancellationToken).ConfigureAwait(false);
        Dictionary<int, List<SimulationKernel.Scoring.BonusContribution>> contributions = AdvanceRoundHandler.GroupBonusContributions(
            combined, standings, seasonNumbers, cupSeason);

        // Cup uses the next-season Stage 1 boundary, identical to the selection
        // bonus component: source-season Stage 32 bonus enters at 80% decay.
        return AdvanceRoundHandler.ComputeStageStartBonuses(combined, contributions, cupSeason, 1, rules);
    }
}
