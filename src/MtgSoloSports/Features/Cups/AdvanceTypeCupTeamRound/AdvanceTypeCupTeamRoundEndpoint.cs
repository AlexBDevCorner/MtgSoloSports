using MtgSoloSports.Features.Cups.GetTypeCupTeamLive;
using MtgSoloSports.Features.Cups.GetTypeCupTeamResult;
using MtgSoloSports.Features.Cups.RunTypeCupTeam;
using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Cups.AdvanceTypeCupTeamRound;

public static class AdvanceTypeCupTeamRoundEndpoint
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapPost("/api/saves/{saveId:guid}/cups/type/team/rounds/advance", async (
            Guid saveId,
            int? sourceSeason,
            AdvanceTypeCupTeamRoundHandler handler,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(handler);
            try
            {
                TypeCupTeamLiveResponse response = await handler.HandleAsync(saveId, sourceSeason, cancellationToken).ConfigureAwait(false);
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
            catch (TypeCupTeamResultNotFoundException ex)
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
