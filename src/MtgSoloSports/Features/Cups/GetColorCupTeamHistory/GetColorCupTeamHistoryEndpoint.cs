using MtgSoloSports.Features.Cups.CupHistory;
using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Cups.GetColorCupTeamHistory;

public static class GetColorCupTeamHistoryEndpoint
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/api/saves/{saveId:guid}/cups/color/teams/{teamKey}/history", async (
            Guid saveId,
            string teamKey,
            GetColorCupTeamHistoryHandler handler,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(handler);
            try
            {
                CupTeamHistoryResponse response = await handler.HandleAsync(saveId, teamKey, cancellationToken).ConfigureAwait(false);
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
            catch (CupTeamHistoryNotFoundException)
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
