namespace MtgSoloSports.Features.Athletes.GetRecordHoldings;

/// <summary>
/// Thrown when the requested athlete does not exist in the save.
/// </summary>
public sealed class AthleteRecordHoldingsNotFoundException : Exception
{
    public AthleteRecordHoldingsNotFoundException(string message)
        : base(message)
    {
    }
}
