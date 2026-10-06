namespace MtgSoloSports.Features.Superleague.GetFeederMovements;

public sealed class FeederMovementsNotFoundException : Exception
{
    public FeederMovementsNotFoundException(string message)
        : base(message)
    {
    }
}
