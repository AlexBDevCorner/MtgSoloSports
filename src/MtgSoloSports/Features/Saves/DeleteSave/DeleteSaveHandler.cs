using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Saves.DeleteSave;

public sealed class DeleteSaveHandler
{
    private readonly SaveStore _store;

    public DeleteSaveHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task HandleAsync(Guid saveId, CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        await _store.DeleteAsync(saveId, cancellationToken).ConfigureAwait(false);
    }
}
