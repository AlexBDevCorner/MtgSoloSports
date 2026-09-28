namespace MtgSoloSports.Features.Catalog.ImportFromScryfall;

public static class ImportFromScryfallEndpoint
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/api/catalog/import-from-scryfall", HandleAsync);
    }

    private static async Task<IResult> HandleAsync(
        ImportFromScryfallHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(httpContext);

        try
        {
            ImportFromScryfallResponse response = await handler.HandleAsync(cancellationToken).ConfigureAwait(false);
            return Results.Ok(response);
        }
        catch (ScryfallImportInProgressException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
        catch (CatalogQuotaInsufficientException ex)
        {
            return MapQuotaInsufficient(ex);
        }
        catch (ScryfallUnavailableException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
        }
        catch (ScryfallImportFailedException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (OperationCanceledException) when (!httpContext.RequestAborted.IsCancellationRequested)
        {
            return Results.Json(
                new { error = "The Scryfall import timed out or was cancelled before saving. The existing catalog was left unchanged. Try again." },
                statusCode: StatusCodes.Status504GatewayTimeout);
        }
        catch (OperationCanceledException)
        {
            return Results.Json(
                new { error = "The Scryfall import was cancelled before saving. The existing catalog was left unchanged. Try again." },
                statusCode: StatusCodes.Status504GatewayTimeout);
        }
        catch (HttpRequestException ex)
        {
            return Results.Json(
                new { error = $"Could not reach Scryfall: {ex.Message} Check your internet connection and try again." },
                statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static IResult MapQuotaInsufficient(CatalogQuotaInsufficientException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return Results.UnprocessableEntity(new
        {
            error = ex.Message,
            countsBySportingColor = ex.CountsBySportingColor,
            insufficient = ex.Insufficient.Select(c => c.ToString()).ToArray(),
            sourceType = ex.BulkSource?.Type,
            sourceName = ex.BulkSource?.Name,
            sourceUpdatedAt = ex.BulkSource?.UpdatedAt,
        });
    }
}
