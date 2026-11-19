using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.Shared;

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
            int? tournamentPhase,
            int? qualificationGroup,
            GetHistoryEventTeamStandingsHandler handler,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(handler);
            try
            {
                HistoryEventTeamStandingsResponse response = await handler.HandleAsync(saveId, seasonNumber, eventKey, beforeGroup, beforeRound, cancellationToken, tournamentPhase, qualificationGroup).ConfigureAwait(false);
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
