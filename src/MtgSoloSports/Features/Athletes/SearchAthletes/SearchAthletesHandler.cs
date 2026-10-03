using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;

namespace MtgSoloSports.Features.Athletes.SearchAthletes;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Serves the athlete
/// search/browse page from authoritative persisted history with bulk queries
/// only (no N+1, no round payloads). Non-pool seasons reuse the domain concept
/// <c>AthleteCareer.SeasonsActive</c> (distinct seasons with league membership
/// outside the pool, 0/1 per season, retained after pool return); when career
/// projections are missing for old saves the count falls back to distinct
/// active <c>SeasonMembership</c> seasons. Honours/titles/Cup presence derive
/// from authoritative standings tables so old saves without accelerator rows
/// stay searchable. Read-only: no lock, no RNG access, no mutation.
/// </summary>
public sealed class SearchAthletesHandler
{
    private readonly SaveStore _store;

    public SearchAthletesHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<SearchAthletesResponse> HandleAsync(Guid saveId, SearchAthletesQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        query.Validate();
        using SaveDbContext context = _store.OpenDbContext(saveId);
        await _store.EnsureMigratedAsync(saveId, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<SearchAthletesFilter.Candidate> candidates = await LoadCandidatesAsync(context, cancellationToken).ConfigureAwait(false);
        return MapPaged(saveId, candidates, query);
    }

    public async Task<SearchAthletesOptionsResponse> HandleOptionsAsync(Guid saveId, CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        using SaveDbContext context = _store.OpenDbContext(saveId);
        await _store.EnsureMigratedAsync(saveId, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<SearchAthletesFilter.Candidate> candidates = await LoadCandidatesAsync(context, cancellationToken).ConfigureAwait(false);
        return MapOptions(saveId, candidates);
    }

    internal static SearchAthletesResponse MapPaged(
        Guid saveId,
        IReadOnlyList<SearchAthletesFilter.Candidate> candidates,
        SearchAthletesQuery query)
    {
        IReadOnlyList<SearchAthletesFilter.Candidate> filtered = SearchAthletesFilter.ApplyFilters(candidates, query);
        (string sort, string dir) = query.EffectiveSort();
        IReadOnlyList<SearchAthletesFilter.Candidate> sorted = SearchAthletesFilter.ApplySort(filtered, sort, dir);
        int skip = query.EffectiveSkip;
        int take = query.EffectiveTake;
        List<AthleteSearchResultDto> page = sorted
            .Skip(skip)
            .Take(take)
            .Select(MapResult)
            .ToList();
        return new SearchAthletesResponse(saveId, filtered.Count, skip, take, page);
    }

    internal static AthleteSearchResultDto MapResult(SearchAthletesFilter.Candidate candidate)
    {
        return new AthleteSearchResultDto(
            candidate.AthleteId,
            candidate.Name,
            candidate.ImageUrl,
            candidate.TypeLine,
            candidate.SportingColor,
            candidate.SportingColorName,
            candidate.CreatureTypes,
            candidate.IsActive,
            candidate.CurrentLeagueName,
            candidate.CurrentLeagueKind,
            candidate.NonPoolSeasons,
            candidate.HonoursCount,
            candidate.TitlesCount,
            candidate.BestSeasonFinish,
            candidate.BestSeasonNumber,
            candidate.EverSuperleague,
            candidate.SuperSeasons,
            candidate.CupAppearances,
            candidate.CupPodiums,
            candidate.CupTitles);
    }

    internal static SearchAthletesOptionsResponse MapOptions(Guid saveId, IReadOnlyList<SearchAthletesFilter.Candidate> candidates)
    {
        IReadOnlyList<AthleteSearchColourOption> colours = BuildColourOptions(candidates);
        IReadOnlyList<AthleteSearchTypeOption> types = BuildTypeOptions(candidates);
        IReadOnlyList<AthleteSearchLeagueOption> leagues = BuildLeagueOptions(candidates);
        int maxNonPool = candidates.Count == 0 ? 0 : candidates.Max(e => e.NonPoolSeasons);
        int maxHonours = candidates.Count == 0 ? 0 : candidates.Max(e => e.HonoursCount);
        int maxTitles = candidates.Count == 0 ? 0 : candidates.Max(e => e.TitlesCount);
        return new SearchAthletesOptionsResponse(
            saveId,
            colours,
            types,
            leagues,
            maxNonPool,
            maxHonours,
            maxTitles,
            candidates.Any(e => e.EverSuperleague),
            candidates.Any(e => e.CupAppearances > 0));
    }

    private static IReadOnlyList<AthleteSearchColourOption> BuildColourOptions(
        IReadOnlyList<SearchAthletesFilter.Candidate> candidates)
    {
        List<AthleteSearchColourOption> colours = [];
        for (int color = 0; color <= 7; color++)
        {
            string name = Enum.IsDefined(typeof(SportingColor), color) ? ((SportingColor)color).ToString() : $"Color{color}";
            colours.Add(new AthleteSearchColourOption(color, name, candidates.Count(e => e.SportingColor == color)));
        }

        return colours;
    }

    private static IReadOnlyList<AthleteSearchTypeOption> BuildTypeOptions(
        IReadOnlyList<SearchAthletesFilter.Candidate> candidates)
    {
        Dictionary<string, int> typeCounts = new(StringComparer.OrdinalIgnoreCase);
        foreach (SearchAthletesFilter.Candidate candidate in candidates)
        {
            foreach (string type in candidate.CreatureTypes.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string key = type.Trim();
                if (key.Length == 0)
                {
                    continue;
                }

                typeCounts.TryGetValue(key, out int count);
                typeCounts[key] = checked(count + 1);
            }
        }

        return typeCounts
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => new AthleteSearchTypeOption(kv.Key, kv.Value))
            .ToList();
    }

    private static IReadOnlyList<AthleteSearchLeagueOption> BuildLeagueOptions(
        IReadOnlyList<SearchAthletesFilter.Candidate> candidates)
    {
        Dictionary<string, AthleteSearchLeagueOption> leagues = new(StringComparer.Ordinal);
        foreach (SearchAthletesFilter.Candidate candidate in candidates)
        {
            string name = candidate.IsActive ? (candidate.CurrentLeagueName ?? "League") : SearchAthletesFilter.PoolLeagueName;
            bool isPool = !candidate.IsActive;
            int? kind = candidate.IsActive ? candidate.CurrentLeagueKind : null;
            if (leagues.TryGetValue(name, out AthleteSearchLeagueOption? existing))
            {
                leagues[name] = existing with { Count = checked(existing.Count + 1) };
            }
            else
            {
                leagues[name] = new AthleteSearchLeagueOption(name, kind, isPool, 1);
            }
        }

        return leagues.Values
            .OrderBy(e => e.IsPool)
            .ThenBy(e => e.Name, StringComparer.Ordinal)
            .ToList();
    }

    internal static async Task<IReadOnlyList<SearchAthletesFilter.Candidate>> LoadCandidatesAsync(
        SaveDbContext context,
        CancellationToken cancellationToken)
    {
        List<AthleteProbe> athletes = await LoadAthletesAsync(context, cancellationToken).ConfigureAwait(false);
        Dictionary<int, CareerProbe> careers = await LoadCareersAsync(context, cancellationToken).ConfigureAwait(false);
        Dictionary<int, int> fallbackNonPool = await LoadFallbackNonPoolAsync(context, careers, cancellationToken).ConfigureAwait(false);
        IReadOnlyDictionary<int, SearchAthletesFilter.HonourTotals> honours = await LoadHonourTotalsAsync(context, cancellationToken).ConfigureAwait(false);
        Dictionary<int, (bool EverSuper, int SuperSeasons)> super = await LoadSuperTenureAsync(context, cancellationToken).ConfigureAwait(false);
        return BuildCandidates(athletes, careers, fallbackNonPool, honours, super);
    }

    private static Task<List<AthleteProbe>> LoadAthletesAsync(SaveDbContext context, CancellationToken cancellationToken)
    {
        return context.SaveAthletes
            .AsNoTracking()
            .OrderBy(e => e.Id)
            .Select(e => new AthleteProbe(
                e.Id, e.Name, e.SportingColor, e.CreatureTypesJson, e.TypeLine, e.ImageUrl))
            .ToListAsync(cancellationToken);
    }

    private static Task<Dictionary<int, CareerProbe>> LoadCareersAsync(SaveDbContext context, CancellationToken cancellationToken)
    {
        return context.AthleteCareers
            .AsNoTracking()
            .Select(e => new CareerProbe(
                e.SaveAthleteId, e.SeasonsActive, e.IsActive, e.CurrentLeagueName, e.CurrentLeagueKind,
                e.BestSeasonFinish, e.BestSeasonNumber))
            .ToDictionaryAsync(e => e.SaveAthleteId, cancellationToken);
    }

    private static IReadOnlyList<SearchAthletesFilter.Candidate> BuildCandidates(
        IReadOnlyList<AthleteProbe> athletes,
        IReadOnlyDictionary<int, CareerProbe> careers,
        IReadOnlyDictionary<int, int> fallbackNonPool,
        IReadOnlyDictionary<int, SearchAthletesFilter.HonourTotals> honours,
        IReadOnlyDictionary<int, (bool EverSuper, int SuperSeasons)> super)
    {
        List<SearchAthletesFilter.Candidate> candidates = new(athletes.Count);
        foreach (AthleteProbe athlete in athletes)
        {
            candidates.Add(BuildCandidate(athlete, careers, fallbackNonPool, honours, super));
        }

        return candidates;
    }

    private static SearchAthletesFilter.Candidate BuildCandidate(
        AthleteProbe athlete,
        IReadOnlyDictionary<int, CareerProbe> careers,
        IReadOnlyDictionary<int, int> fallbackNonPool,
        IReadOnlyDictionary<int, SearchAthletesFilter.HonourTotals> honours,
        IReadOnlyDictionary<int, (bool EverSuper, int SuperSeasons)> super)
    {
        careers.TryGetValue(athlete.Id, out CareerProbe? career);
        int nonPool = career?.SeasonsActive ?? fallbackNonPool.GetValueOrDefault(athlete.Id);
        honours.TryGetValue(athlete.Id, out SearchAthletesFilter.HonourTotals? totals);
        totals ??= new SearchAthletesFilter.HonourTotals(0, 0, 0, 0, 0);
        super.TryGetValue(athlete.Id, out (bool EverSuper, int SuperSeasons) tenure);
        IReadOnlyList<string> types = SearchAthletesFilter.ParseCreatureTypes(athlete.CreatureTypesJson);
        string colorName = Enum.IsDefined(typeof(SportingColor), athlete.SportingColor)
            ? ((SportingColor)athlete.SportingColor).ToString()
            : $"Color{athlete.SportingColor}";
        return new SearchAthletesFilter.Candidate(
            athlete.Id,
            athlete.Name,
            athlete.ImageUrl,
            athlete.TypeLine,
            athlete.SportingColor,
            colorName,
            types,
            career?.IsActive ?? false,
            career?.CurrentLeagueName,
            career?.CurrentLeagueKind,
            nonPool,
            totals.Honours,
            totals.Titles,
            career?.BestSeasonFinish,
            career?.BestSeasonNumber,
            tenure.EverSuper,
            tenure.SuperSeasons,
            totals.CupAppearances,
            totals.CupPodiums,
            totals.CupTitles);
    }

    private static async Task<Dictionary<int, int>> LoadFallbackNonPoolAsync(
        SaveDbContext context,
        Dictionary<int, CareerProbe> careers,
        CancellationToken cancellationToken)
    {
        if (careers.Count > 0)
        {
            bool anyMissing = await context.SaveAthletes
                .AsNoTracking()
                .AnyAsync(e => !careers.Keys.Contains(e.Id), cancellationToken)
                .ConfigureAwait(false);
            if (!anyMissing)
            {
                return [];
            }
        }

        // Distinct active seasons per athlete from the authoritative membership table.
        // One membership row per athlete per season; LeagueId null means pool.
        List<MembershipProbe> rows = await context.SeasonMemberships
            .AsNoTracking()
            .Where(e => e.LeagueId != null)
            .Select(e => new MembershipProbe(e.SaveAthleteId, e.SeasonId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return CountDistinctSeasons(rows);
    }

    private static Dictionary<int, int> CountDistinctSeasons(IReadOnlyList<MembershipProbe> rows)
    {
        Dictionary<int, HashSet<int>> seasonsByAthlete = [];
        foreach (MembershipProbe row in rows)
        {
            if (!seasonsByAthlete.TryGetValue(row.SaveAthleteId, out HashSet<int>? seasons))
            {
                seasons = [];
                seasonsByAthlete[row.SaveAthleteId] = seasons;
            }

            seasons.Add(row.SeasonId);
        }

        return seasonsByAthlete.ToDictionary(kv => kv.Key, kv => kv.Value.Count);
    }

    internal static async Task<IReadOnlyDictionary<int, SearchAthletesFilter.HonourTotals>> LoadHonourTotalsAsync(
        SaveDbContext context,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<SearchAthletesFilter.LeaguePodiumInput> league = await LoadLeaguePodiumsAsync(context, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<SearchAthletesFilter.CupIndividualInput> individuals = await LoadCupIndividualsAsync(context, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<SearchAthletesFilter.CupColorLegInput> colorLegs = await LoadColorLegsAsync(context, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<SearchAthletesFilter.CupColorTeamInput> colorTeams = await LoadColorTeamsAsync(context, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<SearchAthletesFilter.CupTypeLegInput> typeLegs = await LoadTypeLegsAsync(context, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<SearchAthletesFilter.CupTypeTeamInput> typeTeams = await LoadTypeTeamsAsync(context, cancellationToken).ConfigureAwait(false);
        return SearchAthletesFilter.AggregateHonours(league, individuals, colorLegs, colorTeams, typeLegs, typeTeams);
    }

    private static Task<List<SearchAthletesFilter.LeaguePodiumInput>> LoadLeaguePodiumsAsync(
        SaveDbContext context, CancellationToken cancellationToken)
    {
        return context.SeasonStandings
            .AsNoTracking()
            .Where(e => e.SeasonRank >= 1 && e.SeasonRank <= 3)
            .Select(e => new SearchAthletesFilter.LeaguePodiumInput(e.SaveAthleteId, e.SeasonRank))
            .ToListAsync(cancellationToken);
    }

    private static Task<List<SearchAthletesFilter.CupIndividualInput>> LoadCupIndividualsAsync(
        SaveDbContext context, CancellationToken cancellationToken)
    {
        return context.ColorCupIndividualStandings
            .AsNoTracking()
            .Select(e => new SearchAthletesFilter.CupIndividualInput(e.SaveAthleteId, e.CupRank))
            .ToListAsync(cancellationToken);
    }

    private static Task<List<SearchAthletesFilter.CupColorLegInput>> LoadColorLegsAsync(
        SaveDbContext context, CancellationToken cancellationToken)
    {
        return context.ColorCupTeamGroupStandings
            .AsNoTracking()
            .Select(e => new SearchAthletesFilter.CupColorLegInput(e.SaveAthleteId, e.SourceSeasonId, e.SportingColor))
            .ToListAsync(cancellationToken);
    }

    private static Task<List<SearchAthletesFilter.CupColorTeamInput>> LoadColorTeamsAsync(
        SaveDbContext context, CancellationToken cancellationToken)
    {
        return context.ColorCupTeamStandings
            .AsNoTracking()
            .Select(e => new SearchAthletesFilter.CupColorTeamInput(e.SourceSeasonId, e.SportingColor, e.TeamRank))
            .ToListAsync(cancellationToken);
    }

    private static Task<List<SearchAthletesFilter.CupTypeLegInput>> LoadTypeLegsAsync(
        SaveDbContext context, CancellationToken cancellationToken)
    {
        return context.TypeCupTeamGroupStandings
            .AsNoTracking()
            .Select(e => new SearchAthletesFilter.CupTypeLegInput(e.SaveAthleteId, e.SourceSeasonId, e.CreatureType))
            .ToListAsync(cancellationToken);
    }

    private static Task<List<SearchAthletesFilter.CupTypeTeamInput>> LoadTypeTeamsAsync(
        SaveDbContext context, CancellationToken cancellationToken)
    {
        return context.TypeCupTeamStandings
            .AsNoTracking()
            .Select(e => new SearchAthletesFilter.CupTypeTeamInput(e.SourceSeasonId, e.CreatureType, e.TeamRank))
            .ToListAsync(cancellationToken);
    }

    internal static Task<Dictionary<int, (bool EverSuper, int SuperSeasons)>> LoadSuperTenureAsync(
        SaveDbContext context,
        CancellationToken cancellationToken)
    {
        return LoadSuperTenureInnerAsync(context, cancellationToken);
    }

    private static async Task<Dictionary<int, (bool EverSuper, int SuperSeasons)>> LoadSuperTenureInnerAsync(
        SaveDbContext context,
        CancellationToken cancellationToken)
    {
        const int superleagueKind = 1;
        List<SummaryProbe> summaries = await context.AthleteSeasonSummaries
            .AsNoTracking()
            .Where(e => e.WasActive)
            .Select(e => new SummaryProbe(e.SaveAthleteId, e.LeagueKind))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (summaries.Count > 0)
        {
            return AggregateSuperSummaries(summaries, superleagueKind);
        }

        return await LoadSuperTenureFallbackAsync(context, superleagueKind, cancellationToken).ConfigureAwait(false);
    }

    private static Dictionary<int, (bool EverSuper, int SuperSeasons)> AggregateSuperSummaries(
        IReadOnlyList<SummaryProbe> summaries, int superleagueKind)
    {
        Dictionary<int, (bool EverSuper, int SuperSeasons)> tenure = [];
        foreach (SummaryProbe row in summaries)
        {
            tenure.TryGetValue(row.SaveAthleteId, out (bool EverSuper, int SuperSeasons) existing);
            bool isSuper = row.LeagueKind == superleagueKind;
            tenure[row.SaveAthleteId] = (existing.EverSuper || isSuper, checked(existing.SuperSeasons + (isSuper ? 1 : 0)));
        }

        return tenure;
    }

    private static async Task<Dictionary<int, (bool EverSuper, int SuperSeasons)>> LoadSuperTenureFallbackAsync(
        SaveDbContext context, int superleagueKind, CancellationToken cancellationToken)
    {
        // Fallback for saves without summary projections: memberships joined to league kinds.
        Dictionary<int, int> leagueKinds = await context.Leagues
            .AsNoTracking()
            .ToDictionaryAsync(e => e.Id, e => e.Kind, cancellationToken)
            .ConfigureAwait(false);
        List<MembershipKindProbe> memberships = await context.SeasonMemberships
            .AsNoTracking()
            .Where(e => e.LeagueId != null)
            .Select(e => new MembershipKindProbe(e.SaveAthleteId, e.LeagueId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        Dictionary<int, (bool EverSuper, int SuperSeasons)> fallback = [];
        foreach (MembershipKindProbe row in memberships)
        {
            if (row.LeagueId is null)
            {
                continue;
            }

            if (!leagueKinds.TryGetValue(row.LeagueId.Value, out int kind))
            {
                throw new InvalidOperationException($"Membership references unknown league {row.LeagueId.Value}.");
            }

            fallback.TryGetValue(row.SaveAthleteId, out (bool EverSuper, int SuperSeasons) existing);
            bool isSuper = kind == superleagueKind;
            fallback[row.SaveAthleteId] = (existing.EverSuper || isSuper, checked(existing.SuperSeasons + (isSuper ? 1 : 0)));
        }

        return fallback;
    }

    private sealed record AthleteProbe(
        int Id, string Name, int SportingColor, string CreatureTypesJson, string TypeLine, string? ImageUrl);

    private sealed record CareerProbe(
        int SaveAthleteId, int SeasonsActive, bool IsActive, string? CurrentLeagueName, int? CurrentLeagueKind,
        int? BestSeasonFinish, int? BestSeasonNumber);

    private sealed record MembershipProbe(int SaveAthleteId, int SeasonId);

    private sealed record MembershipKindProbe(int SaveAthleteId, int? LeagueId);

    private sealed record SummaryProbe(int SaveAthleteId, int? LeagueKind);
}
