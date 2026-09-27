namespace MtgSoloSports.Persistence.Saves;

/// <summary>
/// Thrown when an import would overwrite an existing save without explicit
/// opt-in. The existing save is left untouched.
/// </summary>
public sealed class SaveAlreadyExistsException : InvalidOperationException
{
    public SaveAlreadyExistsException(Guid saveId)
        : base($"Save '{saveId:D}' already exists. Import without overwrite is rejected; pass overwrite explicitly to replace it after a verified checkpoint.")
    {
        SaveId = saveId;
    }

    public Guid SaveId { get; }
}
