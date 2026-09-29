using MtgSoloSports.Features.Catalog.ImportCatalog;
using MtgSoloSports.Persistence.Catalog;
using MtgSoloSports.SimulationKernel.Catalog;

namespace MtgSoloSports.Features.Catalog.ImportFromScryfall;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Orchestrates the server-mediated
/// one-click Scryfall import: discovers the live bulk file through metadata,
/// streams the advertised download, reuses the existing eligibility,
/// duplicate-collapse, classification and persistence behavior, and verifies
/// quotas before replacing a healthy catalog.
/// Failed, corrupt, cancelled or quota-inadequate downloads never destroy a
/// healthy existing catalog because persistence happens only after verification.
/// </summary>
public sealed class ImportFromScryfallHandler
{
    private readonly IScryfallBulkGateway _gateway;
    private readonly CatalogStore _store;
    private readonly ScryfallImportLock _lock;

    public ImportFromScryfallHandler(
        IScryfallBulkGateway gateway,
        CatalogStore store,
        ScryfallImportLock importLock)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _lock = importLock ?? throw new ArgumentNullException(nameof(importLock));
    }

    public async Task<ImportFromScryfallResponse> HandleAsync(CancellationToken cancellationToken = default)
    {
        if (!_lock.TryAcquire())
        {
            throw new ScryfallImportInProgressException();
        }

        try
        {
            return await ImportUnderLockAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    internal static Dictionary<SportingColor, int> CountByColor(IReadOnlyList<CatalogAthlete> athletes)
    {
        ArgumentNullException.ThrowIfNull(athletes);
        Dictionary<SportingColor, int> counts = new();
        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            counts[color] = 0;
        }

        foreach (CatalogAthlete athlete in athletes)
        {
            counts[athlete.SportingColor]++;
        }

        return counts;
    }

    private async Task<ImportFromScryfallResponse> ImportUnderLockAsync(CancellationToken cancellationToken)
    {
        ScryfallImportSource source = await _gateway.GetDefaultCardsSourceAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<BulkCardRecord> records = await _gateway.DownloadCardsAsync(source, cancellationToken).ConfigureAwait(false);
        VerifiedDataset verified = VerifyDataset(records, source);
        await EnsureReplaceableAsync(verified.Counts, verified.Insufficient, source, cancellationToken).ConfigureAwait(false);
        CatalogImportResult result = await _store.ImportAsync(records, verified.Athletes, cancellationToken).ConfigureAwait(false);
        return ToResponse(result, source);
    }

    private static VerifiedDataset VerifyDataset(IReadOnlyList<BulkCardRecord> records, ScryfallImportSource source)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(source);
        if (records.Count == 0)
        {
            throw new ScryfallImportFailedException(
                "The Scryfall bulk file contained no cards. Try again later.");
        }

        IReadOnlyList<CatalogAthlete> athletes = BulkCatalogParser.BuildAthletes(records);
        if (athletes.Count == 0)
        {
            throw new ScryfallImportFailedException(
                "The Scryfall dataset produced no eligible creature athletes. The existing catalog was left unchanged.");
        }

        Dictionary<SportingColor, int> counts = CountByColor(athletes);
        IReadOnlyList<SportingColor> insufficient = CatalogQuotas.FindInsufficient(counts);
        return new VerifiedDataset(athletes, counts, insufficient);
    }

    private async Task EnsureReplaceableAsync(
        IReadOnlyDictionary<SportingColor, int> counts,
        IReadOnlyList<SportingColor> insufficient,
        ScryfallImportSource source,
        CancellationToken cancellationToken)
    {
        if (insufficient.Count == 0)
        {
            return;
        }

        IReadOnlyDictionary<SportingColor, int> existing = await _store.GetCountsAsync(cancellationToken).ConfigureAwait(false);
        if (CatalogQuotas.IsSufficient(existing))
        {
            throw new CatalogQuotaInsufficientException(
                $"The Scryfall dataset cannot supply a save universe: {DescribeShortfalls(counts, insufficient)}. The existing catalog was left unchanged.",
                counts,
                insufficient,
                source);
        }
    }

    private static string DescribeShortfalls(
        IReadOnlyDictionary<SportingColor, int> counts,
        IReadOnlyList<SportingColor> insufficient)
    {
        List<string> details = new(insufficient.Count);
        foreach (SportingColor color in insufficient)
        {
            int count = counts.TryGetValue(color, out int value) ? value : 0;
            details.Add($"{color} has {count}, needs {CatalogQuotas.RequiredPerColor}");
        }

        return string.Join("; ", details);
    }

    private static ImportFromScryfallResponse ToResponse(CatalogImportResult result, ScryfallImportSource source)
    {
        return new ImportFromScryfallResponse(
            result.TotalPrintings,
            result.EligiblePrintings,
            result.UniqueAthletes,
            result.CountsBySportingColor,
            result.SkippedTokens,
            result.SkippedNonCreature,
            result.SkippedAmbiguousColor,
            result.IsSufficientForSave,
            source.Type,
            source.Name,
            source.UpdatedAt,
            source.DownloadUri);
    }

    private sealed record VerifiedDataset(
        IReadOnlyList<CatalogAthlete> Athletes,
        Dictionary<SportingColor, int> Counts,
        IReadOnlyList<SportingColor> Insufficient);
}
