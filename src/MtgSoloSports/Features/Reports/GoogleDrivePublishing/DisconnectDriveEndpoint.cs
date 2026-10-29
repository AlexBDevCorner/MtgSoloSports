using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Reports.GoogleDrivePublishing;

public static class DisconnectDriveEndpoint
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapPost("/api/saves/{saveId:guid}/reports/drive/disconnect", async (
            Guid saveId,
            DisconnectDriveHandler handler,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(handler);
            ArgumentNullException.ThrowIfNull(httpContext);
            IResult? blocked = DriveCsrfGuard.BlockIfUnsafe(httpContext.Request);
            if (blocked is not null)
            {
                return blocked;
            }
            try
            {
                await handler.HandleAsync(saveId, cancellationToken).ConfigureAwait(false);
                return Results.Ok(new { connected = false });
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
