using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Reports.GoogleDrivePublishing;

public static class GetDriveAuthUrlEndpoint
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/api/saves/{saveId:guid}/reports/drive/auth-url", async (
            Guid saveId,
            GetDriveAuthUrlHandler handler,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(handler);
            try
            {
                GetDriveAuthUrlHandler.AuthUrlResult result = await handler.HandleAsync(saveId, cancellationToken).ConfigureAwait(false);
                return Results.Ok(new { authUrl = result.AuthUrl });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (SaveNotFoundException)
            {
                return Results.NotFound();
            }
            catch (DriveNotConfiguredException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });
    }
}
