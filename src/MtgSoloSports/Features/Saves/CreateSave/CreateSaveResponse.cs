namespace MtgSoloSports.Features.Saves.CreateSave;

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
    int RulesVersion);
