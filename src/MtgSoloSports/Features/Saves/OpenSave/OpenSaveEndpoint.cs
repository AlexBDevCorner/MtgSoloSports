using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Saves.OpenSave;

public static class OpenSaveEndpoint
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/api/saves/{saveId:guid}", async (Guid saveId, OpenSaveHandler handler, CancellationToken cancellationToken) =>
        {
            try
            {
                OpenSaveResponse response = await handler.HandleAsync(saveId, cancellationToken).ConfigureAwait(false);
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
        });
    }
}
