using MtgSoloSports.Shared;

namespace MtgSoloSports.Features.Saves.ListSaves;

public static class ListSavesEndpoint
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/api/saves", async (ListSavesHandler handler, CancellationToken cancellationToken) =>
        {
            try
            {
                ListSavesResponse response = await handler.HandleAsync(cancellationToken).ConfigureAwait(false);
                return Results.Ok(response);
            }
            catch (SaveCorruptionException ex)
            {
                // Corrupted sporting state is a server failure, never a client error (MSS-144).
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status500InternalServerError);
            }
        });
    }
}
