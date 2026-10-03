namespace MtgSoloSports.Features.Athletes.SearchAthletes;

/// <summary>
/// Filter-option payload for the browse page controls. Counts are per-option
/// athlete counts in the current save.
/// </summary>
public sealed record SearchAthletesOptionsResponse(
    Guid SaveId,
    IReadOnlyList<AthleteSearchColourOption> Colours,
    IReadOnlyList<AthleteSearchTypeOption> CreatureTypes,
    IReadOnlyList<AthleteSearchLeagueOption> CurrentLeagues,
    int MaxNonPoolSeasons,
    int MaxHonours,
    int MaxTitles,
    bool HasSuperleague,
    bool HasCups);
