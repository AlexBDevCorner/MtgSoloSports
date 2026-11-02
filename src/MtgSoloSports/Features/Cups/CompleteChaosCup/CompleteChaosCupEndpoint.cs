using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Cups.CompleteChaosCup;

/// <summary>
/// Chaos Cup bulk simulation routes (MSS-103, deferred).
/// MSS-106 owns forward-only postseason activation and season/phase gating, so
/// these routes stay unmapped until that lifecycle integration lands. Handlers
/// remain available via DI for direct calls and regression tests. Do not map
/// this endpoint in <c>Program.cs</c> before MSS-106.
/// </summary>
public static class CompleteChaosCupEndpoint
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        MapCompleteStage(app);
        MapCompleteCup(app);
    }

    private static void MapCompleteStage(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapPost("/api/saves/{saveId:guid}/cups/chaos/stages/{stageIndex:int}/complete", async (
            Guid saveId,
            int stageIndex,
            int? sourceSeason,
            CompleteChaosCupHandler handler,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(handler);
            try
            {
                CompleteChaosCupStageResponse response = await handler.CompleteStageAsync(saveId, stageIndex, sourceSeason, cancellationToken).ConfigureAwait(false);
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
            catch (CompleteChaosCupConflictException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }

    private static void MapCompleteCup(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapPost("/api/saves/{saveId:guid}/cups/chaos/complete", async (
            Guid saveId,
            int? sourceSeason,
            CompleteChaosCupHandler handler,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(handler);
            try
            {
                CompleteChaosCupResponse response = await handler.CompleteCupAsync(saveId, sourceSeason, cancellationToken).ConfigureAwait(false);
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
            catch (CompleteChaosCupConflictException ex)
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
