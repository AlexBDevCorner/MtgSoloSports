using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Leagues;

namespace MtgSoloSports.Features.Qualifiers.PlayFeederQualifierRound;

public static class PlayFeederQualifierRoundEndpoint
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapPost("/api/saves/{saveId:guid}/qualifiers/{boundary}/{color}/rounds/next", async (
            Guid saveId,
            string boundary,
            string color,
            PlayFeederQualifierRoundHandler handler,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(handler);
            try
            {
                (QualifierBoundary parsedBoundary, int parsedColor) = RunFeederQualifierEndpoint.Parse(boundary, color);
                PlayFeederQualifierRoundResponse response = await handler.HandleAsync(
                    saveId, parsedBoundary, parsedColor, cancellationToken).ConfigureAwait(false);
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
            catch (RunFeederQualifierConflictException ex)
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
