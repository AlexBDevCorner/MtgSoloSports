using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Saves.ImportSave;

public static class ImportSaveEndpoint
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        // Note: the bundle body is capped by SaveBundle.MaxBundleBytes at the
        // application level; the host's own max-request-body limit still
        // applies underneath (Kestrel default 30 MB) for non-TestServer hosts.
        app.MapPost("/api/saves/import", async (
            HttpRequest httpRequest,
            ImportSaveHandler handler,
            bool overwrite = false,
            CancellationToken cancellationToken = default) =>
        {
            ArgumentNullException.ThrowIfNull(httpRequest);
            ArgumentNullException.ThrowIfNull(handler);
            try
            {
                byte[] bundleBytes = await SaveBundle.ReadCappedAsync(httpRequest.Body, cancellationToken).ConfigureAwait(false);
                ImportSaveResponse response = await handler.HandleAsync(bundleBytes, overwrite, cancellationToken).ConfigureAwait(false);
                return Results.Created($"/api/saves/{response.SaveId:D}", response);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (SaveAlreadyExistsException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
            catch (SaveNotFoundException)
            {
                return Results.NotFound();
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }
}
