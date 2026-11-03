using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Cups.RunChaosCupGroup;

/// <summary>
/// Chaos Cup single-group simulation routes (MSS-101, MSS-110 Live controls).
/// Only the scoped POST mutations are mapped; the GET group replay stays on
/// <c>GetChaosCupGroupEndpoint</c> so one route owns the rich read model.
/// Each (stage, group) owns an isolated RNG, so mapped group order and
/// incremental-vs-complete calls stay byte-identical.
/// </summary>
public static class RunChaosCupGroupEndpoint
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        MapPlayNext(app);
        MapFinish(app);
    }

    private static void MapPlayNext(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapPost("/api/saves/{saveId:guid}/cups/chaos/stages/{stageIndex:int}/groups/{groupNumber:int}/rounds", async (
            Guid saveId,
            int stageIndex,
            int groupNumber,
            int? sourceSeason,
            RunChaosCupGroupHandler handler,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(handler);
            try
            {
                RunChaosCupGroupResponse response = await handler.PlayNextAsync(saveId, stageIndex, groupNumber, sourceSeason, cancellationToken).ConfigureAwait(false);
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
            catch (RunChaosCupGroupConflictException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }

    private static void MapFinish(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapPost("/api/saves/{saveId:guid}/cups/chaos/stages/{stageIndex:int}/groups/{groupNumber:int}/complete", async (
            Guid saveId,
            int stageIndex,
            int groupNumber,
            int? sourceSeason,
            RunChaosCupGroupHandler handler,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(handler);
            try
            {
                RunChaosCupGroupResponse response = await handler.FinishGroupAsync(saveId, stageIndex, groupNumber, sourceSeason, cancellationToken).ConfigureAwait(false);
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
            catch (RunChaosCupGroupConflictException ex)
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
