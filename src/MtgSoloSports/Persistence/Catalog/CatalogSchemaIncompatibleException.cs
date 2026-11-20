namespace MtgSoloSports.Persistence.Catalog;

/// <summary>
/// The catalog database's schema cannot be served or migrated by this
/// application: it was created by a newer application version, or its
/// pre-migration shape no longer matches what this version can baseline.
/// This is a server-state failure, never a client error: it maps to HTTP 500
/// with an actionable message, and no caller may swallow or downgrade it.
/// Deliberately does not derive from <see cref="InvalidOperationException"/>
/// so an un-audited <c>catch (InvalidOperationException)</c> filter can never
/// silently absorb the failure. The catalog is fully re-importable (Scryfall
/// import) and saves keep their own snapshots, so the documented remedy is to
/// delete the catalog file and import again — never a silent auto-recreate.
/// </summary>
public sealed class CatalogSchemaIncompatibleException : Exception
{
    public CatalogSchemaIncompatibleException(string message)
        : base(message)
    {
    }

    public CatalogSchemaIncompatibleException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
