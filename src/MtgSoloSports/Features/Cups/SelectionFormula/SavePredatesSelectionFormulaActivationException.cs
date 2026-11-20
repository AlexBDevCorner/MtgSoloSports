namespace MtgSoloSports.Features.Cups.SelectionFormula;

/// <summary>
/// The save schema predates the Cup selection formula activation table: the
/// exact "no such table" condition on a not-yet-migrated pre-MSS-132 save.
/// This is a legacy save signal (migrate the save first), not corruption:
/// the status read model falls back to snapshot weights and other callers
/// surface the migrate-first message. Derives from
/// <see cref="InvalidOperationException"/> so un-audited InvalidOperationException
/// handling keeps its historical behavior.
/// </summary>
public sealed class SavePredatesSelectionFormulaActivationException(string message, Exception innerException)
    : InvalidOperationException(message, innerException)
{
}
