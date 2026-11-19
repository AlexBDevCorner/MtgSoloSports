using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.Shared;

namespace MtgSoloSports.Features.Superleague.GetQualifierResult;

public static class GetQualifierResultEndpoint
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/api/saves/{saveId:guid}/superleague/qualifier", async (
            Guid saveId,
            int? fromSeason,
            GetQualifierResultHandler handler,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(handler);
            try
            {
                GetQualifierResultResponse response = await handler.HandleAsync(saveId, fromSeason, cancellationToken).ConfigureAwait(false);
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
            catch (QualifierResultNotFoundException)
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
        });
    }
}
