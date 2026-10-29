using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Reports.GoogleDrivePublishing;

public static class GetDriveStatusEndpoint
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/api/saves/{saveId:guid}/reports/drive/status", async (
            Guid saveId,
            GetDriveStatusHandler handler,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(handler);
            try
            {
                DriveStatusResponse response = await handler.HandleAsync(saveId, cancellationToken).ConfigureAwait(false);
                return Results.Ok(response);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (SaveNotFoundException)
            {
                return Results.NotFound();
            }
        });
    }
}
