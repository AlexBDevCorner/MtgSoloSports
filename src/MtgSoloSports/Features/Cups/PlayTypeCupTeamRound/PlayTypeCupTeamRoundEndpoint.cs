using MtgSoloSports.Features.Cups.RunTypeCupTeam;
using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Cups.PlayTypeCupTeamRound;

public static class PlayTypeCupTeamRoundEndpoint
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapPost("/api/saves/{saveId:guid}/cups/type/team/rounds/next", async (
            Guid saveId,
            int? sourceSeason,
            PlayTypeCupTeamRoundHandler handler,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(handler);
            try
            {
                PlayTypeCupTeamRoundResponse response = await handler.HandleAsync(saveId, sourceSeason, cancellationToken).ConfigureAwait(false);
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
            catch (RunTypeCupTeamConflictException ex)
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
