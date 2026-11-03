using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Cups.GetChaosCupSurvival;

public static class GetChaosCupSurvivalEndpoint
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/api/saves/{saveId:guid}/cups/chaos/survival", async (
            Guid saveId,
            int? sourceSeason,
            GetChaosCupSurvivalHandler handler,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(handler);
            try
            {
                GetChaosCupSurvivalResponse response = await handler.HandleAsync(
                    saveId, sourceSeason, cancellationToken).ConfigureAwait(false);
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
            catch (GetChaosCupSurvivalNotFoundException)
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
