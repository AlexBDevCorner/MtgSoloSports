using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.History.GetRoundReplay;

public static class GetHistoryRoundReplayEndpoint
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/api/saves/{saveId:guid}/history/seasons/{seasonNumber:int}/competitions/{leagueId:int}/stages/{stageNumber:int}/rounds/{roundNumber:int}", async (
            Guid saveId,
            int seasonNumber,
            int leagueId,
            int stageNumber,
            int roundNumber,
            GetHistoryRoundReplayHandler handler,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(handler);
            try
            {
                GetHistoryRoundReplayResponse response = await handler.HandleAsync(saveId, seasonNumber, leagueId, stageNumber, roundNumber, cancellationToken).ConfigureAwait(false);
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
