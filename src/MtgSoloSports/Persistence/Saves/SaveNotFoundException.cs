namespace MtgSoloSports.Persistence.Saves;

/// <summary>
/// Thrown when a save id has no database file in the saves root.
/// Endpoints map this to HTTP 404.
/// </summary>
public sealed class SaveNotFoundException : Exception
{
    public SaveNotFoundException(Guid saveId)
        : base($"Save '{saveId:D}' was not found.")
    {
        SaveId = saveId;
    }

    public Guid SaveId { get; }
}
