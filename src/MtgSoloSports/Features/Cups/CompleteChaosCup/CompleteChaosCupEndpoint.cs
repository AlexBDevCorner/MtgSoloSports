using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Cups.CompleteChaosCup;

/// <summary>
/// Chaos Cup bulk simulation routes (MSS-103, MSS-110 Live controls).
/// Stage-scoped and tournament-wide fast-forwards reuse the canonical one-group
/// complete action and the persisted stage-draw action.
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
