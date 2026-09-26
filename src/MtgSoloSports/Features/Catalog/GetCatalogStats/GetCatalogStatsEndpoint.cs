namespace MtgSoloSports.Features.Catalog.GetCatalogStats;

public static class GetCatalogStatsEndpoint
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/api/catalog/stats", async (GetCatalogStatsHandler handler, CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(handler);
            GetCatalogStatsResponse response = await handler.HandleAsync(cancellationToken).ConfigureAwait(false);
            return Results.Ok(response);
        });
    }
}
