using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Reports.GoogleDrivePublishing;

public static class DriveLibrarySyncEndpoint
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapPost("/api/saves/{saveId:guid}/reports/drive/library/preview", HandlePreviewAsync);
        app.MapPost("/api/saves/{saveId:guid}/reports/drive/library/sync", HandleSyncAsync);
    }

    private static async Task<IResult> HandlePreviewAsync(
        Guid saveId,
        DriveLibrarySyncRequest? request,
        DriveLibrarySyncHandler handler,
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

        DriveLibrarySyncRequest preview = (request ?? new DriveLibrarySyncRequest(false, false, false)) with { DryRun = true };
        return await HandleAsync(saveId, preview, handler, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IResult> HandleSyncAsync(
        Guid saveId,
        DriveLibrarySyncRequest? request,
        DriveLibrarySyncHandler handler,
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

        return await HandleAsync(
            saveId,
            request ?? new DriveLibrarySyncRequest(false, false, false),
            handler,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IResult> HandleAsync(
        Guid saveId,
        DriveLibrarySyncRequest request,
        DriveLibrarySyncHandler handler,
        CancellationToken cancellationToken)
    {
        try
        {
            DriveLibrarySyncResponse response = await handler
                .HandleAsync(saveId, request, cancellationToken)
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
        catch (DriveNotConfiguredException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (DriveNotConnectedException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status401Unauthorized);
        }
        catch (DriveConflictException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
        catch (DriveUpstreamException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
        }
        catch (DriveApiException ex)
        {
            return ex.StatusCode == System.Net.HttpStatusCode.Unauthorized
                ? Results.Json(new { error = "Google authorization expired or was revoked. Reconnect with Connect Google Drive." }, statusCode: StatusCodes.Status401Unauthorized)
                : Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }
}
