namespace MtgSoloSports.Features.Leagues.SeasonProgress;

/// <summary>
/// One league's progress within the current global stage.
/// Feeder division and league level identify the tier explicitly without
/// parsing league names.
/// </summary>
public sealed record SeasonProgressLeague(
    int LeagueId,
    string LeagueName,
    string LeagueKind,
    int FeederDivision,
    string LeagueLevel,
    int? CurrentStage,
    int CompletedStages,
    bool IsLeagueComplete);
