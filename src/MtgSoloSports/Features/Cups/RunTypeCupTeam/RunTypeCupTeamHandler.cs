using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Cups.SelectTypeCupTeams;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.FixedPoint;
using MtgSoloSports.SimulationKernel.Random;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Features.Cups.RunTypeCupTeam;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Runs the four ranked
/// Type Cup team groups for a completed even source season: all team #1
/// athletes compete against each other, then all #2, #3 and #4 athletes. Each
/// group holds exactly N athletes (one per participating creature type at the
/// same selection rank, where N is the dynamically varying team count) over
/// exactly eight rounds. Only the Color Cup has a fixed eight-team field; this
/// slice never assumes exactly eight teams. Active career bonus at the next-season
/// Stage 1 boundary applies with normal fixed-point scoring/ranking; no new round
/// or stage career bonus is generated, no league championship points are awarded,
/// and no league <c>StageStanding</c>, <c>SeasonStanding</c> or <c>Round</c> rows
/// are created. The team score is the sum of the type's four legs' group scores
/// with deterministic tie-breaking (group-rank counts, aggregated round-place
/// counts, raw base totals, seeded draw). Persists 32 Cup round payloads plus
/// N x 4 leg standings plus N team standings (Gold for rank 1, Silver for rank 2,
/// Bronze for rank 3) plus four official team championship honours (one per
/// winning-team member) plus permanent nationality for uncapped participants
/// plus the RNG-after state, in one transaction. Holds
/// one per-save lock; read-only Cup queries never lock.
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
        // Resume-capable full run (MSS-045): loops the same single-round advance
        // core as the Next Round endpoint, so an unfinished round-by-round event
        // completes with byte-identical results instead of failing on its own
        // partial progress. Each round commits with its RNG state in its own
        // transaction; finalization (official standings, honours, nationality,
        // stories) happens exactly once inside the final round's transaction.
        await _store.EnsureMigratedAsync(saveId, cancellationToken).ConfigureAwait(false);
        AdvanceTypeCupTeamRound.AdvanceTypeCupTeamRoundHandler advance = new(_store);
        AdvanceTypeCupTeamRound.AdvanceTypeCupTeamRoundHandler.AdvanceOutcome? outcome = null;
        for (int attempt = 0; ; attempt++)
        {
            outcome = await advance.AdvanceCoreAsync(saveId, sourceSeasonNumber, cancellationToken).ConfigureAwait(false);
            if (outcome.CompletedSimulation is not null)
            {
                break;
            }

            if (attempt + 1 >= outcome.TotalRounds)
            {
                throw new InvalidOperationException("Type Cup team event did not complete after simulating all scheduled rounds.");
            }
        }

        using SaveDbContext context = _store.OpenDbContext(saveId);
        SeasonEntity? source = await context.Seasons
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.Id == outcome.SourceSeasonId, cancellationToken)
            .ConfigureAwait(false);
        if (source is null)
        {
            throw new InvalidOperationException("Type Cup team event references an unknown season.");
        }

        return await BuildResponseAsync(_store, saveId, source, outcome.CompletedSimulation, cancellationToken).ConfigureAwait(false);
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
