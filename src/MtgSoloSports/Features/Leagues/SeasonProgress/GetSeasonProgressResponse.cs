namespace MtgSoloSports.Features.Leagues.SeasonProgress;

/// <summary>
/// Immutable presentation DTO tracking the current global stage: the minimum
/// incomplete stage across active leagues. Stage N+1 cannot begin until
/// Stage N is complete everywhere; leagues within the stage complete one by
/// one. <see cref="GlobalStage"/> is 33 when the season is complete.
/// </summary>
public sealed record GetSeasonProgressResponse(
    Guid SaveId,
    int SeasonNumber,
    int GlobalStage,
    bool IsSeasonComplete,
    IReadOnlyList<SeasonProgressLeague> Leagues);
