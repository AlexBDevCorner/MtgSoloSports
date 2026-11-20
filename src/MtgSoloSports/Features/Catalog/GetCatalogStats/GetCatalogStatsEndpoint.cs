using MtgSoloSports.Persistence.Catalog;

namespace MtgSoloSports.Features.Catalog.GetCatalogStats;

public static class GetCatalogStatsEndpoint
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/api/catalog/stats", async (GetCatalogStatsHandler handler, CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(handler);
            try
            {
                GetCatalogStatsResponse response = await handler.HandleAsync(cancellationToken).ConfigureAwait(false);
                return Results.Ok(response);
            }
            catch (CatalogSchemaIncompatibleException ex)
            {
                // An unserveable catalog schema is server state, never a client error (MSS-147);
                // the message carries the re-import guidance.
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status500InternalServerError);
            }
        });
    }
}
