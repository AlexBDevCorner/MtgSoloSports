using MtgSoloSports.Features.History.EventRounds;
using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.History.GetEventRound;

public static class GetHistoryEventRoundEndpoint
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/api/saves/{saveId:guid}/history/seasons/{seasonNumber:int}/events/{eventKey}/rounds/{round:int}", async (
            Guid saveId,
            int seasonNumber,
            string eventKey,
            int round,
            int? group,
            GetHistoryEventRoundHandler handler,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(handler);
            try
            {
                EventRoundView response = await handler.HandleAsync(saveId, seasonNumber, eventKey, round, group, cancellationToken).ConfigureAwait(false);
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
