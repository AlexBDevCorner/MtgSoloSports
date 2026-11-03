namespace MtgSoloSports.Features.Cups.GetChaosCupAthleteJourney;

/// <summary>Thrown when a Chaos Cup athlete journey has no persisted result yet (404).</summary>
public sealed class GetChaosCupAthleteJourneyNotFoundException : Exception
{
    public GetChaosCupAthleteJourneyNotFoundException(string message)
        : base(message)
    {
    }
}
