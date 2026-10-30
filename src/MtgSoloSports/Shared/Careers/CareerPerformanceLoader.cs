using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Careers;
using MtgSoloSports.SimulationKernel.Leagues;

namespace MtgSoloSports.Shared.Careers;

/// <summary>
/// Shared finalized career-placement loader for Hall of Fame and future squad
/// selection (MSS-095). Loads authoritative finalized placements without round
/// payloads: league finishes come from <c>SeasonStandings</c> of completed
/// seasons (field size = actual rows in that league season); Color Cup
/// individual finishes come from <c>ColorCupIndividualStandings</c>; Color Cup
/// team finishes credit each leg with its color's official team rank (field =
/// team count) via the group standings roster at the time of the competition;
/// Type Cup team finishes use the authoritative Final (legacy phase 0 or Final
/// phase 2) for finalists and a reduced qualification credit for eliminated
/// qualifiers; an athlete with a Final leg never also counts qualification.
/// Qualification standings alone never complete the tournament: a staged edition
/// with phase-1 rows but no persisted Final contributes nothing. Incomplete
/// cups (standings absent) contribute nothing; corrupt references abort. No
/// resimulation, no RNG, no clock. Read-only: no lock, no mutation.
/// Historical attribution is source-season scoped: league levels resolve from
/// the standing's own league row, Color legs use their persisted sporting color
/// and Type legs use their persisted creature type, never current membership.
/// Season caps are source-season numbers (selection source season or Hall of
/// Fame range end); all queries filter at the database level so per-athlete
/// projections stay bounded. Field sizes always reflect the full official
/// field for the capped seasons, even when an athlete subset is requested.
/// </summary>
public static class CareerPerformanceLoader
{
    public sealed record LoadResult(
        IReadOnlyList<CareerPerformanceScorer.PlacementEvent> Events,
        int MinSeasonNumber,
        int MaxSeasonNumber);

    public static async Task<LoadResult> LoadAsync(
        SaveDbContext context,
        int? fromSeason,
        int? toSeason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return await LoadAsync(context, fromSeason, toSeason, athleteIds: null, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<LoadResult> LoadAsync(
        SaveDbContext context,
        int? fromSeason,
        int? toSeason,
        IReadOnlyCollection<int>? athleteIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (fromSeason is <= 0)
        {
            throw new ArgumentException("From season must be positive.", nameof(fromSeason));
        }

        if (toSeason is <= 0)
        {
            throw new ArgumentException("To season must be positive.", nameof(toSeason));
        }

        if (fromSeason is not null && toSeason is not null && fromSeason.Value > toSeason.Value)
        {
            throw new ArgumentException("From season must not exceed to season.", nameof(fromSeason));
        }

        HashSet<int>? athleteFilter = NormalizeAthleteFilter(athleteIds);
        Dictionary<int, (int SeasonNumber, bool IsComplete)> seasons = await context.Seasons
            .AsNoTracking()
            .ToDictionaryAsync(e => e.Id, e => (e.SeasonNumber, e.IsComplete), cancellationToken)
            .ConfigureAwait(false);
        Dictionary<int, (int Kind, int Division)> leagues = await LoadLeaguesAsync(context, cancellationToken).ConfigureAwait(false);

        int minSeason = seasons.Count == 0 ? 0 : seasons.Values.Min(v => v.SeasonNumber);
        int maxSeason = seasons.Count == 0 ? 0 : seasons.Values.Max(v => v.SeasonNumber);

        List<CareerPerformanceScorer.PlacementEvent> events = [];
        events.AddRange(await LoadLeagueEventsAsync(context, seasons, leagues, fromSeason, toSeason, athleteFilter, cancellationToken).ConfigureAwait(false));
        events.AddRange(await LoadColorIndividualEventsAsync(context, fromSeason, toSeason, athleteFilter, cancellationToken).ConfigureAwait(false));
        events.AddRange(await LoadColorTeamEventsAsync(context, fromSeason, toSeason, athleteFilter, cancellationToken).ConfigureAwait(false));
        events.AddRange(await LoadTypeTeamEventsAsync(context, fromSeason, toSeason, athleteFilter, cancellationToken).ConfigureAwait(false));
        return new LoadResult(events, minSeason, maxSeason);
    }

    /// <summary>
    /// Read-only per-athlete career-score projection capped to completed source
    /// seasons at or below <paramref name="upToSeasonNumber"/> (null = all
    /// finalized history). Returns exactly the same per-athlete totals as the
    /// Hall of Fame range path with <c>toSeason</c> set to the same cap, without
    /// reimplementing event loading. Intended for squad-selection prestige
    /// (MSS-096): pass the selection source season and the candidate ids for a
    /// bounded query. No lock, no RNG, no mutation.
    /// </summary>
    public static async Task<IReadOnlyDictionary<int, CareerPerformanceScorer.AthleteCareerScore>> LoadScoresUpToAsync(
        SaveDbContext context,
        int? upToSeasonNumber,
        IReadOnlyCollection<int>? athleteIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (upToSeasonNumber is <= 0)
        {
            throw new ArgumentException("Season number must be positive.", nameof(upToSeasonNumber));
        }

        HashSet<int>? athleteFilter = NormalizeAthleteFilter(athleteIds);
        if (athleteFilter is not null && athleteFilter.Count == 0)
        {
            return new Dictionary<int, CareerPerformanceScorer.AthleteCareerScore>();
        }

        LoadResult loaded = await LoadAsync(context, fromSeason: null, toSeason: upToSeasonNumber, athleteFilter, cancellationToken).ConfigureAwait(false);
        return CareerPerformanceScorer.ComputeScores(loaded.Events);
    }

    private static HashSet<int>? NormalizeAthleteFilter(IReadOnlyCollection<int>? athleteIds)
    {
        if (athleteIds is null)
        {
            return null;
        }

        HashSet<int> filter = athleteIds.ToHashSet();
        foreach (int athleteId in filter)
        {
            if (athleteId <= 0)
            {
                throw new ArgumentException("Athlete id must be positive.", nameof(athleteIds));
            }
        }

        return filter;
    }

    private static bool InRange(int seasonNumber, int? fromSeason, int? toSeason) =>
        (fromSeason is null || seasonNumber >= fromSeason.Value) &&
        (toSeason is null || seasonNumber <= toSeason.Value);

    private static async Task<Dictionary<int, (int Kind, int Division)>> LoadLeaguesAsync(
        SaveDbContext context,
        CancellationToken cancellationToken)
    {
        // League levels resolve from the standing's own league row (historical
        // attribution). The table stays small (one row per league per season),
        // so keep the full map to preserve exact corrupt-reference behavior.
        return await context.Leagues
            .AsNoTracking()
            .ToDictionaryAsync(e => e.Id, e => (e.Kind, e.FeederDivision), cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<List<CareerPerformanceScorer.PlacementEvent>> LoadLeagueEventsAsync(
        SaveDbContext context,
        Dictionary<int, (int SeasonNumber, bool IsComplete)> seasons,
        Dictionary<int, (int Kind, int Division)> leagues,
        int? fromSeason,
        int? toSeason,
        HashSet<int>? athleteFilter,
        CancellationToken cancellationToken)
    {
        HashSet<int> eligibleSeasonIds = seasons
            .Where(kv => kv.Value.IsComplete && InRange(kv.Value.SeasonNumber, fromSeason, toSeason))
            .Select(kv => kv.Key)
            .ToHashSet();
        if (eligibleSeasonIds.Count == 0)
        {
            await GuardUnknownSeasonReferenceAsync(context, seasons, cancellationToken).ConfigureAwait(false);
            return [];
        }

        // Field sizes reflect the full official field for the capped seasons,
        // even when a per-athlete subset is requested.
        Dictionary<(int SeasonId, int LeagueId), int> fieldSizes = await context.SeasonStandings
            .AsNoTracking()
            .Where(e => eligibleSeasonIds.Contains(e.SeasonId))
            .GroupBy(e => new { e.SeasonId, e.LeagueId })
            .Select(g => new { g.Key.SeasonId, g.Key.LeagueId, Count = g.Count() })
            .ToDictionaryAsync(e => (e.SeasonId, e.LeagueId), e => e.Count, cancellationToken)
            .ConfigureAwait(false);

        IQueryable<SeasonStandingEntity> query = context.SeasonStandings
            .AsNoTracking()
            .Where(e => eligibleSeasonIds.Contains(e.SeasonId));
        if (athleteFilter is not null)
        {
            query = query.Where(e => athleteFilter.Contains(e.SaveAthleteId));
        }

        List<LeagueRow> rows = await query
            .Select(e => new LeagueRow(e.SeasonId, e.LeagueId, e.SaveAthleteId, e.SeasonRank))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        await GuardUnknownSeasonReferenceAsync(context, seasons, cancellationToken).ConfigureAwait(false);

        List<CareerPerformanceScorer.PlacementEvent> events = new(rows.Count);
        foreach (LeagueRow row in rows)
        {
            CareerPerformanceScorer.PlacementEvent? scored = MapLeagueRow(row, seasons, leagues, fieldSizes, fromSeason, toSeason);
            if (scored is not null)
            {
                events.Add(scored);
            }
        }

        return events;
    }

    private static async Task GuardUnknownSeasonReferenceAsync(
        SaveDbContext context,
        Dictionary<int, (int SeasonNumber, bool IsComplete)> seasons,
        CancellationToken cancellationToken)
    {
        // Bounded corruption guard: the capped standings query skips rows outside
        // the eligible seasons, so an explicit Take(1) probe preserves the
        // fail-loud invariant for standings pointing at an unknown season.
        if (seasons.Count == 0)
        {
            bool anyStanding = await context.SeasonStandings.AsNoTracking().AnyAsync(cancellationToken).ConfigureAwait(false);
            if (anyStanding)
            {
                int unknown = await context.SeasonStandings
                    .AsNoTracking()
                    .Select(e => e.SeasonId)
                    .FirstAsync(cancellationToken)
                    .ConfigureAwait(false);
                throw new InvalidOperationException($"Season standing references unknown season {unknown}.");
            }

            return;
        }

        HashSet<int> knownIds = seasons.Keys.ToHashSet();
        int? corrupt = await context.SeasonStandings
            .AsNoTracking()
            .Where(e => !knownIds.Contains(e.SeasonId))
            .Select(e => (int?)e.SeasonId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (corrupt is not null)
        {
            throw new InvalidOperationException($"Season standing references unknown season {corrupt.Value}.");
        }
    }

    internal static CareerPerformanceScorer.PlacementEvent? MapLeagueRow(
        LeagueRow row,
        Dictionary<int, (int SeasonNumber, bool IsComplete)> seasons,
        Dictionary<int, (int Kind, int Division)> leagues,
        Dictionary<(int SeasonId, int LeagueId), int> fieldSizes,
        int? fromSeason,
        int? toSeason)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(seasons);
        ArgumentNullException.ThrowIfNull(leagues);
        ArgumentNullException.ThrowIfNull(fieldSizes);
        if (!seasons.TryGetValue(row.SeasonId, out (int SeasonNumber, bool IsComplete) season))
        {
            throw new InvalidOperationException($"Season standing references unknown season {row.SeasonId}.");
        }

        if (!season.IsComplete || !InRange(season.SeasonNumber, fromSeason, toSeason))
        {
            return null;
        }

        if (!leagues.TryGetValue(row.LeagueId, out (int Kind, int Division) league))
        {
            throw new InvalidOperationException($"Season standing references unknown league {row.LeagueId}.");
        }

        LeagueLevel level = LeagueHierarchy.LevelForDivision(
            (FeederDivision)league.Division, isSuperleague: league.Kind == (int)LeagueKind.Superleague);
        CareerPerformanceScorer.Category category = CareerPerformanceScorer.CategoryForLevel(level);
        return new CareerPerformanceScorer.PlacementEvent(
            row.SaveAthleteId, season.SeasonNumber, category, row.Rank, fieldSizes[(row.SeasonId, row.LeagueId)], IsFinal: true);
    }

    internal static async Task<List<CareerPerformanceScorer.PlacementEvent>> LoadColorIndividualEventsAsync(
        SaveDbContext context,
        int? fromSeason,
        int? toSeason,
        HashSet<int>? athleteFilter,
        CancellationToken cancellationToken)
    {
        IQueryable<ColorCupIndividualStandingEntity> fieldQuery = context.ColorCupIndividualStandings.AsNoTracking();
        if (fromSeason is not null)
        {
            fieldQuery = fieldQuery.Where(e => e.SourceSeasonNumber >= fromSeason.Value);
        }

        if (toSeason is not null)
        {
            fieldQuery = fieldQuery.Where(e => e.SourceSeasonNumber <= toSeason.Value);
        }

        Dictionary<int, int> fieldSizes = await fieldQuery
            .GroupBy(e => e.SourceSeasonNumber)
            .Select(g => new { SeasonNumber = g.Key, Count = g.Count() })
            .ToDictionaryAsync(e => e.SeasonNumber, e => e.Count, cancellationToken)
            .ConfigureAwait(false);

        IQueryable<ColorCupIndividualStandingEntity> query = fieldQuery;
        if (athleteFilter is not null)
        {
            query = query.Where(e => athleteFilter.Contains(e.SaveAthleteId));
        }

        List<ColorIndividualRow> rows = await query
            .Select(e => new ColorIndividualRow(e.SourceSeasonNumber, e.SaveAthleteId, e.CupRank))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        List<CareerPerformanceScorer.PlacementEvent> events = new(rows.Count);
        foreach (ColorIndividualRow row in rows)
        {
            if (!InRange(row.SeasonNumber, fromSeason, toSeason))
            {
                continue;
            }

            events.Add(new CareerPerformanceScorer.PlacementEvent(
                row.AthleteId,
                row.SeasonNumber,
                CareerPerformanceScorer.Category.ColorCupIndividual,
                row.Rank,
                fieldSizes[row.SeasonNumber],
                IsFinal: true));
        }

        return events;
    }

    internal static async Task<List<CareerPerformanceScorer.PlacementEvent>> LoadColorTeamEventsAsync(
        SaveDbContext context,
        int? fromSeason,
        int? toSeason,
        HashSet<int>? athleteFilter,
        CancellationToken cancellationToken)
    {
        IQueryable<ColorCupTeamStandingEntity> teamsQuery = context.ColorCupTeamStandings.AsNoTracking();
        IQueryable<ColorCupTeamGroupStandingEntity> legsQuery = context.ColorCupTeamGroupStandings.AsNoTracking();
        if (fromSeason is not null)
        {
            teamsQuery = teamsQuery.Where(e => e.SourceSeasonNumber >= fromSeason.Value);
            legsQuery = legsQuery.Where(e => e.SourceSeasonNumber >= fromSeason.Value);
        }

        if (toSeason is not null)
        {
            teamsQuery = teamsQuery.Where(e => e.SourceSeasonNumber <= toSeason.Value);
            legsQuery = legsQuery.Where(e => e.SourceSeasonNumber <= toSeason.Value);
        }

        List<ColorTeamStandingRow> teams = await teamsQuery
            .Select(e => new ColorTeamStandingRow(e.SourceSeasonNumber, e.SportingColor, e.TeamRank))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        Dictionary<int, List<ColorTeamStandingRow>> teamsBySeason = teams
            .GroupBy(t => t.SeasonNumber)
            .ToDictionary(g => g.Key, g => g.ToList());

        if (athleteFilter is not null)
        {
            legsQuery = legsQuery.Where(e => athleteFilter.Contains(e.SaveAthleteId));
        }

        List<ColorTeamLegRow> legs = await legsQuery
            .Select(e => new ColorTeamLegRow(e.SourceSeasonNumber, e.SaveAthleteId, e.SportingColor))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return MapColorTeamEvents(legs, teamsBySeason, fromSeason, toSeason);
    }

    private static List<CareerPerformanceScorer.PlacementEvent> MapColorTeamEvents(
        List<ColorTeamLegRow> legs,
        Dictionary<int, List<ColorTeamStandingRow>> teamsBySeason,
        int? fromSeason,
        int? toSeason)
    {
        ArgumentNullException.ThrowIfNull(legs);
        ArgumentNullException.ThrowIfNull(teamsBySeason);
        List<CareerPerformanceScorer.PlacementEvent> events = [];
        foreach (ColorTeamLegRow leg in legs)
        {
            if (!InRange(leg.SeasonNumber, fromSeason, toSeason))
            {
                continue;
            }

            if (!teamsBySeason.TryGetValue(leg.SeasonNumber, out List<ColorTeamStandingRow>? seasonTeams))
            {
                continue;
            }

            ColorTeamStandingRow? team = seasonTeams.SingleOrDefault(t => t.Color == leg.Color);
            if (team is null)
            {
                throw new InvalidOperationException(
                    $"Color Cup team leg for season {leg.SeasonNumber} references unknown color {leg.Color}.");
            }

            events.Add(new CareerPerformanceScorer.PlacementEvent(
                leg.AthleteId,
                leg.SeasonNumber,
                CareerPerformanceScorer.Category.ColorCupTeam,
                team.Rank,
                seasonTeams.Count,
                IsFinal: true));
        }

        return events;
    }

    internal static async Task<List<CareerPerformanceScorer.PlacementEvent>> LoadTypeTeamEventsAsync(
        SaveDbContext context,
        int? fromSeason,
        int? toSeason,
        HashSet<int>? athleteFilter,
        CancellationToken cancellationToken)
    {
        IQueryable<TypeCupTeamStandingEntity> standingsQuery = context.TypeCupTeamStandings.AsNoTracking();
        IQueryable<TypeCupTeamGroupStandingEntity> legsQuery = context.TypeCupTeamGroupStandings.AsNoTracking();
        if (fromSeason is not null)
        {
            standingsQuery = standingsQuery.Where(e => e.SourceSeasonNumber >= fromSeason.Value);
            legsQuery = legsQuery.Where(e => e.SourceSeasonNumber >= fromSeason.Value);
        }

        if (toSeason is not null)
        {
            standingsQuery = standingsQuery.Where(e => e.SourceSeasonNumber <= toSeason.Value);
            legsQuery = legsQuery.Where(e => e.SourceSeasonNumber <= toSeason.Value);
        }

        List<TypeTeamStandingRow> standings = await standingsQuery
            .Select(e => new TypeTeamStandingRow(
                e.SourceSeasonNumber, e.TournamentPhase, e.QualificationGroup, e.CreatureType, e.TeamRank))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Qualification field sizes always reflect the full official group for
        // the capped seasons, even for per-athlete subsets: standings stay
        // unfiltered by athlete below while legs carry the athlete filter.
        if (athleteFilter is not null)
        {
            legsQuery = legsQuery.Where(e => athleteFilter.Contains(e.SaveAthleteId));
        }

        List<TypeTeamLegRow> legs = await legsQuery
            .Select(e => new TypeTeamLegRow(
                e.SourceSeasonNumber, e.TournamentPhase, e.QualificationGroup,
                e.SaveAthleteId, e.CreatureType, e.GroupNumber))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Distinct capped seasons come from the full standings plus the
        // requested athletes' legs; unrequested athletes never widen the set.
        // Field sizes below are recomputed from the full standings per season,
        // so per-athlete filtering cannot shrink official fields.
        List<CareerPerformanceScorer.PlacementEvent> events = [];
        foreach (int seasonNumber in standings.Select(s => s.SeasonNumber).Concat(legs.Select(l => l.SeasonNumber)).Distinct())
        {
            if (!InRange(seasonNumber, fromSeason, toSeason))
            {
                continue;
            }

            events.AddRange(ScoreTypeSeason(seasonNumber, standings, legs));
        }

        return events;
    }

    private static List<CareerPerformanceScorer.PlacementEvent> ScoreTypeSeason(
        int seasonNumber,
        List<TypeTeamStandingRow> standings,
        List<TypeTeamLegRow> legs)
    {
        List<TypeTeamStandingRow> seasonStandings = standings.Where(s => s.SeasonNumber == seasonNumber).ToList();
        List<TypeTeamLegRow> seasonLegs = legs.Where(l => l.SeasonNumber == seasonNumber).ToList();
        if (seasonStandings.Count == 0 || seasonLegs.Count == 0)
        {
            return [];
        }

        TypeFinals finals = ResolveFinals(seasonStandings);
        if (finals.ByTeam.Count == 0)
        {
            // No authoritative Final (legacy phase 0 or staged phase 2) is
            // persisted. Phase-1 qualification rows alone never complete the
            // tournament, so award nothing rather than provisional credit to
            // teams that may still advance.
            return [];
        }

        HashSet<int> finalists = CollectFinalists(seasonLegs, finals.HasStagedFinal);
        if (!finals.HasStagedFinal)
        {
            // Legacy single-field edition: the Final is the only result.
            // Never award qualification credit on the legacy path.
            return ScoreTypeFinalists(seasonNumber, seasonLegs, finals.ByTeam, finalists);
        }

        List<CareerPerformanceScorer.PlacementEvent> events = [];
        events.AddRange(ScoreTypeFinalists(seasonNumber, seasonLegs, finals.ByTeam, finalists));
        events.AddRange(ScoreTypeQualifiers(seasonNumber, seasonStandings, seasonLegs, finalists));
        return events;
    }

    private static TypeFinals ResolveFinals(List<TypeTeamStandingRow> seasonStandings)
    {
        ArgumentNullException.ThrowIfNull(seasonStandings);
        bool hasStagedFinal = seasonStandings.Any(s => s.Phase == 2);
        List<TypeTeamStandingRow> finals = hasStagedFinal
            ? seasonStandings.Where(s => s.Phase == 2).ToList()
            : seasonStandings.Where(s => s.Phase is 2 or 0).ToList();
        Dictionary<string, TypeTeamStandingRow> byTeam = finals
            .GroupBy(s => s.Team, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Single(), StringComparer.Ordinal);
        return new TypeFinals(byTeam, hasStagedFinal);
    }

    private static HashSet<int> CollectFinalists(List<TypeTeamLegRow> seasonLegs, bool hasStagedFinal)
    {
        ArgumentNullException.ThrowIfNull(seasonLegs);
        return seasonLegs
            .Where(l => hasStagedFinal ? l.Phase == 2 : l.Phase == 0)
            .Select(l => l.AthleteId)
            .ToHashSet();
    }

    private static List<CareerPerformanceScorer.PlacementEvent> ScoreTypeFinalists(
        int seasonNumber,
        List<TypeTeamLegRow> seasonLegs,
        Dictionary<string, TypeTeamStandingRow> finalByTeam,
        HashSet<int> finalists)
    {
        ArgumentNullException.ThrowIfNull(seasonLegs);
        ArgumentNullException.ThrowIfNull(finalByTeam);
        ArgumentNullException.ThrowIfNull(finalists);
        List<CareerPerformanceScorer.PlacementEvent> events = [];
        foreach (TypeTeamLegRow leg in seasonLegs.Where(l => finalists.Contains(l.AthleteId)).DistinctBy(l => l.AthleteId))
        {
            if (!finalByTeam.TryGetValue(leg.Team, out TypeTeamStandingRow? team))
            {
                throw new InvalidOperationException(
                    $"Type Cup Final leg for season {seasonNumber} references unknown team '{leg.Team}'.");
            }

            events.Add(new CareerPerformanceScorer.PlacementEvent(
                leg.AthleteId,
                seasonNumber,
                CareerPerformanceScorer.Category.TypeCupTeam,
                team.Rank,
                finalByTeam.Count,
                IsFinal: true));
        }

        return events;
    }

    private static List<CareerPerformanceScorer.PlacementEvent> ScoreTypeQualifiers(
        int seasonNumber,
        List<TypeTeamStandingRow> seasonStandings,
        List<TypeTeamLegRow> seasonLegs,
        HashSet<int> finalists)
    {
        ArgumentNullException.ThrowIfNull(seasonStandings);
        ArgumentNullException.ThrowIfNull(seasonLegs);
        ArgumentNullException.ThrowIfNull(finalists);
        Dictionary<(int Group, string Team), List<TypeTeamStandingRow>> qualByGroupTeam = seasonStandings
            .Where(s => s.Phase == 1)
            .GroupBy(s => (s.Group, s.Team))
            .ToDictionary(g => g.Key, g => g.ToList());
        Dictionary<int, int> qualFieldByGroup = seasonStandings
            .Where(s => s.Phase == 1)
            .GroupBy(s => s.Group)
            .ToDictionary(g => g.Key, g => g.Count());

        List<CareerPerformanceScorer.PlacementEvent> events = [];
        foreach (TypeTeamLegRow leg in seasonLegs.Where(l => !finalists.Contains(l.AthleteId)).DistinctBy(l => l.AthleteId))
        {
            CareerPerformanceScorer.PlacementEvent? scored = MapQualifierLeg(
                seasonNumber, seasonLegs, qualByGroupTeam, qualFieldByGroup, leg);
            if (scored is not null)
            {
                events.Add(scored);
            }
        }

        return events;
    }

    private static CareerPerformanceScorer.PlacementEvent? MapQualifierLeg(
        int seasonNumber,
        List<TypeTeamLegRow> seasonLegs,
        Dictionary<(int Group, string Team), List<TypeTeamStandingRow>> qualByGroupTeam,
        Dictionary<int, int> qualFieldByGroup,
        TypeTeamLegRow leg)
    {
        ArgumentNullException.ThrowIfNull(seasonLegs);
        ArgumentNullException.ThrowIfNull(qualByGroupTeam);
        ArgumentNullException.ThrowIfNull(qualFieldByGroup);
        ArgumentNullException.ThrowIfNull(leg);
        TypeTeamLegRow qualLeg = seasonLegs
            .Where(l => l.AthleteId == leg.AthleteId && l.Phase == 1)
            .OrderBy(l => l.Group)
            .FirstOrDefault()
            ?? leg;
        (int Group, string Team) key = (qualLeg.Group, qualLeg.Team);
        if (!qualByGroupTeam.TryGetValue(key, out List<TypeTeamStandingRow>? groupTeams))
        {
            return null;
        }

        TypeTeamStandingRow? team = groupTeams.SingleOrDefault(t => string.Equals(t.Team, qualLeg.Team, StringComparison.Ordinal));
        if (team is null)
        {
            return null;
        }

        return new CareerPerformanceScorer.PlacementEvent(
            leg.AthleteId,
            seasonNumber,
            CareerPerformanceScorer.Category.TypeCupTeam,
            team.Rank,
            qualFieldByGroup[qualLeg.Group],
            IsFinal: false);
    }

    private sealed record TypeFinals(Dictionary<string, TypeTeamStandingRow> ByTeam, bool HasStagedFinal);

    internal sealed record LeagueRow(int SeasonId, int LeagueId, int SaveAthleteId, int Rank);
    private sealed record ColorIndividualRow(int SeasonNumber, int AthleteId, int Rank);
    private sealed record ColorTeamStandingRow(int SeasonNumber, int Color, int Rank);
    private sealed record ColorTeamLegRow(int SeasonNumber, int AthleteId, int Color);
    private sealed record TypeTeamStandingRow(int SeasonNumber, int Phase, int Group, string Team, int Rank);
    private sealed record TypeTeamLegRow(int SeasonNumber, int Phase, int Group, int AthleteId, string Team, int GroupNumber);
}
