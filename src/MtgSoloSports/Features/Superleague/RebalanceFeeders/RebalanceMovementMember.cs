namespace MtgSoloSports.Features.Superleague.RebalanceFeeders;

/// <summary>
/// One persisted pool-transfer movement for inspection.
/// </summary>
public sealed record RebalanceMovementMember(
    int AthleteId,
    string Name,
    string SportingColor,
    int FromLeagueId,
    string FromLeagueName,
    int ToLeagueId,
    string ToLeagueName,
    string Kind,
    int FromSeasonRank);
