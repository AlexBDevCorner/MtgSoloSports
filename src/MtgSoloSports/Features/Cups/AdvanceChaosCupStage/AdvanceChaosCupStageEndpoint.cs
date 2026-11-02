using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Cups.AdvanceChaosCupStage;

/// <summary>
/// Chaos Cup next-stage draw routes (MSS-102, deferred).
/// MSS-106 owns forward-only postseason activation and season/phase gating, so
/// these routes stay unmapped until that lifecycle integration lands. Handlers
/// remain available via DI for direct calls and regression tests. Do not map
/// this endpoint in <c>Program.cs</c> before MSS-106.
/// </summary>
public static class AdvanceChaosCupStageEndpoint
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        MapDraw(app);
        MapGet(app);
    }

    private static void MapDraw(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapPost("/api/saves/{saveId:guid}/cups/chaos/stages/{stageIndex:int}/draw", async (
            Guid saveId,
            int stageIndex,
            int? sourceSeason,
            AdvanceChaosCupStageHandler handler,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(handler);
            try
            {
                AdvanceChaosCupStageResponse response = await handler.HandleAsync(saveId, stageIndex, sourceSeason, cancellationToken).ConfigureAwait(false);
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
            catch (AdvanceChaosCupStageConflictException ex)
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
        app.MapGet("/api/saves/{saveId:guid}/cups/chaos/stages/{stageIndex:int}/draw", async (
            Guid saveId,
            int stageIndex,
            int? sourceSeason,
            AdvanceChaosCupStageHandler handler,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(handler);
            try
            {
                AdvanceChaosCupStageResponse response = await handler.GetAsync(saveId, stageIndex, sourceSeason, cancellationToken).ConfigureAwait(false);
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
            catch (AdvanceChaosCupStageConflictException ex)
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
