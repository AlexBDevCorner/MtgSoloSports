using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Saves.ListSaves;

public sealed class ListSavesHandler
{
    private readonly SaveStore _store;

    public ListSavesHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<ListSavesResponse> HandleAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<SaveStore.SaveRecord> records = await _store.ListAsync(cancellationToken).ConfigureAwait(false);
        List<SaveSummary> summaries = new(records.Count);
        foreach (SaveStore.SaveRecord record in records)
        {
            // A damaged placeholder carries no valid phase; it is listed as-is
            // instead of aborting the whole listing.
            if (!record.Unreadable)
            {
                _ = SavePhaseParser.Parse(record.Phase);
            }

            summaries.Add(new SaveSummary(
                record.SaveId,
                record.Name,
                record.CreatedUtc,
                record.SchemaVersion,
                record.CurrentSeason,
                record.Phase,
                record.Unreadable,
                record.UnreadableReason));
        }

        return new ListSavesResponse(summaries);
    }
}
