using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Superleague.GetFeederMovements;

public static class GetFeederMovementsEndpoint
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/api/saves/{saveId:guid}/feeder-movements", async (
            Guid saveId,
            int? fromSeason,
            GetFeederMovementsHandler handler,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(handler);
            try
            {
                GetFeederMovementsResponse response = await handler.HandleAsync(saveId, fromSeason, cancellationToken).ConfigureAwait(false);
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
            catch (FeederMovementsNotFoundException)
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
