using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Saves.ExportSave;

public static class ExportSaveEndpoint
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        // MSS-136: streams the ZIP from a temporary file with bounded buffers
        // so large saves are limited only by disk space, not by whole-artifact
        // byte arrays or a fixed size ceiling. The temp file is opened with
        // DeleteOnClose so it is removed when the response stream is disposed,
        // on success, failure, or client cancellation.
        app.MapGet("/api/saves/{saveId:guid}/export", async (
            Guid saveId,
            ExportSaveHandler handler,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(handler);
            string tempBundlePath = Path.Combine(
                Path.GetTempPath(),
                $".mtgsolosports-export-{saveId:N}-{Guid.NewGuid():N}.tmp");
            try
            {
                SaveStore.ExportFileRecord exported = await handler
                    .HandleFileAsync(saveId, tempBundlePath, cancellationToken)
                    .ConfigureAwait(false);
                FileStream stream = new(
                    exported.BundlePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    81920,
                    FileOptions.DeleteOnClose | FileOptions.SequentialScan);
                return Results.File(stream, exported.ContentType, exported.FileName);
            }
            catch (ArgumentException ex)
            {
                DeleteFileBestEffort(tempBundlePath);
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (SaveNotFoundException)
            {
                DeleteFileBestEffort(tempBundlePath);
                return Results.NotFound();
            }
            catch (InvalidOperationException ex)
            {
                DeleteFileBestEffort(tempBundlePath);
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (OperationCanceledException)
            {
                DeleteFileBestEffort(tempBundlePath);
                throw;
            }
        });
    }

    private static void DeleteFileBestEffort(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
