using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Saves.ExportSave;

public static class ExportSaveEndpoint
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/api/saves/{saveId:guid}/export", async (
            Guid saveId,
            ExportSaveHandler handler,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(handler);
            try
            {
                ExportSaveResponse response = await handler.HandleAsync(saveId, cancellationToken).ConfigureAwait(false);
                return Results.File(response.ZipBytes, response.ContentType, response.FileName);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
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
