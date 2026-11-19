using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.Shared;

namespace MtgSoloSports.Features.Leagues.UpgradeToTiered;

public static class UpgradeToTieredEndpoint
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapPost("/api/saves/{saveId:guid}/leagues/upgrade-to-tiered", async (
            Guid saveId,
            UpgradeToTieredHandler handler,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(handler);
            try
            {
                UpgradeToTieredResponse response = await handler.HandleAsync(saveId, cancellationToken).ConfigureAwait(false);
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
            catch (UpgradeToTieredConflictException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
            catch (SaveCorruptionException ex)
            {
                // Corrupted sporting state is a server failure, never a client error (MSS-144).
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status500InternalServerError);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }
}
