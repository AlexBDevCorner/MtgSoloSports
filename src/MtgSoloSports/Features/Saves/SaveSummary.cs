namespace MtgSoloSports.Features.Saves;

/// <summary>
/// Feature-local save listing contract shared by the Saves slices.
/// </summary>
public sealed record SaveSummary(
    Guid SaveId,
    string Name,
    DateTimeOffset CreatedUtc,
    int SchemaVersion,
    int CurrentSeason,
    string Phase);
