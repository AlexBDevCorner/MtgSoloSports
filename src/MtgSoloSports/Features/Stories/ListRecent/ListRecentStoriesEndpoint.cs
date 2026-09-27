namespace MtgSoloSports.Features.Stories.ListRecent;

public static class ListRecentStoriesEndpoint
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/api/saves/{saveId:guid}/stories/recent", async (
            Guid saveId,
            int? take,
            ListRecentStoriesHandler handler,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(handler);
            try
            {
                ListRecentStoriesResponse response = await handler.HandleAsync(
                    saveId, take ?? ListRecentStoriesHandler.DefaultTake, cancellationToken).ConfigureAwait(false);
                return Results.Ok(response);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (Persistence.Saves.SaveNotFoundException)
            {
                return Results.NotFound();
            }
        });
    }
}
