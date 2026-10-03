using Microsoft.EntityFrameworkCore;
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
/// there is no artificial maximum. Ratings reuse the 35/30/25/10 Cup formula
/// (effective bonus, completed-season performance, recent form over the latest
/// ten league stages weighted 1..10 oldest-newest, career prestige) normalized
/// globally across active candidates with fixed-point integer-only math and no
/// sporting RNG. Persists one row per allocated athlete with raw inputs plus
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

    public SelectTypeCupTeamsHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
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
        try
        {
            return await SelectUnderLockAsync(saveId, sourceSeasonNumber, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
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
        SelectionInputs inputs = await LoadInputsAsync(context, source, cancellationToken).ConfigureAwait(false);
        List<TypeCupAllocation.CandidateRaw> candidates = BuildCandidates(inputs, rules);
        TypeCupAllocation.AllocationResult allocation = TypeCupAllocation.Allocate(candidates, rules);
        Dictionary<int, string> names = inputs.ActiveAthletes.ToDictionary(e => e.Id, e => e.Name);
        return MapResponse(saveId, source, rules, allocation, names);
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

        SelectionInputs inputs = await LoadInputsAsync(context, source, cancellationToken).ConfigureAwait(false);
        List<TypeCupAllocation.CandidateRaw> candidates = BuildCandidates(inputs, rules);
        TypeCupAllocationInsight.Result insight = TypeCupAllocationInsight.Allocate(candidates, rules);
        TypeCupAllocation.AllocationResult allocation = insight.Allocation;
        AddReport(context, source, BuildReport(insight, rules), rules);
        await PersistSelectionsAsync(context, source, allocation, rules, cancellationToken).ConfigureAwait(false);

        List<TypeCupSelectionEntity> persisted = await LoadPersistedAsync(context, source, cancellationToken).ConfigureAwait(false);
        TypeCupSelectionInvariants.ValidatePersisted(source, persisted, rules);
        await ValidateNationalityUntouchedAsync(context, persisted, cancellationToken).ConfigureAwait(false);
        metadata.Phase = Features.Saves.SavePhaseParser.ToText(Features.Saves.SavePhase.CupSelectionResolved);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return await BuildResponseAsync(_store, saveId, source, rules, cancellationToken).ConfigureAwait(false);
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
        List<HonourEntity> honours = await LoadHonoursAsync(context, source, cancellationToken).ConfigureAwait(false);
        (List<SeasonMembershipEntity> memberships, Dictionary<int, int> leagueKinds) =
            await LoadMembershipInputsAsync(context, seasonNumbers, cancellationToken).ConfigureAwait(false);
        return new SelectionInputs(active, seasonNumbers, stageRows, performance, honours, memberships, leagueKinds);
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

    internal static async Task<(List<SeasonMembershipEntity> Memberships, Dictionary<int, int> LeagueKinds)> LoadMembershipInputsAsync(
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
        Dictionary<int, int> kinds = await context.Leagues
            .AsNoTracking()
            .ToDictionaryAsync(e => e.Id, e => e.Kind, cancellationToken)
            .ConfigureAwait(false);
        return (memberships, kinds);
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
                    member.PrestigeNormThousandths,
                    member.BonusRawThousandths,
                    member.PerformanceRawThousandths,
                    member.FormRaw,
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
            rules.CupPrestigeWeightPermille,
            teams);
    }
}
