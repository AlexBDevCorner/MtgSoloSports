using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Cups.RunTypeCupTeam;
using MtgSoloSports.Features.Cups.SelectTypeCupTeams;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.SimulationKernel.Random;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Features.Cups.DrawTypeCupQualificationGroups;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Persists the random Type Cup
/// qualification draw for one completed even source season (MSS-061).
/// The draw runs after squad allocation and before qualification rounds: squad
/// membership never changes with group assignment and permanent nationality
/// eligibility from MSS-055 is untouched. For at most 32 selected teams there is
/// no qualification stage and no draw rows (direct Final). For larger fields all
/// selected teams are randomly distributed into balanced groups of at most 32
/// using only the versioned save RNG, with exactly 32 Final places allocated as
/// evenly as possible. The RNG commit and the draw share one transaction so
/// reload/history never redraws. Holds one per-save lock; reads never lock.
/// </summary>
public sealed class DrawTypeCupQualificationGroupsHandler
{
    private readonly SaveStore _store;

    public DrawTypeCupQualificationGroupsHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<DrawTypeCupQualificationGroupsResponse> HandleAsync(
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
            return await DrawUnderLockAsync(saveId, sourceSeasonNumber, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Read-only draw query: returns the persisted draw, or the direct-Final
    /// shape when no draw exists and the field holds at most 32 teams.
    /// </summary>
    public async Task<DrawTypeCupQualificationGroupsResponse> GetAsync(
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
        List<TypeCupSelectionEntity> selection = await LoadSelectionAsync(context, source, cancellationToken).ConfigureAwait(false);
        TypeCupSelectionInvariants.ValidatePersisted(source, selection, rules);
        int teamCount = TypeCupTeamInvariants.ValidateField(selection, rules);
        List<TypeCupTournamentDrawEntity> draws = await context.TypeCupTournamentDraws
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return BuildResponse(saveId, source, rules, teamCount, draws);
    }

    internal async Task<DrawTypeCupQualificationGroupsResponse> DrawUnderLockAsync(
        Guid saveId,
        int? sourceSeasonNumber,
        CancellationToken cancellationToken)
    {
        await _store.EnsureMigratedAsync(saveId, cancellationToken).ConfigureAwait(false);
        using SaveDbContext context = _store.OpenDbContext(saveId);
        using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        DrawState state = await LoadDrawStateAsync(context, saveId, sourceSeasonNumber, cancellationToken).ConfigureAwait(false);
        await EnsureDrawableAsync(context, state, cancellationToken).ConfigureAwait(false);
        if (TypeCupTournamentFormat.IsDirectFinal(state.TeamCount, state.Rules))
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return BuildResponse(saveId, state.Source, state.Rules, state.TeamCount, []);
        }

        EnsureScalableFormat(state.Rules);
        DrawOutcome outcome = PerformDraw(state);
        await PersistDrawAsync(context, state, outcome, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return BuildResponse(saveId, state.Source, state.Rules, state.TeamCount, outcome.Persisted);
    }

    private sealed record DrawState(
        RulesV1 Rules,
        SeasonEntity Source,
        List<TypeCupSelectionEntity> Selection,
        int TeamCount,
        Pcg32State RngBefore);

    private sealed record DrawOutcome(
        IReadOnlyList<TypeCupTournamentFormat.QualificationAssignment> Assignments,
        IReadOnlyList<int> GroupSizes,
        IReadOnlyList<int> FinalPlaces,
        string Checksum,
        Pcg32State RngAfter,
        List<TypeCupTournamentDrawEntity> Persisted);

    private static async Task<DrawState> LoadDrawStateAsync(
        SaveDbContext context,
        Guid saveId,
        int? sourceSeasonNumber,
        CancellationToken cancellationToken)
    {
        RulesV1 rules = await AdvanceRoundHandler.LoadRulesAsync(context, cancellationToken).ConfigureAwait(false);
        _ = await AdvanceRoundHandler.LoadMetadataAsync(context, saveId, cancellationToken).ConfigureAwait(false);
        RngStateEntity rngRow = await AdvanceRoundHandler.LoadRngAsync(context, cancellationToken).ConfigureAwait(false);
        SeasonEntity source = await LoadSourceSeasonAsync(context, sourceSeasonNumber, cancellationToken).ConfigureAwait(false);
        EnsureEvenSeason(source);
        List<TypeCupSelectionEntity> selection = await LoadSelectionAsync(context, source, cancellationToken).ConfigureAwait(false);
        TypeCupSelectionInvariants.ValidatePersisted(source, selection, rules);
        int teamCount = TypeCupTeamInvariants.ValidateField(selection, rules);
        return new DrawState(rules, source, selection, teamCount, rngRow.ToState());
    }

    private static async Task EnsureDrawableAsync(SaveDbContext context, DrawState state, CancellationToken cancellationToken)
    {
        List<TypeCupTournamentDrawEntity> existing = await context.TypeCupTournamentDraws
            .Where(e => e.SourceSeasonId == state.Source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (existing.Count != 0)
        {
            throw new DrawTypeCupQualificationGroupsConflictException(
                $"Type Cup draw for Season {state.Source.SeasonNumber} has already been resolved.");
        }

        await EnsureTeamUnresolvedAsync(context, state.Source, cancellationToken).ConfigureAwait(false);
    }

    private static void EnsureScalableFormat(RulesV1 rules)
    {
        if (rules.TypeCupTournamentFormatVersion != RulesV1.DefaultTypeCupTournamentFormatVersion)
        {
            throw new DrawTypeCupQualificationGroupsConflictException(
                $"Save uses legacy Type Cup format v{rules.TypeCupTournamentFormatVersion}; qualification draw requires v{RulesV1.DefaultTypeCupTournamentFormatVersion}.");
        }
    }

    private static DrawOutcome PerformDraw(DrawState state)
    {
        Pcg32V1 rng = Pcg32V1.Restore(state.RngBefore);
        List<string> types = state.Selection
            .Select(e => e.CreatureType)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var assignments = TypeCupTournamentFormat.DrawQualificationGroups(types, rng, state.Rules);
        Pcg32State rngAfter = rng.Snapshot();
        var groupSizes = TypeCupTournamentFormat.BalancedQualificationGroupSizes(state.TeamCount, state.Rules);
        var finalPlaces = TypeCupTournamentFormat.AllocateFinalPlaces(groupSizes, state.Rules);
        foreach (int size in groupSizes)
        {
            TypeCupTournamentFormat.ValidateCompetitionFieldSize(size, state.Rules);
        }

        TypeCupTournamentFormat.ValidateTournamentDraw(types, assignments, groupSizes, finalPlaces, state.Rules);
        string checksum = TypeCupTournamentFormat.ComputeDrawChecksum(assignments, state.TeamCount, groupSizes);
        return new DrawOutcome(assignments, groupSizes, finalPlaces, checksum, rngAfter, []);
    }

    private static async Task PersistDrawAsync(
        SaveDbContext context,
        DrawState state,
        DrawOutcome outcome,
        CancellationToken cancellationToken)
    {
        List<TypeCupTournamentDrawEntity> rows = BuildRows(
            state.Source,
            state.Rules,
            state.TeamCount,
            outcome.GroupSizes,
            outcome.FinalPlaces,
            outcome.Assignments,
            state.RngBefore,
            outcome.RngAfter,
            outcome.Checksum);
        context.TypeCupTournamentDraws.AddRange(rows);
        context.ApplyRngState(outcome.RngAfter);
        _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        List<TypeCupTournamentDrawEntity> persisted = await context.TypeCupTournamentDraws
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == state.Source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<TypeCupSelectionEntity> reselected = await context.TypeCupSelections
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == state.Source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        TypeCupTournamentDrawInvariants.ValidatePersisted(state.Source, reselected, persisted, state.Rules);
        EnsureSelectionUntouched(state.Selection, reselected);
        outcome.Persisted.AddRange(persisted);
    }

    internal static DrawTypeCupQualificationGroupsResponse BuildResponse(
        Guid saveId,
        SeasonEntity source,
        RulesV1 rules,
        int teamCount,
        IReadOnlyList<TypeCupTournamentDrawEntity> draws)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(draws);
        if (TypeCupTournamentFormat.IsDirectFinal(teamCount, rules))
        {
            return BuildDirectFinalResponse(saveId, source, rules, teamCount, draws);
        }

        return BuildQualificationResponse(saveId, source, draws);
    }

    private static DrawTypeCupQualificationGroupsResponse BuildDirectFinalResponse(
        Guid saveId,
        SeasonEntity source,
        RulesV1 rules,
        int teamCount,
        IReadOnlyList<TypeCupTournamentDrawEntity> draws)
    {
        if (draws.Count != 0)
        {
            throw new InvalidOperationException($"Direct-Final Season {source.SeasonNumber} must not persist a qualification draw.");
        }

        return new DrawTypeCupQualificationGroupsResponse(
            saveId,
            source.SeasonNumber,
            source.Id,
            rules.Version,
            rules.TypeCupTournamentFormatVersion,
            true,
            teamCount,
            0,
            [],
            [],
            string.Empty,
            0UL,
            0UL,
            0UL,
            0UL,
            []);
    }

    private static DrawTypeCupQualificationGroupsResponse BuildQualificationResponse(
        Guid saveId,
        SeasonEntity source,
        IReadOnlyList<TypeCupTournamentDrawEntity> draws)
    {
        if (draws.Count == 0)
        {
            throw new DrawTypeCupQualificationGroupsConflictException(
                $"Type Cup draw for Season {source.SeasonNumber} has not been resolved yet.");
        }

        TypeCupTournamentDrawEntity first = draws.OrderBy(d => d.Id).First();
        int groupCount = first.QualificationGroupCount;
        List<int> sizes = new(groupCount);
        List<int> quotas = new(groupCount);
        List<TypeCupQualificationGroupResult> groups = new(groupCount);
        for (int group = 1; group <= groupCount; group++)
        {
            List<string> members = draws
                .Where(d => d.QualificationGroup == group)
                .Select(d => d.CreatureType)
                .OrderBy(t => t, StringComparer.Ordinal)
                .ToList();
            TypeCupTournamentDrawEntity row = draws.First(d => d.QualificationGroup == group);
            sizes.Add(row.GroupSize);
            quotas.Add(row.FinalPlacesForGroup);
            groups.Add(new TypeCupQualificationGroupResult(group, row.GroupSize, row.FinalPlacesForGroup, members));
        }

        return new DrawTypeCupQualificationGroupsResponse(
            saveId,
            source.SeasonNumber,
            source.Id,
            first.RulesVersion,
            first.TournamentFormatVersion,
            false,
            first.FieldTeamCount,
            groupCount,
            sizes,
            quotas,
            first.DrawChecksum,
            unchecked((ulong)first.RngBeforeState),
            unchecked((ulong)first.RngBeforeStream),
            unchecked((ulong)first.RngAfterState),
            unchecked((ulong)first.RngAfterStream),
            groups);
    }

    private static List<TypeCupTournamentDrawEntity> BuildRows(
        SeasonEntity source,
        RulesV1 rules,
        int teamCount,
        IReadOnlyList<int> groupSizes,
        IReadOnlyList<int> finalPlaces,
        IReadOnlyList<TypeCupTournamentFormat.QualificationAssignment> assignments,
        Pcg32State rngBefore,
        Pcg32State rngAfter,
        string checksum)
    {
        Dictionary<string, int> groupByType = assignments.ToDictionary(a => a.CreatureType, a => a.QualificationGroup, StringComparer.Ordinal);
        List<TypeCupTournamentDrawEntity> rows = new(assignments.Count);
        foreach (TypeCupTournamentFormat.QualificationAssignment assignment in assignments.OrderBy(a => a.CreatureType, StringComparer.Ordinal))
        {
            int group = groupByType[assignment.CreatureType];
            rows.Add(new TypeCupTournamentDrawEntity
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

        return rows;
    }

    private static void EnsureSelectionUntouched(
        IReadOnlyList<TypeCupSelectionEntity> before,
        IReadOnlyList<TypeCupSelectionEntity> after)
    {
        HashSet<(int Athlete, string Type, int Rank)> beforeKeys = before
            .Select(r => (r.SaveAthleteId, r.CreatureType, r.SelectionRank))
            .ToHashSet();
        HashSet<(int Athlete, string Type, int Rank)> afterKeys = after
            .Select(r => (r.SaveAthleteId, r.CreatureType, r.SelectionRank))
            .ToHashSet();
        if (!beforeKeys.SetEquals(afterKeys))
        {
            throw new InvalidOperationException("Type Cup draw must not change squad membership.");
        }
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
                throw new DrawTypeCupQualificationGroupsConflictException(
                    $"Season {sourceSeasonNumber.Value} is not a completed season ready for the Type Cup draw.");
            }

            return explicitSeason;
        }

        List<TypeCupSelectionEntity> any = await context.TypeCupSelections
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (any.Count == 0)
        {
            throw new DrawTypeCupQualificationGroupsConflictException(
                "Type Cup team selection must be resolved before the draw can run.");
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
            throw new DrawTypeCupQualificationGroupsConflictException(
                $"Season {source.SeasonNumber} is odd; the Type Cup draw runs only for even seasons (odd seasons use the Color Cup).");
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
            throw new DrawTypeCupQualificationGroupsConflictException(
                $"Type Cup team selection for Season {source.SeasonNumber} must be resolved before the draw can run.");
        }

        return rows;
    }

    internal static async Task EnsureTeamUnresolvedAsync(
        SaveDbContext context,
        SeasonEntity source,
        CancellationToken cancellationToken)
    {
        bool hasTeams = await context.TypeCupTeamStandings.AnyAsync(
            e => e.SourceSeasonId == source.Id, cancellationToken).ConfigureAwait(false);
        if (hasTeams)
        {
            throw new DrawTypeCupQualificationGroupsConflictException(
                $"Type Cup team event for Season {source.SeasonNumber} has already been resolved.");
        }
    }
}
