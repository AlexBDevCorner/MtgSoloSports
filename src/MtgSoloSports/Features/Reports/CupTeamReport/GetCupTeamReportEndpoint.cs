using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Reports.CupTeamReport;

public static class GetCupTeamReportEndpoint
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/api/saves/{saveId:guid}/cups/{cup}/teams/{teamKey}/report", async (
            Guid saveId,
            string cup,
            string teamKey,
            string? format,
            GetCupTeamReportHandler handler,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(handler);
            try
            {
                GetCupTeamReportHandler.ReportFile file = await handler.HandleAsync(saveId, cup, teamKey, format, cancellationToken).ConfigureAwait(false);
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
            catch (CupTeamReportNotFoundException)
            {
                return Results.NotFound();
            }
            catch (Cups.CupHistory.CupTeamHistoryNotFoundException)
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
