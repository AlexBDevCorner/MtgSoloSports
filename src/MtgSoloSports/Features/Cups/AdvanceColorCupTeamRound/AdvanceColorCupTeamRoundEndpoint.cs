using MtgSoloSports.Features.Cups.GetColorCupTeamLive;
using MtgSoloSports.Features.Cups.GetColorCupTeamResult;
using MtgSoloSports.Features.Cups.RunColorCupTeam;
using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Cups.AdvanceColorCupTeamRound;

public static class AdvanceColorCupTeamRoundEndpoint
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapPost("/api/saves/{saveId:guid}/cups/color/team/rounds/advance", async (
            Guid saveId,
            int? sourceSeason,
            AdvanceColorCupTeamRoundHandler handler,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(handler);
            try
            {
                ColorCupTeamLiveResponse response = await handler.HandleAsync(saveId, sourceSeason, cancellationToken).ConfigureAwait(false);
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
            catch (ColorCupTeamResultNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }
}
