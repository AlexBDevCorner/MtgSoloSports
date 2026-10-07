using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;
using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.SimulationKernel.Rules;
using MtgSoloSports.SimulationKernel.Scoring;

namespace MtgSoloSports.Features.Cups.SelectColorCupTeams;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Selects four Color Cup
/// representatives per sporting color for one completed odd source season using
/// the 35/30/25/10 formula (effective bonus, completed-season performance,
/// recent form over the latest ten league stages weighted 1..10 oldest-newest,
/// career prestige). Candidates are all save athletes grouped by their original
/// sporting color, so Superleague athletes represent their original color.
/// All normalized/weighted math is fixed-point integer-only with deterministic
/// tie-breaking and consumes no sporting RNG. Persists 32 rows (8 x 4) with
/// raw inputs plus normalized components plus final ratings in one transaction.
/// Holds one per-save lock; read-only selection queries never lock.
/// </summary>
public sealed partial class SelectColorCupTeamsHandler
{
    private readonly SaveStore _store;

    public SelectColorCupTeamsHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<SelectColorCupTeamsResponse> HandleAsync(
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
            return await SelectUnderLockAsync(saveId, sourceSeasonNumber, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task<SelectColorCupTeamsResponse> SelectUnderLockAsync(
        Guid saveId,
        int? sourceSeasonNumber,
        CancellationToken cancellationToken)
    {
        await _store.EnsureMigratedAsync(saveId, cancellationToken).ConfigureAwait(false);
        using SaveDbContext context = _store.OpenDbContext(saveId);
        using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        RulesV1 rules = await AdvanceRoundHandler.LoadRulesAsync(context, cancellationToken).ConfigureAwait(false);
        SaveMetadataEntity metadata = await AdvanceRoundHandler.LoadMetadataAsync(context, saveId, cancellationToken).ConfigureAwait(false);
        SeasonEntity source = await LoadSourceSeasonAsync(context, sourceSeasonNumber, cancellationToken).ConfigureAwait(false);
        EnsureOddSeason(source);
        await EnsureNotAlreadySelectedAsync(context, source, cancellationToken).ConfigureAwait(false);

        SelectionInputs inputs = await LoadInputsAsync(context, source, rules, cancellationToken).ConfigureAwait(false);
        (Dictionary<int, IReadOnlyList<ColorCupSelection.ScoredCandidate>> rankings, Dictionary<int, CupSelectionMetrics.CupMetrics> metrics, Dictionary<int, CupPrestigeCalculator.PrestigeBreakdown> prestige) =
            RankAllColors(inputs, rules);
        IReadOnlyList<ColorCupSelection.ScoredCandidate> all = TakeTeams(rankings, rules);
        AddReport(context, source, BuildReport(rankings, metrics, prestige, inputs, rules), rules);
        await PersistSelectionsAsync(context, source, all, rules, cancellationToken).ConfigureAwait(false);

        List<ColorCupSelectionEntity> persisted = await LoadPersistedAsync(context, source, cancellationToken).ConfigureAwait(false);
        ColorCupSelectionInvariants.ValidatePersisted(source, persisted, rules);
        metadata.Phase = Features.Saves.SavePhaseParser.ToText(Features.Saves.SavePhase.CupSelectionResolved);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return await BuildResponseAsync(_store, saveId, source, rules, cancellationToken).ConfigureAwait(false);
    }

    internal sealed record SelectionInputs(
        List<SaveAthleteEntity> Athletes,
        Dictionary<int, int> SeasonNumbers,
        List<StageStandingEntity> StageRows,
        Dictionary<int, int> PerformanceByAthlete,
        List<HonourEntity> Honours,
        List<SeasonMembershipEntity> Memberships,
        Dictionary<int, int> LeagueKinds,
        Dictionary<int, int> LeagueLevels,
        int SourceSeasonId,
        int SourceSeasonNumber,
        Dictionary<int, int> SourceLeagueIdByAthlete,
        Dictionary<int, int> SourceLeagueLevels,
        Dictionary<int, string> SourceLeagueNames);

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
                throw new SelectColorCupTeamsConflictException(
                    $"Season {sourceSeasonNumber.Value} is not a completed season ready for Color Cup selection.");
            }

            return explicitSeason;
        }

        SeasonEntity? latest = await context.Seasons
            .Where(e => e.IsComplete)
            .OrderByDescending(e => e.SeasonNumber)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (latest is null)
        {
            throw new SelectColorCupTeamsConflictException("No completed season is ready for Color Cup selection.");
        }

        return latest;
    }

    internal static void EnsureOddSeason(SeasonEntity source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.SeasonNumber % 2 == 0)
        {
            throw new SelectColorCupTeamsConflictException(
                $"Season {source.SeasonNumber} is even; Color Cup selection runs only for odd seasons (even seasons use the Type Cup).");
        }
    }

    internal static async Task EnsureNotAlreadySelectedAsync(
        SaveDbContext context,
        SeasonEntity source,
        CancellationToken cancellationToken)
    {
        bool exists = await context.ColorCupSelections
            .AnyAsync(e => e.SourceSeasonId == source.Id, cancellationToken)
            .ConfigureAwait(false);
        if (exists)
        {
            throw new SelectColorCupTeamsConflictException(
                $"Color Cup selection for Season {source.SeasonNumber} has already been resolved.");
        }
    }

    internal static async Task<SelectionInputs> LoadInputsAsync(
        SaveDbContext context,
        SeasonEntity source,
        RulesV1 rules,
        CancellationToken cancellationToken)
    {
        List<SaveAthleteEntity> athletes = await LoadAthletesAsync(context, rules, cancellationToken).ConfigureAwait(false);
        Dictionary<int, int> seasonNumbers = await LoadSeasonNumbersAsync(context, source, cancellationToken).ConfigureAwait(false);
        List<StageStandingEntity> stageRows = await LoadStageRowsAsync(context, seasonNumbers, cancellationToken).ConfigureAwait(false);
        Dictionary<int, int> performance = await LoadPerformanceAsync(context, source, cancellationToken).ConfigureAwait(false);
        List<HonourEntity> honours = await LoadHonoursAsync(context, source, cancellationToken).ConfigureAwait(false);
        (List<SeasonMembershipEntity> memberships, Dictionary<int, int> leagueKinds, Dictionary<int, int> leagueLevels) =
            await LoadMembershipInputsAsync(context, seasonNumbers, cancellationToken).ConfigureAwait(false);
        (Dictionary<int, int> leagueIdByAthlete, Dictionary<int, int> levels, Dictionary<int, string> names) =
            await LoadSourceLeagueInputsAsync(context, source, athletes, cancellationToken).ConfigureAwait(false);
        return new SelectionInputs(
            athletes, seasonNumbers, stageRows, performance, honours, memberships, leagueKinds, leagueLevels,
            source.Id, source.SeasonNumber, leagueIdByAthlete, levels, names);
    }

    internal static async Task<List<SaveAthleteEntity>> LoadAthletesAsync(
        SaveDbContext context,
        RulesV1 rules,
        CancellationToken cancellationToken)
    {
        List<SaveAthleteEntity> athletes = await context.SaveAthletes
            .AsNoTracking()
            .OrderBy(e => e.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (athletes.Count != rules.TotalAthletesInSave)
        {
            throw new InvalidOperationException($"Save must hold exactly {rules.TotalAthletesInSave} athletes, was {athletes.Count}.");
        }

        return athletes;
    }

    internal static async Task<Dictionary<int, int>> LoadSeasonNumbersAsync(
        SaveDbContext context,
        SeasonEntity source,
        CancellationToken cancellationToken)
    {
        List<SeasonEntity> seasons = await context.Seasons
            .AsNoTracking()
            .Where(e => e.SeasonNumber <= source.SeasonNumber)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!seasons.Any(e => e.Id == source.Id))
        {
            throw new InvalidOperationException($"Save is missing source season {source.SeasonNumber}.");
        }

        return seasons.ToDictionary(e => e.Id, e => e.SeasonNumber);
    }

    internal static async Task<List<StageStandingEntity>> LoadStageRowsAsync(
        SaveDbContext context,
        Dictionary<int, int> seasonNumbers,
        CancellationToken cancellationToken)
    {
        HashSet<int> seasonIds = seasonNumbers.Keys.ToHashSet();
        return await context.StageStandings
            .AsNoTracking()
            .Where(e => seasonIds.Contains(e.SeasonId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<Dictionary<int, int>> LoadPerformanceAsync(
        SaveDbContext context,
        SeasonEntity source,
        CancellationToken cancellationToken)
    {
        List<SeasonStandingEntity> rows = await context.SeasonStandings
            .AsNoTracking()
            .Where(e => e.SeasonId == source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        Dictionary<int, int> map = new(rows.Count);
        foreach (SeasonStandingEntity row in rows)
        {
            if (row.TotalChampionshipPointsThousandths < 0)
            {
                throw new InvalidOperationException($"Season standing {row.Id} has corrupt negative championship points.");
            }

            map[row.SaveAthleteId] = row.TotalChampionshipPointsThousandths;
        }

        return map;
    }

    internal static async Task<List<HonourEntity>> LoadHonoursAsync(
        SaveDbContext context,
        SeasonEntity source,
        CancellationToken cancellationToken)
    {
        return await context.Honours
            .AsNoTracking()
            .Where(e => e.SeasonNumber <= source.SeasonNumber)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<(List<SeasonMembershipEntity> Memberships, Dictionary<int, int> LeagueKinds, Dictionary<int, int> LeagueLevels)> LoadMembershipInputsAsync(
        SaveDbContext context,
        Dictionary<int, int> seasonNumbers,
        CancellationToken cancellationToken)
    {
        HashSet<int> seasonIds = seasonNumbers.Keys.ToHashSet();
        List<SeasonMembershipEntity> memberships = await context.SeasonMemberships
            .AsNoTracking()
            .Where(e => seasonIds.Contains(e.SeasonId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<LeagueEntity> leagues = await context.Leagues
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        Dictionary<int, int> kinds = new(leagues.Count);
        Dictionary<int, int> levels = new(leagues.Count);
        foreach (LeagueEntity league in leagues)
        {
            kinds[league.Id] = league.Kind;
            levels[league.Id] = (int)LeagueEntityLevels.GetLevel(league);
        }

        return (memberships, kinds, levels);
    }

    internal static async Task<(Dictionary<int, int> LeagueIdByAthlete, Dictionary<int, int> Levels, Dictionary<int, string> Names)> LoadSourceLeagueInputsAsync(
        SaveDbContext context,
        SeasonEntity source,
        List<SaveAthleteEntity> athletes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(athletes);
        List<LeagueEntity> leagues = await context.Leagues
            .AsNoTracking()
            .Where(e => e.SeasonId == source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        Dictionary<int, int> levels = new(leagues.Count);
        Dictionary<int, string> names = new(leagues.Count);
        foreach (LeagueEntity league in leagues)
        {
            levels[league.Id] = (int)LeagueEntityLevels.GetLevel(league);
            names[league.Id] = league.Name;
        }

        Dictionary<int, int?> membershipLeague = await context.SeasonMemberships
            .AsNoTracking()
            .Where(e => e.SeasonId == source.Id)
            .ToDictionaryAsync(e => e.SaveAthleteId, e => e.LeagueId, cancellationToken)
            .ConfigureAwait(false);
        Dictionary<int, int> leagueIdByAthlete = new(athletes.Count);
        foreach (SaveAthleteEntity athlete in athletes)
        {
            if (membershipLeague.TryGetValue(athlete.Id, out int? leagueId) && leagueId.HasValue)
            {
                if (!levels.ContainsKey(leagueId.Value))
                {
                    throw new InvalidOperationException(
                        $"Source season {source.SeasonNumber} membership for athlete {athlete.Id} references unknown league {leagueId.Value}.");
                }

                leagueIdByAthlete[athlete.Id] = leagueId.Value;
            }
        }

        return (leagueIdByAthlete, levels, names);
    }

    internal static IReadOnlyList<ColorCupSelection.ScoredCandidate> ScoreAllColors(SelectionInputs inputs, RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(rules);
        (Dictionary<int, IReadOnlyList<ColorCupSelection.ScoredCandidate>> rankings, _, _) = RankAllColors(inputs, rules);
        return TakeTeams(rankings, rules);
    }

    internal static void ValidateEightColors(Dictionary<int, List<ColorCupSelection.CandidateRaw>> byColor, RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(byColor);
        ArgumentNullException.ThrowIfNull(rules);
        if (byColor.Count != rules.ColorCupColorCount)
        {
            throw new InvalidOperationException($"Color Cup selection must cover exactly {rules.ColorCupColorCount} sporting colors, was {byColor.Count}.");
        }
    }
}
