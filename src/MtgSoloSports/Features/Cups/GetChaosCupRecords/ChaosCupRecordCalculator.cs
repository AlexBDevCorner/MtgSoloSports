using MtgSoloSports.SimulationKernel.Cups;

namespace MtgSoloSports.Features.Cups.GetChaosCupRecords;

/// <summary>
/// Pure deterministic Chaos records and Tournament Stories (MSS-117).
/// All inputs come from completed-history projections only (editions, entrants,
/// group standings); round payloads, RNG, clock and database ordering are never
/// consulted and all arithmetic is integer-only. Stories are read-model cards:
/// they never mutate sporting state and carry no points, bonus or selection
/// effect. Absence of a story is allowed and reproducible.
/// </summary>
public static class ChaosCupRecordCalculator
{
    public const string StoryDefenseFailed = "defense_failed";

    public const string StoryRecordRun = "record_run";

    public const string StoryUnderdog = "underdog";

    public sealed record EditionInput(int SourceSeasonNumber, bool IsComplete);

    public sealed record EntrantInput(
        int SourceSeasonNumber,
        int SaveAthleteId,
        int SportingColor,
        string DivisionKey);

    public sealed record StandingInput(
        int SourceSeasonNumber,
        int StageIndex,
        int SaveAthleteId,
        int GroupRank,
        bool IsAdvanced);

    public sealed record AthleteChaosRecord(
        int SaveAthleteId,
        string AthleteName,
        int Titles,
        int Medals,
        int Finals,
        int? BestFinalRank,
        int DeepestStage,
        int LongestProgressStreak,
        int P16Survivals);

    public sealed record DivisionBest(
        string DivisionKey,
        string Label,
        int? BestFinalRank,
        IReadOnlyList<int> BestFinalHolderIds,
        int DeepestStage);

    public sealed record ColorBest(
        int SportingColor,
        string SportingColorName,
        int TitlesMax,
        IReadOnlyList<int> TitleHolderIds,
        int? BestFinalRank,
        IReadOnlyList<int> BestRankHolderIds);

    public sealed record TournamentStory(
        string Kind,
        int? SaveAthleteId,
        string AthleteName,
        int SourceSeasonNumber,
        int? RelatedSeasonNumber,
        string Detail,
        string Text);

    public sealed record RecordsResult(
        IReadOnlyList<int> CompletedSeasons,
        IReadOnlyList<AthleteChaosRecord> Athletes,
        IReadOnlyList<DivisionBest> Divisions,
        IReadOnlyList<ColorBest> Colors,
        IReadOnlyList<TournamentStory> Stories);

    internal static readonly IReadOnlyList<string> DivisionOrder =
        ["Pool", "Superleague", "Feeder1", "Feeder2", "Feeder3"];

    public static RecordsResult ComputeAll(
        IReadOnlyList<EditionInput> editions,
        IReadOnlyList<EntrantInput> entrants,
        IReadOnlyList<StandingInput> standings,
        IReadOnlyDictionary<int, string> athleteNames)
    {
        ArgumentNullException.ThrowIfNull(editions);
        ArgumentNullException.ThrowIfNull(entrants);
        ArgumentNullException.ThrowIfNull(standings);
        ArgumentNullException.ThrowIfNull(athleteNames);

        List<int> completed = editions
            .Where(e => e.IsComplete)
            .Select(e => e.SourceSeasonNumber)
            .Distinct()
            .OrderBy(s => s)
            .ToList();

        if (completed.Count == 0)
        {
            return new RecordsResult([], [], DefaultDivisions(), DefaultColors(), []);
        }

        HashSet<int> completedSet = completed.ToHashSet();
        ValidateInputs(entrants, standings, completedSet);

        List<StandingInput> kept = standings.Where(s => completedSet.Contains(s.SourceSeasonNumber)).ToList();
        List<EntrantInput> keptEntrants = entrants.Where(e => completedSet.Contains(e.SourceSeasonNumber)).ToList();

        List<AthleteChaosRecord> athletes = ComputeAthletes(completed, keptEntrants, kept, athleteNames);
        List<DivisionBest> divisions = ComputeDivisions(keptEntrants, kept);
        List<ColorBest> colors = ComputeColors(keptEntrants, kept);
        List<TournamentStory> stories = ComputeStories(completed, keptEntrants, kept, athletes, athleteNames);
        return new RecordsResult(completed, athletes, divisions, colors, stories);
    }

    internal static void ValidateInputs(
        IReadOnlyList<EntrantInput> entrants,
        IReadOnlyList<StandingInput> standings,
        HashSet<int> completedSet)
    {
        foreach (EntrantInput entrant in entrants)
        {
            if (!completedSet.Contains(entrant.SourceSeasonNumber))
            {
                continue;
            }

            if (entrant.SaveAthleteId <= 0)
            {
                throw new InvalidOperationException($"Chaos record entrant references corrupt athlete {entrant.SaveAthleteId}.");
            }

            if (!DivisionOrder.Contains(entrant.DivisionKey, StringComparer.Ordinal))
            {
                throw new InvalidOperationException($"Chaos record entrant {entrant.SaveAthleteId} has unknown division '{entrant.DivisionKey}'.");
            }
        }

        foreach (StandingInput standing in standings)
        {
            if (!completedSet.Contains(standing.SourceSeasonNumber))
            {
                continue;
            }

            if (standing.SaveAthleteId <= 0)
            {
                throw new InvalidOperationException($"Chaos record standing references corrupt athlete {standing.SaveAthleteId}.");
            }

            if (standing.StageIndex < 0 || standing.StageIndex >= ChaosCupFormat.StageCount)
            {
                throw new InvalidOperationException($"Chaos record standing for athlete {standing.SaveAthleteId} has corrupt stage {standing.StageIndex}.");
            }

            if (standing.GroupRank < 1 || standing.GroupRank > ChaosCupFormat.GroupSize)
            {
                throw new InvalidOperationException($"Chaos record standing for athlete {standing.SaveAthleteId} has corrupt rank {standing.GroupRank}.");
            }
        }
    }

    internal static List<AthleteChaosRecord> ComputeAthletes(
        List<int> completed,
        List<EntrantInput> entrants,
        List<StandingInput> standings,
        IReadOnlyDictionary<int, string> athleteNames)
    {
        Dictionary<(int Season, int Athlete), StandingInput> stageZero = standings
            .Where(s => s.StageIndex == 0)
            .ToDictionary(s => (s.SourceSeasonNumber, s.SaveAthleteId));
        Dictionary<int, Dictionary<int, EntrantInput>> entrantsBySeason = entrants
            .GroupBy(e => e.SourceSeasonNumber)
            .ToDictionary(g => g.Key, g => g.ToDictionary(e => e.SaveAthleteId));

        HashSet<int> athleteIds = CollectAthleteIds(entrants, standings);
        List<AthleteChaosRecord> records = new(athleteIds.Count);
        foreach (int athleteId in athleteIds)
        {
            records.Add(TallyAthlete(completed, standings, entrantsBySeason, stageZero, athleteNames, athleteId));
        }

        return OrderAthletes(records);
    }

    internal static HashSet<int> CollectAthleteIds(
        List<EntrantInput> entrants,
        List<StandingInput> standings)
    {
        HashSet<int> athleteIds = new(entrants.Select(e => e.SaveAthleteId));
        foreach (StandingInput standing in standings)
        {
            athleteIds.Add(standing.SaveAthleteId);
        }

        return athleteIds;
    }

    internal static AthleteChaosRecord TallyAthlete(
        List<int> completed,
        List<StandingInput> standings,
        Dictionary<int, Dictionary<int, EntrantInput>> entrantsBySeason,
        Dictionary<(int Season, int Athlete), StandingInput> stageZero,
        IReadOnlyDictionary<int, string> athleteNames,
        int athleteId)
    {
        int finalStage = ChaosCupFormat.StageCount - 1;
        int titles = 0;
        int medals = 0;
        int finalsCount = 0;
        int? bestRank = null;
        int deepest = -1;
        int p16 = 0;
        foreach (StandingInput standing in standings)
        {
            if (standing.SaveAthleteId != athleteId)
            {
                continue;
            }

            deepest = Math.Max(deepest, standing.StageIndex);
            if (standing.StageIndex == finalStage)
            {
                finalsCount++;
                bestRank = bestRank is null ? standing.GroupRank : Math.Min(bestRank.Value, standing.GroupRank);
                if (standing.GroupRank == 1)
                {
                    titles++;
                }

                if (standing.GroupRank is >= 1 and <= 3)
                {
                    medals++;
                }
            }
            else if (standing.GroupRank == ChaosCupFormat.AdvancingPerGroup && standing.IsAdvanced)
            {
                p16++;
            }
        }

        int streak = MeasureProgressStreak(completed, entrantsBySeason, stageZero, athleteId);
        athleteNames.TryGetValue(athleteId, out string? name);
        return new AthleteChaosRecord(
            athleteId,
            name ?? $"Athlete {athleteId}",
            titles,
            medals,
            finalsCount,
            bestRank,
            deepest < 0 ? 0 : deepest,
            streak,
            p16);
    }

    internal static List<AthleteChaosRecord> OrderAthletes(List<AthleteChaosRecord> records)
    {
        return records
            .OrderByDescending(r => r.Titles)
            .ThenByDescending(r => r.Medals)
            .ThenByDescending(r => r.Finals)
            .ThenBy(r => r.BestFinalRank ?? int.MaxValue)
            .ThenBy(r => r.AthleteName, StringComparer.Ordinal)
            .ThenBy(r => r.SaveAthleteId)
            .ToList();
    }

    internal static int MeasureProgressStreak(
        List<int> completed,
        Dictionary<int, Dictionary<int, EntrantInput>> entrantsBySeason,
        Dictionary<(int Season, int Athlete), StandingInput> stageZero,
        int athleteId)
    {
        int best = 0;
        int run = 0;
        int previous = int.MinValue;
        foreach (int season in completed)
        {
            bool entered = entrantsBySeason.TryGetValue(season, out Dictionary<int, EntrantInput>? roster)
                && roster.ContainsKey(athleteId);
            bool advanced = stageZero.TryGetValue((season, athleteId), out StandingInput? zero) && zero.IsAdvanced;
            if (entered && advanced)
            {
                run = previous != int.MinValue && season == previous + 1 ? run + 1 : 1;
            }
            else
            {
                run = 0;
            }

            best = Math.Max(best, run);
            previous = season;
        }

        return best;
    }

    internal static List<DivisionBest> ComputeDivisions(
        List<EntrantInput> entrants,
        List<StandingInput> standings)
    {
        Dictionary<(int Season, int Athlete), EntrantInput> entrantBySeasonAthlete = entrants
            .ToDictionary(e => (e.SourceSeasonNumber, e.SaveAthleteId));
        Dictionary<string, List<int>> finalRanks = CollectDivisionFinalRanks(standings, entrantBySeasonAthlete);
        Dictionary<string, int> deepest = CollectDivisionDeepest(standings, entrantBySeasonAthlete);
        Dictionary<string, List<int>> bestHolders = CollectDivisionBestHolders(standings, entrantBySeasonAthlete, finalRanks);
        return BuildDivisionBests(finalRanks, deepest, bestHolders);
    }

    internal static Dictionary<string, List<int>> CollectDivisionFinalRanks(
        List<StandingInput> standings,
        Dictionary<(int Season, int Athlete), EntrantInput> entrantBySeasonAthlete)
    {
        int finalStage = ChaosCupFormat.StageCount - 1;
        Dictionary<string, List<int>> finalRanks = new(StringComparer.Ordinal);
        foreach (StandingInput standing in standings.Where(s => s.StageIndex == finalStage))
        {
            if (!entrantBySeasonAthlete.TryGetValue((standing.SourceSeasonNumber, standing.SaveAthleteId), out EntrantInput? entrant))
            {
                continue;
            }

            if (!finalRanks.TryGetValue(entrant.DivisionKey, out List<int>? ranks))
            {
                ranks = [];
                finalRanks[entrant.DivisionKey] = ranks;
            }

            ranks.Add(standing.GroupRank);
        }

        return finalRanks;
    }

    internal static Dictionary<string, int> CollectDivisionDeepest(
        List<StandingInput> standings,
        Dictionary<(int Season, int Athlete), EntrantInput> entrantBySeasonAthlete)
    {
        Dictionary<string, int> deepest = new(StringComparer.Ordinal);
        foreach (string division in DivisionOrder)
        {
            deepest[division] = 0;
        }

        foreach (StandingInput standing in standings)
        {
            if (!entrantBySeasonAthlete.TryGetValue((standing.SourceSeasonNumber, standing.SaveAthleteId), out EntrantInput? entrant))
            {
                continue;
            }

            deepest[entrant.DivisionKey] = Math.Max(deepest[entrant.DivisionKey], standing.StageIndex);
        }

        return deepest;
    }

    internal static Dictionary<string, List<int>> CollectDivisionBestHolders(
        List<StandingInput> standings,
        Dictionary<(int Season, int Athlete), EntrantInput> entrantBySeasonAthlete,
        Dictionary<string, List<int>> finalRanks)
    {
        int finalStage = ChaosCupFormat.StageCount - 1;
        Dictionary<string, List<int>> bestHolders = new(StringComparer.Ordinal);
        foreach (StandingInput standing in standings.Where(s => s.StageIndex == finalStage))
        {
            if (!entrantBySeasonAthlete.TryGetValue((standing.SourceSeasonNumber, standing.SaveAthleteId), out EntrantInput? entrant))
            {
                continue;
            }

            if (!finalRanks.TryGetValue(entrant.DivisionKey, out List<int>? ranks) || ranks.Count == 0)
            {
                continue;
            }

            if (standing.GroupRank == ranks.Min())
            {
                if (!bestHolders.TryGetValue(entrant.DivisionKey, out List<int>? holders))
                {
                    holders = [];
                    bestHolders[entrant.DivisionKey] = holders;
                }

                holders.Add(standing.SaveAthleteId);
            }
        }

        return bestHolders;
    }

    internal static List<DivisionBest> BuildDivisionBests(
        Dictionary<string, List<int>> finalRanks,
        Dictionary<string, int> deepest,
        Dictionary<string, List<int>> bestHolders)
    {
        List<DivisionBest> results = new(DivisionOrder.Count);
        foreach (string division in DivisionOrder)
        {
            finalRanks.TryGetValue(division, out List<int>? ranks);
            int? best = ranks is null || ranks.Count == 0 ? null : ranks.Min();
            bestHolders.TryGetValue(division, out List<int>? holders);
            List<int> ordered = (holders ?? []).Distinct().OrderBy(id => id).ToList();
            results.Add(new DivisionBest(division, DivisionLabel(division), best, ordered, deepest[division]));
        }

        return results;
    }

    internal static List<ColorBest> ComputeColors(
        List<EntrantInput> entrants,
        List<StandingInput> standings)
    {
        Dictionary<(int Season, int Athlete), EntrantInput> entrantBySeasonAthlete = entrants
            .ToDictionary(e => (e.SourceSeasonNumber, e.SaveAthleteId));
        var colors = Enum.GetValues<SimulationKernel.Catalog.SportingColor>();
        List<ColorBest> results = new(colors.Length);
        foreach (SimulationKernel.Catalog.SportingColor color in colors)
        {
            results.Add(BuildColorBest(color, standings, entrantBySeasonAthlete));
        }

        return results;
    }

    internal static ColorBest BuildColorBest(
        SimulationKernel.Catalog.SportingColor color,
        List<StandingInput> standings,
        Dictionary<(int Season, int Athlete), EntrantInput> entrantBySeasonAthlete)
    {
        int finalStage = ChaosCupFormat.StageCount - 1;
        List<StandingInput> colorFinals = standings
            .Where(s => s.StageIndex == finalStage)
            .Where(s =>
                entrantBySeasonAthlete.TryGetValue((s.SourceSeasonNumber, s.SaveAthleteId), out EntrantInput? entrant)
                    && entrant.SportingColor == (int)color)
            .ToList();
        Dictionary<int, int> titlesByAthlete = colorFinals
            .Where(s => s.GroupRank == 1)
            .GroupBy(s => s.SaveAthleteId)
            .ToDictionary(g => g.Key, g => g.Count());
        int maxTitles = titlesByAthlete.Count == 0 ? 0 : titlesByAthlete.Values.Max();
        List<int> titleHolders = titlesByAthlete
            .Where(kv => kv.Value == maxTitles && maxTitles > 0)
            .Select(kv => kv.Key)
            .OrderBy(id => id)
            .ToList();
        int? bestRank = colorFinals.Count == 0 ? null : colorFinals.Min(s => s.GroupRank);
        List<int> bestHolders = bestRank is null
            ? []
            : colorFinals.Where(s => s.GroupRank == bestRank.Value).Select(s => s.SaveAthleteId).Distinct().OrderBy(id => id).ToList();
        return new ColorBest((int)color, color.ToString(), maxTitles, titleHolders, bestRank, bestHolders);
    }

    internal static List<TournamentStory> ComputeStories(
        List<int> completed,
        List<EntrantInput> entrants,
        List<StandingInput> standings,
        List<AthleteChaosRecord> athletes,
        IReadOnlyDictionary<int, string> athleteNames)
    {
        List<TournamentStory> stories = [];
        stories.AddRange(ComputeDefenseStories(completed, entrants, standings, athleteNames));
        stories.AddRange(ComputeRecordStories(completed, athletes));
        stories.AddRange(ComputeUnderdogStories(entrants, standings, athleteNames));
        return stories
            .OrderBy(s => s.SourceSeasonNumber)
            .ThenBy(s => s.Kind, StringComparer.Ordinal)
            .ThenBy(s => s.SaveAthleteId ?? int.MaxValue)
            .ToList();
    }

    internal static List<TournamentStory> ComputeDefenseStories(
        List<int> completed,
        List<EntrantInput> entrants,
        List<StandingInput> standings,
        IReadOnlyDictionary<int, string> athleteNames)
    {
        int finalStage = ChaosCupFormat.StageCount - 1;
        Dictionary<int, int> championBySeason = standings
            .Where(s => s.StageIndex == finalStage && s.GroupRank == 1)
            .GroupBy(s => s.SourceSeasonNumber)
            .ToDictionary(g => g.Key, g => g.Single().SaveAthleteId);
        HashSet<(int Season, int Athlete)> entered = entrants
            .Select(e => (e.SourceSeasonNumber, e.SaveAthleteId))
            .ToHashSet();
        List<TournamentStory> stories = [];
        for (int i = 1; i < completed.Count; i++)
        {
            int previous = completed[i - 1];
            int next = completed[i];
            if (next != previous + 1)
            {
                continue;
            }

            if (!championBySeason.TryGetValue(previous, out int champion)
                || !championBySeason.TryGetValue(next, out int nextChampion))
            {
                continue;
            }

            if (nextChampion == champion || !entered.Contains((next, champion)))
            {
                continue;
            }

            athleteNames.TryGetValue(champion, out string? name);
            string athleteName = name ?? $"Athlete {champion}";
            stories.Add(new TournamentStory(
                StoryDefenseFailed,
                champion,
                athleteName,
                next,
                previous,
                $"Defending champion from Season {previous} did not repeat in Season {next}.",
                $"{athleteName} failed to defend the Chaos Cup title in Season {next} after winning Season {previous}."));
        }

        return stories;
    }

    internal static List<TournamentStory> ComputeRecordStories(
        List<int> completed,
        List<AthleteChaosRecord> athletes)
    {
        List<TournamentStory> stories = [];
        if (completed.Count == 0 || athletes.Count == 0)
        {
            return stories;
        }

        int latest = completed.Max();
        int titlesMax = athletes.Max(a => a.Titles);
        if (titlesMax >= ChaosCupRecordThresholds.RecordTitlesMinimum)
        {
            foreach (AthleteChaosRecord holder in athletes.Where(a => a.Titles == titlesMax).OrderBy(a => a.SaveAthleteId))
            {
                stories.Add(new TournamentStory(
                    StoryRecordRun,
                    holder.SaveAthleteId,
                    holder.AthleteName,
                    latest,
                    null,
                    $"Chaos titles record {titlesMax}.",
                    $"{holder.AthleteName} holds the Chaos Cup titles record at {titlesMax} (Season {latest})."));
            }
        }

        int finalsMax = athletes.Max(a => a.Finals);
        if (finalsMax >= ChaosCupRecordThresholds.RecordFinalsMinimum)
        {
            foreach (AthleteChaosRecord holder in athletes.Where(a => a.Finals == finalsMax).OrderBy(a => a.SaveAthleteId))
            {
                stories.Add(new TournamentStory(
                    StoryRecordRun,
                    holder.SaveAthleteId,
                    holder.AthleteName,
                    latest,
                    null,
                    $"Chaos finals record {finalsMax}.",
                    $"{holder.AthleteName} holds the Chaos Cup finals record at {finalsMax} (Season {latest})."));
            }
        }

        int p16Max = athletes.Max(a => a.P16Survivals);
        if (p16Max >= ChaosCupRecordThresholds.RepeatedP16Minimum)
        {
            foreach (AthleteChaosRecord holder in athletes.Where(a => a.P16Survivals == p16Max).OrderBy(a => a.SaveAthleteId))
            {
                stories.Add(new TournamentStory(
                    StoryRecordRun,
                    holder.SaveAthleteId,
                    holder.AthleteName,
                    latest,
                    null,
                    $"Repeated P16 survival record {p16Max}.",
                    $"{holder.AthleteName} holds the repeated P16 survival record at {p16Max} (Season {latest})."));
            }
        }

        return stories;
    }

    internal static List<TournamentStory> ComputeUnderdogStories(
        List<EntrantInput> entrants,
        List<StandingInput> standings,
        IReadOnlyDictionary<int, string> athleteNames)
    {
        int finalStage = ChaosCupFormat.StageCount - 1;
        Dictionary<(int Season, int Athlete), EntrantInput> entrantBySeasonAthlete = entrants
            .ToDictionary(e => (e.SourceSeasonNumber, e.SaveAthleteId));
        List<TournamentStory> stories = [];
        foreach (StandingInput standing in standings.Where(s => s.StageIndex == finalStage).OrderBy(s => s.SourceSeasonNumber).ThenBy(s => s.GroupRank))
        {
            if (!entrantBySeasonAthlete.TryGetValue((standing.SourceSeasonNumber, standing.SaveAthleteId), out EntrantInput? entrant))
            {
                continue;
            }

            bool qualifies = entrant.DivisionKey switch
            {
                "Pool" => standing.GroupRank <= ChaosCupRecordThresholds.PoolUnderdogMaxFinalRank,
                "Feeder3" => standing.GroupRank <= ChaosCupRecordThresholds.Feeder3UnderdogMaxFinalRank,
                _ => false,
            };
            if (!qualifies)
            {
                continue;
            }

            athleteNames.TryGetValue(standing.SaveAthleteId, out string? name);
            string athleteName = name ?? $"Athlete {standing.SaveAthleteId}";
            string divisionLabel = DivisionLabel(entrant.DivisionKey);
            stories.Add(new TournamentStory(
                StoryUnderdog,
                standing.SaveAthleteId,
                athleteName,
                standing.SourceSeasonNumber,
                null,
                $"{divisionLabel} entrant Final P{standing.GroupRank}.",
                $"{athleteName} reached Final P{standing.GroupRank} as a {divisionLabel} entrant in Season {standing.SourceSeasonNumber}."));
        }

        return stories;
    }

    internal static string DivisionLabel(string divisionKey) => divisionKey switch
    {
        "Pool" => "Pool",
        "Superleague" => "Superleague",
        "Feeder1" => "Feeder 1",
        "Feeder2" => "Feeder 2",
        "Feeder3" => "Feeder 3",
        _ => divisionKey,
    };

    internal static List<DivisionBest> DefaultDivisions() => DivisionOrder
        .Select(d => new DivisionBest(d, DivisionLabel(d), null, [], 0))
        .ToList();

    internal static List<ColorBest> DefaultColors() =>
        Enum.GetValues<SimulationKernel.Catalog.SportingColor>()
            .Select(c => new ColorBest((int)c, c.ToString(), 0, [], null, []))
            .ToList();
}
