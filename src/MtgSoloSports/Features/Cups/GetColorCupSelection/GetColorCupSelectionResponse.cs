namespace MtgSoloSports.Features.Cups.GetColorCupSelection;

/// <summary>
/// Persisted Color Cup team selection read model (never resimulates).
/// </summary>
public sealed record GetColorCupSelectionResponse(
    Guid SaveId,
    int SourceSeasonNumber,
    int SourceSeasonId,
    int RulesVersion,
    int TotalSelected,
    IReadOnlyList<GetColorCupTeam> Teams);
