using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Cups.CupHistory;
using MtgSoloSports.Features.Cups.RunColorCupIndividual;
using MtgSoloSports.Features.Cups.RunTypeCupTeam;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;

namespace MtgSoloSports.Features.Reports.UniverseAlmanac;

internal static class UniverseAlmanacQueries
{
    internal static async Task<UniverseAlmanacData> LoadAsync(
        SaveDbContext context,
        CancellationToken cancellationToken)
    {
        List<SeasonEntity> seasons = await LoadSeasonsAsync(context, cancellationToken).ConfigureAwait(false);
        Dictionary<int, string> names = await LoadNamesAsync(context, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<AlmanacSeasonEntry> seasonEntries = await LoadSeasonEntriesAsync(context, seasons, names, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<SuperleaguePodiumEntry> podiums = await LoadSuperleaguePodiumsAsync(context, seasons, names, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<FeederTitleEntry> feederTitles = await LoadFeederTitlesAsync(context, seasons, names, cancellationToken).ConfigureAwait(false);
        AlmanacCupTables cups = await UniverseAlmanacCupQueries.LoadCupTablesAsync(context, names, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<AlmanacCareerRecordEntry> records = await UniverseAlmanacLeaderQueries.LoadCareerRecordsAsync(context, cancellationToken).ConfigureAwait(false);
        AlmanacCareerLeaders leaders = await UniverseAlmanacLeaderQueries.LoadWeightedLeadersAsync(context, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<LeaguePointsLeaderEntry> pointsLeaders = await UniverseAlmanacLeaderQueries.LoadSuperleaguePointsLeadersAsync(context, names, cancellationToken).ConfigureAwait(false);
        (IReadOnlyList<StreakEntry> titleStreaks, IReadOnlyList<StreakEntry> stageStreaks) = UniverseAlmanacLeaderQueries.BuildStreaks(leaders);
        int from = seasons.Count == 0 ? 0 : seasons.Min(s => s.SeasonNumber);
        int to = seasons.Count == 0 ? 0 : seasons.Max(s => s.SeasonNumber);
        return new UniverseAlmanacData(
            seasons.Count,
            from,
            to,
            seasonEntries,
            podiums,
            feederTitles,
            cups.Editions,
            cups.ColorTeamTable,
            cups.ColorIndividualMedals,
            cups.ColorCombinedMedals,
            cups.TypeTeamTable,
            records,
            leaders,
            pointsLeaders,
            titleStreaks,
            stageStreaks);
    }

    internal static async Task<List<SeasonEntity>> LoadSeasonsAsync(SaveDbContext context, CancellationToken cancellationToken)
    {
        return await context.Seasons
            .AsNoTracking()
            .OrderBy(e => e.SeasonNumber)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<Dictionary<int, string>> LoadNamesAsync(SaveDbContext context, CancellationToken cancellationToken)
    {
        return await context.SaveAthletes
            .AsNoTracking()
            .ToDictionaryAsync(e => e.Id, e => e.Name, cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<IReadOnlyList<AlmanacSeasonEntry>> LoadSeasonEntriesAsync(
        SaveDbContext context,
        List<SeasonEntity> seasons,
        Dictionary<int, string> names,
        CancellationToken cancellationToken)
    {
        if (seasons.Count == 0)
        {
            return [];
        }

        Dictionary<int, LeagueEntity?> superleagueBySeason = await MapSuperleagueBySeasonAsync(context, seasons, cancellationToken).ConfigureAwait(false);
        Dictionary<int, SeasonStandingEntity> champions = await MapChampionsAsync(context, seasons, superleagueBySeason, cancellationToken).ConfigureAwait(false);
        Dictionary<int, (string Color, string Type)> cupStates = await MapCupStatesAsync(context, seasons, cancellationToken).ConfigureAwait(false);
        List<AlmanacSeasonEntry> entries = new(seasons.Count);
        foreach (SeasonEntity season in seasons)
        {
            superleagueBySeason.TryGetValue(season.Id, out LeagueEntity? superleague);
            champions.TryGetValue(season.Id, out SeasonStandingEntity? champion);
            cupStates.TryGetValue(season.SeasonNumber, out (string Color, string Type) states);
            string championName = "(no Superleague in Season 1)";
            int? championId = null;
            if (champion is not null)
            {
                names.TryGetValue(champion.SaveAthleteId, out string? name);
                championName = name ?? $"Athlete {champion.SaveAthleteId}";
                championId = champion.SaveAthleteId;
            }
            else if (superleague is not null && season.IsComplete)
            {
                championName = "— (final table missing; corrupted if final)";
            }

            entries.Add(new AlmanacSeasonEntry(
                season.SeasonNumber,
                season.IsComplete,
                superleague is not null,
                superleague is null ? null : championName,
                championId,
                string.IsNullOrEmpty(states.Color) ? "NotHeld" : states.Color,
                string.IsNullOrEmpty(states.Type) ? "NotHeld" : states.Type));
        }

        return entries;
    }

    internal static async Task<Dictionary<int, LeagueEntity?>> MapSuperleagueBySeasonAsync(
        SaveDbContext context,
        List<SeasonEntity> seasons,
        CancellationToken cancellationToken)
    {
        HashSet<int> ids = seasons.Select(s => s.Id).ToHashSet();
        List<LeagueEntity> leagues = await context.Leagues
            .AsNoTracking()
            .Where(e => ids.Contains(e.SeasonId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        Dictionary<int, LeagueEntity?> mapped = new();
        foreach (SeasonEntity season in seasons)
        {
            List<LeagueEntity> seasonLeagues = leagues.Where(l => l.SeasonId == season.Id).ToList();
            mapped[season.Id] = seasonLeagues.FirstOrDefault(l => l.Kind == (int)LeagueKind.Superleague);
        }

        return mapped;
    }

    internal static async Task<Dictionary<int, SeasonStandingEntity>> MapChampionsAsync(
        SaveDbContext context,
        List<SeasonEntity> seasons,
        Dictionary<int, LeagueEntity?> superleagueBySeason,
        CancellationToken cancellationToken)
    {
        HashSet<int> seasonIds = seasons.Select(s => s.Id).ToHashSet();
        List<SeasonStandingEntity> rows = await context.SeasonStandings
            .AsNoTracking()
            .Where(e => seasonIds.Contains(e.SeasonId) && e.SeasonRank == 1)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        Dictionary<int, SeasonStandingEntity> mapped = new();
        foreach (SeasonStandingEntity row in rows)
        {
            if (superleagueBySeason.TryGetValue(row.SeasonId, out LeagueEntity? superleague)
                && superleague is not null
                && row.LeagueId == superleague.Id)
            {
                mapped[row.SeasonId] = row;
            }
        }

        return mapped;
    }

    internal static async Task<Dictionary<int, (string Color, string Type)>> MapCupStatesAsync(
        SaveDbContext context,
        List<SeasonEntity> seasons,
        CancellationToken cancellationToken)
    {
        List<ColorCupSelectionEntity> colorSelections = await context.ColorCupSelections
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<TypeCupSelectionEntity> typeSelections = await context.TypeCupSelections
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        HashSet<int> colorSeasons = colorSelections.Select(s => s.SourceSeasonNumber).ToHashSet();
        HashSet<int> typeSeasons = typeSelections.Select(s => s.SourceSeasonNumber).ToHashSet();
        List<ColorCupTeamStandingEntity> colorTeams = await context.ColorCupTeamStandings
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<ColorCupIndividualStandingEntity> colorIndividuals = await context.ColorCupIndividualStandings
            .AsNoTracking()
            .Where(e => e.CupRank == 1)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<TypeCupTeamStandingEntity> typeFinals = await context.TypeCupTeamStandings
            .AsNoTracking()
            .Where(e => e.TournamentPhase == (int)SimulationKernel.Cups.TypeCupTournamentFormat.TournamentPhase.Final
                || e.TournamentPhase == (int)SimulationKernel.Cups.TypeCupTournamentFormat.TournamentPhase.LegacySingleField)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        Dictionary<int, (string Color, string Type)> states = new();
        foreach (SeasonEntity season in seasons)
        {
            string color = "NotHeld";
            if (colorSeasons.Contains(season.SeasonNumber))
            {
                bool teamDone = colorTeams.Any(t => t.SourceSeasonNumber == season.SeasonNumber);
                bool individualDone = colorIndividuals.Any(t => t.SourceSeasonNumber == season.SeasonNumber);
                color = CupEditionState.For(teamDone || individualDone, teamDone && individualDone);
            }

            string type = "NotHeld";
            if (typeSeasons.Contains(season.SeasonNumber))
            {
                bool finalDone = typeFinals.Any(t => t.SourceSeasonNumber == season.SeasonNumber);
                bool anyPlayed = finalDone || await HasTypeRoundsAsync(context, season.SeasonNumber, cancellationToken).ConfigureAwait(false);
                type = CupEditionState.For(anyPlayed, finalDone);
            }

            states[season.SeasonNumber] = (color, type);
        }

        return states;
    }

    internal static async Task<bool> HasTypeRoundsAsync(SaveDbContext context, int seasonNumber, CancellationToken cancellationToken)
    {
        return await context.TypeCupTeamRounds
            .AsNoTracking()
            .AnyAsync(e => e.SourceSeasonNumber == seasonNumber, cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<IReadOnlyList<SuperleaguePodiumEntry>> LoadSuperleaguePodiumsAsync(
        SaveDbContext context,
        List<SeasonEntity> seasons,
        Dictionary<int, string> names,
        CancellationToken cancellationToken)
    {
        if (seasons.Count == 0)
        {
            return [];
        }

        Dictionary<int, LeagueEntity?> superleagueBySeason = await MapSuperleagueBySeasonAsync(context, seasons, cancellationToken).ConfigureAwait(false);
        HashSet<int> superleagueIds = superleagueBySeason.Values.Where(l => l is not null).Select(l => l!.Id).ToHashSet();
        HashSet<int> seasonIds = seasons.Select(s => s.Id).ToHashSet();
        List<SeasonStandingEntity> podiums = await context.SeasonStandings
            .AsNoTracking()
            .Where(e => seasonIds.Contains(e.SeasonId) && superleagueIds.Contains(e.LeagueId) && e.SeasonRank <= 3)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<SuperleaguePodiumEntry> entries = new(seasons.Count);
        foreach (SeasonEntity season in seasons)
        {
            if (!superleagueBySeason.TryGetValue(season.Id, out LeagueEntity? superleague) || superleague is null)
            {
                entries.Add(new SuperleaguePodiumEntry(season.SeasonNumber, season.IsComplete, null, null, null));
                continue;
            }

            List<SeasonStandingEntity> rows = podiums.Where(r => r.SeasonId == season.Id && r.LeagueId == superleague.Id).ToList();
            entries.Add(new SuperleaguePodiumEntry(
                season.SeasonNumber,
                season.IsComplete,
                ToAlmanacPodium(rows, 1, names),
                ToAlmanacPodium(rows, 2, names),
                ToAlmanacPodium(rows, 3, names)));
        }

        return entries;
    }

    internal static AlmanacPodium? ToAlmanacPodium(List<SeasonStandingEntity> rows, int rank, Dictionary<int, string> names)
    {
        SeasonStandingEntity? row = rows.FirstOrDefault(r => r.SeasonRank == rank);
        if (row is null)
        {
            return null;
        }

        names.TryGetValue(row.SaveAthleteId, out string? name);
        return new AlmanacPodium(row.SaveAthleteId, name ?? $"Athlete {row.SaveAthleteId}", row.TotalChampionshipPointsThousandths);
    }

    internal static async Task<IReadOnlyList<FeederTitleEntry>> LoadFeederTitlesAsync(
        SaveDbContext context,
        List<SeasonEntity> seasons,
        Dictionary<int, string> names,
        CancellationToken cancellationToken)
    {
        if (seasons.Count == 0)
        {
            return [];
        }

        HashSet<int> seasonIds = seasons.Select(s => s.Id).ToHashSet();
        List<LeagueEntity> leagues = await context.Leagues
            .AsNoTracking()
            .Where(e => seasonIds.Contains(e.SeasonId) && e.Kind == (int)LeagueKind.Feeder)
            .OrderBy(e => e.SeasonId)
            .ThenBy(e => e.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (leagues.Count == 0)
        {
            return [];
        }

        HashSet<int> leagueIds = leagues.Select(l => l.Id).ToHashSet();
        List<SeasonStandingEntity> champions = await context.SeasonStandings
            .AsNoTracking()
            .Where(e => seasonIds.Contains(e.SeasonId) && leagueIds.Contains(e.LeagueId) && e.SeasonRank == 1)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        Dictionary<(int SeasonId, int LeagueId), SeasonStandingEntity> byKey = champions.ToDictionary(r => (r.SeasonId, r.LeagueId));
        Dictionary<int, SeasonEntity> seasonsById = seasons.ToDictionary(s => s.Id);
        List<FeederTitleEntry> entries = new(leagues.Count);
        foreach (LeagueEntity league in leagues)
        {
            if (!seasonsById.TryGetValue(league.SeasonId, out SeasonEntity? season))
            {
                continue;
            }

            byKey.TryGetValue((league.SeasonId, league.Id), out SeasonStandingEntity? champion);
            AlmanacPodium? podium = null;
            if (champion is not null)
            {
                names.TryGetValue(champion.SaveAthleteId, out string? name);
                podium = new AlmanacPodium(champion.SaveAthleteId, name ?? $"Athlete {champion.SaveAthleteId}", champion.TotalChampionshipPointsThousandths);
            }

            string level = LeagueEntityLevels.GetLevel(league).ToString();
            entries.Add(new FeederTitleEntry(season.SeasonNumber, league.Id, league.Name, level, season.IsComplete, podium));
        }

        return entries;
    }
}
