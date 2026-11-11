namespace MtgSoloSports.Features.Leagues.SeasonProgress;

/// <summary>
/// One league's progress within the current global stage.
/// Feeder division and league level identify the tier explicitly without
/// parsing league names. <c>SportingColor</c> (MSS-139 additive) carries the
/// persisted sporting color so the frontend can build the stable league key
/// without a second request; older consumers ignore it.
/// </summary>
public sealed record SeasonProgressLeague(
    int LeagueId,
    string LeagueName,
    string LeagueKind,
    int FeederDivision,
    string LeagueLevel,
    int? CurrentStage,
    int CompletedStages,
    bool IsLeagueComplete,
    int SportingColor = 0);
