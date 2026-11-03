namespace MtgSoloSports.Features.Cups.GetChaosCupSurvival;

/// <summary>Thrown when a Chaos Cup survival report has no persisted edition yet (404).</summary>
public sealed class GetChaosCupSurvivalNotFoundException : Exception
{
    public GetChaosCupSurvivalNotFoundException(string message)
        : base(message)
    {
    }
}
