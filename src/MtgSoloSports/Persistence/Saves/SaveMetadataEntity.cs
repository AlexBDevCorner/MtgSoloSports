namespace MtgSoloSports.Persistence.Saves;

/// <summary>
/// Single-row table (Id always 1) identifying a save universe.
/// </summary>
public sealed class SaveMetadataEntity
{
    public int Id { get; set; }

    public Guid SaveId { get; set; }

    public string Name { get; set; } = string.Empty;

    public DateTimeOffset CreatedUtc { get; set; }

    public int SchemaVersion { get; set; }

    public int CurrentSeason { get; set; }

    public string Phase { get; set; } = string.Empty;
}
