namespace MtgSoloSports.Features.Saves;

/// <summary>
/// Feature-local save listing contract shared by the Saves slices.
/// <see cref="Unreadable"/> marks a damaged placeholder for a save file that
/// exists on disk but could not be read; existing fields keep their shape.
/// </summary>
public sealed record SaveSummary(
    Guid SaveId,
    string Name,
    DateTimeOffset CreatedUtc,
    int SchemaVersion,
    int CurrentSeason,
    string Phase,
    bool Unreadable = false,
    string? UnreadableReason = null);
