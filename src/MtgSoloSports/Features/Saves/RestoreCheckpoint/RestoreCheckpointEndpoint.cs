using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Saves.RestoreCheckpoint;

public static class RestoreCheckpointEndpoint
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapPost("/api/saves/{saveId:guid}/checkpoints/{checkpointId:guid}/restore", async (
            Guid saveId,
            Guid checkpointId,
            RestoreCheckpointHandler handler,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(handler);
            try
            {
                RestoreCheckpointResponse response = await handler.HandleAsync(saveId, checkpointId, cancellationToken).ConfigureAwait(false);
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
            catch (SaveCheckpointNotFoundException)
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
