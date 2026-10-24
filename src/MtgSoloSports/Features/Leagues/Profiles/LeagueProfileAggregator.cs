using System.Globalization;

namespace MtgSoloSports.Features.Leagues.Profiles;

/// <summary>
/// Pure deterministic aggregation for league profiles. Inputs are already-loaded
/// completed final <c>SeasonStanding</c> rows (authoritative per-athlete,
/// per-league, per-season totals used exactly once); no round payloads, no
/// resimulation, no RNG. All ordering is deterministic; ties share records.
/// </summary>
public static class LeagueProfileAggregator
{
    public sealed record LeagueStandingInput(
        int SaveAthleteId,
        int SeasonRank,
        int TotalChampionshipPointsThousandths,
        bool IsChampion);

    public sealed record LeagueSeasonInput(
        int SeasonNumber,
        int SeasonId,
        int LeagueId,
        int Kind,
        int FeederDivision,
        int SportingColor,
        string LeagueName,
        bool IsComplete,
        IReadOnlyList<LeagueStandingInput> Standings);

    public static IReadOnlyList<GetLeagueProfileResponse.PodiumSeason> BuildPodiumHistory(
        IReadOnlyList<LeagueSeasonInput> seasons,
        IReadOnlyDictionary<int, string> names)
    {
        ArgumentNullException.ThrowIfNull(seasons);
        ArgumentNullException.ThrowIfNull(names);
        List<GetLeagueProfileResponse.PodiumSeason> history = new();
        foreach (LeagueSeasonInput season in seasons
                     .Where(s => s.IsComplete && s.Standings.Count > 0)
                     .OrderByDescending(s => s.SeasonNumber))
        {
            List<LeagueStandingInput> ordered = season.Standings.OrderBy(r => r.SeasonRank).ToList();
            GetLeagueProfileResponse.PodiumAthlete? champion = ToPodium(ordered, 1, names);
            GetLeagueProfileResponse.PodiumAthlete? runnerUp = ToPodium(ordered, 2, names);
            GetLeagueProfileResponse.PodiumAthlete? third = ToPodium(ordered, 3, names);
            int? margin = champion is not null && runnerUp is not null
                ? champion.TotalChampionshipPointsThousandths - runnerUp.TotalChampionshipPointsThousandths
                : null;
            history.Add(new GetLeagueProfileResponse.PodiumSeason(
                season.SeasonNumber,
                season.LeagueId,
                true,
                true,
                champion,
                runnerUp,
                third,
                margin));
        }

        return history;
    }

    public static IReadOnlyList<GetLeagueProfileResponse.AllTimeRow> BuildLeaderboard(
        IReadOnlyList<LeagueSeasonInput> seasons,
        IReadOnlyDictionary<int, string> names)
    {
        ArgumentNullException.ThrowIfNull(seasons);
        ArgumentNullException.ThrowIfNull(names);
        Dictionary<int, MutableCareer> careers = AccumulateCareers(seasons);
        return OrderCareers(careers, names);
    }

    private static Dictionary<int, MutableCareer> AccumulateCareers(IReadOnlyList<LeagueSeasonInput> seasons)
    {
        ArgumentNullException.ThrowIfNull(seasons);
        Dictionary<int, MutableCareer> careers = new();
        foreach (LeagueSeasonInput season in seasons.Where(s => s.IsComplete && s.Standings.Count > 0))
        {
            foreach (LeagueStandingInput row in season.Standings)
            {
                AccumulateRow(careers, row);
            }
        }

        return careers;
    }

    private static void AccumulateRow(Dictionary<int, MutableCareer> careers, LeagueStandingInput row)
    {
        ArgumentNullException.ThrowIfNull(careers);
        ArgumentNullException.ThrowIfNull(row);
        if (!careers.TryGetValue(row.SaveAthleteId, out MutableCareer? career))
        {
            career = new MutableCareer();
            careers[row.SaveAthleteId] = career;
        }

        career.TotalPoints = checked(career.TotalPoints + row.TotalChampionshipPointsThousandths);
        career.Seasons++;
        career.BestRank = Math.Min(career.BestRank, row.SeasonRank);
        if (row.SeasonRank == 1)
        {
            career.Titles++;
        }

        if (row.SeasonRank == 2)
        {
            career.RunnerUps++;
        }

        if (row.SeasonRank is 1 or 2 or 3)
        {
            career.Podiums++;
        }
    }

    private static IReadOnlyList<GetLeagueProfileResponse.AllTimeRow> OrderCareers(
        Dictionary<int, MutableCareer> careers,
        IReadOnlyDictionary<int, string> names)
    {
        ArgumentNullException.ThrowIfNull(careers);
        ArgumentNullException.ThrowIfNull(names);
        return careers
            .Select(kv =>
            {
                names.TryGetValue(kv.Key, out string? name);
                return new GetLeagueProfileResponse.AllTimeRow(
                    kv.Key,
                    name ?? $"Athlete {kv.Key}",
                    kv.Value.TotalPoints,
                    kv.Value.Seasons,
                    kv.Value.BestRank == int.MaxValue ? 0 : kv.Value.BestRank,
                    kv.Value.Titles,
                    kv.Value.Podiums,
                    kv.Value.RunnerUps);
            })
            .OrderByDescending(r => r.TotalLeaguePointsThousandths)
            .ThenByDescending(r => r.Titles)
            .ThenByDescending(r => r.Podiums)
            .ThenBy(r => r.BestRank == 0 ? int.MaxValue : r.BestRank)
            .ThenBy(r => r.AthleteName, StringComparer.Ordinal)
            .ThenBy(r => r.AthleteId)
            .ToList();
    }

    public static IReadOnlyList<GetLeagueProfileResponse.LeagueRecordEntry> BuildRecords(
        IReadOnlyList<LeagueSeasonInput> seasons,
        IReadOnlyDictionary<int, string> names)
    {
        ArgumentNullException.ThrowIfNull(seasons);
        ArgumentNullException.ThrowIfNull(names);
        List<LeagueSeasonInput> completed = seasons
            .Where(s => s.IsComplete && s.Standings.Count > 0)
            .OrderBy(s => s.SeasonNumber)
            .ToList();
        if (completed.Count == 0)
        {
            return [];
        }

        bool mixedFieldSizes = completed.Select(s => s.Standings.Count).Distinct().Count() > 1;
        string? scopeNote = mixedFieldSizes
            ? "Field sizes differ across seasons; cross-season point comparisons are approximate."
            : null;

        RecordInputs collected = CollectRecordInputs(completed);
        return EmitRecords(collected, completed, names, scopeNote);
    }

    internal sealed record RecordInputs(
        Dictionary<int, List<int>> TitlesByAthlete,
        Dictionary<int, List<int>> PodiumsByAthlete,
        Dictionary<int, List<int>> AppearancesByAthlete,
        Dictionary<int, List<int>> RunnerUpsByAthlete,
        Dictionary<int, List<PeakSeason>> PeaksByAthlete,
        List<MarginSeason> Margins);

    internal static RecordInputs CollectRecordInputs(List<LeagueSeasonInput> completed)
    {
        ArgumentNullException.ThrowIfNull(completed);
        Dictionary<int, List<int>> titlesByAthlete = new();
        Dictionary<int, List<int>> podiumsByAthlete = new();
        Dictionary<int, List<int>> appearancesByAthlete = new();
        Dictionary<int, List<int>> runnerUpsByAthlete = new();
        Dictionary<int, List<PeakSeason>> peaksByAthlete = new();
        List<MarginSeason> margins = new();
        foreach (LeagueSeasonInput season in completed)
        {
            CollectSeason(season, appearancesByAthlete, podiumsByAthlete, runnerUpsByAthlete, titlesByAthlete, peaksByAthlete, margins);
        }

        return new RecordInputs(titlesByAthlete, podiumsByAthlete, appearancesByAthlete, runnerUpsByAthlete, peaksByAthlete, margins);
    }

    internal static void CollectSeason(
        LeagueSeasonInput season,
        Dictionary<int, List<int>> appearancesByAthlete,
        Dictionary<int, List<int>> podiumsByAthlete,
        Dictionary<int, List<int>> runnerUpsByAthlete,
        Dictionary<int, List<int>> titlesByAthlete,
        Dictionary<int, List<PeakSeason>> peaksByAthlete,
        List<MarginSeason> margins)
    {
        ArgumentNullException.ThrowIfNull(season);
        ArgumentNullException.ThrowIfNull(appearancesByAthlete);
        ArgumentNullException.ThrowIfNull(podiumsByAthlete);
        ArgumentNullException.ThrowIfNull(runnerUpsByAthlete);
        ArgumentNullException.ThrowIfNull(titlesByAthlete);
        ArgumentNullException.ThrowIfNull(peaksByAthlete);
        ArgumentNullException.ThrowIfNull(margins);
        List<LeagueStandingInput> ordered = season.Standings.OrderBy(r => r.SeasonRank).ToList();
        foreach (LeagueStandingInput row in ordered)
        {
            AddSeason(appearancesByAthlete, row.SaveAthleteId, season.SeasonNumber);
            if (row.SeasonRank is 1 or 2 or 3)
            {
                AddSeason(podiumsByAthlete, row.SaveAthleteId, season.SeasonNumber);
            }

            if (row.SeasonRank == 2)
            {
                AddSeason(runnerUpsByAthlete, row.SaveAthleteId, season.SeasonNumber);
            }

            if (row.SeasonRank == 1 || row.IsChampion)
            {
                AddSeason(titlesByAthlete, row.SaveAthleteId, season.SeasonNumber);
            }

            if (!peaksByAthlete.TryGetValue(row.SaveAthleteId, out List<PeakSeason>? peaks))
            {
                peaks = [];
                peaksByAthlete[row.SaveAthleteId] = peaks;
            }

            peaks.Add(new PeakSeason(season.SeasonNumber, row.TotalChampionshipPointsThousandths));
        }

        LeagueStandingInput? first = ordered.FirstOrDefault(r => r.SeasonRank == 1);
        LeagueStandingInput? second = ordered.FirstOrDefault(r => r.SeasonRank == 2);
        if (first is not null && second is not null)
        {
            margins.Add(new MarginSeason(
                season.SeasonNumber,
                first.SaveAthleteId,
                first.TotalChampionshipPointsThousandths - second.TotalChampionshipPointsThousandths));
        }
    }

    internal static IReadOnlyList<GetLeagueProfileResponse.LeagueRecordEntry> EmitRecords(
        RecordInputs collected,
        List<LeagueSeasonInput> completed,
        IReadOnlyDictionary<int, string> names,
        string? scopeNote)
    {
        ArgumentNullException.ThrowIfNull(collected);
        ArgumentNullException.ThrowIfNull(completed);
        ArgumentNullException.ThrowIfNull(names);
        List<GetLeagueProfileResponse.LeagueRecordEntry> records = new();
        AddCountRecord(records, "league_most_titles", "Most league championships", collected.TitlesByAthlete, names);
        AddCountRecord(records, "league_most_podiums", "Most podium finishes", collected.PodiumsByAthlete, names);
        AddCountRecord(records, "league_most_appearances", "Most league appearances", collected.AppearancesByAthlete, names);
        AddPeakRecord(records, collected.PeaksByAthlete, names, scopeNote);
        AddStreakRecord(records, completed, names);
        AddMarginRecord(records, collected.Margins, names, scopeNote);
        AddCountRecord(records, "league_most_runner_ups", "Most runner-up finishes", collected.RunnerUpsByAthlete, names);
        return records;
    }

    private static GetLeagueProfileResponse.PodiumAthlete? ToPodium(
        List<LeagueStandingInput> ordered,
        int rank,
        IReadOnlyDictionary<int, string> names)
    {
        LeagueStandingInput? row = ordered.FirstOrDefault(r => r.SeasonRank == rank);
        if (row is null)
        {
            return null;
        }

        names.TryGetValue(row.SaveAthleteId, out string? name);
        return new GetLeagueProfileResponse.PodiumAthlete(
            row.SaveAthleteId,
            name ?? $"Athlete {row.SaveAthleteId}",
            row.SeasonRank,
            row.TotalChampionshipPointsThousandths);
    }

    private sealed class MutableCareer
    {
        public long TotalPoints;
        public int Seasons;
        public int BestRank = int.MaxValue;
        public int Titles;
        public int Podiums;
        public int RunnerUps;
    }

    internal sealed record PeakSeason(int SeasonNumber, int PointsThousandths);

    internal sealed record MarginSeason(int SeasonNumber, int ChampionAthleteId, int MarginThousandths);

    private static void AddSeason(Dictionary<int, List<int>> byAthlete, int athleteId, int seasonNumber)
    {
        if (!byAthlete.TryGetValue(athleteId, out List<int>? seasons))
        {
            seasons = [];
            byAthlete[athleteId] = seasons;
        }

        if (!seasons.Contains(seasonNumber))
        {
            seasons.Add(seasonNumber);
        }
    }

    private static void AddCountRecord(
        List<GetLeagueProfileResponse.LeagueRecordEntry> records,
        string recordKey,
        string label,
        Dictionary<int, List<int>> byAthlete,
        IReadOnlyDictionary<int, string> names)
    {
        if (byAthlete.Count == 0)
        {
            return;
        }

        int max = byAthlete.Values.Max(seasons => seasons.Count);
        if (max <= 0)
        {
            return;
        }

        List<GetLeagueProfileResponse.LeagueRecordHolder> holders = byAthlete
            .Where(kv => kv.Value.Count == max)
            .Select(kv =>
            {
                names.TryGetValue(kv.Key, out string? name);
                List<int> orderedSeasons = kv.Value.OrderBy(s => s).ToList();
                return new GetLeagueProfileResponse.LeagueRecordHolder(
                    kv.Key,
                    name ?? $"Athlete {kv.Key}",
                    orderedSeasons,
                    orderedSeasons.Count == 1 ? orderedSeasons[0] : null,
                    null);
            })
            .OrderBy(h => h.AthleteName, StringComparer.Ordinal)
            .ThenBy(h => h.AthleteId)
            .ToList();
        records.Add(new GetLeagueProfileResponse.LeagueRecordEntry(
            recordKey,
            label,
            max,
            max.ToString(CultureInfo.InvariantCulture),
            holders,
            null));
    }

    private static void AddPeakRecord(
        List<GetLeagueProfileResponse.LeagueRecordEntry> records,
        Dictionary<int, List<PeakSeason>> peaksByAthlete,
        IReadOnlyDictionary<int, string> names,
        string? scopeNote)
    {
        int max = 0;
        bool found = false;
        foreach (List<PeakSeason> peaks in peaksByAthlete.Values)
        {
            foreach (PeakSeason peak in peaks)
            {
                if (!found || peak.PointsThousandths > max)
                {
                    max = peak.PointsThousandths;
                    found = true;
                }
            }
        }

        if (!found)
        {
            return;
        }

        List<GetLeagueProfileResponse.LeagueRecordHolder> holders = new();
        foreach ((int athleteId, List<PeakSeason> peaks) in peaksByAthlete)
        {
            List<int> atMax = peaks
                .Where(p => p.PointsThousandths == max)
                .Select(p => p.SeasonNumber)
                .OrderBy(s => s)
                .ToList();
            if (atMax.Count == 0)
            {
                continue;
            }

            names.TryGetValue(athleteId, out string? name);
            holders.Add(new GetLeagueProfileResponse.LeagueRecordHolder(
                athleteId,
                name ?? $"Athlete {athleteId}",
                atMax,
                atMax.Count == 1 ? atMax[0] : null,
                max));
        }

        holders = holders
            .OrderBy(h => h.AthleteName, StringComparer.Ordinal)
            .ThenBy(h => h.AthleteId)
            .ToList();
        records.Add(new GetLeagueProfileResponse.LeagueRecordEntry(
            "league_highest_season_points",
            "Highest single-season points",
            max,
            FormatPoints(max),
            holders,
            scopeNote));
    }

    private sealed class StreakTracker
    {
        public Dictionary<int, int> LongestByAthlete { get; } = new();

        public Dictionary<int, List<int>> LongestSeasonsByAthlete { get; } = new();

        public Dictionary<int, int> CurrentByAthlete { get; } = new();

        public Dictionary<int, List<int>> CurrentSeasonsByAthlete { get; } = new();

        public void Observe(int seasonNumber, int? champion)
        {
            foreach (int athleteId in CurrentByAthlete.Keys.ToList())
            {
                if (champion != athleteId)
                {
                    CurrentByAthlete[athleteId] = 0;
                    CurrentSeasonsByAthlete[athleteId] = [];
                }
            }

            if (champion is null)
            {
                return;
            }

            if (!CurrentByAthlete.ContainsKey(champion.Value))
            {
                CurrentByAthlete[champion.Value] = 0;
                CurrentSeasonsByAthlete[champion.Value] = [];
            }

            CurrentByAthlete[champion.Value] = checked(CurrentByAthlete[champion.Value] + 1);
            CurrentSeasonsByAthlete[champion.Value].Add(seasonNumber);
            if (!LongestByAthlete.TryGetValue(champion.Value, out int longest)
                || CurrentByAthlete[champion.Value] > longest)
            {
                LongestByAthlete[champion.Value] = CurrentByAthlete[champion.Value];
                LongestSeasonsByAthlete[champion.Value] = [.. CurrentSeasonsByAthlete[champion.Value]];
            }
        }
    }

    private static void AddStreakRecord(
        List<GetLeagueProfileResponse.LeagueRecordEntry> records,
        List<LeagueSeasonInput> completed,
        IReadOnlyDictionary<int, string> names)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(completed);
        ArgumentNullException.ThrowIfNull(names);
        StreakTracker tracker = new();
        foreach (LeagueSeasonInput season in completed)
        {
            tracker.Observe(season.SeasonNumber, ChampionOf(season));
        }

        if (tracker.LongestByAthlete.Count == 0)
        {
            return;
        }

        int max = tracker.LongestByAthlete.Values.Max();
        if (max <= 0)
        {
            return;
        }

        List<GetLeagueProfileResponse.LeagueRecordHolder> holders = tracker.LongestByAthlete
            .Where(kv => kv.Value == max)
            .Select(kv => MapStreakHolder(kv.Key, tracker.LongestSeasonsByAthlete[kv.Key], names))
            .OrderBy(h => h.AthleteName, StringComparer.Ordinal)
            .ThenBy(h => h.AthleteId)
            .ToList();
        string label = max == 1 ? "Longest championship streak" : "Longest consecutive championship streak";
        records.Add(new GetLeagueProfileResponse.LeagueRecordEntry(
            "league_longest_title_streak",
            label,
            max,
            max == 1 ? "1 title" : $"{max.ToString(CultureInfo.InvariantCulture)} in a row",
            holders,
            "Successive completed seasons of this league only."));
    }

    internal static int? ChampionOf(LeagueSeasonInput season)
    {
        ArgumentNullException.ThrowIfNull(season);
        return season.Standings
            .Where(r => r.SeasonRank == 1 || r.IsChampion)
            .OrderBy(r => r.SeasonRank)
            .Select(r => (int?)r.SaveAthleteId)
            .FirstOrDefault();
    }

    internal static GetLeagueProfileResponse.LeagueRecordHolder MapStreakHolder(
        int athleteId,
        List<int> streakSeasons,
        IReadOnlyDictionary<int, string> names)
    {
        ArgumentNullException.ThrowIfNull(streakSeasons);
        ArgumentNullException.ThrowIfNull(names);
        names.TryGetValue(athleteId, out string? name);
        List<int> ordered = streakSeasons.OrderBy(s => s).ToList();
        return new GetLeagueProfileResponse.LeagueRecordHolder(
            athleteId,
            name ?? $"Athlete {athleteId}",
            ordered,
            ordered.Count == 1 ? ordered[0] : null,
            null);
    }

    private static void AddMarginRecord(
        List<GetLeagueProfileResponse.LeagueRecordEntry> records,
        List<MarginSeason> margins,
        IReadOnlyDictionary<int, string> names,
        string? scopeNote)
    {
        if (margins.Count == 0)
        {
            return;
        }

        int max = margins.Max(m => m.MarginThousandths);
        List<GetLeagueProfileResponse.LeagueRecordHolder> holders = margins
            .Where(m => m.MarginThousandths == max)
            .Select(m =>
            {
                names.TryGetValue(m.ChampionAthleteId, out string? name);
                return new GetLeagueProfileResponse.LeagueRecordHolder(
                    m.ChampionAthleteId,
                    name ?? $"Athlete {m.ChampionAthleteId}",
                    [m.SeasonNumber],
                    m.SeasonNumber,
                    max);
            })
            .OrderBy(h => h.SeasonNumbers.First())
            .ThenBy(h => h.AthleteName, StringComparer.Ordinal)
            .ThenBy(h => h.AthleteId)
            .ToList();
        records.Add(new GetLeagueProfileResponse.LeagueRecordEntry(
            "league_largest_winning_margin",
            "Largest championship winning margin",
            max,
            FormatPoints(max),
            holders,
            scopeNote));
    }

    internal static string FormatPoints(long thousandths)
    {
        return (thousandths / 1000.0).ToString("F3", CultureInfo.InvariantCulture);
    }
}
