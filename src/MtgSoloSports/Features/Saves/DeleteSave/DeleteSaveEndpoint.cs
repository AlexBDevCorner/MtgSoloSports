using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Saves.DeleteSave;

public static class DeleteSaveEndpoint
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapDelete("/api/saves/{saveId:guid}", async (Guid saveId, DeleteSaveHandler handler, CancellationToken cancellationToken) =>
        {
            try
            {
                await handler.HandleAsync(saveId, cancellationToken).ConfigureAwait(false);
                return Results.NoContent();
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (SaveNotFoundException)
            {
                return Results.NotFound();
            }
        });
    }
}
