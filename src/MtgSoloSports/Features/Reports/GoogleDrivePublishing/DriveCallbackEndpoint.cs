namespace MtgSoloSports.Features.Reports.GoogleDrivePublishing;

public static class DriveCallbackEndpoint
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/api/reports/drive/callback", async (
            string? code,
            string? state,
            string? error,
            DriveCallbackHandler handler,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(handler);
            try
            {
                DriveCallbackHandler.CallbackResult result =
                    await handler.HandleAsync(code, state, error, cancellationToken).ConfigureAwait(false);
                return Results.Redirect(result.RedirectTo, preserveMethod: false);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (DriveNotConfiguredException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch (DriveNotConnectedException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (DriveUpstreamException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
        });
    }
}
