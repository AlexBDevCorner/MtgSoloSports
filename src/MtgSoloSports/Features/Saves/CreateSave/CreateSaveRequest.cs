namespace MtgSoloSports.Features.Saves.CreateSave;

/// <summary>
/// Creates a new independent save universe (one SQLite file).
/// Seed/stream are optional; when omitted the server draws fresh entropy from
/// the OS cryptographic generator. Tests pass explicit values for determinism.
/// </summary>
public sealed record CreateSaveRequest(string Name, ulong? Seed, ulong? Stream);
