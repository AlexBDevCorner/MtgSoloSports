namespace MtgSoloSports.Features.Saves.CreateSave;

/// <summary>
/// Save-creation result including the deterministic universe summary for UI
/// display. Per-color counts are keyed by sporting-color name; the checksum
/// fingerprints the selected 2,048-athlete set.
/// </summary>
public sealed record CreateSaveResponse(
    Guid SaveId,
    string Name,
    DateTimeOffset CreatedUtc,
    int SchemaVersion,
    int CurrentSeason,
    string Phase,
    string RngAlgorithm,
    int RngVersion,
    string RngState,
    string RngStream,
    int RulesVersion,
    int TotalAthletes,
    IReadOnlyDictionary<string, int> AthletesPerColor,
    string UniverseChecksum);
