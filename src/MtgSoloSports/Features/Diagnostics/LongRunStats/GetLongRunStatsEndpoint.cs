using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Diagnostics.LongRunStats;

public static class GetLongRunStatsEndpoint
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/api/saves/{saveId:guid}/diagnostics/longrun-stats", async (
            Guid saveId,
            GetLongRunStatsHandler handler,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(handler);
            try
            {
                GetLongRunStatsResponse response = await handler.HandleAsync(saveId, cancellationToken).ConfigureAwait(false);
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
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }
}
