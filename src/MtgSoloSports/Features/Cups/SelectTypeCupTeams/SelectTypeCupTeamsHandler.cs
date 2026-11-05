using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Reports.GoogleDrivePublishing;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Features.Cups.SelectTypeCupTeams;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Allocates deterministic
/// four-athlete creature-type teams for one completed even source season (Game
/// Rules §15, Technical Design §17). Eligible candidates are currently active
/// athletes only (source-season membership with a league); common-pool athletes
/// never participate. Capped athletes may represent only their permanent Type
/// Cup nationality; uncapped athletes may initially represent any printed
/// creature type and prefer the type where they rank higher (for example #1
/// Wizard over #4 Human). A deterministic exact maximum-cardinality global
/// allocation with backtracking maximizes the number of valid
/// four-distinct-athlete teams with ordinal tie-breaking; only types that
/// actually receive four participate and
/// there is no artificial maximum. Ratings reuse the 35/30/20/5/10 Cup formula
/// (effective bonus, completed-season performance, recent form over the latest
/// ten league stages weighted 1..10 oldest-newest, Chaos form, career prestige)
/// normalized globally across active candidates with fixed-point integer-only
/// math and no sporting RNG. Persists one row per allocated athlete with raw inputs plus
/// normalized components plus final rating plus selection/type ranks in one
/// transaction. Persisting the allocation never sets
/// <c>SaveAthletes.TypeCupNationality</c>; nationality becomes permanent only
/// when an athlete actually participates (a later slice), so allocation previews
/// and selections leave capped state untouched. Holds one per-save lock;
/// read-only allocation queries never lock.
/// </summary>
public sealed partial class SelectTypeCupTeamsHandler
{
    private readonly SaveStore _store;
    private readonly DriveAutoPublishNotifier? _autoPublish;

    public SelectTypeCupTeamsHandler(SaveStore store, DriveAutoPublishNotifier? autoPublish = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _autoPublish = autoPublish;
    }

    public async Task<SelectTypeCupTeamsResponse> HandleAsync(
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
        SelectTypeCupTeamsResponse response;
        try
        {
            response = await SelectUnderLockAsync(saveId, sourceSeasonNumber, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }

        // MSS-091: selection creates the Type Cup edition and team-history
        // reports. Post-commit checkpoint note only (local file write, never
        // Google calls); the automatic publisher refreshes the affected
        // reports. Never throws, never fails the sporting command.
        if (_autoPublish is not null)
        {
            await _autoPublish.NotifyStageCheckpointAsync(saveId, CancellationToken.None).ConfigureAwait(false);
        }

        return response;
    }

    /// <summary>
    /// Deterministic allocation preview: runs the same eligibility, scoring and
    /// matching as <see cref="HandleAsync"/> but persists nothing and never sets
    /// permanent nationality. Read-only; never takes the per-save lock.
    /// </summary>
    public async Task<SelectTypeCupTeamsResponse> PreviewAsync(
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

        await _store.EnsureMigratedAsync(saveId, cancellationToken).ConfigureAwait(false);
        using SaveDbContext context = _store.OpenDbContext(saveId);
        RulesV1 rules = await AdvanceRoundHandler.LoadRulesAsync(context, cancellationToken).ConfigureAwait(false);
        await AdvanceRoundHandler.LoadMetadataAsync(context, saveId, cancellationToken).ConfigureAwait(false);
        SeasonEntity source = await LoadSourceSeasonAsync(context, sourceSeasonNumber, cancellationToken).ConfigureAwait(false);
        EnsureEvenSeason(source);
        // MSS-132: preview resolves the same source-season effective formula as
        // the mutating path but persists nothing and never advances activation.
        CupSelectionFormulaActivationEntity previewActivation =
            await SelectionFormula.CupSelectionFormulaLifecycle.LoadActivationAsync(context, cancellationToken).ConfigureAwait(false);
        RulesV1 previewEffective = SelectionFormula.CupSelectionFormulaLifecycle.ToEffectiveRules(rules, previewActivation, source.SeasonNumber);
        SelectionInputs inputs = await LoadInputsAsync(context, source, cancellationToken).ConfigureAwait(false);
        IReadOnlyDictionary<int, MtgSoloSports.SimulationKernel.Careers.CareerPerformanceScorer.AthleteCareerScore> career =
            await LoadCareerPrestigeAsync(
                context,
                source.SeasonNumber,
                inputs.ActiveAthletes.Select(e => e.Id).ToList(),
                cancellationToken).ConfigureAwait(false);
        Dictionary<int, int> chaos = await LoadChaosRawsAsync(context, source, cancellationToken).ConfigureAwait(false);
        List<TypeCupAllocation.CandidateRaw> candidates = BuildCandidates(inputs, previewEffective, career, chaos);
        TypeCupAllocation.AllocationResult allocation = TypeCupAllocation.Allocate(candidates, previewEffective);
        Dictionary<int, string> names = inputs.ActiveAthletes.ToDictionary(e => e.Id, e => e.Name);
        return MapResponse(saveId, source, previewEffective, allocation, names);
    }

    internal async Task<SelectTypeCupTeamsResponse> SelectUnderLockAsync(
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
        EnsureEvenSeason(source);
        await EnsureNotAlreadySelectedAsync(context, source, cancellationToken).ConfigureAwait(false);

        // MSS-132: same forward-only activation as the Color Cup path; current
        // source keeps its snapshot formula, future sources use 35/30/20/5/10.
        CupSelectionFormulaActivationEntity activation =
            await SelectionFormula.CupSelectionFormulaLifecycle.EnsureActivationAsync(context, cancellationToken).ConfigureAwait(false);
        RulesV1 effective = SelectionFormula.CupSelectionFormulaLifecycle.ToEffectiveRules(rules, activation, source.SeasonNumber);

        SelectionInputs inputs = await LoadInputsAsync(context, source, cancellationToken).ConfigureAwait(false);
        IReadOnlyDictionary<int, MtgSoloSports.SimulationKernel.Careers.CareerPerformanceScorer.AthleteCareerScore> career =
            await LoadCareerPrestigeAsync(
                context,
                source.SeasonNumber,
                inputs.ActiveAthletes.Select(e => e.Id).ToList(),
                cancellationToken).ConfigureAwait(false);
        Dictionary<int, int> chaos = await LoadChaosRawsAsync(context, source, cancellationToken).ConfigureAwait(false);
        List<TypeCupAllocation.CandidateRaw> candidates = BuildCandidates(inputs, effective, career, chaos);
        TypeCupAllocationInsight.Result insight = TypeCupAllocationInsight.Allocate(candidates, effective);
        TypeCupAllocation.AllocationResult allocation = insight.Allocation;
        Dictionary<int, List<MtgSoloSports.SimulationKernel.Cups.ColorCupSelection.StageFormEntry>> stagesByAthlete = GroupStages(inputs);
        Dictionary<int, MtgSoloSports.SimulationKernel.Cups.CupSelectionMetrics.CupMetrics> metrics = BuildMetricsMap(inputs, effective, stagesByAthlete);
        AddReport(context, source, BuildReport(insight, metrics, career, inputs, effective), effective);
        await PersistSelectionsAsync(context, source, allocation, effective, cancellationToken).ConfigureAwait(false);

        List<TypeCupSelectionEntity> persisted = await LoadPersistedAsync(context, source, cancellationToken).ConfigureAwait(false);
        TypeCupSelectionInvariants.ValidatePersisted(source, persisted, effective);
        await ValidateNationalityUntouchedAsync(context, persisted, cancellationToken).ConfigureAwait(false);
        metadata.Phase = Features.Saves.SavePhaseParser.ToText(Features.Saves.SavePhase.CupSelectionResolved);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return await BuildResponseAsync(_store, saveId, source, effective, cancellationToken).ConfigureAwait(false);
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
                throw new SelectTypeCupTeamsConflictException(
                    $"Season {sourceSeasonNumber.Value} is not a completed season ready for Type Cup allocation.");
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
            throw new SelectTypeCupTeamsConflictException("No completed season is ready for Type Cup allocation.");
        }

        return latest;
    }

    internal static void EnsureEvenSeason(SeasonEntity source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.SeasonNumber % 2 == 1)
        {
            throw new SelectTypeCupTeamsConflictException(
                $"Season {source.SeasonNumber} is odd; Type Cup allocation runs only for even seasons (odd seasons use the Color Cup).");
        }
    }

    internal static async Task EnsureNotAlreadySelectedAsync(
        SaveDbContext context,
        SeasonEntity source,
        CancellationToken cancellationToken)
    {
        bool exists = await context.TypeCupSelections
            .AnyAsync(e => e.SourceSeasonId == source.Id, cancellationToken)
            .ConfigureAwait(false);
        if (exists)
        {
            throw new SelectTypeCupTeamsConflictException(
                $"Type Cup allocation for Season {source.SeasonNumber} has already been resolved.");
        }
    }

    internal static async Task<SelectionInputs> LoadInputsAsync(
        SaveDbContext context,
        SeasonEntity source,
        CancellationToken cancellationToken)
    {
        List<SaveAthleteEntity> active = await LoadActiveAthletesAsync(context, source, cancellationToken).ConfigureAwait(false);
        Dictionary<int, int> seasonNumbers = await LoadSeasonNumbersAsync(context, source, cancellationToken).ConfigureAwait(false);
        List<StageStandingEntity> stageRows = await LoadStageRowsAsync(context, seasonNumbers, cancellationToken).ConfigureAwait(false);
        Dictionary<int, int> performance = await LoadPerformanceAsync(context, source, cancellationToken).ConfigureAwait(false);
        (Dictionary<int, int> leagueIdByAthlete, Dictionary<int, int> levels, Dictionary<int, string> names) =
            await LoadSourceLeagueInputsAsync(context, source, active, cancellationToken).ConfigureAwait(false);
        return new SelectionInputs(
            active, seasonNumbers, stageRows, performance,
            source.Id, source.SeasonNumber, leagueIdByAthlete, levels, names);
    }

    internal static async Task<List<SaveAthleteEntity>> LoadActiveAthletesAsync(
        SaveDbContext context,
        SeasonEntity source,
        CancellationToken cancellationToken)
    {
        List<int> activeIds = await context.SeasonMemberships
            .AsNoTracking()
            .Where(e => e.SeasonId == source.Id && e.LeagueId != null)
            .Select(e => e.SaveAthleteId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (activeIds.Count == 0)
        {
            return [];
        }

        HashSet<int> distinct = activeIds.ToHashSet();
        if (distinct.Count != activeIds.Count)
        {
            throw new InvalidOperationException($"Season {source.SeasonNumber} has duplicate active memberships.");
        }

        List<SaveAthleteEntity> athletes = await context.SaveAthletes
            .AsNoTracking()
            .Where(e => distinct.Contains(e.Id))
            .OrderBy(e => e.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (athletes.Count != distinct.Count)
        {
            throw new InvalidOperationException($"Season {source.SeasonNumber} references unknown athletes.");
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

    internal static async Task ValidateNationalityUntouchedAsync(
        SaveDbContext context,
        List<TypeCupSelectionEntity> persisted,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(persisted);
        if (persisted.Count == 0)
        {
            return;
        }

        HashSet<int> selected = persisted.Select(r => r.SaveAthleteId).ToHashSet();
        List<SaveAthleteEntity> athletes = await context.SaveAthletes
            .Where(e => selected.Contains(e.Id))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (SaveAthleteEntity athlete in athletes)
        {
            TypeCupSelectionEntity row = persisted.First(r => r.SaveAthleteId == athlete.Id);
            if (athlete.TypeCupNationality is not null
                && !string.Equals(athlete.TypeCupNationality.Trim(), row.CreatureType, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Athlete '{athlete.Name}' is capped for '{athlete.TypeCupNationality}' but allocated to '{row.CreatureType}'.");
            }
        }
    }

    internal static SelectTypeCupTeamsResponse MapResponse(
        Guid saveId,
        SeasonEntity source,
        RulesV1 rules,
        TypeCupAllocation.AllocationResult allocation,
        Dictionary<int, string> names)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(allocation);
        ArgumentNullException.ThrowIfNull(names);
        List<TypeCupTeamResult> teams = new(allocation.Teams.Count);
        foreach (TypeCupAllocation.AllocatedTeam team in allocation.Teams)
        {
            List<TypeCupTeamMember> members = new(team.Members.Count);
            foreach (TypeCupAllocation.AllocatedMember member in team.Members)
            {
                names.TryGetValue(member.AthleteId, out string? name);
                members.Add(new TypeCupTeamMember(
                    member.AthleteId,
                    name ?? member.Name,
                    team.CreatureType,
                    member.SelectionRank,
                    member.TypeRank,
                    member.FinalRatingThousandths,
                    member.BonusNormThousandths,
                    member.PerformanceNormThousandths,
                    member.FormNormThousandths,
                    member.ChaosNormThousandths,
                    member.PrestigeNormThousandths,
                    member.BonusRawThousandths,
                    member.PerformanceRawThousandths,
                    member.FormRaw,
                    member.ChaosRaw,
                    member.PrestigeRaw));
            }

            teams.Add(new TypeCupTeamResult(team.CreatureType, members));
        }

        return new SelectTypeCupTeamsResponse(
            saveId,
            source.SeasonNumber,
            source.Id,
            rules.Version,
            teams.Sum(t => t.Members.Count),
            teams.Count,
            rules.CupBonusWeightPermille,
            rules.CupPerformanceWeightPermille,
            rules.CupFormWeightPermille,
            rules.CupChaosWeightPermille,
            rules.CupPrestigeWeightPermille,
            teams);
    }
}
