namespace MtgSoloSports.Features.Cups.SelectTypeCupTeams;

/// <summary>
/// Persisted Type Cup team allocation for one completed even source season:
/// one four-athlete team per participating creature type, ordered #1..#4.
/// There is no artificial team limit; types that cannot field four distinct
/// currently active athletes do not participate. Persisting this allocation
/// never sets permanent nationality; nationality becomes permanent only when
/// an athlete actually participates (a later slice).
/// </summary>
public sealed record SelectTypeCupTeamsResponse(
    Guid SaveId,
    int SourceSeasonNumber,
    int SourceSeasonId,
    int RulesVersion,
    int TotalSelected,
    int TeamCount,
    int CupBonusWeightPermille,
    int CupPerformanceWeightPermille,
    int CupFormWeightPermille,
    int CupPrestigeWeightPermille,
    IReadOnlyList<TypeCupTeamResult> Teams);
