using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Cups.RunChaosCupGroup;

/// <summary>
/// Chaos Cup single-group simulation routes (MSS-101, deferred).
/// MSS-106 owns forward-only postseason activation and season/phase gating, so
/// these routes stay unmapped until that lifecycle integration lands. Handlers
/// remain available via DI for direct calls and regression tests. Do not map
/// this endpoint in <c>Program.cs</c> before MSS-106.
/// </summary>
public static class RunChaosCupGroupEndpoint
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        MapPlayNext(app);
        MapFinish(app);
        MapGet(app);
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

    private static void MapGet(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/api/saves/{saveId:guid}/cups/chaos/stages/{stageIndex:int}/groups/{groupNumber:int}", async (
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
                RunChaosCupGroupResponse response = await handler.GetAsync(saveId, stageIndex, groupNumber, sourceSeason, cancellationToken).ConfigureAwait(false);
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
