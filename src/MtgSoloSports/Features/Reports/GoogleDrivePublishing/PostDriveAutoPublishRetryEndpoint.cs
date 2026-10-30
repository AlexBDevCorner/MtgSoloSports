using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Reports.GoogleDrivePublishing;

public static class PostDriveAutoPublishRetryEndpoint
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapPost("/api/saves/{saveId:guid}/reports/drive/auto-publish/retry", HandleAsync);
    }

    private static async Task<IResult> HandleAsync(
        Guid saveId,
        PostDriveAutoPublishRetryHandler handler,
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
                .HandleAsync(saveId, cancellationToken)
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
        catch (DriveNotConnectedException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status401Unauthorized);
        }
        catch (DriveConflictException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
    }
}
