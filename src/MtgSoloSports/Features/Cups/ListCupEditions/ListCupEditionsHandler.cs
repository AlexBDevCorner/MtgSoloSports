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

    /// <summary>
    /// One Cup's persisted rows in neutral form. <see cref="Champions"/> is null
    /// for a Cup without an individual event.
    /// </summary>
    internal sealed record CupRows(
        List<Appearance> Appearances,
        List<Result> Results,
        HashSet<int> PlayedSeasons,
        Dictionary<int, ListCupEditionsResponse.IndividualChampion>? Champions);

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
        return new ListCupEditionsResponse(
            saveId,
            editions.OrderByDescending(e => e.SourceSeasonNumber).ThenBy(e => e.Cup, StringComparer.Ordinal).ToList(),
            BuildTeams(color.Appearances, color.Results),
            BuildTeams(type.Appearances, type.Results));
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
        return new CupRows(appearances, results, [.. teamRoundSeasons, .. individualRoundSeasons], champions);
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
        return new CupRows(appearances, results, [.. roundSeasons], null);
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
}
