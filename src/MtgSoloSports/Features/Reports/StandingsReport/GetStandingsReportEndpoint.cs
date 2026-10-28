using MtgSoloSports.Features.History;
using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Reports.StandingsReport;

public static class GetStandingsReportEndpoint
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/api/saves/{saveId:guid}/leagues/{leagueId:int}/seasons/{seasonNumber:int}/report", async (
            Guid saveId,
            int leagueId,
            int seasonNumber,
            string? format,
            GetStandingsReportHandler handler,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(handler);
            try
            {
                GetStandingsReportHandler.ReportFile file = await handler.HandleAsync(saveId, leagueId, seasonNumber, format, cancellationToken).ConfigureAwait(false);
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
            catch (HistoryNotFoundException)
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
