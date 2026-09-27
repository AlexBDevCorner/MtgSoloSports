using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Cups.GetColorCupSelection;

public static class GetColorCupSelectionEndpoint
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/api/saves/{saveId:guid}/cups/color/teams", async (
            Guid saveId,
            int? sourceSeason,
            GetColorCupSelectionHandler handler,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(handler);
            try
            {
                GetColorCupSelectionResponse response = await handler.HandleAsync(saveId, sourceSeason, cancellationToken).ConfigureAwait(false);
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
            catch (ColorCupSelectionNotFoundException)
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
