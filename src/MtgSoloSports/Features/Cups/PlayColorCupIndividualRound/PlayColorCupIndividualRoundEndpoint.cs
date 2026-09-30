using MtgSoloSports.Features.Cups.RunColorCupIndividual;
using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Cups.PlayColorCupIndividualRound;

public static class PlayColorCupIndividualRoundEndpoint
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapPost("/api/saves/{saveId:guid}/cups/color/individual/rounds/next", async (
            Guid saveId,
            int? sourceSeason,
            PlayColorCupIndividualRoundHandler handler,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(handler);
            try
            {
                PlayColorCupIndividualRoundResponse response = await handler.HandleAsync(saveId, sourceSeason, cancellationToken).ConfigureAwait(false);
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
            catch (RunColorCupIndividualConflictException ex)
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
