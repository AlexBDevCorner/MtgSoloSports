namespace MtgSoloSports.Features.Cups.SelectColorCupTeams;

/// <summary>
/// Persisted Color Cup team selection for one completed odd source season:
/// exactly four athletes per sporting color (8 x 4 = 32) ordered #1..#4.
/// </summary>
public sealed record SelectColorCupTeamsResponse(
    Guid SaveId,
    int SourceSeasonNumber,
    int SourceSeasonId,
    int RulesVersion,
    int TotalSelected,
    int CupBonusWeightPermille,
    int CupPerformanceWeightPermille,
    int CupFormWeightPermille,
    int CupPrestigeWeightPermille,
    IReadOnlyList<ColorCupTeamResult> Teams);
