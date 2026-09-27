namespace MtgSoloSports.Features.Saves.ExportSave;

/// <summary>
/// Portable export artifact descriptor. The ZIP bytes embed the SQLite save
/// database plus manifest/version metadata for validated import elsewhere.
/// </summary>
public sealed record ExportSaveResponse(
    Guid SaveId,
    string FileName,
    string ContentType,
    byte[] ZipBytes,
    int FormatVersion,
    int SchemaVersion,
    int RulesVersion,
    string DatabaseSha256,
    DateTimeOffset ExportedUtc);
