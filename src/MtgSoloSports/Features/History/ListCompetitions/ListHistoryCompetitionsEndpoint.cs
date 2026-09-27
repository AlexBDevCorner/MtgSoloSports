using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.History.ListCompetitions;

public static class ListHistoryCompetitionsEndpoint
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/api/saves/{saveId:guid}/history/seasons/{seasonNumber:int}/competitions", async (
            Guid saveId,
            int seasonNumber,
            ListHistoryCompetitionsHandler handler,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(handler);
            try
            {
                ListHistoryCompetitionsResponse response = await handler.HandleAsync(saveId, seasonNumber, cancellationToken).ConfigureAwait(false);
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
