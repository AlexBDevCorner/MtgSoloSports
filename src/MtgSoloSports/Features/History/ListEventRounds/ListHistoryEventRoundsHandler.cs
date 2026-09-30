using MtgSoloSports.Features.History.EventRounds;
using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.History.ListEventRounds;

/// <summary>
/// Endpoint -&gt; Handler direct call. Lists the stored rounds of one postseason
/// event in (group, round) order. Read-only; no lock.
/// </summary>
public sealed class ListHistoryEventRoundsHandler
{
    private readonly SaveStore _store;

    public ListHistoryEventRoundsHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<ListHistoryEventRoundsResponse> HandleAsync(Guid saveId, int seasonNumber, string eventKey, CancellationToken cancellationToken = default)
    {
        HistoryEventRows.ValidateRequest(saveId, seasonNumber, eventKey);
        using SaveDbContext context = _store.OpenDbContext(saveId);
        SeasonEntity season = await HistoryEventRows.LoadSeasonAsync(context, seasonNumber, cancellationToken).ConfigureAwait(false);
        List<HistoryEventRows.StoredRound> rows = await HistoryEventRows.LoadAsync(context, season, eventKey, cancellationToken).ConfigureAwait(false);
        return new ListHistoryEventRoundsResponse(
            saveId, seasonNumber, eventKey, rows.Select(r => new HistoryEventRoundSummary(r.Group, r.Round)).ToList());
    }
}
