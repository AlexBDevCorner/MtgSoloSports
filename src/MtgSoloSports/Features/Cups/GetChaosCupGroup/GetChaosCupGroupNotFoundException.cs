namespace MtgSoloSports.Features.Cups.GetChaosCupGroup;

/// <summary>Thrown when a Chaos Cup group or round has no persisted result yet (404).</summary>
public sealed class GetChaosCupGroupNotFoundException : Exception
{
    public GetChaosCupGroupNotFoundException(string message)
        : base(message)
    {
    }
}
