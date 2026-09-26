namespace MtgSoloSports.Features.Saves.ListSaves;

public static class ListSavesEndpoint
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/api/saves", async (ListSavesHandler handler, CancellationToken cancellationToken) =>
        {
            ListSavesResponse response = await handler.HandleAsync(cancellationToken).ConfigureAwait(false);
            return Results.Ok(response);
        });
    }
}
