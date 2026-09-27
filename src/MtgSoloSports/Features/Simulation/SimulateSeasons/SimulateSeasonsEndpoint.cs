using MtgSoloSports.Features.Seasons.StartNextSeason;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Features.Simulation.CompleteStage;
using MtgSoloSports.Features.Simulation.CompleteStageForAllLeagues;
using MtgSoloSports.Features.Superleague.CreateInaugural;
using MtgSoloSports.Features.Superleague.RebalanceFeeders;
using MtgSoloSports.Features.Superleague.ResolveAutomaticMovement;
using MtgSoloSports.Features.Superleague.RunQualifier;
using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Simulation.SimulateSeasons;

public static class SimulateSeasonsEndpoint
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapPost("/api/saves/{saveId:guid}/simulate-seasons", HandleAsync);
    }

    internal static async Task<IResult> HandleAsync(
        Guid saveId,
        SimulateSeasonsRequest? request,
        SimulateSeasonsHandler handler,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (request is null)
        {
            return Results.BadRequest(new { error = "Request body is required." });
        }

        try
        {
            SimulateSeasonsResponse response = await handler.HandleAsync(saveId, request, cancellationToken).ConfigureAwait(false);
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
        catch (InvalidOperationException ex)
        {
            return MapLifecycleException(ex);
        }
    }

    internal static IResult MapLifecycleException(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return ex switch
        {
            SimulateSeasonsConflictException conflict => Results.Conflict(new { error = conflict.Message }),
            CompleteStageForAllLeaguesConflictException conflict => Results.Conflict(new { error = conflict.Message }),
            CompleteStageConflictException conflict => Results.Conflict(new { error = conflict.Message }),
            AdvanceRoundConflictException conflict => Results.Conflict(new { error = conflict.Message }),
            CreateInauguralSuperleagueConflictException conflict => Results.Conflict(new { error = conflict.Message }),
            ResolveAutomaticMovementConflictException conflict => Results.Conflict(new { error = conflict.Message }),
            RunQualifierConflictException conflict => Results.Conflict(new { error = conflict.Message }),
            RebalanceFeedersConflictException conflict => Results.Conflict(new { error = conflict.Message }),
            StartNextSeasonConflictException conflict => Results.Conflict(new { error = conflict.Message }),
            _ => Results.BadRequest(new { error = ex.Message }),
        };
    }
}
