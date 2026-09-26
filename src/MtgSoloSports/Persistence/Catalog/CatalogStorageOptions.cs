namespace MtgSoloSports.Persistence.Catalog;

/// <summary>
/// File-system location for the shared catalog SQLite database.
/// The catalog database is deliberately separate from per-save databases.
/// </summary>
public sealed class CatalogStorageOptions
{
    public const string SectionName = "Catalog";

    /// <summary>
    /// Absolute path, or a path relative to the host content root.
    /// Defaults to a local <c>catalog/catalog.db</c> file.
    /// </summary>
    public string CatalogPath { get; set; } = "catalog/catalog.db";
}
