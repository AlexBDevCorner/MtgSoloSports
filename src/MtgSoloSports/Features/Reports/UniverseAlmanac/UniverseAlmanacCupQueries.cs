using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Cups.CupHistory;
using MtgSoloSports.Features.Cups.ListCupEditions;
using MtgSoloSports.Features.Cups.RunColorCupIndividual;
using MtgSoloSports.Features.Cups.RunTypeCupTeam;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;

namespace MtgSoloSports.Features.Reports.UniverseAlmanac;

internal static class UniverseAlmanacCupQueries
{
    internal static async Task<AlmanacCupTables> LoadCupTablesAsync(
        SaveDbContext context,
        Dictionary<int, string> names,
        CancellationToken cancellationToken)
    {
        ColorCupRows color = await LoadColorRowsAsync(context, cancellationToken).ConfigureAwait(false);
        TypeCupRows type = await LoadTypeRowsAsync(context, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<AlmanacCupEditionEntry> editions = BuildEditions(color, type);
        IReadOnlyList<ListCupEditionsResponse.TeamSummary> colorTeams = BuildTeamTable(color.Appearances, color.Results);
        IReadOnlyList<ListCupEditionsResponse.TeamSummary> typeTeams = BuildTeamTable(type.Appearances, type.Results);
        IReadOnlyList<ListCupEditionsResponse.ColorMedalRow> individual = BuildIndividualMedals(color.Appearances, color.IndividualMedals);
        IReadOnlyList<ListCupEditionsResponse.ColorMedalRow> combined = BuildCombinedMedals(colorTeams, individual);
        return new AlmanacCupTables(editions, colorTeams, individual, combined, typeTeams);
    }

    internal sealed record ColorCupRows(
        List<(int Season, string TeamKey, string TeamName)> Appearances,
        List<(int Season, string TeamKey, string TeamName, int Rank, string Medal, int Score)> Results,
        HashSet<int> PlayedSeasons,
        Dictionary<int, (int AthleteId, string Name)> Champions,
        List<(int Season, string TeamKey, int Medal)> IndividualMedals);

    internal sealed record TypeCupRows(
        List<(int Season, string TeamKey, string TeamName)> Appearances,
        List<(int Season, string TeamKey, string TeamName, int Rank, string Medal, int Score)> Results,
        HashSet<int> PlayedSeasons);

    internal static async Task<ColorCupRows> LoadColorRowsAsync(SaveDbContext context, CancellationToken cancellationToken)
    {
        List<ColorCupSelectionEntity> selections = await context.ColorCupSelections
            .AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        List<ColorCupTeamStandingEntity> standings = await context.ColorCupTeamStandings
            .AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        List<int> teamRounds = await context.ColorCupTeamRounds
            .AsNoTracking().Select(e => e.SourceSeasonNumber).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false);
        List<int> individualRounds = await context.ColorCupIndividualRounds
            .AsNoTracking().Select(e => e.SourceSeasonNumber).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false);
        List<ColorCupIndividualStandingEntity> individuals = await context.ColorCupIndividualStandings
            .AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        Dictionary<int, string> athleteNames = await context.SaveAthletes
            .AsNoTracking().ToDictionaryAsync(e => e.Id, e => e.Name, cancellationToken).ConfigureAwait(false);
        List<(int Season, string TeamKey, string TeamName)> appearances = selections
            .Select(s => (s.SourceSeasonNumber, s.SportingColor))
            .Distinct()
            .Select(p => (p.SourceSeasonNumber, CupTeamKeys.ColorKey((SportingColor)p.SportingColor), CupTeamKeys.ColorName((SportingColor)p.SportingColor)))
            .ToList();
        List<(int Season, string TeamKey, string TeamName, int Rank, string Medal, int Score)> results = standings
            .Select(s => (s.SourceSeasonNumber, CupTeamKeys.ColorKey((SportingColor)s.SportingColor), CupTeamKeys.ColorName((SportingColor)s.SportingColor), s.TeamRank, ((ColorCupMedal)s.Medal).ToString(), s.TeamScoreThousandths))
            .ToList();
        Dictionary<int, (int AthleteId, string Name)> champions = individuals
            .Where(i => i.CupRank == 1)
            .ToDictionary(i => i.SourceSeasonNumber, i => (i.SaveAthleteId, athleteNames.GetValueOrDefault(i.SaveAthleteId, $"Athlete {i.SaveAthleteId}")));
        List<(int Season, string TeamKey, int Medal)> medals = individuals
            .Where(i => i.Medal != (int)ColorCupMedal.None)
            .Select(i => (i.SourceSeasonNumber, CupTeamKeys.ColorKey((SportingColor)i.SportingColor), i.Medal))
            .ToList();
        HashSet<int> played = [.. teamRounds, .. individualRounds];
        return new ColorCupRows(appearances, results, played, champions, medals);
    }

    internal static async Task<TypeCupRows> LoadTypeRowsAsync(SaveDbContext context, CancellationToken cancellationToken)
    {
        List<TypeCupSelectionEntity> selections = await context.TypeCupSelections
            .AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        List<TypeCupTeamStandingEntity> standings = (await context.TypeCupTeamStandings
                .AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false))
            .Where(e => e.TournamentPhase == (int)SimulationKernel.Cups.TypeCupTournamentFormat.TournamentPhase.LegacySingleField
                || e.TournamentPhase == (int)SimulationKernel.Cups.TypeCupTournamentFormat.TournamentPhase.Final)
            .ToList();
        List<int> rounds = await context.TypeCupTeamRounds
            .AsNoTracking().Select(e => e.SourceSeasonNumber).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false);
        List<(int Season, string TeamKey, string TeamName)> appearances = selections
            .Select(s => (s.SourceSeasonNumber, s.CreatureType))
            .Distinct()
            .Select(p => (p.SourceSeasonNumber, p.CreatureType, p.CreatureType))
            .ToList();
        List<(int Season, string TeamKey, string TeamName, int Rank, string Medal, int Score)> results = standings
            .Select(s => (s.SourceSeasonNumber, s.CreatureType, s.CreatureType, s.TeamRank, ((TypeCupMedal)s.Medal).ToString(), s.TeamScoreThousandths))
            .ToList();
        return new TypeCupRows(appearances, results, [.. rounds]);
    }

    internal static IReadOnlyList<AlmanacCupEditionEntry> BuildEditions(ColorCupRows color, TypeCupRows type)
    {
        List<AlmanacCupEditionEntry> editions = [];
        editions.AddRange(BuildCupEditions("Color", color.Appearances, color.Results, color.PlayedSeasons, color.Champions));
        editions.AddRange(BuildCupEditions("Type", type.Appearances, type.Results, type.PlayedSeasons, null));
        return editions.OrderByDescending(e => e.SourceSeasonNumber).ThenBy(e => e.Cup, StringComparer.Ordinal).ToList();
    }

    internal static List<AlmanacCupEditionEntry> BuildCupEditions(
        string cup,
        List<(int Season, string TeamKey, string TeamName)> appearances,
        List<(int Season, string TeamKey, string TeamName, int Rank, string Medal, int Score)> results,
        HashSet<int> playedSeasons,
        Dictionary<int, (int AthleteId, string Name)>? champions)
    {
        List<AlmanacCupEditionEntry> editions = [];
        foreach (IGrouping<int, (int Season, string TeamKey, string TeamName)> season in appearances.GroupBy(a => a.Season))
        {
            List<(int Season, string TeamKey, string TeamName, int Rank, string Medal, int Score)> seasonResults = results.Where(r => r.Season == season.Key).ToList();
            (int AthleteId, string Name) championValue = default;
            bool hasChampion = champions is not null && champions.TryGetValue(season.Key, out championValue);
            (int AthleteId, string Name)? champion = hasChampion ? championValue : null;
            bool teamComplete = seasonResults.Count > 0;
            bool complete = teamComplete && (champions is null || champion is not null);
            bool anyPlayed = teamComplete || champion is not null || playedSeasons.Contains(season.Key);
            string state = CupEditionState.For(anyPlayed, complete);
            List<AlmanacCupPodium> podium = seasonResults
                .Where(r => r.Rank <= 3)
                .OrderBy(r => r.Rank)
                .Select(r => new AlmanacCupPodium(r.TeamKey, r.TeamName, r.Rank, r.Medal, r.Score))
                .ToList();
            editions.Add(new AlmanacCupEditionEntry(cup, season.Key, state, season.Count(), podium, champion?.Name, champion?.AthleteId));
        }

        return editions;
    }

    internal static List<ListCupEditionsResponse.TeamSummary> BuildTeamTable(
        List<(int Season, string TeamKey, string TeamName)> appearances,
        List<(int Season, string TeamKey, string TeamName, int Rank, string Medal, int Score)> results)
    {
        return appearances
            .GroupBy(a => a.TeamKey, StringComparer.Ordinal)
            .Select(team =>
            {
                List<(int Season, string TeamKey, string TeamName, int Rank, string Medal, int Score)> teamResults = results.Where(r => string.Equals(r.TeamKey, team.Key, StringComparison.Ordinal)).ToList();
                return new ListCupEditionsResponse.TeamSummary(
                    team.Key,
                    team.First().TeamName,
                    team.Count(),
                    teamResults.Count(r => r.Rank == 1),
                    teamResults.Count(r => r.Rank == 2),
                    teamResults.Count(r => r.Rank == 3),
                    teamResults.Count == 0 ? null : teamResults.Min(r => r.Rank),
                    team.Max(a => a.Season));
            })
            .OrderByDescending(t => t.Gold)
            .ThenByDescending(t => t.Silver)
            .ThenByDescending(t => t.Bronze)
            .ThenBy(t => t.TeamName, StringComparer.Ordinal)
            .ToList();
    }

    internal static List<ListCupEditionsResponse.ColorMedalRow> BuildIndividualMedals(
        List<(int Season, string TeamKey, string TeamName)> appearances,
        List<(int Season, string TeamKey, int Medal)> medals)
    {
        if (appearances.Count == 0)
        {
            return [];
        }

        // Only completed Color editions count: an individual event can resolve
        // while its team event is still pending. Mirror ListCupEditions: count
        // individual medals only where the team event has finished.
        HashSet<int> completedSeasons = medals.Select(m => m.Season).ToHashSet();
        return Enum.GetValues<SportingColor>()
            .Select(color =>
            {
                string key = CupTeamKeys.ColorKey(color);
                string name = CupTeamKeys.ColorName(color);
                List<(int Season, string TeamKey, int Medal)> colorMedals = medals.Where(m => string.Equals(m.TeamKey, key, StringComparison.Ordinal)).ToList();
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

    internal static List<ListCupEditionsResponse.ColorMedalRow> BuildCombinedMedals(
        IReadOnlyList<ListCupEditionsResponse.TeamSummary> teamTable,
        IReadOnlyList<ListCupEditionsResponse.ColorMedalRow> individualTable)
    {
        if (teamTable.Count == 0 && individualTable.Count == 0)
        {
            return [];
        }

        Dictionary<string, ListCupEditionsResponse.TeamSummary> teams = teamTable.ToDictionary(t => t.TeamKey, StringComparer.Ordinal);
        Dictionary<string, ListCupEditionsResponse.ColorMedalRow> individuals = individualTable.ToDictionary(t => t.TeamKey, StringComparer.Ordinal);
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
                return new ListCupEditionsResponse.ColorMedalRow(
                    key,
                    name,
                    (team?.Gold ?? 0) + (individual?.Gold ?? 0),
                    (team?.Silver ?? 0) + (individual?.Silver ?? 0),
                    (team?.Bronze ?? 0) + (individual?.Bronze ?? 0));
            })
            .OrderByDescending(t => t.Gold)
            .ThenByDescending(t => t.Silver)
            .ThenByDescending(t => t.Bronze)
            .ThenBy(t => t.TeamName, StringComparer.Ordinal)
            .ToList();
    }
}
