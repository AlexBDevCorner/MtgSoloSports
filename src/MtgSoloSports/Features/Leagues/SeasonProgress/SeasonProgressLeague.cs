namespace MtgSoloSports.Features.Leagues.SeasonProgress;

/// <summary>
/// One league's progress within the current global stage.
/// </summary>
public sealed record SeasonProgressLeague(
    int LeagueId,
    string LeagueName,
    string LeagueKind,
    int? CurrentStage,
    int CompletedStages,
    bool IsLeagueComplete);
