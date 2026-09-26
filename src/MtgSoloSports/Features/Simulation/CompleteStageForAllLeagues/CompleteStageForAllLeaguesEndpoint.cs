using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Simulation.CompleteStageForAllLeagues;

public static class CompleteStageForAllLeaguesEndpoint
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapPost("/api/saves/{saveId:guid}/stages/complete-all", async (
            Guid saveId,
            CompleteStageForAllLeaguesHandler handler,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(handler);
            try
            {
                CompleteStageForAllLeaguesResponse response = await handler.HandleAsync(saveId, cancellationToken).ConfigureAwait(false);
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
            catch (CompleteStageForAllLeaguesConflictException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
            catch (CompleteStage.CompleteStageConflictException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
            catch (AdvanceRound.AdvanceRoundConflictException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }
}
