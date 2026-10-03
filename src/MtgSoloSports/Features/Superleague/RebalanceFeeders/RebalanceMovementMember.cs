namespace MtgSoloSports.Features.Superleague.RebalanceFeeders;

/// <summary>
/// One rebalance movement for inspection: persisted pool transfers
/// (<c>RebalanceDraw</c>/<c>RebalanceDisplacement</c>) plus derived
/// Superleague transfers (<c>SuperleagueDeparture</c> feeder to Superleague and
/// <c>SuperleagueReturn</c> Superleague to feeder) resolved from persisted
/// source/next memberships and source standings. Presentation only; sporting
/// decisions are never recomputed here.
/// ImageUrl carries the existing card artwork (null when unavailable) so the
/// rebalance reveal renders recognizable tiles.
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
    int FromSeasonRank,
    string? ImageUrl = null);
