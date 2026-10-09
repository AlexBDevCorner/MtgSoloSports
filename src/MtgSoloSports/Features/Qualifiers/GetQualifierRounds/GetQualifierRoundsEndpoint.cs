using MtgSoloSports.Features.History;
using MtgSoloSports.Features.History.EventRounds;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Leagues;

namespace MtgSoloSports.Features.Qualifiers.GetQualifierRounds;

public static class GetQualifierRoundsEndpoint
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        MapList(app);
        MapSingle(app);
    }

    internal static void MapList(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/api/saves/{saveId:guid}/qualifiers/{boundary}/{color}/rounds", async (
            Guid saveId,
            string boundary,
            string color,
            int? fromSeason,
            GetQualifierRoundsHandler handler,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(handler);
            try
            {
                (QualifierBoundary parsedBoundary, int parsedColor) = RunFeederQualifierEndpoint.Parse(boundary, color);
                GetQualifierRoundsResponse response = await handler.HandleListAsync(
                    saveId, parsedBoundary, parsedColor, fromSeason, cancellationToken).ConfigureAwait(false);
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
            catch (QualifierListNotFoundException)
            {
                return Results.NotFound();
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }

    internal static void MapSingle(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/api/saves/{saveId:guid}/qualifiers/{boundary}/{color}/rounds/{roundNumber:int}", async (
            Guid saveId,
            string boundary,
            string color,
            int roundNumber,
            int? fromSeason,
            GetQualifierRoundsHandler handler,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(handler);
            try
            {
                (QualifierBoundary parsedBoundary, int parsedColor) = RunFeederQualifierEndpoint.Parse(boundary, color);
                EventRoundView response = await handler.HandleRoundAsync(
                    saveId, parsedBoundary, parsedColor, roundNumber, fromSeason, cancellationToken).ConfigureAwait(false);
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
            catch (QualifierListNotFoundException)
            {
                return Results.NotFound();
            }
            catch (HistoryNotFoundException)
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
