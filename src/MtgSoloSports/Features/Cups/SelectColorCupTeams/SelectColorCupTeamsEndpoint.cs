using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Cups.SelectColorCupTeams;

public static class SelectColorCupTeamsEndpoint
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapPost("/api/saves/{saveId:guid}/cups/color/teams", async (
            Guid saveId,
            int? sourceSeason,
            SelectColorCupTeamsHandler handler,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(handler);
            try
            {
                SelectColorCupTeamsResponse response = await handler.HandleAsync(saveId, sourceSeason, cancellationToken).ConfigureAwait(false);
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
            catch (SelectColorCupTeamsConflictException ex)
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
