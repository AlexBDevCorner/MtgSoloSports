using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Leagues;

namespace MtgSoloSports.Features.Qualifiers;

public static class RunFeederQualifierEndpoint
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        MapSingle(app);
        MapRunAll(app);
    }

    internal static void MapSingle(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapPost("/api/saves/{saveId:guid}/qualifiers/{boundary}/{color}", async (
            Guid saveId,
            string boundary,
            string color,
            BoundaryQualifierRunner handler,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(handler);
            try
            {
                (QualifierBoundary parsedBoundary, int parsedColor) = Parse(boundary, color);
                RunFeederQualifierResponse response = await handler.HandleAsync(
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

    internal static void MapRunAll(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapPost("/api/saves/{saveId:guid}/qualifiers/run-all", async (
            Guid saveId,
            RunAllQualifiersHandler handler,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(handler);
            try
            {
                RunAllQualifiersResponse response = await handler.HandleAsync(saveId, cancellationToken).ConfigureAwait(false);
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

    internal static (QualifierBoundary Boundary, int Color) Parse(string boundary, string color)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(boundary);
        ArgumentException.ThrowIfNullOrWhiteSpace(color);
        QualifierBoundary parsedBoundary = boundary.Trim().ToUpperInvariant() switch
        {
            "F1F2" => QualifierBoundary.Feeder1Feeder2,
            "F1-F2" => QualifierBoundary.Feeder1Feeder2,
            "F2F3" => QualifierBoundary.Feeder2Feeder3,
            "F2-F3" => QualifierBoundary.Feeder2Feeder3,
            _ => throw new ArgumentException($"Unknown qualifier boundary '{boundary}'. Use F1F2 or F2F3.", nameof(boundary)),
        };
        if (!Enum.TryParse<MtgSoloSports.SimulationKernel.Catalog.SportingColor>(color, ignoreCase: true, out var parsedColor))
        {
            throw new ArgumentException($"Unknown sporting color '{color}'.", nameof(color));
        }

        return (parsedBoundary, (int)parsedColor);
    }
}
