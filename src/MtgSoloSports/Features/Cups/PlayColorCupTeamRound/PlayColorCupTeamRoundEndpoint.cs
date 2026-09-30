using MtgSoloSports.Features.Cups.RunColorCupTeam;
using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Cups.PlayColorCupTeamRound;

public static class PlayColorCupTeamRoundEndpoint
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapPost("/api/saves/{saveId:guid}/cups/color/team/rounds/next", async (
            Guid saveId,
            int? sourceSeason,
            PlayColorCupTeamRoundHandler handler,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(handler);
            try
            {
                PlayColorCupTeamRoundResponse response = await handler.HandleAsync(saveId, sourceSeason, cancellationToken).ConfigureAwait(false);
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
            catch (RunColorCupTeamConflictException ex)
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
