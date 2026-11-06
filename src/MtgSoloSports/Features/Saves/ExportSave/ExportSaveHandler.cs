using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Saves.ExportSave;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Exports one save as a
/// portable ZIP artifact (SQLite database plus manifest/version metadata).
/// </summary>
public sealed class ExportSaveHandler
{
    private readonly SaveStore _store;

    public ExportSaveHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<ExportSaveResponse> HandleAsync(Guid saveId, CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        SaveStore.ExportRecord exported = await _store.ExportAsync(saveId, cancellationToken).ConfigureAwait(false);
        return new ExportSaveResponse(
            exported.SaveId,
            exported.FileName,
            exported.ContentType,
            exported.ZipBytes,
            exported.Manifest.FormatVersion,
            exported.Manifest.SchemaVersion,
            exported.Manifest.RulesVersion,
            exported.Manifest.DatabaseSha256,
            exported.Manifest.ExportedUtc);
    }

    /// <summary>
    /// File-streaming export for the production download path (MSS-136).
    /// Writes the ZIP directly to <paramref name="destinationPath"/> with
    /// bounded buffers and no fixed size ceiling.
    /// </summary>
    public async Task<SaveStore.ExportFileRecord> HandleFileAsync(
        Guid saveId,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        return await _store.ExportToFileAsync(saveId, destinationPath, cancellationToken).ConfigureAwait(false);
    }
}
