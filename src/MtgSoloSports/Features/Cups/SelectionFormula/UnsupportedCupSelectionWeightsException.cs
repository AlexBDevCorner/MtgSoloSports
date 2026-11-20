using MtgSoloSports.Shared;

namespace MtgSoloSports.Features.Cups.SelectionFormula;

/// <summary>
/// The persisted rules snapshot carries Cup selection weights that are neither
/// the legacy nor the approved formula: a server-side rules inconsistency
/// that is corruption-class (game-rule migration is explicit and never
/// implicit). The lifecycle detail read model keeps its snapshot-weight
/// fallback for display; every other path fails loudly as a 500-class server
/// failure instead of silently reporting legacy defaults.
/// </summary>
public sealed class UnsupportedCupSelectionWeightsException(string message)
    : SaveCorruptionException(message)
{
}
