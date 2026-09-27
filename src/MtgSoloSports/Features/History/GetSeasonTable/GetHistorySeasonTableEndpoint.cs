using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.History.GetSeasonTable;

public static class GetHistorySeasonTableEndpoint
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/api/saves/{saveId:guid}/history/seasons/{seasonNumber:int}/competitions/{leagueId:int}/table", async (
            Guid saveId,
            int seasonNumber,
            int leagueId,
            GetHistorySeasonTableHandler handler,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(handler);
            try
            {
                GetHistorySeasonTableResponse response = await handler.HandleAsync(saveId, seasonNumber, leagueId, cancellationToken).ConfigureAwait(false);
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
