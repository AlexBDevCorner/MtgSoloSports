namespace MtgSoloSports.Features.Cups.ChaosCup;

/// <summary>
/// The save schema predates the Chaos activation table: the exact
/// "no such table" condition on a not-yet-migrated save. This is a legacy
/// save signal (migrate the save first), not corruption: read models keep the
/// previous chain and movement gates convert it to their 409 conflict.
/// Derives from <see cref="InvalidOperationException"/> so un-audited
/// InvalidOperationException handling keeps its historical behavior.
/// </summary>
public sealed class SavePredatesChaosActivationException(string message, Exception innerException)
    : InvalidOperationException(message, innerException)
{
}
