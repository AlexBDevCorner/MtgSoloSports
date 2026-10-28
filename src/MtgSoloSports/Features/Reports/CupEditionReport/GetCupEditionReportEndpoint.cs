using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Reports.CupEditionReport;

public static class GetCupEditionReportEndpoint
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/api/saves/{saveId:guid}/cups/{cup}/editions/{seasonNumber:int}/report", async (
            Guid saveId,
            string cup,
            int seasonNumber,
            string? format,
            GetCupEditionReportHandler handler,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(handler);
            try
            {
                GetCupEditionReportHandler.ReportFile file = await handler.HandleAsync(saveId, cup, seasonNumber, format, cancellationToken).ConfigureAwait(false);
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
            catch (CupEditionReportNotFoundException)
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
