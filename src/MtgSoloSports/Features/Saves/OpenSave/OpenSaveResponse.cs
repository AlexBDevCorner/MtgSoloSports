namespace MtgSoloSports.Features.Saves.OpenSave;

public sealed record OpenSaveResponse(
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
