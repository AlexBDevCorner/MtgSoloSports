namespace MtgSoloSports.Persistence.Saves;

/// <summary>
/// File-system location for per-save SQLite databases.
/// Each save is one <c>{saveId:N}.db</c> file inside <see cref="SavesRoot"/>.
/// There is no shared sporting-history database.
/// </summary>
public sealed class SaveStorageOptions
{
    public const string SectionName = "Saves";

    /// <summary>
    /// Absolute path, or a path relative to the host content root.
    /// Defaults to a local <c>saves</c> directory.
    /// </summary>
    public string SavesRoot { get; set; } = "saves";
}
