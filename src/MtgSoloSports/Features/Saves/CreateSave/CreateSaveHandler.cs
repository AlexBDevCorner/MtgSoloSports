using System.Globalization;
using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Saves.CreateSave;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Creates the save database file
/// with metadata, immutable Rules v1 snapshot and versioned RNG state in one transaction.
/// </summary>
public sealed class CreateSaveHandler
{
    private readonly SaveStore _store;

    public CreateSaveHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<CreateSaveResponse> HandleAsync(CreateSaveRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        SaveStore.SaveDetailRecord detail = await _store.CreateAsync(request.Name, request.Seed, request.Stream, cancellationToken).ConfigureAwait(false);
        _ = SavePhaseParser.Parse(detail.Phase);
        return new CreateSaveResponse(
            detail.SaveId,
            detail.Name,
            detail.CreatedUtc,
            detail.SchemaVersion,
            detail.CurrentSeason,
            detail.Phase,
            detail.RngAlgorithm,
            detail.RngVersion,
            detail.RngState.ToString(CultureInfo.InvariantCulture),
            detail.RngStream.ToString(CultureInfo.InvariantCulture),
            detail.RulesVersion);
    }
}
