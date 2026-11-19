using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.Shared;

namespace MtgSoloSports.Features.History.ListRounds;

public static class ListHistoryRoundsEndpoint
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/api/saves/{saveId:guid}/history/seasons/{seasonNumber:int}/competitions/{leagueId:int}/stages/{stageNumber:int}/rounds", async (
            Guid saveId,
            int seasonNumber,
            int leagueId,
            int stageNumber,
            ListHistoryRoundsHandler handler,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(handler);
            try
            {
                ListHistoryRoundsResponse response = await handler.HandleAsync(saveId, seasonNumber, leagueId, stageNumber, cancellationToken).ConfigureAwait(false);
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
            catch (SaveCorruptionException ex)
            {
                // Corrupted sporting state is a server failure, never a client error (MSS-144).
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status500InternalServerError);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }
}
