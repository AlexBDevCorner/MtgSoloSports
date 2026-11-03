using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Cups.GetChaosCupGroup;

public static class GetChaosCupGroupEndpoint
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        MapGroup(app);
        MapRound(app);
    }

    private static void MapGroup(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/api/saves/{saveId:guid}/cups/chaos/stages/{stageIndex:int}/groups/{groupNumber:int}", async (
            Guid saveId,
            int stageIndex,
            int groupNumber,
            int? sourceSeason,
            GetChaosCupGroupHandler handler,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(handler);
            try
            {
                GetChaosCupGroupResponse response = await handler.HandleGroupAsync(
                    saveId, stageIndex, groupNumber, sourceSeason, cancellationToken).ConfigureAwait(false);
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
            catch (GetChaosCupGroupNotFoundException)
            {
                return Results.NotFound();
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }

    private static void MapRound(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/api/saves/{saveId:guid}/cups/chaos/stages/{stageIndex:int}/groups/{groupNumber:int}/rounds/{roundNumber:int}", async (
            Guid saveId,
            int stageIndex,
            int groupNumber,
            int roundNumber,
            int? sourceSeason,
            GetChaosCupGroupHandler handler,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(handler);
            try
            {
                GetChaosCupRoundResponse response = await handler.HandleRoundAsync(
                    saveId, stageIndex, groupNumber, roundNumber, sourceSeason, cancellationToken).ConfigureAwait(false);
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
            catch (GetChaosCupGroupNotFoundException)
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
