using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Reports.GoogleDrivePublishing;

public static class PutDriveDestinationEndpoint
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapPut("/api/saves/{saveId:guid}/reports/drive/destination", async (
            Guid saveId,
            PutDriveDestinationRequest request,
            PutDriveDestinationHandler handler,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(handler);
            try
            {
                ArgumentNullException.ThrowIfNull(request);
                DriveDestinationResponse response = await handler.HandleAsync(saveId, request, cancellationToken).ConfigureAwait(false);
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
            catch (DriveConflictException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        });
    }
}
