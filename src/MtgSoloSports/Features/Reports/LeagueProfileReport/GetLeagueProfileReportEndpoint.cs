using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Reports.LeagueProfileReport;

public static class GetLeagueProfileReportEndpoint
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/api/saves/{saveId:guid}/leagues/{leagueKey}/report", async (
            Guid saveId,
            string leagueKey,
            string? format,
            GetLeagueProfileReportHandler handler,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(handler);
            try
            {
                GetLeagueProfileReportHandler.ReportFile file = await handler.HandleAsync(saveId, leagueKey, format, cancellationToken).ConfigureAwait(false);
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
            catch (LeagueProfileReportNotFoundException)
            {
                return Results.NotFound();
            }
            catch (Features.Leagues.Profiles.LeagueProfileNotFoundException)
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
