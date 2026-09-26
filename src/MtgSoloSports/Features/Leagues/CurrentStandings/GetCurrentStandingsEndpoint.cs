using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Leagues.CurrentStandings;

public static class GetCurrentStandingsEndpoint
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/api/saves/{saveId:guid}/leagues/{leagueId:int}/standings/current", async (
            Guid saveId,
            int leagueId,
            GetCurrentStandingsHandler handler,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(handler);
            try
            {
                GetCurrentStandingsResponse response = await handler.HandleAsync(saveId, leagueId, cancellationToken).ConfigureAwait(false);
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
