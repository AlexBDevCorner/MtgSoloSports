using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Cups.GetChaosCupAthleteJourney;

public static class GetChaosCupAthleteJourneyEndpoint
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/api/saves/{saveId:guid}/cups/chaos/athletes/{athleteId:int}", async (
            Guid saveId,
            int athleteId,
            int? sourceSeason,
            GetChaosCupAthleteJourneyHandler handler,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(handler);
            try
            {
                GetChaosCupAthleteJourneyResponse response = await handler.HandleAsync(
                    saveId, athleteId, sourceSeason, cancellationToken).ConfigureAwait(false);
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
            catch (GetChaosCupAthleteJourneyNotFoundException)
            {
                return Results.NotFound();
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }
}
