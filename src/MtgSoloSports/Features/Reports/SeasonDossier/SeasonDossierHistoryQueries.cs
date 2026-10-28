using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Records;
using MtgSoloSports.Features.Reports.StandingsReport;
using MtgSoloSports.Features.Stories;
using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Reports.SeasonDossier;

internal static class SeasonDossierHistoryQueries
{
    internal static async Task<HistorySection> LoadHistoryAsync(
        SaveDbContext context,
        SeasonEntity season,
        LeagueEntity superleague,
        GetStandingsReportHandler.ReportData standings,
        CupSummaryEntry? color,
        CupSummaryEntry? type,
        Dictionary<int, string> names,
        CancellationToken cancellationToken)
    {
        List<SeasonEntity> window = await LoadWindowAsync(context, season, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ChampionSeasonEntry> lastFive = await LoadChampionsAsync(context, window, names, cancellationToken).ConfigureAwait(false);
        (PodiumEntry? previous, PodiumEntry? defending) = PickPreviousAndDefending(window, lastFive);
        IReadOnlyList<LeaderCareerEntry> leaders = await LoadLeaderCareersAsync(context, standings, names, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<LeaderCareerEntry> performers = await LoadPerformerCareersAsync(context, color, type, leaders, names, cancellationToken).ConfigureAwait(false);
        return new HistorySection(season.SeasonNumber - 1 <= 0 ? null : season.SeasonNumber - 1, previous, defending, lastFive, leaders, performers);
    }

    internal static async Task<List<SeasonEntity>> LoadWindowAsync(
        SaveDbContext context,
        SeasonEntity season,
        CancellationToken cancellationToken)
    {
        return await context.Seasons
            .AsNoTracking()
            .Where(e => e.SeasonNumber <= season.SeasonNumber)
            .OrderByDescending(e => e.SeasonNumber)
            .Take(5)
            .OrderBy(e => e.SeasonNumber)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<IReadOnlyList<ChampionSeasonEntry>> LoadChampionsAsync(
        SaveDbContext context,
        List<SeasonEntity> window,
        Dictionary<int, string> names,
        CancellationToken cancellationToken)
    {
        if (window.Count == 0)
        {
            return [];
        }

        HashSet<int> seasonIds = window.Select(s => s.Id).ToHashSet();
        List<LeagueEntity> leagues = await context.Leagues
            .AsNoTracking()
            .Where(e => seasonIds.Contains(e.SeasonId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        Dictionary<int, int> superleagueBySeason = PickSuperleagueBySeason(window, leagues);
        List<SeasonStandingEntity> champions = await context.SeasonStandings
            .AsNoTracking()
            .Where(e => seasonIds.Contains(e.SeasonId) && e.SeasonRank == 1)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        Dictionary<(int SeasonId, int LeagueId), SeasonStandingEntity> byKey = champions
            .ToDictionary(r => (r.SeasonId, r.LeagueId));
        List<ChampionSeasonEntry> entries = new(window.Count);
        foreach (SeasonEntity season in window.OrderBy(s => s.SeasonNumber))
        {
            PodiumEntry? podium = null;
            if (superleagueBySeason.TryGetValue(season.Id, out int leagueId)
                && byKey.TryGetValue((season.Id, leagueId), out SeasonStandingEntity? row))
            {
                names.TryGetValue(row.SaveAthleteId, out string? name);
                podium = new PodiumEntry(row.SaveAthleteId, name ?? $"Athlete {row.SaveAthleteId}", row.TotalChampionshipPointsThousandths);
            }

            entries.Add(new ChampionSeasonEntry(season.SeasonNumber, podium, season.IsComplete));
        }

        return entries;
    }

    internal static Dictionary<int, int> PickSuperleagueBySeason(List<SeasonEntity> window, List<LeagueEntity> leagues)
    {
        Dictionary<int, int> picked = new();
        foreach (SeasonEntity season in window)
        {
            List<LeagueEntity> seasonLeagues = leagues.Where(l => l.SeasonId == season.Id).OrderBy(l => l.Id).ToList();
            if (seasonLeagues.Count == 0)
            {
                continue;
            }

            LeagueEntity official = seasonLeagues.FirstOrDefault(l => l.Kind == (int)LeagueKind.Superleague) ?? seasonLeagues[0];
            picked[season.Id] = official.Id;
        }

        return picked;
    }

    internal static (PodiumEntry? Previous, PodiumEntry? Defending) PickPreviousAndDefending(
        List<SeasonEntity> window,
        IReadOnlyList<ChampionSeasonEntry> lastFive)
    {
        if (window.Count <= 1)
        {
            return (null, null);
        }

        int previousNumber = window.OrderBy(s => s.SeasonNumber).Last().SeasonNumber - 1;
        ChampionSeasonEntry? previous = lastFive.FirstOrDefault(e => e.SeasonNumber == previousNumber);
        return (previous?.Champion, previous?.Champion);
    }

    internal static async Task<IReadOnlyList<LeaderCareerEntry>> LoadLeaderCareersAsync(
        SaveDbContext context,
        GetStandingsReportHandler.ReportData standings,
        Dictionary<int, string> names,
        CancellationToken cancellationToken)
    {
        List<int> topIds = standings.Rows.OrderBy(r => r.Rank).Take(5).Select(r => r.AthleteId).ToList();
        if (topIds.Count == 0)
        {
            return [];
        }

        return await LoadCareerEntriesAsync(context, topIds, standings, names, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<IReadOnlyList<LeaderCareerEntry>> LoadPerformerCareersAsync(
        SaveDbContext context,
        CupSummaryEntry? color,
        CupSummaryEntry? type,
        IReadOnlyList<LeaderCareerEntry> leaders,
        Dictionary<int, string> names,
        CancellationToken cancellationToken)
    {
        HashSet<int> leaderIds = leaders.Select(l => l.AthleteId).ToHashSet();
        List<int> performerIds = CollectPerformerIds(color, type).Where(id => !leaderIds.Contains(id)).Take(12).ToList();
        if (performerIds.Count == 0)
        {
            return [];
        }

        return await LoadCareerEntriesAsync(context, performerIds, null, names, cancellationToken).ConfigureAwait(false);
    }

    internal static List<int> CollectPerformerIds(CupSummaryEntry? color, CupSummaryEntry? type)
    {
        List<int> ids = [];
        if (color?.Individual?.ChampionAthleteId is int individualId)
        {
            ids.Add(individualId);
        }

        if (color?.Team?.WinningRoster is { Count: > 0 } colorRoster)
        {
            ids.AddRange(colorRoster.Select(r => r.AthleteId));
        }

        if (type?.Tournament?.WinningRoster is { Count: > 0 } typeRoster)
        {
            ids.AddRange(typeRoster.Select(r => r.AthleteId));
        }

        return ids.Distinct().ToList();
    }

    internal static async Task<IReadOnlyList<LeaderCareerEntry>> LoadCareerEntriesAsync(
        SaveDbContext context,
        List<int> athleteIds,
        GetStandingsReportHandler.ReportData? standings,
        Dictionary<int, string> names,
        CancellationToken cancellationToken)
    {
        Dictionary<int, GetStandingsReportHandler.StandingsReportRow> rows = standings?.Rows
            .ToDictionary(r => r.AthleteId) ?? new Dictionary<int, GetStandingsReportHandler.StandingsReportRow>();
        List<HonourEntity> honours = await context.Honours
            .AsNoTracking()
            .Where(e => athleteIds.Contains(e.SaveAthleteId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<AthleteCareerEntity> careers = await context.AthleteCareers
            .AsNoTracking()
            .Where(e => athleteIds.Contains(e.SaveAthleteId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        Dictionary<int, AthleteCareerEntity> careersById = careers.ToDictionary(c => c.SaveAthleteId);
        List<LeaderCareerEntry> entries = new(athleteIds.Count);
        foreach (int athleteId in athleteIds.OrderBy(id => id))
        {
            rows.TryGetValue(athleteId, out GetStandingsReportHandler.StandingsReportRow? row);
            int superTitles = honours.Count(h => h.SaveAthleteId == athleteId && h.Kind == (int)HonourKind.SuperleagueTitle);
            int feederTitles = honours.Count(h => h.SaveAthleteId == athleteId && h.Kind == (int)HonourKind.FeederTitle);
            careersById.TryGetValue(athleteId, out AthleteCareerEntity? career);
            names.TryGetValue(athleteId, out string? name);
            entries.Add(new LeaderCareerEntry(
                athleteId,
                name ?? row?.Name ?? $"Athlete {athleteId}",
                row?.Rank,
                row?.TotalChampionshipPointsThousandths ?? 0,
                superTitles,
                feederTitles,
                superTitles + feederTitles,
                career?.SeasonsActive ?? 0));
        }

        return entries.OrderBy(e => e.CurrentRank ?? int.MaxValue).ThenBy(e => e.AthleteId).ToList();
    }

    internal static async Task<IReadOnlyList<CareerRecordEntry>> LoadCareerRecordsAsync(
        SaveDbContext context,
        CancellationToken cancellationToken)
    {
        RecordLoader.RecordInputs inputs = await RecordLoader.LoadAsync(context, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<RecordCalculator.RecordHolders> computed = RecordCalculator.ComputeAll(
            inputs.AthleteNames,
            inputs.SeasonStandings,
            inputs.StageStandings,
            inputs.Memberships,
            inputs.Careers,
            inputs.Promotions);
        List<CareerRecordEntry> entries = new(computed.Count);
        foreach (RecordCalculator.RecordHolders holders in computed)
        {
            entries.Add(MapRecord(holders, inputs.AthleteNames));
        }

        return entries;
    }

    internal static CareerRecordEntry MapRecord(
        RecordCalculator.RecordHolders holders,
        Dictionary<int, string> names)
    {
        string label = StoryEventRenderer.FormatRecordKey(holders.RecordKey);
        string display = StoryEventRenderer.FormatRecordValue(holders.RecordKey, holders.Value) ?? holders.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        List<CareerRecordHolder> holderEntries = holders.HolderAthleteIds
            .Select(id =>
            {
                names.TryGetValue(id, out string? name);
                return new CareerRecordHolder(id, name ?? $"Athlete {id}");
            })
            .ToList();
        return new CareerRecordEntry(holders.RecordKey, label, holders.Value, display, holderEntries);
    }
}
