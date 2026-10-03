using MtgSoloSports.Features.Cups.GetTypeCupSelection;
using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Cups.GetTypeCupSelectionReport;

public static class GetTypeCupSelectionReportEndpoint
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/api/saves/{saveId:guid}/cups/type/selection-report", async (
            Guid saveId,
            int? sourceSeason,
            GetTypeCupSelectionReportHandler handler,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(handler);
            try
            {
                GetTypeCupSelectionReportResponse response = await handler.HandleAsync(saveId, sourceSeason, cancellationToken).ConfigureAwait(false);
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
            catch (TypeCupSelectionNotFoundException)
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
