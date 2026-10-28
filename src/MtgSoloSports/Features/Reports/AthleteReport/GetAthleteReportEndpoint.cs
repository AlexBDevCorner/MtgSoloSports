using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Reports.AthleteReport;

public static class GetAthleteReportEndpoint
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/api/saves/{saveId:guid}/athletes/{athleteId:int}/report", async (
            Guid saveId,
            int athleteId,
            string? format,
            GetAthleteReportHandler handler,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(handler);
            try
            {
                GetAthleteReportHandler.ReportFile file = await handler.HandleAsync(saveId, athleteId, format, cancellationToken).ConfigureAwait(false);
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
            catch (Athletes.GetProfile.AthleteProfileNotFoundException)
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
