using MtgSoloSports.Features.History.EventRounds;
using MtgSoloSports.Features.Seasons.SeasonLifecycle;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Features.History.ListEvents;

/// <summary>
/// Endpoint -&gt; Handler direct call. Lists the round-based postseason events
/// of a source season that have at least one stored round, with progress.
/// Read-only; no lock.
/// </summary>
public sealed class ListHistoryEventsHandler
{
    private readonly SaveStore _store;

    public ListHistoryEventsHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<ListHistoryEventsResponse> HandleAsync(Guid saveId, int seasonNumber, CancellationToken cancellationToken = default)
    {
        HistoryEventRows.ValidateRequest(saveId, seasonNumber, PostseasonEvents.Qualifier);
        using SaveDbContext context = _store.OpenDbContext(saveId);
        RulesV1 rules = await AdvanceRoundHandler.LoadRulesAsync(context, cancellationToken).ConfigureAwait(false);
        SeasonEntity season = await HistoryEventRows.LoadSeasonAsync(context, seasonNumber, cancellationToken).ConfigureAwait(false);
        List<HistoryEventSummary> events = [];
        foreach (string key in PostseasonEvents.All)
        {
            List<HistoryEventRows.StoredRound> rows = await HistoryEventRows.LoadAsync(context, season, key, cancellationToken).ConfigureAwait(false);
            if (rows.Count == 0)
            {
                continue;
            }

            // MSS-069: the legacy `qualifier` history slice is the Superleague
            // event only (16 rounds, 32 athletes). Feeder qualifiers are read
            // via the qualifier-list/rounds APIs with boundary identity, so the
            // phase-wide 272-round total must never appear under the
            // Superleague title.
            PostseasonEvents.EventShape shape = string.Equals(key, PostseasonEvents.Qualifier, StringComparison.Ordinal)
                ? new PostseasonEvents.EventShape(1, rules.QualifierRounds)
                : PostseasonEvents.Shape(key, rules);
            bool complete = await HistoryEventRows.IsCompleteAsync(context, season, key, cancellationToken).ConfigureAwait(false);
            events.Add(new HistoryEventSummary(
                key, PostseasonEvents.Title(key), rows.Count, shape.TotalRounds, shape.GroupCount, shape.RoundsPerGroup, complete));
        }

        return new ListHistoryEventsResponse(saveId, seasonNumber, events);
    }
}
