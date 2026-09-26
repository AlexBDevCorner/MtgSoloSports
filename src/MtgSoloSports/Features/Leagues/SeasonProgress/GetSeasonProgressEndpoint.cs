using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Leagues.SeasonProgress;

public static class GetSeasonProgressEndpoint
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/api/saves/{saveId:guid}/seasons/{seasonNumber:int}/progress", async (
            Guid saveId,
            int seasonNumber,
            GetSeasonProgressHandler handler,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(handler);
            try
            {
                GetSeasonProgressResponse response = await handler.HandleAsync(saveId, seasonNumber, cancellationToken).ConfigureAwait(false);
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
