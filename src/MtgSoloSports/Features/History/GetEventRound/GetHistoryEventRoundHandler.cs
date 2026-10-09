using MtgSoloSports.Features.History.EventRounds;
using MtgSoloSports.Features.Seasons.SeasonLifecycle;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Features.History.GetEventRound;

/// <summary>
/// Endpoint -&gt; Handler direct call. Returns one stored postseason event round
/// in the league replay presentation shape. Grouped (team) events require a
/// group; single-stage events reject one. Read-only; never resimulates.
/// </summary>
public sealed class GetHistoryEventRoundHandler
{
    private readonly SaveStore _store;

    public GetHistoryEventRoundHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<EventRoundView> HandleAsync(
        Guid saveId,
        int seasonNumber,
        string eventKey,
        int round,
        int? group,
        CancellationToken cancellationToken = default)
    {
        HistoryEventRows.ValidateRequest(saveId, seasonNumber, eventKey);
        using SaveDbContext context = _store.OpenDbContext(saveId);
        RulesV1 rules = await AdvanceRoundHandler.LoadRulesAsync(context, cancellationToken).ConfigureAwait(false);
        PostseasonEvents.EventShape shape = string.Equals(eventKey, PostseasonEvents.Qualifier, StringComparison.Ordinal)
            ? new PostseasonEvents.EventShape(1, rules.QualifierRounds)
            : PostseasonEvents.Shape(eventKey, rules);
        if (shape.IsGrouped && (group is null || group < 1 || group > shape.GroupCount))
        {
            throw new ArgumentException($"{PostseasonEvents.Title(eventKey)} rounds need a group between 1 and {shape.GroupCount}.", nameof(group));
        }

        if (!shape.IsGrouped && group is not null)
        {
            throw new ArgumentException($"{PostseasonEvents.Title(eventKey)} has no groups.", nameof(group));
        }

        if (round < 1 || round > shape.RoundsPerGroup)
        {
            throw new ArgumentException($"Round must be between 1 and {shape.RoundsPerGroup}.", nameof(round));
        }

        SeasonEntity season = await HistoryEventRows.LoadSeasonAsync(context, seasonNumber, cancellationToken).ConfigureAwait(false);
        List<HistoryEventRows.StoredRound> rows = await HistoryEventRows.LoadAsync(context, season, eventKey, cancellationToken).ConfigureAwait(false);
        HistoryEventRows.StoredRound row = rows.SingleOrDefault(r => r.Group == group && r.Round == round)
            ?? throw new HistoryNotFoundException($"{PostseasonEvents.Title(eventKey)} round {round} has not been played.");
        return await EventRoundViews.BuildAsync(
            context,
            seasonNumber,
            eventKey,
            row.Group,
            row.Round,
            row.RulesVersion,
            row.Checksum,
            row.Before,
            row.After,
            HistoryEventRows.DecodePlacements(eventKey, row.PayloadJson),
            cancellationToken).ConfigureAwait(false);
    }
}
