using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.History.EventRounds;
using MtgSoloSports.Features.History.GetEventRound;
using MtgSoloSports.Features.Seasons.SeasonLifecycle;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;
using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.SimulationKernel.Random;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Features.History.GetEventTeamStandings;

/// <summary>
/// Endpoint -&gt; Handler direct call. Team standings of a team Cup event: the
/// persisted final ranking once the event is complete, otherwise a provisional
/// display projection summing each team's stored round points over every round
/// played so far (no ranking, no tie-break, no RNG). With a before-round cursor
/// the projection covers only the rounds played ahead of that round, so a round
/// reveal can add its own stored points on top. Type Cup tournaments are
/// stage-scoped: a qualification group's baseline never includes another
/// group's or the Final's points, and Final baselines start fresh at zero.
/// Read-only; never resimulates.
/// </summary>
public sealed class GetHistoryEventTeamStandingsHandler
{
    private readonly SaveStore _store;

    public GetHistoryEventTeamStandingsHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<HistoryEventTeamStandingsResponse> HandleAsync(
        Guid saveId,
        int seasonNumber,
        string eventKey,
        int? beforeGroup = null,
        int? beforeRound = null,
        CancellationToken cancellationToken = default,
        int? tournamentPhase = null,
        int? qualificationGroup = null)
    {
        HistoryEventRows.ValidateRequest(saveId, seasonNumber, eventKey);
        if (!string.Equals(eventKey, PostseasonEvents.ColorCupTeam, StringComparison.Ordinal)
            && !string.Equals(eventKey, PostseasonEvents.TypeCupTeam, StringComparison.Ordinal))
        {
            throw new ArgumentException($"{PostseasonEvents.Title(eventKey)} has no team standings.", nameof(eventKey));
        }

        bool isTypeCup = string.Equals(eventKey, PostseasonEvents.TypeCupTeam, StringComparison.Ordinal);
        if (!isTypeCup && (tournamentPhase is not null || qualificationGroup is not null))
        {
            throw new ArgumentException($"{PostseasonEvents.Title(eventKey)} has no tournament stages.", nameof(tournamentPhase));
        }

        using SaveDbContext context = _store.OpenDbContext(saveId);
        RulesV1 rules = await AdvanceRoundHandler.LoadRulesAsync(context, cancellationToken).ConfigureAwait(false);
        PostseasonEvents.EventShape shape = PostseasonEvents.Shape(eventKey, rules);
        SeasonEntity season = await HistoryEventRows.LoadSeasonAsync(context, seasonNumber, cancellationToken).ConfigureAwait(false);
        bool color = string.Equals(eventKey, PostseasonEvents.ColorCupTeam, StringComparison.Ordinal);
        if (isTypeCup)
        {
            GetHistoryEventRoundHandler.ValidateTypeCupStage(tournamentPhase, qualificationGroup);
        }

        (int Group, int Round)? before = ValidateBefore(beforeGroup, beforeRound, shape);
        if (isTypeCup && before is not null && tournamentPhase is null)
        {
            await RequireUnambiguousTypeCupBeforeAsync(context, season, eventKey, before.Value, cancellationToken).ConfigureAwait(false);
        }

        Dictionary<int, string> teamByAthlete = await LoadTeamByAthleteAsync(context, season, eventKey, color, cancellationToken).ConfigureAwait(false);
        Dictionary<int, string> scopedByAthlete = await ScopeToStageAsync(context, season, eventKey, teamByAthlete, isTypeCup, tournamentPhase, qualificationGroup, cancellationToken).ConfigureAwait(false);

        List<HistoryEventTeamMember> members = scopedByAthlete
            .OrderBy(pair => pair.Key)
            .Select(pair => new HistoryEventTeamMember(pair.Key, pair.Value))
            .ToList();

        if (before is null && tournamentPhase is null
            && await HistoryEventRows.IsCompleteAsync(context, season, eventKey, cancellationToken).ConfigureAwait(false))
        {
            List<HistoryEventTeamRow> final = await LoadFinalAsync(context, season, color, cancellationToken).ConfigureAwait(false);
            return new HistoryEventTeamStandingsResponse(saveId, seasonNumber, eventKey, IsFinal: true, shape.GroupCount, final, members);
        }

        if (before is null && tournamentPhase is not null)
        {
            List<HistoryEventTeamRow> stagedFinal = await LoadStageProvisionalAsync(context, season, eventKey, shape, scopedByAthlete, tournamentPhase.Value, qualificationGroup!.Value, before: null, cancellationToken).ConfigureAwait(false);
            int stagedGroups = await CountStageGroupsAsync(context, season, eventKey, shape, tournamentPhase.Value, qualificationGroup!.Value, before: null, cancellationToken).ConfigureAwait(false);
            return new HistoryEventTeamStandingsResponse(saveId, seasonNumber, eventKey, IsFinal: false, stagedGroups, stagedFinal, members);
        }

        (int groupsCompleted, List<HistoryEventTeamRow> provisional) =
            await ProjectProvisionalAsync(context, season, eventKey, shape, scopedByAthlete, before, cancellationToken, tournamentPhase, qualificationGroup).ConfigureAwait(false);
        return new HistoryEventTeamStandingsResponse(saveId, seasonNumber, eventKey, IsFinal: false, groupsCompleted, provisional, members);
    }

    internal static async Task<List<HistoryEventTeamRow>> LoadFinalAsync(
        SaveDbContext context, SeasonEntity season, bool color, CancellationToken cancellationToken)
    {
        if (color)
        {
            return (await context.ColorCupTeamStandings.AsNoTracking()
                    .Where(e => e.SourceSeasonId == season.Id)
                    .OrderBy(e => e.TeamRank)
                    .ToListAsync(cancellationToken).ConfigureAwait(false))
                .Select(e => new HistoryEventTeamRow(((SportingColor)e.SportingColor).ToString(), e.TeamRank, e.TeamScoreThousandths))
                .ToList();
        }

        List<TypeCupTeamStandingEntity> rows = await context.TypeCupTeamStandings.AsNoTracking()
                .Where(e => e.SourceSeasonId == season.Id)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
        bool hasLegacy = rows.Any(e => e.TournamentPhase == (int)MtgSoloSports.SimulationKernel.Cups.TypeCupTournamentFormat.TournamentPhase.LegacySingleField);
        IEnumerable<TypeCupTeamStandingEntity> official = hasLegacy
            ? rows.OrderBy(e => e.TeamRank)
            : rows.Where(e => e.TournamentPhase == (int)MtgSoloSports.SimulationKernel.Cups.TypeCupTournamentFormat.TournamentPhase.Final).OrderBy(e => e.TeamRank);
        return official
            .Select(e => new HistoryEventTeamRow(e.CreatureType, e.TeamRank, e.TeamScoreThousandths))
            .ToList();
    }

    private static (int Group, int Round)? ValidateBefore(int? beforeGroup, int? beforeRound, PostseasonEvents.EventShape shape)
    {
        if (beforeGroup is null && beforeRound is null)
        {
            return null;
        }

        if (beforeGroup is not int group || beforeRound is not int round)
        {
            throw new ArgumentException("A before-round cursor needs both a group and a round.", nameof(beforeRound));
        }

        if (group < 1 || group > shape.GroupCount || round < 1 || round > shape.RoundsPerGroup)
        {
            throw new ArgumentException($"Group {group} round {round} is outside the event.", nameof(beforeRound));
        }

        return (group, round);
    }

    private static async Task<Dictionary<int, string>> LoadTeamByAthleteAsync(
        SaveDbContext context, SeasonEntity season, string eventKey, bool color, CancellationToken cancellationToken)
    {
        Dictionary<int, string> teamByAthlete = color
            ? (await context.ColorCupSelections.AsNoTracking()
                    .Where(e => e.SourceSeasonId == season.Id)
                    .ToListAsync(cancellationToken).ConfigureAwait(false))
                .ToDictionary(e => e.SaveAthleteId, e => ((SportingColor)e.SportingColor).ToString())
            : (await context.TypeCupSelections.AsNoTracking()
                    .Where(e => e.SourceSeasonId == season.Id)
                    .ToListAsync(cancellationToken).ConfigureAwait(false))
                .ToDictionary(e => e.SaveAthleteId, e => e.CreatureType);
        if (teamByAthlete.Count == 0)
        {
            throw new HistoryNotFoundException($"{PostseasonEvents.Title(eventKey)} has no selected field for Season {season.SeasonNumber}.");
        }

        return teamByAthlete;
    }

    internal static async Task RequireUnambiguousTypeCupBeforeAsync(
        SaveDbContext context,
        SeasonEntity season,
        string eventKey,
        (int Group, int Round) before,
        CancellationToken cancellationToken)
    {
        List<HistoryEventRows.StoredRound> rows = await HistoryEventRows.LoadAsync(context, season, eventKey, cancellationToken).ConfigureAwait(false);
        int stages = rows
            .Where(r => r.Group == before.Group && r.Round == before.Round)
            .Select(r => (r.TournamentPhase, r.QualificationGroup))
            .Distinct()
            .Count();
        if (stages > 1)
        {
            throw new ArgumentException(
                $"Type Cup group {before.Group} round {before.Round} exists in {stages} tournament stages; specify tournamentPhase and qualificationGroup.",
                nameof(before));
        }
    }

    internal static async Task<int> CountStageGroupsAsync(
        SaveDbContext context,
        SeasonEntity season,
        string eventKey,
        PostseasonEvents.EventShape shape,
        int tournamentPhase,
        int qualificationGroup,
        (int Group, int Round)? before,
        CancellationToken cancellationToken)
    {
        List<HistoryEventRows.StoredRound> rows = await HistoryEventRows.LoadAsync(context, season, eventKey, cancellationToken).ConfigureAwait(false);
        rows = rows
            .Where(r => r.TournamentPhase == tournamentPhase && r.QualificationGroup == qualificationGroup)
            .ToList();
        if (before is (int beforeGroup, int beforeRound))
        {
            rows = rows
                .Where(row => row.Group < beforeGroup || (row.Group == beforeGroup && row.Round < beforeRound))
                .ToList();
        }

        return rows.Count / shape.RoundsPerGroup;
    }

    internal static async Task<List<HistoryEventTeamRow>> LoadStageProvisionalAsync(
        SaveDbContext context,
        SeasonEntity season,
        string eventKey,
        PostseasonEvents.EventShape shape,
        Dictionary<int, string> teamByAthlete,
        int tournamentPhase,
        int qualificationGroup,
        (int Group, int Round)? before,
        CancellationToken cancellationToken)
    {
        List<HistoryEventRows.StoredRound> rows = await HistoryEventRows.LoadAsync(context, season, eventKey, cancellationToken).ConfigureAwait(false);
        rows = rows
            .Where(r => r.TournamentPhase == tournamentPhase && r.QualificationGroup == qualificationGroup)
            .ToList();
        if (before is (int beforeGroup, int beforeRound))
        {
            rows = rows
                .Where(row => row.Group < beforeGroup || (row.Group == beforeGroup && row.Round < beforeRound))
                .ToList();
        }

        HashSet<string>? roster = await ResolveStageRosterAsync(context, season, eventKey, teamByAthlete, tournamentPhase, qualificationGroup, cancellationToken).ConfigureAwait(false);
        IEnumerable<string> scopeTeams = roster ?? teamByAthlete.Values.Distinct(StringComparer.Ordinal);
        Dictionary<string, long> totals = scopeTeams.ToDictionary(team => team, _ => 0L, StringComparer.Ordinal);
        foreach (HistoryEventRows.StoredRound row in rows)
        {
            foreach (RoundPayloadEntry placement in HistoryEventRows.DecodePlacements(eventKey, row.PayloadJson))
            {
                if (!teamByAthlete.TryGetValue(placement.AthleteId, out string? team))
                {
                    throw new InvalidOperationException($"{PostseasonEvents.Title(eventKey)} round references athlete {placement.AthleteId} outside the selected field.");
                }

                if (roster is not null && !roster.Contains(team))
                {
                    throw new InvalidOperationException($"{PostseasonEvents.Title(eventKey)} stage {tournamentPhase}/{qualificationGroup} round references team '{team}' outside the persisted stage roster.");
                }

                totals[team] += placement.FinalThousandths;
            }
        }

        return totals
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new HistoryEventTeamRow(pair.Key, Rank: null, checked((int)pair.Value)))
            .ToList();
    }

    internal static async Task<Dictionary<int, string>> ScopeToStageAsync(
        SaveDbContext context,
        SeasonEntity season,
        string eventKey,
        Dictionary<int, string> teamByAthlete,
        bool isTypeCup,
        int? tournamentPhase,
        int? qualificationGroup,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(season);
        ArgumentNullException.ThrowIfNull(teamByAthlete);
        if (!isTypeCup || tournamentPhase is null || qualificationGroup is null)
        {
            return teamByAthlete;
        }

        HashSet<string>? roster = await ResolveStageRosterAsync(context, season, eventKey, teamByAthlete, tournamentPhase.Value, qualificationGroup.Value, cancellationToken).ConfigureAwait(false);
        return roster is null
            ? teamByAthlete
            : teamByAthlete.Where(pair => roster.Contains(pair.Value)).ToDictionary(pair => pair.Key, pair => pair.Value);
    }

    /// <summary>
    /// Resolves the persisted team roster for a Type Cup tournament stage.
    /// Returns null when no stage scoping applies (non-Type Cup, legacy single
    /// field, or direct Final where the whole selected field contests the Final).
    /// Otherwise returns the exact persisted stage teams: the qualification draw
    /// for qualification groups, the persisted Final standings when complete,
    /// else the union of teams in persisted Final rounds, else empty when the
    /// Final has no persisted roster yet. Never returns all selected teams for
    /// a staged tournament qualification or Final.
    /// </summary>
    internal static async Task<HashSet<string>?> ResolveStageRosterAsync(
        SaveDbContext context,
        SeasonEntity season,
        string eventKey,
        Dictionary<int, string> teamByAthlete,
        int tournamentPhase,
        int qualificationGroup,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(season);
        ArgumentNullException.ThrowIfNull(teamByAthlete);
        if (!string.Equals(eventKey, PostseasonEvents.TypeCupTeam, StringComparison.Ordinal))
        {
            return null;
        }

        if (tournamentPhase == (int)TypeCupTournamentFormat.TournamentPhase.LegacySingleField)
        {
            return null;
        }

        if (tournamentPhase == (int)TypeCupTournamentFormat.TournamentPhase.Qualification)
        {
            return await ResolveQualificationRosterAsync(context, season, eventKey, teamByAthlete, tournamentPhase, qualificationGroup, cancellationToken).ConfigureAwait(false);
        }

        if (tournamentPhase == (int)TypeCupTournamentFormat.TournamentPhase.Final)
        {
            return await ResolveFinalRosterAsync(context, season, eventKey, teamByAthlete, tournamentPhase, qualificationGroup, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    internal static async Task<HashSet<string>> ResolveQualificationRosterAsync(
        SaveDbContext context,
        SeasonEntity season,
        string eventKey,
        Dictionary<int, string> teamByAthlete,
        int tournamentPhase,
        int qualificationGroup,
        CancellationToken cancellationToken)
    {
        List<string> drawTypes = await context.TypeCupTournamentDraws.AsNoTracking()
            .Where(e => e.SourceSeasonId == season.Id && e.QualificationGroup == qualificationGroup)
            .Select(e => e.CreatureType)
            .Distinct()
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (drawTypes.Count > 0)
        {
            return new HashSet<string>(drawTypes, StringComparer.Ordinal);
        }

        return CollectStageUnion(await HistoryEventRows.LoadAsync(context, season, eventKey, cancellationToken).ConfigureAwait(false), eventKey, teamByAthlete, tournamentPhase, qualificationGroup);
    }

    internal static async Task<HashSet<string>?> ResolveFinalRosterAsync(
        SaveDbContext context,
        SeasonEntity season,
        string eventKey,
        Dictionary<int, string> teamByAthlete,
        int tournamentPhase,
        int qualificationGroup,
        CancellationToken cancellationToken)
    {
        bool hasDraw = await context.TypeCupTournamentDraws
            .AnyAsync(e => e.SourceSeasonId == season.Id, cancellationToken).ConfigureAwait(false);
        if (!hasDraw)
        {
            return null;
        }

        List<string> finalStandings = await context.TypeCupTeamStandings.AsNoTracking()
            .Where(e => e.SourceSeasonId == season.Id && e.TournamentPhase == (int)TypeCupTournamentFormat.TournamentPhase.Final)
            .Select(e => e.CreatureType)
            .Distinct()
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (finalStandings.Count > 0)
        {
            return new HashSet<string>(finalStandings, StringComparer.Ordinal);
        }

        HashSet<string> union = CollectStageUnion(await HistoryEventRows.LoadAsync(context, season, eventKey, cancellationToken).ConfigureAwait(false), eventKey, teamByAthlete, tournamentPhase, qualificationGroup);
        if (union.Count > 0)
        {
            return union;
        }

        // MSS-078 correction: the Final has no persisted rounds yet. When every
        // qualification group has a persisted table, derive the 32 finalists
        // from those authoritative standings (same quotas/math as the runners)
        // so the pending Final renders its zeroed starting field. While quals
        // are still partial, return the empty union so the UI keeps its
        // pending message instead of unscoped totals. Read-only: the RNG copy
        // is restored, never consumed.
        HashSet<string> pending = await ResolvePendingFinalistsFromQualsAsync(context, season, cancellationToken).ConfigureAwait(false);
        return pending;
    }

    internal static async Task<HashSet<string>> ResolvePendingFinalistsFromQualsAsync(
        SaveDbContext context,
        SeasonEntity season,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(season);
        List<TypeCupTournamentDrawEntity> draws = await context.TypeCupTournamentDraws.AsNoTracking()
            .Where(e => e.SourceSeasonId == season.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (draws.Count == 0)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        int policy = draws[0].TournamentFormatVersion;
        if (draws.Any(d => d.TournamentFormatVersion != policy))
        {
            throw new InvalidOperationException("Type Cup qualification draw has mixed tournament format versions within one edition.");
        }

        List<int> groups = draws.Select(d => d.QualificationGroup).Distinct().OrderBy(g => g).ToList();
        if (groups.Count == 0)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        List<TypeCupTeamStandingEntity> qualTeams = await context.TypeCupTeamStandings.AsNoTracking()
            .Where(e => e.SourceSeasonId == season.Id && e.TournamentPhase == (int)TypeCupTournamentFormat.TournamentPhase.Qualification)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (qualTeams.Count == 0)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        RulesV1 rules = await AdvanceRoundHandler.LoadRulesAsync(context, cancellationToken).ConfigureAwait(false);
        if (policy == RulesV1.FixedQuotaTypeCupTournamentFormatVersion)
        {
            return ResolvePendingFixedQuota(draws, groups, qualTeams);
        }

        if (policy != RulesV1.WildcardTypeCupTournamentFormatVersion)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        return await ResolvePendingWildcardsAsync(context, draws, groups, qualTeams, rules, cancellationToken).ConfigureAwait(false);
    }

    internal static HashSet<string> ResolvePendingFixedQuota(
        List<TypeCupTournamentDrawEntity> draws,
        List<int> groups,
        List<TypeCupTeamStandingEntity> qualTeams)
    {
        ArgumentNullException.ThrowIfNull(draws);
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(qualTeams);
        HashSet<string> finalists = new(StringComparer.Ordinal);
        foreach (int group in groups)
        {
            List<TypeCupTournamentDrawEntity> drawRows = draws.Where(d => d.QualificationGroup == group).ToList();
            int size = drawRows[0].GroupSize;
            int quota = drawRows[0].FinalPlacesForGroup;
            if (drawRows.Any(r => r.GroupSize != size || r.FinalPlacesForGroup != quota))
            {
                throw new InvalidOperationException($"Type Cup draw group {group} has inconsistent size/quota metadata.");
            }

            List<TypeCupTeamStandingEntity> ordered = qualTeams
                .Where(e => e.QualificationGroup == group)
                .OrderBy(e => e.TeamRank)
                .ToList();
            if (ordered.Count != size)
            {
                return new HashSet<string>(StringComparer.Ordinal);
            }

            ValidateQualRanks(group, ordered);
            foreach (TypeCupTeamStandingEntity row in ordered.Take(quota))
            {
                finalists.Add(row.CreatureType);
            }
        }

        return finalists;
    }

    internal static async Task<HashSet<string>> ResolvePendingWildcardsAsync(
        SaveDbContext context,
        List<TypeCupTournamentDrawEntity> draws,
        List<int> groups,
        List<TypeCupTeamStandingEntity> qualTeams,
        RulesV1 rules,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(draws);
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(qualTeams);
        ArgumentNullException.ThrowIfNull(rules);
        int groupCount = groups.Count;
        int guaranteed = rules.TypeCupFinalTeamCount / groupCount;
        int wildcardCount = rules.TypeCupFinalTeamCount - (groupCount * guaranteed);
        HashSet<string> finalists = new(StringComparer.Ordinal);
        List<TypeCupTournamentFormat.WildcardCandidate> candidates = new(groupCount);
        foreach (int group in groups)
        {
            if (!CollectPendingGroup(draws, qualTeams, group, guaranteed, wildcardCount, finalists, candidates))
            {
                return new HashSet<string>(StringComparer.Ordinal);
            }
        }

        if (wildcardCount > 0 && candidates.Count > 0)
        {
            await AddPendingWildcardWinnersAsync(context, candidates, wildcardCount, rules, finalists, cancellationToken).ConfigureAwait(false);
        }

        return finalists;
    }

    internal static bool CollectPendingGroup(
        List<TypeCupTournamentDrawEntity> draws,
        List<TypeCupTeamStandingEntity> qualTeams,
        int group,
        int guaranteed,
        int wildcardCount,
        HashSet<string> finalists,
        List<TypeCupTournamentFormat.WildcardCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(draws);
        ArgumentNullException.ThrowIfNull(qualTeams);
        ArgumentNullException.ThrowIfNull(finalists);
        ArgumentNullException.ThrowIfNull(candidates);
        List<TypeCupTournamentDrawEntity> drawRows = draws.Where(d => d.QualificationGroup == group).ToList();
        int size = drawRows[0].GroupSize;
        if (drawRows.Any(r => r.GroupSize != size))
        {
            throw new InvalidOperationException($"Type Cup draw group {group} has inconsistent size metadata.");
        }

        List<TypeCupTeamStandingEntity> ordered = qualTeams
            .Where(e => e.QualificationGroup == group)
            .OrderBy(e => e.TeamRank)
            .ToList();
        if (ordered.Count != size)
        {
            return false;
        }

        ValidateQualRanks(group, ordered);
        foreach (TypeCupTeamStandingEntity row in ordered.Take(guaranteed))
        {
            finalists.Add(row.CreatureType);
        }

        if (wildcardCount > 0)
        {
            if (ordered.Count <= guaranteed)
            {
                return false;
            }

            TypeCupTeamStandingEntity candidate = ordered[guaranteed];
            candidates.Add(new TypeCupTournamentFormat.WildcardCandidate(
                group,
                candidate.CreatureType,
                candidate.TeamRank,
                candidate.TeamScoreThousandths,
                candidate.TeamBaseThousandths,
                size));
        }

        return true;
    }

    internal static async Task AddPendingWildcardWinnersAsync(
        SaveDbContext context,
        List<TypeCupTournamentFormat.WildcardCandidate> candidates,
        int wildcardCount,
        RulesV1 rules,
        HashSet<string> finalists,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(finalists);
        RngStateEntity rngRow = await AdvanceRoundHandler.LoadRngAsync(context, cancellationToken).ConfigureAwait(false);
        Pcg32V1 rng = Pcg32V1.Restore(rngRow.ToState());
        TypeCupTournamentFormat.WildcardResolution resolution =
            TypeCupTournamentFormat.ResolveWildcards(candidates, wildcardCount, rng, rules);
        foreach (TypeCupTournamentFormat.WildcardCandidate winner in resolution.Winners)
        {
            finalists.Add(winner.Team);
        }
    }

    internal static void ValidateQualRanks(int group, List<TypeCupTeamStandingEntity> ordered)
    {
        ArgumentNullException.ThrowIfNull(ordered);
        for (int i = 0; i < ordered.Count; i++)
        {
            if (ordered[i].TeamRank != i + 1)
            {
                throw new InvalidOperationException($"Type Cup qualification group {group} ranks are corrupt.");
            }
        }
    }

    internal static HashSet<string> CollectStageUnion(
        List<HistoryEventRows.StoredRound> allRows,
        string eventKey,
        Dictionary<int, string> teamByAthlete,
        int tournamentPhase,
        int qualificationGroup)
    {
        ArgumentNullException.ThrowIfNull(allRows);
        ArgumentNullException.ThrowIfNull(teamByAthlete);
        List<HistoryEventRows.StoredRound> stageRows = allRows
            .Where(r => r.TournamentPhase == tournamentPhase && r.QualificationGroup == qualificationGroup)
            .ToList();
        HashSet<string> union = new(StringComparer.Ordinal);
        foreach (HistoryEventRows.StoredRound row in stageRows)
        {
            foreach (RoundPayloadEntry placement in HistoryEventRows.DecodePlacements(eventKey, row.PayloadJson))
            {
                if (teamByAthlete.TryGetValue(placement.AthleteId, out string? team))
                {
                    union.Add(team);
                }
            }
        }

        return union;
    }

    /// <summary>Sums each team's stored round points over every round played so far, or ahead of <paramref name="before"/> (display only).</summary>
    internal static async Task<(int GroupsCompleted, List<HistoryEventTeamRow> Teams)> ProjectProvisionalAsync(
        SaveDbContext context,
        SeasonEntity season,
        string eventKey,
        PostseasonEvents.EventShape shape,
        Dictionary<int, string> teamByAthlete,
        (int Group, int Round)? before,
        CancellationToken cancellationToken,
        int? tournamentPhase = null,
        int? qualificationGroup = null)
    {
        List<HistoryEventRows.StoredRound> rows = await HistoryEventRows.LoadAsync(context, season, eventKey, cancellationToken).ConfigureAwait(false);
        bool isTypeCup = string.Equals(eventKey, PostseasonEvents.TypeCupTeam, StringComparison.Ordinal);
        if (isTypeCup && tournamentPhase is not null && qualificationGroup is not null)
        {
            rows = rows
                .Where(r => r.TournamentPhase == tournamentPhase && r.QualificationGroup == qualificationGroup)
                .ToList();
        }

        if (before is (int beforeGroup, int beforeRound))
        {
            rows = rows
                .Where(row => row.Group < beforeGroup || (row.Group == beforeGroup && row.Round < beforeRound))
                .ToList();
        }

        int groupsCompleted = rows.Count / shape.RoundsPerGroup;
        HashSet<string>? roster = null;
        if (isTypeCup && tournamentPhase is not null && qualificationGroup is not null)
        {
            roster = await ResolveStageRosterAsync(context, season, eventKey, teamByAthlete, tournamentPhase.Value, qualificationGroup.Value, cancellationToken).ConfigureAwait(false);
        }

        IEnumerable<string> scopeTeams = roster ?? teamByAthlete.Values.Distinct(StringComparer.Ordinal);
        Dictionary<string, long> totals = scopeTeams.ToDictionary(team => team, _ => 0L, StringComparer.Ordinal);
        foreach (HistoryEventRows.StoredRound row in rows)
        {
            foreach (RoundPayloadEntry placement in HistoryEventRows.DecodePlacements(eventKey, row.PayloadJson))
            {
                if (!teamByAthlete.TryGetValue(placement.AthleteId, out string? team))
                {
                    throw new InvalidOperationException($"{PostseasonEvents.Title(eventKey)} round references athlete {placement.AthleteId} outside the selected field.");
                }

                if (roster is not null && !roster.Contains(team))
                {
                    throw new InvalidOperationException($"{PostseasonEvents.Title(eventKey)} stage {tournamentPhase}/{qualificationGroup} round references team '{team}' outside the persisted stage roster.");
                }

                totals[team] += placement.FinalThousandths;
            }
        }

        List<HistoryEventTeamRow> teams = totals
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new HistoryEventTeamRow(pair.Key, Rank: null, checked((int)pair.Value)))
            .ToList();
        return (groupsCompleted, teams);
    }
}
