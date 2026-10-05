namespace MtgSoloSports.Features.History.GetSeasonPlacements;

/// <summary>
/// Immutable presentation DTO for the stage-by-stage placement matrix.
/// Built from normalized <c>StageStanding</c> rows (authoritative history)
/// plus the season's <c>SeasonMembership</c> roster. Never selects round
/// payloads and never decompresses history.
/// </summary>
public sealed record GetSeasonPlacementsResponse(
    Guid SaveId,
    int SeasonNumber,
    int LeagueId,
    string LeagueName,
    string LeagueKind,
    int FeederDivision,
    string LeagueLevel,
    bool IsSeasonComplete,
    int CompletedStages,
    IReadOnlyList<SeasonPlacementAthlete> Athletes,
    IReadOnlyList<SeasonPlacementCell> Placements);
