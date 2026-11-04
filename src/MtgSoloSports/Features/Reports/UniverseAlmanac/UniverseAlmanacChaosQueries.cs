using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Cups.ListCupEditions;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;
using MtgSoloSports.SimulationKernel.Cups;

namespace MtgSoloSports.Features.Reports.UniverseAlmanac;

internal static class UniverseAlmanacChaosQueries
{
    internal sealed record ChaosTables(
        IReadOnlyList<AlmanacChaosEditionEntry> Editions,
        IReadOnlyList<AlmanacChaosMedalEntry> Medals,
        IReadOnlyList<AlmanacChaosColorEntry> ColorMedals);

    internal static async Task<ChaosTables> LoadChaosTablesAsync(
        SaveDbContext context,
        Dictionary<int, string> names,
        CancellationToken cancellationToken)
    {
        List<ChaosCupEditionEntity> editions = await context.ChaosCupEditions
            .AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        if (editions.Count == 0)
        {
            return new ChaosTables([], [], []);
        }

        Dictionary<(int SeasonId, int Stage), int> draws = await LoadDrawCountsAsync(context, cancellationToken).ConfigureAwait(false);
        Dictionary<(int SeasonId, int Stage), int> rounds = await LoadRoundCountsAsync(context, cancellationToken).ConfigureAwait(false);
        Dictionary<(int SeasonId, int Stage), int> standings = await LoadStandingCountsAsync(context, cancellationToken).ConfigureAwait(false);
        HashSet<int> completeSeasonIds = CompleteSeasonIds(editions, draws, rounds, standings);
        Dictionary<int, List<ChaosCupGroupStandingEntity>> finalsBySeason = await LoadFinalsAsync(context, completeSeasonIds, cancellationToken).ConfigureAwait(false);
        Dictionary<int, ChaosCupEntrantEntity> entrantsByAthlete = await LoadEntrantsAsync(context, editions, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<AlmanacChaosEditionEntry> editionEntries = BuildEditions(editions, completeSeasonIds, finalsBySeason, names);
        IReadOnlyList<AlmanacChaosMedalEntry> medals = BuildMedals(editionEntries, names);
        IReadOnlyList<AlmanacChaosColorEntry> colorMedals = BuildColorMedals(editionEntries, entrantsByAthlete);
        return new ChaosTables(editionEntries, medals, colorMedals);
    }

    internal static HashSet<int> CompleteSeasonIds(
        List<ChaosCupEditionEntity> editions,
        Dictionary<(int SeasonId, int Stage), int> draws,
        Dictionary<(int SeasonId, int Stage), int> rounds,
        Dictionary<(int SeasonId, int Stage), int> standings)
    {
        ArgumentNullException.ThrowIfNull(editions);
        return editions
            .Where(e => ListCupEditionsHandler.IsChaosComplete(e.SourceSeasonId, draws, rounds, standings))
            .Select(e => e.SourceSeasonId)
            .ToHashSet();
    }

    internal static async Task<Dictionary<int, List<ChaosCupGroupStandingEntity>>> LoadFinalsAsync(
        SaveDbContext context,
        HashSet<int> completeSeasonIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        List<ChaosCupGroupStandingEntity> finals = await context.ChaosCupGroupStandings
            .AsNoTracking()
            .Where(e => e.StageIndex == ChaosCupFormat.StageCount - 1 && completeSeasonIds.Contains(e.SourceSeasonId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return finals
            .GroupBy(e => e.SourceSeasonId)
            .ToDictionary(g => g.Key, g => g.OrderBy(e => e.GroupRank).ToList());
    }

    internal static async Task<Dictionary<int, ChaosCupEntrantEntity>> LoadEntrantsAsync(
        SaveDbContext context,
        List<ChaosCupEditionEntity> editions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        HashSet<int> seasonIds = editions.Select(e => e.SourceSeasonId).ToHashSet();
        List<ChaosCupEntrantEntity> entrants = await context.ChaosCupEntrants
            .AsNoTracking()
            .Where(e => seasonIds.Contains(e.SourceSeasonId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return entrants
            .GroupBy(e => e.SaveAthleteId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(e => e.SourceSeasonNumber).First());
    }

    internal static async Task<Dictionary<int, string>> LoadChaosStatesAsync(
        SaveDbContext context,
        List<SeasonEntity> seasons,
        CancellationToken cancellationToken)
    {
        List<ChaosCupEditionEntity> editions = await context.ChaosCupEditions
            .AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        if (editions.Count == 0)
        {
            return new Dictionary<int, string>();
        }

        Dictionary<(int SeasonId, int Stage), int> draws = await LoadDrawCountsAsync(context, cancellationToken).ConfigureAwait(false);
        Dictionary<(int SeasonId, int Stage), int> rounds = await LoadRoundCountsAsync(context, cancellationToken).ConfigureAwait(false);
        Dictionary<(int SeasonId, int Stage), int> standings = await LoadStandingCountsAsync(context, cancellationToken).ConfigureAwait(false);
        return MapStates(seasons, editions, draws, rounds, standings);
    }

    internal static Dictionary<int, string> MapStates(
        List<SeasonEntity> seasons,
        List<ChaosCupEditionEntity> editions,
        Dictionary<(int SeasonId, int Stage), int> draws,
        Dictionary<(int SeasonId, int Stage), int> rounds,
        Dictionary<(int SeasonId, int Stage), int> standings)
    {
        ArgumentNullException.ThrowIfNull(seasons);
        Dictionary<int, int> seasonIdByNumber = seasons.ToDictionary(s => s.SeasonNumber, s => s.Id);
        Dictionary<int, ChaosCupEditionEntity> editionBySeasonId = editions.ToDictionary(e => e.SourceSeasonId);
        Dictionary<int, string> states = new();
        foreach ((int seasonNumber, int seasonId) in seasonIdByNumber)
        {
            if (!editionBySeasonId.TryGetValue(seasonId, out ChaosCupEditionEntity? edition))
            {
                continue;
            }

            bool complete = ListCupEditionsHandler.IsChaosComplete(edition.SourceSeasonId, draws, rounds, standings);
            states[seasonNumber] = complete ? "Completed" : "InProgress";
        }

        return states;
    }

    internal static async Task<Dictionary<(int SeasonId, int Stage), int>> LoadDrawCountsAsync(
        SaveDbContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return (await context.ChaosCupDraws
                .AsNoTracking()
                .GroupBy(e => new { e.SourceSeasonId, e.StageIndex })
                .Select(g => new { g.Key.SourceSeasonId, g.Key.StageIndex, Count = g.Count() })
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(g => (g.SourceSeasonId, g.StageIndex), g => g.Count);
    }

    internal static async Task<Dictionary<(int SeasonId, int Stage), int>> LoadRoundCountsAsync(
        SaveDbContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return (await context.ChaosCupRounds
                .AsNoTracking()
                .GroupBy(e => new { e.SourceSeasonId, e.StageIndex })
                .Select(g => new { g.Key.SourceSeasonId, g.Key.StageIndex, Count = g.Count() })
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(g => (g.SourceSeasonId, g.StageIndex), g => g.Count);
    }

    internal static async Task<Dictionary<(int SeasonId, int Stage), int>> LoadStandingCountsAsync(
        SaveDbContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return (await context.ChaosCupGroupStandings
                .AsNoTracking()
                .GroupBy(e => new { e.SourceSeasonId, e.StageIndex })
                .Select(g => new { g.Key.SourceSeasonId, g.Key.StageIndex, Count = g.Count() })
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(g => (g.SourceSeasonId, g.StageIndex), g => g.Count);
    }

    internal static IReadOnlyList<AlmanacChaosEditionEntry> BuildEditions(
        List<ChaosCupEditionEntity> editions,
        HashSet<int> completeSeasonIds,
        Dictionary<int, List<ChaosCupGroupStandingEntity>> finalsBySeason,
        Dictionary<int, string> names)
    {
        ArgumentNullException.ThrowIfNull(editions);
        List<AlmanacChaosEditionEntry> entries = new(editions.Count);
        foreach (ChaosCupEditionEntity edition in editions.OrderByDescending(e => e.SourceSeasonNumber))
        {
            entries.Add(MapEdition(edition, completeSeasonIds, finalsBySeason, names));
        }

        return entries;
    }

    internal static AlmanacChaosEditionEntry MapEdition(
        ChaosCupEditionEntity edition,
        HashSet<int> completeSeasonIds,
        Dictionary<int, List<ChaosCupGroupStandingEntity>> finalsBySeason,
        Dictionary<int, string> names)
    {
        ArgumentNullException.ThrowIfNull(edition);
        if (!completeSeasonIds.Contains(edition.SourceSeasonId))
        {
            return new AlmanacChaosEditionEntry(edition.SourceSeasonNumber, "InProgress", false, edition.FieldSize, null, null, []);
        }

        List<ChaosCupGroupStandingEntity> ordered = RequireFinal(edition, finalsBySeason);
        List<AlmanacCupPodium> podium = BuildPodium(ordered, names);
        ChaosCupGroupStandingEntity winner = ordered[0];
        return new AlmanacChaosEditionEntry(
            edition.SourceSeasonNumber,
            "Completed",
            true,
            edition.FieldSize,
            names.GetValueOrDefault(winner.SaveAthleteId, $"Athlete {winner.SaveAthleteId}"),
            winner.SaveAthleteId,
            podium);
    }

    internal static List<ChaosCupGroupStandingEntity> RequireFinal(
        ChaosCupEditionEntity edition,
        Dictionary<int, List<ChaosCupGroupStandingEntity>> finalsBySeason)
    {
        ArgumentNullException.ThrowIfNull(edition);
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

        return ordered;
    }

    internal static List<AlmanacCupPodium> BuildPodium(
        List<ChaosCupGroupStandingEntity> ordered,
        Dictionary<int, string> names)
    {
        ArgumentNullException.ThrowIfNull(ordered);
        return ordered
            .Where(e => e.GroupRank <= 3)
            .OrderBy(e => e.GroupRank)
            .Select(e => new AlmanacCupPodium(
                e.SaveAthleteId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                names.GetValueOrDefault(e.SaveAthleteId, $"Athlete {e.SaveAthleteId}"),
                e.GroupRank,
                e.GroupRank == 1 ? "Gold" : e.GroupRank == 2 ? "Silver" : "Bronze",
                e.GroupScoreThousandths))
            .ToList();
    }

    internal static IReadOnlyList<AlmanacChaosMedalEntry> BuildMedals(
        IReadOnlyList<AlmanacChaosEditionEntry> editions,
        Dictionary<int, string> names)
    {
        ArgumentNullException.ThrowIfNull(editions);
        Dictionary<int, (int Gold, int Silver, int Bronze, int LastSeason)> tally = new();
        Dictionary<int, HashSet<int>> seasonsByAthlete = new();
        foreach (AlmanacChaosEditionEntry edition in editions.Where(e => e.IsComplete))
        {
            TallyEdition(edition, tally, seasonsByAthlete);
        }

        return MapMedals(tally, seasonsByAthlete, names);
    }

    internal static void TallyEdition(
        AlmanacChaosEditionEntry edition,
        Dictionary<int, (int Gold, int Silver, int Bronze, int LastSeason)> tally,
        Dictionary<int, HashSet<int>> seasonsByAthlete)
    {
        ArgumentNullException.ThrowIfNull(edition);
        foreach (AlmanacCupPodium podium in edition.FinalTopThree)
        {
            int athleteId = ParseAthlete(podium, edition.SourceSeasonNumber);
            tally.TryGetValue(athleteId, out var current);
            current = AddMedal(current, podium.TeamRank);
            if (podium.TeamRank is < 1 or > 3)
            {
                continue;
            }

            current.LastSeason = Math.Max(current.LastSeason, edition.SourceSeasonNumber);
            tally[athleteId] = current;
            if (!seasonsByAthlete.TryGetValue(athleteId, out HashSet<int>? seasons))
            {
                seasons = new HashSet<int>();
                seasonsByAthlete[athleteId] = seasons;
            }

            seasons.Add(edition.SourceSeasonNumber);
        }
    }

    internal static (int Gold, int Silver, int Bronze, int LastSeason) AddMedal(
        (int Gold, int Silver, int Bronze, int LastSeason) current,
        int rank)
    {
        if (rank == 1)
        {
            current.Gold = checked(current.Gold + 1);
        }
        else if (rank == 2)
        {
            current.Silver = checked(current.Silver + 1);
        }
        else if (rank == 3)
        {
            current.Bronze = checked(current.Bronze + 1);
        }

        return current;
    }

    internal static int ParseAthlete(AlmanacCupPodium podium, int seasonNumber)
    {
        ArgumentNullException.ThrowIfNull(podium);
        if (!int.TryParse(podium.TeamKey, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int athleteId))
        {
            throw new InvalidOperationException($"Chaos Cup Final podium for Season {seasonNumber} has corrupt athlete key '{podium.TeamKey}'.");
        }

        return athleteId;
    }

    internal static IReadOnlyList<AlmanacChaosMedalEntry> MapMedals(
        Dictionary<int, (int Gold, int Silver, int Bronze, int LastSeason)> tally,
        Dictionary<int, HashSet<int>> seasonsByAthlete,
        Dictionary<int, string> names)
    {
        ArgumentNullException.ThrowIfNull(tally);
        return tally
            .Select(kv => new AlmanacChaosMedalEntry(
                kv.Key,
                names.GetValueOrDefault(kv.Key, $"Athlete {kv.Key}"),
                kv.Value.Gold,
                kv.Value.Silver,
                kv.Value.Bronze,
                seasonsByAthlete[kv.Key].Count,
                kv.Value.LastSeason))
            .OrderByDescending(m => m.Gold)
            .ThenByDescending(m => m.Silver)
            .ThenByDescending(m => m.Bronze)
            .ThenBy(m => m.AthleteName, StringComparer.Ordinal)
            .ThenBy(m => m.AthleteId)
            .ToList();
    }

    internal static IReadOnlyList<AlmanacChaosColorEntry> BuildColorMedals(
        IReadOnlyList<AlmanacChaosEditionEntry> editions,
        Dictionary<int, ChaosCupEntrantEntity> entrantsByAthlete)
    {
        ArgumentNullException.ThrowIfNull(editions);
        Dictionary<SportingColor, (int Gold, int Silver, int Bronze)> tally = new();
        foreach (AlmanacChaosEditionEntry edition in editions.Where(e => e.IsComplete))
        {
            TallyColorEdition(edition, entrantsByAthlete, tally);
        }

        return MapColorMedals(tally, entrantsByAthlete);
    }

    internal static void TallyColorEdition(
        AlmanacChaosEditionEntry edition,
        Dictionary<int, ChaosCupEntrantEntity> entrantsByAthlete,
        Dictionary<SportingColor, (int Gold, int Silver, int Bronze)> tally)
    {
        ArgumentNullException.ThrowIfNull(edition);
        foreach (AlmanacCupPodium podium in edition.FinalTopThree)
        {
            int athleteId = ParseAthlete(podium, edition.SourceSeasonNumber);
            if (!entrantsByAthlete.TryGetValue(athleteId, out ChaosCupEntrantEntity? entrant))
            {
                throw new InvalidOperationException($"Chaos Cup Final medalist {athleteId} for Season {edition.SourceSeasonNumber} is outside the frozen field.");
            }

            SportingColor color = (SportingColor)entrant.SportingColor;
            tally.TryGetValue(color, out var current);
            tally[color] = AddColorMedal(current, podium.TeamRank);
        }
    }

    internal static (int Gold, int Silver, int Bronze) AddColorMedal(
        (int Gold, int Silver, int Bronze) current,
        int rank)
    {
        if (rank == 1)
        {
            current.Gold = checked(current.Gold + 1);
        }
        else if (rank == 2)
        {
            current.Silver = checked(current.Silver + 1);
        }
        else if (rank == 3)
        {
            current.Bronze = checked(current.Bronze + 1);
        }

        return current;
    }

    internal static IReadOnlyList<AlmanacChaosColorEntry> MapColorMedals(
        Dictionary<SportingColor, (int Gold, int Silver, int Bronze)> tally,
        Dictionary<int, ChaosCupEntrantEntity> entrantsByAthlete)
    {
        ArgumentNullException.ThrowIfNull(tally);
        Dictionary<SportingColor, int> entrantsByColor = CountEntrantsByColor(entrantsByAthlete);
        return Enum.GetValues<SportingColor>()
            .Select(color =>
            {
                tally.TryGetValue(color, out var counts);
                entrantsByColor.TryGetValue(color, out int entrants);
                return new AlmanacChaosColorEntry(color.ToString(), color.ToString(), counts.Gold, counts.Silver, counts.Bronze, entrants);
            })
            .OrderByDescending(t => t.Gold)
            .ThenByDescending(t => t.Silver)
            .ThenByDescending(t => t.Bronze)
            .ThenBy(t => t.TeamName, StringComparer.Ordinal)
            .ToList();
    }

    internal static Dictionary<SportingColor, int> CountEntrantsByColor(
        Dictionary<int, ChaosCupEntrantEntity> entrantsByAthlete)
    {
        ArgumentNullException.ThrowIfNull(entrantsByAthlete);
        Dictionary<SportingColor, int> counts = new();
        foreach (ChaosCupEntrantEntity entrant in entrantsByAthlete.Values)
        {
            SportingColor color = (SportingColor)entrant.SportingColor;
            counts.TryGetValue(color, out int current);
            counts[color] = checked(current + 1);
        }

        return counts;
    }
}
