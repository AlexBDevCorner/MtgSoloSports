using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.Shared;

namespace MtgSoloSports.Features.Records.GetHallOfFame;

public static class GetHallOfFameEndpoint
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/api/saves/{saveId:guid}/hall-of-fame", (
            Guid saveId,
            int? take,
            int? fromSeason,
            int? toSeason,
            GetHallOfFameHandler handler,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(handler);
            return HandleAsync(saveId, take, fromSeason, toSeason, handler, cancellationToken);
        });
    }

    internal static async Task<IResult> HandleAsync(
        Guid saveId,
        int? take,
        int? fromSeason,
        int? toSeason,
        GetHallOfFameHandler handler,
        CancellationToken cancellationToken)
    {
        try
        {
            GetHallOfFameResponse response = await handler.HandleAsync(
                saveId, take ?? GetHallOfFameHandler.DefaultTake, fromSeason, toSeason, cancellationToken).ConfigureAwait(false);
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
        catch (SaveCorruptionException ex)
        {
            // Corrupted sporting state is a server failure, never a client error (MSS-144).
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status500InternalServerError);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }
}
