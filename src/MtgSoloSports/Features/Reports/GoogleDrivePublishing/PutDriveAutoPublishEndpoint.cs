using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Reports.GoogleDrivePublishing;

public static class PutDriveAutoPublishEndpoint
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapPut("/api/saves/{saveId:guid}/reports/drive/auto-publish", HandleAsync);
    }

    private static async Task<IResult> HandleAsync(
        Guid saveId,
        PutDriveAutoPublishRequest? request,
        PutDriveAutoPublishHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
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
            DriveDestinationResponse response = await handler
                .HandleAsync(saveId, request ?? new PutDriveAutoPublishRequest(false), cancellationToken)
                .ConfigureAwait(false);
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
    }
}
