using MtgSoloSports.Features.Reports.SeasonDossier;
using MtgSoloSports.Features.Reports.UniverseAlmanac;
using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Reports.GoogleDrivePublishing;

public static class PublishDriveReportsEndpoint
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapPost("/api/saves/{saveId:guid}/reports/drive/publish", async (
            Guid saveId,
            PublishDriveReportsRequest? request,
            PublishDriveReportsHandler handler,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(handler);
            try
            {
                PublishDriveReportsResponse response = await handler
                    .HandleAsync(saveId, request ?? new PublishDriveReportsRequest(false, false), cancellationToken)
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
            catch (SeasonDossierNotFoundException)
            {
                return Results.NotFound();
            }
            catch (UniverseAlmanacNotFoundException)
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
        });
    }
}
