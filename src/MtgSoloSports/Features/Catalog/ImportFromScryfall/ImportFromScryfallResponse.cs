using MtgSoloSports.SimulationKernel.Catalog;

namespace MtgSoloSports.Features.Catalog.ImportFromScryfall;

/// <summary>
/// Successful one-click Scryfall import outcome: persisted catalog counts plus
/// the source dataset identity for truthful UI display. Never fabricates
/// freshness: source fields are null when Scryfall did not advertise them.
/// </summary>
/// <param name="TotalPrintings">Raw bulk entries downloaded.</param>
/// <param name="EligiblePrintings">Printings passing creature/token eligibility.</param>
/// <param name="UniqueAthletes">Collapsed unique card names persisted.</param>
/// <param name="CountsBySportingColor">Persisted unique athletes per sporting color.</param>
/// <param name="SkippedTokens">Raw entries rejected as tokens.</param>
/// <param name="SkippedNonCreature">Raw entries rejected as non-creature.</param>
/// <param name="IsSufficientForSave">True when every color reaches 256 athletes.</param>
/// <param name="SourceType">Bulk type used, normally <c>default_cards</c>.</param>
/// <param name="SourceName">Human-readable dataset name from Scryfall metadata.</param>
/// <param name="SourceUpdatedAt">Last-updated timestamp from Scryfall metadata, when advertised.</param>
/// <param name="SourceDownloadUri">Advertised bulk file URL used for this import.</param>
public sealed record ImportFromScryfallResponse(
    int TotalPrintings,
    int EligiblePrintings,
    int UniqueAthletes,
    IReadOnlyDictionary<SportingColor, int> CountsBySportingColor,
    int SkippedTokens,
    int SkippedNonCreature,
    bool IsSufficientForSave,
    string SourceType,
    string? SourceName,
    string? SourceUpdatedAt,
    string SourceDownloadUri);
