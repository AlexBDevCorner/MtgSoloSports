namespace MtgSoloSports.Features.Cups.ChaosCup;

/// <summary>
/// The genuine "Chaos Cup not yet complete" signal: an eligible source season
/// still owes its Chaos tournament before promotion movement may resolve.
/// Movement gates convert this to their feature 409 conflict; corruption
/// signals pass through uncaught instead of being downgraded to a retryable
/// conflict.
/// </summary>
public sealed class ChaosCupIncompleteException(string message)
    : InvalidOperationException(message)
{
}
