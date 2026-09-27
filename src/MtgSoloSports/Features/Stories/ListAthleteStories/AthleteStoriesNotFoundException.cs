namespace MtgSoloSports.Features.Stories.ListAthleteStories;

public sealed class AthleteStoriesNotFoundException : Exception
{
    public AthleteStoriesNotFoundException(string message)
        : base(message)
    {
    }
}
