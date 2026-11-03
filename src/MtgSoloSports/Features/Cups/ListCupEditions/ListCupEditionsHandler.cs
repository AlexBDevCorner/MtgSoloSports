using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Cups.CupHistory;
using MtgSoloSports.Features.Cups.RunColorCupIndividual;
using MtgSoloSports.Features.Cups.RunTypeCupTeam;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;
using MtgSoloSports.SimulationKernel.Cups;

namespace MtgSoloSports.Features.Cups.ListCupEditions;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Lists every Cup edition that
/// has a stored squad selection and aggregates the all-time team table of both
/// Cups. Read-only: nothing is resimulated and a save without Cups yields
/// empty lists.
/// </summary>
public sealed class ListCupEditionsHandler
{
    private readonly SaveStore _store;

    public ListCupEditionsHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    internal sealed record Appearance(int SeasonNumber, string TeamKey, string TeamName);

    internal sealed record Result(
        int SeasonNumber,
        string TeamKey,
        string TeamName,
        int TeamRank,
        string Medal,
        int TeamScoreThousandths);

    internal sealed record IndividualMedal(
        int SeasonNumber,
        string TeamKey,
        string TeamName,
        int Medal);

    /// <summary>
    /// One Cup's persisted rows in neutral form. <see cref="Champions"/> is null
    /// for a Cup without an individual event. <see cref="IndividualMedals"/>
    /// holds one entry per awarded individual Color Cup medal (Gold/Silver/
    /// Bronze), credited to the color the athlete represented in that edition
    /// via the persisted individual-standing SportingColor; empty for a Cup
    /// without an individual event.
    /// </summary>
    internal sealed record CupRows(
        List<Appearance> Appearances,
        List<Result> Results,
        HashSet<int> PlayedSeasons,
        Dictionary<int, ListCupEditionsResponse.IndividualChampion>? Champions,
        List<IndividualMedal> IndividualMedals);

    public async Task<ListCupEditionsResponse> HandleAsync(Guid saveId, CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        await _store.EnsureMigratedAsync(saveId, cancellationToken).ConfigureAwait(false);
        using SaveDbContext context = _store.OpenDbContext(saveId);
        CupRows color = await LoadColorAsync(context, cancellationToken).ConfigureAwait(false);
        CupRows type = await LoadTypeAsync(context, cancellationToken).ConfigureAwait(false);
        List<ListCupEditionsResponse.Edition> chaos = await LoadChaosAsync(context, cancellationToken).ConfigureAwait(false);
        List<ListCupEditionsResponse.Edition> editions =
        [
            .. BuildEditions(CupTeamKeys.ColorCup, color.Appearances, color.Results, color.PlayedSeasons, color.Champions),
            .. BuildEditions(CupTeamKeys.TypeCup, type.Appearances, type.Results, type.PlayedSeasons, type.Champions),
            .. chaos,
        ];
        // MSS-081 correction: the individual event resolves before the team
        // event, so persisted individual standings can exist while the edition
        // is still InProgress. Count only Completed Color Cup editions, using
        // the same edition state as the Cups page; the existing team table and
        // its aggregation stay unchanged.
        HashSet<int> completedColorSeasons = editions
            .Where(e => string.Equals(e.Cup, CupTeamKeys.ColorCup, StringComparison.Ordinal)
                && string.Equals(e.State, CupEditionState.Completed, StringComparison.Ordinal))
            .Select(e => e.SourceSeasonNumber)
            .ToHashSet();
        List<IndividualMedal> completedIndividualMedals = color.IndividualMedals
            .Where(m => completedColorSeasons.Contains(m.SeasonNumber))
            .ToList();
        List<ListCupEditionsResponse.TeamSummary> colorTeams = BuildTeams(color.Appearances, color.Results);
        List<ListCupEditionsResponse.ColorMedalRow> colorIndividual = BuildIndividualMedals(color.Appearances, completedIndividualMedals);
        List<ListCupEditionsResponse.ColorMedalRow> colorCombined = BuildCombinedMedals(colorTeams, colorIndividual);
        return new ListCupEditionsResponse(
            saveId,
            editions.OrderByDescending(e => e.SourceSeasonNumber).ThenBy(CupOrder).ThenBy(e => e.Cup, StringComparer.Ordinal).ToList(),
            colorTeams,
            BuildTeams(type.Appearances, type.Results),
            colorIndividual,
            colorCombined);
    }

    private static async Task<CupRows> LoadColorAsync(SaveDbContext context, CancellationToken cancellationToken)
    {
        List<ColorCupSelectionEntity> selections = await context.ColorCupSelections
            .AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        List<ColorCupTeamStandingEntity> standings = await context.ColorCupTeamStandings
            .AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        List<int> teamRoundSeasons = await context.ColorCupTeamRounds
            .AsNoTracking().Select(e => e.SourceSeasonNumber).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false);
        List<int> individualRoundSeasons = await context.ColorCupIndividualRounds
            .AsNoTracking().Select(e => e.SourceSeasonNumber).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false);
        List<Appearance> appearances = selections
            .Select(s => (s.SourceSeasonNumber, s.SportingColor))
            .Distinct()
            .Select(p => new Appearance(
                p.SourceSeasonNumber,
                CupTeamKeys.ColorKey((SportingColor)p.SportingColor),
                CupTeamKeys.ColorName((SportingColor)p.SportingColor)))
            .ToList();
        List<Result> results = standings
            .Select(s => new Result(
                s.SourceSeasonNumber,
                CupTeamKeys.ColorKey((SportingColor)s.SportingColor),
                CupTeamKeys.ColorName((SportingColor)s.SportingColor),
                s.TeamRank,
                ((ColorCupMedal)s.Medal).ToString(),
                s.TeamScoreThousandths))
            .ToList();
        Dictionary<int, ListCupEditionsResponse.IndividualChampion> champions =
            await LoadChampionsAsync(context, cancellationToken).ConfigureAwait(false);
        List<IndividualMedal> individualMedals = (await context.ColorCupIndividualStandings
                .AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false))
            .Where(s => s.Medal != (int)ColorCupMedal.None)
            .Select(s => new IndividualMedal(
                s.SourceSeasonNumber,
                CupTeamKeys.ColorKey((SportingColor)s.SportingColor),
                CupTeamKeys.ColorName((SportingColor)s.SportingColor),
                s.Medal))
            .ToList();
        return new CupRows(appearances, results, [.. teamRoundSeasons, .. individualRoundSeasons], champions, individualMedals);
    }

    private static async Task<Dictionary<int, ListCupEditionsResponse.IndividualChampion>> LoadChampionsAsync(
        SaveDbContext context,
        CancellationToken cancellationToken)
    {
        List<ColorCupIndividualStandingEntity> champions = await context.ColorCupIndividualStandings
            .AsNoTracking().Where(e => e.CupRank == 1).ToListAsync(cancellationToken).ConfigureAwait(false);
        List<int> championIds = champions.Select(c => c.SaveAthleteId).Distinct().ToList();
        Dictionary<int, SaveAthleteEntity> athletes = await context.SaveAthletes
            .AsNoTracking()
            .Where(e => championIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, cancellationToken)
            .ConfigureAwait(false);
        Dictionary<int, ListCupEditionsResponse.IndividualChampion> bySeason = [];
        foreach (ColorCupIndividualStandingEntity champion in champions)
        {
            if (!athletes.TryGetValue(champion.SaveAthleteId, out SaveAthleteEntity? athlete))
            {
                throw new InvalidOperationException(
                    $"Color Cup individual champion of Season {champion.SourceSeasonNumber} references unknown athlete {champion.SaveAthleteId}.");
            }

            bySeason.Add(champion.SourceSeasonNumber, new ListCupEditionsResponse.IndividualChampion(
                athlete.Id,
                athlete.Name,
                athlete.ImageUrl,
                CupTeamKeys.ColorKey((SportingColor)champion.SportingColor)));
        }

        return bySeason;
    }

    private static async Task<CupRows> LoadTypeAsync(SaveDbContext context, CancellationToken cancellationToken)
    {
        List<TypeCupSelectionEntity> selections = await context.TypeCupSelections
            .AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        // Official results only: legacy single-field rows plus Final rows.
        // Qualification standings never produce podiums or all-time honours.
        List<TypeCupTeamStandingEntity> standings = (await context.TypeCupTeamStandings
            .AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false))
            .Where(e => e.TournamentPhase == (int)SimulationKernel.Cups.TypeCupTournamentFormat.TournamentPhase.LegacySingleField
                || e.TournamentPhase == (int)SimulationKernel.Cups.TypeCupTournamentFormat.TournamentPhase.Final)
            .ToList();
        List<int> roundSeasons = await context.TypeCupTeamRounds
            .AsNoTracking().Select(e => e.SourceSeasonNumber).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false);
        List<Appearance> appearances = selections
            .Select(s => (s.SourceSeasonNumber, s.CreatureType))
            .Distinct()
            .Select(p => new Appearance(p.SourceSeasonNumber, p.CreatureType, p.CreatureType))
            .ToList();
        List<Result> results = standings
            .Select(s => new Result(
                s.SourceSeasonNumber,
                s.CreatureType,
                s.CreatureType,
                s.TeamRank,
                ((TypeCupMedal)s.Medal).ToString(),
                s.TeamScoreThousandths))
            .ToList();
        return new CupRows(appearances, results, [.. roundSeasons], null, []);
    }

    private static int CupOrder(ListCupEditionsResponse.Edition edition)
    {
        ArgumentNullException.ThrowIfNull(edition);
        if (string.Equals(edition.Cup, CupTeamKeys.ColorCup, StringComparison.Ordinal))
        {
            return 0;
        }

        if (string.Equals(edition.Cup, CupTeamKeys.TypeCup, StringComparison.Ordinal))
        {
            return 1;
        }

        if (string.Equals(edition.Cup, CupTeamKeys.ChaosCup, StringComparison.Ordinal))
        {
            return 2;
        }

        return 3;
    }

    /// <summary>
    /// Chaos editions from persisted rows only (MSS-111). Empty Chaos tables
    /// (historic saves without Chaos) yield no editions; nothing is invented.
    /// State mirrors <c>GetChaosCupEdition</c>: <c>Completed</c> once every
    /// stage holds its draws, rounds and standings, otherwise
    /// <c>InProgress</c>. Podium (Final P1..P3) and champion (Final P1) are
    /// official only when the Final is complete; partial editions carry none.
    /// </summary>
    private static async Task<List<ListCupEditionsResponse.Edition>> LoadChaosAsync(
        SaveDbContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        List<ChaosCupEditionEntity> editions = await context.ChaosCupEditions
            .AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        if (editions.Count == 0)
        {
            return [];
        }

        Dictionary<(int SeasonId, int Stage), int> draws = (await context.ChaosCupDraws
                .AsNoTracking()
                .GroupBy(e => new { e.SourceSeasonId, e.StageIndex })
                .Select(g => new { g.Key.SourceSeasonId, g.Key.StageIndex, Count = g.Count() })
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(g => (g.SourceSeasonId, g.StageIndex), g => g.Count);
        Dictionary<(int SeasonId, int Stage), int> rounds = (await context.ChaosCupRounds
                .AsNoTracking()
                .GroupBy(e => new { e.SourceSeasonId, e.StageIndex })
                .Select(g => new { g.Key.SourceSeasonId, g.Key.StageIndex, Count = g.Count() })
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(g => (g.SourceSeasonId, g.StageIndex), g => g.Count);
        Dictionary<(int SeasonId, int Stage), int> standings = (await context.ChaosCupGroupStandings
                .AsNoTracking()
                .GroupBy(e => new { e.SourceSeasonId, e.StageIndex })
                .Select(g => new { g.Key.SourceSeasonId, g.Key.StageIndex, Count = g.Count() })
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(g => (g.SourceSeasonId, g.StageIndex), g => g.Count);

        HashSet<int> completeSeasonIds = editions
            .Where(e => IsChaosComplete(e.SourceSeasonId, draws, rounds, standings))
            .Select(e => e.SourceSeasonId)
            .ToHashSet();
        Dictionary<int, List<ChaosCupGroupStandingEntity>> finalsBySeason = [];
        Dictionary<int, SaveAthleteEntity> athletesById = new();
        if (completeSeasonIds.Count > 0)
        {
            List<ChaosCupGroupStandingEntity> finals = await context.ChaosCupGroupStandings
                .AsNoTracking()
                .Where(e => e.StageIndex == ChaosCupFormat.StageCount - 1 && completeSeasonIds.Contains(e.SourceSeasonId))
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            finalsBySeason = finals
                .GroupBy(e => e.SourceSeasonId)
                .ToDictionary(g => g.Key, g => g.OrderBy(e => e.GroupRank).ToList());
            List<int> athleteIds = finals.Select(e => e.SaveAthleteId).Distinct().ToList();
            athletesById = await context.SaveAthletes
                .AsNoTracking()
                .Where(e => athleteIds.Contains(e.Id))
                .ToDictionaryAsync(e => e.Id, cancellationToken)
                .ConfigureAwait(false);
        }

        return BuildChaosEditions(editions, draws, rounds, standings, finalsBySeason, athletesById);
    }

    internal static bool IsChaosComplete(
        int sourceSeasonId,
        IReadOnlyDictionary<(int SeasonId, int Stage), int> draws,
        IReadOnlyDictionary<(int SeasonId, int Stage), int> rounds,
        IReadOnlyDictionary<(int SeasonId, int Stage), int> standings)
    {
        ArgumentNullException.ThrowIfNull(draws);
        ArgumentNullException.ThrowIfNull(rounds);
        ArgumentNullException.ThrowIfNull(standings);
        for (int stage = 0; stage < ChaosCupFormat.StageCount; stage++)
        {
            int expectedEntrants = ChaosCupFormat.EntrantCountForStage(stage);
            int expectedRounds = checked(ChaosCupFormat.GroupCountForStage(stage) * ChaosCupFormat.RoundsPerGroup);
            draws.TryGetValue((sourceSeasonId, stage), out int drawCount);
            rounds.TryGetValue((sourceSeasonId, stage), out int roundCount);
            standings.TryGetValue((sourceSeasonId, stage), out int standingCount);
            if (drawCount != expectedEntrants || roundCount != expectedRounds || standingCount != expectedEntrants)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Pure Chaos edition mapping for <see cref="LoadChaosAsync"/>. Complete
    /// editions require Final ranks 1..32 exactly once with known athletes;
    /// otherwise the mutation aborts instead of inventing a champion.
    /// </summary>
    internal static List<ListCupEditionsResponse.Edition> BuildChaosEditions(
        IReadOnlyList<ChaosCupEditionEntity> editions,
        IReadOnlyDictionary<(int SeasonId, int Stage), int> draws,
        IReadOnlyDictionary<(int SeasonId, int Stage), int> rounds,
        IReadOnlyDictionary<(int SeasonId, int Stage), int> standings,
        IReadOnlyDictionary<int, List<ChaosCupGroupStandingEntity>> finalsBySeason,
        IReadOnlyDictionary<int, SaveAthleteEntity> athletesById)
    {
        ArgumentNullException.ThrowIfNull(editions);
        ArgumentNullException.ThrowIfNull(draws);
        ArgumentNullException.ThrowIfNull(rounds);
        ArgumentNullException.ThrowIfNull(standings);
        ArgumentNullException.ThrowIfNull(finalsBySeason);
        ArgumentNullException.ThrowIfNull(athletesById);
        List<ListCupEditionsResponse.Edition> result = new(editions.Count);
        foreach (ChaosCupEditionEntity edition in editions)
        {
            bool complete = IsChaosComplete(edition.SourceSeasonId, draws, rounds, standings);
            string state = complete ? CupEditionState.Completed : CupEditionState.InProgress;
            (List<ListCupEditionsResponse.PodiumTeam> Podium, ListCupEditionsResponse.IndividualChampion? Champion) official =
                complete ? MapChaosOfficial(edition, finalsBySeason, athletesById) : ([], null);
            result.Add(new ListCupEditionsResponse.Edition(
                CupTeamKeys.ChaosCup,
                edition.SourceSeasonNumber,
                state,
                edition.FieldSize,
                official.Podium,
                official.Champion));
        }

        return result;
    }

    private static (List<ListCupEditionsResponse.PodiumTeam> Podium, ListCupEditionsResponse.IndividualChampion? Champion) MapChaosOfficial(
        ChaosCupEditionEntity edition,
        IReadOnlyDictionary<int, List<ChaosCupGroupStandingEntity>> finalsBySeason,
        IReadOnlyDictionary<int, SaveAthleteEntity> athletesById)
    {
        ArgumentNullException.ThrowIfNull(edition);
        ArgumentNullException.ThrowIfNull(finalsBySeason);
        ArgumentNullException.ThrowIfNull(athletesById);
        if (!finalsBySeason.TryGetValue(edition.SourceSeasonId, out List<ChaosCupGroupStandingEntity>? finals)
            || finals.Count != ChaosCupFormat.FinalFieldSize)
        {
            throw new InvalidOperationException(
                $"Chaos Cup for Season {edition.SourceSeasonNumber} is marked complete but misses its 32-athlete Final.");
        }

        List<ChaosCupGroupStandingEntity> ordered = finals.OrderBy(e => e.GroupRank).ToList();
        for (int rank = 1; rank <= ordered.Count; rank++)
        {
            if (ordered[rank - 1].GroupRank != rank)
            {
                throw new InvalidOperationException(
                    $"Chaos Cup Final for Season {edition.SourceSeasonNumber} must cover ranks 1..{ChaosCupFormat.FinalFieldSize} exactly once.");
            }
        }

        List<ListCupEditionsResponse.PodiumTeam> podium = [];
        foreach (ChaosCupGroupStandingEntity standing in ordered.Where(e => e.GroupRank <= 3))
        {
            if (!athletesById.TryGetValue(standing.SaveAthleteId, out SaveAthleteEntity? athlete))
            {
                throw new InvalidOperationException(
                    $"Chaos Cup Final for Season {edition.SourceSeasonNumber} references unknown athlete {standing.SaveAthleteId}.");
            }

            podium.Add(new ListCupEditionsResponse.PodiumTeam(
                standing.SaveAthleteId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                athlete.Name,
                standing.GroupRank,
                standing.GroupRank == 1 ? "Gold" : standing.GroupRank == 2 ? "Silver" : "Bronze",
                standing.GroupScoreThousandths));
        }

        ChaosCupGroupStandingEntity winner = ordered[0];
        SaveAthleteEntity championAthlete = athletesById[winner.SaveAthleteId];
        ListCupEditionsResponse.IndividualChampion champion = new(
            championAthlete.Id,
            championAthlete.Name,
            championAthlete.ImageUrl,
            winner.SaveAthleteId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return (podium, champion);
    }

    /// <summary>
    /// <paramref name="champions"/> is null for a Cup without an individual
    /// event; otherwise an edition is complete only once its champion exists.
    /// </summary>
    internal static List<ListCupEditionsResponse.Edition> BuildEditions(
        string cup,
        List<Appearance> appearances,
        List<Result> results,
        HashSet<int> playedSeasons,
        Dictionary<int, ListCupEditionsResponse.IndividualChampion>? champions)
    {
        ArgumentNullException.ThrowIfNull(appearances);
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(playedSeasons);
        List<ListCupEditionsResponse.Edition> editions = [];
        foreach (IGrouping<int, Appearance> season in appearances.GroupBy(a => a.SeasonNumber))
        {
            List<Result> seasonResults = results.Where(r => r.SeasonNumber == season.Key).ToList();
            ListCupEditionsResponse.IndividualChampion? champion = null;
            champions?.TryGetValue(season.Key, out champion);
            bool teamComplete = seasonResults.Count > 0;
            bool complete = teamComplete && (champions is null || champion is not null);
            bool anyPlayed = teamComplete || champion is not null || playedSeasons.Contains(season.Key);
            editions.Add(new ListCupEditionsResponse.Edition(
                cup,
                season.Key,
                CupEditionState.For(anyPlayed, complete),
                season.Count(),
                seasonResults
                    .Where(r => r.TeamRank <= 3)
                    .OrderBy(r => r.TeamRank)
                    .Select(r => new ListCupEditionsResponse.PodiumTeam(r.TeamKey, r.TeamName, r.TeamRank, r.Medal, r.TeamScoreThousandths))
                    .ToList(),
                champion));
        }

        return editions;
    }

    internal static List<ListCupEditionsResponse.TeamSummary> BuildTeams(List<Appearance> appearances, List<Result> results)
    {
        ArgumentNullException.ThrowIfNull(appearances);
        ArgumentNullException.ThrowIfNull(results);
        return appearances
            .GroupBy(a => a.TeamKey, StringComparer.Ordinal)
            .Select(team =>
            {
                List<Result> teamResults = results.Where(r => string.Equals(r.TeamKey, team.Key, StringComparison.Ordinal)).ToList();
                return new ListCupEditionsResponse.TeamSummary(
                    team.Key,
                    team.First().TeamName,
                    team.Count(),
                    teamResults.Count(r => r.TeamRank == 1),
                    teamResults.Count(r => r.TeamRank == 2),
                    teamResults.Count(r => r.TeamRank == 3),
                    teamResults.Count == 0 ? null : teamResults.Min(r => r.TeamRank),
                    team.Max(a => a.SeasonNumber));
            })
            .OrderByDescending(t => t.Gold)
            .ThenByDescending(t => t.Silver)
            .ThenByDescending(t => t.Bronze)
            .ThenBy(t => t.TeamName, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// All-time individual Color Cup medals by color. Each awarded individual
    /// medal counts once for the persisted representation color of that
    /// edition (never the athlete's current league/team). Every supported
    /// color is listed once a Color Cup exists, so colors without medals show
    /// zeros; a save without Color Cups yields an empty table. Callers pass
    /// only medals from <see cref="CupEditionState.Completed"/> editions: an
    /// individual event can resolve while its edition is still InProgress
    /// (team event pending), and those medals must not count yet. Sorting
    /// matches <see cref="BuildTeams"/>.
    /// </summary>
    internal static List<ListCupEditionsResponse.ColorMedalRow> BuildIndividualMedals(
        List<Appearance> appearances,
        List<IndividualMedal> medals)
    {
        ArgumentNullException.ThrowIfNull(appearances);
        ArgumentNullException.ThrowIfNull(medals);
        if (appearances.Count == 0)
        {
            return [];
        }

        return Enum.GetValues<SportingColor>()
            .Select(color =>
            {
                string key = CupTeamKeys.ColorKey(color);
                string name = CupTeamKeys.ColorName(color);
                List<IndividualMedal> colorMedals = medals
                    .Where(m => string.Equals(m.TeamKey, key, StringComparison.Ordinal)).ToList();
                foreach (IndividualMedal medal in colorMedals)
                {
                    if (medal.Medal != (int)ColorCupMedal.Gold
                        && medal.Medal != (int)ColorCupMedal.Silver
                        && medal.Medal != (int)ColorCupMedal.Bronze)
                    {
                        throw new InvalidOperationException(
                            $"Color Cup individual medal for Season {medal.SeasonNumber} has unknown medal {medal.Medal}.");
                    }
                }

                return new ListCupEditionsResponse.ColorMedalRow(
                    key,
                    name,
                    colorMedals.Count(m => m.Medal == (int)ColorCupMedal.Gold),
                    colorMedals.Count(m => m.Medal == (int)ColorCupMedal.Silver),
                    colorMedals.Count(m => m.Medal == (int)ColorCupMedal.Bronze));
            })
            .OrderByDescending(t => t.Gold)
            .ThenByDescending(t => t.Silver)
            .ThenByDescending(t => t.Bronze)
            .ThenBy(t => t.TeamName, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Combined Color Cup medals by color: exact per-color, per-medal sum of
    /// the existing team table and the individual table. One team podium is
    /// one medal for its color (team standings hold one row per color per
    /// season, never one per squad member); one individual podium is one
    /// medal. Sorting matches <see cref="BuildTeams"/>.
    /// </summary>
    internal static List<ListCupEditionsResponse.ColorMedalRow> BuildCombinedMedals(
        IReadOnlyList<ListCupEditionsResponse.TeamSummary> teamTable,
        IReadOnlyList<ListCupEditionsResponse.ColorMedalRow> individualTable)
    {
        ArgumentNullException.ThrowIfNull(teamTable);
        ArgumentNullException.ThrowIfNull(individualTable);
        if (teamTable.Count == 0 && individualTable.Count == 0)
        {
            return [];
        }

        Dictionary<string, ListCupEditionsResponse.TeamSummary> teams = teamTable
            .ToDictionary(t => t.TeamKey, StringComparer.Ordinal);
        Dictionary<string, ListCupEditionsResponse.ColorMedalRow> individuals = individualTable
            .ToDictionary(t => t.TeamKey, StringComparer.Ordinal);
        HashSet<string> keys = new(StringComparer.Ordinal);
        foreach (string key in teams.Keys)
        {
            keys.Add(key);
        }

        foreach (string key in individuals.Keys)
        {
            keys.Add(key);
        }

        return keys
            .Select(key =>
            {
                teams.TryGetValue(key, out ListCupEditionsResponse.TeamSummary? team);
                individuals.TryGetValue(key, out ListCupEditionsResponse.ColorMedalRow? individual);
                string name = team?.TeamName ?? individual!.TeamName;
                int gold = (team?.Gold ?? 0) + (individual?.Gold ?? 0);
                int silver = (team?.Silver ?? 0) + (individual?.Silver ?? 0);
                int bronze = (team?.Bronze ?? 0) + (individual?.Bronze ?? 0);
                return new ListCupEditionsResponse.ColorMedalRow(key, name, gold, silver, bronze);
            })
            .OrderByDescending(t => t.Gold)
            .ThenByDescending(t => t.Silver)
            .ThenByDescending(t => t.Bronze)
            .ThenBy(t => t.TeamName, StringComparer.Ordinal)
            .ToList();
    }
}
