namespace MtgSoloSports.Features.Cups.GetChaosCupEdition;

/// <summary>Thrown when no Chaos Cup edition exists for the season (404).</summary>
public sealed class GetChaosCupEditionNotFoundException : Exception
{
    public GetChaosCupEditionNotFoundException(string message)
        : base(message)
    {
    }
}
