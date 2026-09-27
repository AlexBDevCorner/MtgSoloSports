using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Saves.CreateCheckpoint;

public static class CreateCheckpointEndpoint
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapPost("/api/saves/{saveId:guid}/checkpoints", async (
            Guid saveId,
            CreateCheckpointRequest? request,
            CreateCheckpointHandler handler,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(handler);
            try
            {
                CreateCheckpointResponse response = await handler.HandleAsync(saveId, request?.Reason, cancellationToken).ConfigureAwait(false);
                return Results.Created($"/api/saves/{saveId:D}/checkpoints/{response.CheckpointId:D}", response);
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
