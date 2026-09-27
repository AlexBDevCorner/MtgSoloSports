using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.History.GetStageStandings;

public static class GetHistoryStageStandingsEndpoint
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/api/saves/{saveId:guid}/history/seasons/{seasonNumber:int}/competitions/{leagueId:int}/stages/{stageNumber:int}/standings", async (
            Guid saveId,
            int seasonNumber,
            int leagueId,
            int stageNumber,
            GetHistoryStageStandingsHandler handler,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(handler);
            try
            {
                GetHistoryStageStandingsResponse response = await handler.HandleAsync(saveId, seasonNumber, leagueId, stageNumber, cancellationToken).ConfigureAwait(false);
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
