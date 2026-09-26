using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Leagues.SeasonTable;

public static class GetSeasonTableEndpoint
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/api/saves/{saveId:guid}/leagues/{leagueId:int}/seasons/{seasonNumber:int}/table", async (
            Guid saveId,
            int leagueId,
            int seasonNumber,
            GetSeasonTableHandler handler,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(handler);
            try
            {
                GetSeasonTableResponse response = await handler.HandleAsync(saveId, leagueId, seasonNumber, cancellationToken).ConfigureAwait(false);
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
            catch (SeasonTableNotFoundException)
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
