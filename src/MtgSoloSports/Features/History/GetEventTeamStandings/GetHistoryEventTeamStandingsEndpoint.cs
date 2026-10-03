using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.History.GetEventTeamStandings;

public static class GetHistoryEventTeamStandingsEndpoint
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/api/saves/{saveId:guid}/history/seasons/{seasonNumber:int}/events/{eventKey}/team-standings", async (
            Guid saveId,
            int seasonNumber,
            string eventKey,
            int? beforeGroup,
            int? beforeRound,
            GetHistoryEventTeamStandingsHandler handler,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(handler);
            try
            {
                HistoryEventTeamStandingsResponse response = await handler.HandleAsync(saveId, seasonNumber, eventKey, beforeGroup, beforeRound, cancellationToken).ConfigureAwait(false);
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
