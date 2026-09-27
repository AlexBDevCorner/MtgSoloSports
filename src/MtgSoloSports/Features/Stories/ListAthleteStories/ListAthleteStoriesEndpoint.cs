namespace MtgSoloSports.Features.Stories.ListAthleteStories;

public static class ListAthleteStoriesEndpoint
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/api/saves/{saveId:guid}/athletes/{athleteId:int}/stories", async (
            Guid saveId,
            int athleteId,
            int? take,
            ListAthleteStoriesHandler handler,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(handler);
            try
            {
                Features.Stories.ListRecent.ListRecentStoriesResponse response = await handler.HandleAsync(
                    saveId, athleteId, take ?? Features.Stories.ListRecent.ListRecentStoriesHandler.DefaultTake, cancellationToken).ConfigureAwait(false);
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
            catch (AthleteStoriesNotFoundException)
            {
                return Results.NotFound();
            }
        });
    }
}
