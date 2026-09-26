using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Athletes.GetProfile;

public static class GetAthleteProfileEndpoint
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/api/saves/{saveId:guid}/athletes/{athleteId:int}", async (
            Guid saveId,
            int athleteId,
            GetAthleteProfileHandler handler,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(handler);
            try
            {
                GetAthleteProfileResponse response = await handler.HandleAsync(saveId, athleteId, cancellationToken).ConfigureAwait(false);
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
            catch (AthleteProfileNotFoundException)
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
