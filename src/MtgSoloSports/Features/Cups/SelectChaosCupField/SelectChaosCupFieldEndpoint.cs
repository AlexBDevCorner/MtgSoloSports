using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Cups.SelectChaosCupField;

public static class SelectChaosCupFieldEndpoint
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        MapSelect(app);
        MapGet(app);
    }

    private static void MapSelect(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapPost("/api/saves/{saveId:guid}/cups/chaos/field", async (
            Guid saveId,
            int? sourceSeason,
            SelectChaosCupFieldHandler handler,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(handler);
            try
            {
                SelectChaosCupFieldResponse response = await handler.HandleAsync(saveId, sourceSeason, cancellationToken).ConfigureAwait(false);
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
            catch (SelectChaosCupFieldConflictException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }

    private static void MapGet(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/api/saves/{saveId:guid}/cups/chaos/field", async (
            Guid saveId,
            int? sourceSeason,
            SelectChaosCupFieldHandler handler,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(handler);
            try
            {
                SelectChaosCupFieldResponse response = await handler.GetAsync(saveId, sourceSeason, cancellationToken).ConfigureAwait(false);
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
            catch (SelectChaosCupFieldConflictException ex)
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
