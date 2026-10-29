using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Reports.UniverseAlmanac;

public static class GetUniverseAlmanacEndpoint
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/api/saves/{saveId:guid}/reports/almanac", async (
            Guid saveId,
            string? format,
            GetUniverseAlmanacHandler handler,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(handler);
            try
            {
                GetUniverseAlmanacHandler.ReportFile file = await handler.HandleAsync(saveId, format, cancellationToken).ConfigureAwait(false);
                return Results.File(file.Content, file.ContentType, file.FileName);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (SaveNotFoundException)
            {
                return Results.NotFound();
            }
            catch (UniverseAlmanacNotFoundException)
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
