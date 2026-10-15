using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Cups.CupHistory;
using MtgSoloSports.Features.Cups.RunColorCupIndividual;
using MtgSoloSports.Features.Cups.RunTypeCupTeam;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;

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
        List<ListCupEditionsResponse.Edition> editions =
        [
            .. BuildEditions(CupTeamKeys.ColorCup, color.Appearances, color.Results, color.PlayedSeasons, color.Champions),
            .. BuildEditions(CupTeamKeys.TypeCup, type.Appearances, type.Results, type.PlayedSeasons, type.Champions),
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
            editions.OrderByDescending(e => e.SourceSeasonNumber).ThenBy(e => e.Cup, StringComparer.Ordinal).ToList(),
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
