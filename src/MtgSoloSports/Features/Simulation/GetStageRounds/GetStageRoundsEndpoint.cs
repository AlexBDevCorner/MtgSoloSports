using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Simulation.GetStageRounds;

public static class GetStageRoundsEndpoint
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/api/saves/{saveId:guid}/leagues/{leagueId:int}/stages/{stageNumber:int}/rounds", async (
            Guid saveId,
            int leagueId,
            int stageNumber,
            GetStageRoundsHandler handler,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(handler);
            try
            {
                GetStageRoundsResponse response = await handler.HandleAsync(saveId, leagueId, stageNumber, cancellationToken).ConfigureAwait(false);
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
