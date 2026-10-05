using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Features.Simulation.GlobalStage;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Features.Leagues.SeasonProgress;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Reads the current global
/// stage plus per-league completion so presentation can simulate leagues one
/// by one within the stage while respecting the synchronous gate.
/// </summary>
public sealed class GetSeasonProgressHandler
{
    private readonly SaveStore _store;

    public GetSeasonProgressHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<GetSeasonProgressResponse> HandleAsync(Guid saveId, int seasonNumber, CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        if (seasonNumber < 1)
        {
            throw new ArgumentException("Season number must be positive.", nameof(seasonNumber));
        }

        using SaveDbContext context = _store.OpenDbContext(saveId);
        SeasonEntity? season = await context.Seasons
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.SeasonNumber == seasonNumber, cancellationToken)
            .ConfigureAwait(false);
        if (season is null)
        {
            throw new SaveNotFoundException(saveId);
        }

        RulesV1 rules = await AdvanceRoundHandler.LoadRulesAsync(context, cancellationToken).ConfigureAwait(false);
        GlobalStageGate.GlobalStageView global = await GlobalStageGate
            .LoadGlobalStageAsync(context, season, rules, cancellationToken)
            .ConfigureAwait(false);
        return MapResponse(saveId, season, global);
    }

    internal static GetSeasonProgressResponse MapResponse(
        Guid saveId,
        SeasonEntity season,
        GlobalStageGate.GlobalStageView global)
    {
        List<SeasonProgressLeague> leagues = new(global.Leagues.Count);
        foreach (GlobalStageGate.LeagueStageStatus status in global.Leagues.OrderBy(l => l.LeagueId))
        {
            leagues.Add(new SeasonProgressLeague(
                status.LeagueId,
                status.LeagueName,
                status.LeagueKind,
                status.FeederDivision,
                status.LeagueLevel,
                status.CurrentStage,
                status.CompletedStages,
                status.IsLeagueComplete));
        }

        return new GetSeasonProgressResponse(
            saveId,
            season.SeasonNumber,
            global.CurrentStage,
            global.IsSeasonComplete,
            leagues);
    }
}
