using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Superleague.CreateInaugural;

public static class CreateInauguralSuperleagueEndpoint
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapPost("/api/saves/{saveId:guid}/superleague/inaugural", async (
            Guid saveId,
            CreateInauguralSuperleagueHandler handler,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(handler);
            try
            {
                CreateInauguralSuperleagueResponse response = await handler.HandleAsync(saveId, cancellationToken).ConfigureAwait(false);
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
            catch (CreateInauguralSuperleagueConflictException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }
}
