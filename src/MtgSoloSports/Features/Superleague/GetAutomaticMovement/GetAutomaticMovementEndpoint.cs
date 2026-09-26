using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Superleague.GetAutomaticMovement;

public static class GetAutomaticMovementEndpoint
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/api/saves/{saveId:guid}/superleague/automatic-movement", async (
            Guid saveId,
            int? fromSeason,
            GetAutomaticMovementHandler handler,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(handler);
            try
            {
                GetAutomaticMovementResponse response = await handler.HandleAsync(saveId, fromSeason, cancellationToken).ConfigureAwait(false);
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
            catch (AutomaticMovementNotFoundException)
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
