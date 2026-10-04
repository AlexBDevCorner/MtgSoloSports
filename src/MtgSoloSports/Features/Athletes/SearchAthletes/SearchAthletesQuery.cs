namespace MtgSoloSports.Features.Athletes.SearchAthletes;

/// <summary>
/// Immutable search request for the athlete browse page. All filters compose
/// with AND semantics across categories; multi-value filters within one
/// category use OR semantics. Historical aggregates (non-pool seasons,
/// honours, titles, Superleague tenure, Cup presence) are derived from
/// authoritative persisted history, never from current status alone.
/// </summary>
public sealed record SearchAthletesQuery(
    string? Search,
    int? MinNonPoolSeasons,
    int? MaxNonPoolSeasons,
    IReadOnlyList<int>? SportingColors,
    IReadOnlyList<string>? CreatureTypes,
    IReadOnlyList<string>? CurrentLeagues,
    int? MinHonours,
    int? MaxHonours,
    int? MinTitles,
    string? HasTitle,
    string? HighestLeague,
    int? BestFinishMax,
    int? MinSuperSeasons,
    int? MaxSuperSeasons,
    string? CupStatus,
    string? Sort,
    string? Dir,
    int? Skip,
    int? Take)
{
    public const int DefaultTake = 100;

    public const int MaxTake = 500;

    public static SearchAthletesQuery Empty { get; } = new(
        null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null);

    /// <summary>
    /// Validates raw query values. Throws <see cref="ArgumentException"/> on
    /// invalid values so the endpoint returns 400, never silent substitution.
    /// </summary>
    public void Validate()
    {
        Validate(this);
    }

    public static void Validate(SearchAthletesQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidateRanges(query);
        ValidateLists(query);
        ValidateEnums(query);
        ValidatePaging(query);
    }

    private static void ValidateRanges(SearchAthletesQuery query)
    {
        if (query.MinNonPoolSeasons is < 0)
        {
            throw new ArgumentException("minNonPool must be >= 0.", nameof(query));
        }

        if (query.MaxNonPoolSeasons is < 0)
        {
            throw new ArgumentException("maxNonPool must be >= 0.", nameof(query));
        }

        if (query.MinNonPoolSeasons is not null && query.MaxNonPoolSeasons is not null && query.MinNonPoolSeasons > query.MaxNonPoolSeasons)
        {
            throw new ArgumentException("minNonPool must not exceed maxNonPool.", nameof(query));
        }

        if (query.MinHonours is < 0)
        {
            throw new ArgumentException("minHonours must be >= 0.", nameof(query));
        }

        if (query.MaxHonours is < 0)
        {
            throw new ArgumentException("maxHonours must be >= 0.", nameof(query));
        }

        if (query.MinHonours is not null && query.MaxHonours is not null && query.MinHonours > query.MaxHonours)
        {
            throw new ArgumentException("minHonours must not exceed maxHonours.", nameof(query));
        }

        if (query.MinTitles is < 0)
        {
            throw new ArgumentException("minTitles must be >= 0.", nameof(query));
        }

        if (query.BestFinishMax is < 1 or > 32)
        {
            throw new ArgumentException("bestFinishMax must be 1..32.", nameof(query));
        }

        if (query.MinSuperSeasons is < 0)
        {
            throw new ArgumentException("minSuperSeasons must be >= 0.", nameof(query));
        }

        if (query.MaxSuperSeasons is < 0)
        {
            throw new ArgumentException("maxSuperSeasons must be >= 0.", nameof(query));
        }

        if (query.MinSuperSeasons is not null && query.MaxSuperSeasons is not null && query.MinSuperSeasons > query.MaxSuperSeasons)
        {
            throw new ArgumentException("minSuperSeasons must not exceed maxSuperSeasons.", nameof(query));
        }
    }

    private static void ValidateLists(SearchAthletesQuery query)
    {
        if (query.SportingColors is not null)
        {
            foreach (int color in query.SportingColors)
            {
                if (color is < 0 or > 7)
                {
                    throw new ArgumentException($"Unknown sporting color '{color}'.", nameof(query));
                }
            }
        }

        if (query.CreatureTypes is not null)
        {
            foreach (string type in query.CreatureTypes)
            {
                if (string.IsNullOrWhiteSpace(type))
                {
                    throw new ArgumentException("Creature type filters must not be empty.", nameof(query));
                }
            }
        }

        if (query.CurrentLeagues is not null)
        {
            foreach (string league in query.CurrentLeagues)
            {
                if (string.IsNullOrWhiteSpace(league))
                {
                    throw new ArgumentException("Current league filters must not be empty.", nameof(query));
                }
            }
        }
    }

    private static void ValidateEnums(SearchAthletesQuery query)
    {
        if (query.HasTitle is not null && query.HasTitle is not ("only" or "none"))
        {
            throw new ArgumentException("hasTitle must be 'only' or 'none'.", nameof(query));
        }

        if (query.HighestLeague is not null && query.HighestLeague is not ("superleague" or "feeder" or "none"))
        {
            throw new ArgumentException("highest must be 'superleague', 'feeder' or 'none'.", nameof(query));
        }

        if (query.CupStatus is not null && query.CupStatus is not ("participant" or "podium" or "title" or "none"))
        {
            throw new ArgumentException("cup must be 'participant', 'podium', 'title' or 'none'.", nameof(query));
        }

        if (query.Sort is not null && query.Sort is not ("name" or "nonPool" or "honours" or "titles" or "bestFinish" or "currentLeague" or "superSeasons"))
        {
            throw new ArgumentException("sort must be a supported sort key.", nameof(query));
        }

        if (query.Dir is not null && query.Dir is not ("asc" or "desc"))
        {
            throw new ArgumentException("dir must be 'asc' or 'desc'.", nameof(query));
        }
    }

    private static void ValidatePaging(SearchAthletesQuery query)
    {
        if (query.Skip is < 0)
        {
            throw new ArgumentException("skip must be >= 0.", nameof(query));
        }

        if (query.Take is <= 0 or > MaxTake)
        {
            throw new ArgumentException($"take must be 1..{MaxTake}.", nameof(query));
        }
    }

    public int EffectiveSkip => Skip ?? 0;

    public int EffectiveTake => Take ?? DefaultTake;

    public (string Sort, string Dir) EffectiveSort()
    {
        string sort = Sort ?? "name";
        string dir = Dir ?? (sort is "name" or "bestFinish" or "currentLeague" ? "asc" : "desc");
        return (sort, dir);
    }
}
