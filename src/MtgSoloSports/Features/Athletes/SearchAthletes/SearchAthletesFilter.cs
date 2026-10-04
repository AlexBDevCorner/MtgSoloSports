using System.Text.Json;

namespace MtgSoloSports.Features.Athletes.SearchAthletes;

/// <summary>
/// Pure in-memory filter/sort/aggregation for athlete search. All methods are
/// deterministic and integer-only; text matching is ordinal case-insensitive
/// partial match. Database queries bulk-load minimal projections (no N+1, no
/// round payloads); this class applies AND-across-categories / OR-within-category
/// semantics and sorting before pagination so behavior is explicit and testable.
/// </summary>
public static class SearchAthletesFilter
{
    public const string PoolLeagueName = "Common pool";

    public sealed record Candidate(
        int AthleteId,
        string Name,
        string? ImageUrl,
        string TypeLine,
        int SportingColor,
        string SportingColorName,
        IReadOnlyList<string> CreatureTypes,
        bool IsActive,
        string? CurrentLeagueName,
        int? CurrentLeagueKind,
        int NonPoolSeasons,
        int HonoursCount,
        int TitlesCount,
        int? BestSeasonFinish,
        int? BestSeasonNumber,
        bool EverSuperleague,
        int SuperSeasons,
        int CupAppearances,
        int CupPodiums,
        int CupTitles);

    public sealed record LeaguePodiumInput(int SaveAthleteId, int SeasonRank);

    public sealed record CupIndividualInput(int SaveAthleteId, int CupRank);

    public sealed record CupColorLegInput(int SaveAthleteId, int SourceSeasonId, int SportingColor);

    public sealed record CupColorTeamInput(int SourceSeasonId, int SportingColor, int TeamRank);

    public sealed record CupTypeLegInput(int SaveAthleteId, int SourceSeasonId, string CreatureType);

    public sealed record CupTypeTeamInput(int SourceSeasonId, string CreatureType, int TeamRank);

    public sealed record HonourTotals(int Honours, int Titles, int CupAppearances, int CupPodiums, int CupTitles);

    /// <summary>
    /// Aggregates honours/titles/Cup presence from authoritative standings rows.
    /// League podiums (rank 1..3) each yield one honour; rank 1 yields one title.
    /// Cup individual ranks 1..3 yield one honour each; team legs yield one honour
    /// when their team (same season + color/type) finished 1..3. Rank 4+ yields none.
    /// Every individual row and every team leg counts as one Cup appearance.
    /// </summary>
    public static IReadOnlyDictionary<int, HonourTotals> AggregateHonours(
        IReadOnlyList<LeaguePodiumInput> leaguePodiums,
        IReadOnlyList<CupIndividualInput> individuals,
        IReadOnlyList<CupColorLegInput> colorLegs,
        IReadOnlyList<CupColorTeamInput> colorTeams,
        IReadOnlyList<CupTypeLegInput> typeLegs,
        IReadOnlyList<CupTypeTeamInput> typeTeams)
    {
        ArgumentNullException.ThrowIfNull(leaguePodiums);
        ArgumentNullException.ThrowIfNull(individuals);
        ArgumentNullException.ThrowIfNull(colorLegs);
        ArgumentNullException.ThrowIfNull(colorTeams);
        ArgumentNullException.ThrowIfNull(typeLegs);
        ArgumentNullException.ThrowIfNull(typeTeams);

        Dictionary<int, HonourTotals> totals = [];
        AddLeaguePodiums(totals, leaguePodiums);
        AddIndividuals(totals, individuals);
        AddColorLegs(totals, colorLegs, colorTeams);
        AddTypeLegs(totals, typeLegs, typeTeams);
        return totals;
    }

    private static void AddLeaguePodiums(
        Dictionary<int, HonourTotals> totals,
        IReadOnlyList<LeaguePodiumInput> leaguePodiums)
    {
        foreach (LeaguePodiumInput podium in leaguePodiums)
        {
            if (podium.SeasonRank is < 1 or > 3)
            {
                continue;
            }

            Add(totals, podium.SaveAthleteId, 1, podium.SeasonRank == 1 ? 1 : 0, 0, 0, 0);
        }
    }

    private static void AddIndividuals(
        Dictionary<int, HonourTotals> totals,
        IReadOnlyList<CupIndividualInput> individuals)
    {
        foreach (CupIndividualInput row in individuals)
        {
            bool podium = row.CupRank is >= 1 and <= 3;
            bool title = row.CupRank == 1;
            Add(totals, row.SaveAthleteId, podium ? 1 : 0, title ? 1 : 0, 1, podium ? 1 : 0, title ? 1 : 0);
        }
    }

    private static void AddColorLegs(
        Dictionary<int, HonourTotals> totals,
        IReadOnlyList<CupColorLegInput> colorLegs,
        IReadOnlyList<CupColorTeamInput> colorTeams)
    {
        Dictionary<(int SeasonId, int Color), int> rank = colorTeams
            .ToDictionary(t => (t.SourceSeasonId, t.SportingColor), t => t.TeamRank);
        foreach (CupColorLegInput leg in colorLegs)
        {
            bool hasTeam = rank.TryGetValue((leg.SourceSeasonId, leg.SportingColor), out int teamRank);
            bool podium = hasTeam && teamRank is >= 1 and <= 3;
            bool title = hasTeam && teamRank == 1;
            Add(totals, leg.SaveAthleteId, podium ? 1 : 0, title ? 1 : 0, 1, podium ? 1 : 0, title ? 1 : 0);
        }
    }

    private static void AddTypeLegs(
        Dictionary<int, HonourTotals> totals,
        IReadOnlyList<CupTypeLegInput> typeLegs,
        IReadOnlyList<CupTypeTeamInput> typeTeams)
    {
        Dictionary<(int SeasonId, string Type), int> rank = [];
        foreach (CupTypeTeamInput team in typeTeams)
        {
            rank[(team.SourceSeasonId, team.CreatureType)] = team.TeamRank;
        }

        foreach (CupTypeLegInput leg in typeLegs)
        {
            bool hasTeam = rank.TryGetValue((leg.SourceSeasonId, leg.CreatureType), out int teamRank);
            bool podium = hasTeam && teamRank is >= 1 and <= 3;
            bool title = hasTeam && teamRank == 1;
            Add(totals, leg.SaveAthleteId, podium ? 1 : 0, title ? 1 : 0, 1, podium ? 1 : 0, title ? 1 : 0);
        }
    }

    private static void Add(
        Dictionary<int, HonourTotals> totals,
        int athleteId,
        int honours,
        int titles,
        int appearances,
        int podiums,
        int cupTitles)
    {
        totals.TryGetValue(athleteId, out HonourTotals? existing);
        existing ??= new HonourTotals(0, 0, 0, 0, 0);
        totals[athleteId] = new HonourTotals(
            checked(existing.Honours + honours),
            checked(existing.Titles + titles),
            checked(existing.CupAppearances + appearances),
            checked(existing.CupPodiums + podiums),
            checked(existing.CupTitles + cupTitles));
    }

    /// <summary>
    /// Parses the save-owned creature-type JSON array. Corrupt JSON aborts;
    /// never silently treated as empty.
    /// </summary>
    public static IReadOnlyList<string> ParseCreatureTypes(string creatureTypesJson)
    {
        ArgumentNullException.ThrowIfNull(creatureTypesJson);
        List<string>? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<List<string>>(creatureTypesJson);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Athlete creature types are corrupt: {ex.Message}", ex);
        }

        if (parsed is null)
        {
            throw new InvalidOperationException("Athlete creature types are corrupt.");
        }

        return parsed;
    }

    public static bool MatchesSearch(string name, string? search)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (string.IsNullOrWhiteSpace(search))
        {
            return true;
        }

        return name.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public static bool MatchesCreatureTypes(IReadOnlyList<string> athleteTypes, IReadOnlyList<string>? selected)
    {
        ArgumentNullException.ThrowIfNull(athleteTypes);
        if (selected is null || selected.Count == 0)
        {
            return true;
        }

        foreach (string wanted in selected)
        {
            foreach (string owned in athleteTypes)
            {
                if (string.Equals(owned, wanted.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public static bool MatchesCurrentLeague(bool isActive, string? currentLeagueName, IReadOnlyList<string>? selected)
    {
        if (selected is null || selected.Count == 0)
        {
            return true;
        }

        string actual = isActive ? (currentLeagueName ?? string.Empty) : PoolLeagueName;
        foreach (string wanted in selected)
        {
            if (string.Equals(actual, wanted.Trim(), StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    public static bool MatchesHighestLeague(bool everActive, bool everSuperleague, string? highest)
    {
        return highest switch
        {
            null => true,
            "superleague" => everSuperleague,
            "feeder" => everActive && !everSuperleague,
            "none" => !everActive,
            _ => throw new ArgumentException($"Unknown highest league '{highest}'.", nameof(highest)),
        };
    }

    public static bool MatchesCupStatus(int appearances, int podiums, int titles, string? cupStatus)
    {
        return cupStatus switch
        {
            null => true,
            "participant" => appearances >= 1,
            "podium" => podiums >= 1,
            "title" => titles >= 1,
            "none" => appearances == 0,
            _ => throw new ArgumentException($"Unknown cup status '{cupStatus}'.", nameof(cupStatus)),
        };
    }

    public static bool MatchesHasTitle(int titlesCount, string? hasTitle)
    {
        return hasTitle switch
        {
            null => true,
            "only" => titlesCount >= 1,
            "none" => titlesCount == 0,
            _ => throw new ArgumentException($"Unknown hasTitle '{hasTitle}'.", nameof(hasTitle)),
        };
    }

    /// <summary>
    /// Applies all filters with AND semantics across categories.
    /// </summary>
    public static IReadOnlyList<Candidate> ApplyFilters(IReadOnlyList<Candidate> candidates, SearchAthletesQuery query)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(query);
        HashSet<int>? colors = query.SportingColors is not null ? query.SportingColors.ToHashSet() : null;
        List<Candidate> filtered = [];
        foreach (Candidate candidate in candidates)
        {
            if (MatchesCandidate(candidate, query, colors))
            {
                filtered.Add(candidate);
            }
        }

        return filtered;
    }

    private static bool MatchesCandidate(Candidate candidate, SearchAthletesQuery query, HashSet<int>? colors)
    {
        if (!MatchesSearch(candidate.Name, query.Search))
        {
            return false;
        }

        if (colors is not null && !colors.Contains(candidate.SportingColor))
        {
            return false;
        }

        if (!MatchesCreatureTypes(candidate.CreatureTypes, query.CreatureTypes))
        {
            return false;
        }

        if (!MatchesCurrentLeague(candidate.IsActive, candidate.CurrentLeagueName, query.CurrentLeagues))
        {
            return false;
        }

        if (!MatchesCounts(candidate, query))
        {
            return false;
        }

        if (!MatchesHistory(candidate, query))
        {
            return false;
        }

        return MatchesCupStatus(candidate.CupAppearances, candidate.CupPodiums, candidate.CupTitles, query.CupStatus);
    }

    private static bool MatchesCounts(Candidate candidate, SearchAthletesQuery query)
    {
        if (query.MinNonPoolSeasons is not null && candidate.NonPoolSeasons < query.MinNonPoolSeasons)
        {
            return false;
        }

        if (query.MaxNonPoolSeasons is not null && candidate.NonPoolSeasons > query.MaxNonPoolSeasons)
        {
            return false;
        }

        if (query.MinHonours is not null && candidate.HonoursCount < query.MinHonours)
        {
            return false;
        }

        if (query.MaxHonours is not null && candidate.HonoursCount > query.MaxHonours)
        {
            return false;
        }

        if (query.MinTitles is not null && candidate.TitlesCount < query.MinTitles)
        {
            return false;
        }

        return MatchesHasTitle(candidate.TitlesCount, query.HasTitle);
    }

    private static bool MatchesHistory(Candidate candidate, SearchAthletesQuery query)
    {
        bool everActive = candidate.NonPoolSeasons > 0;
        if (!MatchesHighestLeague(everActive, candidate.EverSuperleague, query.HighestLeague))
        {
            return false;
        }

        if (query.BestFinishMax is not null)
        {
            if (candidate.BestSeasonFinish is null || candidate.BestSeasonFinish > query.BestFinishMax)
            {
                return false;
            }
        }

        if (query.MinSuperSeasons is not null && candidate.SuperSeasons < query.MinSuperSeasons)
        {
            return false;
        }

        if (query.MaxSuperSeasons is not null && candidate.SuperSeasons > query.MaxSuperSeasons)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Deterministic sort with name/id tie-breaks so pages are stable.
    /// </summary>
    public static IReadOnlyList<Candidate> ApplySort(IReadOnlyList<Candidate> candidates, string sort, string dir)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(sort);
        ArgumentNullException.ThrowIfNull(dir);
        bool asc = string.Equals(dir, "asc", StringComparison.Ordinal);
        IEnumerable<Candidate> ordered = sort switch
        {
            "name" => SortByName(candidates, asc),
            "nonPool" => SortByNonPool(candidates, asc),
            "honours" => SortByHonours(candidates, asc),
            "titles" => SortByTitles(candidates, asc),
            "bestFinish" => SortByBestFinish(candidates, asc),
            "currentLeague" => SortByCurrentLeague(candidates, asc),
            "superSeasons" => SortBySuperSeasons(candidates, asc),
            _ => throw new ArgumentException($"Unknown sort '{sort}'.", nameof(sort)),
        };
        return ordered.ToList();
    }

    private static IEnumerable<Candidate> SortByName(IReadOnlyList<Candidate> candidates, bool asc)
    {
        return asc
            ? candidates.OrderBy(e => e.Name, StringComparer.Ordinal).ThenBy(e => e.AthleteId)
            : candidates.OrderByDescending(e => e.Name, StringComparer.Ordinal).ThenBy(e => e.AthleteId);
    }

    private static IEnumerable<Candidate> SortByNonPool(IReadOnlyList<Candidate> candidates, bool asc)
    {
        return asc
            ? candidates.OrderBy(e => e.NonPoolSeasons).ThenBy(e => e.Name, StringComparer.Ordinal).ThenBy(e => e.AthleteId)
            : candidates.OrderByDescending(e => e.NonPoolSeasons).ThenBy(e => e.Name, StringComparer.Ordinal).ThenBy(e => e.AthleteId);
    }

    private static IEnumerable<Candidate> SortByHonours(IReadOnlyList<Candidate> candidates, bool asc)
    {
        return asc
            ? candidates.OrderBy(e => e.HonoursCount).ThenBy(e => e.TitlesCount).ThenBy(e => e.Name, StringComparer.Ordinal).ThenBy(e => e.AthleteId)
            : candidates.OrderByDescending(e => e.HonoursCount).ThenByDescending(e => e.TitlesCount).ThenBy(e => e.Name, StringComparer.Ordinal).ThenBy(e => e.AthleteId);
    }

    private static IEnumerable<Candidate> SortByTitles(IReadOnlyList<Candidate> candidates, bool asc)
    {
        return asc
            ? candidates.OrderBy(e => e.TitlesCount).ThenBy(e => e.HonoursCount).ThenBy(e => e.Name, StringComparer.Ordinal).ThenBy(e => e.AthleteId)
            : candidates.OrderByDescending(e => e.TitlesCount).ThenByDescending(e => e.HonoursCount).ThenBy(e => e.Name, StringComparer.Ordinal).ThenBy(e => e.AthleteId);
    }

    private static IEnumerable<Candidate> SortByBestFinish(IReadOnlyList<Candidate> candidates, bool asc)
    {
        return asc
            ? candidates.OrderBy(e => e.BestSeasonFinish ?? int.MaxValue).ThenBy(e => e.Name, StringComparer.Ordinal).ThenBy(e => e.AthleteId)
            : candidates.OrderByDescending(e => e.BestSeasonFinish ?? int.MinValue).ThenBy(e => e.Name, StringComparer.Ordinal).ThenBy(e => e.AthleteId);
    }

    private static IEnumerable<Candidate> SortByCurrentLeague(IReadOnlyList<Candidate> candidates, bool asc)
    {
        static string Key(Candidate e) => e.IsActive ? e.CurrentLeagueName ?? string.Empty : PoolLeagueName;
        return asc
            ? candidates.OrderBy(Key, StringComparer.Ordinal).ThenBy(e => e.Name, StringComparer.Ordinal).ThenBy(e => e.AthleteId)
            : candidates.OrderByDescending(Key, StringComparer.Ordinal).ThenBy(e => e.Name, StringComparer.Ordinal).ThenBy(e => e.AthleteId);
    }

    private static IEnumerable<Candidate> SortBySuperSeasons(IReadOnlyList<Candidate> candidates, bool asc)
    {
        return asc
            ? candidates.OrderBy(e => e.SuperSeasons).ThenBy(e => e.Name, StringComparer.Ordinal).ThenBy(e => e.AthleteId)
            : candidates.OrderByDescending(e => e.SuperSeasons).ThenBy(e => e.Name, StringComparer.Ordinal).ThenBy(e => e.AthleteId);
    }
}
