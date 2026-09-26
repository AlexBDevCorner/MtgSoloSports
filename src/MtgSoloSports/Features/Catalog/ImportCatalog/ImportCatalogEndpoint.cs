namespace MtgSoloSports.Features.Catalog.ImportCatalog;

public static class ImportCatalogEndpoint
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/api/catalog/import", async (HttpRequest httpRequest, ImportCatalogHandler handler, CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(httpRequest);
            ArgumentNullException.ThrowIfNull(handler);

            string bulkJson;
            using (StreamReader reader = new(httpRequest.Body))
            {
                bulkJson = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            }

            if (string.IsNullOrWhiteSpace(bulkJson))
            {
                return Results.BadRequest(new { error = "Bulk JSON array body is required." });
            }

            try
            {
                ImportCatalogResponse response = await handler.HandleAsync(new ImportCatalogRequest(bulkJson), cancellationToken).ConfigureAwait(false);
                return Results.Ok(response);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }
}
