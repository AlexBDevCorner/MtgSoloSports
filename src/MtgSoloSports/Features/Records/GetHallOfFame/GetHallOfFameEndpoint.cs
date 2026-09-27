using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Records.GetHallOfFame;

public static class GetHallOfFameEndpoint
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/api/saves/{saveId:guid}/hall-of-fame", async (
            Guid saveId,
            int? take,
            GetHallOfFameHandler handler,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(handler);
            try
            {
                GetHallOfFameResponse response = await handler.HandleAsync(saveId, take ?? GetHallOfFameHandler.DefaultTake, cancellationToken).ConfigureAwait(false);
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
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }
}
