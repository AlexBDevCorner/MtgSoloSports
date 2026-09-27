namespace MtgSoloSports.Features.Cups.GetTypeCupSelection;

/// <summary>
/// Persisted Type Cup team allocation for one completed even source season.
/// </summary>
public sealed record GetTypeCupSelectionResponse(
    Guid SaveId,
    int SourceSeasonNumber,
    int SourceSeasonId,
    int RulesVersion,
    int TotalSelected,
    int TeamCount,
    IReadOnlyList<GetTypeCupTeam> Teams);
