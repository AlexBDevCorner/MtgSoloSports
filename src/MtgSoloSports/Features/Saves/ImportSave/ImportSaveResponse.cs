namespace MtgSoloSports.Features.Saves.ImportSave;

/// <summary>
/// Validated import result. Mirrors the open-save detail shape so imported
/// universes behave exactly like locally created ones.
/// </summary>
public sealed record ImportSaveResponse(
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
    int RulesVersion);
