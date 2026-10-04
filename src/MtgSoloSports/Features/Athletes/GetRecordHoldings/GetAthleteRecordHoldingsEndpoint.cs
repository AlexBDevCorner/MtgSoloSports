using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Athletes.GetRecordHoldings;

public static class GetAthleteRecordHoldingsEndpoint
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/api/saves/{saveId:guid}/athletes/{athleteId:int}/record-holdings", async (
            Guid saveId,
            int athleteId,
            GetAthleteRecordHoldingsHandler handler,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(handler);
            try
            {
                GetAthleteRecordHoldingsResponse response = await handler.HandleAsync(saveId, athleteId, cancellationToken).ConfigureAwait(false);
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
            catch (AthleteRecordHoldingsNotFoundException)
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
