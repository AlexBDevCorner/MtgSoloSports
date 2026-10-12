using MtgSoloSports.Features.Cups.RunTypeCupTeam;
using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Cups.FinishTypeCupTeamStage;

public static class FinishTypeCupTeamStageEndpoint
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapPost("/api/saves/{saveId:guid}/cups/type/team/stages/finish", async (
            Guid saveId,
            int? tournamentPhase,
            int? qualificationGroup,
            int? sourceSeason,
            FinishTypeCupTeamStageHandler handler,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(handler);
            try
            {
                FinishTypeCupTeamStageResponse response = await handler.HandleAsync(
                    saveId, tournamentPhase, qualificationGroup, sourceSeason, cancellationToken).ConfigureAwait(false);
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
